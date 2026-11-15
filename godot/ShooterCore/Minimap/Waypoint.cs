using System.Globalization;
using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>
/// The waypoint a player sets by clicking the tactical map: one ground point (x, z) per player, local only, shown in the world
/// as a light "Target - 500m" marker and on both maps. Nothing leads to it (no route, no line). The point is cleared when the
/// player gets within <see cref="ReachedMetres"/> of it, when it is clicked again or right-clicked, and when a new match starts.
/// </summary>
public sealed class Waypoint
{
    /// <summary>Within this distance (metres, on the ground) the waypoint counts as reached and clears itself.</summary>
    public const float ReachedMetres = 5f;

    /// <summary>The ground point (x, z), or null when none is set.</summary>
    public Vector2? At { get; private set; }

    /// <summary>Sets the waypoint. Null clears it.</summary>
    public void Set(Vector2? groundXZ) => At = groundXZ;

    public void Clear() => At = null;

    /// <summary>
    /// A click at <paramref name="groundXZ"/>: sets the waypoint, or clears it when the click lands within <paramref name="hitMetres"/>
    /// of the current one (clicking the marker again takes it away). True when a waypoint is set afterwards.
    /// </summary>
    public bool Click(Vector2 groundXZ, float hitMetres)
    {
        if (At is { } cur && Vector2.Distance(cur, groundXZ) <= hitMetres) { At = null; return false; }
        At = groundXZ;
        return true;
    }

    /// <summary>Clears the waypoint when <paramref name="playerXZ"/> is within <see cref="ReachedMetres"/> of it. True when it cleared.</summary>
    public bool ClearIfReached(Vector2 playerXZ)
    {
        if (At is not { } cur || Vector2.Distance(cur, playerXZ) > ReachedMetres) return false;
        At = null;
        return true;
    }

    /// <summary>The distance as the marker writes it: whole metres under a kilometre ("500m"), one decimal of kilometres above ("1.2km").</summary>
    public static string Distance(float metres)
    {
        var whole = (int)MathF.Round(Math.Max(0f, metres));
        return whole < 1000
            ? whole.ToString(CultureInfo.InvariantCulture) + "m"
            : (MathF.Round(whole / 100f) / 10f).ToString("0.0", CultureInfo.InvariantCulture) + "km";
    }
}
