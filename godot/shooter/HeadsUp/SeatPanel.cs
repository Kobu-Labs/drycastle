using Godot;
using Drycastle.Shooter.Menu;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Vehicles;

namespace Drycastle.Shooter.HeadsUp;

/// <summary>
/// The seat panel (vehicle seat switch): the real top-view outline of the vehicle the player sits in, bottom right above the vehicle readout, with the
/// seat they are in named above it ("Seat 2: Gunner") and the number keys that take another below it. It is text and shape, never colour alone, and
/// ignores the mouse. State is set on change only (<see cref="Set"/> compares references), so a frame costs a few comparisons.
/// </summary>
internal sealed class SeatPanel
{
    readonly PanelContainer _panel;
    readonly Label _title, _hint;
    readonly SeatSchematicView _view;
    VehicleDef? _def;
    ushort[]? _occupants;
    int _mine = -1;

    public SeatPanel(Control root)
    {
        // above the vehicle speed readout, which sits above the ammo panel and the progress strip
        const float above = 112 + 72 + 40 + 3 * ShooterTheme.Space;
        _panel = HudUi.Place(HudUi.P("HudPanel"), 1, 1, -ShooterTheme.Margin, -ShooterTheme.Margin - above, SeatSchematicView.W + 2 * ShooterTheme.Space, 296);
        _panel.Visible = false;
        root.AddChild(_panel);
        var v = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        v.AddThemeConstantOverride("separation", ShooterTheme.Space / 2);
        _panel.AddChild(v);
        _title = HudUi.L("", "HudSmall", HorizontalAlignment.Center);
        _hint = HudUi.L("", "HudSmall", HorizontalAlignment.Center);
        _view = new SeatSchematicView();
        v.AddChild(_title); v.AddChild(_view); v.AddChild(_hint);
    }

    /// <summary>The key hint names the (maybe rebound) keys: the panel is rebuilt on the next frame.</summary>
    public void Forget() => _def = null;

    /// <summary>The seats of the vehicle the local player sits in, or null to hide the panel (not seated).</summary>
    public void Set((VehicleDef Def, ushort[] Occupants, int Mine)? seats)
    {
        if (seats is not { Mine: >= 0 } s)
        {
            if (_panel.Visible) { _panel.Visible = false; _def = null; _occupants = null; _mine = -1; }
            return;
        }
        if (ReferenceEquals(s.Def, _def) && ReferenceEquals(s.Occupants, _occupants) && s.Mine == _mine) return;
        _def = s.Def; _occupants = s.Occupants; _mine = s.Mine;
        var role = s.Mine < s.Def.Seats.Count ? s.Def.Seats[s.Mine].Role : "passenger";
        if (role == "driver" && s.Def.Flight != null) role = "pilot"; // the driver seat of a helicopter is the pilot's
        if (s.Mine < s.Def.Seats.Count && s.Def.Seats[s.Mine].Armed) role = "armed"; // a passenger who fights from the seat (ADR 0011)
        HudUi.Set(_title, Strings.F("hud.seats.title", s.Mine + 1, Strings.T("hud.seat." + role)));
        // nothing to switch to in a one-seat vehicle: the hint is left blank (the line keeps its place, so the panel does not jump between vehicles)
        HudUi.Set(_hint, s.Def.Seats.Count > 1 ? Strings.F("hud.seats.hint", InputBindings.Name("slot_1"), InputBindings.Name($"slot_{s.Def.Seats.Count}")) : "");
        _view.Set(s.Def, s.Occupants, s.Mine);
        _panel.Visible = true;
    }
}
