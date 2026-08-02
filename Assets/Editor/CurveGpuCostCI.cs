using System;
using UnityEditor;
using UnityEngine;

namespace FairyGUIEditor
{
    /// <summary>
    /// 8a build entry: a macOS (Metal) development player whose only job is to
    /// run CurveGpuCostBench. enableFrameTimingStats is switched on here — GPU
    /// frame times come back as zero without it, and ProjectSettings churn is
    /// never committed in this repository, so build-time is the right place.
    ///
    ///   eval: FairyGUIEditor.CurveGpuCostCI.BuildMac()  ->  Build/MacCurveGpu.app
    ///   run:  Build/MacCurveGpu.app/Contents/MacOS/* -curvegpu \
    ///           -curvegpuOut /abs/path.txt -screen-width 1280 -screen-height 800 \
    ///           -screen-fullscreen 0
    /// The player writes the report and exits with the verdict as exit code.
    /// </summary>
    public static class CurveGpuCostCI
    {
        public static string BuildMac()
        {
            PlayerSettings.enableFrameTimingStats = true;
            var options = new BuildPlayerOptions
            {
                //the VirtualList demo scene, not ValidationScene: the latter carries a
                //component the player build refuses ("script class layout is
                //incompatible"), and the bench self-boots via -curvegpu in any
                //scene that hosts a GRoot
                scenes = new[] { "Assets/Examples/Scenes/Example 15 - VirtualList.unity" },
                locationPathName = "Build/MacCurveGpu.app",
                target = BuildTarget.StandaloneOSX,
                options = BuildOptions.Development,
            };
            var report = BuildPipeline.BuildPlayer(options);
            return report.summary.result + " size=" + report.summary.totalSize
                + " errors=" + report.summary.totalErrors;
        }
    }
}
