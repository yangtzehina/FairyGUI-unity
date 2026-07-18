#if UNITY_2020_1_OR_NEWER
using System.Text;
using FairyGUI;
using UnityEngine;

/// <summary>
/// M4 acceptance driver: takes over a UIPanel's UI with an in-place
/// InstancedUIStream across real frames and pixel-compares against the native
/// renderer in four states — native / taken-over / scrolled(taken-over) /
/// scrolled(native after dispose). Runs as a MonoBehaviour so each capture sits
/// in its own frame (RenderMeshPrimitives submissions are per-frame, and
/// FairyGUI's pixel-perfect snap is frame-deferred).
/// </summary>
[DefaultExecutionOrder(1000)]
public class InPlaceAcceptance : MonoBehaviour
{
    public static void Run(GComponent panelUI, GList scrollList, string outputDir)
    {
        var go = new GameObject("InPlaceAcceptance");
        var a = go.AddComponent<InPlaceAcceptance>();
        a._ui = panelUI;
        a._list = scrollList;
        a._dir = outputDir;
    }

    GComponent _ui;
    GList _list;
    string _dir;
    InstancedUIStream _stream;
    int _frame;
    Color32[] _a, _b, _c, _d;
    readonly StringBuilder _log = new StringBuilder();

    void LateUpdate()
    {
        _frame++;
        //submit BEFORE any capture: RenderMeshPrimitives submissions are consumed
        //by the camera render of the same frame
        if (_stream != null)
            _stream.Render();
        switch (_frame)
        {
            case 3: //settled: native baseline
                _a = Capture("m4_native");
                break;
            case 4:
                _stream = new InstancedUIStream((Container)_ui.displayObject, default, true, true);
                break;
            case 7:
                _c = null; //placeholder ordering guard
                _b = Capture("m4_inplace");
                _log.Append($"takeover: segments={_stream.segmentCount} quads={_stream.quadCount} skipped={_stream.lastSkippedPairs}\n");
                _log.Append("A vs B (native vs in-place): ").Append(Diff(_a, _b)).Append("\n");
                _list.scrollPane.posY = 130;
                break;
            case 10:
                _c = Capture("m4_inplace_scrolled");
                _stream.Dispose();
                _stream = null;
                break;
            case 13:
                _d = Capture("m4_native_scrolled");
                _log.Append("C vs D (in-place scrolled vs native scrolled): ").Append(Diff(_c, _d)).Append("\n");
                _list.scrollPane.posY = 0;
                System.IO.File.WriteAllText(_dir + "/m4_acceptance.txt", _log.ToString());
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
