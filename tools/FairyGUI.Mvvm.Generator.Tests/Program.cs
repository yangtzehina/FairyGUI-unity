using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using FairyGUI.Mvvm.Generator;

/// <summary>
/// Behavioural gate for the three source generators, one check per defect the
/// 2026-08-01 S2/S5 re-audit confirmed — a regression here reads as the original
/// bug. Runs the REAL generators through CSharpGeneratorDriver against stub
/// FairyGUI/Mvvm types plus the repository's actual Basics package bytes.
/// Prints "RESULT pass=N fail=N" and exits non-zero on failure, matching the
/// repository's other gates.
///
///   ~/.dotnet/dotnet run -c Release --project tools/FairyGUI.Mvvm.Generator.Tests
/// </summary>
static class Program
{
    // minimal mirrors of the runtime types the generators inspect BY NAME —
    // display-string comparisons only, so stubs with the right full names suffice
    const string Stubs = @"
namespace System.Runtime.CompilerServices { class IsExternalInit {} }
namespace FairyGUI
{
    public class GObject { public bool visible; }
    public class GComponent : GObject { public GObject GetChild(string name) => null; }
    public class GTextField : GObject { public string text; }
    public class TextField { public string text; }
    public class GProgressBar : GObject { public double value; }
    public class GSlider : GObject { public double value; }
    public class GList : GComponent {}
    public class GButton : GComponent {}
    public class GImage : GObject {}
    public class GGraph : GObject {}
    public class GLoader : GObject {}
    public class GGroup : GObject {}
    public class GRichTextField : GTextField {}
    public class GTextInput : GTextField {}
    public class GLabel : GComponent {}
    public class GComboBox : GComponent {}
    public class GScrollBar : GComponent {}
    public class GTree : GList {}
    public class GLoader3D : GObject {}
    public class GMovieClip : GObject {}
    public static class TextFieldExtensions { public static void SetIntText(GTextField t, long v) {} }
    public static class UIPackage { public static object CreateObject(string p, string c) => null; }
}
namespace FairyGUI.Mvvm
{
    public class ViewModel { protected void MarkDirty(int i) {} }
    public class Binder { public Binder Bind(ViewModel vm, int i, System.Action a) => this; }
    [System.AttributeUsage(System.AttributeTargets.Field)] public sealed class ObservableAttribute : System.Attribute {}
    [System.AttributeUsage(System.AttributeTargets.Class)] public sealed class BindContextAttribute : System.Attribute { public BindContextAttribute(System.Type t) {} }
    [System.AttributeUsage(System.AttributeTargets.Field | System.AttributeTargets.Method)] public sealed class BindAttribute : System.Attribute { public BindAttribute(string p) {} }
    [System.AttributeUsage(System.AttributeTargets.Class)] public sealed class FuiViewAttribute : System.Attribute { public FuiViewAttribute(string p, string c) {} }
}";

    static int pass, fail;
    static readonly StringBuilder Log = new StringBuilder();

    static void Check(string name, bool ok, string detail = null)
    {
        if (ok) pass++; else fail++;
        Log.Append(ok ? "PASS " : "FAIL ").Append(name);
        if (!ok && detail != null) Log.Append("  <- ").Append(detail);
        Log.AppendLine();
    }

    sealed class MemAdditionalText : AdditionalText
    {
        readonly string _path; readonly byte[] _bytes;
        public MemAdditionalText(string path, byte[] bytes) { _path = path; _bytes = bytes; }
        public override string Path => _path;
        public override SourceText GetText(CancellationToken ct = default)
            => SourceText.From(_bytes, _bytes.Length, canBeEmbedded: false, throwIfBinaryDetected: false);
    }

