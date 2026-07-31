#if UNITY_2020_1_OR_NEWER
using System;
using System.Collections.Generic;
using System.Text;
using FairyGUI;
using UnityEngine;

/// <summary>
/// Shared harness for the instanced-renderer validation suites (M4 scenarios,
/// batches 1-4). The suites were originally session-local eval scripts; they are
/// rebuilt here from the git-log Validation records and
/// docs/design/batch3-incremental.md so they live in the repo.
///
/// The environment pins the backend (see <see cref="useVertexBackend"/>) and
/// drives frames synchronously with Stage.ForceUpdate, so a whole suite runs
/// inside one eval call. Pixel probes render StageCamera into a RenderTexture
/// with a solid clear override (the stage camera itself only clears depth) and
/// sample in stage coordinates via GObject.LocalToGlobal.
/// </summary>
public class InstancedValidationEnv : IDisposable
{
    public static readonly Color32 BG = new Color32(20, 20, 20, 255);

    /// <summary>
    /// Which backend the suites compile against. Default is the vertex-stream
    /// path: it renders everywhere, and this machine's editor historically drew
    /// NOTHING on the buffer path (vertex-stage StructuredBuffer — design doc
    /// §11), which is why every suite was written against it.
    ///
    /// That quirk did not reproduce on a freshly launched editor on 2026-07-31
    /// (verified by a control: disabling the segment renderers removed the
    /// pixels, so they really came from the instanced draw), so the suites can
    /// now be run against the buffer path too — set this to false, or use the
    /// runner entry points that sweep both. Keep the default as-is: when the
    /// quirk resurfaces, the whole suite must still be runnable.
    /// </summary>
    public static bool useVertexBackend = true;

    /// <summary>The backend name the current setting is expected to produce.</summary>
    public static string expectedBackend
    {
        get { return useVertexBackend ? "vertex-stream" : "buffer"; }
    }

    public readonly GComponent root;
    public readonly GGraph bg;
    readonly bool _prevForce;
    readonly StringBuilder _sb = new StringBuilder();
    int _pass, _fail;
    RenderTexture _rt;
    Texture2D _readback;
    readonly List<GObject> _extras = new List<GObject>(); //disposed with the env
    readonly List<Texture2D> _ownedTextures = new List<Texture2D>();

    public int W { get; private set; }
    public int H { get; private set; }

    public InstancedValidationEnv(float width = 620, float height = 440)
    {
        _prevForce = InstancedUIStream.forceVertexPath;
        InstancedUIStream.forceVertexPath = useVertexBackend;
        if (string.IsNullOrEmpty(UIConfig.defaultFont))
            UIConfig.defaultFont = "Arial";
        root = new GComponent();
        root.SetSize(width, height);
        GRoot.inst.AddChild(root);
        root.SetXY(10, 10);
        bg = new GGraph();
        bg.DrawRect(width, height, 0, Color.clear, BG);
        root.AddChild(bg);
        bg.SetXY(0, 0);
        Step(2);
    }

    //---- report -------------------------------------------------------------

    public void Check(string name, bool ok)
    {
        if (ok) _pass++; else _fail++;
        _sb.Append(ok ? "PASS " : "FAIL ").Append(name).Append('\n');
    }

    public void Note(string line)
    {
        _sb.Append("     ").Append(line).Append('\n');
    }

    public string Report()
    {
        return $"RESULT pass={_pass} fail={_fail}\n" + _sb;
    }

    //---- frame driving ------------------------------------------------------

    /// <summary>One synchronous frame: full stage update walk + stream RenderAll.</summary>
    public void Step(int n = 1)
    {
        for (int i = 0; i < n; i++)
            Stage.inst.ForceUpdate();
    }

    //---- UI builders --------------------------------------------------------

    public GGraph Rect(GComponent parent, float x, float y, float w, float h, Color fill)
    {
        var g = new GGraph();
        g.DrawRect(w, h, 0, Color.clear, fill);
        parent.AddChild(g);
        g.SetXY(x, y);
        return g;
    }

    public GTextField Text(GComponent parent, float x, float y, float w, float h, string text, int size = 24)
    {
        var tf = new GTextField();
        tf.SetSize(w, h);
        tf.autoSize = AutoSizeType.None;
        TextFormat fmt = tf.textFormat;
        fmt.size = size;
        fmt.color = Color.white;
        tf.textFormat = fmt;
        tf.text = text;
        parent.AddChild(tf);
        tf.SetXY(x, y);
        return tf;
    }

    public GGraph ImageLeaf(GComponent parent, float x, float y, Color32 solid, out Image image, int texSize = 32)
    {
        var tex = new Texture2D(texSize, texSize, TextureFormat.RGBA32, false);
        var px = new Color32[texSize * texSize];
        for (int i = 0; i < px.Length; i++)
            px[i] = solid;
        tex.SetPixels32(px);
        tex.Apply(false, false);
        tex.hideFlags = HideFlags.DontSave;
        _ownedTextures.Add(tex);
        image = new Image(new NTexture(tex));
        var holder = new GGraph();
        holder.SetNativeObject(image);
        parent.AddChild(holder);
        holder.SetXY(x, y);
        return holder;
    }

