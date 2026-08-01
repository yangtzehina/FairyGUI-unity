using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FairyGUI.Mvvm.Generator
{
    /// <summary>
    /// Emits strongly-typed view classes from real .fui packages supplied as
    /// AdditionalFiles: one typed field per named child, Bind(GComponent) wiring them via
    /// GetChild, and a Create() factory. Renaming a child in the FairyGUI editor becomes
    /// a compile error instead of a null at runtime.
    /// </summary>
    [Generator]
    public sealed class FuiViewGenerator : IIncrementalGenerator
    {
        static readonly DiagnosticDescriptor NotPartial = new DiagnosticDescriptor(
            "FGM201", "Class must be partial",
            "Class '{0}' has [FuiView] and must be declared partial", "FairyGUI.Mvvm",
            DiagnosticSeverity.Error, true);

        static readonly DiagnosticDescriptor PackageNotFound = new DiagnosticDescriptor(
            "FGM202", "Package not found",
            "No .fui AdditionalFile contains package '{0}' (add /additionalfile:<path> to csc.rsp; found: {1})", "FairyGUI.Mvvm",
            DiagnosticSeverity.Error, true);

        static readonly DiagnosticDescriptor ComponentNotFound = new DiagnosticDescriptor(
            "FGM203", "Component not found",
            "Package '{0}' has no component named '{1}'", "FairyGUI.Mvvm",
            DiagnosticSeverity.Error, true);

        static readonly DiagnosticDescriptor ParseFailed = new DiagnosticDescriptor(
            "FGM204", "Package parse failed",
            "Failed to parse '{0}': {1}", "FairyGUI.Mvvm",
            DiagnosticSeverity.Error, true);

        static readonly DiagnosticDescriptor ChildSkipped = new DiagnosticDescriptor(
            "FGM205", "Child not representable",
            "Component '{0}': child '{1}' gets no typed field ({2}); rename it in the FairyGUI editor to bind it", "FairyGUI.Mvvm",
            DiagnosticSeverity.Warning, true);

        static readonly DiagnosticDescriptor Unsupported = new DiagnosticDescriptor(
            "FGM206", "Unsupported declaration",
            "Class '{0}' is nested or generic; [FuiView] supports top-level non-generic classes", "FairyGUI.Mvvm",
            DiagnosticSeverity.Error, true);

        static readonly DiagnosticDescriptor DuplicateView = new DiagnosticDescriptor(
            "FGM207", "Duplicate [FuiView]",
            "[FuiView] appears more than once for '{0}'; only the first is generated", "FairyGUI.Mvvm",
            DiagnosticSeverity.Warning, true);

        static readonly ConcurrentDictionary<string, (DateTime stamp, FuiPackage pkg, string error)> sCache
            = new ConcurrentDictionary<string, (DateTime, FuiPackage, string)>();

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var views = context.SyntaxProvider
                .ForAttributeWithMetadataName(
                    "FairyGUI.Mvvm.FuiViewAttribute",
                    static (node, _) => node is ClassDeclarationSyntax,
                    static (ctx, _) =>
                    {
                        var type = (INamedTypeSymbol)ctx.TargetSymbol;
                        var args = ctx.Attributes[0].ConstructorArguments;
                        bool partial = type.DeclaringSyntaxReferences.All(r =>
                            r.GetSyntax() is ClassDeclarationSyntax c && c.Modifiers.Any(SyntaxKind.PartialKeyword));
                        return new ViewInfo
                        {
                            ns = type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString(),
                            className = type.Name,
                            packageName = args.Length > 0 ? args[0].Value as string : null,
                            componentName = args.Length > 1 ? args[1].Value as string : null,
                            isPartial = partial,
                            isNestedOrGeneric = type.ContainingType != null || type.IsGenericType,
                        };
                    })
                .Collect();

            //the pipeline value must CHANGE when the .fui's CONTENT changes, or
            //Roslyn serves the whole downstream from cache and the generated view
            //goes stale while the package moves on — the mtime cache in
            //LoadPackage never even runs, because Emit itself is never re-invoked.
            //Path alone (the original value here) has exactly that failure.
            var files = context.AdditionalTextsProvider
                .Where(static t => t.Path.EndsWith(".bytes", StringComparison.OrdinalIgnoreCase)
                                || t.Path.EndsWith(".fui", StringComparison.OrdinalIgnoreCase))
                .Select(static (t, ct) => (path: t.Path, version: ContentVersion(t, ct)))
                .Collect();

            context.RegisterSourceOutput(views.Combine(files), static (spc, pair) =>
            {
                //dedup: [FuiView] on two partial parts of one class yields the
                //symbol twice, and a duplicate AddSource hint name would throw and
                //abort the entire generator (every view in the compilation)
                var seen = new HashSet<string>();
                foreach (var view in pair.Left)
                {
                    string key = view.ns + "." + view.className;
                    if (!seen.Add(key))
                    {
                        spc.ReportDiagnostic(Diagnostic.Create(DuplicateView, Location.None, key));
                        continue;
                    }
                    Emit(spc, view, pair.Right);
                }
            });
        }

        /// <summary>
        /// A value that moves when the file's bytes move. NOT AdditionalText.
        /// GetText: the real csc host DIAGNOSES CS2015 on a binary additional
        /// file the moment GetText runs — a test host decodes it quietly, which
        /// is how that call survived to a Unity compile before failing. .fui IS
        /// binary, so hash the bytes directly (the generator already reads this
        /// same file in LoadPackage; deliberate, per the csproj note on RS1035).
        /// </summary>
        static string ContentVersion(AdditionalText t, System.Threading.CancellationToken ct)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(t.Path);
                ulong h = 0xcbf29ce484222325UL;
                for (int i = 0; i < bytes.Length; i++)
                    h = (h ^ bytes[i]) * 0x100000001b3UL;
                return bytes.Length + ":" + h.ToString("x16");
            }
            catch
            {
                //unreadable now != unreadable before: fold in time so the state
                //is not sticky across the moment the file becomes readable
                var fi = new FileInfo(t.Path);
                return fi.Exists ? "unreadable:" + fi.Length + ":" + fi.LastWriteTimeUtc.Ticks : "missing";
            }
        }

        sealed class ViewInfo
        {
            public string ns;
            public string className;
            public string packageName;
            public string componentName;
            public bool isPartial;
            public bool isNestedOrGeneric;
        }

        static FuiPackage LoadPackage(SourceProductionContext spc, string path)
        {
            DateTime stamp;
            try { stamp = File.GetLastWriteTimeUtc(path); }
            catch { stamp = DateTime.MinValue; }

            var entry = sCache.GetOrAdd(path, _ => Parse(path, stamp));
            if (entry.stamp != stamp)
            {
                entry = Parse(path, stamp);
                sCache[path] = entry;
            }

            if (entry.error != null)
                spc.ReportDiagnostic(Diagnostic.Create(ParseFailed, Location.None, path, entry.error));
            return entry.pkg;
        }

        static (DateTime, FuiPackage, string) Parse(string path, DateTime stamp)
        {
            try
            {
                return (stamp, FuiReader.Parse(File.ReadAllBytes(path)), null);
            }
            catch (Exception e)
            {
                return (stamp, null, e.Message);
            }
        }

        static void Emit(SourceProductionContext spc, ViewInfo view, IReadOnlyList<(string path, string version)> files)
        {
            if (view.packageName == null || view.componentName == null)
                return;

            if (view.isNestedOrGeneric)
            {
                //emitting would place the partial at NAMESPACE scope — a new type
                //that shadows nothing and completes nothing (same guard as FGM005)
                spc.ReportDiagnostic(Diagnostic.Create(Unsupported, Location.None, view.className));
                return;
            }

            if (!view.isPartial)
            {
                spc.ReportDiagnostic(Diagnostic.Create(NotPartial, Location.None, view.className));
                return;
            }

            FuiPackage pkg = null;
            foreach (var (path, _) in files)
            {
                var p = LoadPackage(spc, path);
                if (p != null && p.name == view.packageName)
                {
                    pkg = p;
                    break;
                }
            }

            if (pkg == null)
            {
                string found = string.Join(", ", files.Select(f => Path.GetFileName(f.path)));
                spc.ReportDiagnostic(Diagnostic.Create(PackageNotFound, Location.None, view.packageName,
                    found.Length > 0 ? found : "none"));
                return;
            }

            var comp = pkg.FindComponent(view.componentName);
            if (comp == null)
            {
                spc.ReportDiagnostic(Diagnostic.Create(ComponentNotFound, Location.None, pkg.name, view.componentName));
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated by FairyGUI.Mvvm.Generator (FuiView)/>");
            bool hasNs = view.ns.Length > 0;
            if (hasNs)
            {
                sb.Append("namespace ").AppendLine(view.ns);
                sb.AppendLine("{");
            }
            string ind = hasNs ? "    " : "";

            sb.Append(ind).Append("partial class ").AppendLine(view.className);
            sb.Append(ind).AppendLine("{");
            sb.Append(ind).AppendLine("    public global::FairyGUI.GComponent root;");

            var used = new HashSet<string> { "root", "Bind", "Create", view.className };
            var fields = new List<(string name, string type)>();
            foreach (var child in comp.children)
            {
                //silently dropping a child makes 'the field does not exist' look
                //like 'the child does not exist' — report each skip and why (this
                //repo's own Basics package has two children this fires on today)
                if (child.name == null || child.name.Length == 0)
                    continue; //unnamed children are unaddressable by design
                if (!IsValidIdentifier(child.name))
                {
                    spc.ReportDiagnostic(Diagnostic.Create(ChildSkipped, Location.None,
                        comp.name, child.name, "not a valid C# identifier"));
                    continue;
                }
                if (!used.Add(child.name))
                {
                    spc.ReportDiagnostic(Diagnostic.Create(ChildSkipped, Location.None,
                        comp.name, child.name, "name collides with a generated member or an earlier child"));
                    continue;
                }
                fields.Add((child.name, ResolveClass(pkg, child)));
            }

            foreach (var f in fields)
                sb.Append(ind).Append("    public global::FairyGUI.").Append(f.type).Append(' ').Append(f.name).AppendLine(";");

            sb.Append(ind).AppendLine("    public void Bind(global::FairyGUI.GComponent component)");
            sb.Append(ind).AppendLine("    {");
            //'this.' on every field write: a child named 'component' generates a
            //field that the PARAMETER would otherwise shadow, silently mis-binding
            //every assignment after it (the simple name binds to the parameter)
            sb.Append(ind).AppendLine("        this.root = component;");
            foreach (var f in fields)
                sb.Append(ind).Append("        this.").Append(f.name).Append(" = (global::FairyGUI.").Append(f.type)
                  .Append(")component.GetChild(\"").Append(f.name).AppendLine("\");");
            sb.Append(ind).AppendLine("    }");

            sb.Append(ind).Append("    public static ").Append(view.className).AppendLine(" Create()");
            sb.Append(ind).AppendLine("    {");
            sb.Append(ind).Append("        var view = new ").Append(view.className).AppendLine("();");
            sb.Append(ind).Append("        view.Bind((global::FairyGUI.GComponent)global::FairyGUI.UIPackage.CreateObject(\"")
              .Append(pkg.name).Append("\", \"").Append(comp.name).AppendLine("\"));");
            sb.Append(ind).AppendLine("        return view;");
            sb.Append(ind).AppendLine("    }");

            sb.Append(ind).AppendLine("}");
            if (hasNs)
                sb.AppendLine("}");

            spc.AddSource((hasNs ? view.ns + "." : "") + view.className + ".FuiView.g.cs", sb.ToString());
        }

        static string ResolveClass(FuiPackage pkg, FuiChild child)
        {
            int objectType = child.objectType;
            //a plain component reference takes the concrete extension type of the item it
            //instantiates (button, label, ...); cross-package references stay GComponent
            if (objectType == 9 && child.srcId != null && (child.pkgId == null || child.pkgId == pkg.id))
            {
                var referenced = pkg.FindComponentById(child.srcId);
                if (referenced != null)
                    objectType = referenced.objectType;
            }

            switch (objectType)
            {
                case 0: return "GImage";
                case 1: return "GMovieClip";
                case 2: return "GMovieClip";
                case 3: return "GGraph";
                case 4: return "GLoader";
                case 5: return "GGroup";
                case 6: return "GTextField";
                case 7: return "GRichTextField";
                case 8: return "GTextInput";
                case 10: return "GList";
                case 11: return "GLabel";
                case 12: return "GButton";
                case 13: return "GComboBox";
                case 14: return "GProgressBar";
                case 15: return "GSlider";
                case 16: return "GScrollBar";
                case 17: return "GTree";
                case 18: return "GLoader3D";
                default: return "GComponent";
            }
        }

        static bool IsValidIdentifier(string name)
        {
            if (name.Length == 0)
                return false;
            if (!char.IsLetter(name[0]) && name[0] != '_')
                return false;
            for (int i = 1; i < name.Length; i++)
            {
                if (!char.IsLetterOrDigit(name[i]) && name[i] != '_')
                    return false;
            }
            return SyntaxFacts.GetKeywordKind(name) == SyntaxKind.None;
        }
    }
}
