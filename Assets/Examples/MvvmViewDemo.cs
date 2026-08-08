using FairyGUI;
using FairyGUI.Mvvm;

//Typed views come from ONE generator now: the bake-time facade
//(FairyGUIEditor.FqsViewGenerator via Tools/FairyGUI/Bake Packages (FQS)) —
//e.g. FairyGUI.Baked.VirtualList.MainView with typed m_ fields, page enums
//and a construction-time BakedSourceHash staleness warning. The compile-time
//Roslyn twin ([FuiView] + csc.rsp additionalfile) is retired: Unity never
//re-ran the compiler when only the .fui bytes changed, so its "compile-time
//guarantee" had a standing stale window the bake-time facade does not.

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
