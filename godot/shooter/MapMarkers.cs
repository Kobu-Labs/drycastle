using Godot;
using Drycastle.ShooterCore;
using Vector2 = Godot.Vector2;
using NVector2 = System.Numerics.Vector2;

namespace Drycastle.Shooter;

/// <summary>
/// What the minimap and the tactical map both show on top of the terrain: the player, vehicles, teammates and the UAV's enemy
/// pings. The HUD owns one instance and gives it to both views, and the match feeds it through the minimap's setters
/// (<see cref="Minimap.SetPlayer"/> and the others) from one place, so the two always agree. Positions are ground (x, z) in
/// metres, the game's axes (north is -z).
/// </summary>
public sealed class MapMarkers
{
    public Vector2 Player { get; private set; }
    /// <summary>The view yaw (forward = (-sin yaw, -cos yaw) in ground x/z).</summary>
    public float Yaw { get; private set; }
    public bool HasPlayer { get; private set; }
    public List<NVector2> Vehicles { get; } = new();
    public List<NVector2> Friends { get; } = new();
    public MinimapPings Pings { get; } = new();
    /// <summary>Gunfire blips (enemy shots near the player); null until the client has the match data's radius and lifetime.</summary>
    public GunfireBlips? Blips { get; set; }
    /// <summary>The player's waypoint (set by clicking the tactical map): drawn on both maps and in the world.</summary>
    public Waypoint Waypoint { get; } = new();
    /// <summary>Team deathmatch: pings use the enemy colour (orange) and friends are drawn, as outlined circles in the friend colour.</summary>
    public bool TeamColours { get; set; }

    /// <summary>True the first time a player is set; the minimap uses it to start its eased yaw without a sweep from zero.</summary>
    public bool SetPlayer(Vector2 groundXZ, float viewYaw)
    {
        Player = groundXZ;
        Yaw = viewYaw;
        var first = !HasPlayer;
        HasPlayer = true;
        return first;
    }

    public void SetVehicles(IReadOnlyList<Vector2> groundXZ) => Copy(groundXZ, Vehicles);

    public void SetFriends(IReadOnlyList<Vector2> groundXZ) => Copy(groundXZ, Friends);

    static void Copy(IReadOnlyList<Vector2> src, List<NVector2> dst)
    {
        dst.Clear();
        for (var i = 0; i < src.Count; i++) dst.Add(new NVector2(src[i].X, src[i].Y));
    }

    public void SetPings(IReadOnlyList<Vector2> groundXZ, double expiry)
    {
        var copy = new NVector2[groundXZ.Count];
        for (var i = 0; i < copy.Length; i++) copy[i] = new NVector2(groundXZ[i].X, groundXZ[i].Y);
        Pings.Set(copy, expiry);
    }
}
