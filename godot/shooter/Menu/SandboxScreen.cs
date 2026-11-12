using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// The sandbox panel (F4, #156): the eight killstreaks as icon buttons (pick one to hold it), "Use now" for the held one,
/// and the cheap sandbox actions: heal and refill, teleport to a spawn, a vehicle in front, the "Take damage" checkbox
/// (off is godmode) and the target dummy respawn switch.
/// Built once and updated in place, so the focus stays where the keyboard left it. Esc closes (one level up). F4 closes too.
/// Without sandbox mode it says so and offers only Back.
/// </summary>
public sealed partial class SandboxScreen : MenuScreen
{
    static readonly string[] Vehicles = { "car", "jeep", "quad", "boat", "amphibious", "flak", "bus", "utility_helicopter" };

    readonly NetClient _client;
    readonly Func<GameData> _data;
    readonly List<(Button Cell, Label Mark, string Id)> _streaks = new();
    VBoxContainer _body = null!;
    Label _off = null!, _held = null!;
    Button _use = null!, _respawn = null!;
    CheckBox _damage = null!;
    bool _respawnOn = true;

    public SandboxScreen(NetClient client, Func<GameData> data) : base("MenuBackdrop") { _client = client; _data = data; }

    protected override Control Build()
    {
        var root = Ui.VBox();
        root.CustomMinimumSize = new Vector2(960, 0);
        root.AddChild(Ui.Lbl(Strings.T("sandbox.panel.title"), "HudDisplay"));
        root.AddChild(Ui.Lbl(Strings.T("sandbox.panel.hint"), "HudDim", 0, true));
        root.AddChild(Ui.Gap(ShooterTheme.Space));

        _off = Ui.Lbl(Strings.T("sandbox.panel.off"), "MenuError", 0, true);
        root.AddChild(_off);

        _body = Ui.VBox();
        root.AddChild(_body);
        var data = _data();
        _body.AddChild(Ui.Lbl(Strings.T("sandbox.panel.killstreaks"), "HudHead"));
        _held = Ui.Lbl("", "HudAccent", 0, true);
        _body.AddChild(_held);
        var grid = new GridContainer { Columns = 4 };
        var icons = new IconRegistry(data);
        foreach (var s in data.Killstreaks.Streaks) grid.AddChild(StreakCell(icons, s.Id));
        _body.AddChild(grid);
        _body.AddChild(Ui.Gap(ShooterTheme.Space));

        _body.AddChild(Ui.Lbl(Strings.T("sandbox.panel.actions"), "HudHead"));
        var actions = Ui.HBox();
        _use = Ui.Btn(Strings.T("sandbox.panel.use"), () => _client.SendSandboxAction(SandboxAction.Activate), "MenuPrimary", 200);
        actions.AddChild(_use);
        actions.AddChild(Ui.Btn(Strings.T("sandbox.panel.heal"), () => _client.SendSandboxAction(SandboxAction.Heal), null, 220));
        actions.AddChild(Ui.Btn(Strings.T("sandbox.panel.teleport"), () => _client.SendSandboxAction(SandboxAction.Teleport), null, 220));
        _body.AddChild(actions);

        _body.AddChild(Ui.Lbl(Strings.T("sandbox.panel.vehicles"), "HudDim"));
        var vehicles = new GridContainer { Columns = 4 }; // a grid, as the killstreaks above: the row of buttons does not outgrow the panel
        foreach (var id in Vehicles)
        {
            var index = data.Vehicles.Vehicles.FindIndex(v => v.Id == id);
            if (index < 0) continue; // a vehicle missing from vehicles.json is left out, not an error
            vehicles.AddChild(Ui.Btn(Strings.T("sandbox.vehicle." + id), () => _client.SendSandboxAction(SandboxAction.SpawnVehicle, (byte)index), null, 140));
        }
        _body.AddChild(vehicles);

        _body.AddChild(Ui.Gap(ShooterTheme.Space));
        _damage = new CheckBox
        {
            Text = Strings.T("sandbox.panel.damage"), TooltipText = Strings.T("sandbox.panel.damage.hint"),
            FocusMode = Control.FocusModeEnum.All, MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin,
        };
        _damage.Toggled += on => _client.SetSandboxTakesDamage(on);
        _body.AddChild(_damage);
        _body.AddChild(Ui.Lbl(Strings.T("sandbox.panel.damage.hint"), "HudDim", 0, true));

        _respawn = Ui.Btn("", ToggleRespawn, null, 360);
        _body.AddChild(_respawn);

        root.AddChild(Ui.Gap(ShooterTheme.Space));
        var footer = Ui.HBox();
        footer.AddChild(Ui.Btn(Strings.T("dialog.back"), Back, null, 160));
        footer.AddChild(Ui.Fill());
        root.AddChild(footer);

        _client.SandboxChanged += Refresh;
        Refresh();

        var card = new PanelContainer { ThemeTypeVariation = "MenuCard" };
        card.AddChild(root);
        return card;
    }

