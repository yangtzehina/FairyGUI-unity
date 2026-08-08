#if UNITY_2020_1_OR_NEWER
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using FairyGUI;
using UnityEngine;

/// <summary>
/// M1 quad-reassembler synthetics (19 checks), rebuilt from commit f21e803's
/// record: "canonical + alternate index patterns, 16-vertex scale9 grid -> 9
/// quads, rotated-UV corner mapping, 90-degree rotation matrix,
/// collapsed/zero-area pairs, missing colors, offset, flags, stride".
///
/// QuadReassembler is pure over lists (no Mesh dependency) precisely so it can
/// be driven with synthetic data — this suite needs no scene, no stream and no
/// pixels, which makes it the bottom of the validation stack: if quad
/// reconstruction is wrong, every higher suite is green for nothing.
///
/// Returns a "RESULT pass=N fail=N" report.
/// </summary>
public static class InstancedReassemblerSuite
{
    //--- synthetic mesh builders --------------------------------------------

    /// <summary>One quad: 4 vertices in (min, minY..) order with corner UVs.</summary>
    static void AddQuad(List<Vector3> v, List<Vector2> uv, List<Color32> c,
        float x0, float y0, float x1, float y1, Color32 col)
    {
        int b = v.Count;
        v.Add(new Vector3(x0, y0)); //bottom-left
        v.Add(new Vector3(x1, y0)); //bottom-right
        v.Add(new Vector3(x1, y1)); //top-right
        v.Add(new Vector3(x0, y1)); //top-left
        uv.Add(new Vector2(0, 0));
        uv.Add(new Vector2(1, 0));
        uv.Add(new Vector2(1, 1));
        uv.Add(new Vector2(0, 1));
        for (int i = 0; i < 4; i++) c.Add(col);
        _ = b;
    }

    static List<int> Tris(params int[] idx) { return new List<int>(idx); }

    static int Run1(List<Vector3> v, List<Vector2> uv, List<Color32> c, List<int> t,
        out List<QuadInstance> outp, out int skipped, Matrix4x4? m = null,
        Vector2 offset = default, uint flags = 0)
    {
        outp = new List<QuadInstance>();
        return QuadReassembler.Append(outp, v, uv, c, t, m ?? Matrix4x4.identity,
            offset, flags, out skipped);
    }

    static bool RectIs(QuadInstance q, float x, float y, float w, float h, float eps = 1e-4f)
    {
        return Mathf.Abs(q.rect.x - x) < eps && Mathf.Abs(q.rect.y - y) < eps
            && Mathf.Abs(q.rect.z - w) < eps && Mathf.Abs(q.rect.w - h) < eps;
    }

    public static string Run()
    {
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok)
        {
            if (ok) pass++; else fail++;
            sb.Append(ok ? "PASS " : "FAIL ").Append(name).Append('\n');
        }

        List<QuadInstance> o;
        int skipped;

        //--- q1/q2: the canonical pattern (0,1,2)(2,3,0) ---------------------
        {
            var v = new List<Vector3>(); var uv = new List<Vector2>(); var c = new List<Color32>();
            AddQuad(v, uv, c, 10, 20, 60, 50, new Color32(255, 0, 0, 255));
            int n = Run1(v, uv, c, Tris(0, 1, 2, 2, 3, 0), out o, out skipped);
            Check($"q1.canonical indices -> 1 quad, exact rect (n={n} skipped={skipped})",
                n == 1 && skipped == 0 && RectIs(o[0], 10, 20, 50, 30));
            Check("q2.corner UVs land in uvA/uvB by position",
                o.Count == 1
                && o[0].uvA == new Vector4(0, 0, 1, 0)   //(min,min) (max,min)
                && o[0].uvB == new Vector4(0, 1, 1, 1)); //(min,max) (max,max)
        }

        //--- q3/q4: alternate index patterns ---------------------------------
        {
            var v = new List<Vector3>(); var uv = new List<Vector2>(); var c = new List<Color32>();
            AddQuad(v, uv, c, 0, 0, 40, 30, Color.white);
            int n1 = Run1(v, uv, c, Tris(0, 1, 2, 2, 1, 3), out o, out skipped);
            bool a = n1 == 1 && skipped == 0 && RectIs(o[0], 0, 0, 40, 30);
            int n2 = Run1(v, uv, c, Tris(0, 2, 1, 1, 2, 3), out o, out skipped);
            bool b = n2 == 1 && skipped == 0 && RectIs(o[0], 0, 0, 40, 30);
            Check("q3.alternate index pattern (0,1,2)(2,1,3) reassembles", a);
            Check("q4.reversed winding still reassembles", b);
        }

