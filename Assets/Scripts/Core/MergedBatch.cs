using System;
using System.Collections.Generic;
using UnityEngine;
#if UNITY_2020_1_OR_NEWER
using Unity.Profiling;
#endif

namespace FairyGUI
{
    /// <summary>
    /// Experimental mesh-merging renderer for a fairyBatching root (see Container.mergedBatching).
    ///
    /// Consecutive batch elements sharing a material are baked into one combined mesh drawn
    /// by a single renderer, so the container costs one draw call per material run instead of
    /// one per element. Leaf display objects keep their full update pipeline (mesh generation,
    /// material selection, hit testing); only their renderers are switched off via
    /// forceRenderingOff while their content is drawn by the merged mesh.
    ///
    /// Rebuild is dirty-driven:
    /// - structure: DoFairyBatching re-sort marks the batch dirty
    /// - content: NGraphics._contentVersion changes (mesh rebuild / alpha / tint)
    /// - material: the material a leaf resolved this frame differs from the baked one
    /// - transform: Transform.hasChanged on any watched leaf or intermediate container
    ///
    /// Elements that cannot be merged (masks, painting mode, custom property blocks,
    /// vertex matrices, breakBatch) keep their own renderer; draw order is preserved because
    /// each merged run takes the sortingOrder of its first element.
    /// </summary>
    class MergedBatch
    {
        class Run
        {
            public GameObject gameObject;
            public MeshFilter meshFilter;
            public MeshRenderer meshRenderer;
            public Mesh mesh;
            public Material material;
            public NGraphics firstSource; //sortingOrder is taken from this element each frame
            public int sortingOrder = -1;
        }

        Container _owner;
        bool _structureDirty;

        readonly List<Run> _runs = new List<Run>();
        readonly List<Run> _runPool = new List<Run>();

        //bookkeeping for merged sources, parallel lists
        readonly List<NGraphics> _sources = new List<NGraphics>();
        readonly List<int> _sourceVersions = new List<int>();
        readonly List<Material> _sourceMaterials = new List<Material>();

        //transforms whose movement invalidates baked vertices (leaves + intermediate containers)
        readonly List<Transform> _watched = new List<Transform>();

        //scratch buffers shared by all MergedBatch instances (main thread only)
        static readonly List<Vector3> sVerts = new List<Vector3>();
        static readonly List<Color32> sColors = new List<Color32>();
        static readonly List<Vector2> sUV0 = new List<Vector2>();
        static readonly List<Vector2> sUV1 = new List<Vector2>();
        static readonly List<int> sTriangles = new List<int>();

        static readonly List<Vector3> cVerts = new List<Vector3>();
        static readonly List<Color32> cColors = new List<Color32>();
        static readonly List<Vector2> cUV0 = new List<Vector2>();
        static readonly List<Vector2> cUV1 = new List<Vector2>();
        static readonly List<int> cTriangles = new List<int>();
        static bool cHasUV1;

#if UNITY_2020_1_OR_NEWER
        static readonly ProfilerMarker sSyncMarker = new ProfilerMarker("MergedBatch.Sync");
        static readonly ProfilerMarker sBuildMarker = new ProfilerMarker("MergedBatch.Build");
#endif

        public MergedBatch(Container owner)
        {
            _owner = owner;
            _structureDirty = true;
        }

        public void SetStructureDirty()
        {
            _structureDirty = true;
        }

        public void Sync(UpdateContext context, List<BatchElement> elements)
        {
            if (elements == null)
                return;

#if UNITY_2020_1_OR_NEWER
            using (sSyncMarker.Auto())
#endif
            {
                if (_structureDirty || IsContentDirty())
                    Build(elements);

                Stats.MergedRuns += _runs.Count;
                Stats.MergedElements += _sources.Count;
            }

            //the owner may switch layers (e.g. painting mode captures the subtree on a
            //hidden layer); run GameObjects are not display children, so follow explicitly
            int layer = _owner.gameObject.layer;

            int cnt = _runs.Count;
            for (int i = 0; i < cnt; i++)
            {
                Run run = _runs[i];
                int order = run.firstSource.renderingOrder;
                if (run.sortingOrder != order)
                {
                    run.sortingOrder = order;
                    run.meshRenderer.sortingOrder = order;
                }
                if (run.gameObject.layer != layer)
                    run.gameObject.layer = layer;
            }
        }