    /// <summary>A sibling component directly on GRoot (outside the env root). Disposed with the env.</summary>
    public GComponent Sibling(float x, float y, float w, float h)
    {
        var c = new GComponent();
        c.SetSize(w, h);
        GRoot.inst.AddChild(c);
        c.SetXY(x, y);
        _extras.Add(c);
        return c;
    }

    /// <summary>
    /// Requests every glyph of s from the dynamic font once, so later text
    /// mutations cannot trigger a font-atlas resize (a watched-texture
    /// onSizeChanged would recompile streams and break extractCount asserts).
    /// </summary>
    public void WarmGlyphs(string s)
    {
        var tf = Text(GRoot.inst, -2000, -2000, 600, 60, s);
        Step(2);
        tf.Dispose();
        Step(1);
    }

    public static NGraphics G(GObject o)
    {
        return o.displayObject.graphics;
    }

    public static Container C(GComponent c)
    {
        return (Container)c.displayObject;
    }

    /// <summary>Active segment renderer GameObject ids under a container (sorted).</summary>
    public static List<int> SegmentIds(Container c)
    {
        var ids = new List<int>();
        Transform t = c.cachedTransform;
        for (int i = 0; i < t.childCount; i++)
        {
            Transform ch = t.GetChild(i);
            if (ch.name == "InstancedUISegment" && ch.gameObject.activeSelf)
                ids.Add(ch.gameObject.GetInstanceID());
        }
        ids.Sort();
        return ids;
    }

    public static InstancedUIStream StreamOf(GComponent comp)
    {
        var list = new List<InstancedUIStream>();
        InstancedUIStream.GetLiveStreams(list);
        Container c = (Container)comp.displayObject;
        foreach (var s in list)
        {
            //the wrapper may target an inner clip-owner container
            for (Container p = s.container; p != null; p = p.parent)
            {
                if (p == c)
                    return s;
            }
        }
        return null;
    }

    public static int LiveStreamCount()
    {
        var list = new List<InstancedUIStream>();
        InstancedUIStream.GetLiveStreams(list);
        return list.Count;
    }

    //---- pixel probes -------------------------------------------------------

    /// <summary>Renders the stage camera into an RT with a solid black clear and reads it back.</summary>
    public Color32[] Capture()
    {
        Camera cam = StageCamera.main;
        W = Screen.width;
        H = Screen.height;
        if (_rt == null || _rt.width != W || _rt.height != H)
        {
            if (_rt != null)
                _rt.Release();
            _rt = new RenderTexture(W, H, 24);
            _readback = new Texture2D(W, H, TextureFormat.RGB24, false);
        }
        var prevTarget = cam.targetTexture;
        var prevFlags = cam.clearFlags;
        var prevColor = cam.backgroundColor;
        cam.targetTexture = _rt;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = Color.black;
        cam.Render();
        cam.targetTexture = prevTarget;
        cam.clearFlags = prevFlags;
        cam.backgroundColor = prevColor;
        var prevActive = RenderTexture.active;
        RenderTexture.active = _rt;
        _readback.ReadPixels(new UnityEngine.Rect(0, 0, W, H), 0, 0);
        _readback.Apply();
        RenderTexture.active = prevActive;
        return _readback.GetPixels32();
    }

    public Color32 ProbeStage(Color32[] px, Vector2 stagePt)
    {
        int x = Mathf.Clamp(Mathf.RoundToInt(stagePt.x), 0, W - 1);
        int y = Mathf.Clamp(Mathf.RoundToInt(stagePt.y), 0, H - 1);
        return px[(H - 1 - y) * W + x];
    }

    /// <summary>Probe a point given in obj's local (y-down) coordinates.</summary>
    public Color32 Probe(Color32[] px, GObject obj, float lx, float ly)
    {
        return ProbeStage(px, obj.LocalToGlobal(new Vector2(lx, ly)));
    }

    public static bool Near(Color32 c, Color32 e, int tol = 5)
    {
        return Mathf.Abs(c.r - e.r) <= tol && Mathf.Abs(c.g - e.g) <= tol && Mathf.Abs(c.b - e.b) <= tol;
    }

    public static bool NearRGB(Color32 c, int r, int g, int b, int tol = 5)
    {
        return Mathf.Abs(c.r - r) <= tol && Mathf.Abs(c.g - g) <= tol && Mathf.Abs(c.b - b) <= tol;
    }

    public static string Fmt(Color32 c)
    {
        return $"({c.r},{c.g},{c.b})";
    }

