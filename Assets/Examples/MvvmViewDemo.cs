using FairyGUI;
using FairyGUI.Mvvm;

/// <summary>
/// Strongly-typed view generated at compile time from the real VirtualList package
/// (supplied to the compiler via Assets/csc.rsp -additionalfile). Child names and types
/// come from the .fui itself: renaming a child in the FairyGUI editor breaks the build
/// here instead of returning null at runtime. Republish + touch any script to refresh.
/// </summary>
[FuiView("VirtualList", "Main")]
public partial class VirtualListMainView
{
}

public partial class MailBoxVM : ViewModel
{
    [Observable] string _title;
    [Observable] long _mailCount;
}

/// <summary>
/// [Bind] members are wired by the generated BindTo(Binder, MailBoxVM): field bindings
/// are derived from the field/property types, methods run whenever the property is dirty.
/// </summary>
[BindContext(typeof(MailBoxVM))]
public partial class MailBoxPanel
{
    [Bind("Title")] GTextField _titleText;
    [Bind("MailCount")] GTextField _countText;

    public void Attach(GTextField title, GTextField count)
    {
        _titleText = title;
        _countText = count;
    }
}
