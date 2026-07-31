#if UNITY_2020_1_OR_NEWER
using System;
using System.Text;

/// <summary>
/// Runs every instanced-renderer validation suite in regression order —
/// M4 scenarios 19, batch1 14, batch2 8, batch3 19, batch3d 10, batch4 12,
/// batch5 curve text 10, plus the MVVM reentrancy 11 — and aggregates the
/// verdict. Invoke
/// InstancedValidationAll.Run() from a Play mode eval; the first line is
/// "ALL RESULT pass=N fail=N".
/// </summary>
public static class InstancedValidationAll
{
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

        RunSuite("m4_scenarios", M4ScenarioSuite.Run);
        RunSuite("batch1", InstancedBatch1Suite.Run);
        RunSuite("batch2", InstancedBatch2Suite.Run);
        RunSuite("batch3", InstancedBatch3Suite.Run);
        RunSuite("batch3d", InstancedBatch3dSuite.Run);
        RunSuite("batch4", InstancedBatch4Suite.Run);
        RunSuite("batch5_curvetext", InstancedBatch5Suite.Run);
        RunSuite("mvvm_reentrancy", BinderReentrancyCheck.Run);

        sb.Insert(0, $"ALL RESULT pass={pass} fail={fail}\n\n");
        return sb.ToString();
    }
}
#endif
