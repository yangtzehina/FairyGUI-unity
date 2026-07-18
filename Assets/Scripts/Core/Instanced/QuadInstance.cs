using System.Runtime.InteropServices;
using UnityEngine;

namespace FairyGUI
{
    /// <summary>
    /// One GPU-resident quad of the instanced UI stream (80 bytes, 16-byte aligned;
    /// must match the struct in InstancedUI.shader).
    ///
    /// rect is the container-local min corner + size. The four corner UVs are stored
    /// explicitly (uvA = corners (0,0),(1,0); uvB = corners (0,1),(1,1)) so rotated
    /// atlas sprites sample correctly. color carries the vertex color with baked
    /// opacity. transformIndex and clipIndex are reserved for the v4 transform-slot
    /// and clip-buffer milestones; in M1 both are 0. flags bit 0 selects alpha-only
    /// texture sampling (dynamic font atlases).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct QuadInstance
    {
        public Vector4 rect;
        public Vector4 uvA;
        public Vector4 uvB;
        public Color color;
        public uint transformIndex;
        public uint clipIndex;
        public uint flags;
        public uint padding;

        public const int Stride = 80;
        public const uint FlagAlphaTexture = 1u;

        //M7 SDF primitives (GPUI-inspired): the quad's coverage is computed
        //analytically in the fragment shader instead of sampled from a mesh
        //silhouette. Corner radii live in padding as 4 bytes (quadrant order
        //bottomLeft|bottomRight|topLeft|topRight in Unity-local space, 0-255 px);
        //the border width lives in flags bits 8-15 (0-255 px). Fill coverage is
        //inset by the border width, the border band spans [edge-width, edge] —
        //matching RoundedRectMesh's triangulated geometry.
        public const uint FlagSdfFill = 1u << 1;
        public const uint FlagSdfBorder = 1u << 2;

        public static uint PackRadii(float bottomLeft, float bottomRight, float topLeft, float topRight)
        {
            uint bl = (uint)Mathf.Clamp(Mathf.RoundToInt(bottomLeft), 0, 255);
            uint br = (uint)Mathf.Clamp(Mathf.RoundToInt(bottomRight), 0, 255);
            uint tl = (uint)Mathf.Clamp(Mathf.RoundToInt(topLeft), 0, 255);
            uint tr = (uint)Mathf.Clamp(Mathf.RoundToInt(topRight), 0, 255);
            return bl | (br << 8) | (tl << 16) | (tr << 24);
        }

        public static uint PackBorderWidth(uint flags, float width)
        {
            uint w = (uint)Mathf.Clamp(Mathf.RoundToInt(width), 0, 255);
            return (flags & ~0xFF00u) | (w << 8);
        }
    }
}
