#if UNITY_2020_1_OR_NEWER
using System.Text;
using FairyGUI;
using UnityEngine;

/// <summary>
/// M5 acceptance driver, two phases pixel-compared against native rendering:
///
/// 1. Interleave: an instanced background, a FALLBACK polygon (non-quad topology,
///    keeps its native renderer) on top of it, and an instanced rect on top of the
///    polygon — the fallback barrier must split the stream into two sortingOrder
///    runs so the native renderer draws BETWEEN them.
/// 2. Filter capture: a BlurFilter on the stream root puts the whole scope into
///    painting mode; the segment renderers must follow the SetChildrenLayer flip
///    so the CaptureCamera sees them and the main camera does not (review M12).
/// </summary>
[DefaultExecutionOrder(1000)]
public class M5Acceptance : MonoBehaviour
{
    public static void Run(string outputDir)
    {
        var go = new GameObject("M5Acceptance");
        var a = go.AddComponent<M5Acceptance>();
        a._dir = outputDir;
    }

    string _dir;
    GComponent _holder;
    GGraph _poly;
    InstancedUIStream _stream;
    int _frame;
    Color32[] _a1, _b1, _a2, _b2;
    readonly StringBuilder _log = new StringBuilder();

    void Start()
    {
        _holder = new GComponent();
        _holder.name = "m5holder";
        GRoot.inst.AddChild(_holder);
        _holder.SetSize(400, 400);
        _holder.SetXY(100, 50);

        var bg = new GGraph();
        bg.SetSize(300, 300);
        bg.DrawRect(300, 300, 0, Color.clear, new Color(0.2f, 0.7f, 0.7f));
        _holder.AddChild(bg);
        bg.SetXY(20, 20);

        //non-quad topology -> fallback barrier, natively rendered
        _poly = new GGraph();
        _poly.SetSize(120, 120);
        _poly.DrawPolygon(120, 120, new[] {
            new Vector2(0, 90), new Vector2(45, 0), new Vector2(90, 25),
            new Vector2(85, 75), new Vector2(25, 90) }, new Color(0.95f, 0.85f, 0.2f));
        _holder.AddChild(_poly);
        _poly.SetXY(60, 60);

        var top = new GGraph();
        top.SetSize(80, 60);
        top.DrawRect(80, 60, 0, Color.clear, new Color(0.85f, 0.25f, 0.25f));
        _holder.AddChild(top);
        top.SetXY(90, 90);
    }

    void LateUpdate()
    {
        _frame++;
        if (_stream != null)
            _stream.Render();
        switch (_frame)
        {
            case 3:
                _a1 = Capture("m5_native_sandwich");
                _stream = new InstancedUIStream((Container)_holder.displayObject, default, true, true);
                break;
            case 6:
                _b1 = Capture("m5_inplace_sandwich");
                _log.Append($"sandwich: segments={_stream.segmentCount} runs={_stream.runCount} quads={_stream.quadCount} skipped={_stream.lastSkippedPairs}\n");
                _log.Append("A1 vs B1 (native vs in-place, fallback interleave): ").Append(Diff(_a1, _b1)).Append("\n");
                _holder.filter = new BlurFilter();
                break;
            case 10:
                _b2 = Capture("m5_inplace_filtered");
                _stream.Dispose();
                _stream = null;
                break;
            case 13:
                _a2 = Capture("m5_native_filtered");
                _log.Append("B2 vs A2 (in-place vs native, filter capture): ").Append(Diff(_b2, _a2)).Append("\n");
                _holder.filter = null;
                System.IO.File.WriteAllText(_dir + "/m5_acceptance.txt", _log.ToString());
                _holder.Dispose();
                Destroy(gameObject);
                return;
        }
    }

    void OnDestroy()
    {
        if (_stream != null)
        {
            _stream.Dispose();
            _stream = null;
        }
    }

    Color32[] Capture(string name)
    {
        Camera cam = StageCamera.main;
        var rt = new RenderTexture(1136, 640, 24);
        var prev = cam.targetTexture;
        cam.targetTexture = rt;
        cam.Render();
        cam.targetTexture = prev;
        var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
        var prevA = RenderTexture.active;
        RenderTexture.active = rt;
        tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        tex.Apply();
        RenderTexture.active = prevA;
        System.IO.File.WriteAllBytes(_dir + "/" + name + ".png", tex.EncodeToPNG());
        var px = tex.GetPixels32();
        Destroy(tex);
        Destroy(rt);
        return px;
    }

    static string Diff(Color32[] a, Color32[] b)
    {
        long sum = 0;
        int worst = 0, bad = 0;
        for (int i = 0; i < a.Length; i++)
        {
            int d = Mathf.Abs(a[i].r - b[i].r) + Mathf.Abs(a[i].g - b[i].g) + Mathf.Abs(a[i].b - b[i].b);
            sum += d;
            if (d > worst) worst = d;
            if (d > 24) bad++;
        }
        return $"mean={(double)sum / a.Length:F3} worst={worst} badPx={bad} ({100.0 * bad / a.Length:F3}%)";
    }
}
#endif
