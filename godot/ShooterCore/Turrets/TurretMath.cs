using System.Numerics;

namespace Suchohrad.ShooterCore;

/// <summary>Why a turret cannot be placed where the owner aims. <see cref="Valid"/> is the only one that lets it go down.</summary>
public enum PlacementCheck : byte
{
    Valid = 0,
    /// <summary>The aim ray hits no ground within the probe distance.</summary>
    NoGround = 1,
    /// <summary>The ground is steeper than the placement slope, or the aim hits a wall or vehicle side.</summary>
    TooSteep = 2,
    /// <summary>The ground is further than the placement range from the player's feet.</summary>
    OutOfRange = 3,
    /// <summary>The ground is more than the placement step above or below the player's feet.</summary>
    Uneven = 4,
}

/// <summary>A world surface the aim ray hit: where, and which way it faces.</summary>
public readonly record struct GroundHit(Vector3 Point, Vector3 Normal);

/// <summary>A live enemy the turret may shoot: its id and the point it aims at (chest height).</summary>
public readonly record struct TurretCandidate(ushort Id, Vector3 Aim);

/// <summary>Pure turret rules: placement, turning, and target choice. No Godot, so the tests run without a server.</summary>
public static class TurretMath
{
    /// <summary>
    /// Checks a placement. <paramref name="feet"/> is the player's feet; <paramref name="hit"/> is the world surface the
    /// aim ray found, or null. Returns the verdict and the point the ghost sits at (the hit, or the feet when nothing was hit).
    /// </summary>
    public static (PlacementCheck Check, Vector3 Point) Evaluate(Vector3 feet, GroundHit? hit, TurretDef def)
    {
        if (hit is not { } h) return (PlacementCheck.NoGround, feet);
        var flat = MathF.Cos(def.PlacementSlopeDeg * MathF.PI / 180f);
        if (h.Normal.Y < flat) return (PlacementCheck.TooSteep, h.Point);
        var dx = h.Point.X - feet.X;
        var dz = h.Point.Z - feet.Z;
        if (MathF.Sqrt(dx * dx + dz * dz) > def.PlacementRangeM) return (PlacementCheck.OutOfRange, h.Point);
        if (MathF.Abs(h.Point.Y - feet.Y) > def.PlacementStepM) return (PlacementCheck.Uneven, h.Point);
        return (PlacementCheck.Valid, h.Point);
    }

    /// <summary>Yaw (Godot convention, 0 faces -Z) that points from <paramref name="from"/> to <paramref name="to"/> in the ground plane.</summary>
    public static float YawTo(Vector3 from, Vector3 to) => MathF.Atan2(-(to.X - from.X), -(to.Z - from.Z));

    /// <summary>The signed shortest difference between two yaws, in radians (-pi..pi].</summary>
    public static float AngleDiff(float from, float to)
    {
        var d = (to - from) % (2 * MathF.PI);
        if (d > MathF.PI) d -= 2 * MathF.PI;
        else if (d <= -MathF.PI) d += 2 * MathF.PI;
        return d;
    }

    /// <summary>Turns <paramref name="current"/> towards <paramref name="target"/> by at most <paramref name="maxStep"/> radians, the short way round.</summary>
    public static float StepAngle(float current, float target, float maxStep)
    {
        var d = AngleDiff(current, target);
        return MathF.Abs(d) <= maxStep ? target : current + MathF.Sign(d) * maxStep;
    }

    /// <summary>
    /// The nearest candidate within <paramref name="sightRange"/> of the muzzle that <paramref name="clear"/> says can be
    /// seen (no world surface between). Null when none. Ties keep the lower id, so the choice is stable.
    /// </summary>
    public static ushort? PickTarget(Vector3 muzzle, IEnumerable<TurretCandidate> candidates, float sightRange, Func<Vector3, Vector3, bool> clear)
    {
        ushort? best = null;
        var bestD = float.PositiveInfinity;
        foreach (var c in candidates)
        {
            var d = Vector3.Distance(muzzle, c.Aim);
            if (d > sightRange) continue;
            if (d > bestD || (d == bestD && best is { } b && c.Id > b)) continue;
            if (!clear(muzzle, c.Aim)) continue;
            best = c.Id;
            bestD = d;
        }
        return best;
    }
}
