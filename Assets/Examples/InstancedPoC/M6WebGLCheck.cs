#if UNITY_2020_1_OR_NEWER
using System.Text;
using FairyGUI;
using UnityEngine;

/// <summary>
/// M6 ladder level 2: in-build self-check for the WebGL player. The editor can
/// only ever exercise this machine's desktop driver, and the vertex-stream
/// backend exists FOR the platforms where vertex-stage StructuredBuffer is
/// unavailable — WebGL reports zero vertex compute buffer inputs, so it selects
/// that path automatically. Everything specific to it (the attribute layout,
/// its packing, the shader's decode) is unverified until it has run here.
///
/// Two stages, one verdict:
///  1. the demo-list comparison: native vs instanced pixels, static and
///     scrolled, on real package content;
///  2. an SDF rounded rect with a border, which is the only thing here that
///     reaches the fields the vertex layout PACKS — corner radii as UNorm8
///     bytes and border width inside flags.
/// Both are scored against their OWN screen region, not the whole frame: a
/// probe that covers 3% of the canvas cannot move a frame-global threshold,
/// so a frame-global score would have made stage 2 unable to fail.
///
/// The repository's editor suites are deliberately NOT run here — their
/// Step() drives Stage.ForceUpdate(), which re-enters the PlayerLoop when
/// called from inside it (AGENTS.md pitfall 20).
///
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
        //validation harness only: never self-start in a release build (it takes
        //over the first list, resets its scroll state and reads pixels back)
        if (!Debug.isDebugBuild)
            return;
        new GameObject("M6WebGLCheck").AddComponent<M6WebGLCheck>();
    }
