using System.Collections.Generic;
using UnityEngine;

namespace FairyGUI
{
    /// <summary>
    /// Rebuilds axis-aligned quads from raw mesh data for the instanced UI stream.
    ///
    /// Works on consecutive triangle pairs, so it handles both mesh topologies FairyGUI
    /// produces: independent 4-vertex quads (images, glyphs — indices (0,1,2)(2,3,0))
    /// and shared-vertex grids (scale9 = 16 vertices, 9 quads sharing corners). A pair
    /// that does not cover exactly 4 distinct vertices, or whose transformed bounds are
    /// degenerate, is skipped and counted — callers use the skip count to route the
    /// element to the mesh fallback path.
    ///
    /// Pure over lists (no Mesh dependency) so synthetic-data validation can drive it
    /// directly.
    /// </summary>
    public static class QuadReassembler
    {
        static readonly int[] sIndices = new int[4];

        /// <summary>
        /// Appends reassembled quads to output; returns the number appended.
        /// skippedPairs reports triangle pairs that were not quads.
        /// </summary>
        public static int Append(List<QuadInstance> output,
            List<Vector3> vertices, List<Vector2> uvs, List<Color32> colors, List<int> triangles,
            Matrix4x4 toLocal, Vector2 offset, uint flags, out int skippedPairs)
        {
            int appended = 0;
            skippedPairs = 0;
            //a triangle count that is not a multiple of 6 cannot be all quads
            //(e.g. a triangle fan's odd tail) — count it so callers fall back
            if (triangles.Count % 6 != 0)
                skippedPairs++;
            bool hasColors = colors != null && colors.Count >= vertices.Count;

            for (int t = 0; t + 5 < triangles.Count; t += 6)
            {
                int distinct = 0;
                for (int k = 0; k < 6; k++)
                {
                    int idx = triangles[t + k];
                    bool seen = false;
                    for (int d = 0; d < distinct; d++)
                    {
                        if (sIndices[d] == idx)
                        {
                            seen = true;
                            break;
                        }
                    }
                    if (!seen)
                    {
                        if (distinct == 4)
                        {
                            distinct = 5;
                            break;
                        }
                        sIndices[distinct++] = idx;
                    }
                }
                if (distinct != 4)
                {
                    skippedPairs++;
                    continue;
                }

                Vector2 p0 = toLocal.MultiplyPoint3x4(vertices[sIndices[0]]);
                Vector2 p1 = toLocal.MultiplyPoint3x4(vertices[sIndices[1]]);
                Vector2 p2 = toLocal.MultiplyPoint3x4(vertices[sIndices[2]]);
                Vector2 p3 = toLocal.MultiplyPoint3x4(vertices[sIndices[3]]);

                Vector2 min = Vector2.Min(Vector2.Min(p0, p1), Vector2.Min(p2, p3));
                Vector2 max = Vector2.Max(Vector2.Max(p0, p1), Vector2.Max(p2, p3));
                Vector2 size = max - min;
                if (size.x <= 0 || size.y <= 0)
                {
                    skippedPairs++;
                    continue;
                }

                //assign each source vertex's UV to the bounds corner its position
                //sits on (rotated atlas sprites keep correct sampling) — and REQUIRE
                //that the four points each cover a distinct corner: triangle-fan
                //pairs (arbitrary polygons) share the 4-distinct-vertex signature of
                //real quads but their points do not form an axis-aligned rectangle,
                //and non-axis-aligned (arbitrarily rotated) quads cannot be encoded
                //as an instance rect either; both must fall back to the native path
                float epsX = size.x * 0.001f + 0.001f;
                float epsY = size.y * 0.001f + 0.001f;
                Vector2 uv00 = default, uv10 = default, uv01 = default, uv11 = default;
                int cornerMask = 0;
                for (int k = 0; k < 4; k++)
                {
                    Vector2 p = k == 0 ? p0 : k == 1 ? p1 : k == 2 ? p2 : p3;
                    bool xMin = p.x - min.x <= epsX, xMax = max.x - p.x <= epsX;
                    bool yMin = p.y - min.y <= epsY, yMax = max.y - p.y <= epsY;
                    if (!(xMin || xMax) || !(yMin || yMax))
                    {
                        cornerMask = -1;
                        break;
                    }
                    int corner = (xMax ? 1 : 0) | (yMax ? 2 : 0);
                    if ((cornerMask & (1 << corner)) != 0)
                    {
                        cornerMask = -1;
                        break;
                    }
                    cornerMask |= 1 << corner;
                    Vector2 uv = uvs[sIndices[k]];
                    if (corner == 0) uv00 = uv;
                    else if (corner == 1) uv10 = uv;
                    else if (corner == 2) uv01 = uv;
                    else uv11 = uv;
                }
                if (cornerMask != 15)
                {
                    skippedPairs++;
                    continue;
                }

                output.Add(new QuadInstance
                {
                    rect = new Vector4(min.x + offset.x, min.y + offset.y, size.x, size.y),
                    uvA = new Vector4(uv00.x, uv00.y, uv10.x, uv10.y),
                    uvB = new Vector4(uv01.x, uv01.y, uv11.x, uv11.y),
                    color = hasColors ? (Color)colors[sIndices[0]] : Color.white,
                    flags = flags
                });
                appended++;
            }

            return appended;
        }
    }
}
