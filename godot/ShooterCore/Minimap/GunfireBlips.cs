using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>One live gunfire blip: where the shot came from (ground x, z) and how far through its life it is (0 = just fired, 1 = gone).</summary>
public readonly record struct GunfireBlip(Vector2 Position, float Age01)
{
    /// <summary>Opacity: full when fresh, fading to nothing. With reduce-motion the fade is off: full until it vanishes.</summary>
    public float Alpha(bool reduceMotion) => reduceMotion ? 1f : 1f - Age01;

    /// <summary>Ring growth, 0 to 1 over the life (the ring swells as it fades). Constant with reduce-motion.</summary>
    public float Growth(bool reduceMotion) => reduceMotion ? 0.5f : Age01;
}

/// <summary>
/// Which shots show on the radar and how often (gunfire blips, GLOSSARY "Gunfire blip"). Godot-free. The server already sends every
/// Shot and PelletFan to every client with the shooter and origin, so no event of its own is needed.
/// </summary>
public static class GunfireBlipRules
{
    /// <summary>
    /// Whether a shot is a blip candidate: a player's own held weapon (not a streak weapon or the vehicle guns, which carry special
    /// weapon ids past the weapon table), not a knife, from a player who is not the local one and not a teammate (<paramref name="friends"/>
    /// is false in free-for-all). There are no suppressors in the game yet, so every such shot is unsuppressed. Ghost does not hide gunfire.
    /// </summary>
    public static bool Qualifies(ushort shooter, ushort local, bool shooterIsPlayer, bool friends, int weaponIndex, int weaponCount, WeaponKind kind)
        => shooter != local && shooterIsPlayer && !friends && weaponIndex >= 0 && weaponIndex < weaponCount && kind != WeaponKind.Melee;
}

/// <summary>
/// The live gunfire blips of one client: a shot inside the radius adds a blip that lives <see cref="LifetimeSeconds"/>, and a shooter
/// blips at most once per <see cref="MinIntervalSeconds"/> so automatic fire does not stack them. Times share one clock.
/// </summary>
public sealed class GunfireBlips
{
    const int MaxLive = 32;
    readonly Dictionary<ushort, double> _last = new();
    readonly List<(Vector2 Pos, double At)> _live = new();

    public float RadiusMetres { get; }
    public float LifetimeSeconds { get; }
    public float MinIntervalSeconds { get; }

    public GunfireBlips(float radiusMetres, float lifetimeSeconds, float minIntervalSeconds)
    {
        RadiusMetres = radiusMetres;
        LifetimeSeconds = lifetimeSeconds;
        MinIntervalSeconds = minIntervalSeconds;
    }

    /// <summary>Adds a blip for a qualifying shot from <paramref name="at"/> heard by a listener at <paramref name="listener"/>; false when out of range or rate limited.</summary>
    public bool Add(ushort shooter, Vector2 at, Vector2 listener, double now)
    {
        if (Vector2.DistanceSquared(at, listener) > RadiusMetres * RadiusMetres) return false;
        if (_last.TryGetValue(shooter, out var prev) && now - prev < MinIntervalSeconds) return false;
        _last[shooter] = now;
        if (_live.Count >= MaxLive) _live.RemoveAt(0);
        _live.Add((at, now));
        return true;
    }

    /// <summary>Appends the blips still alive at <paramref name="now"/> to <paramref name="into"/> (cleared first), and drops the expired.</summary>
    public void Collect(double now, List<GunfireBlip> into)
    {
        into.Clear();
        _live.RemoveAll(b => now - b.At >= LifetimeSeconds || now < b.At);
        foreach (var b in _live) into.Add(new GunfireBlip(b.Pos, (float)((now - b.At) / LifetimeSeconds)));
        if (_last.Count > 128) foreach (var k in _last.Where(p => now - p.Value > LifetimeSeconds).Select(p => p.Key).ToList()) _last.Remove(k);
    }

    public void Clear()
    {
        _live.Clear();
        _last.Clear();
    }
}