    Button StreakCell(IconRegistry icons, string id)
    {
        var cell = new Button { Text = "", FocusMode = Control.FocusModeEnum.All, CustomMinimumSize = new Vector2(216, 144), MouseDefaultCursorShape = Control.CursorShape.PointingHand };
        cell.Pressed += () => _client.SendSandboxGrant(id);
        var col = Ui.VBox();
        col.MouseFilter = Control.MouseFilterEnum.Ignore;
        col.Alignment = BoxContainer.AlignmentMode.Center;
        col.AddChild(new TextureRect
        {
            Texture = ResourceLoader.Load<Texture2D>(icons.Resolve(IconKind.Streak, id, p => ResourceLoader.Exists(p))),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            CustomMinimumSize = new Vector2(120, 72),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        });
        col.AddChild(Ui.Lbl(SandboxStrings.StreakName(id), null, 0, true));
        var mark = Ui.Lbl(Strings.T("sandbox.panel.held_mark"), "HudWarn");
        col.AddChild(mark);
        col.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        cell.AddChild(col);
        _streaks.Add((cell, mark, id));
        return cell;
    }

    void ToggleRespawn()
    {
        _respawnOn = !_respawnOn;
        _client.SendSandboxAction(SandboxAction.TargetRespawn, (byte)(_respawnOn ? 1 : 0));
        Refresh();
    }

    /// <summary>Redraws the state: granted or not, the held streak (the cell variation and its HELD mark), Use now, the respawn switch.</summary>
    void Refresh()
    {
        if (!IsInstanceValid(_body)) return;
        var granted = _client.SandboxGranted;
        _off.Visible = !granted;
        _body.Visible = granted;
        var held = _client.SandboxHeldStreak;
        _held.Text = held == "" ? Strings.T("sandbox.held.none") : Strings.F("sandbox.held", SandboxStrings.StreakName(held));
        foreach (var (cell, mark, id) in _streaks)
        {
            var isHeld = id == held;
            cell.ThemeTypeVariation = isHeld ? "MenuPrimary" : "Button";
            mark.Visible = isHeld;
        }
        _use.Disabled = held == "";
        _damage.SetPressedNoSignal(_client.SandboxTakesDamage);
        _respawn.Text = Strings.T(_respawnOn ? "sandbox.panel.respawn_on" : "sandbox.panel.respawn_off");
    }

    protected override Control? FirstFocusTarget() =>
        _client.SandboxGranted && _streaks.Count > 0 ? _streaks[0].Cell : null;

    /// <summary>F4 again closes the panel, like Esc.</summary>
    public override bool HandleInput(InputEvent e)
    {
        if (e is InputEventKey { Echo: false } && e.IsActionPressed("sandbox_panel"))
        {
            Back();
            return true;
        }
        return false;
    }

    public override void _ExitTree()
    {
        if (_client != null && IsInstanceValid(_client)) _client.SandboxChanged -= Refresh;
    }
}
