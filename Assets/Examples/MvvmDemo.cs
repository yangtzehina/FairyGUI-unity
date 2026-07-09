using FairyGUI;
using FairyGUI.Mvvm;
using UnityEngine;

/// <summary>
/// Minimal one-way MVVM loop on FairyGUI: [Observable] fields become generated
/// properties that mark a dirty bit; the Binder flushes dirty bindings to the UI once
/// per frame; UI events go the other way through a CommandQueue that game logic drains.
/// The UI here is built in code so the demo needs no UI package.
/// </summary>
public partial class MvvmDemoVM : ViewModel
{
    [Observable] string _title;
    [Observable] int _gold;
}

public class MvvmDemo : MonoBehaviour
{
    public enum Command
    {
        AddGold
    }

    public MvvmDemoVM vm;
    public Binder binder;
    public CommandQueue<Command> commands;
    public GTextField titleText;
    public GTextField goldText;

    void Start()
    {
        titleText = new GTextField();
        titleText.textFormat = new TextFormat { size = 26, color = Color.white };
        titleText.SetSize(400, 40);
        titleText.SetXY(40, 40);
        GRoot.inst.AddChild(titleText);

        goldText = new GTextField();
        goldText.textFormat = new TextFormat { size = 22, color = Color.yellow };
        goldText.SetSize(400, 36);
        goldText.SetXY(40, 90);
        GRoot.inst.AddChild(goldText);

        GGraph button = new GGraph();
        button.SetSize(160, 44);
        button.SetXY(40, 140);
        button.DrawRect(160, 44, 1, Color.gray, new Color(0.2f, 0.5f, 0.9f));
        GRoot.inst.AddChild(button);

        vm = new MvvmDemoVM();
        commands = new CommandQueue<Command>();

        binder = new Binder()
            .Bind(vm, MvvmDemoVM.TitleProperty, () => titleText.text = vm.Title)
            .Bind(vm, MvvmDemoVM.GoldProperty, () => goldText.text = "Gold: " + vm.Gold);

        //view → command queue; no game logic runs inside UI callbacks
        button.onClick.Add(() => commands.Enqueue(Command.AddGold));

        vm.Title = "MVVM demo";
    }

    void Update()
    {
        //game logic: drain commands, mutate the view model
        while (commands.TryDequeue(out Command cmd))
        {
            if (cmd == Command.AddGold)
                vm.Gold += 10;
        }
    }

    void LateUpdate()
    {
        binder.Flush();
    }
}
