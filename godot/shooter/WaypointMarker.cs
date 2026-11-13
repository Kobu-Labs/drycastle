using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// The player's waypoint in the world: a light green diamond at the point with "Target - 500m" under it, nothing else (no line,
/// no arrow, no plate behind the text; the text is outlined thinly so it reads on any ground). Drawn only while the point is in
/// front of the camera and on the screen. Hidden, and not redrawn, while no waypoint is set. The label string is rebuilt only
/// when the whole metres change.
/// </summary>
public sealed partial class WaypointMarker : Control
{
    const float Half = 9f, TextGap = 6f, Lift = 2f;

    readonly Waypoint _wp;
    readonly Func<MapBase?> _map;
    int _shownMetres = -1;
    string _label = "";
    string _format = "";
    readonly Vector2[] _diamond = new Vector2[5];

    public WaypointMarker(Waypoint waypoint, Func<MapBase?> map)
    {
        _wp = waypoint;
        _map = map;
        Visible = false;
    }

    public override void _Process(double delta)
    {
        var on = _wp.At != null;
        if (on != Visible) { Visible = on; _shownMetres = -1; }
        if (on) QueueRedraw();
    }

    public override void _Draw()
    {
        if (_wp.At is not { } at || GetViewport()?.GetCamera3D() is not { } cam) return;
        var y = (_map()?.HeightAt(at.X, at.Y) ?? cam.GlobalPosition.Y) + Lift;
        var world = new Vector3(at.X, y, at.Y);
        if (cam.IsPositionBehind(world)) return;
        var screen = cam.UnprojectPosition(world);
        var p = GetGlobalTransformWithCanvas().AffineInverse() * screen; // the HUD root is scaled: back into its units
        if (p.X < 0 || p.Y < 0 || p.X > Size.X || p.Y > Size.Y) return;

        var cp = cam.GlobalPosition;
        var metres = (int)MathF.Round(new Vector2(cp.X - at.X, cp.Z - at.Y).Length());
        if (metres != _shownMetres || _format.Length == 0)
        {
            if (_format.Length == 0) _format = Strings.T("map.waypoint");
            _shownMetres = metres;
            _label = string.Format(_format, Waypoint.Distance(metres));
        }

        _diamond[0] = p + new Vector2(0, -Half);
        _diamond[1] = p + new Vector2(Half, 0);
        _diamond[2] = p + new Vector2(0, Half);
        _diamond[3] = p + new Vector2(-Half, 0);
        _diamond[4] = _diamond[0];
        DrawPolyline(_diamond, ShooterTheme.Outline, 4f, true);
        DrawPolyline(_diamond, ShooterTheme.Waypoint, 2f, true);

        var font = GetThemeDefaultFont();
        const int fs = ShooterTheme.FontSmall;
        var at0 = new Vector2(p.X - 100f, p.Y + Half + TextGap + font.GetAscent(fs));
        DrawStringOutline(font, at0, _label, HorizontalAlignment.Center, 200f, fs, 4, ShooterTheme.Outline);
        DrawString(font, at0, _label, HorizontalAlignment.Center, 200f, fs, ShooterTheme.Waypoint);
    }
}
