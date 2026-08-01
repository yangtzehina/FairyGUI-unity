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
    /// Emits BindTo(Binder, TViewModel) for partial classes marked
    /// [BindContext(typeof(TViewModel))]. Members marked [Bind("Prop")] are wired by
    /// type-directed rules:
    /// - GTextField/TextField field + integral property  -> SetIntText (allocation-free)
    /// - GTextField/TextField field + string property    -> .text
    /// - GProgressBar/GSlider field + numeric property   -> .value
    /// - GObject-derived field + bool property           -> .visible
    /// - parameterless void method                       -> invoked when dirty
    /// The referenced property must be an [Observable] field of the ViewModel, so a typo
    /// or a renamed property is a compile error.
    /// </summary>
    [Generator]
    public sealed class BindGenerator : IIncrementalGenerator
    {
        static readonly DiagnosticDescriptor NotPartial = new DiagnosticDescriptor(
            "FGM101", "Class must be partial",
            "Class '{0}' has [BindContext] and must be declared partial", "FairyGUI.Mvvm",
            DiagnosticSeverity.Error, true);

        static readonly DiagnosticDescriptor UnknownProperty = new DiagnosticDescriptor(
            "FGM102", "Unknown ViewModel property",
            "'{0}' is not an [Observable] property of {1}", "FairyGUI.Mvvm",
            DiagnosticSeverity.Error, true);

        static readonly DiagnosticDescriptor NoRule = new DiagnosticDescriptor(
            "FGM103", "No binding rule",
            "No binding rule for member '{0}' ({1}) with property type {2}; use a [Bind] method instead", "FairyGUI.Mvvm",
            DiagnosticSeverity.Error, true);

        static readonly DiagnosticDescriptor BadMethod = new DiagnosticDescriptor(
            "FGM104", "Invalid bind method",
            "[Bind] method '{0}' must be void and parameterless", "FairyGUI.Mvvm",
            DiagnosticSeverity.Error, true);

        static readonly DiagnosticDescriptor Unsupported = new DiagnosticDescriptor(
            "FGM105", "Unsupported declaration",
            "Class '{0}' is nested or generic; [BindContext] supports top-level non-generic classes", "FairyGUI.Mvvm",
            DiagnosticSeverity.Error, true);

        static readonly DiagnosticDescriptor Inaccessible = new DiagnosticDescriptor(
            "FGM106", "Inaccessible [Bind] member",
            "[Bind] member '{0}' on base class {1} is private; generated code in {2} cannot reach it", "FairyGUI.Mvvm",
            DiagnosticSeverity.Error, true);

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var classes = context.SyntaxProvider
                .ForAttributeWithMetadataName(
                    "FairyGUI.Mvvm.BindContextAttribute",
                    static (node, _) => node is ClassDeclarationSyntax,
                    static (ctx, _) => (INamedTypeSymbol)ctx.TargetSymbol)
                .Collect();

            context.RegisterSourceOutput(classes, static (spc, types) =>
            {
                foreach (var type in types)
                    Emit(spc, type);
            });
        }

        static void Emit(SourceProductionContext spc, INamedTypeSymbol type)
        {
            var location = type.Locations.FirstOrDefault() ?? Location.None;

            //same guard ObservableGenerator has always had (FGM005): without it the
            //partial below is emitted at NAMESPACE scope and silently declares a
            //brand-new type instead of completing the user's nested/generic one
            if (type.ContainingType != null || type.IsGenericType)
            {
                spc.ReportDiagnostic(Diagnostic.Create(Unsupported, location, type.Name));
                return;
            }

            if (!type.DeclaringSyntaxReferences.All(r =>
                    r.GetSyntax() is ClassDeclarationSyntax c && c.Modifiers.Any(SyntaxKind.PartialKeyword)))
            {
                spc.ReportDiagnostic(Diagnostic.Create(NotPartial, location, type.Name));
                return;
            }

            var contextAttr = type.GetAttributes().FirstOrDefault(a =>
                a.AttributeClass?.ToDisplayString() == "FairyGUI.Mvvm.BindContextAttribute");
            if (contextAttr == null || contextAttr.ConstructorArguments.Length == 0)
                return;
            var vmType = contextAttr.ConstructorArguments[0].Value as INamedTypeSymbol;
            if (vmType == null)
                return;

            //observable property names of the ViewModel, derived the same way
            //ObservableGenerator derives them from field names
            var vmProps = new Dictionary<string, ITypeSymbol>();
            for (var t = vmType; t != null; t = t.BaseType)
            {
                foreach (var member in t.GetMembers())
                {
                    if (member is IFieldSymbol f && f.GetAttributes().Any(a =>
                            a.AttributeClass?.ToDisplayString() == "FairyGUI.Mvvm.ObservableAttribute"))
                    {
                        string prop = DerivePropertyName(f.Name);
                        if (prop != null && !vmProps.ContainsKey(prop))
                            vmProps[prop] = f.Type;
                    }
                }
            }

            string vmFull = vmType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            var lines = new List<string>();

            //GetMembers() returns only members DECLARED on this type, so [Bind]
            //members on a base view class used to be silently skipped — BindTo
            //compiled clean with an empty body and the UI never updated. Walk the
            //base chain, derived-most first; a redeclared name shadows the base one.
            var members = new List<ISymbol>();
            var seenNames = new HashSet<string>();
            for (var t = type; t != null && t.SpecialType != SpecialType.System_Object; t = t.BaseType)
            {
                foreach (var member in t.GetMembers())
                {
                    if (!seenNames.Add(member.Name))
                        continue;
                    members.Add(member);
                }
            }

            foreach (var member in members)
            {
                var bindAttr = member.GetAttributes().FirstOrDefault(a =>
                    a.AttributeClass?.ToDisplayString() == "FairyGUI.Mvvm.BindAttribute");
                if (bindAttr == null || bindAttr.ConstructorArguments.Length == 0)
                    continue;

                //a private base member is invisible to the generated code, which
                //lives in the DERIVED class — report rather than silently skip
                //(silent skipping is exactly the defect this rewrite removes)
                if (!SymbolEqualityComparer.Default.Equals(member.ContainingType, type)
                    && member.DeclaredAccessibility == Accessibility.Private)
                {
                    spc.ReportDiagnostic(Diagnostic.Create(Inaccessible,
                        member.Locations.FirstOrDefault() ?? location,
                        member.Name, member.ContainingType.Name, type.Name));
                    continue;
                }

                string propName = bindAttr.ConstructorArguments[0].Value as string;
                if (propName == null)
                    continue;

                ITypeSymbol propType;
                if (!vmProps.TryGetValue(propName, out propType))
                {
                    spc.ReportDiagnostic(Diagnostic.Create(UnknownProperty,
                        member.Locations.FirstOrDefault() ?? location, propName, vmType.Name));
                    continue;
                }

                string indexRef = vmFull + "." + propName + "Property";

                if (member is IMethodSymbol method)
                {
                    if (!method.ReturnsVoid || method.Parameters.Length != 0)
                    {
                        spc.ReportDiagnostic(Diagnostic.Create(BadMethod,
                            method.Locations.FirstOrDefault() ?? location, method.Name));
                        continue;
                    }
                    lines.Add($"binder.Bind(vm, {indexRef}, this.{method.Name});");
                    continue;
                }

                if (member is IFieldSymbol field)
                {
                    string apply = ApplyExpression(field, propName, propType);
                    if (apply == null)
                    {
                        spc.ReportDiagnostic(Diagnostic.Create(NoRule,
                            field.Locations.FirstOrDefault() ?? location,
                            field.Name, field.Type.Name, propType.Name));
                        continue;
                    }
                    lines.Add($"binder.Bind(vm, {indexRef}, () => {apply});");
                }
            }

            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated by FairyGUI.Mvvm.Generator (Bind)/>");
            bool hasNs = !type.ContainingNamespace.IsGlobalNamespace;
            if (hasNs)
            {
                sb.Append("namespace ").AppendLine(type.ContainingNamespace.ToDisplayString());
                sb.AppendLine("{");
            }
            string ind = hasNs ? "    " : "";

            sb.Append(ind).Append("partial class ").AppendLine(type.Name);
            sb.Append(ind).AppendLine("{");
            sb.Append(ind).Append("    public void BindTo(global::FairyGUI.Mvvm.Binder binder, ").Append(vmFull).AppendLine(" vm)");
            sb.Append(ind).AppendLine("    {");
            foreach (var line in lines)
                sb.Append(ind).Append("        ").AppendLine(line);
            sb.Append(ind).AppendLine("    }");
            sb.Append(ind).AppendLine("}");
            if (hasNs)
                sb.AppendLine("}");

            string hint = (hasNs ? type.ContainingNamespace.ToDisplayString() + "." : "") + type.Name + ".Bind.g.cs";
            spc.AddSource(hint, sb.ToString());
        }

        static string ApplyExpression(IFieldSymbol field, string propName, ITypeSymbol propType)
        {
            string get = "vm." + propName;
            bool integral = propType.SpecialType == SpecialType.System_Int32
                         || propType.SpecialType == SpecialType.System_Int64
                         || propType.SpecialType == SpecialType.System_Int16
                         || propType.SpecialType == SpecialType.System_Byte;
            bool numeric = integral
                         || propType.SpecialType == SpecialType.System_Single
                         || propType.SpecialType == SpecialType.System_Double;
            bool isString = propType.SpecialType == SpecialType.System_String;
            bool isBool = propType.SpecialType == SpecialType.System_Boolean;

            //bool -> .visible is checked FIRST: every G* control derives from
            //GObject, so behind the per-control branches' early returns the rule
            //was unreachable for exactly the field types those branches match
            //(a bool bound to a GTextField/GProgressBar/GSlider always FGM103'd,
            //despite the class doc promising otherwise)
            if (isBool)
                return DerivesFrom(field.Type, "FairyGUI.GObject")
                    ? $"{field.Name}.visible = {get}"
                    : null;

            if (DerivesFrom(field.Type, "FairyGUI.GTextField") || DerivesFrom(field.Type, "FairyGUI.TextField"))
            {
                if (integral)
                    return $"global::FairyGUI.TextFieldExtensions.SetIntText({field.Name}, {get})";
                if (isString)
                    return $"{field.Name}.text = {get}";
                return $"{field.Name}.text = {get}.ToString()";
            }

            if (DerivesFrom(field.Type, "FairyGUI.GProgressBar") || DerivesFrom(field.Type, "FairyGUI.GSlider"))
            {
                if (numeric)
                    return $"{field.Name}.value = {get}";
                return null;
            }

            return null;
        }

        static bool DerivesFrom(ITypeSymbol type, string fullName)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                if (t.ToDisplayString() == fullName)
                    return true;
            }
            return false;
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
