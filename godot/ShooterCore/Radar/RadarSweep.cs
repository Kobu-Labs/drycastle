namespace Drycastle.ShooterCore;

/// <summary>What a radar contact is: a person on foot (a target dummy counts as one) or a vehicle with somebody aboard. Wire byte of <see cref="RadarCodec"/>; do not renumber.</summary>
public enum RadarKind : byte { Player = 0, Vehicle = 1 }

/// <summary>A thing the radar could show: the id of the player or vehicle and its ground position (x, z).</summary>
public readonly record struct RadarBlip(ushort Id, RadarKind Kind, float X, float Z);

/// <summary>A swept contact: where the sweep last found it and how long ago (<see cref="Age"/>, seconds; the client fades it over one revolution).</summary>
public readonly record struct RadarContact(ushort Id, RadarKind Kind, float X, float Z, float Age);

/// <summary>The states of <see cref="RadarSweep"/>.</summary>
public enum RadarSweepState : byte { Off, Sweeping }

/// <summary>
/// The sweep of a radar unit (GLOSSARY "Radar unit"; ADR 0037). The dish turns once every <see cref="Revolution"/> seconds while the unit is hitched (the caller says so with <see cref="Power"/>); the sweep
/// arm points along a bearing about the unit (0 = -Z, the map's north, turning clockwise as seen from above: +X is 90 degrees) and reaches the whole map. A contact is stamped, with its position at that
/// moment, when the arm passes its bearing; between passes it stays where it was stamped, and a stamp older than a revolution is dropped (the contact was not found again). Pure: the server keeps one per
/// radar unit and sends <see cref="Collect"/> to the operator alone.
/// </summary>
public sealed class RadarSweep
{
    /// <summary>The state clock is the sweep: <see cref="StateMachine{TState}.Elapsed"/> is the seconds into the present revolution, and the countdown given to Sweeping ends it.</summary>
    public static readonly StateMachineSpec<RadarSweepState> Spec = StateMachineSpec<RadarSweepState>.Define("RadarUnit.Sweep", "A radar unit's dish: still, or turning and stamping the contacts its arm passes.", RadarSweepState.Off)
        .Allow(RadarSweepState.Off, RadarSweepState.Sweeping)
        .Allow(RadarSweepState.Sweeping, RadarSweepState.Off)
        .Note(RadarSweepState.Off, "Not hitched to a tractor: the dish is still, nothing is stamped. Hitching it starts the sweep from bearing 0.")
        .Note(RadarSweepState.Sweeping, "Hitched: the arm turns once per revolution (6 s) and stamps every contact it passes. Its clock restarts each revolution. Unhitching stops it and forgets every stamp.")
        .Build();

    /// <summary>The revolution time of the shipped unit, seconds (vehicles.json <c>radar.revolution_seconds</c>).</summary>
    public const float DefaultRevolution = 6f;

    readonly StateMachine<RadarSweepState> _fsm = new(Spec);
    readonly Dictionary<ushort, Mark> _stamps = new();
    long _turn;

    readonly record struct Mark(RadarKind Kind, float X, float Z, double At);

    public RadarSweep(float revolutionSeconds = DefaultRevolution)
    {
        if (!(revolutionSeconds > 0.5f) || !float.IsFinite(revolutionSeconds)) throw new ArgumentOutOfRangeException(nameof(revolutionSeconds));
        Revolution = revolutionSeconds;
    }

    /// <summary>Seconds for the arm to go once round.</summary>
    public float Revolution { get; }
    public bool Sweeping => _fsm.Is(RadarSweepState.Sweeping);

    /// <summary>The arm's bearing now, radians in [0, 2 pi): 0 along -Z, increasing clockwise from above. 0 while the dish is still.</summary>
    public float Angle => Sweeping ? MathF.Tau * Math.Clamp(_fsm.Elapsed / Revolution, 0f, 1f) % MathF.Tau : 0f;

