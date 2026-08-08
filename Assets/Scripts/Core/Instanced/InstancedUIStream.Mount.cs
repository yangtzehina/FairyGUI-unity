using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
#if UNITY_2020_1_OR_NEWER
using Unity.Profiling;
#endif


namespace FairyGUI
{
    /// <summary>
    /// M8 bake line: splicing a mounted FQS blob into the stream in place of
    /// the subtree walk, plus the mount visibility and interior-transform
    /// tiers that service page flips without a recompile.
    /// </summary>
    public partial class InstancedUIStream
    {

        /// <summary>
        /// M8-2: allocates a transform slot for a mounted container regardless
        /// of hot state — baked quads live in mount-local space, so the slot IS
        /// the placement (and mount movement becomes tier-1 for free).
        /// </summary>
        int AllocMountSlot(Container c)
        {
            if (_slotIndices.TryGetValue(c, out int idx))
                return idx;
            for (int i = 1; i < MaxTransformSlots; i++)
            {
                if (_slotOwners[i] == null)
                {
                    _slotIndices[c] = i;
                    _slotOwners[i] = c;
                    return i;
                }
            }
            slotOverflow++;
            return -1;
        }

        /// <summary>
        /// M8-2: splices a mounted blob into the walk — quads copy with clip
        /// remap/slot stamp/grayed OR, blob clip owners re-enter the ordinary
        /// PushClip fold (live rects, slot-riding), and the whole mount becomes
        /// ONE adjacency entry (unique key: internal order is frozen).
        /// Returns false to fall back to the runtime walk.
        /// </summary>
        bool SpliceMount(Container c, FqsMount fm, uint parentClip, bool grayed)
        {
            int slot = AllocMountSlot(c);
            if (slot < 0)
                return false; //no slot left: runtime walk still renders correctly

            uint rootClip = parentClip;
            if (c.clipRect != null)
                rootClip = PushClip(c, parentClip, (uint)slot);

            var clipMap = new uint[fm.data.clips.Length];
            clipMap[0] = rootClip;
            for (int i = 1; i < fm.data.clips.Length; i++)
            {
                Container owner = fm.clipOwners[i];
                if (owner == null || owner.isDisposed || owner.clipRect == null)
                    return false;
                clipMap[i] = PushClip(owner, clipMap[fm.data.clips[i].parentIndex], (uint)slot);
            }

            int stageStart = _staging.Count;
            var quads = fm.data.quads;
            for (int i = 0; i < quads.Length; i++)
            {
                QuadInstance q = quads[i];
                q.transformIndex = (uint)slot;
                q.clipIndex = clipMap[q.clipIndex];
                if (grayed)
                    q.flags |= QuadInstance.FlagGrayed;
                _staging.Add(q);
            }

            //root-space AABB of the mount for the adjacency sort
            Matrix4x4 m = _rootWorldToLocal * c.cachedTransform.localToWorldMatrix;
            Vector4 lb = fm.localBounds;
            Vector2 c0 = m.MultiplyPoint3x4(new Vector3(lb.x, lb.y, 0));
            Vector2 c1 = m.MultiplyPoint3x4(new Vector3(lb.z, lb.y, 0));
            Vector2 c2 = m.MultiplyPoint3x4(new Vector3(lb.x, lb.w, 0));
            Vector2 c3 = m.MultiplyPoint3x4(new Vector3(lb.z, lb.w, 0));
            Vector2 bmin = Vector2.Min(Vector2.Min(c0, c1), Vector2.Min(c2, c3));
            Vector2 bmax = Vector2.Max(Vector2.Max(c0, c1), Vector2.Max(c2, c3));

            _entries.Add(new AdjacencyEntry
            {
                key = fm, //unique: never merges with other entries
                x0 = bmin.x, y0 = bmin.y, x1 = bmax.x, y1 = bmax.y,
                payload = _pending.Count
            });
            _pending.Add(new PendingLeaf
            {
                mount = fm,
                mountClipMap = clipMap,
                stageStart = stageStart,
                stageCount = quads.Length,
                instanceable = true,
                slotIndex = (uint)slot,
                clipIndex = rootClip,
                flags = grayed ? QuadInstance.FlagGrayed : 0u,
                bakeAlpha = 1f,
            });
            return true;
        }

