using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FairyGUI.Mvvm.Generator
{
    /// <summary>
    /// Turns fields marked [FairyGUI.Mvvm.Observable] inside partial ViewModel classes into
    /// change-tracking properties: an equality-guarded setter that calls MarkDirty(index),
    /// plus a public "{Name}Property" index constant for allocation-free binding registration.
    /// Property indices follow field declaration order, offset by observable fields in base
    /// ViewModel classes, so inheritance chains stay consistent.
    /// </summary>
    [Generator]
    public sealed class ObservableGenerator : IIncrementalGenerator
    {
        const string AttributeName = "FairyGUI.Mvvm.ObservableAttribute";
        const string ViewModelName = "FairyGUI.Mvvm.ViewModel";

        static readonly DiagnosticDescriptor NotPartial = new DiagnosticDescriptor(
            "FGM001", "Class must be partial",
            "Class '{0}' has [Observable] fields and must be declared partial", "FairyGUI.Mvvm",
            DiagnosticSeverity.Error, true);

        static readonly DiagnosticDescriptor NotViewModel = new DiagnosticDescriptor(
            "FGM002", "Class must derive from ViewModel",
            "Class '{0}' has [Observable] fields but does not derive from FairyGUI.Mvvm.ViewModel", "FairyGUI.Mvvm",
            DiagnosticSeverity.Error, true);

        static readonly DiagnosticDescriptor TooManyProperties = new DiagnosticDescriptor(
            "FGM003", "Too many observable properties",
            "Class '{0}' exceeds 64 observable properties across its inheritance chain", "FairyGUI.Mvvm",
            DiagnosticSeverity.Error, true);

        static readonly DiagnosticDescriptor BadFieldName = new DiagnosticDescriptor(
            "FGM004", "Cannot derive property name",
            "Field '{0}': cannot derive a distinct property name (use _camelCase or m_camelCase)", "FairyGUI.Mvvm",
            DiagnosticSeverity.Error, true);

        static readonly DiagnosticDescriptor Unsupported = new DiagnosticDescriptor(
            "FGM005", "Unsupported declaration",
            "Class '{0}' is nested or generic; [Observable] supports top-level non-generic classes", "FairyGUI.Mvvm",
            DiagnosticSeverity.Error, true);

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var fields = context.SyntaxProvider
                .ForAttributeWithMetadataName(
                    AttributeName,
                    static (node, _) => node is VariableDeclaratorSyntax,
                    static (ctx, _) => (IFieldSymbol)ctx.TargetSymbol)
                .Collect();

            context.RegisterSourceOutput(fields, static (spc, fieldSymbols) =>
            {
                foreach (var classGroup in fieldSymbols.GroupBy<IFieldSymbol, INamedTypeSymbol>(
                             f => f.ContainingType, SymbolEqualityComparer.Default))
                {
                    Emit(spc, classGroup.Key, classGroup.ToList());
                }
            });
        }

        static void Emit(SourceProductionContext spc, INamedTypeSymbol type, List<IFieldSymbol> fields)
        {
            var location = type.Locations.FirstOrDefault() ?? Location.None;

            if (type.ContainingType != null || type.IsGenericType)
            {
                spc.ReportDiagnostic(Diagnostic.Create(Unsupported, location, type.Name));
                return;
            }

            if (!type.DeclaringSyntaxReferences.All(r =>
                    r.GetSyntax() is ClassDeclarationSyntax c &&
                    c.Modifiers.Any(SyntaxKind.PartialKeyword)))
            {
                spc.ReportDiagnostic(Diagnostic.Create(NotPartial, location, type.Name));
                return;
            }

            if (!DerivesFromViewModel(type))
            {
                spc.ReportDiagnostic(Diagnostic.Create(NotViewModel, location, type.Name));
                return;
            }

            //indices continue after observable fields declared in base classes
            int baseCount = CountBaseObservables(type);

            //stable order: file path FIRST, then declaration order within the
            //file. Raw span offsets compare positions from DIFFERENT files when a
            //partial class spans several — whitespace edits in one file then
            //reshuffle the interleaving, and with it every generated
            //{Name}Property constant. Path then span is deterministic and only
            //moves when a field actually moves (or a file is renamed).
            fields.Sort((a, b) =>
            {
                var la = a.Locations.FirstOrDefault();
                var lb = b.Locations.FirstOrDefault();
                int byFile = string.CompareOrdinal(
                    la?.SourceTree?.FilePath ?? "", lb?.SourceTree?.FilePath ?? "");
                if (byFile != 0)
                    return byFile;
                return (la?.SourceSpan.Start ?? 0).CompareTo(lb?.SourceSpan.Start ?? 0);
            });

            if (baseCount + fields.Count > 64)
            {
                spc.ReportDiagnostic(Diagnostic.Create(TooManyProperties, location, type.Name));
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated by FairyGUI.Mvvm.Generator/>");
            bool hasNamespace = !type.ContainingNamespace.IsGlobalNamespace;
            if (hasNamespace)
            {
                sb.Append("namespace ").AppendLine(type.ContainingNamespace.ToDisplayString());
                sb.AppendLine("{");
            }

            string indent = hasNamespace ? "    " : "";
            sb.Append(indent).Append("partial class ").AppendLine(type.Name);
            sb.Append(indent).AppendLine("{");

            for (int i = 0; i < fields.Count; i++)
            {
                var field = fields[i];
                string propName = DerivePropertyName(field.Name);
                //validity too, not just distinctness: '_2ndSlot' derives '2ndSlot',
                //which emits a file that does not PARSE — one bad field name and
                //the whole generated partial (every property) fails to compile
                if (propName == null || propName == field.Name
                    || !SyntaxFacts.IsValidIdentifier(propName))
                {
                    spc.ReportDiagnostic(Diagnostic.Create(BadFieldName,
                        field.Locations.FirstOrDefault() ?? location, field.Name));
                    continue;
                }

                string fieldType = field.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                int index = baseCount + i;

                sb.Append(indent).Append("    public const int ").Append(propName).Append("Property = ").Append(index).AppendLine(";");
                sb.Append(indent).Append("    public ").Append(fieldType).Append(' ').AppendLine(propName);
                sb.Append(indent).AppendLine("    {");
                sb.Append(indent).Append("        get { return ").Append(field.Name).AppendLine("; }");
                sb.Append(indent).AppendLine("        set");
                sb.Append(indent).AppendLine("        {");
                sb.Append(indent).Append("            if (!global::System.Collections.Generic.EqualityComparer<").Append(fieldType).Append(">.Default.Equals(").Append(field.Name).AppendLine(", value))");
                sb.Append(indent).AppendLine("            {");
                sb.Append(indent).Append("                ").Append(field.Name).AppendLine(" = value;");
                sb.Append(indent).Append("                MarkDirty(").Append(propName).AppendLine("Property);");
                sb.Append(indent).AppendLine("            }");
                sb.Append(indent).AppendLine("        }");
                sb.Append(indent).AppendLine("    }");
            }

            sb.Append(indent).Append("    public ").Append(baseCount > 0 ? "new " : "").Append("const int ObservableCount = ").Append(baseCount + fields.Count).AppendLine(";");
            sb.Append(indent).AppendLine("}");
            if (hasNamespace)
                sb.AppendLine("}");

            string hintNamespace = hasNamespace ? type.ContainingNamespace.ToDisplayString() + "." : "";
            spc.AddSource(hintNamespace + type.Name + ".Observable.g.cs", sb.ToString());
        }

        static bool DerivesFromViewModel(INamedTypeSymbol type)
        {
            for (var t = type.BaseType; t != null; t = t.BaseType)
            {
                if (t.ToDisplayString() == ViewModelName)
                    return true;
            }
            return false;
        }

        static int CountBaseObservables(INamedTypeSymbol type)
        {
            int count = 0;
            for (var t = type.BaseType; t != null && t.ToDisplayString() != ViewModelName; t = t.BaseType)
            {
                foreach (var member in t.GetMembers())
                {
                    if (member is IFieldSymbol f &&
                        f.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == AttributeName))
                        count++;
                }
            }
            return count;
        }

        static string DerivePropertyName(string fieldName)
        {
            string body = fieldName;
            if (body.StartsWith("m_", StringComparison.Ordinal))
                body = body.Substring(2);
            else if (body.StartsWith("_", StringComparison.Ordinal))
                body = body.Substring(1);

            if (body.Length == 0)
                return null;

            return char.ToUpperInvariant(body[0]) + body.Substring(1);
        }
    }
}
