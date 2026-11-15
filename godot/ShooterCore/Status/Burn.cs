namespace Drycastle.ShooterCore;

/// <summary>Burn tuning (<c>burn</c> of holy_mass_demon.json). Seconds and hit points.</summary>
public sealed class BurnDef
{
    /// <summary>Hit points lost per second.</summary>
    public float Dps { get; set; }
    /// <summary>How long a burn lasts from its last ignition.</summary>
    public float DurationS { get; set; }
    /// <summary>Damage is dealt in lumps of <c>Dps * TickIntervalS</c> (health events are whole hit points on the wire, so not every tick).</summary>
    public float TickIntervalS { get; set; }
}

/// <summary>Damage one burning player takes now, credited to <see cref="Source"/> (<see cref="BurnSet.NoSource"/> for the demon's fire).</summary>
public readonly record struct BurnTick(ushort Victim, ushort Source, float Damage);

/// <summary>
/// Everyone who is burning: damage over time, one burn per victim. A new ignition refreshes the burn (the full duration again, the latest
/// igniter is credited) and never stacks. Pure: the host calls <see cref="Step"/> each tick and deals the returned ticks (the kill is the
/// source's), calls <see cref="Extinguish"/> when the victim dies and <see cref="Clear"/> at a round start.
/// </summary>
public sealed class BurnSet
{
    /// <summary>Source of the demon's own fire (a fireball): no player is credited.</summary>
    public const ushort NoSource = ushort.MaxValue;

    const float Eps = 1e-3f;

    sealed class Burn
    {
        public ushort Source;
        public float Remaining, Accum, Dps, Interval;
    }

    readonly Dictionary<ushort, Burn> _burns = new();

    public int Count => _burns.Count;

    public bool IsBurning(ushort victim) => _burns.ContainsKey(victim);

    /// <summary>Sets the victim ablaze, or refreshes its burn: full duration again, credited to <paramref name="source"/>.</summary>
    public void Ignite(ushort victim, ushort source, BurnDef def)
    {
        if (!_burns.TryGetValue(victim, out var b)) _burns[victim] = b = new Burn();
        b.Source = source; b.Dps = def.Dps; b.Interval = def.TickIntervalS; b.Remaining = def.DurationS;
    }

    public void Extinguish(ushort victim) => _burns.Remove(victim);

    public void Clear() => _burns.Clear();

    /// <summary>Advances every burn by <paramref name="dt"/> and appends the damage due to <paramref name="ticks"/>. The last, partial tick is paid when the burn ends, so a full burn deals exactly <c>Dps * DurationS</c>.</summary>
    public void Step(float dt, List<BurnTick> ticks)
    {
        List<ushort>? ended = null;
        foreach (var (victim, b) in _burns)
        {
            var span = MathF.Min(dt, b.Remaining);
            b.Remaining -= span;
            b.Accum += span;
            while (b.Accum >= b.Interval - Eps) { ticks.Add(new BurnTick(victim, b.Source, b.Dps * b.Interval)); b.Accum -= b.Interval; }
            if (b.Remaining > Eps) continue;
            if (b.Accum > Eps) ticks.Add(new BurnTick(victim, b.Source, b.Dps * b.Accum));
            (ended ??= new()).Add(victim);
        }
        if (ended != null) foreach (var v in ended) _burns.Remove(v);
    }
}

/// <summary>The devil's curse (<see cref="StatusKind.Curse"/>): the cursed player's hits ignite the victim.</summary>
public static class CurseRules
{
    /// <summary>
    /// A hit by <paramref name="attacker"/> on <paramref name="victim"/> that did not kill: ignites when the attacker is cursed (not for self damage).
    /// Returns whether it ignited. The host calls this after its damage step when the result is <see cref="DamageResult.Damaged"/>.
    /// </summary>
    public static bool OnHit(PlayerStatuses? attacker, ushort attackerId, ushort victim, BurnSet burns, BurnDef def)
    {
        if (attacker == null || attackerId == victim || !attacker.Has(StatusKind.Curse)) return false;
        burns.Ignite(victim, attackerId, def);
        return true;
    }

    /// <summary>Curses every credited player of a slain demon (<see cref="DemonDied"/>) that still has a status set; returns the ones who got it.</summary>
    public static List<ushort> Grant(IEnumerable<ushort> credited, Func<ushort, PlayerStatuses?> statusesOf)
    {
        var granted = new List<ushort>();
        foreach (var id in credited)
            if (statusesOf(id) is { } s && s.Grant(StatusKind.Curse)) granted.Add(id);
        return granted;
    }
}