        //--- q5: two independent quads keep order ----------------------------
        {
            var v = new List<Vector3>(); var uv = new List<Vector2>(); var c = new List<Color32>();
            AddQuad(v, uv, c, 0, 0, 10, 10, new Color32(255, 0, 0, 255));
            AddQuad(v, uv, c, 20, 0, 35, 12, new Color32(0, 255, 0, 255));
            int n = Run1(v, uv, c, Tris(0, 1, 2, 2, 3, 0, 4, 5, 6, 6, 7, 4), out o, out skipped);
            Check($"q5.two independent quads, order preserved (n={n})",
                n == 2 && skipped == 0
                && RectIs(o[0], 0, 0, 10, 10) && RectIs(o[1], 20, 0, 15, 12));
        }

        //--- q6/q7: the 16-vertex scale9 grid --------------------------------
        {
            var v = new List<Vector3>(); var uv = new List<Vector2>(); var c = new List<Color32>();
            float[] xs = { 0, 10, 50, 60 }, ys = { 0, 8, 40, 48 };
            for (int r = 0; r < 4; r++)
                for (int cc = 0; cc < 4; cc++)
                {
                    v.Add(new Vector3(xs[cc], ys[r]));
                    uv.Add(new Vector2(xs[cc] / 60f, ys[r] / 48f));
                    c.Add(new Color32(200, 200, 200, 255));
                }
            var t = new List<int>();
            for (int r = 0; r < 3; r++)
                for (int cc = 0; cc < 3; cc++)
                {
                    int i0 = r * 4 + cc, i1 = i0 + 1, i2 = i0 + 4, i3 = i0 + 5;
                    t.Add(i0); t.Add(i1); t.Add(i2);
                    t.Add(i2); t.Add(i1); t.Add(i3);
                }
            int n = Run1(v, uv, c, t, out o, out skipped);
            Check($"q6.16-vertex scale9 grid -> 9 quads (n={n} skipped={skipped})",
                n == 9 && skipped == 0);
            Check("q7.scale9 corner and center cells have exact rects",
                n == 9 && RectIs(o[0], 0, 0, 10, 8)      //bottom-left cell
                && RectIs(o[4], 10, 8, 40, 32)           //center cell
                && RectIs(o[8], 50, 40, 10, 8));         //top-right cell
        }

        //--- q8: UVs follow POSITION, not vertex order (rotated atlas) -------
        {
            var v = new List<Vector3>(); var uv = new List<Vector2>(); var c = new List<Color32>();
            //same 4 corners, but the uv assignment is rotated by one step
            v.Add(new Vector3(0, 0)); uv.Add(new Vector2(0, 1));
            v.Add(new Vector3(20, 0)); uv.Add(new Vector2(0, 0));
            v.Add(new Vector3(20, 10)); uv.Add(new Vector2(1, 0));
            v.Add(new Vector3(0, 10)); uv.Add(new Vector2(1, 1));
            for (int i = 0; i < 4; i++) c.Add(Color.white);
            int n = Run1(v, uv, c, Tris(0, 1, 2, 2, 3, 0), out o, out skipped);
            Check("q8.rotated-UV mapping assigns by bounds corner",
                n == 1 && o[0].uvA == new Vector4(0, 1, 0, 0)
                && o[0].uvB == new Vector4(1, 1, 1, 0));
        }

        //--- q9: a 90-degree rotation stays axis-aligned ---------------------
        {
            var v = new List<Vector3>(); var uv = new List<Vector2>(); var c = new List<Color32>();
            AddQuad(v, uv, c, 0, 0, 40, 20, Color.white);
            Matrix4x4 rot = Matrix4x4.Rotate(Quaternion.Euler(0, 0, 90));
            int n = Run1(v, uv, c, Tris(0, 1, 2, 2, 3, 0), out o, out skipped, rot);
            Check($"q9.90-degree rotation matrix still reassembles (n={n})",
                n == 1 && skipped == 0 && RectIs(o[0], -20, 0, 20, 40, 1e-3f));
        }

        //--- q10: an arbitrary rotation cannot be encoded --------------------
        {
            var v = new List<Vector3>(); var uv = new List<Vector2>(); var c = new List<Color32>();
            AddQuad(v, uv, c, 0, 0, 40, 20, Color.white);
            Matrix4x4 rot = Matrix4x4.Rotate(Quaternion.Euler(0, 0, 45));
            int n = Run1(v, uv, c, Tris(0, 1, 2, 2, 3, 0), out o, out skipped, rot);
            Check($"q10.45-degree rotation is skipped, not mis-encoded (n={n} skipped={skipped})",
                n == 0 && skipped == 1);
        }

        //--- q11/q12: degenerate pairs ---------------------------------------
        {
            var v = new List<Vector3>(); var uv = new List<Vector2>(); var c = new List<Color32>();
            AddQuad(v, uv, c, 5, 5, 5, 25, Color.white);   //zero width
            AddQuad(v, uv, c, 0, 0, 0, 0, Color.white);    //zero area
            int n = Run1(v, uv, c, Tris(0, 1, 2, 2, 3, 0, 4, 5, 6, 6, 7, 4), out o, out skipped);
            Check($"q11.collapsed (zero-width) pair skipped and counted (skipped={skipped})",
                n == 0 && skipped == 2);
            Check("q12.zero-area pair produces no quad", n == 0);
        }