        /// <summary>
        /// M8-2: turns a mount's staged quads (already clip-remapped, slot-
        /// stamped, grayed) into stream segments and live LeafRanges. Baked
        /// leaves get full push-channel service: tier-2 rewrites re-read the
        /// live meshes, the color tier rescales from bakedAlpha, and rewrites
        /// recorded before a re-splice are re-queued so stale blob quads never
        /// linger.
        /// </summary>
        void SpliceMountSegments(PendingLeaf leaf, int runIndex)
        {
            FqsMount fm = leaf.mount;
            int quadBase = _quads.Count;
            for (int q = 0; q < leaf.stageCount; q++)
                _quads.Add(_staging[leaf.stageStart + q]);

            var segs = fm.data.segs;
            int segBase = _segments.Count;
            for (int i = 0; i < segs.Length; i++)
            {
                Segment sg = TakeSegment();
                sg.z = -0.5f * _segments.Count;
                sg.start = quadBase + segs[i].start;
                sg.count = segs[i].count;
                sg.runIndex = runIndex;
                sg.texCount = 0;
                AddSegTex(sg, fm, segs[i].tex0);
                AddSegTex(sg, fm, segs[i].tex1);
                AddSegTex(sg, fm, segs[i].tex2);
                AddSegTex(sg, fm, segs[i].tex3);
                _segments.Add(sg);
            }

            var leaves = fm.data.leaves;
            for (int i = 0; i < leaves.Length; i++)
            {
                FqsLeafRecord lr = leaves[i];
                NGraphics g = fm.leafGraphics[i];
                int segIndex = segBase;
                for (int si = 0; si < segs.Length; si++)
                {
                    if (lr.start >= segs[si].start && lr.start < segs[si].start + segs[si].count)
                    {
                        segIndex = segBase + si;
                        break;
                    }
                }
                var range = new LeafRange
                {
                    graphics = g,
                    start = quadBase + lr.start,
                    count = lr.count,
                    liveCount = lr.liveCount,
                    bakedAlpha = lr.bakedAlpha,
                    slotIndex = leaf.slotIndex,
                    texIndexBits = _quads[quadBase + lr.start].flags & (3u << QuadInstance.TexIndexShift),
                    segIndex = segIndex,
                    flags = (lr.flags & ~(FqsLeafRecord.KindSdf | FqsLeafRecord.KindCurve)) | leaf.flags,
                    clipIndex = leaf.mountClipMap[lr.clipIndex],
                    sdf = (lr.flags & FqsLeafRecord.KindSdf) != 0,
                    curve = false,
                    mount = fm,
                };
                _leaves.Add(range);
                _leafLookup[g] = range;

                //self-heal staleness: content rewritten since the bake, or a
                //color/alpha state differing from the baked value, refreshes
                //through the ordinary push channels right after this extract
                if (fm.rewritten.Contains(g))
                    _QueueLeafUpdate(g);
                else if (g._colorStale || g._currentAlpha != lr.bakedAlpha)
                    _QueueLeafColor(g);
            }

            //M8-4: visibility is re-derived from the LIVE flags every splice
            //(stateless — hidden branches zero their ranges again)
            {
                int cnt = fm.root.numChildren;
                for (int i = 0; i < cnt; i++)
                {
                    DisplayObject child = fm.root.GetChildAt(i);
                    if (!child.visible)
                        HideSubtree(child, true);
                    else
                        HideInvisibleBelow(child);
                }
            }
            fm.spliced = true;
        }

        /// <summary>
        /// M8-4 visibility tier: hide zeroes the subtree's mounted quad ranges
        /// in place; show clears the flag and requeues tier-2 rebuilds (settled
        /// in the same frame's Flush). Showing content ABSENT from the blob
        /// (invisible at bake) invalidates the mount — the runtime walk renders
        /// it, correctness over speed.
        /// </summary>
        internal bool _OnMountVisibility(DisplayObject obj)
        {
            if (_leaves.Count == 0)
                return false;
            if (obj.visible)
            {
                if (!ShowSubtree(obj))
                    return false;
            }
            else
                HideSubtree(obj, false);
            return true;
        }

