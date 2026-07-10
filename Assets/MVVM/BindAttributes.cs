using System;

namespace FairyGUI.Mvvm
{
    /// <summary>
    /// Declares the ViewModel type a partial view class binds against. The source
    /// generator emits BindTo(Binder, TViewModel) wiring every [Bind] member.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class BindContextAttribute : Attribute
    {
        public BindContextAttribute(Type viewModelType)
        {
        }
    }

    /// <summary>
    /// On a field: binds a UI object to a ViewModel property; the apply code is derived
    /// from the field/property types (numeric text via SetIntText, string text, bool to
    /// visible, numeric to GProgressBar/GSlider value). On a parameterless void method:
    /// the method is invoked whenever the property is dirty (escape hatch for anything
    /// the field rules cannot express).
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Method)]
    public sealed class BindAttribute : Attribute
    {
        public BindAttribute(string propertyName)
        {
        }
    }

    /// <summary>
    /// Generates a strongly-typed view for a component inside a .fui package supplied to
    /// the compiler as an AdditionalFile (Unity: csc.rsp /additionalfile:...). The
    /// generator parses the package at compile time and emits one typed field per named
    /// child plus Bind(GComponent) and Create(). Renaming a child in the FairyGUI editor
    /// then becomes a compile error instead of a silent GetChild(null).
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class FuiViewAttribute : Attribute
    {
        public FuiViewAttribute(string packageName, string componentName)
        {
        }
    }
}
