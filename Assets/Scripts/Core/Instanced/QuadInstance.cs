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
    }
}
