using System;

namespace FairyGUI.Mvvm
{
    /// <summary>
    /// Marks a field of a partial ViewModel subclass for property generation. The source
    /// generator emits a property with an equality-guarded setter that calls MarkDirty,
    /// plus a "{Name}Property" index constant for binding registration.
    /// Field naming: _camelCase or m_camelCase.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class ObservableAttribute : Attribute
    {
    }
}