#endif

    //CI thresholds (batch 4): the ladder-1 editor runs measured mean ~0.1 and
    //badPx ~0.02% for a healthy takeover; anything an order beyond that is a
    //real divergence, not noise
    const double kMaxMean = 1.5;
    const double kMaxBadPct = 0.5;
    bool _failed;
    GComponent _sdfHost;
    InstancedUIStream _sdfStream;
    Color32[] _sdfNative;
    int _capW, _capH;         //the captured rect, so scores can be region-scoped
    RectInt _sdfRect;         //the probe in capture pixels

    GList _list;
    GComponent _ui;
    InstancedUIStream _stream;
    Vector2 _savedSoftness;
    int _waited;
    int _frame = -1;
    Color32[] _a, _b, _c;
    readonly StringBuilder _log = new StringBuilder();

    void LateUpdate()
    {
        if (_list == null)
        {
            //No panel/list within a sane window. This must NOT print a PASS
            //verdict: _failed can only ever be set by the comparisons below,
            //so a build whose UI failed to construct would report green to the
            //CI that greps this line — worse than the silence it replaced.
            if (++_waited > 600)
            {
                _failed = true;
                Verdict("nothing-compared: no populated list found");
                Destroy(gameObject);
                return;
            }
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
            _savedSoftness = _list.clipSoftness;
            _list.clipSoftness = Vector2.zero; //neutralize known softness divergence
            _list.scrollPane.posY = 0;
            _frame = 0;
            Debug.Log($"M6CHECK boot: device={SystemInfo.graphicsDeviceType} vertexCaps={SystemInfo.maxComputeBufferInputsVertex} useVertexPath={InstancedUIStream.useVertexPath}");
            return;
        }

        _frame++;
        if (_stream != null)
            _stream.Render();
        if (_sdfStream != null)
            _sdfStream.Render();
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
                if (_stream.quadCount == 0)
                    _failed = true; //takeover produced nothing: unconditionally wrong
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
                _list.scrollPane.posY = 0;
                _list.clipSoftness = _savedSoftness;
                //--- the list is done; now the paths it cannot reach --------
                BuildSdfProbe();
                break;
            case 21:
                _sdfNative = Capture();
                _sdfStream = new InstancedUIStream((Container)_sdfHost.displayObject, default, true, true);
                break;
            case 25:
                Debug.Log($"M6CHECK sdf takeover: quads={_sdfStream.quadCount}"
                    + $" segments={_sdfStream.segmentCount}"
                    + $" measuredStride={_sdfStream.measuredVertexStride}B"
                    + $" rect={_sdfRect.x},{_sdfRect.y} {_sdfRect.width}x{_sdfRect.height}");
                //an SDF rounded rect is ONE quad whose silhouette is computed
                //analytically — a native mesh would be many. Zero quads means
                //the primitive was not claimed and the comparison is vacuous.
                if (_sdfStream.quadCount == 0)
                    _failed = true;
                //the layout the GPU actually got, read back from the mesh —
                //the constant echoed elsewhere proves only that we can print it
                if (_sdfStream.measuredVertexStride != InstancedUIStream.vertexUploadStride)
                {
                    _failed = true;
                    Debug.Log($"M6CHECK sdf: mesh stride {_sdfStream.measuredVertexStride}B"
                        + $" != declared {InstancedUIStream.vertexUploadStride}B");
                }
                CheckSdfCorners(Capture());
                _sdfStream.Dispose();
                _sdfStream = null;
                _sdfHost.Dispose();
                _sdfHost = null;
                Verdict("list+sdf");
                Debug.Log("M6CHECK DONE");
                Destroy(gameObject);
                break;
        }
    }

    /// <summary>
    /// Builds what the demo list cannot exercise: an SDF rounded rect with a
    /// border. Its corner radii and border width are the two fields this
    /// vertex layout PACKS — radii as UNorm8 bytes, width inside flags — so
    /// their correctness depends on the real driver's attribute normalization,
    /// not on shader arithmetic the editor already proved.
    /// </summary>
    void BuildSdfProbe()
    {
        _sdfHost = new GComponent();
        _sdfHost.SetSize(220, 120);
        GRoot.inst.AddChild(_sdfHost);
        //centred rather than edge-parked: Capture() clamps to 1136x640, so a
        //probe pinned to the right edge of a wider canvas would fall outside
        //the captured rect and be compared against nothing
        _sdfHost.SetXY((GRoot.inst.width - 220f) * 0.5f, (GRoot.inst.height - 120f) * 0.5f);

        //opaque backdrop: the radius test asks "is the SHAPE here", and both
        //the fill and the border are inside the shape. Against the demo scene
        //that question has no stable answer, so give it one.
        var bg = new GGraph();
        bg.SetSize(220, 120);
        _sdfHost.AddChild(bg);
        bg.SetXY(0, 0);
        bg.DrawRect(220, 120, 0, Color.clear, Color.black);

        var g = new GGraph();
        g.SetSize(200, 100);
        _sdfHost.AddChild(g);
        g.SetXY(10, 10);
        //Radii spread across the byte range so a mis-decoded CHANNEL shows as
        //one wrong corner rather than a uniform shift. The 5px BORDER is not
        //decoration: border width is the field that moved out of misc.w into
        //flags bits 8-15, and without a nonzero one that new decode expression
        //is never evaluated with a value that could be wrong.
        //GGraph's own DrawRoundRect has no border parameter; the core Shape
        //does (AGENTS.md pitfall 6 catalogues these signature differences)
        g.DrawRoundRect(200, 100, new Color(0.2f, 0.4f, 0.9f, 1f),
            new float[] { 12, 24, 36, 48 });
        g.shape.DrawRoundRect(5, Color.yellow, new Color(0.2f, 0.4f, 0.9f, 1f),
            12, 24, 36, 48);

        //probe rect in capture pixels (stage y is down, the capture array is up)
        Vector2 tl = _sdfHost.LocalToGlobal(Vector2.zero);
        Vector2 br = _sdfHost.LocalToGlobal(new Vector2(_sdfHost.width, _sdfHost.height));
        _sdfRect = new RectInt(Mathf.FloorToInt(tl.x), Mathf.FloorToInt(_capH - br.y),
            Mathf.CeilToInt(br.x - tl.x), Mathf.CeilToInt(br.y - tl.y));
    }

    /// The radii baked into the probe, and what each SCREEN corner must
    /// measure back as. The per-corner expectation is what gives this teeth:
    /// an earlier version only asked "is exactly one corner tight at 4px",
    /// which a swap among the three loose radii slid straight past.
    /// Order is top-left, top-right, bottom-left, bottom-right in screen
    /// terms; the values are MEASURED from a healthy build rather than
    /// assumed, because FairyGUI's corner argument order and the shader's
    /// quadrant fold both sit between the array and the pixels.
    static readonly float[] kExpectRadii = { 12f, 24f, 36f, 48f };
    //tolerance covers the antialiasing bias in reading an arc back from
    //pixels (a healthy build measures ~8.5/20.5/32.4/47.8 against 12/22/34/46).
    //The radii are spaced 12 apart — more than 2x this — so any permutation of
    //two corners moves both readings clear outside it.
    const float kRadiusTol = 5f;

    /// <summary>
    /// Reads the corner radii back OUT of the rendered pixels.
    ///
    /// A region mean is the wrong instrument here and measuring one taught me
    /// why: an SDF primitive computes coverage analytically while the native
    /// mesh triangulates it, so their edges differ by ~2% of the probe area on
    /// a healthy build — enough to swamp any threshold a decode error would
    /// have to cross. (Worse, the obvious fault to inject — deleting the
    /// shader's floor(x*255+0.5) — is a NO-OP on a driver whose UNorm8 unpack
    /// is already exact, so a region mean flagged neither the fault nor its
    /// absence.)
    ///
    /// Radii are geometry, so read them as geometry. For a corner of radius r
    /// the arc centre sits at (r,r), so the diagonal point (d,d) is covered
    /// exactly when d >= r*(1 - 1/sqrt2) = 0.2929r. Walking d outward until
    /// coverage starts therefore MEASURES r, and each corner is then checked
    /// against its own expected radius — so zeroed radii, a dropped channel
    /// and any permutation all show up, and the log carries the four numbers
    /// rather than a verdict alone.
    /// </summary>
    void CheckSdfCorners(Color32[] px)
    {
        //rect is at (10,10) sized 200x100 inside the host
        var corners = new[]
        {
            new Vector2(10, 10), new Vector2(210, 10),
            new Vector2(10, 110), new Vector2(210, 110),
        };
        var inward = new[]
        {
            new Vector2(1, 1), new Vector2(-1, 1),
            new Vector2(1, -1), new Vector2(-1, -1),
        };
        var report = new StringBuilder();
        bool ok = true;
        for (int i = 0; i < 4; i++)
        {
            float first = -1f;
            for (float d = 0.5f; d <= 32f; d += 0.5f)
            {
                if (SampleIsShape(px, corners[i] + inward[i] * d)) { first = d; break; }
            }
            float measured = first < 0f ? -1f : first / 0.2929f;
            bool good = first >= 0f && Mathf.Abs(measured - kExpectRadii[i]) <= kRadiusTol;
            if (!good) ok = false;
            report.Append($"{measured:F1}/{kExpectRadii[i]:F0}{(good ? "" : "!")} ");
        }
        if (!ok)
            _failed = true;
        Debug.Log($"M6CHECK sdf radii (measured/expected): {report.ToString().Trim()}"
            + $" tol=±{kRadiusTol} {(ok ? "ok" : "WRONG")}");
    }

    /// <summary>
    /// True when the rounded rect COVERS a host-local point — fill or border
    /// alike, since the radius sets the shape's outline and the 5px border
    /// rides its outer edge. Anything still showing the probe's black backdrop
    /// is outside the shape.
    /// </summary>
    bool SampleIsShape(Color32[] px, Vector2 hostLocal)
    {
        Vector2 st = _sdfHost.LocalToGlobal(hostLocal);
        int x = Mathf.Clamp(Mathf.RoundToInt(st.x), 0, _capW - 1);
        int y = Mathf.Clamp(_capH - 1 - Mathf.RoundToInt(st.y), 0, _capH - 1);
        Color32 c = px[y * _capW + x];
        return c.r + c.g + c.b > 90; //blue fill, yellow border, black backdrop
    }

    /// <summary>The single machine-readable line the hosting page / CI greps.</summary>
    void Verdict(string coverage)
    {
        Debug.Log("M6CHECK VERDICT: " + (_failed ? "FAIL" : "PASS")
            + $" coverage={coverage} upload={InstancedUIStream.vertexUploadStride}B/vertex"
            + $" (thresholds mean<{kMaxMean} badPx<{kMaxBadPct}%)");
    }

    void OnDestroy()
    {
        if (_stream != null)
        {
            _stream.Dispose();
            _stream = null;
        }
        if (_sdfStream != null)
        {
            _sdfStream.Dispose();
            _sdfStream = null;
        }
        if (_sdfHost != null)
        {
            _sdfHost.Dispose();
            _sdfHost = null;
        }
    }

    Color32[] Capture()
    {
        Camera cam = StageCamera.main;
        int w = Mathf.Min(Screen.width, 1136), h = Mathf.Min(Screen.height, 640);
        _capW = w; _capH = h;
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

    string Diff(Color32[] a, Color32[] b)
    {
        return Diff(a, b, 0, 0, _capW, _capH, "frame");
    }

    /// <summary>
    /// Scores a RECTANGLE, not the frame. A small probe scored frame-globally
    /// cannot fail: the SDF rect is ~3% of the canvas, so deleting the shader's
    /// radii rounding — the single line this batch's correctness rests on —
    /// moves whole-frame badPx by under 0.1%, comfortably inside a 0.5%
    /// threshold. Scoring the probe's own pixels makes the same error ~30x
    /// larger than the bar instead of 5x under it.
    /// </summary>
    string Diff(Color32[] a, Color32[] b, int x0, int y0, int x1, int y1, string what)
    {
        x0 = Mathf.Clamp(x0, 0, _capW); x1 = Mathf.Clamp(x1, 0, _capW);
        y0 = Mathf.Clamp(y0, 0, _capH); y1 = Mathf.Clamp(y1, 0, _capH);
        int n = Mathf.Max(0, x1 - x0) * Mathf.Max(0, y1 - y0);
        if (n < 256)
        {
            //an empty or degenerate region means the probe fell outside the
            //captured area: the comparison would be vacuous, so say so loudly
            _failed = true;
            return $"{what}: EMPTY REGION ({x0},{y0})-({x1},{y1}) — nothing compared";
        }
        long sum = 0;
        int worst = 0, bad = 0;
        for (int y = y0; y < y1; y++)
        {
            int row = y * _capW;
            for (int x = x0; x < x1; x++)
            {
                int i = row + x;
                int d = Mathf.Abs(a[i].r - b[i].r) + Mathf.Abs(a[i].g - b[i].g) + Mathf.Abs(a[i].b - b[i].b);
                sum += d;
                if (d > worst) worst = d;
                if (d > 24) bad++;
            }
        }
        double mean = (double)sum / n;
        double badPct = 100.0 * bad / n;
        if (mean > kMaxMean || badPct > kMaxBadPct)
            _failed = true;
        return $"{what}: mean={mean:F3} worst={worst} badPx={bad}/{n} ({badPct:F3}%)";
    }
}
#endif