    static CSharpCompilation Compile(params string[] sources)
    {
        var refs = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(typeof(object).Assembly.Location), "System.Runtime.dll")),
        };
        var trees = new List<SyntaxTree> { CSharpSyntaxTree.ParseText(Stubs, path: "Stubs.cs") };
        for (int i = 0; i < sources.Length; i++)
            trees.Add(CSharpSyntaxTree.ParseText(sources[i], path: $"User{i}.cs"));
        return CSharpCompilation.Create("Test", trees, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    static (string generated, ImmutableArray<Diagnostic> diags) Run(
        IIncrementalGenerator gen, CSharpCompilation comp, params AdditionalText[] extras)
    {
        var driver = CSharpGeneratorDriver.Create(
            new[] { gen.AsSourceGenerator() },
            additionalTexts: extras.Length > 0 ? extras : null);
        driver.RunGeneratorsAndUpdateCompilation(comp, out var updated, out var diags);
        var generated = string.Join("\n// ---- next file ----\n",
            updated.SyntaxTrees.Skip(comp.SyntaxTrees.Length).Select(t => t.ToString()));
        return (generated, diags);
    }

    static void Main()
    {
        string repo = FindRepoRoot();
        byte[] basics = File.ReadAllBytes(System.IO.Path.Combine(
            repo, "Assets/Examples/Resources/UI/Basics_fui.bytes"));

        //the generator reads package BYTES from the AdditionalText's disk path
        //(AdditionalText.GetText mangles binary), so the tests must supply real
        //files — an in-memory path silently turns every case into FGM204
        string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "fgm-gen-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        string basicsPath = System.IO.Path.Combine(tmp, "Basics_fui.bytes");
        File.WriteAllBytes(basicsPath, basics);

        //--- G1: [BindContext] on a nested class -> FGM105, not a stray type ----
        {
            var (gen, diags) = Run(new BindGenerator(), Compile(@"
using FairyGUI.Mvvm;
public partial class VM1 : ViewModel { [Observable] int _hp; }
public partial class Window1 { [BindContext(typeof(VM1))] public partial class Inner { [Bind(""Hp"")] FairyGUI.GTextField _hpText; } }"));
            Check("g1.nested [BindContext] reports FGM105",
                diags.Any(d => d.Id == "FGM105"), string.Join("; ", diags.Select(d => d.Id)));
            Check("g1.and emits nothing at namespace scope", !gen.Contains("partial class Inner"));
        }

        //--- G2: [Bind] members inherited from a base view class -----------------
        {
            var (gen, diags) = Run(new BindGenerator(), Compile(@"
using FairyGUI.Mvvm;
public partial class VM2 : ViewModel { [Observable] string _title; }
public class PanelBase { [Bind(""Title"")] protected FairyGUI.GTextField _titleText; }
[BindContext(typeof(VM2))] public partial class Panel2 : PanelBase { }"));
            Check("g2.base-class [Bind] field is wired",
                gen.Contains("_titleText.text = vm.Title"), gen);
            Check("g2.no diagnostics", !diags.Any(d => d.Severity == DiagnosticSeverity.Error),
                string.Join("; ", diags.Select(d => d.Id)));
        }

        //--- G2b: a PRIVATE base [Bind] member is reported, not silently dropped -
        {
            var (_, diags) = Run(new BindGenerator(), Compile(@"
using FairyGUI.Mvvm;
public partial class VM2b : ViewModel { [Observable] string _title; }
public class PanelBase2 { [Bind(""Title"")] FairyGUI.GTextField _titleText; }
[BindContext(typeof(VM2b))] public partial class Panel2b : PanelBase2 { }"));
            Check("g2b.private base [Bind] member reports FGM106",
                diags.Any(d => d.Id == "FGM106"), string.Join("; ", diags.Select(d => d.Id)));
        }

        //--- G3: bool property + GTextField field -> .visible (was unreachable) --
        {
            var (gen, diags) = Run(new BindGenerator(), Compile(@"
using FairyGUI.Mvvm;
public partial class VM3 : ViewModel { [Observable] bool _busy; }
[BindContext(typeof(VM3))] public partial class Panel3
{
    [Bind(""Busy"")] FairyGUI.GTextField _busyLabel;
    [Bind(""Busy"")] FairyGUI.GProgressBar _busyBar;
}"));
            Check("g3.bool -> GTextField.visible", gen.Contains("_busyLabel.visible = vm.Busy"), gen);
            Check("g3.bool -> GProgressBar.visible", gen.Contains("_busyBar.visible = vm.Busy"), gen);
            Check("g3.no FGM103", !diags.Any(d => d.Id == "FGM103"),
                string.Join("; ", diags.Select(d => d.ToString())));
        }

        //--- G4: .fui content change re-runs the pipeline ------------------------
        //THE SAME DRIVER, incrementally: that is where the defect lived. The old
        //pipeline carried only Path, so replacing the additional text with new
        //content at the same path left the pipeline value unchanged and Roslyn
        //served Emit from cache — the generated view silently went stale. The
        //corruption targets the HEADER so the change is guaranteed visible (a
        //flipped byte in the middle can land in a segment the reader never
        //parses, which is a test bug, not a pass).
        {
            var compilation = Compile(@"
using FairyGUI.Mvvm;
[FuiView(""Basics"", ""Demo_Button"")] public partial class ButtonView {}");
            var a = new MemAdditionalText(basicsPath, basics);
            GeneratorDriver driver = CSharpGeneratorDriver.Create(
                new[] { new FuiViewGenerator().AsSourceGenerator() },
                additionalTexts: new AdditionalText[] { a });
            driver = driver.RunGenerators(compilation);
            var run1 = driver.GetRunResult();
            bool viewGenerated = run1.GeneratedTrees.Any(t => t.ToString().Contains("partial class ButtonView"));

            byte[] corrupt = (byte[])basics.Clone();
            corrupt[0] ^= 0xFF; //header: the reader cannot miss this
            File.WriteAllBytes(basicsPath, corrupt);
            File.SetLastWriteTimeUtc(basicsPath, DateTime.UtcNow.AddSeconds(7));
            driver = driver.ReplaceAdditionalText(a, new MemAdditionalText(basicsPath, corrupt));
            driver = driver.RunGenerators(compilation);
            var run2 = driver.GetRunResult();
            bool sawNewContent = run2.Diagnostics.Any(d => d.Id == "FGM204" || d.Id == "FGM202");

            File.WriteAllBytes(basicsPath, basics); //restore for later cases
            File.SetLastWriteTimeUtc(basicsPath, DateTime.UtcNow.AddSeconds(14));

            Check("g4.pristine package generated a view", viewGenerated);
            Check("g4.incremental re-run SEES the content change (no stale cache)",
                sawNewContent, string.Join("; ", run2.Diagnostics.Select(d => d.Id).Distinct()));
        }

        //--- G5: a child named 'component' cannot shadow the Bind parameter ------
        {
            var (gen, _) = Run(new FuiViewGenerator(), Compile(@"
using FairyGUI.Mvvm;
[FuiView(""Basics"", ""Demo_Button"")] public partial class ButtonView2 {}"),
                new MemAdditionalText(basicsPath, basics));
            Check("g5.field writes are this.-qualified",
                gen.Contains("this.root = component;") && !gen.Contains("\n        root = component;"), gen);
        }

        //--- G6: invalid child names produce FGM205, and exist in Basics today ---
        {
            var (_, diags) = Run(new FuiViewGenerator(), Compile(@"
using FairyGUI.Mvvm;
[FuiView(""Basics"", ""Main"")] public partial class MainView {}"),
                new MemAdditionalText(basicsPath, basics));
            int skipped = diags.Count(d => d.Id == "FGM205");
            Check($"g6.skipped children are reported (FGM205 x{skipped})", skipped > 0,
                string.Join("; ", diags.Select(d => d.Id).Distinct()));
        }

        //--- G7: nested [FuiView] refuses; duplicate attribute does not abort ----
        {
            var (gen, diags) = Run(new FuiViewGenerator(), Compile(@"
using FairyGUI.Mvvm;
public partial class Screen7 { [FuiView(""Basics"", ""Demo_Button"")] public partial class Nested7 {} }"),
                new MemAdditionalText(basicsPath, basics));
            Check("g7.nested [FuiView] reports FGM206",
                diags.Any(d => d.Id == "FGM206"), string.Join("; ", diags.Select(d => d.Id)));
            Check("g7.and emits nothing", gen.Trim().Length == 0, gen);
        }
        {
            var (gen, diags) = Run(new FuiViewGenerator(), Compile(@"
using FairyGUI.Mvvm;
[FuiView(""Basics"", ""Demo_Button"")] public partial class Twice {}
[FuiView(""Basics"", ""Demo_Checkbox"")] public partial class Twice {}"),
                new MemAdditionalText(basicsPath, basics));
            Check("g7b.duplicate view emits once + FGM207 instead of aborting",
                diags.Any(d => d.Id == "FGM207")
                && gen.Split(new[] { "partial class Twice" }, StringSplitOptions.None).Length == 2,
                string.Join("; ", diags.Select(d => d.Id)));
        }

        //--- G8: [Observable] _2ndSlot -> FGM004, output still parses ------------
        {
            var (gen, diags) = Run(new ObservableGenerator(), Compile(@"
using FairyGUI.Mvvm;
public partial class VM8 : ViewModel { [Observable] int _2ndSlot; [Observable] int _hp; }"));
            Check("g8.leading-digit field reports FGM004",
                diags.Any(d => d.Id == "FGM004"), string.Join("; ", diags.Select(d => d.Id)));
            var tree = CSharpSyntaxTree.ParseText(gen);
            Check("g8.generated file still parses (other properties survive)",
                !tree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error)
                && gen.Contains("HpProperty"), gen);
        }

        //--- G9: cross-file partial indices ignore whitespace shifts -------------
        {
            string fileA = "using FairyGUI.Mvvm;\npublic partial class VM9 : ViewModel { [Observable] int _zzz; }";
            string fileB = "using FairyGUI.Mvvm;\npublic partial class VM9 : ViewModel { [Observable] int _aaa; }";
            var (gen1, _) = Run(new ObservableGenerator(), Compile(fileA, fileB));
            //push fileA's span far past fileB's: under raw-offset sorting this
            //reorders the fields and renumbers every constant
            var (gen2, _) = Run(new ObservableGenerator(),
                Compile(new string('\n', 300) + fileA, fileB));
            string N(string g) => string.Join(",",
                g.Split('\n').Where(l => l.Contains("Property = ")).Select(l => l.Trim()));
            Check("g9.whitespace in one file does not renumber constants",
                N(gen1) == N(gen2), N(gen1) + "  vs  " + N(gen2));
        }

        Console.WriteLine($"RESULT pass={pass} fail={fail}");
        Console.Write(Log.ToString());
        Environment.Exit(fail == 0 ? 0 : 1);
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(System.IO.Path.Combine(dir.FullName, "AGENTS.md")))
            dir = dir.Parent;
        if (dir == null)
            throw new InvalidOperationException("repo root not found");
        return dir.FullName;
    }
}
