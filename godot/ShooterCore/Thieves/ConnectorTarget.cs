using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>
/// The <c>loot.connector</c> sub-section of thieves.json (docs/events/thieves/07). A pole is robbed again after <see cref="PoleCooldownS"/>; at the top of the climb a Thief is electrocuted with
/// <see cref="ElectrocuteChance"/>: Shocked for <see cref="ShockSeconds"/>, Falling, Down for <see cref="DownSeconds"/>, GetUp for <see cref="GetUpSeconds"/>, then it climbs the same pole again.
/// The base of the climb is a navigation-mesh point <see cref="BaseOffsetM"/> beside the column; the top is the highest wire seat, or <see cref="TopFraction"/> of the pole's height without one.
/// </summary>
public sealed class ConnectorLootConfig : LootKindConfig
{
    public ConnectorLootConfig() { StealSeconds = 6f; }

    public float PoleCooldownS { get; set; } = 120f;
    public float ElectrocuteChance { get; set; } = 0.05f;
    public float ShockSeconds { get; set; } = 2f;
    public float DownSeconds { get; set; } = 4f;
    public float GetUpSeconds { get; set; } = 1.2f;
    public float BaseOffsetM { get; set; } = 0.9f;
    public float TopFraction { get; set; } = 0.9f;

    public override void Validate(string file, string kind)
    {
        base.Validate(file, kind);
        void Positive(float v, string what) { if (!(v > 0) || !float.IsFinite(v)) throw new DataFileException($"{file}: loot.connector.{what} must be positive"); }
        if (!(PoleCooldownS >= 0) || !float.IsFinite(PoleCooldownS)) throw new DataFileException($"{file}: loot.connector.pole_cooldown_s must not be negative");
        if (!(ElectrocuteChance >= 0 && ElectrocuteChance <= 1)) throw new DataFileException($"{file}: loot.connector.electrocute_chance must be between 0 and 1");
        Positive(ShockSeconds, "shock_seconds"); Positive(DownSeconds, "down_seconds"); Positive(GetUpSeconds, "get_up_seconds"); Positive(BaseOffsetM, "base_offset_m");
        if (!(TopFraction > 0 && TopFraction <= 1)) throw new DataFileException($"{file}: loot.connector.top_fraction must be above 0 and at most 1");
    }
}

/// <summary>A pole with wires as the connector target sees it: its id (the adapter's, stable), the column's foot, the climb's base (on the ground beside the column) and its top (where the Thief works, straight above the base).</summary>
public readonly record struct PoleSpot(int Id, Vector3 Column, Vector3 Base, Vector3 Top);

/// <summary>The poles the connectors are stolen from (the server's adapter over the map, its navigation mesh and its objects).</summary>
public interface IPoles
{
    /// <summary>Every pole with wires, in a fixed order.</summary>
    IReadOnlyList<PoleSpot> All();
    bool TryGet(int id, out PoleSpot pole);
}

/// <summary>
/// The connector target (docs/events/thieves/07): one target per pole with wires. The Thief climbs to the top (a target with a <see cref="StealTarget.Top"/>), works there and climbs down with a
/// connector. Nothing goes missing on the pole, so <see cref="Take"/> only starts the pole's cooldown (<see cref="ConnectorLootConfig.PoleCooldownS"/>, on the clock the source is given: the pole is not a
/// target meanwhile) and <see cref="Return"/> does nothing: a connector sent back is just removed. A new round clears the cooldowns. At the top the source rolls the electrocution
/// (<see cref="AloftHazard"/>) with <see cref="ConnectorLootConfig.ElectrocuteChance"/>.
/// </summary>
public sealed class ConnectorSource : IStealSource
{
    readonly ConnectorLootConfig _cfg;
    readonly IPoles _poles;
    readonly Func<float> _now;
    readonly Dictionary<int, float> _readyAt = new();   // pole id -> the time it can be robbed again

    public ConnectorSource(ConnectorLootConfig cfg, IPoles poles, Func<float> seconds) { _cfg = cfg; _poles = poles; _now = seconds; }

    public LootKind Kind => LootKind.Connector;

    /// <summary>Whether the pole is not in its cooldown.</summary>
    bool Ready(int id) => !_readyAt.TryGetValue(id, out var t) || _now() >= t;

    static StealTarget TargetOf(PoleSpot p) => new(LootKind.Connector, p.Id, p.Column, p.Base, p.Top);

    public void FreeNear(Vector3 near, float radius, List<StealTarget> into)
    {
        foreach (var p in _poles.All())
        {
            if (!Ready(p.Id)) continue;
            var dx = p.Column.X - near.X; var dz = p.Column.Z - near.Z;
            if (dx * dx + dz * dz <= radius * radius) into.Add(TargetOf(p));
        }
    }

    public void Places(List<Vector2> into)
    {
        foreach (var p in _poles.All())
            if (Ready(p.Id)) into.Add(new Vector2(p.Column.X, p.Column.Z));
    }

    public bool TryFind(int id, out StealTarget target)
    {
        target = default;
        if (!Ready(id) || !_poles.TryGet(id, out var p)) return false;
        target = TargetOf(p);
        return true;
    }

    public void Take(int id) => _readyAt[id] = _now() + _cfg.PoleCooldownS;

    /// <summary>Nothing went missing on the pole: nothing to put back.</summary>
    public void Return(int id) { }

    public void Reset() => _readyAt.Clear();

    public AloftShock? AloftHazard(int id, Random rng) =>
        rng.NextDouble() < _cfg.ElectrocuteChance ? new AloftShock(_cfg.ShockSeconds, _cfg.DownSeconds, _cfg.GetUpSeconds) : null;
}
