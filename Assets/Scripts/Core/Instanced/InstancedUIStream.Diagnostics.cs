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
    /// Diagnostics face of the stream: counters, backend identity and quad/clip
    /// peeks the validation suites and the Instanced UI Streams panel read.
    /// Everything here is a read-only view over core state.
    /// </summary>
    public partial class InstancedUIStream
    {
        public int segmentCount { get { return _segments.Count; } }

        /// <summary>The stream's root container (diagnostics).</summary>
        public Container container { get { return _container; } }

        /// <summary>Which backend this stream compiled against (diagnostics).</summary>
        public string backendName { get { return _vertexPath ? "vertex-stream" : "buffer"; } }

        /// <summary>Leaves currently claimed away from native rendering (diagnostics).</summary>
        public int claimedLeafCount { get { return _claimed.Count; } }

        /// <summary>Validation/diagnostics probe: full recompiles since construction.</summary>
        public int extractCount { get; private set; }

        /// <summary>Transform slots currently assigned (hot interior containers).</summary>
        public int slotCount { get { return _slotIndices.Count; } }

        /// <summary>Hot containers that could not get a slot in the last Extract (>15).</summary>
        public int slotOverflow { get; private set; }

        /// <summary>Diagnostics/CI probe: compiled leaf ranges.</summary>
        public int leafCount { get { return _leaves.Count; } }

        //--- vertex-backend upload width (diagnostics) ----------------------
        //The vertex path pays 4 vertices per quad, so this width IS its upload
        //bandwidth. Exposed because QuadVertex is internal and a validation
        //gate in another assembly has to be able to pin the number: a field
        //widened by 4 bytes costs every WebGL frame silently while the pixel
        //gates stay green.
        public static int vertexUploadStride { get { return QuadVertex.Stride; } }

        /// <summary>
        /// Round-trips one instance through the vertex writer and rebuilds the
        /// four packed bytes the way the shader does. Exposed for validation:
        /// under FlagCurveGlyph those bytes are a glyph index, not radii, so
        /// the packing has to be lossless in a way plain radii would forgive.
        /// </summary>
        public static uint PackedRadiiForDiagnostics(in QuadInstance q)
        {
            var v = new QuadVertex[4];
            QuadVertex.WriteQuad(v, 0, in q);
            return v[0].radBL | ((uint)v[0].radBR << 8)
                 | ((uint)v[0].radTL << 16) | ((uint)v[0].radTR << 24);
        }

        /// <summary>Diagnostics/CI probe (M8-6 parity): copies the compiled quad
        /// stream out for comparison harnesses.</summary>
        public void CopyQuadsForDiagnostics(List<QuadInstance> into)
        {
            into.Clear();
            into.AddRange(_quads);
        }

        public static int vertexUploadStructSize
        {
            get { return System.Runtime.InteropServices.Marshal.SizeOf<QuadVertex>(); }
        }

        /// <summary>
        /// The stride Unity ACTUALLY allocated for this stream's segment
        /// meshes, read back from the mesh. The constants above are what we
        /// declared; this is what the GPU got. -1 when no segment mesh exists
        /// (buffer backend, or nothing compiled yet).
        /// </summary>
        public int measuredVertexStride
        {
            get
            {
                for (int i = 0; i < _segments.Count; i++)
                {
                    Mesh m = _segments[i].mesh;
                    if (m != null && m.vertexCount > 0)
                        return m.GetVertexBufferStride(0);
                }
                return -1;
            }
        }

        /// <summary>Bytes the declared attribute layout actually describes —
        /// must agree with both of the above or the GPU reads shifted fields.</summary>
        public static int vertexUploadLayoutSize
        {
            get
            {
                int n = 0;
                var layout = QuadVertex.Layout;
                for (int i = 0; i < layout.Length; i++)
                {
                    var f = layout[i].format;
                    n += layout[i].dimension * (f == VertexAttributeFormat.Float32 ? 4
                        : f == VertexAttributeFormat.Float16 || f == VertexAttributeFormat.UNorm16 ? 2 : 1);
                }
                return n;
            }
        }

        public int quadCount { get { return _quads.Count; } }

        /// <summary>
        /// Submission runs delimited by fallback barriers (segments of one run share
        /// a sortingOrder slot; native fallback renderers draw between runs).
        /// </summary>
        public int runCount { get { return _runBarriers.Count + 1; } }

        /// <summary>
        /// Internal clip regions found by the last Extract, including the entry-0
        /// "no clip" sentinel.
        /// </summary>
        public int clipEntryCount { get { return _clipEntries.Count; } }

        /// <summary>
        /// Triangle pairs that could not be reassembled as quads in the last Extract —
        /// content for the mesh fallback path (M5).
        /// </summary>
        public int lastSkippedPairs { get { return _skippedPairs; } }

        /// <summary>
        /// Stencil-masked subtrees skipped by the last Extract — content for the
        /// fallback scope path (M5).
        /// </summary>
        public int lastMaskedSubtrees { get { return _maskedSubtrees; } }

        /// <summary>Validation probe: the clip entry index a leaf was stamped with.</summary>
        public uint GetLeafClipIndex(NGraphics graphics)
        {
            return _leafLookup.TryGetValue(graphics, out LeafRange r) ? r.clipIndex : 0;
        }

        /// <summary>Validation probe: a clip entry by index.</summary>
        public ClipEntry GetClipEntry(int index)
        {
            return _clipEntries[index];
        }

        /// <summary>Validation probe: whether a leaf is currently claimed (in-place).</summary>
        /// <summary>
        /// Approximate resident bytes of this stream: GPU instance/clip
        /// buffers (buffer path), segment + pull meshes (vertex path, 4
        /// QuadVertex per quad), and the managed quad/staging lists. The
        /// audit's finding was a panel with counts and no bytes — capacities
        /// here only ever grow, so this is the number to watch across long
        /// window-churn sessions.
        /// </summary>
        public long approxResidentBytes
        {
            get
            {
                long n = 0;
                if (_buffer != null)
                    n += (long)_bufferCapacity * 80;
                if (_clipBuffer != null)
                    n += (long)_clipBufferCapacity * 32;
                if (_vertexPath)
                {
                    for (int i = 0; i < _segments.Count; i++)
                        n += (long)_segments[i].count * 4 * QuadVertex.Stride;
                }
                n += (long)(_pullCapacity > 0 ? _pullCapacity : 0) * 4 * QuadVertex.Stride;
                n += ((long)_quads.Capacity + _staging.Capacity) * 80;
                return n;
            }
        }

        public bool IsClaimed(NGraphics graphics)
        {
            return _claimed.Contains(graphics);
        }
    }
}
