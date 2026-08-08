using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace FairyGUIEditor
{
    /// <summary>
    /// In-editor UTF runner for the validation suites: kicks off the PlayMode
    /// test run (ValidationSuites, both backend fixtures) through
    /// TestRunnerApi and writes a grep-able report. The report file is the
    /// completion signal for driving harnesses (unicli): it is DELETED at
    /// launch and appears only when the run finished, first line
    /// "UTF VALIDATION VERDICT: PASS|FAIL pass=N fail=M".
    ///
    /// For real CI prefer the stock command line — no editor session needed:
    ///   Unity -batchmode -projectPath . -runTests -testPlatform PlayMode \
    ///         -testResults Logs/utf-results.xml
    /// (keep graphics: the suites read rendered pixels back.)
    /// </summary>
    public static class ValidationTestRunnerCI
    {
        const string kOutput = "Logs/UtfValidationResults.txt";

        [MenuItem("Tools/FairyGUI/Run Validation Suites (UTF)")]
        public static void Run()
        {
            if (File.Exists(kOutput))
                File.Delete(kOutput);
            Callbacks.Reset();
            var api = ScriptableObject.CreateInstance<TestRunnerApi>();
            api.Execute(new ExecutionSettings(new Filter
            {
                testMode = TestMode.PlayMode,
            }));
            Debug.Log($"UTF validation run started; report lands in {kOutput}.");
        }

        /// <summary>
        /// Registered from InitializeOnLoad so the callback survives the
        /// domain reloads a PlayMode run performs — an instance registered
        /// only at Execute time would be gone by RunFinished.
        /// </summary>
        [InitializeOnLoad]
        sealed class Callbacks : ICallbacks
        {
            static readonly Callbacks sInstance = new Callbacks();
            //SessionState (not statics): the collected lines must survive the
            //enter-play and exit-play domain reloads mid-run
            const string kLinesKey = "FairyGUI.UtfCI.Lines";
            const string kCountsKey = "FairyGUI.UtfCI.Counts";

            static Callbacks()
            {
                var api = ScriptableObject.CreateInstance<TestRunnerApi>();
                api.RegisterCallbacks(sInstance);
            }

            internal static void Reset()
            {
                SessionState.SetString(kLinesKey, "");
                SessionState.SetString(kCountsKey, "0 0");
            }

            public void RunStarted(ITestAdaptor testsToRun) { }

            public void TestStarted(ITestAdaptor test) { }

            public void TestFinished(ITestResultAdaptor result)
            {
                if (result.Test.HasChildren)
                    return;
                bool passed = result.TestStatus == TestStatus.Passed;
                var counts = SessionState.GetString(kCountsKey, "0 0").Split(' ');
                int p = int.Parse(counts[0]) + (passed ? 1 : 0);
                int f = int.Parse(counts[1]) + (passed ? 0 : 1);
                SessionState.SetString(kCountsKey, p + " " + f);
                string line = (passed ? "PASS " : "FAIL ") + result.FullName;
                if (!passed)
                    line += "\n" + result.Message;
                SessionState.SetString(kLinesKey,
                    SessionState.GetString(kLinesKey, "") + line + "\n");
            }

            public void RunFinished(ITestResultAdaptor result)
            {
                var counts = SessionState.GetString(kCountsKey, "0 0").Split(' ');
                if (counts[0] == "0" && counts[1] == "0")
                    return; //a run this class did not launch (window-driven with no tests?) — still fine to report, but empty means nothing ran through us
                string verdict = counts[1] == "0" ? "PASS" : "FAIL";
                var sb = new StringBuilder();
                sb.Append("UTF VALIDATION VERDICT: ").Append(verdict)
                  .Append(" pass=").Append(counts[0]).Append(" fail=").Append(counts[1]).Append('\n')
                  .Append('\n')
                  .Append(SessionState.GetString(kLinesKey, ""));
                Directory.CreateDirectory(Path.GetDirectoryName(kOutput));
                File.WriteAllText(kOutput, sb.ToString());
                Reset();
                Debug.Log($"UTF validation finished: {verdict} pass={counts[0]} fail={counts[1]} -> {kOutput}");
            }
        }
    }
}
