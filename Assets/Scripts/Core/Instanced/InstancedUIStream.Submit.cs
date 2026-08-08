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
    /// SUBMIT layer (design §3): per-frame consumption of the push channels
    /// (leaf rewrites, color tier, scroll) and Render — run-order assignment
    /// against native fallback content and segment renderer sync.
    /// </summary>
    public partial class InstancedUIStream
    {

        /// <summary>
        /// Scroll tier: one vector write, applied in the vertex shader.
        /// </summary>
        public void SetScrollOffset(Vector2 offset)
        {
#if UNITY_2020_1_OR_NEWER
            using (sScroll.Auto())
#endif
            {
                _scrollOffset = offset;
            }
        }

        /// <summary>
        /// Content tier: re-reassemble one leaf and upload only its instance range.
        /// Returns false when the quad count changed (caller should Extract).
        /// </summary>
        public bool UpdateLeaf(NGraphics graphics)
        {
            return UpdateLeaf(graphics, false);
        }

        bool UpdateLeaf(NGraphics graphics, bool deferUpload)
        {
#if UNITY_2020_1_OR_NEWER
            using (sLeafUpdate.Auto())
#endif
            {
                if (!_leafLookup.TryGetValue(graphics, out LeafRange range))
                    return false;
                if (_vertexPath ? _vertexUpload == null : _buffer == null)
                    return false;
                if (range.hidden)
                    return true; //stays zeroed; the show path requeues a rebuild

                if (graphics._renderless)
                    graphics._EnsureMeshBuilt(); //deferred leaf: build on demand
                Mesh mesh = graphics.mesh;
                if (mesh == null)
                    return false;

                Matrix4x4 baseW2L;
                if (range.slotIndex == 0)
                    baseW2L = _container.cachedTransform.worldToLocalMatrix;
                else
                {
                    Container slotOwner = _slotOwners[range.slotIndex];
                    if (slotOwner == null || slotOwner.isDisposed)
                        return false; //slot died: recompile re-bakes in root space
                    baseW2L = slotOwner.cachedTransform.worldToLocalMatrix;
                }
                Matrix4x4 m = baseW2L * graphics.gameObject.transform.localToWorldMatrix;

                sLeafScratch.Clear();
                int rebuilt;
                if (range.sdf)
                {
                    //analytic leaf: re-emit from the factory parameters (a factory
                    //swap or a border toggling on/off changes the count -> recompile)
                    rebuilt = EmitSdfQuads(graphics, m, sLeafScratch, range.flags & QuadInstance.FlagGrayed);
                    if (rebuilt != range.count)
                        return false;
                }
                else if (range.curve)
                {
                    //curve leaves carry glyph-count slack (batch 5), so any
                    //length within the reserved range stays tier-2
                    rebuilt = EmitCurveQuads(graphics, m, sLeafScratch, range.flags & QuadInstance.FlagGrayed);
                    if (rebuilt == 0 || rebuilt > range.count)
                        return false;
                }
                else
                {
                    //color tier (batch 3): deferred alpha/tint must land in the
                    //mesh before it is re-read
                    graphics._RestoreNativeColors();
                    mesh.GetVertices(sVerts);
                    mesh.GetUVs(0, sUVs);
                    mesh.GetColors(sColors);
                    mesh.GetTriangles(sTris, 0);

                    int skipped;
                    rebuilt = QuadReassembler.Append(sLeafScratch, sVerts, sUVs, sColors, sTris, m, _drawOffset, range.flags, out skipped);
                    if (skipped > 0)
                        return false; //topology went non-quad: recompile
                    //text leaves carry slack (batch 2): any length within the
                    //reserved range stays tier-2; the tail is padded below
                    bool slackLeaf = (range.flags & QuadInstance.FlagAlphaTexture) != 0;
                    if (slackLeaf ? rebuilt > range.count : rebuilt != range.count)
                        return false;
                }

                //write the rebuilt quads; clear only the tail that was live in a
                //previously longer text (same-length churn touches exactly the
                //glyphs it has)
                int touch = rebuilt > range.liveCount ? rebuilt : range.liveCount;
                for (int i = 0; i < touch; i++)
                {
                    QuadInstance q = i < rebuilt ? sLeafScratch[i] : default;
                    q.clipIndex = range.clipIndex;
                    q.transformIndex = range.slotIndex;
                    q.flags |= range.texIndexBits;
                    _quads[range.start + i] = q;
                    _uploadArray[range.start + i] = q;
                    if (_vertexPath)
                        QuadVertex.WriteQuad(_vertexUpload, range.start + i, in q);
                }
                range.liveCount = rebuilt;
                range.bakedAlpha = graphics._currentAlpha;

                //coalesce (batch 2): mark the owning segment's dirty range; the
                //Flush loop uploads each segment once, direct callers keep the
                //old upload-immediately semantics
                Segment owner = _segments[range.segIndex];
                if (range.start < owner.dirtyMin) owner.dirtyMin = range.start;
                int last = range.start + touch - 1;
                if (last > owner.dirtyMax) owner.dirtyMax = last;
                if (!deferUpload)
                    UploadDirtyRange(owner);
                return true;
            }
        }

        /// <summary>
        /// Color tier (batch 3): alpha/tint change on a claimed leaf rewrites only
        /// the color field of its live quads — no native mesh read, no reassembly.
        /// The alpha basis is rescaled from the baked value (QuadReassembler takes
        /// one color per quad, so this is exact); a leaf baked at alpha 0 has no
        /// recoverable basis and takes the full path once, as does a deferred tint
        /// on an analytic (SDF/curve) leaf whose quads mix fill/border colors.
        /// </summary>
        void UpdateLeafColor(NGraphics graphics)
        {
            if (!_leafLookup.TryGetValue(graphics, out LeafRange range))
                return;
            if (_vertexPath ? _vertexUpload == null : _buffer == null)
                return;

            if (range.hidden)
                return; //zeroed by the visibility tier
            float alpha = graphics._currentAlpha;
            bool tint = graphics._tintStale;
            if (range.bakedAlpha <= 0f || (tint && (range.sdf || range.curve)))
            {
                if (!UpdateLeaf(graphics, true))
                    _structureDirty = true;
                return;
            }

            float scale = alpha / range.bakedAlpha;
            Color tintRgb = graphics._tintColor;
            bool tintRewrite = tint && !range.sdf && !range.curve;
            for (int i = 0; i < range.liveCount; i++)
            {
                QuadInstance q = _quads[range.start + i];
                Color c = q.color;
                if (tintRewrite)
                {
                    c.r = tintRgb.r;
                    c.g = tintRgb.g;
                    c.b = tintRgb.b;
                }
                c.a *= scale;
                q.color = c;
                _quads[range.start + i] = q;
                _uploadArray[range.start + i] = q;
                if (_vertexPath)
                    QuadVertex.WriteQuad(_vertexUpload, range.start + i, in q);
            }
            range.bakedAlpha = alpha;

            if (range.liveCount > 0)
            {
                Segment owner = _segments[range.segIndex];
                if (range.start < owner.dirtyMin) owner.dirtyMin = range.start;
                int last = range.start + range.liveCount - 1;
                if (last > owner.dirtyMax) owner.dirtyMax = last;
            }
        }

        /// <summary>
        /// Submission tier: per-frame sync of the segment renderers. The segments
        /// are real MeshRenderers (children of the container), so the camera draws
        /// them like any other UI renderer; this call keeps their sortingOrder,
        /// layer and shared uniforms in step. Call once per frame AFTER the stage
        /// update (native renderingOrder must be assigned first).
        /// </summary>
        public void Render()
        {
#if UNITY_2020_1_OR_NEWER
            using (sRender.Auto())
#endif
            {
                if (_inPlace)
                {
                    if (_container.isDisposed || !_container.visible || !_container.gameObject.activeInHierarchy)
                    {
                        SetSegmentsVisible(false);
                        return;
                    }
                    Flush();
                }

                if ((_vertexPath ? _vertexUpload == null : _buffer == null) || _segments.Count == 0)
                    return;

                //recomputed every frame: in in-place mode the container itself moves
                //when its ScrollPane scrolls, while the mask window stays put
                ComputeExternalWindow();

                //run orders shift only when the native renderingOrder assignment
                //moved (tree changes outside the stream shift our block uniformly,
                //changes inside recompile via Extract) — probe first leaf plus
                //EVERY barrier order instead of walking every leaf every frame
                //(batch 2; endpoint-only sampling missed compensating same-frame
                //shifts between middle barriers, e.g. two GoWrappers trading a
                //renderer count — barriers are few, the loop is a handful of
                //int reads)
                int runProbe = _leaves.Count > 0 ? _leaves[0].graphics.renderingOrder : 0;
                for (int bi = 0; bi < _runBarriers.Count; bi++)
                    runProbe = (runProbe * 397) ^ _runBarriers[bi].order;
                if (runProbe != _lastRunOrderProbe)
                {
                    ComputeRunOrders();
                    _lastRunOrderProbe = runProbe;
                }

                //transform slots (batch 3): a slot container moved — re-derive the
                //slot matrices (and any slot-riding internal clips) from the live
                //transforms; the refreshed values ride the property push below
                if (_slotsDirty)
                {
                    _slotsDirty = false;
                    RecomputeSlotMatrices();
                    if (_hasSlottedClips)
                    {
                        RecomputeSlottedClips();
                        if (!_vertexPath && _clipBuffer != null)
                        {
                            _clipEntries.CopyTo(_clipUploadArray);
                            _clipBuffer.SetData(_clipUploadArray, 0, 0, _clipEntries.Count);
                        }
                    }
                    _propsDirty = true;
                }

                //stream-level uniforms: push property blocks only when something
                //they carry actually changed (batch 2)
                int curveVer = -1;
                if (CurveFontStore.loaded)
                {
                    //data textures (batch 5): both backends read the tables as
                    //globals — refresh them when new glyphs were baked
                    CurveFontStore.EnsureBuffers();
                    curveVer = CurveFontStore.version;
                }
                bool pushProps = _propsDirty
                    || _scrollOffset != _lastScroll
                    || _clipRect != _lastClipRect
                    || _clipSoft != _lastClipSoft
                    || curveVer != _lastCurveFontVersion;

                if (pushProps && _vertexPath)
                {
                    //internal clips as uniform arrays (always full-size: Unity pins
                    //array length at first use per shader)
                    for (int i = 0; i < MaxVertexPathClips; i++)
                    {
                        if (i < _clipEntries.Count)
                        {
                            _clipRectArr[i] = _clipEntries[i].rect;
                            _clipSoftArr[i] = _clipEntries[i].soft;
                        }
                        else
                        {
                            _clipRectArr[i] = ClipEntry.None.rect;
                            _clipSoftArr[i] = Vector4.zero;
                        }
                    }
                }

                //layer protocol: follow a claimed leaf's gameObject — painting
                //captures flip leaves via SetChildrenLayer, and the segments must
                //flip with them so CaptureCamera sees them and the main camera
                //does not (review M12)
                int layer = _container.gameObject.layer;
                if (_leaves.Count > 0 && _leaves[0].graphics.gameObject != null)
                    layer = _leaves[0].graphics.gameObject.layer;

                for (int i = 0; i < _segments.Count; i++)
                {
                    Segment seg = _segments[i];
                    if (seg.renderer == null || seg.count == 0)
                        continue;

                    if (!seg.go.activeSelf)
                        seg.go.SetActive(true);

                    //interleaving: all segments of a run share the sortingOrder slot
                    //just below their closing barrier; within a run the z step keeps
                    //segment order (Unity breaks sortingOrder ties by depth)
                    int order = _runOrderScratch[seg.runIndex];
                    if (order != seg.lastSortingOrder)
                    {
                        seg.renderer.sortingOrder = order;
                        seg.lastSortingOrder = order;
                    }

                    if (layer != seg.lastLayer)
                    {
                        seg.go.layer = layer;
                        seg.lastLayer = layer;
                    }

                    if (pushProps)
                    {
                        seg.props.SetVector("_ScrollOffset", _scrollOffset);
                        seg.props.SetVector("_ClipRect", _clipRect);
                        seg.props.SetVector("_ClipSoft", _clipSoft);
                        seg.props.SetMatrixArray("_TransformSlots", _slotMatrixArr);
                        if (_vertexPath)
                        {
                            seg.props.SetVectorArray("_ClipRects", _clipRectArr);
                            seg.props.SetVectorArray("_ClipSofts", _clipSoftArr);
                        }
                        seg.renderer.SetPropertyBlock(seg.props);
                    }
                }

                if (pushProps)
                {
                    _propsDirty = false;
                    _lastScroll = _scrollOffset;
                    _lastClipRect = _clipRect;
                    _lastClipSoft = _clipSoft;
                    _lastCurveFontVersion = curveVer;
                }
            }
        }

        /// <summary>
        /// Layer-flip protocol: SetChildrenLayer carries the segment renderers along
        /// with the DisplayObject children (CaptureCamera's synchronous
        /// flip-render-flip must include them for filter/painting captures).
        /// </summary>
        internal void _SetSegmentLayers(int layer)
        {
            for (int i = 0; i < _segments.Count; i++)
            {
                Segment seg = _segments[i];
                if (seg.go != null)
                {
                    seg.go.layer = layer;
                    seg.lastLayer = layer;
                }
            }
        }

        void SetSegmentsVisible(bool visible)
        {
            for (int i = 0; i < _segments.Count; i++)
            {
                Segment seg = _segments[i];
                if (seg.go != null && seg.go.activeSelf != visible)
                    seg.go.SetActive(visible);
            }
        }

        /// <summary>
        /// Per-run sortingOrder: the highest claimed-leaf renderingOrder in the run
        /// that is still below the closing barrier's order. Claimed leaves consume
        /// order slots in the native assignment pass anyway, so the slot is free;
        /// overlapping content cannot have been sorted across a barrier, which makes
        /// this order correct wherever it is visually observable.
        /// </summary>
        void ComputeRunOrders()
        {
            int runCount = _runBarriers.Count + 1;
            _runOrderScratch.Clear();
            for (int r = 0; r < runCount; r++)
                _runOrderScratch.Add(int.MinValue);

            for (int i = 0; i < _leaves.Count; i++)
            {
                LeafRange leaf = _leaves[i];
                int r = _segments[leaf.segIndex].runIndex;
                int barrierOrder = r < _runBarriers.Count ? _runBarriers[r].order : int.MaxValue;
                int o = leaf.graphics.renderingOrder;
                if (o < barrierOrder && o > _runOrderScratch[r])
                    _runOrderScratch[r] = o;
            }

            for (int r = 0; r < runCount; r++)
            {
                if (_runOrderScratch[r] == int.MinValue)
                {
                    //run has no usable slot (nothing in it overlaps its barrier):
                    //sit just above the previous barrier's LAST native slot
                    _runOrderScratch[r] = r > 0 ? _runBarriers[r - 1].order + 1 : 0;
                }
            }
        }
    }
}
