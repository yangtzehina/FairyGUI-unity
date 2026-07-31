using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace FairyGUIEditor
{
    /// <summary>
    /// CI entry for the perf RATIO gates (tier B). Deliberately separate from
    /// InstancedValidationCI: the 227 behavioural checks leave GC and driver
    /// debt behind, and that debt is exactly what skews micro-benchmarks — the
    /// M8-5 incident had a real 45% improvement read as 18% inside a long
    /// session. A batchmode launch is a fresh session by construction, so this
    /// entry must run as its OWN Unity process, never chained after the
    /// behavioural one inside the same editor.
    ///
    ///   Unity -batchmode -projectPath . \
    ///         -executeMethod FairyGUIEditor.InstancedPerfCI.Run \
    ///         [-ciOutput Logs/InstancedPerfResults.txt]
    ///
    /// Same harness rules as InstancedValidationCI: no -quit (the entry must
    /// survive the play-mode domain reload and exits itself), no -nographics,
    /// and the project must not be open in a GUI editor. Verdict line:
    /// "INSTANCED PERF VERDICT: PASS|FAIL pass=N fail=M".
    ///
    /// Only RATIOS are gated. Absolute microseconds land in the report as a
    /// trend record — gating them would mean re-calibrating per machine.
    /// </summary>
    public static class InstancedPerfCI
    {
        const string kScene = "Assets/Examples/InstancedPoC/Validation/ValidationScene.unity";
        const string kDefaultOutput = "Logs/InstancedPerfResults.txt";
        const string kVerdictPrefix = "INSTANCED PERF VERDICT: ";

        const string kActiveKey = "FairyGUI.InstancedPerfCI.active";
        const string kOutputKey = "FairyGUI.InstancedPerfCI.output";
        const string kDeadlineKey = "FairyGUI.InstancedPerfCI.deadline";

        const int kSettleFrames = 45;
        const double kTimeoutSeconds = 900;

        static int sFrames;

        public static void Run()
        {
            string output = kDefaultOutput;
            string[] args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, "-ciOutput");
            if (i >= 0 && i + 1 < args.Length)
                output = args[i + 1];

            SessionState.SetString(kOutputKey, output);
            SessionState.SetFloat(kDeadlineKey, (float)(EditorApplication.timeSinceStartup + kTimeoutSeconds));
            SessionState.SetBool(kActiveKey, true);

            try
            {
                EditorSceneManager.OpenScene(kScene, OpenSceneMode.Single);
            }
            catch (Exception e)
            {
                Fail("could not open the validation scene: " + e.Message);
                return;
            }

            Debug.Log("InstancedPerfCI: entering Play mode (fresh session)…");
            EditorApplication.EnterPlaymode();
            Attach();
        }

        [InitializeOnLoadMethod]
        static void Hook()
        {
            if (SessionState.GetBool(kActiveKey, false))
                Attach();
        }

        static void Attach()
        {
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        static void Tick()
        {
            if (!SessionState.GetBool(kActiveKey, false))
            {
                EditorApplication.update -= Tick;
                return;
            }
            if (EditorApplication.timeSinceStartup > SessionState.GetFloat(kDeadlineKey, 0))
            {
                Fail($"timed out after {kTimeoutSeconds:F0}s");
                return;
            }
            if (!EditorApplication.isPlaying || EditorApplication.isCompiling)
                return;
            if (++sFrames < kSettleFrames)
                return;

            EditorApplication.update -= Tick;

            string report;
            try
            {
                Type t = Type.GetType("InstancedPerfRatioBench, Assembly-CSharp");
                if (t == null)
                {
                    Fail("InstancedPerfRatioBench not found in Assembly-CSharp");
                    return;
                }
                MethodInfo m = t.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
                if (m == null)
                {
                    Fail("InstancedPerfRatioBench.Run() not found");
                    return;
                }
                report = (string)m.Invoke(null, null);
            }
            catch (Exception e)
            {
                Fail("bench threw: " + (e.InnerException ?? e));
                return;
            }

            int pass = 0, fail = 1;
            int nl = report.IndexOf('\n');
            string head = nl >= 0 ? report.Substring(0, nl) : report;
            foreach (var token in head.Split(' '))
            {
                if (token.StartsWith("pass=")) int.TryParse(token.Substring(5), out pass);
                if (token.StartsWith("fail=")) int.TryParse(token.Substring(5), out fail);
            }

            string verdict = kVerdictPrefix + (fail == 0 ? "PASS" : "FAIL") + $" pass={pass} fail={fail}";
            WriteReport(verdict + "\n\n" + report);
            Debug.Log(report);
            Debug.Log(verdict);
            Finish(fail == 0 ? 0 : 1);
        }

        static void Fail(string why)
        {
            string verdict = kVerdictPrefix + "FAIL pass=0 fail=1 (" + why + ")";
            WriteReport(verdict + "\n");
            Debug.LogError("InstancedPerfCI: " + why);
            Debug.Log(verdict);
            Finish(1);
        }

        static void WriteReport(string text)
        {
            //NOT Temp/: Unity wipes it on exit and CI would find nothing
            string output = SessionState.GetString(kOutputKey, kDefaultOutput);
            try
            {
                string dir = Path.GetDirectoryName(output);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(output, text);
                Debug.Log("InstancedPerfCI: report -> " + output);
            }
            catch (Exception e)
            {
                Debug.LogError("InstancedPerfCI: could not write " + output + ": " + e.Message);
            }
        }

        static void Finish(int code)
        {
            EditorApplication.update -= Tick;
            SessionState.SetBool(kActiveKey, false);
            if (Application.isBatchMode)
                EditorApplication.Exit(code);
            else if (EditorApplication.isPlaying)
                EditorApplication.isPlaying = false;
        }

        [MenuItem("Tools/FairyGUI/Run Perf Gates")]
        static void RunFromMenu()
        {
            if (!EditorApplication.isPlaying)
            {
                EditorUtility.DisplayDialog("Instanced perf gates",
                    "Enter Play mode with " + kScene + " open first.\n\n"
                    + "Note: perf numbers are only trustworthy in a FRESH play session — "
                    + "a long session's GC/driver debt skews micro-benchmarks.", "OK");
                return;
            }
            SessionState.SetString(kOutputKey, kDefaultOutput);
            SessionState.SetFloat(kDeadlineKey, (float)(EditorApplication.timeSinceStartup + kTimeoutSeconds));
            SessionState.SetBool(kActiveKey, true);
            sFrames = kSettleFrames;
            Attach();
        }
    }
}
