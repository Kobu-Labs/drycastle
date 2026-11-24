using Drycastle.MapLoader.Core;
using Godot;
using System;

namespace Drycastle.Viewer.Editor;

/// <summary>The two frames the editor works in: the map's local metres (x east, z = -north) and absolute S-JTSK metres (x east, y north). Both need <c>OriginEpsg5514</c>.</summary>
public static class MapFrame
{
    /// <summary>Metres the editor snaps absolute positions to.</summary>
    public const double MoveSnap = 0.5;

    /// <summary>Absolute S-JTSK position of a local ground point, unrounded.</summary>
    public static (double x, double y) RawAbs(this Drycastle.MapLoader.MapLoader map, Vector3 ground)
    {
        var o = map.OriginEpsg5514!.Value;
        return (o.X + ground.X, o.Y - ground.Z);
    }

    /// <summary>Degrees folded into [0, 360).</summary>
    public static double Mod360(double d) { var m = d % 360.0; return m < 0 ? m + 360.0 : m; }

    public static double Snap(double v, double step) => Math.Round(v / step, MidpointRounding.AwayFromZero) * step;

    /// <summary>Absolute S-JTSK position of a ground point (map metres), snapped to 0.5 m unless free, 3 decimals.</summary>
    public static (double x, double y) SnapAbs(this Drycastle.MapLoader.MapLoader map, Vector3 ground, bool free)
    {
        var (x, y) = map.RawAbs(ground);
        if (!free) { x = Snap(x, MoveSnap); y = Snap(y, MoveSnap); }
        return (Math.Round(x, 3), Math.Round(y, 3));
    }

    /// <summary>Absolute S-JTSK position of a local (x, z) ground point, 3 decimals.</summary>
    public static (double x, double y) ToAbsolute(this Drycastle.MapLoader.MapLoader map, Vector2 ground)
    {
        var o = map.OriginEpsg5514!.Value;
        return (Math.Round(o.X + ground.X, 3), Math.Round(o.Y - ground.Y, 3));
    }

    /// <summary>The local (x, z) ground point of an absolute S-JTSK position.</summary>
    public static Vector2 FromAbsolute(this Drycastle.MapLoader.MapLoader map, double x, double y)
    {
        var o = map.OriginEpsg5514!.Value;
        return new Vector2((float)(x - o.X), (float)(-(y - o.Y)));
    }

    public static ParcelInfo? ParcelAtAbs(this Drycastle.MapLoader.MapLoader map, double x, double y)
    {
        var l = map.FromAbsolute(x, y);
        return map.ParcelAt(l.X, l.Y);
    }
}
