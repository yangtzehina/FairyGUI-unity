using System.Runtime.InteropServices;
using UnityEngine;

namespace FairyGUI
{
    /// <summary>
    /// One internal clip region of the instanced UI stream (32 bytes; must match the
    /// struct in FairyGUI-InstancedUI.shader). Instances reference an entry via
    /// QuadInstance.clipIndex; entry 0 is the "no clip" sentinel (infinite rect).
    ///
    /// rect is (xMin, yMin, xMax, yMax) in the stream's container-local space at
    /// scroll 0 — internal clip windows scroll with the content, so the fragment test
    /// uses the unscrolled position. Nested rect clips are folded by intersection at
    /// extract time (same semantics as UpdateContext.EnterClipping). The stream's own
    /// external window (a ScrollPane mask around the root) stays a uniform because it
    /// does NOT scroll with content and cannot be folded with these.
    ///
    /// soft is (softMinX, softMinY, softMaxX, softMaxY) in pixels: alpha ramps from 0
    /// at the edge to 1 at that distance inside (FairyGUI clipSoftness semantics).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ClipEntry
    {
        public Vector4 rect;
        public Vector4 soft;

        public const int Stride = 32;

        public static ClipEntry None
        {
            get
            {
                return new ClipEntry
                {
                    rect = new Vector4(-1e30f, -1e30f, 1e30f, 1e30f),
                    soft = Vector4.zero
                };
            }
        }
    }
}
