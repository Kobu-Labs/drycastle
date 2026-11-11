using System.Numerics;
using System.Text.Json;

namespace Suchohrad.ShooterCore;

/// <summary>One road edge of roads.json: its polyline in local metres (x east, y north), width in metres, OSM class.</summary>
public sealed record RoadLine(Vector2[] Points, float WidthM, string Highway);

/// <summary>Reads roads.json (map 0.4.0+) into polylines for the minimap texture. Empty input gives no roads.</summary>
public static class RoadNetwork
{
    public static List<RoadLine> Parse(string json)
    {
        var roads = new List<RoadLine>();
        if (string.IsNullOrWhiteSpace(json)) return roads;
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("edges", out var edges)) return roads;
        foreach (var e in edges.EnumerateArray())
        {
            if (!e.TryGetProperty("polyline", out var line)) continue;
            var pts = new List<Vector2>();
            foreach (var p in line.EnumerateArray())
                if (p.GetArrayLength() >= 2) pts.Add(new Vector2((float)p[0].GetDouble(), (float)p[1].GetDouble()));
            if (pts.Count < 2) continue;
            var width = e.TryGetProperty("width", out var w) && w.ValueKind == JsonValueKind.Number ? (float)w.GetDouble() : 4f;
            var highway = e.TryGetProperty("highway", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() ?? "" : "";
            roads.Add(new RoadLine(pts.ToArray(), width, highway));
        }
        return roads;
    }
}

/// <summary>
/// Godot-free minimap geometry (ticket #119). The texture is in ground axes: texel (u, v) = ground (x, z) relative to the
/// texture origin, so the texture's axes match the world's. The view rotates so the player's facing points up.
/// </summary>
public static class MinimapMath
{
    /// <summary>
    /// Rotation (radians) that turns the player's facing to screen-up, for yaw in the game's convention
    /// (forward = (-sin yaw, -cos yaw) in ground x/z). Facing north (yaw 0) gives 0: north stays up.
    /// </summary>
    public static float RotationFor(float yaw)
    {
        var fx = -MathF.Sin(yaw);
        var fz = -MathF.Cos(yaw);
        return -MathF.PI / 2f - MathF.Atan2(fz, fx);
    }

    /// <summary>Rotates a 2D vector by <paramref name="theta"/> in screen coordinates (y down).</summary>
    public static Vector2 Rotate(Vector2 v, float theta)
    {
        var c = MathF.Cos(theta);
        var s = MathF.Sin(theta);
        return new Vector2(v.X * c - v.Y * s, v.X * s + v.Y * c);
    }

    /// <summary>
    /// Screen offset (pixels from the minimap centre) of a ground point <paramref name="deltaMetres"/> from the player
    /// (x, z), for a view rotated by <paramref name="theta"/> with <paramref name="pxPerMetre"/> scale.
    /// </summary>
    public static Vector2 ToScreen(Vector2 deltaMetres, float theta, float pxPerMetre) => Rotate(deltaMetres, theta) * pxPerMetre;

    /// <summary>Pulls a screen offset into the square of half-size <paramref name="half"/>, so far dots sit on the edge.</summary>
    public static Vector2 ClampToSquare(Vector2 p, float half) =>
        new(Math.Clamp(p.X, -half, half), Math.Clamp(p.Y, -half, half));

    /// <summary>Pixels per metre so that <paramref name="halfViewMetres"/> fills <paramref name="halfPx"/>.</summary>
    public static float PxPerMetre(float halfPx, float halfViewMetres) => halfViewMetres > 0 ? halfPx / halfViewMetres : 0f;
}

/// <summary>
/// Enemy pings for the minimap (the UAV streak's layer, ticket #119 hook). A ping set lives until its expiry time on the
/// caller's clock; a new set replaces the old one. Positions are ground (x, z) in metres.
/// </summary>
public sealed class MinimapPings
{
    Vector2[] _positions = Array.Empty<Vector2>();
    double _until;

    /// <summary>The current positions (empty once expired or cleared).</summary>
    public IReadOnlyList<Vector2> Positions(double now) => Active(now) ? _positions : Array.Empty<Vector2>();

    /// <summary>Whether a set is still live at <paramref name="now"/>.</summary>
    public bool Active(double now) => _until > 0 && now < _until;

    /// <summary>Replaces the pings; they show until <paramref name="expiry"/> (same clock as <paramref name="now"/>).</summary>
    public void Set(IReadOnlyList<Vector2> positions, double expiry)
    {
        _positions = positions.ToArray();
        _until = expiry;
    }

    public void Clear()
    {
        _positions = Array.Empty<Vector2>();
        _until = 0;
    }
}
