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
                        };
                    })
                .Collect();

            var files = context.AdditionalTextsProvider
                .Where(static t => t.Path.EndsWith(".bytes", StringComparison.OrdinalIgnoreCase)
                                || t.Path.EndsWith(".fui", StringComparison.OrdinalIgnoreCase))
                .Select(static (t, _) => t.Path)
                .Collect();

            context.RegisterSourceOutput(views.Combine(files), static (spc, pair) =>
            {
                foreach (var view in pair.Left)
                    Emit(spc, view, pair.Right);
            });
        }

        sealed class ViewInfo
        {
            public string ns;
            public string className;
            public string packageName;
            public string componentName;
            public bool isPartial;
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

        static void Emit(SourceProductionContext spc, ViewInfo view, IReadOnlyList<string> files)
        {
            if (view.packageName == null || view.componentName == null)
                return;

            if (!view.isPartial)
            {
                spc.ReportDiagnostic(Diagnostic.Create(NotPartial, Location.None, view.className));
                return;
            }

            FuiPackage pkg = null;
            foreach (var path in files)
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
                string found = string.Join(", ", files.Select(Path.GetFileName));
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
                if (child.name == null || !IsValidIdentifier(child.name) || !used.Add(child.name))
                    continue;
                fields.Add((child.name, ResolveClass(pkg, child)));
            }

            foreach (var f in fields)
                sb.Append(ind).Append("    public global::FairyGUI.").Append(f.type).Append(' ').Append(f.name).AppendLine(";");

            sb.Append(ind).AppendLine("    public void Bind(global::FairyGUI.GComponent component)");
            sb.Append(ind).AppendLine("    {");
            sb.Append(ind).AppendLine("        root = component;");
            foreach (var f in fields)
                sb.Append(ind).Append("        ").Append(f.name).Append(" = (global::FairyGUI.").Append(f.type)
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
