#if UNITY_2020_1_OR_NEWER
using System;
using System.Text;

/// <summary>
/// Runs every instanced-renderer validation suite in regression order and
/// aggregates the verdict. The suite catalog with per-suite check counts
/// lives in Validation/README.md (single source of truth — per-suite numbers
/// enumerated here drifted three times before this comment gave up on them).
/// Wall-clock RATIO gates are a separate fresh-session
/// entry (FairyGUIEditor.InstancedPerfCI); see Validation/README.md.
/// M8-3 (codegen) and M8-6 (the standing parity catalog) live in the editor
/// assembly: see Validation/README.md. Invoke
/// InstancedValidationAll.Run() from a Play mode eval; the first line is
/// "ALL RESULT pass=N fail=N".
/// </summary>
public static class InstancedValidationAll
{
    /// <summary>
    /// Runs the whole set against ONE backend (true = vertex-stream, false =
    /// buffer / vertex-stage StructuredBuffer) and restores the previous
    /// selection. The buffer path is the desktop and Android-Vulkan path, so
    /// covering it is not optional — it was simply unrunnable here while the
    /// editor's buffer-path quirk was active.
    /// </summary>
    public static string RunOn(bool vertexBackend)
    {
        bool prev = InstancedValidationEnv.useVertexBackend;
        InstancedValidationEnv.useVertexBackend = vertexBackend;
        try
        {
            return Run();
        }
        finally { InstancedValidationEnv.useVertexBackend = prev; }
    }

    /// <summary>
    /// Sweeps BOTH backends and aggregates. The two paths are separate shader
    /// and upload implementations of the same semantics, so a check that only
    /// ever ran on one of them covers half of what it claims.
    /// </summary>
    public static string RunBothBackends()
    {
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        foreach (bool vertex in new[] { true, false })
        {
            string report = RunOn(vertex);
            int p = 0, f = 1;
            int nl = report.IndexOf('\n');
            string head = nl >= 0 ? report.Substring(0, nl) : report;
            foreach (var token in head.Split(' '))
            {
                if (token.StartsWith("pass=")) int.TryParse(token.Substring(5), out p);
                if (token.StartsWith("fail=")) int.TryParse(token.Substring(5), out f);
            }
            pass += p;
            fail += f;
            sb.Append("######## backend: ").Append(vertex ? "vertex-stream" : "buffer")
              .Append(" ########\n").Append(report).Append('\n');
        }
        sb.Insert(0, $"ALL RESULT pass={pass} fail={fail}\n\n");
        return sb.ToString();
    }

    public static string Run()
    {
        var sb = new StringBuilder();
        int pass = 0, fail = 0;

        void RunSuite(string name, Func<string> suite)
        {
            string report;
            try
            {
                report = suite();
            }
            catch (Exception ex)
            {
                report = "RESULT pass=0 fail=1\nEXCEPTION " + ex + "\n";
            }
            //first line: RESULT pass=N fail=M
            int p = 0, f = 1;
            int nl = report.IndexOf('\n');
            string head = nl >= 0 ? report.Substring(0, nl) : report;
            foreach (var token in head.Split(' '))
            {
                if (token.StartsWith("pass=")) int.TryParse(token.Substring(5), out p);
                if (token.StartsWith("fail=")) int.TryParse(token.Substring(5), out f);
            }
            pass += p;
            fail += f;
            sb.Append("== ").Append(name).Append(" ==\n").Append(report).Append('\n');
        }

        //bottom of the stack first: if quad reconstruction or clip folding is
        //wrong, everything above is green for nothing
        RunSuite("m1_reassembler", InstancedReassemblerSuite.Run);
        RunSuite("m3_clipstack", InstancedClipStackSuite.Run);
        RunSuite("scope_barriers", InstancedScopeBarrierSuite.Run);
        RunSuite("m7_sdf", InstancedM7SdfSuite.Run);
        RunSuite("m4_scenarios", M4ScenarioSuite.Run);
        RunSuite("colorfilter_leaf", InstancedColorFilterSuite.Run);
        RunSuite("batch1", InstancedBatch1Suite.Run);
        RunSuite("batch2", InstancedBatch2Suite.Run);
        RunSuite("batch3", InstancedBatch3Suite.Run);
        RunSuite("batch3d", InstancedBatch3dSuite.Run);
        RunSuite("batch4", InstancedBatch4Suite.Run);
        RunSuite("batch5_curvetext", InstancedBatch5Suite.Run);
        RunSuite("m8_1_blob_baker", InstancedM81Suite.Run);
        RunSuite("m8_2_mount_fusion", InstancedM82Suite.Run);
        RunSuite("m8_4_tiers", InstancedM84Suite.Run);
        RunSuite("m8_5_renderless", InstancedM85Suite.Run);
        RunSuite("m8_automount", FqsAutoMountSuite.Run);
        RunSuite("m8_superset", FqsSupersetSuite.Run);
        RunSuite("curve_effects", CurveEffectsSuite.Run);
        RunSuite("perf_invariants", InstancedPerfInvariantSuite.Run);
        RunSuite("event_semantics", EventSemanticsSuite.Run);
        RunSuite("mvvm_reentrancy", BinderReentrancyCheck.Run);

        sb.Insert(0, $"ALL RESULT pass={pass} fail={fail}\n\n");
        return sb.ToString();
    }
}
#endif