    //region probes transform the two corners ONCE and then index the capture
    //directly — a per-sample LocalToGlobal is a native camera call and turns a
    //region sweep into minutes. Assumes the local rect is axis-aligned on
    //stage, which every region probe here satisfies.
    void StageSpan(GObject obj, float x0, float y0, float x1, float y1,
        out int sx0, out int sy0, out int sx1, out int sy1)
    {
        Vector2 a = obj.LocalToGlobal(new Vector2(x0, y0));
        Vector2 b = obj.LocalToGlobal(new Vector2(x1, y1));
        sx0 = Mathf.Clamp(Mathf.RoundToInt(Mathf.Min(a.x, b.x)), 0, W - 1);
        sx1 = Mathf.Clamp(Mathf.RoundToInt(Mathf.Max(a.x, b.x)), 0, W - 1);
        sy0 = Mathf.Clamp(Mathf.RoundToInt(Mathf.Min(a.y, b.y)), 0, H - 1);
        sy1 = Mathf.Clamp(Mathf.RoundToInt(Mathf.Max(a.y, b.y)), 0, H - 1);
    }

    /// <summary>Any sampled pixel in the local-rect brighter than the threshold (glyph presence).</summary>
    public bool AnyBright(Color32[] px, GObject obj, float x0, float y0, float x1, float y1, int sumThreshold = 240)
    {
        int sx0, sy0, sx1, sy1;
        StageSpan(obj, x0, y0, x1, y1, out sx0, out sy0, out sx1, out sy1);
        for (int y = sy0; y <= sy1; y++)
            for (int x = sx0; x <= sx1; x++)
            {
                Color32 c = px[(H - 1 - y) * W + x];
                if (c.r + c.g + c.b > sumThreshold)
                    return true;
            }
        return false;
    }

    /// <summary>Every sampled pixel in the local-rect matches the expected color.</summary>
    public bool AllNear(Color32[] px, GObject obj, float x0, float y0, float x1, float y1, Color32 expected, int tol = 8)
    {
        int sx0, sy0, sx1, sy1;
        StageSpan(obj, x0, y0, x1, y1, out sx0, out sy0, out sx1, out sy1);
        for (int y = sy0; y <= sy1; y++)
            for (int x = sx0; x <= sx1; x++)
            {
                if (!Near(px[(H - 1 - y) * W + x], expected, tol))
                    return false;
            }
        return true;
    }

    /// <summary>Pixels differing (sum of channel deltas > 24) between two captures over a local rect.</summary>
    public int DiffCount(Color32[] a, Color32[] b, GObject obj, float x0, float y0, float x1, float y1)
    {
        int sx0, sy0, sx1, sy1;
        StageSpan(obj, x0, y0, x1, y1, out sx0, out sy0, out sx1, out sy1);
        int n = 0;
        for (int y = sy0; y <= sy1; y++)
            for (int x = sx0; x <= sx1; x++)
            {
                Color32 ca = a[(H - 1 - y) * W + x];
                Color32 cb = b[(H - 1 - y) * W + x];
                if (Mathf.Abs(ca.r - cb.r) + Mathf.Abs(ca.g - cb.g) + Mathf.Abs(ca.b - cb.b) > 24)
                    n++;
            }
        return n;
    }

    /// <summary>Mean channel-sum delta and bad-pixel percentage over a local rect (M6-style).</summary>
    public void DiffStats(Color32[] a, Color32[] b, GObject obj, float x0, float y0, float x1, float y1,
        out double mean, out double badPct)
    {
        int sx0, sy0, sx1, sy1;
        StageSpan(obj, x0, y0, x1, y1, out sx0, out sy0, out sx1, out sy1);
        long sum = 0;
        int bad = 0, count = 0;
        for (int y = sy0; y <= sy1; y++)
            for (int x = sx0; x <= sx1; x++)
            {
                Color32 ca = a[(H - 1 - y) * W + x];
                Color32 cb = b[(H - 1 - y) * W + x];
                int d = Mathf.Abs(ca.r - cb.r) + Mathf.Abs(ca.g - cb.g) + Mathf.Abs(ca.b - cb.b);
                sum += d;
                if (d > 24) bad++;
                count++;
            }
        mean = count > 0 ? (double)sum / count : 0;
        badPct = count > 0 ? 100.0 * bad / count : 0;
    }

    public void Dispose()
    {
        foreach (var e in _extras)
        {
            if (!e.isDisposed)
                e.Dispose();
        }
        _extras.Clear();
        if (!root.isDisposed)
            root.Dispose();
        InstancedUIStream.forceVertexPath = _prevForce;
        Step(1);
        if (_rt != null)
        {
            _rt.Release();
            UnityEngine.Object.Destroy(_rt);
            _rt = null;
        }
        if (_readback != null)
        {
            UnityEngine.Object.Destroy(_readback);
            _readback = null;
        }
        foreach (var t in _ownedTextures)
            UnityEngine.Object.Destroy(t);
        _ownedTextures.Clear();
    }
}
#endif
