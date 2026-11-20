using System.Buffers.Binary;
using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>Where a Lock is: nothing in the lock circle, building on one Aircraft, or complete (the launcher may fire).</summary>
public enum LockPhase : byte { Idle, Locking, Locked }

/// <summary>
/// What an Aircraft's owner is told about the Lock-on launchers aimed at their Aircraft, the worst of them: nothing, a Lock past the alert share,
/// a complete Lock, a missile in the air. The values are the wire's.
/// </summary>
public enum LockAlert : byte { None, Locking, Locked, Launched }

/// <summary>An Aircraft a Lock-on launcher could lock: its world entity id, where it is now and its kind id (killstreaks.json).</summary>
public readonly record struct LockCandidate(ushort Id, Vector3 Position, string Kind);

/// <summary>
/// The rules of the Lock-on launcher (ADR 0031, glossary "Lock-on launcher", "Lock"): which Aircraft the lock circle holds, how the Lock builds
/// and is lost, what the Aircraft's owner is told, and when a missile fired on a Lock reaches its Aircraft. Godot-free; the server is the only
/// one that decides (<c>NetServer.LockOn</c>), the client draws what it is sent.
/// </summary>
public static class LockRules
{
    /// <summary>
    /// Whether a seat lets its holder use a launcher at all (#373): on foot, or in an armed seat. A driver and an unarmed passenger cannot fire from the seat,
    /// so no Lock builds for them, the server says nothing, and the client draws no sight and plays no seeker beep. The server and the client both ask here.
    /// </summary>
    public static bool SeatAllows(bool seated, bool armedSeat) => !seated || armedSeat;

    public static readonly StateMachineSpec<LockPhase> Spec = StateMachineSpec<LockPhase>
        .Define("Launcher.Lock", "The Lock of a Lock-on launcher on one Aircraft: it builds while the Aircraft stays in the lock circle and in sight, and only a complete Lock lets the launcher fire.", LockPhase.Idle)
        .Allow(LockPhase.Idle, LockPhase.Locking)
        .Allow(LockPhase.Locking, LockPhase.Locked, LockPhase.Idle)
        .Allow(LockPhase.Locked, LockPhase.Idle)
        .Note(LockPhase.Idle, "No Aircraft is held: the launcher is stowed, the player is dead or seated at a gun, or nothing lockable is in the circle.")
        .Note(LockPhase.Locking, "One Aircraft is in the circle; the progress runs for lock.seconds and waits (up to lock.grace_seconds) while the Aircraft is out of the circle or out of sight.")
        .Note(LockPhase.Locked, "The Lock is complete: a pull fires a missile that homes on the Aircraft, and the Lock starts over.")
        .Build();

    /// <summary>Whether <paramref name="c"/> is inside the lock circle round <paramref name="forward"/> (a unit vector) and inside the range of its kind.</summary>
    public static bool Holds(LockDef def, Vector3 eye, Vector3 forward, in LockCandidate c) => Off(def, eye, forward, c) is not null;

    /// <summary>The angle between the aim and the Aircraft, radians, when the circle and the range hold it; null otherwise.</summary>
    static float? Off(LockDef def, Vector3 eye, Vector3 forward, in LockCandidate c)
    {
        var to = c.Position - eye;
        var dist = to.Length();
        if (dist < 1e-3f) return null;
        if (dist > def.RangeM && !def.UnlimitedRange.Contains(c.Kind)) return null;
        var angle = MathF.Acos(Math.Clamp(Vector3.Dot(to / dist, forward), -1f, 1f));
        return angle <= def.ConeDeg * MathF.PI / 180f ? angle : null;
    }

    /// <summary>
    /// The Aircraft the launcher looks at: the one it already holds (<paramref name="current"/>) while the circle, the range and the sight still
    /// hold it, else the one nearest the middle of the circle. <paramref name="visible"/> is the line of sight (asked only for Aircraft in the circle).
    /// </summary>
    public static ushort? Pick(LockDef def, Vector3 eye, Vector3 forward, IReadOnlyList<LockCandidate> candidates, ushort current, Func<LockCandidate, bool> visible)
    {
        ushort? best = null;
        var bestOff = float.MaxValue;
        for (var i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            if (Off(def, eye, forward, c) is not { } off || !visible(c)) continue;
            if (c.Id == current) return current;
            if (off < bestOff) { bestOff = off; best = c.Id; }
        }
        return best;
    }

