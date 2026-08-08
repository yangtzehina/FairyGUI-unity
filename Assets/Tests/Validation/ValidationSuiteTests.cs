#if UNITY_2020_1_OR_NEWER
using System;
using System.Collections;
using NUnit.Framework;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

/// <summary>
/// Unity Test Framework face of the validation suites: one PlayMode test per
/// suite, the fixture instantiated once per backend (vertex-stream / buffer),
/// 44 entries total wrapping the same checks InstancedValidationAll runs.
///
/// The suites themselves are unchanged — each builds its own
/// InstancedValidationEnv, runs synchronously (Stage.ForceUpdate frame
/// driving) and returns a "RESULT pass=N fail=N" report; the wrapper parses
/// the head line and fails the test with the FULL report attached, so a red
/// test carries the same per-check diagnostics the eval runners print.
///
/// This is the PORTABLE runner: the Test Runner window, `Unity -runTests
/// -testPlatform PlayMode` on any CI, and FairyGUIEditor.ValidationTestRunnerCI
/// (in-editor, result file + verdict line) all execute these. The legacy
/// entries (InstancedValidationAll via eval, InstancedValidationCI batchmode)
/// stay for the ordered full-sweep form; suites must not depend on order —
/// UTF runs them alphabetically per fixture.
///
/// The wall-clock perf RATIO gates are deliberately NOT here: they need a
/// fresh process (Validation/README.md) and stay on InstancedPerfCI.
/// </summary>
//the namespace is load-bearing: Unity's bundled NUnit builds a
//ParameterizedFixtureSuite with the namespace as the parent suite name and
//throws ArgumentNullException for global-namespace parametrized fixtures
namespace FairyGUI.ValidationTests
{
[TestFixture(true)]
[TestFixture(false)]
public class ValidationSuites
{
    readonly bool _vertexBackend;

    public ValidationSuites(bool vertexBackend)
    {
        _vertexBackend = vertexBackend;
    }

    static bool sSceneLoaded;
    bool _savedBackend;

    [UnitySetUp]
    public IEnumerator SetUp()
    {
        if (!sSceneLoaded)
        {
            sSceneLoaded = true;
#if UNITY_EDITOR
            //the CI fixture scene (a camera over a dark ground, no scripts);
            //not in build settings, so the editor-only loader is the way in.
            //A player test build would run in its default scene instead —
            //the env bootstraps Stage/GRoot itself either way
            UnityEditor.SceneManagement.EditorSceneManager.LoadSceneInPlayMode(
                "Assets/Examples/InstancedPoC/Validation/ValidationScene.unity",
                new LoadSceneParameters(LoadSceneMode.Single));
            yield return null;
#endif
        }
        _savedBackend = InstancedValidationEnv.useVertexBackend;
        InstancedValidationEnv.useVertexBackend = _vertexBackend;
        yield return null;
    }

    [TearDown]
    public void TearDown()
    {
        InstancedValidationEnv.useVertexBackend = _savedBackend;
    }

    static void RunSuite(Func<string> suite)
    {
        string report = suite();
        int nl = report.IndexOf('\n');
        string head = nl >= 0 ? report.Substring(0, nl) : report;
        int pass = 0, fail = 1;
        foreach (var token in head.Split(' '))
        {
            if (token.StartsWith("pass=")) int.TryParse(token.Substring(5), out pass);
            if (token.StartsWith("fail=")) int.TryParse(token.Substring(5), out fail);
        }
        Assert.That(fail, Is.Zero, report);
        Assert.That(pass, Is.GreaterThan(0), "suite produced no checks:\n" + report);
    }

    //regression order lives in InstancedValidationAll; here the names carry it
    [Test] public void m1_reassembler() => RunSuite(InstancedReassemblerSuite.Run);
    [Test] public void m3_clipstack() => RunSuite(InstancedClipStackSuite.Run);
    [Test] public void scope_barriers() => RunSuite(InstancedScopeBarrierSuite.Run);
    [Test] public void colorfilter_leaf() => RunSuite(InstancedColorFilterSuite.Run);
    [Test] public void m7_sdf() => RunSuite(InstancedM7SdfSuite.Run);
    [Test] public void m4_scenarios() => RunSuite(M4ScenarioSuite.Run);
    [Test] public void batch1() => RunSuite(InstancedBatch1Suite.Run);
    [Test] public void batch2() => RunSuite(InstancedBatch2Suite.Run);
    [Test] public void batch3() => RunSuite(InstancedBatch3Suite.Run);
    [Test] public void batch3d() => RunSuite(InstancedBatch3dSuite.Run);
    [Test] public void batch4() => RunSuite(InstancedBatch4Suite.Run);
    [Test] public void batch5_curvetext() => RunSuite(InstancedBatch5Suite.Run);
    [Test] public void m8_1_blob() => RunSuite(InstancedM81Suite.Run);
    [Test] public void m8_2_mount() => RunSuite(InstancedM82Suite.Run);
    [Test] public void m8_4_tiers() => RunSuite(InstancedM84Suite.Run);
    [Test] public void m8_5_renderless() => RunSuite(InstancedM85Suite.Run);
    [Test] public void m8_automount() => RunSuite(FqsAutoMountSuite.Run);
    [Test] public void m8_superset() => RunSuite(FqsSupersetSuite.Run);
    [Test] public void curve_effects() => RunSuite(CurveEffectsSuite.Run);
    [Test] public void perf_invariants() => RunSuite(InstancedPerfInvariantSuite.Run);
    [Test] public void event_semantics() => RunSuite(EventSemanticsSuite.Run);
    [Test] public void mvvm_reentrancy() => RunSuite(BinderReentrancyCheck.Run);
}
}
#endif
