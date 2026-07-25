using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace FairyGUIEditor
{
    /// <summary>
    /// CI entry for the WebGL validation build (v4 ladder level 2, batch 4):
    /// builds the VirtualList demo as a development WebGL player. The build
    /// contains M6WebGLCheck, which self-boots in development builds, compares
    /// native vs instanced rendering pixel-by-pixel and logs a single
    /// "M6CHECK VERDICT: PASS|FAIL" line to the browser console for the CI
    /// harness to grep.
    ///
    ///   Unity -batchmode -quit -projectPath . \
    ///         -executeMethod FairyGUIEditor.InstancedCIBuild.BuildWebGL \
    ///         [-ciOutput Builds/WebGLCheck]
    /// </summary>
    public static class InstancedCIBuild
    {
        const string kScene = "Assets/Examples/Scenes/Example 15 - VirtualList.unity";
        const string kDefaultOutput = "Builds/WebGLCheck";

        public static void BuildWebGL()
        {
            string output = kDefaultOutput;
            string[] args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, "-ciOutput");
            if (i >= 0 && i + 1 < args.Length)
                output = args[i + 1];

            var options = new BuildPlayerOptions
            {
                scenes = new[] { kScene },
                locationPathName = output,
                target = BuildTarget.WebGL,
                //development: M6WebGLCheck's boot guard requires Debug.isDebugBuild
                options = BuildOptions.Development,
            };

            var report = BuildPipeline.BuildPlayer(options);
            bool ok = report.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded;
            Debug.Log($"InstancedCIBuild: {report.summary.result}, {report.summary.totalSize} bytes, "
                + $"{report.summary.totalErrors} errors -> {output}");
            if (!ok)
                EditorApplication.Exit(1);
        }
    }
}
