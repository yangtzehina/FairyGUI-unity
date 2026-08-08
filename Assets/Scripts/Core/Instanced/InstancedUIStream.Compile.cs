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
    /// COMPILE layer (design §3): the tree walk that turns a container subtree
    /// into the staged quad list — extraction, scope barriers, transform-slot
    /// promotion, clip folding, the SDF/curve emitters, and run segmentation.
    /// </summary>
    public partial class InstancedUIStream
    {

        /// <summary>
        /// Full recompile: walk visible leaves, adjacency-sort them (segment count
        /// shrinks where draw order may legally change), reassemble quads, slice
        /// segments, upload the instance buffer.
        /// </summary>
        public void Extract()
        {
#if UNITY_2020_1_OR_NEWER
            using (sExtract.Auto())
#endif
            {
                extractCount++;
                //batch 3: same-shape recompiles transfer renderers in place
                //instead of the SetParent/SetActive round trip through the pool
                _prevSegments.Clear();
                _prevSegments.AddRange(_segments);
                _segments.Clear();
                _leaves.Clear();
                _leafLookup.Clear();
                _quads.Clear();
                _staging.Clear();
                _pending.Clear();
                _entries.Clear();
                _runBarriers.Clear();
                _clipEntries.Clear();
                _clipEntries.Add(ClipEntry.None);
                _skippedPairs = 0;
                _maskedSubtrees = 0;

                foreach (var t in _watchedTextures)
                    t.onSizeChanged -= _onWatchedTexture;
                _watchedTextures.Clear();

                _rootWorldToLocal = _container.cachedTransform.worldToLocalMatrix;
                _clipMeta.Clear();
                _clipMeta.Add(default);
                _slotIndices.Clear();
                for (int i = 0; i < MaxTransformSlots; i++)
                    _slotOwners[i] = null;
                slotOverflow = 0;
                _hasSlottedClips = false;
                if (_hotContainers.Count > 0)
                {
                    //hot containers that stopped moving (or died) age out
                    sHotScratch.Clear();
                    foreach (var kv in _hotContainers)
                        if (kv.Key.isDisposed || Time.frameCount - kv.Value > 3000)
                            sHotScratch.Add(kv.Key);
                    foreach (var c in sHotScratch)
                        _hotContainers.Remove(c);
                }

                //a stream root with a stencil mask cannot be expressed: claimed
                //leaves leave the native pass, so the mask would neither write
                //stencil against them nor clip the segments — claim nothing and
                //let the whole subtree render natively (wrong batching beats
                //wrong pixels; the claim diff below releases earlier claims)
                if (_inPlace && _container.mask != null)
                {
                    if (!_rootMaskWarned)
                    {
                        _rootMaskWarned = true;
                        Debug.LogWarning("InstancedUIStream: the stream root has a stencil mask; instanced rendering is suspended until the mask is removed.", _container.gameObject);
                    }
                }
                else
                {
                    //re-arm the warning: each suspension period warns once
                    //(Extract only runs on structure dirt, so no spam)
                    _rootMaskWarned = false;
                    //grayed inherits from ABOVE the stream root too (native
                    //content gets it via context accumulation) — recompiles
                    //re-read the chain, _NotifyDescendantStreams triggers them
                    bool rootGrayed = _inPlace ? _ChainGrayed(_container) : _container.grayed;
                    _lastRootGrayed = rootGrayed;
                    ExtractContainer(_container, _rootWorldToLocal, 0, rootGrayed, 0);
                }

                if (_sortAdjacency)
                    AdjacencySorter.Sort(_entries);
                BuildSegments();

                if (_inPlace)
                {
                    //claim diff: leaves that left the stream resume native
                    //rendering; new leaves stop rendering natively
                    _claimScratch.Clear();
                    for (int i = 0; i < _leaves.Count; i++)
                        _claimScratch.Add(_leaves[i].graphics);
                    foreach (var g in _claimed)
                    {
                        //ownership check: another stream may have claimed this
                        //leaf since it left us (cross-root move, review M9)
                        if (!_claimScratch.Contains(g) && g._instancedBy == this)
                            g._ClearInstancedOwner();
                    }
                    foreach (var g in _claimScratch)
                    {
                        if (!_claimed.Contains(g))
                            g._SetInstancedOwner(this);
                    }
                    var tmp = _claimed;
                    _claimed = _claimScratch;
                    _claimScratch = tmp;
                }

                if (_quads.Count == 0)
                {
                    for (int i = 0; i < _prevSegments.Count; i++)
                        RecycleSegment(_prevSegments[i]);
                    _prevSegments.Clear();
                    return;
                }

                EnsureUploadCapacity(_quads.Count);
                _quads.CopyTo(_uploadArray);
                if (_vertexPath)
                {
                    //vertex-stream backend: instance data baked x4 into per-segment
                    //mesh vertices; no compute buffers anywhere
                    for (int i = 0; i < _quads.Count; i++)
                        QuadVertex.WriteQuad(_vertexUpload, i, in _uploadArray[i]);
                }
                else
                {
                    //capacity-grown, never shrunk: segments read only within
                    //_InstanceStart/Count, so a larger buffer is harmless
                    if (_buffer == null || _bufferCapacity < _quads.Count)
                    {
                        if (_buffer != null)
                            _buffer.Release();
                        _bufferCapacity = Mathf.NextPowerOfTwo(Mathf.Max(_quads.Count, 256));
                        _buffer = new ComputeBuffer(_bufferCapacity, QuadInstance.Stride, ComputeBufferType.Structured);
                    }
                    _buffer.SetData(_uploadArray, 0, 0, _quads.Count);

                    if (_clipUploadArray == null || _clipUploadArray.Length < _clipEntries.Count)
                        _clipUploadArray = new ClipEntry[Mathf.NextPowerOfTwo(Mathf.Max(_clipEntries.Count, 16))];
                    _clipEntries.CopyTo(_clipUploadArray);
                    if (_clipBuffer == null || _clipBufferCapacity < _clipEntries.Count)
                    {
                        if (_clipBuffer != null)
                            _clipBuffer.Release();
                        _clipBufferCapacity = Mathf.NextPowerOfTwo(Mathf.Max(_clipEntries.Count, 16));
                        _clipBuffer = new ComputeBuffer(_clipBufferCapacity, ClipEntry.Stride, ComputeBufferType.Structured);
                    }
                    _clipBuffer.SetData(_clipUploadArray, 0, 0, _clipEntries.Count);

                    int maxCount = 0;
                    foreach (var seg in _segments)
                        if (seg.count > maxCount) maxCount = seg.count;
                    EnsurePullMesh(maxCount);
                }

                //pass 1: transfer renderers where the texture layout matches by
                //index (z is index-derived, so the transform needs no touch)
                int transferable = Mathf.Min(_prevSegments.Count, _segments.Count);
                for (int i = 0; i < transferable; i++)
                {
                    Segment prev = _prevSegments[i];
                    Segment seg = _segments[i];
                    if (prev.go == null || !SameTextureSet(prev, seg))
                        continue;
                    seg.go = prev.go;
                    seg.filter = prev.filter;
                    seg.renderer = prev.renderer;
                    seg.mesh = prev.mesh;
                    seg.props = prev.props;
                    seg.meshQuadCap = prev.meshQuadCap;
                    seg.lastSortingOrder = prev.lastSortingOrder;
                    seg.lastLayer = prev.lastLayer;
                    prev.go = null;
                    prev.filter = null;
                    prev.renderer = null;
                    prev.mesh = null;
                    prev.props = null;
                }
                //pass 2: unconsumed old segments feed the pools before new claims
                for (int i = 0; i < _prevSegments.Count; i++)
                    RecycleSegment(_prevSegments[i]);
                _prevSegments.Clear();

                //pass 3: resolve materials, claim what did not transfer, upload
                foreach (var seg in _segments)
                {
                    Material mat;
                    var texKey = new TexSetKey { t0 = seg.textures[0], t1 = seg.textures[1], t2 = seg.textures[2], t3 = seg.textures[3] };
                    if (!_materialCache.TryGetValue(texKey, out mat))
                    {
                        mat = new Material(_shader);
                        mat.hideFlags = HideFlags.DontSave;
                        mat.mainTexture = seg.textures[0];
                        if (seg.textures[1] != null) mat.SetTexture("_Tex1", seg.textures[1]);
                        if (seg.textures[2] != null) mat.SetTexture("_Tex2", seg.textures[2]);
                        if (seg.textures[3] != null) mat.SetTexture("_Tex3", seg.textures[3]);
                        _materialCache.Add(texKey, mat);
                    }
                    seg.material = mat;
                    if (seg.go == null)
                    {
                        ClaimSegmentRenderer(seg);
                        if (_mpbPool.Count > 0)
                        {
                            seg.props = _mpbPool[_mpbPool.Count - 1];
                            _mpbPool.RemoveAt(_mpbPool.Count - 1);
                        }
                        else
                            seg.props = new MaterialPropertyBlock();
                    }
                    else if (seg.renderer.sharedMaterial != mat)
                        seg.renderer.sharedMaterial = mat;

                    if (_vertexPath)
                    {
                        UploadSegmentMesh(seg);
                    }
                    else
                    {
                        seg.props.SetBuffer("_Instances", _buffer);
                        seg.props.SetBuffer("_Clips", _clipBuffer);
                        seg.props.SetInt("_InstanceStart", seg.start);
                        seg.props.SetInt("_InstanceCount", seg.count);
                    }
                    seg.renderer.SetPropertyBlock(seg.props);
                }

                //fresh property blocks need the shared uniforms pushed once; run
                //orders and curve-font bindings re-evaluate after a recompile
                RecomputeSlotMatrices();
                _slotsDirty = false;
                _propsDirty = true;
                _lastRunOrderProbe = int.MinValue;
            }
        }

        void ExtractContainer(Container container, Matrix4x4 worldToLocal, uint clipIndex, bool grayed, uint slotIndex)
        {
            int cnt = container.numChildren;
            for (int i = 0; i < cnt; i++)
            {
                DisplayObject child = container.GetChildAt(i);
                if (!child.visible)
                    continue;
                //grayed inherits down, mirroring UpdateContext.grayed accumulation
                bool childGrayed = grayed || child.grayed;

                //painting scopes (filter/blend/perspective/cacheAsBitmap) render
                //through their own capture pipeline — leave them native (review
                //M12), but as a sort BARRIER: the blit quad interleaves with the
                //stream in the native sorting space (design §9)
                if (child._paintingMode > 0)
                {
                    _maskedSubtrees++;
                    AddScopeBarrier(child, child.paintingGraphics);
                    continue;
                }

                //GoWrapper renders through self-managed external renderers and
                //has no graphics: without a barrier its 3D content could sort
                //anywhere relative to the stream's segments
                if (child is GoWrapper)
                {
                    _maskedSubtrees++;
                    AddScopeBarrier(child, null);
                    continue;
                }

                if (child.graphics != null && child.graphics.texture != null)
                    ExtractLeaf(child.graphics, worldToLocal, clipIndex, childGrayed, slotIndex);

                if (child.graphics != null && child.graphics.subInstances != null)
                {
                    foreach (var sub in child.graphics.subInstances)
                        if (sub.texture != null)
                            ExtractLeaf(sub, worldToLocal, clipIndex, childGrayed, slotIndex);
                }

                if (child is Container c)
                {
                    //stencil-masked scopes go to the fallback renderer, and the
                    //subtree's native block needs a barrier so overlapping stream
                    //content cannot sort across it (design §9)
                    if (c.mask != null)
                    {
                        _maskedSubtrees++;
                        AddScopeBarrier(c, null);
                        continue;
                    }
                    //M8-2: a valid baked mount splices its precompiled stream
                    //instead of walking the subtree (in-place streams only —
                    //the baker's replica must always walk real content)
                    if (_inPlace && c._fqsPending != null)
                        FqsAutoMount._Realize(c); //bind against the tree we are walking now
                    if (_inPlace && c._fqsMount != null)
                    {
                        FqsMount fm = c._fqsMount;
                        if (!fm.invalid && SpliceMount(c, fm, clipIndex, childGrayed))
                            continue;
                        fm.invalid = true; //failed splice: stop retrying every extract
                    }
                    //slot promotion BEFORE the clip push: a moving clip owner's
                    //window must ride its own slot
                    uint childSlot = slotIndex;
                    Matrix4x4 childW2L = worldToLocal;
                    if (_inPlace && _hotContainers.ContainsKey(c))
                        TryPromoteSlot(c, ref childSlot, ref childW2L);
                    uint childClip = clipIndex;
                    if (c.clipRect != null)
                        childClip = PushClip(c, clipIndex, childSlot);
                    ExtractContainer(c, childW2L, childClip, childGrayed, childSlot);
                }
            }
        }

        /// <summary>
        /// Emits a sort barrier for a container-level fallback scope (stencil
        /// mask / painting capture / GoWrapper): the subtree keeps its native
        /// renderers, and a null-key entry with an INFINITE box keeps ALL
        /// stream content from sorting across it — the same absolute-barrier
        /// semantics native fairyBatching gives breakBatch elements. A tight
        /// AABB would merge better, but every tight bound goes stale with no
        /// invalidation channel to catch it (mask tween, wrapped-content
        /// animation, filter extend growth — adversarial review round 2), and
        /// the native batcher never merged across these scopes either.
        /// The scope closes a run in BuildSegments, so the neighboring runs'
        /// sortingOrders straddle its native order block (see RunBarrier.order).
        /// </summary>
        void AddScopeBarrier(DisplayObject scope, NGraphics blit)
        {
            _entries.Add(new AdjacencyEntry
            {
                key = null,
                x0 = -1e30f, y0 = -1e30f, x1 = 1e30f, y1 = 1e30f,
                payload = _pending.Count
            });
            _pending.Add(new PendingLeaf { graphics = blit, scope = scope, instanceable = false });
        }

        /// <summary>
        /// Assigns (or reuses) a transform slot for a hot container: its subtree
        /// bakes in the container's own local space from here on.
        /// </summary>
        void TryPromoteSlot(Container c, ref uint slotIndex, ref Matrix4x4 worldToLocal)
        {
            int idx;
            if (!_slotIndices.TryGetValue(c, out idx))
            {
                idx = -1;
                for (int i = 1; i < MaxTransformSlots; i++)
                {
                    if (_slotOwners[i] == null)
                    {
                        idx = i;
                        break;
                    }
                }
                if (idx < 0)
                {
                    slotOverflow++;
                    return; //no slot left: stays recompile-on-move
                }
                _slotIndices[c] = idx;
                _slotOwners[idx] = c;
            }
            slotIndex = (uint)idx;
            worldToLocal = c.cachedTransform.worldToLocalMatrix;
        }

        /// <summary>
        /// Slot matrices map slot-local quads back to CURRENT stream-root space:
        /// M = root.worldToLocal x owner.localToWorld, exact at bake time and
        /// re-derived from the live transforms whenever a slot moves.
        /// </summary>
        void RecomputeSlotMatrices()
        {
            Matrix4x4 rootW2L = _container.cachedTransform.worldToLocalMatrix;
            _slotMatrixArr[0] = Matrix4x4.identity;
            for (int i = 1; i < MaxTransformSlots; i++)
            {
                Container owner = _slotOwners[i];
                _slotMatrixArr[i] = owner == null || owner.isDisposed
                    ? Matrix4x4.identity
                    : rootW2L * owner.cachedTransform.localToWorldMatrix;
            }
        }

        /// <summary>
        /// Re-derives the root-space rect of every slot-riding internal clip from
        /// its owner's CURRENT transform (rotation degrades to the AABB exactly
        /// like Extract does), refolding with the parent — parents are always
        /// registered before children, so one in-order pass suffices.
        /// </summary>
        void RecomputeSlottedClips()
        {
            Matrix4x4 rootW2L = _container.cachedTransform.worldToLocalMatrix;
            for (int i = 1; i < _clipEntries.Count && i < _clipMeta.Count; i++)
            {
                ClipMeta meta = _clipMeta[i];
                if (meta.slotIndex == 0 || meta.owner == null || meta.owner.isDisposed)
                    continue;
                Matrix4x4 m = rootW2L * meta.owner.cachedTransform.localToWorldMatrix;
                Vector4 rect = TransformClipRect(meta.rect, m);
                if (meta.parentIndex != 0)
                {
                    Vector4 pr = _clipEntries[(int)meta.parentIndex].rect;
                    rect = new Vector4(Mathf.Max(rect.x, pr.x), Mathf.Max(rect.y, pr.y),
                        Mathf.Min(rect.z, pr.z), Mathf.Min(rect.w, pr.w));
                }
                ClipEntry e = _clipEntries[i];
                e.rect = rect;
                _clipEntries[i] = e;
            }
        }

        /// <summary>
        /// Registers an internal clip region: the container's clipRect transformed to
        /// stream-local space, folded (intersected) with the enclosing region — same
        /// semantics as UpdateContext.EnterClipping. Identical regions are deduped.
        /// </summary>
        uint PushClip(Container c, uint parentIndex, uint slotIndex)
        {
            //the ENTRY rect is always root-space (the shader's clip test runs on
            //slot-transformed positions); at extract time the slot matrix is the
            //live transform, so the root-relative matrix bakes the same value
            Rect clipRect = (Rect)c.clipRect;
            Matrix4x4 m = _rootWorldToLocal * c.cachedTransform.localToWorldMatrix;
            Vector4 rect = TransformClipRect(clipRect, m);

            if (parentIndex != 0)
            {
                Vector4 p = _clipEntries[(int)parentIndex].rect;
                rect = new Vector4(Mathf.Max(rect.x, p.x), Mathf.Max(rect.y, p.y),
                    Mathf.Min(rect.z, p.z), Mathf.Min(rect.w, p.w));
            }

            Vector4 soft = Vector4.zero;
            if (c.clipSoftness != null)
            {
                Vector4 s = (Vector4)c.clipSoftness;
                soft = new Vector4(s.x, s.w, s.z, s.y);
            }

            for (int i = 1; i < _clipEntries.Count; i++)
            {
                //slot-riding entries only dedup within the same slot: same slot
                //means the merged owners move together (diverging owners fire the
                //structure channel and recompile)
                if (_clipEntries[i].rect == rect && _clipEntries[i].soft == soft
                    && _clipMeta[i].slotIndex == slotIndex)
                    return (uint)i;
            }

            //vertex path: internal clips live in a fixed uniform array; on overflow
            //reuse the enclosing region (correct but coarser: the child clips only
            //by its parent window) and warn once
            if (_vertexPath && _clipEntries.Count >= MaxVertexPathClips)
            {
                if (!_clipOverflowWarned)
                {
                    _clipOverflowWarned = true;
                    Debug.LogWarning($"InstancedUIStream: more than {MaxVertexPathClips - 1} internal clip regions on the vertex-stream backend; extra regions clip by their parent window.");
                }
                return parentIndex;
            }

            _clipEntries.Add(new ClipEntry { rect = rect, soft = soft });
            _clipMeta.Add(new ClipMeta { owner = c, rect = clipRect, parentIndex = parentIndex, slotIndex = slotIndex });
            if (slotIndex != 0)
                _hasSlottedClips = true;
            return (uint)(_clipEntries.Count - 1);
        }

        void ExtractLeaf(NGraphics graphics, Matrix4x4 worldToLocal, uint clipIndex, bool grayed, uint slotIndex)
        {
            //M8-5: deferred (renderless) leaves build their mesh on first read
            if (graphics._renderless)
                graphics._EnsureMeshBuilt();
            Mesh mesh = graphics.mesh;
            if (mesh == null || mesh.vertexCount == 0)
                return;
            //enabled is an admission condition (review M10/M14); its setter pushes
            if (graphics.meshRenderer != null && !graphics.meshRenderer.enabled)
                return;
            //color tier (batch 3): deferred alpha/tint must land in the mesh
            //before the quads are staged from it
            graphics._RestoreNativeColors();
            Texture tex = graphics.texture.nativeTexture;
            if (tex == null)
                return;

            if (_inPlace && _watchedTextures.Add(graphics.texture))
                graphics.texture.onSizeChanged += _onWatchedTexture;

            bool alphaTex = graphics.shader == ShaderConfig.textShader;
            uint flags = alphaTex ? QuadInstance.FlagAlphaTexture : 0u;
            if (grayed)
                flags |= QuadInstance.FlagGrayed;
            Matrix4x4 m = worldToLocal * graphics.gameObject.transform.localToWorldMatrix;

            //stream-local AABB for the overlap test (transform the mesh AABB's
            //4 xy corners so rotated leaves stay conservative), in the same
            //offset space as the clip entries — for slot-baked leaves the quads
            //are slot-local but the SORT still runs in root space
            Matrix4x4 mb2root = slotIndex == 0 ? m
                : _rootWorldToLocal * graphics.gameObject.transform.localToWorldMatrix;
            Bounds mb = mesh.bounds;
            Vector2 c0 = mb2root.MultiplyPoint3x4(new Vector3(mb.min.x, mb.min.y, 0));
            Vector2 c1 = mb2root.MultiplyPoint3x4(new Vector3(mb.max.x, mb.min.y, 0));
            Vector2 c2 = mb2root.MultiplyPoint3x4(new Vector3(mb.min.x, mb.max.y, 0));
            Vector2 c3 = mb2root.MultiplyPoint3x4(new Vector3(mb.max.x, mb.max.y, 0));
            Vector2 bmin = Vector2.Min(Vector2.Min(c0, c1), Vector2.Min(c2, c3)) + _drawOffset;
            Vector2 bmax = Vector2.Max(Vector2.Max(c0, c1), Vector2.Max(c2, c3)) + _drawOffset;

            //clamp sort bounds to the clip window: parts a clip discards cannot
            //cause visual overlap, so they should not block segment merging either
            if (clipIndex != 0)
            {
                Vector4 cr = _clipEntries[(int)clipIndex].rect;
                bmin = Vector2.Max(bmin, new Vector2(cr.x, cr.y));
                bmax = Vector2.Min(bmax, new Vector2(cr.z, cr.w));
            }

            //stage the quads NOW (mesh is hot); non-quad topology becomes a
            //fallback barrier: it keeps its native renderer, and a null sort key
            //makes it immovable — others may still sort past it when they do not
            //overlap, exactly the legality rule native FairyBatching uses
            var p = new PendingLeaf { graphics = graphics, texture = tex, flags = flags, clipIndex = clipIndex, stageStart = _staging.Count, bakeAlpha = graphics._currentAlpha, slotIndex = slotIndex };

            //non-Normal blend modes cannot join the stream (its blend state is
            //fixed): keep the native renderer, act as a sort barrier (review batch 1)
            if (graphics.blendMode != BlendMode.Normal)
            {
                p.stageCount = 0;
                p.instanceable = false;
                _entries.Add(new AdjacencyEntry
                {
                    key = null,
                    x0 = bmin.x, y0 = bmin.y, x1 = bmax.x, y1 = bmax.y,
                    payload = _pending.Count
                });
                _pending.Add(p);
                return;
            }

            //shader keywords (COLOR_FILTER on Image/MovieClip, TMP effects) and
            //custom materials live on the native material/property block, which
            //an instanced quad cannot carry — same native-renderer barrier as
            //blend (ColorFilter audit; the filter pokes the structure channel
            //so an already-claimed leaf gets released on the next extract)
            if (graphics._hasKeywordOrCustomMaterial)
            {
                p.stageCount = 0;
                p.instanceable = false;
                _entries.Add(new AdjacencyEntry
                {
                    key = null,
                    x0 = bmin.x, y0 = bmin.y, x1 = bmax.x, y1 = bmax.y,
                    payload = _pending.Count
                });
                _pending.Add(p);
                return;
            }

            //M9b/batch 5: curve-text leaves (standalone CurveTextMesh or a
            //TextField on a CurveBaseFont) emit one analytic glyph quad per
            //character. Where the stream cannot express them — vertex-stream
            //backend, rotated leaves — the ENCODED native mesh must not be
            //reassembled into the stream: the leaf keeps its native renderer
            //(FairyGUI/CurveText) and becomes a sort barrier.
            bool curveLeaf = (graphics.meshFactory is CurveTextMesh cmProbe && cmProbe.glyphQuads.Count > 0)
                || (graphics._curveGlyphs != null && graphics._curveGlyphs.Count > 0);
            //batch 5b: outline/shadow live in the leaf's property block, which
            //an instanced quad cannot carry — same native-renderer barrier as
            //rotation (bold is fine: it rides padding bit 20)
            if (curveLeaf && (Mathf.Abs(m.m01) > 1e-4f || Mathf.Abs(m.m10) > 1e-4f
                || graphics._curveFxActive))
            {
                p.stageCount = 0;
                p.instanceable = false;
                _entries.Add(new AdjacencyEntry
                {
                    key = null,
                    x0 = bmin.x, y0 = bmin.y, x1 = bmax.x, y1 = bmax.y,
                    payload = _pending.Count
                });
                _pending.Add(p);
                return;
            }
            int curveCount = EmitCurveQuads(graphics, m, _staging, flags & QuadInstance.FlagGrayed);
            if (curveCount > 0)
            {
                //glyph-count slack, same as alpha-texture text (batch 2): pad to
                //the next power of two with degenerate quads so length changes
                //stay on the tier-2 path
                int curveSlack = Mathf.NextPowerOfTwo(curveCount);
                for (int k = curveCount; k < curveSlack; k++)
                    _staging.Add(default);
                p.stageCount = curveSlack;
                p.instanceable = true;
                p.curve = true;
                StampIndices(_staging, p.stageStart, p.stageCount, clipIndex, slotIndex);
                _entries.Add(new AdjacencyEntry
                {
                    key = tex,
                    x0 = bmin.x, y0 = bmin.y, x1 = bmax.x, y1 = bmax.y,
                    payload = _pending.Count
                });
                _pending.Add(p);
                return;
            }

            //M7: rounded/stroked rect and circle shapes bypass their triangulated
            //mesh entirely — analytic SDF coverage from 1-2 quads
            int sdfCount = EmitSdfQuads(graphics, m, _staging, flags & QuadInstance.FlagGrayed);
            if (sdfCount > 0)
            {
                p.stageCount = sdfCount;
                p.instanceable = true;
                p.sdf = true;
                StampIndices(_staging, p.stageStart, p.stageCount, clipIndex, slotIndex);
            }
            else
            {
                mesh.GetVertices(sVerts);
                mesh.GetUVs(0, sUVs);
                mesh.GetColors(sColors);
                mesh.GetTriangles(sTris, 0);
                int skipped;
                p.stageCount = QuadReassembler.Append(_staging, sVerts, sUVs, sColors, sTris, m, _drawOffset, flags, out skipped);
                if (skipped > 0)
                {
                    _skippedPairs += skipped;
                    _staging.RemoveRange(p.stageStart, p.stageCount);
                    p.stageCount = 0;
                    p.instanceable = false;
                    //re-admission parity: a leaf rejected HERE (born gradient/
                    //polygon) must react to content pushes exactly like a leaf
                    //that was claimed once and then released — otherwise the
                    //same leaf re-admits or not depending on its history.
                    //Unconditional overwrite: the CURRENT rejecter is the only
                    //stream whose extraction covers this leaf, so a leaf moved
                    //from stream A to B re-homes on B's first recompile
                    //(first-writer-wins left it dirtying A forever — review
                    //round 3). In-place only: replica (bake) leaves are
                    //throwaway
                    if (_inPlace && p.graphics._instancedBy == null)
                        p.graphics._lastInstancedBy = this;
                }
                else
                {
                    //text slack (batch 2): glyph counts jitter ('9'->'10'), so
                    //text leaves round their range up to a power of two and pad
                    //with degenerate quads (zero size renders nothing) — length
                    //changes within the slack stay on the microsecond tier-2
                    //path instead of forcing a full recompile
                    if (alphaTex && p.stageCount > 0)
                    {
                        int slack = Mathf.NextPowerOfTwo(p.stageCount);
                        for (int k = p.stageCount; k < slack; k++)
                            _staging.Add(default);
                        p.stageCount = slack;
                    }
                    p.instanceable = true;
                    StampIndices(_staging, p.stageStart, p.stageCount, clipIndex, slotIndex);
                }
            }

            _entries.Add(new AdjacencyEntry
            {
                key = p.instanceable ? tex : null,
                x0 = bmin.x, y0 = bmin.y, x1 = bmax.x, y1 = bmax.y,
                payload = _pending.Count
            });
            _pending.Add(p);
        }

        /// <summary>
        /// Consumes the sorted entry list: instanceable leaves append their staged
        /// quads into the final stream (segmenting on texture change), fallback
        /// barriers close the current segment and delimit a RUN — segments of one
        /// run share a sortingOrder slot below the barrier's own order, so native
        /// fallback renderers interleave correctly between runs.
        /// </summary>
        /// <summary>
        /// M7: shapes whose silhouette is an analytic SDF skip mesh reassembly —
        /// a rounded/stroked rect is 1-2 quads (fill inset by the border width,
        /// border band [edge-width, edge], per RoundedRectMesh's geometry), a full
        /// circle is a rounded rect with maxed radii. Returns emitted quad count,
        /// 0 when the factory is not SDF-expressible (gradient ellipses, pies,
        /// rotated/non-uniformly-scaled leaves fall back to the mesh path).
        /// </summary>
        int EmitSdfQuads(NGraphics graphics, Matrix4x4 m, List<QuadInstance> dst, uint extraFlags)
        {
            float rBL, rBR, rTL, rTR, lineWidth;
            Color32 lineColor;
            Color fill;
            Rect local;

            if (graphics.meshFactory is RoundedRectMesh rrm)
            {
                local = rrm.drawRect != null ? (Rect)rrm.drawRect : graphics.contentRect;
                fill = rrm.fillColor != null ? (Color)(Color32)rrm.fillColor : graphics._tintColor;
                lineWidth = rrm.lineWidth;
                lineColor = rrm.lineColor;
                //corner names map directly: FairyGUI's visual top-left is the
                //(minX, maxY) quadrant of our y-negated quad-local space
                float rMax = Mathf.Min(local.width, local.height) * 0.5f;
                rTL = Mathf.Min(rrm.topLeftRadius, rMax);
                rTR = Mathf.Min(rrm.topRightRadius, rMax);
                rBL = Mathf.Min(rrm.bottomLeftRadius, rMax);
                rBR = Mathf.Min(rrm.bottomRightRadius, rMax);
            }
            else if (graphics.meshFactory is EllipseMesh el)
            {
                local = el.drawRect != null ? (Rect)el.drawRect : graphics.contentRect;
                if (el.centerColor != null || el.startDegree != 0 || el.endDegreee != 360
                    || Mathf.Abs(local.width - local.height) > 0.01f)
                    return 0; //gradients, pies and true ellipses stay on the mesh path
                fill = el.fillColor != null ? (Color)(Color32)el.fillColor : graphics._tintColor;
                lineWidth = el.lineWidth;
                lineColor = el.lineColor;
                rBL = rBR = rTL = rTR = local.width * 0.5f;
            }
            else
                return 0;

            //transform to stream-local; the SDF encodes an axis-aligned rect, so a
            //rotated leaf falls back; non-uniform scale would need elliptical
            //corners — fall back there too (native triangulation stays exact)
            Vector2 p0 = m.MultiplyPoint3x4(new Vector3(local.xMin, -local.yMax, 0));
            Vector2 p1 = m.MultiplyPoint3x4(new Vector3(local.xMax, -local.yMin, 0));
            if (Mathf.Abs(m.m01) > 1e-4f || Mathf.Abs(m.m10) > 1e-4f)
                return 0;
            Vector2 bmin = Vector2.Min(p0, p1) + _drawOffset;
            Vector2 size = Vector2.Max(p0, p1) - Vector2.Min(p0, p1);
            if (size.x <= 0 || size.y <= 0)
                return 0;
            float scaleX = local.width > 0 ? size.x / local.width : 1;
            float scaleY = local.height > 0 ? size.y / local.height : 1;
            if (Mathf.Abs(scaleX - scaleY) > 0.01f)
                return 0;
            float s = scaleX;
            if (Mathf.Max(Mathf.Max(rBL, rBR), Mathf.Max(rTL, rTR)) * s > 255 || lineWidth * s > 255)
                return 0; //beyond the packed byte range: keep native

            float alpha = graphics._currentAlpha;
            uint radii = QuadInstance.PackRadii(rBL * s, rBR * s, rTL * s, rTR * s);
            var rect = new Vector4(bmin.x, bmin.y, size.x, size.y);
            uint wBits;

            //fill is always emitted (quad count stays stable under tint changes);
            //the border quad appears only when a line width exists
            Color fc = fill;
            fc.a *= alpha;
            wBits = QuadInstance.PackBorderWidth(QuadInstance.FlagSdfFill | extraFlags, lineWidth * s);
            dst.Add(new QuadInstance { rect = rect, color = fc, flags = wBits, padding = radii });
            if (lineWidth > 0)
            {
                Color lc = lineColor;
                lc.a *= alpha;
                wBits = QuadInstance.PackBorderWidth(QuadInstance.FlagSdfBorder | extraFlags, lineWidth * s);
                dst.Add(new QuadInstance { rect = rect, color = lc, flags = wBits, padding = radii });
                return 2;
            }
            return 1;
        }

        /// <summary>
        /// M9b: CurveTextMesh leaves emit one quad per glyph; coverage comes from
        /// the quadratic outlines in CurveFontStore. The corner-UV channel carries
        /// the glyph-space mapping, padding carries the glyph index. Vertex-stream
        /// backend and rotated leaves keep the native ghost fallback for now.
        /// </summary>
        int EmitCurveQuads(NGraphics graphics, Matrix4x4 m, List<QuadInstance> dst, uint extraFlags)
        {
            IReadOnlyList<CurveTextMesh.GlyphQuad> quads;
            if (graphics.meshFactory is CurveTextMesh ctm)
                quads = ctm.glyphQuads;
            else if (graphics._curveGlyphs != null)
                quads = graphics._curveGlyphs; //batch 5: CurveBaseFont side table
            else
                return 0;
            if (quads.Count == 0)
                return 0;
            if (Mathf.Abs(m.m01) > 1e-4f || Mathf.Abs(m.m10) > 1e-4f)
                return 0;

            float alpha = graphics._currentAlpha;
            int n = 0;
            for (int qi = 0; qi < quads.Count; qi++)
            {
                CurveTextMesh.GlyphQuad gq = quads[qi];
                Vector2 pa = m.MultiplyPoint3x4(new Vector3(gq.rect.xMin, -gq.rect.yMax, 0));
                Vector2 pb = m.MultiplyPoint3x4(new Vector3(gq.rect.xMax, -gq.rect.yMin, 0));
                Vector2 mn = Vector2.Min(pa, pb) + _drawOffset;
                Vector2 sz = Vector2.Max(pa, pb) - Vector2.Min(pa, pb);
                Color col = gq.color;
                col.a *= alpha;
                if (gq.glyphIndex < 0)
                {
                    //solid rect (underline/strikethrough): plain quad sampling
                    //the leaf texture's center (Empty/white for curve fonts)
                    Rect uvr = graphics.texture.uvRect;
                    var cuv = new Vector4((uvr.xMin + uvr.xMax) * 0.5f, (uvr.yMin + uvr.yMax) * 0.5f,
                        (uvr.xMin + uvr.xMax) * 0.5f, (uvr.yMin + uvr.yMax) * 0.5f);
                    dst.Add(new QuadInstance
                    {
                        rect = new Vector4(mn.x, mn.y, sz.x, sz.y),
                        uvA = cuv,
                        uvB = cuv,
                        color = col,
                        flags = extraFlags
                    });
                }
                else
                {
                    Vector4 bb = gq.bbox;
                    dst.Add(new QuadInstance
                    {
                        rect = new Vector4(mn.x, mn.y, sz.x, sz.y),
                        //quad corner (0,0) sits at the SMALLEST unity y = glyph bottom
                        //(em bbox min y), so the interpolated uv is the em position
                        uvA = new Vector4(bb.x, bb.y, bb.z, bb.y),
                        uvB = new Vector4(bb.x, bb.w, bb.z, bb.w),
                        color = col,
                        flags = QuadInstance.FlagCurveGlyph | extraFlags,
                        //bold at bit 20, NOT 24: the vertex path rebuilds this
                        //through float32 (exact to 2^24) — index|1<<24 would
                        //round odd indices to the neighbouring glyph
                        padding = (uint)gq.glyphIndex | (gq.bold ? 1u << 20 : 0u)
                    });
                }
                n++;
            }
            return n;
        }

        void BuildSegments()
        {
            Segment seg = null;
            int runIndex = 0;
            for (int i = 0; i < _entries.Count; i++)
            {
                PendingLeaf leaf = _pending[_entries[i].payload];
                if (!leaf.instanceable)
                {
                    //_runBarriers[r] closes run r; the final run has no closer
                    _runBarriers.Add(new RunBarrier { graphics = leaf.graphics, scope = leaf.scope });
                    runIndex++;
                    seg = null;
                    continue;
                }

                //M8-2: a mount splices its own frozen segments/leaves wholesale
                if (leaf.mount != null)
                {
                    SpliceMountSegments(leaf, runIndex);
                    seg = null; //never merge neighbors into spliced segments
                    continue;
                }

                int texIdx = seg != null ? IndexOfOrAddTexture(seg, leaf.texture) : -1;
                if (texIdx < 0)
                {
                    seg = TakeSegment();
                    seg.z = -0.5f * _segments.Count;
                    seg.start = _quads.Count;
                    seg.count = 0;
                    seg.runIndex = runIndex;
                    _segments.Add(seg);
                    texIdx = IndexOfOrAddTexture(seg, leaf.texture);
                }
                uint texBits = (uint)texIdx << QuadInstance.TexIndexShift;

                var range = new LeafRange
                {
                    graphics = leaf.graphics,
                    start = _quads.Count,
                    count = leaf.stageCount,
                    liveCount = leaf.stageCount,
                    bakedAlpha = leaf.bakeAlpha,
                    slotIndex = leaf.slotIndex,
                    texIndexBits = texBits,
                    segIndex = _segments.Count - 1,
                    flags = leaf.flags,
                    clipIndex = leaf.clipIndex,
                    sdf = leaf.sdf,
                    curve = leaf.curve
                };
                for (int q = 0; q < leaf.stageCount; q++)
                {
                    QuadInstance qi = _staging[leaf.stageStart + q];
                    qi.flags |= texBits;
                    _quads.Add(qi);
                }
                seg.count = _quads.Count - seg.start;
                _leaves.Add(range);
                _leafLookup[range.graphics] = range;
            }
        }

        void HideInvisibleBelow(DisplayObject obj)
        {
            if (obj is Container c)
            {
                int cnt = c.numChildren;
                for (int i = 0; i < cnt; i++)
                {
                    DisplayObject child = c.GetChildAt(i);
                    if (!child.visible)
                        HideSubtree(child, true);
                    else
                        HideInvisibleBelow(child);
                }
            }
        }

        static void StampIndices(List<QuadInstance> quads, int start, int count, uint clipIndex, uint slotIndex)
        {
            if (clipIndex == 0 && slotIndex == 0)
                return;
            for (int i = start; i < start + count; i++)
            {
                QuadInstance q = quads[i];
                q.clipIndex = clipIndex;
                q.transformIndex = slotIndex;
                quads[i] = q;
            }
        }
    }
}