        bool IsContentDirty()
        {
            int cnt = _sources.Count;
            for (int i = 0; i < cnt; i++)
            {
                NGraphics g = _sources[i];
                if (g._contentVersion != _sourceVersions[i])
                    return true;
                if (!ReferenceEquals(g.material, _sourceMaterials[i]))
                    return true;
            }

            cnt = _watched.Count;
            for (int i = 0; i < cnt; i++)
            {
                Transform t = _watched[i];
                if (t == null || t.hasChanged)
                    return true;
            }

            return false;
        }

        void Build(List<BatchElement> elements)
        {
#if UNITY_2020_1_OR_NEWER
            using (sBuildMarker.Auto())
#endif
            {
                BuildInternal(elements);
            }
            Stats.MergedRebuilds++;
        }

        void BuildInternal(List<BatchElement> elements)
        {
            _structureDirty = false;

            //restore renderers of the previous merged set; the new set is re-disabled below,
            //so elements that left the container get their renderer back this frame
            int cnt = _sources.Count;
            for (int i = 0; i < cnt; i++)
            {
                NGraphics g = _sources[i];
                if (g.meshRenderer != null)
                    g.meshRenderer.forceRenderingOff = false;
            }
            _sources.Clear();
            _sourceVersions.Clear();
            _sourceMaterials.Clear();

            foreach (Run run in _runs)
                ReleaseRun(run);
            _runs.Clear();

            CollectWatched();

            Matrix4x4 worldToLocal = _owner.cachedTransform.worldToLocalMatrix;

            Run current = null;
            cnt = elements.Count;
            for (int i = 0; i < cnt; i++)
            {
                BatchElement e = elements[i];
                NGraphics g = GetMergeableGraphics(e);
                if (g == null)
                {
                    CloseRun(ref current);
                    continue;
                }

                Material mat = g.material;
                if (mat == null)
                {
                    CloseRun(ref current);
                    if (g.meshRenderer != null)
                        g.meshRenderer.forceRenderingOff = false;
                    continue;
                }

                if (current != null && !ReferenceEquals(current.material, mat))
                    CloseRun(ref current);

                if (current == null)
                    current = OpenRun(mat, g);

                AppendMesh(g, worldToLocal);
                g.meshRenderer.forceRenderingOff = true;

                _sources.Add(g);
                _sourceVersions.Add(g._contentVersion);
                _sourceMaterials.Add(mat);
            }
            CloseRun(ref current);

            cnt = _watched.Count;
            for (int i = 0; i < cnt; i++)
            {
                Transform t = _watched[i];
                if (t != null)
                    t.hasChanged = false;
            }
        }

        NGraphics GetMergeableGraphics(BatchElement e)
        {
            if (e.breakBatch)
                return null;

            NGraphics g;
            if (e.owner is DisplayObject d)
            {
                if (d._paintingMode != 0)
                    return null;
                g = d.graphics;
            }
            else
                g = e.owner as NGraphics;

            if (g == null || g.meshRenderer == null)
                return null;
            if (g._maskFlag != 0 || g.vertexMatrix != null || g.hasPropertyBlock)
            {
                g.meshRenderer.forceRenderingOff = false;
                return null;
            }

            return g;
        }

        void CollectWatched()
        {
            _watched.Clear();
            CollectWatched(_owner);
        }

        void CollectWatched(Container container)
        {
            int cnt = container.numChildren;
            for (int i = 0; i < cnt; i++)
            {
                DisplayObject child = container.GetChildAt(i);
                if (!child.visible)
                    continue;

                _watched.Add(child.cachedTransform);

                //nested batching roots (clipping, masks, painting, nested fairyBatching)
                //manage their own subtree; their content is not in our element list
                if (child is Container c && (child._flags & DisplayObject.Flags.BatchingRoot) == 0)
                    CollectWatched(c);
            }
        }

        Run OpenRun(Material material, NGraphics firstSource)
        {
            Run run;
            int poolCnt = _runPool.Count;
            if (poolCnt > 0)
            {
                run = _runPool[poolCnt - 1];
                _runPool.RemoveAt(poolCnt - 1);
                run.gameObject.SetActive(true);
            }
            else
            {
                run = new Run();
                run.gameObject = new GameObject("MergedBatch");
                run.gameObject.transform.SetParent(_owner.cachedTransform, false);
                run.meshFilter = run.gameObject.AddComponent<MeshFilter>();
                run.meshRenderer = run.gameObject.AddComponent<MeshRenderer>();
                run.meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                run.meshRenderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
                run.meshRenderer.receiveShadows = false;
                run.mesh = new Mesh();
                run.mesh.name = "MergedBatch";
                run.mesh.MarkDynamic();
                run.meshFilter.mesh = run.mesh;
                run.gameObject.hideFlags = DisplayObject.hideFlags;
                run.meshFilter.hideFlags = DisplayObject.hideFlags;
                run.meshRenderer.hideFlags = DisplayObject.hideFlags;
                run.mesh.hideFlags = DisplayObject.hideFlags;
            }

            run.gameObject.layer = _owner.gameObject.layer;
            run.material = material;
            run.firstSource = firstSource;
            run.sortingOrder = -1;

            cVerts.Clear();
            cColors.Clear();
            cUV0.Clear();
            cUV1.Clear();
            cTriangles.Clear();
            cHasUV1 = false;

            return run;
        }