    /// <summary>Seconds since the sweep started, over every revolution (the stamps' clock).</summary>
    double Clock => _turn * (double)Revolution + _fsm.Elapsed;

    /// <summary>The unit is hitched (<paramref name="on"/>) or not: starts the sweep from bearing 0, or stops it and forgets every stamp. True when that changed it.</summary>
    public bool Power(bool on)
    {
        if (on) { if (!_fsm.Enter(RadarSweepState.Sweeping, Revolution)) return false; _turn = 0; return true; }
        if (!_fsm.Enter(RadarSweepState.Off)) return false;
        _stamps.Clear();
        return true;
    }

    /// <summary>Forgets every stamp (nobody is at the screen: the next operator starts with a blank one). The arm keeps turning.</summary>
    public void Clear() => _stamps.Clear();

    /// <summary>The bearing of the point (<paramref name="dx"/>, <paramref name="dz"/>) from the unit as a fraction of a revolution in [0, 1): 0 along -Z, clockwise.</summary>
    public static float Fraction(float dx, float dz)
    {
        var a = MathF.Atan2(dx, -dz); // -pi..pi, 0 at -Z, +X is +pi/2
        if (a < 0) a += MathF.Tau;
        var f = a / MathF.Tau;
        return f >= 1f ? 0f : f;
    }

    /// <summary>
    /// One tick: the arm turns by <paramref name="dt"/> about the unit at (<paramref name="unitX"/>, <paramref name="unitZ"/>) and stamps every contact of <paramref name="contacts"/> (the enemies as they are
    /// now) whose bearing it passes, at the moment it passes; then stamps older than a revolution are dropped. Nothing happens while the dish is still.
    /// </summary>
    public void Tick(float dt, float unitX, float unitZ, IReadOnlyList<RadarBlip> contacts)
    {
        if (!Sweeping || dt <= 0) return;
        var left = dt;
        while (left > 0)
        {
            var toEnd = MathF.Max(0f, _fsm.Left); // seconds to the end of this revolution
            var step = MathF.Min(left, toEnd);
            var from = _fsm.Elapsed;
            _fsm.Tick(step);
            Stamp(from, from + step, unitX, unitZ, contacts);
            left -= step;
            if (_fsm.Expired) { _turn++; _fsm.Restart(Revolution); }
            else break; // the whole dt fitted inside this revolution
        }
        var now = Clock;
        List<ushort>? old = null;
        foreach (var (id, s) in _stamps) if (now - s.At > Revolution) (old ??= new()).Add(id);
        if (old != null) foreach (var id in old) _stamps.Remove(id);
    }

    /// <summary>Stamps the contacts whose bearing falls in (<paramref name="from"/>, <paramref name="to"/>] seconds of the revolution.</summary>
    void Stamp(float from, float to, float ux, float uz, IReadOnlyList<RadarBlip> contacts)
    {
        var turnStart = _turn * (double)Revolution;
        foreach (var c in contacts)
        {
            var at = Fraction(c.X - ux, c.Z - uz) * Revolution;
            if (at == 0f) at = Revolution; // due north: the arm gets there as the revolution ends
            if (at > from && at <= to) _stamps[c.Id] = new Mark(c.Kind, c.X, c.Z, turnStart + at);
        }
    }

    /// <summary>The contacts the sweep has found and not lost, with their age now: a stamp is at most one revolution old. Appends to <paramref name="into"/> in id order.</summary>
    public void Collect(List<RadarContact> into)
    {
        var now = Clock;
        var start = into.Count;
        foreach (var (id, s) in _stamps) into.Add(new RadarContact(id, s.Kind, s.X, s.Z, (float)Math.Max(0, now - s.At)));
        into.Sort(start, into.Count - start, Comparer<RadarContact>.Create((a, b) => a.Id.CompareTo(b.Id)));
    }

    public int Count => _stamps.Count;
}