        //--- q13: triangle-fan false positive --------------------------------
        {
            //4 distinct vertices that do NOT form an axis-aligned rectangle
            var v = new List<Vector3> {
                new Vector3(0, 0), new Vector3(20, 0), new Vector3(30, 15), new Vector3(10, 25) };
            var uv = new List<Vector2> {
                Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
            var c = new List<Color32> { Color.white, Color.white, Color.white, Color.white };
            int n = Run1(v, uv, c, Tris(0, 1, 2, 2, 3, 0), out o, out skipped);
            Check($"q13.fan-topology false positive rejected (n={n} skipped={skipped})",
                n == 0 && skipped == 1);
        }

        //--- q14: an index count that is not a multiple of 6 -----------------
        {
            var v = new List<Vector3>(); var uv = new List<Vector2>(); var c = new List<Color32>();
            AddQuad(v, uv, c, 0, 0, 10, 10, Color.white);
            var t = Tris(0, 1, 2, 2, 3, 0, 0, 1, 2); //quad + odd tail triangle
            int n = Run1(v, uv, c, t, out o, out skipped);
            Check($"q14.non-multiple-of-6 index tail is counted as skipped (n={n} skipped={skipped})",
                n == 1 && skipped == 1);
        }

        //--- q15/q16: colors --------------------------------------------------
        {
            var v = new List<Vector3>(); var uv = new List<Vector2>(); var c = new List<Color32>();
            AddQuad(v, uv, c, 0, 0, 10, 10, new Color32(10, 20, 30, 40));
            int n = Run1(v, uv, c, Tris(0, 1, 2, 2, 3, 0), out o, out skipped);
            bool fromFirst = n == 1
                && Mathf.Abs(o[0].color.r - 10 / 255f) < 0.01f
                && Mathf.Abs(o[0].color.a - 40 / 255f) < 0.01f;
            int n2 = Run1(v, uv, null, Tris(0, 1, 2, 2, 3, 0), out o, out skipped);
            bool whiteFallback = n2 == 1 && o[0].color == Color.white;
            var shortColors = new List<Color32> { Color.red };
            int n3 = Run1(v, uv, shortColors, Tris(0, 1, 2, 2, 3, 0), out o, out skipped);
            bool shortFallback = n3 == 1 && o[0].color == Color.white;
            Check("q15.missing or short color list falls back to white",
                whiteFallback && shortFallback);
            Check("q16.uniform vertex color propagates to the instance", fromFirst);
        }

        //--- q18/q19: non-uniform colors reject (flatten regression) ---------
        //an instance carries ONE color: gradient pairs used to flatten to
        //whichever vertex the index order put first — now they skip so the
        //leaf falls back to its native renderer
        {
            var v = new List<Vector3>(); var uv = new List<Vector2>(); var c = new List<Color32>();
            AddQuad(v, uv, c, 0, 0, 10, 10, Color.white);
            c[1] = Color.red; //one disagreeing corner is enough
            int n = Run1(v, uv, c, Tris(0, 1, 2, 2, 3, 0), out o, out skipped);
            Check($"q18.gradient pair skips instead of flattening (n={n} skipped={skipped})",
                n == 0 && skipped == 1);

            v.Clear(); uv.Clear(); c.Clear();
            AddQuad(v, uv, c, 0, 0, 10, 10, new Color32(1, 2, 3, 4)); //uniform
            AddQuad(v, uv, c, 20, 0, 30, 10, Color.white);            //gradient
            c[5] = Color.blue;
            int n2 = Run1(v, uv, c, Tris(0, 1, 2, 2, 3, 0, 4, 5, 6, 6, 7, 4), out o, out skipped);
            Check($"q19.mixed mesh keeps the uniform quad, skips the gradient one (n={n2} skipped={skipped})",
                n2 == 1 && skipped == 1
                && Mathf.Abs(o[0].color.r - 1 / 255f) < 0.01f
                && Mathf.Abs(o[0].color.a - 4 / 255f) < 0.01f);
        }

        //--- q17: offset, flags and the frozen GPU stride --------------------
        {
            var v = new List<Vector3>(); var uv = new List<Vector2>(); var c = new List<Color32>();
            AddQuad(v, uv, c, 5, 7, 15, 27, Color.white);
            int n = Run1(v, uv, c, Tris(0, 1, 2, 2, 3, 0), out o, out skipped,
                null, new Vector2(100, -50), QuadInstance.FlagAlphaTexture);
            Check($"q17.drawOffset shifts the rect, flags propagate, stride is {QuadInstance.Stride}B",
                n == 1 && RectIs(o[0], 105, -43, 10, 20)
                && o[0].flags == QuadInstance.FlagAlphaTexture
                && QuadInstance.Stride == 80
                && Marshal.SizeOf(typeof(QuadInstance)) == QuadInstance.Stride);
        }

        sb.Insert(0, $"RESULT pass={pass} fail={fail}\n");
        return sb.ToString();
    }
}
#endif
