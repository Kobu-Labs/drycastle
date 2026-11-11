using System.Numerics;

namespace Suchohrad.ShooterCore;

/// <summary>One planned target position: feet on the ground, facing the centre, in a pose.</summary>
public readonly record struct TargetSpot(Vector3 Feet, float Yaw, Stance Pose, float RingMetres);

/// <summary>
/// Stationary target dummies (issue #148): planning of their positions. Godot-free and deterministic. The caller supplies
/// the ground query (map colliders on the client side of the server, null where no usable ground exists) and an optional
/// extra filter for spots that must be rejected (water, blocked, ...).
/// </summary>
public static class TargetPlacement
{
    /// <summary>Rings around the centre, in metres. Each ring gets an equal share of the targets.</summary>
    public static readonly float[] RingsMetres = { 10f, 25f, 50f, 100f };

    /// <summary>Poses cycle in this order so every ring has stand, crouch and prone targets.</summary>
    public static readonly Stance[] Poses = { Stance.Stand, Stance.Crouch, Stance.Prone };

    /// <summary>Evenly spaced bearings per ring. Sector-major order: the first sector fills every ring before the next sector starts.</summary>
    const int Sectors = 8;

    /// <summary>
    /// Up to <paramref name="count"/> spots around <paramref name="centre"/>. <paramref name="groundY"/> returns the ground
    /// height at (x, z), or null when the position is unusable. <paramref name="accept"/> rejects a spot after the ground
    /// check. Fewer spots than requested are returned when the candidates run out; the result is the same for the same inputs.
    /// </summary>
    public static List<TargetSpot> Plan(Vector3 centre, int count, Func<float, float, float?> groundY, Func<TargetSpot, bool>? accept = null)
    {
        var spots = new List<TargetSpot>();
        if (count <= 0) return spots;
        int candidate = 0;
        for (int sector = 0; sector < Sectors && spots.Count < count; sector++)
        {
            for (int ring = 0; ring < RingsMetres.Length && spots.Count < count; ring++)
            {
                var r = RingsMetres[ring];
                // A per-ring offset keeps the rings from lining up on the same bearing.
                var bearing = (float)(2 * Math.PI * (sector + ring * 0.5) / Sectors);
                var x = centre.X + MathF.Cos(bearing) * r;
                var z = centre.Z + MathF.Sin(bearing) * r;
                var y = groundY(x, z);
                var pose = Poses[candidate % Poses.Length];
                candidate++;
                if (y is not float ground) continue;
                // Face the centre: the direction from the spot back towards the centre, as a yaw (radians, Godot convention).
                var yaw = MathF.Atan2(centre.X - x, centre.Z - z);
                var spot = new TargetSpot(new Vector3(x, ground, z), yaw, pose, r);
                if (accept != null && !accept(spot)) continue;
                spots.Add(spot);
            }
        }
        return spots;
    }
}

/// <summary>
/// The live state of the target dummies of one match: health, death, and the respawn timer. Dummies are not in
/// <see cref="MatchSim"/>'s player roster, so they never count toward the player cap, the scoreboard player count or
/// the round's end. They have no spawn protection and never leave corpses.
/// </summary>
public sealed class TargetDummies
{
    /// <summary>First fake player id. Client ids count up from 1, so the two ranges never meet on a real server.</summary>
    public const ushort FirstId = 50000;
    public const float MaxHealth = MoveConsts.MaxHealth;
    /// <summary>Seconds a killed dummy stays down before it stands again on its spot.</summary>
    public const float RespawnDelay = 3f;

    public sealed class Dummy
    {
        public ushort Id;
        public string Name = "";
        public TargetSpot Spot;
        public float Health;
        public bool Alive;
        /// <summary>Time (seconds, the match clock) at which a dead dummy stands again at its spot.</summary>
        public double RespawnAt;
    }

    readonly List<Dummy> _all = new();
    readonly float _respawnDelay;

    public TargetDummies(IReadOnlyList<TargetSpot> spots, float respawnDelay)
    {
        _respawnDelay = respawnDelay;
        for (int i = 0; i < spots.Count; i++)
            _all.Add(new Dummy { Id = (ushort)(FirstId + i), Name = NameFor(i), Spot = spots[i], Health = MaxHealth, Alive = true });
    }

    public IReadOnlyList<Dummy> All => _all;

    /// <summary>"Target 01", "Target 02", ... (one based).</summary>
    public static string NameFor(int index) => $"Target {index + 1:D2}";

    public static bool IsDummyId(ushort id) => id >= FirstId;

    public Dummy? Get(ushort id) => IsDummyId(id) && id - FirstId < _all.Count ? _all[id - FirstId] : null;

    /// <summary>Hits register only on a standing dummy.</summary>
    public bool IsHittable(ushort id) => Get(id) is { Alive: true };

    /// <summary>
    /// Applies <paramref name="amount"/> (already scaled by headshot, falloff and so on) to a dummy. Returns Killed on the
    /// hit that takes it to zero, Damaged for a hit that does not, and Ignored for unknown or dead dummies.
    /// </summary>
    public DamageResult Damage(ushort id, float amount, double now)
    {
        var d = Get(id);
        if (d is not { Alive: true } || amount <= 0) return DamageResult.Ignored;
        d.Health -= amount;
        if (d.Health > 0) return DamageResult.Damaged;
        d.Health = 0;
        d.Alive = false;
        d.RespawnAt = now + _respawnDelay;
        return DamageResult.Killed;
    }

    /// <summary>Round start: every dummy stands at full health on its spot.</summary>
    public void ResetAll()
    {
        foreach (var d in _all) { d.Alive = true; d.Health = MaxHealth; d.RespawnAt = 0; }
    }

    /// <summary>Stands every dead dummy whose respawn time has come, at full health on its spot. Returns the ids that stood up.</summary>
    public List<ushort> Update(double now)
    {
        var up = new List<ushort>();
        foreach (var d in _all)
        {
            if (d.Alive || now < d.RespawnAt) continue;
            d.Alive = true;
            d.Health = MaxHealth;
            up.Add(d.Id);
        }
        return up;
    }
}
