#if UNITY_2020_1_OR_NEWER
using System.Text;
using FairyGUI;
using UnityEngine;

/// <summary>
/// M6 ladder level 2: in-build self-check for the WebGL player. Boots itself on
/// WebGL, waits for the demo panel, then pixel-compares native rendering against
/// the in-place instanced stream (which auto-selects the vertex-stream backend —
/// WebGL reports zero vertex compute buffer inputs), static and scrolled.
/// Results go to Debug.Log with an M6CHECK prefix so the hosting browser's
/// console carries the verdict.
/// </summary>
[DefaultExecutionOrder(1000)]
public class M6WebGLCheck : MonoBehaviour
{
#if UNITY_WEBGL && !UNITY_EDITOR
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        new GameObject("M6WebGLCheck").AddComponent<M6WebGLCheck>();
    }
#endif

    GList _list;
    GComponent _ui;
    InstancedUIStream _stream;
    int _frame = -1;
    Color32[] _a, _b, _c;
    readonly StringBuilder _log = new StringBuilder();

    void LateUpdate()
    {
        if (_list == null)
        {
            var panel = Object.FindObjectOfType<UIPanel>();
            if (panel == null || panel.ui == null)
                return;
            GList found = null;
            void Walk(GComponent comp)
            {
                for (int i = 0; i < comp.numChildren; i++)
                {
                    var ch = comp.GetChildAt(i);
                    if (ch is GList l && l.numItems > 0 && found == null) found = l;
                    if (ch is GComponent c) Walk(c);
                }
            }
            Walk(panel.ui);
            if (found == null)
                return;
            _list = found;
            _ui = panel.ui;
            _list.clipSoftness = Vector2.zero; //neutralize known softness divergence
            _list.scrollPane.posY = 0;
            _frame = 0;
            Debug.Log($"M6CHECK boot: device={SystemInfo.graphicsDeviceType} vertexCaps={SystemInfo.maxComputeBufferInputsVertex} useVertexPath={InstancedUIStream.useVertexPath}");
            return;
        }

        _frame++;
        if (_stream != null)
            _stream.Render();
        switch (_frame)
        {
            case 5:
                _a = Capture();
                _stream = new InstancedUIStream((Container)_ui.displayObject, default, true, true);
                break;
            case 9:
                _b = Capture();
                Debug.Log($"M6CHECK takeover: segments={_stream.segmentCount} quads={_stream.quadCount} skipped={_stream.lastSkippedPairs}");
                Debug.Log("M6CHECK static: " + Diff(_a, _b));
                _list.scrollPane.posY = 130;
                break;
            case 13:
                _c = Capture();
                _stream.Dispose();
                _stream = null;
                break;
            case 17:
                var d = Capture();
                Debug.Log("M6CHECK scrolled: " + Diff(_c, d));
                Debug.Log("M6CHECK DONE");
                _list.scrollPane.posY = 0;
                Destroy(gameObject);
                break;
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

    Color32[] Capture()
    {
        Camera cam = StageCamera.main;
        int w = Mathf.Min(Screen.width, 1136), h = Mathf.Min(Screen.height, 640);
        var rt = new RenderTexture(w, h, 24);
        var prev = cam.targetTexture;
        cam.targetTexture = rt;
        cam.Render();
        cam.targetTexture = prev;
        var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
        var prevA = RenderTexture.active;
        RenderTexture.active = rt;
        tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
        tex.Apply();
        RenderTexture.active = prevA;
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
