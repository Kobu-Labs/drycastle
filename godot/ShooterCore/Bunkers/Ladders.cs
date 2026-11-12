using System.Numerics;

namespace Suchohrad.ShooterCore;

/// <summary>
/// One ladder between two floors of a bunker (bunkers.json, #224). The climber stands at <see cref="Rail"/> and faces
/// <see cref="Facing"/>; the floor beyond the hatch edge the ladder hangs on is where they step off at the top. Ground axes
/// (x east, y = -north) with heights in Godot Y, like <see cref="BunkerRoom"/>.
/// </summary>
public sealed record Ladder(Vector2 Rail, Vector2 Facing, float Y0, float Y1);

/// <summary>The ladders of the map and the climbing step of the shared movement (<see cref="MovementStep"/>).</summary>
public sealed class Ladders
{
    public static readonly Ladders Empty = new(Array.Empty<Ladder>());

    /// <summary>How far from the climbing spot (m, on the ground) a player can get on.</summary>
    public const float ReachM = 1.3f;
    /// <summary>Climbing speed (m/s).</summary>
    public const float ClimbSpeed = 2f;
    /// <summary>At the top the player is put this far past the climbing spot, on the floor beyond the hatch edge.</summary>
    public const float StepOffTop = 1f;
    /// <summary>At the bottom they are put this far back from the ladder, into the room.</summary>
    public const float StepOffBottom = 0.5f;
    /// <summary>The slack in height (m) at which a player can still get on: a step above the top floor, a little below the bottom.</summary>
    public const float ReachAbove = 0.6f, ReachBelow = 0.3f;

    public IReadOnlyList<Ladder> All { get; }
    public int Count => All.Count;
    public Ladder this[int i] => All[i];

    public Ladders(IReadOnlyList<Ladder> all) => All = all;

    /// <summary>
    /// The ladder a player at <paramref name="feet"/> looking along <paramref name="yaw"/> (Godot convention) can get on, or -1:
    /// near its climbing spot, between its floors, and looking toward it. Several in reach: the nearest.
    /// </summary>
    public int Reach(Vector3 feet, float yaw)
    {
        var (sin, cos) = MathF.SinCos(yaw);
        var look = new Vector2(-sin, -cos);
        var best = -1;
        var bestD = float.MaxValue;
        for (var i = 0; i < All.Count; i++)
        {
            var l = All[i];
            if (feet.Y < l.Y0 - ReachBelow || feet.Y > l.Y1 + ReachAbove) continue;
            var to = l.Rail - new Vector2(feet.X, feet.Z);
            var d = to.Length();
            if (d > ReachM || d >= bestD) continue;
            if (d > 0.2f && Vector2.Dot(look, to / d) < 0.5f) continue;
            best = i; bestD = d;
        }
        return best;
    }

    /// <summary>
    /// The ladder part of a movement tick. Not on a ladder: the Use key (its press) gets on one in reach. On one: forward climbs,
    /// back descends, the Use key or Jump lets go, and reaching the top or the bottom steps off. Returns true when the tick was
    /// spent on the ladder (the caller then skips the walking step and the physics move). Position and state are set here, so
    /// the client prediction and the server agree.
    /// </summary>
    public bool Climb(ref MoveState s, in MoveInput i, float dt)
    {
        var use = i.Has(MoveButtons.Use);
        var press = use && !s.UseKeyDown;
        s.UseKeyDown = use;
        if (!s.OnLadder)
        {
            if (!press || Count == 0) return false;
            var ix = Reach(s.Position, i.Yaw);
            if (ix < 0) return false;
            var l = All[ix];
            s.OnLadder = true;
            s.LadderIndex = ix;
            s.LadderArmed = false; // climbing starts once the movement keys have been let go: pressing Use while walking at the hatch does not step straight off
            s.Position = new Vector3(l.Rail.X, Math.Clamp(s.Position.Y, l.Y0, l.Y1), l.Rail.Y);
            s.Velocity = Vector3.Zero;
            s.OnFloor = true;
            s.FallPeakY = s.Position.Y;
            s.ProneLatched = false; s.Stance = Stance.Stand; s.StepTo = Stance.Stand; s.StanceTimer = 0;
            return true;
        }

        var ladder = All[s.LadderIndex];
        if (press || i.Has(MoveButtons.Jump))
        {
            s.OnLadder = false; s.OnFloor = false; s.Velocity = Vector3.Zero; s.FallPeakY = s.Position.Y;
            return false; // let go: the walking step takes over this tick and gravity does the rest
        }
        if (MathF.Abs(i.MoveY) < 0.1f) s.LadderArmed = true;
        var y = s.Position.Y + (s.LadderArmed ? i.MoveY * ClimbSpeed * dt : 0f);
        var f = ladder.Facing;
        if (y >= ladder.Y1 && s.LadderArmed && i.MoveY > 0) StepOff(ref s, ladder.Rail + f * StepOffTop, ladder.Y1);
        else if (y <= ladder.Y0) StepOff(ref s, ladder.Rail - f * StepOffBottom, ladder.Y0);
        else s.Position = new Vector3(ladder.Rail.X, Math.Min(y, ladder.Y1), ladder.Rail.Y);
        return true;
    }

    static void StepOff(ref MoveState s, Vector2 at, float y)
    {
        s.OnLadder = false; s.OnFloor = true; s.Velocity = Vector3.Zero;
        s.Position = new Vector3(at.X, y, at.Y);
        s.FallPeakY = y;
    }
}
