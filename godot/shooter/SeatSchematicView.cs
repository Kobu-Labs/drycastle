using Godot;
using Suchohrad.ShooterCore;

namespace Suchohrad.Shooter;

/// <summary>
/// The seat schematic's drawing (the vehicle seat switch): a top-down body outline to scale, forward up (a small arrow above the nose), and one numbered
/// marker per seat (<see cref="SeatSchematic"/> places them). The three seat states differ in shape and mark, never in colour alone: free is an outlined
/// disc, yours a filled disc in a double ring, taken a dimmed disc with a strike through its number. Redrawn only when the vehicle or the seating
/// changes (<see cref="Set"/>); it never takes the mouse.
/// </summary>
public sealed partial class SeatSchematicView : Control
{
    public const float W = 184, H = 168;
    const float Pad = 12, Radius = 16;

    SeatLayout? _layout;
    VehicleDef? _def;
    ushort[] _occupants = Array.Empty<ushort>();
    int _mine = -1;

    public SeatSchematicView()
    {
        CustomMinimumSize = new Vector2(W, H);
        MouseFilter = MouseFilterEnum.Ignore;
    }

    /// <summary>Shows <paramref name="def"/> with who sits in each seat (0 = free) and which seat is the player's. The layout is rebuilt only for another vehicle.</summary>
    public void Set(VehicleDef def, ushort[] occupants, int mine)
    {
        if (!ReferenceEquals(def, _def)) { _def = def; _layout = SeatSchematic.Layout(def, W, H, Pad, Radius); }
        _occupants = occupants;
        _mine = mine;
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (_layout is not { } l) return;
        var font = GetThemeDefaultFont();
        var body = new Rect2(l.BodyLeft, l.BodyTop, l.BodyWidth, l.BodyHeight);
        DrawRect(body, ShooterTheme.Raised);
        DrawRect(body, ShooterTheme.Line, false, 2);
        // the nose: an arrow above the top edge, so "up" is forward at a glance
        var mid = l.BodyLeft + l.BodyWidth / 2;
        DrawColoredPolygon(new[] { new Vector2(mid - 8, l.BodyTop - 3), new Vector2(mid + 8, l.BodyTop - 3), new Vector2(mid, l.BodyTop - 11) }, ShooterTheme.Dim);
        foreach (var s in l.Seats)
        {
            var c = new Vector2(s.X, s.Y);
            var mine = s.Index == _mine;
            var taken = !mine && s.Index < _occupants.Length && _occupants[s.Index] != 0;
            DrawCircle(c, Radius, mine ? ShooterTheme.Active : taken ? ShooterTheme.Raised : ShooterTheme.Surface);
            DrawArc(c, Radius, 0, Mathf.Tau, 32, mine ? ShooterTheme.Accent : taken ? ShooterTheme.DisabledText : ShooterTheme.Line, mine ? ShooterTheme.BorderStrong : 2, true);
            if (mine) DrawArc(c, Radius + 4, 0, Mathf.Tau, 32, ShooterTheme.Accent, ShooterTheme.Border, true); // the second ring: "you are here"
            var ink = taken ? ShooterTheme.DisabledText : ShooterTheme.Text;
            DrawString(font, new Vector2(c.X - Radius, c.Y + ShooterTheme.FontBody * 0.35f), s.Number.ToString(), HorizontalAlignment.Center, Radius * 2, ShooterTheme.FontBody, ink);
            if (taken) DrawLine(c + new Vector2(-Radius * 0.7f, Radius * 0.7f), c + new Vector2(Radius * 0.7f, -Radius * 0.7f), ShooterTheme.DisabledText, 2, true);
        }
    }
}
