using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// The seat panel (vehicle seat switch): a small top-down schematic of the vehicle the player sits in, bottom right above the vehicle readout, with the
/// seat they are in named above it ("Seat 2: Gunner") and the number keys that take another below it. It is text and shape, never colour alone, and
/// ignores the mouse. State is set on change only (<see cref="SetSeats"/> compares references), so a frame costs a few comparisons.
/// </summary>
public partial class Hud
{
    PanelContainer _seatsPanel = null!;
    Label _seatsTitle = null!, _seatsHint = null!;
    SeatSchematicView _seatsView = null!;
    VehicleDef? _seatsDef;
    ushort[]? _seatsOccupants;
    int _seatsMine = -1;

    void BuildSeats()
    {
        // above the vehicle speed readout, which sits above the ammo panel and the progress strip
        const float above = 112 + 72 + 40 + 3 * ShooterTheme.Space;
        _seatsPanel = Place(P("HudPanel"), 1, 1, -ShooterTheme.Margin, -ShooterTheme.Margin - above, SeatSchematicView.W + 2 * ShooterTheme.Space, 248);
        _seatsPanel.Visible = false;
        _root.AddChild(_seatsPanel);
        var v = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        v.AddThemeConstantOverride("separation", ShooterTheme.Space / 2);
        _seatsPanel.AddChild(v);
        _seatsTitle = L("", "HudSmall", HorizontalAlignment.Center);
        _seatsHint = L("", "HudSmall", HorizontalAlignment.Center);
        _seatsView = new SeatSchematicView();
        v.AddChild(_seatsTitle); v.AddChild(_seatsView); v.AddChild(_seatsHint);
    }

    /// <summary>The seats of the vehicle the local player sits in, or null to hide the panel (not seated).</summary>
    public void SetSeats((VehicleDef Def, ushort[] Occupants, int Mine)? seats)
    {
        if (seats is not { Mine: >= 0 } s)
        {
            if (_seatsPanel.Visible) { _seatsPanel.Visible = false; _seatsDef = null; _seatsOccupants = null; _seatsMine = -1; }
            return;
        }
        if (ReferenceEquals(s.Def, _seatsDef) && ReferenceEquals(s.Occupants, _seatsOccupants) && s.Mine == _seatsMine) return;
        _seatsDef = s.Def; _seatsOccupants = s.Occupants; _seatsMine = s.Mine;
        var role = s.Mine < s.Def.Seats.Count ? s.Def.Seats[s.Mine].Role : "passenger";
        if (role == "driver" && s.Def.Flight != null) role = "pilot"; // the driver seat of a helicopter is the pilot's
        if (s.Mine < s.Def.Seats.Count && s.Def.Seats[s.Mine].Armed) role = "armed"; // a passenger who fights from the seat (ADR 0011)
        Set(_seatsTitle, Strings.F("hud.seats.title", s.Mine + 1, Strings.T("hud.seat." + role)));
        // nothing to switch to in a one-seat vehicle: the hint is left blank (the line keeps its place, so the panel does not jump between vehicles)
        Set(_seatsHint, s.Def.Seats.Count > 1 ? Strings.F("hud.seats.hint", InputBindings.Name("slot_1"), InputBindings.Name($"slot_{s.Def.Seats.Count}")) : "");
        _seatsView.Set(s.Def, s.Occupants, s.Mine);
        _seatsPanel.Visible = true;
    }
}