    /// <summary>What the Aircraft's owner is told for one launcher's Lock on it.</summary>
    public static LockAlert AlertOf(LockDef def, LockPhase phase, float progress) => phase switch
    {
        LockPhase.Locked => LockAlert.Locked,
        LockPhase.Locking when progress >= def.AlertAtFraction => LockAlert.Locking,
        _ => LockAlert.None,
    };

    /// <summary>Step of the march in <see cref="Intercept"/>, seconds (the flak's, <see cref="FlakRules.Plan"/>).</summary>
    const float MarchS = 1f / 120f;

    /// <summary>
    /// When and where a missile fired from <paramref name="origin"/> at <paramref name="speed"/> meets an Aircraft on its known path
    /// (<paramref name="positionAfter"/>: its position a number of seconds from now): the first moment the missile could have flown that far.
    /// Null when it cannot within <paramref name="life"/> seconds. The Aircraft flies a fixed circle, so the whole flight is known at the fire.
    /// </summary>
    public static (float Seconds, Vector3 At)? Intercept(Vector3 origin, float speed, float life, Func<float, Vector3> positionAfter)
    {
        if (speed <= 0) return null;
        for (var t = 0f; t <= life; t += MarchS)
        {
            var at = positionAfter(t);
            if (Vector3.Distance(origin, at) <= speed * t) return (t, at);
        }
        return null;
    }

    /// <summary>
    /// One tick of a missile homing on a flown vehicle, which has no known path: it re-aims at where the vehicle is now and flies <paramref name="speed"/>
    /// metres a second straight at it. <c>Hit</c> when it reaches within <paramref name="reach"/> of the vehicle this tick (the server's hit; the client draws the same step).
    /// </summary>
    public static (Vector3 Next, bool Hit) Pursue(Vector3 at, Vector3 target, float speed, float reach, float dt)
    {
        var to = target - at;
        var dist = to.Length();
        var step = speed * dt;
        if (dist <= step + reach || dist < 1e-4f) return (target, true);
        return (at + to / dist * step, false);
    }

    /// <summary>
    /// Where a homing missile is drawn <paramref name="dt"/> seconds on, given where it is, where its Aircraft is now and the seconds it has left:
    /// it closes the gap at the rate that lands it on the Aircraft exactly when the server's hit does, so its path bends after the Aircraft.
    /// </summary>
    public static Vector3 Chase(Vector3 at, Vector3 target, float left, float dt) =>
        left <= dt ? target : at + (target - at) * (dt / left);
}

/// <summary>
/// One player's Lock (<see cref="LockRules.Spec"/>). The host calls <see cref="Update"/> once a tick with the Aircraft the launcher looks at
/// (<see cref="LockRules.Pick"/>; null when the launcher is not in hand or nothing is in the circle), and <see cref="Reset"/> when the missile leaves.
/// </summary>
public sealed class LockOn
{
    readonly StateMachine<LockPhase> _fsm = new(LockRules.Spec);
    Countdown _grace;
    float _seconds = 1f;

    public LockPhase Phase => _fsm.Current;
    /// <summary>The Aircraft held (world entity id); 0 when <see cref="Phase"/> is Idle.</summary>
    public ushort Target { get; private set; }
    /// <summary>False while the held Aircraft is out of the circle or out of sight and the grace runs: the progress waits.</summary>
    public bool Tracking { get; private set; }
    /// <summary>Share of the Lock built, 0..1.</summary>
    public float Progress => Phase switch
    {
        LockPhase.Locked => 1f,
        LockPhase.Locking => Math.Clamp(_fsm.Elapsed / _seconds, 0f, 1f),
        _ => 0f,
    };

    /// <summary>One tick. <paramref name="seen"/> is the Aircraft the launcher looks at now, null for none.</summary>
    public void Update(float dt, LockDef def, ushort? seen)
    {
        if (_fsm.Is(LockPhase.Idle))
        {
            if (seen is { } first) Begin(def, first);
            return;
        }
        if (seen == Target)
        {
            _grace.Cancel();
            Tracking = true;
            if (_fsm.Is(LockPhase.Locking) && _fsm.Tick(dt)) _fsm.Enter(LockPhase.Locked);
            return;
        }
        // the held Aircraft is not what the launcher looks at: wait out the grace, then let go (and take the new one, if any)
        if (Tracking) { Tracking = false; _grace.Start(def.GraceSeconds); }
        if (_grace.Running && !_grace.Tick(dt)) return;
        Reset();
        if (seen is { } next) Begin(def, next);
    }