        void AppendMesh(NGraphics g, Matrix4x4 worldToLocal)
        {
            Mesh mesh = g.mesh;
            if (mesh == null || mesh.vertexCount == 0)
                return;

            mesh.GetVertices(sVerts);
            mesh.GetColors(sColors);
            mesh.GetUVs(0, sUV0);
            mesh.GetUVs(1, sUV1);
            mesh.GetTriangles(sTriangles, 0);

            Matrix4x4 m = worldToLocal * g.gameObject.transform.localToWorldMatrix;
            bool mirrored = m.determinant < 0;

            int vertexBase = cVerts.Count;
            int vcnt = sVerts.Count;
            for (int i = 0; i < vcnt; i++)
                cVerts.Add(m.MultiplyPoint3x4(sVerts[i]));
            cColors.AddRange(sColors);
            cUV0.AddRange(sUV0);

            if (sUV1.Count > 0)
            {
                if (!cHasUV1)
                {
                    cHasUV1 = true;
                    for (int i = 0; i < vertexBase; i++)
                        cUV1.Add(Vector2.zero);
                }
                cUV1.AddRange(sUV1);
            }
            else if (cHasUV1)
            {
                for (int i = 0; i < vcnt; i++)
                    cUV1.Add(Vector2.zero);
            }

            int tcnt = sTriangles.Count;
            if (mirrored)
            {
                //negative scale flips winding; reverse triangles to keep faces visible
                for (int i = 0; i < tcnt; i += 3)
                {
                    cTriangles.Add(sTriangles[i + 2] + vertexBase);
                    cTriangles.Add(sTriangles[i + 1] + vertexBase);
                    cTriangles.Add(sTriangles[i] + vertexBase);
                }
            }
            else
            {
                for (int i = 0; i < tcnt; i++)
                    cTriangles.Add(sTriangles[i] + vertexBase);
            }
        }

        void CloseRun(ref Run run)
        {
            if (run == null)
                return;

            run.mesh.Clear();
            if (cVerts.Count > 0)
            {
                run.mesh.SetVertices(cVerts);
                run.mesh.SetColors(cColors);
                run.mesh.SetUVs(0, cUV0);
                if (cHasUV1)
                    run.mesh.SetUVs(1, cUV1);
                run.mesh.SetTriangles(cTriangles, 0);
            }

            if (!Material.ReferenceEquals(run.material, run.meshRenderer.sharedMaterial))
                run.meshRenderer.sharedMaterial = run.material;

            _runs.Add(run);
            run = null;
        }

        void ReleaseRun(Run run)
        {
            run.mesh.Clear();
            run.material = null;
            run.firstSource = null;
            run.sortingOrder = -1;
            run.gameObject.SetActive(false);
            _runPool.Add(run);
        }

        public void Dispose()
        {
            int cnt = _sources.Count;
            for (int i = 0; i < cnt; i++)
            {
                NGraphics g = _sources[i];
                if (g.meshRenderer != null)
                    g.meshRenderer.forceRenderingOff = false;
            }
            _sources.Clear();
            _sourceVersions.Clear();
            _sourceMaterials.Clear();
            _watched.Clear();

            foreach (Run run in _runs)
                DestroyRun(run);
            foreach (Run run in _runPool)
                DestroyRun(run);
            _runs.Clear();
            _runPool.Clear();
        }

        void DestroyRun(Run run)
        {
            if (run.mesh != null)
            {
                if (Application.isPlaying)
                {
                    UnityEngine.Object.Destroy(run.mesh);
                    if (run.gameObject != null)
                        UnityEngine.Object.Destroy(run.gameObject);
                }
                else
                {
                    UnityEngine.Object.DestroyImmediate(run.mesh);
                    if (run.gameObject != null)
                        UnityEngine.Object.DestroyImmediate(run.gameObject);
                }
                run.mesh = null;
                run.gameObject = null;
            }
        }
    }
}
