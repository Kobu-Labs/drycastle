using System.Numerics;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Turrets;
using Drycastle.ShooterCore.Wildlife;
using static Drycastle.ShooterCore.Movement.Planar;

namespace Drycastle.ShooterCore.Colliders;

/// <summary>
/// What a navmesh walker (a citizen, a Gang member, a bot's route) asks of the world as it walks (ADR 0028, the Walker row of <see cref="CollisionRules"/>): what is ahead of it that its row
/// Collides with and the navigation mesh does not know (a trunk, a door that is closed now, a parked vehicle), and the height of the ground it steps onto when it goes round.
/// The game answers with <c>WorldQuery</c>; a walker without one walks its route as it is.
/// </summary>
public interface IWalkerGround
{
    /// <summary>
    /// Metres a walker standing at <paramref name="from"/> can go along <paramref name="dir"/> (flat, unit) before it meets something its row stops on, or infinity when
    /// nothing is within <paramref name="metres"/>. One ray: the caller keeps them rare (<see cref="WalkGuard"/>).
    /// </summary>
    float ClearAhead(Vector3 from, Vector3 dir, float metres);

    /// <summary>The ground height at (x, z), or <paramref name="fallback"/> where the world cannot tell.</summary>
    float GroundY(float x, float z, float fallback);
}

/// <summary>
/// The local detour of a walker on a baked route (ADR 0028: a walker stops at a door closed now, goes round a trunk or a parked vehicle, with no re-bake). The route is the navigation mesh's; this asks one
/// ray of <see cref="IWalkerGround"/> along the next leg, every <see cref="LookAheadM"/> metres of straight run at most (<see cref="ClearPath"/> keeps the answer), and when the leg is blocked tries a point to the
/// side of it, then the other side; with both sides blocked it stands and asks again after <see cref="WaitSeconds"/> (a door that opens, a vehicle that drives off, lets it on). Pure and without a clock of its own:
/// <see cref="Tick"/> once a tick, <see cref="Head"/> for every step it takes.
/// </summary>
public struct WalkGuard
{
    /// <summary>How far ahead the ray looks, metres: the straight run one ray vouches for.</summary>
    public const float LookAheadM = 3f;
    /// <summary>How far a sidestep leaves the leg, metres, and the angles (to the leg) the two sides are tried at, radians.</summary>
    public const float SidestepM = 1.6f, SidestepAngle = 1.0f, WideAngle = 1.55f;
    /// <summary>A walker blocked on both sides stands this long before it asks again, seconds.</summary>
    public const float WaitSeconds = 1f;

    ClearPath _clear;
    Countdown _wait;
    Vector3? _via;
    int _flips; // odd: it went round on the other side last; the next block goes round the same way, so a wall is followed and not paced
    readonly int Side => (_flips & 1) == 0 ? 1 : -1;

    /// <summary>Counts the wait down; once a tick.</summary>
    public void Tick(float dt) => _wait.Tick(dt);

    /// <summary>Whether it stands, blocked on both sides.</summary>
    public readonly bool Waiting => _wait.Running;

    /// <summary>Forgets the detour and what the last ray found (a new route, a teleport).</summary>
    public void Reset() { _clear.Reset(); _wait.Cancel(); _via = null; }

    /// <summary>
    /// The point to walk towards from <paramref name="pos"/> to follow the leg that ends at <paramref name="to"/>: the leg's end when the way is clear, a point beside the leg when going round
    /// something, null when it stands (blocked on both sides). With no <paramref name="ground"/> always the leg's end. <paramref name="step"/> is how far it goes before it asks again (metres).
    /// </summary>
    public Vector3? Head(Vector3 pos, Vector3 to, float step, IWalkerGround? ground)
    {
        if (ground == null) return to;
        if (_wait.Running) return null;
        if (_via is { } via)
        {
            if (Flat(pos, via) > 0.15f) return via;
            _via = null; _clear.Reset(); // round it: the leg again
        }
        var (dx, dz) = (to.X - pos.X, to.Z - pos.Z);
        var len = MathF.Sqrt(dx * dx + dz * dz);
        if (len < 0.05f) return to;
        var dir = new Vector3(dx / len, 0, dz / len);
        var yaw = TurretMath.YawTo(pos, to);
        if (_clear.Covers(pos.X, pos.Z, yaw, step)) return to;
        var look = MathF.Min(len, LookAheadM);
        var hit = ground.ClearAhead(pos, dir, look);
        if (float.IsInfinity(hit) || hit >= look) { _clear.Set(pos.X, pos.Z, yaw, look); return to; }
        // blocked: round it, on the side it went last, else the other, nearer the leg first
        Span<float> turns = stackalloc float[4] { SidestepAngle * Side, WideAngle * Side, -SidestepAngle * Side, -WideAngle * Side };
        foreach (var turn in turns)
        {
            var (s, c) = MathF.SinCos(turn);
            var d = new Vector3(dir.X * c - dir.Z * s, 0, dir.X * s + dir.Z * c);
            if (!float.IsInfinity(ground.ClearAhead(pos, d, SidestepM + 0.4f))) continue;
            var x = pos.X + d.X * SidestepM; var z = pos.Z + d.Z * SidestepM;
            _via = new Vector3(x, ground.GroundY(x, z, pos.Y), z);
            if (turn * Side < 0) _flips++;
            return _via;
        }
        _wait.Start(WaitSeconds);
        _clear.Reset();
        return null;
    }
}