        /// <summary>
        /// M8-4: services a container transform inside a valid mount as exact
        /// per-leaf rewrites (slot-relative matrices re-read the live
        /// transforms). Returns false when an unclaimed visible leaf exists
        /// under it (content the stream does not carry).
        /// </summary>
        internal bool _OnMountInteriorTransform(Container c)
        {
            if (_leaves.Count == 0)
                return false;
            if (!QueueSubtreeUpdates(c))
                return false;
            //the moved container (or a descendant) may OWN slot-riding clip
            //entries — their root-space rects re-derive from live transforms
            //through the slot-dirty recompute path
            _slotsDirty = true;
            return true;
        }

        bool QueueSubtreeUpdates(DisplayObject obj)
        {
            NGraphics g = obj.graphics;
            if (g != null && g.texture != null)
            {
                if (_leafLookup.TryGetValue(g, out LeafRange r))
                {
                    if (!r.hidden)
                        _QueueLeafUpdate(g);
                }
                else if (obj.visible)
                    return false; //visible content the stream does not carry
            }
            if (obj is Container c)
            {
                int cnt = c.numChildren;
                for (int i = 0; i < cnt; i++)
                {
                    DisplayObject child = c.GetChildAt(i);
                    if (!child.visible)
                        continue;
                    if (!QueueSubtreeUpdates(child))
                        return false;
                }
            }
            return true;
        }

        void HideSubtree(DisplayObject obj, bool inExtract)
        {
            if (obj.graphics != null)
                HideLeaf(obj.graphics, inExtract);
            if (obj is Container c)
            {
                int cnt = c.numChildren;
                for (int i = 0; i < cnt; i++)
                    HideSubtree(c.GetChildAt(i), inExtract);
            }
        }

        void HideLeaf(NGraphics g, bool inExtract)
        {
            if (!_leafLookup.TryGetValue(g, out LeafRange range) || range.hidden)
                return;
            range.hidden = true;
            for (int i = 0; i < range.liveCount; i++)
            {
                _quads[range.start + i] = default;
                if (!inExtract)
                {
                    _uploadArray[range.start + i] = default;
                    if (_vertexPath)
                        QuadVertex.WriteQuad(_vertexUpload, range.start + i, default);
                }
            }
            if (!inExtract && range.liveCount > 0)
            {
                Segment owner = _segments[range.segIndex];
                if (range.start < owner.dirtyMin) owner.dirtyMin = range.start;
                int last = range.start + range.liveCount - 1;
                if (last > owner.dirtyMax) owner.dirtyMax = last;
                UploadDirtyRange(owner);
            }
        }

        bool ShowSubtree(DisplayObject obj)
        {
            NGraphics g = obj.graphics;
            //NOTE: no mesh guards here — a leaf hidden since birth has an
            //UNBUILT mesh, and that is exactly the "absent from the blob"
            //case that must invalidate (the runtime walk claims it properly)
            if (g != null && g.texture != null)
            {
                if (!_leafLookup.TryGetValue(g, out LeafRange range))
                    return false; //absent from the blob: only the runtime walk can render it
                if (range.hidden)
                {
                    range.hidden = false;
                    _QueueLeafUpdate(g);
                }
            }
            if (obj is Container c)
            {
                int cnt = c.numChildren;
                for (int i = 0; i < cnt; i++)
                {
                    DisplayObject child = c.GetChildAt(i);
                    if (!child.visible)
                        continue;
                    if (!ShowSubtree(child))
                        return false;
                }
            }
            return true;
        }

        void AddSegTex(Segment sg, FqsMount fm, int texRef)
        {
            if (texRef < 0)
                return;
            sg.textures[sg.texCount++] = fm.textures[texRef];
        }
    }
}
