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
/// Behavioural gate for the two source generators (Observable + Bind; the
/// FuiView twin retired 2026-08-08 in favor of the bake-time facade), one
/// check per defect the 2026-08-01 S2/S5 re-audit confirmed — a regression
/// here reads as the original bug. Runs the REAL generators through
/// CSharpGeneratorDriver against stub FairyGUI/Mvvm types.
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

        //--- D1-D7: KeyedListDiffer semantics (audit: shipped with zero tests) --
        {
            var diff = new FairyGUI.Mvvm.KeyedListDiffer<(int id, string text), int>(x => x.id);
            var list = new List<(int id, string text)> { (1, "a"), (2, "b"), (3, "c") };
            var hits = new List<int>();
            int n1 = diff.Apply(list, i => hits.Add(i));
            Check("d1.first Apply renders every index", n1 == 3 && string.Join(",", hits) == "0,1,2");

            hits.Clear();
            Check("d2.clean second Apply renders nothing", diff.Apply(list, i => hits.Add(i)) == 0 && hits.Count == 0);

            list[1] = (9, "b2");
            hits.Clear();
            Check("d3.one key change renders exactly that index",
                diff.Apply(list, i => hits.Add(i)) == 1 && string.Join(",", hits) == "1");

            list.Add((4, "d"));
            hits.Clear();
            Check("d4.count change falls back to a full pass",
                diff.Apply(list, i => hits.Add(i)) == 4 && hits.Count == 4);

            //V11 regression: render throws -> the OLD key must survive so the
            //next Apply retries; bookkeeping-first marked the row clean forever
            list[2] = (7, "boom");
            bool threw = false;
            try { diff.Apply(list, i => throw new InvalidOperationException("render failed")); }
            catch (InvalidOperationException) { threw = true; }
            hits.Clear();
            Check("d5.render throw keeps the old key and retries (V11)",
                threw && diff.Apply(list, i => hits.Add(i)) == 1 && string.Join(",", hits) == "2");

            list[0] = (8, "a2");
            diff.Record(list);
            hits.Clear();
            Check("d6.Record marks clean without rendering", diff.Apply(list, i => hits.Add(i)) == 0);

            diff.Reset();
            hits.Clear();
            Check("d7.Reset forces a full re-render", diff.Apply(list, i => hits.Add(i)) == 4);
        }

        Console.WriteLine($"RESULT pass={pass} fail={fail}");
        Console.Write(Log.ToString());
        Environment.Exit(fail == 0 ? 0 : 1);
    }

}