    void Begin(LockDef def, ushort target)
    {
        Target = target; Tracking = true; _seconds = MathF.Max(def.Seconds, 1e-3f);
        _grace.Cancel();
        _fsm.Enter(LockPhase.Locking, _seconds);
    }

    /// <summary>Lets go: the missile left, the launcher was stowed, or the player died.</summary>
    public void Reset()
    {
        Target = 0; Tracking = false;
        _grace.Cancel();
        _fsm.Enter(LockPhase.Idle);
    }
}

/// <summary>The shooter's own Lock, sent to them when its phase, Aircraft or tracking changes; the progress runs on from <c>Progress</c> over <c>Seconds</c> while tracking.</summary>
public readonly record struct LockStateMsg(LockPhase Phase, ushort Target, bool Tracking, float Progress, float Seconds);

/// <summary>What an Aircraft's owner is told about the launchers on their Aircraft (to the owner only).</summary>
public readonly record struct LockAlertMsg(LockAlert Level, ushort Aircraft);

/// <summary>A homing missile left a Lock-on launcher (broadcast): its id (the Explosion that ends it carries the same), who fired, the weapon, where from, the Aircraft it homes on and the seconds until it gets there.</summary>
public readonly record struct HomingLaunchMsg(ushort Id, ushort Owner, byte Weapon, Vector3 Origin, ushort Target, float Seconds);

/// <summary>Payloads of <see cref="EventKind.LockState"/>, <see cref="EventKind.LockAlert"/> and <see cref="EventKind.HomingLaunch"/> (little-endian).</summary>
public static class LockCodec
{
    public const int StateSize = 12, AlertSize = 3, LaunchSize = 23;

    public static byte[] Encode(LockStateMsg m)
    {
        var b = new byte[StateSize];
        b[0] = (byte)m.Phase;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(1), m.Target);
        b[3] = (byte)(m.Tracking ? 1 : 0);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(4), m.Progress);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(8), m.Seconds);
        return b;
    }

    public static LockStateMsg? DecodeState(ReadOnlySpan<byte> d) => d.Length < StateSize || d[0] > (byte)LockPhase.Locked ? null : new LockStateMsg(
        (LockPhase)d[0], BinaryPrimitives.ReadUInt16LittleEndian(d[1..]), d[3] != 0, BinaryPrimitives.ReadSingleLittleEndian(d[4..]), BinaryPrimitives.ReadSingleLittleEndian(d[8..]));

    public static byte[] Encode(LockAlertMsg m)
    {
        var b = new byte[AlertSize];
        b[0] = (byte)m.Level;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(1), m.Aircraft);
        return b;
    }

    public static LockAlertMsg? DecodeAlert(ReadOnlySpan<byte> d) => d.Length < AlertSize || d[0] > (byte)LockAlert.Launched ? null : new LockAlertMsg(
        (LockAlert)d[0], BinaryPrimitives.ReadUInt16LittleEndian(d[1..]));

    public static byte[] Encode(HomingLaunchMsg m)
    {
        var b = new byte[LaunchSize];
        BinaryPrimitives.WriteUInt16LittleEndian(b, m.Id);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), m.Owner);
        b[4] = m.Weapon;
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(5), m.Origin.X);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(9), m.Origin.Y);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(13), m.Origin.Z);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(17), m.Target);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(19), m.Seconds);
        return b;
    }

    public static HomingLaunchMsg? DecodeLaunch(ReadOnlySpan<byte> d) => d.Length < LaunchSize ? null : new HomingLaunchMsg(
        BinaryPrimitives.ReadUInt16LittleEndian(d), BinaryPrimitives.ReadUInt16LittleEndian(d[2..]), d[4],
        new Vector3(BinaryPrimitives.ReadSingleLittleEndian(d[5..]), BinaryPrimitives.ReadSingleLittleEndian(d[9..]), BinaryPrimitives.ReadSingleLittleEndian(d[13..])),
        BinaryPrimitives.ReadUInt16LittleEndian(d[17..]), BinaryPrimitives.ReadSingleLittleEndian(d[19..]));
}
