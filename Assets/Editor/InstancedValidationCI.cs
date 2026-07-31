using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace FairyGUIEditor
{
    /// <summary>
    /// CI entry for the instanced-renderer validation suites
    /// (Assets/Examples/InstancedPoC/Validation/). The suites are Play-mode
    /// code — they drive real frames and read back rendered pixels — so this
    /// entry opens the validation scene, enters Play mode, runs
    /// InstancedValidationAll, writes the full report plus a single
    /// machine-readable verdict line, and exits with a non-zero code on any
    /// failure.
    ///
    ///   Unity -batchmode -projectPath . \
    ///         -executeMethod FairyGUIEditor.InstancedValidationCI.Run \
    ///         [-ciOutput Logs/InstancedValidationResults.txt] \
    ///         [-ciBackend vertex|buffer|both]
    ///
    /// The two backends are separate shader and upload implementations of the
    /// same semantics, so "both" is the honest setting where the hardware
    /// supports it. Default stays "vertex": it renders everywhere, including
    /// the machines where the editor's buffer path draws nothing (design §11).
    ///
    /// Notes for the harness:
    /// - do NOT pass -quit: the entry must survive the play-mode domain reload
    ///   and calls EditorApplication.Exit itself;
    /// - do NOT pass -nographics: the suites read back rendered pixels;
    /// - the verdict line is "INSTANCED VALIDATION VERDICT: PASS|FAIL
    ///   pass=N fail=M" — grep that.
    ///
    /// Interactively, use the menu item instead (it assumes you are already in
    /// Play mode with the validation scene open).
    /// </summary>
    public static class InstancedValidationCI
    {
        const string kScene = "Assets/Examples/InstancedPoC/Validation/ValidationScene.unity";
        //NOT Temp/: Unity wipes that directory when the editor quits, so a
        //batchmode report written there is gone before CI can read it
        const string kDefaultOutput = "Logs/InstancedValidationResults.txt";
        const string kVerdictPrefix = "INSTANCED VALIDATION VERDICT: ";

        //survives the domain reload that entering Play mode triggers
        const string kActiveKey = "FairyGUI.InstancedValidationCI.active";
        const string kOutputKey = "FairyGUI.InstancedValidationCI.output";
        const string kDeadlineKey = "FairyGUI.InstancedValidationCI.deadline";
        const string kBackendKey = "FairyGUI.InstancedValidationCI.backend";

        const int kSettleFrames = 45;   //let FairyGUI's Stage/camera boot
        const double kTimeoutSeconds = 900;

        static int sFrames;

        /// <summary>-executeMethod entry.</summary>
        public static void Run()
        {
            string output = kDefaultOutput;
            string[] args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, "-ciOutput");
            if (i >= 0 && i + 1 < args.Length)
                output = args[i + 1];

            string backend = "vertex";
            int b = Array.IndexOf(args, "-ciBackend");
            if (b >= 0 && b + 1 < args.Length)
                backend = args[b + 1].ToLowerInvariant();
            if (backend != "vertex" && backend != "buffer" && backend != "both")
            {
                Fail($"unknown -ciBackend '{backend}' (expected vertex|buffer|both)");
                return;
            }

            SessionState.SetString(kOutputKey, output);
            SessionState.SetString(kBackendKey, backend);
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

            Debug.Log("InstancedValidationCI: entering Play mode…");
            EditorApplication.EnterPlaymode();
            //the domain reload wipes this callback; Hook() re-attaches it
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
                Fail($"timed out after {kTimeoutSeconds:F0}s waiting for the suites");
                return;
            }

            //wait for Play mode, then give the stage a few frames to boot
            if (!EditorApplication.isPlaying || EditorApplication.isCompiling)
                return;
            if (++sFrames < kSettleFrames)
                return;

            EditorApplication.update -= Tick;

            string report;
            try
            {
                //the editor assembly cannot reference Assembly-CSharp at compile
                //time (predefined assemblies are one-way), so bind by name
                Type t = Type.GetType("InstancedValidationAll, Assembly-CSharp");
                if (t == null)
                {
                    Fail("InstancedValidationAll not found in Assembly-CSharp");
                    return;
                }
                string backend = SessionState.GetString(kBackendKey, "vertex");
                MethodInfo m;
                object[] argv;
                if (backend == "both")
                {
                    m = t.GetMethod("RunBothBackends", BindingFlags.Public | BindingFlags.Static);
                    argv = null;
                }
                else
                {
                    m = t.GetMethod("RunOn", BindingFlags.Public | BindingFlags.Static);
                    argv = new object[] { backend == "vertex" };
                }
                if (m == null)
                {
                    Fail("InstancedValidationAll entry point not found");
                    return;
                }
                Debug.Log("InstancedValidationCI: backend = " + backend);
                report = (string)m.Invoke(null, argv);
            }
            catch (Exception e)
            {
                Fail("suite threw: " + (e.InnerException ?? e));
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
            Debug.LogError("InstancedValidationCI: " + why);
            Debug.Log(verdict);
            Finish(1);
        }

        static void WriteReport(string text)
        {
            string output = SessionState.GetString(kOutputKey, kDefaultOutput);
            try
            {
                string dir = Path.GetDirectoryName(output);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(output, text);
                Debug.Log("InstancedValidationCI: report -> " + output);
            }
            catch (Exception e)
            {
                Debug.LogError("InstancedValidationCI: could not write " + output + ": " + e.Message);
            }
        }

        static void Finish(int code)
        {
            EditorApplication.update -= Tick;
            SessionState.SetBool(kActiveKey, false);
            if (Application.isBatchMode)
                EditorApplication.Exit(code);
            else if (EditorApplication.isPlaying)
                EditorApplication.isPlaying = false; //interactive: just stop
        }

        [MenuItem("Tools/FairyGUI/Run Validation Suites")]
        static void RunFromMenu()
        {
            if (!EditorApplication.isPlaying)
            {
                EditorUtility.DisplayDialog("Instanced validation",
                    "Enter Play mode with " + kScene + " open first —\n"
                    + "the suites drive real frames and read back pixels.", "OK");
                return;
            }
            SessionState.SetString(kOutputKey, kDefaultOutput);
            SessionState.SetFloat(kDeadlineKey, (float)(EditorApplication.timeSinceStartup + kTimeoutSeconds));
            SessionState.SetBool(kActiveKey, true);
            sFrames = kSettleFrames; //already running: no settle needed
            Attach();
        }
    }
}
