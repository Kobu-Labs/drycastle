using Godot;
using Drycastle.Shooter.Menu;
using Drycastle.ShooterCore.Minimap;

namespace Drycastle.Shooter.Maps;

/// <summary>
/// The marker of the player's Job in the world (docs/cartel.md "Contact"; <see cref="MapMarkers.Job"/>): an amber star at the point with "Job - 500m" under it, like the waypoint's
/// diamond (no plate behind the text, a thin outline so it reads on any ground). Drawn only while the point is in front of the camera and on the screen; hidden, and not
/// redrawn, while the Job has no marker. The label is rebuilt only when the whole metres change.
/// </summary>
public sealed partial class JobMarker : Control
{
    const float Outer = 11f, Inner = 4.5f, TextGap = 6f, Lift = 2f;

    readonly MapMarkers _markers;
    int _shownMetres = -1;
    string _label = "";
    string _format = "";
    readonly Vector2[] _star = new Vector2[11];

    public JobMarker(MapMarkers markers)
    {
        _markers = markers;
        Visible = false;
    }

    public override void _Process(double delta)
    {
        var on = _markers.Job != null;
        if (on != Visible) { Visible = on; _shownMetres = -1; }
        if (on) QueueRedraw();
    }

    public override void _Draw()
    {
        if (_markers.Job is not { } at || GetViewport()?.GetCamera3D() is not { } cam) return;
        var world = new Vector3(at.X, at.Y + Lift, at.Z);
        if (cam.IsPositionBehind(world)) return;
        var p = GetGlobalTransformWithCanvas().AffineInverse() * cam.UnprojectPosition(world); // the HUD root is scaled: back into its units
        if (p.X < 0 || p.Y < 0 || p.X > Size.X || p.Y > Size.Y) return;

        var cp = cam.GlobalPosition;
        var metres = (int)MathF.Round(new Vector2(cp.X - at.X, cp.Z - at.Z).Length());
        if (metres != _shownMetres || _format.Length == 0)
        {
            if (_format.Length == 0) _format = Strings.T("cartel.marker");
            _shownMetres = metres;
            _label = string.Format(_format, Waypoint.Distance(metres));
        }

        MapGlyphs.StarPoints(p, Outer, Inner, _star);
        DrawPolyline(_star, ShooterTheme.Outline, 4f, true);
        DrawPolyline(_star, ShooterTheme.JobMark, 2f, true);

        var font = GetThemeDefaultFont();
        const int fs = ShooterTheme.FontSmall;
        var at0 = new Vector2(p.X - 100f, p.Y + Outer + TextGap + font.GetAscent(fs));
        DrawStringOutline(font, at0, _label, HorizontalAlignment.Center, 200f, fs, 4, ShooterTheme.Outline);
        DrawString(font, at0, _label, HorizontalAlignment.Center, 200f, fs, ShooterTheme.JobMark);
    }
}
