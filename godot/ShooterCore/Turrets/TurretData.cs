namespace Suchohrad.ShooterCore;

/// <summary>Tuning of the sentry turret (turret.json). Lengths are metres, angles degrees, times seconds.</summary>
public sealed class TurretDef
{
    /// <summary>Hit points. Shown to players as a percentage on the wire.</summary>
    public float Hp { get; init; }
    /// <summary>Lifetime after placement; the turret despawns when it runs out.</summary>
    public float LifetimeSeconds { get; init; }
    /// <summary>Furthest horizontal distance from the player's feet at which a turret can be placed.</summary>
    public float PlacementRangeM { get; init; }
    /// <summary>Steepest slope of the ground under a placed turret.</summary>
    public float PlacementSlopeDeg { get; init; }
    /// <summary>Largest height difference between the player's feet and the placement ground.</summary>
    public float PlacementStepM { get; init; }
    /// <summary>How far along the aim ray the placement probe looks for ground.</summary>
    public float PlacementProbeM { get; init; }
    /// <summary>Targets beyond this distance (and behind walls) are ignored.</summary>
    public float SightRangeM { get; init; }
    /// <summary>How fast the turret turns towards its target.</summary>
    public float TurnRateDegPerS { get; init; }
    /// <summary>Delay after a new target is acquired before the turret fires: the outplay window.</summary>
    public float AcquireDelayS { get; init; }
    /// <summary>The turret fires once its barrel is within this angle of the target.</summary>
    public float AimToleranceDeg { get; init; }
    /// <summary>Seconds between shots.</summary>
    public float FireIntervalS { get; init; }
    /// <summary>Damage per shot, before the attacker's perk (the turret's own shots are not perk-scaled).</summary>
    public float DamagePerShot { get; init; }
    /// <summary>A knife does one third of the turret's hp, so this many knife hits destroy it.</summary>
    public int KnifeHitsToDestroy { get; init; }
}

/// <summary>turret.json, schema version 1. Loaded through <see cref="DataFile"/>.</summary>
public sealed class TurretFile : IVersioned
{
    /// <summary>Newest turret.json version this code reads.</summary>
    public const int SupportedVersion = 1;

    public int Version { get; init; }
    public TurretDef Def { get; init; } = null!;

    /// <summary>Damage of one knife hit on a turret: a third of its hp, rounded up so that the required hits always destroy it.</summary>
    public float KnifeDamage => MathF.Ceiling(Def.Hp / Def.KnifeHitsToDestroy);

    public static TurretFile Load(string path) => Resolve(DataFile.Load<TurretRaw>(path, SupportedVersion));

    public static TurretFile Parse(string json) => Resolve(DataFile.Parse<TurretRaw>(json, "turret.json", SupportedVersion));

    static TurretFile Resolve(TurretRaw raw)
    {
        const string F = "turret.json";
        var d = new TurretDef
        {
            Hp = Positive(raw.Hp, F, "hp"),
            LifetimeSeconds = Positive(raw.LifetimeSeconds, F, "lifetime_seconds"),
            PlacementRangeM = Positive(raw.PlacementRangeM, F, "placement_range_m"),
            PlacementSlopeDeg = Range(raw.PlacementSlopeDeg, F, "placement_slope_deg", 0.1f, 89f),
            PlacementStepM = Positive(raw.PlacementStepM, F, "placement_step_m"),
            PlacementProbeM = Positive(raw.PlacementProbeM, F, "placement_probe_m"),
            SightRangeM = Positive(raw.SightRangeM, F, "sight_range_m"),
            TurnRateDegPerS = Positive(raw.TurnRateDegPerS, F, "turn_rate_deg_per_s"),
            AcquireDelayS = Range(raw.AcquireDelayS, F, "acquire_delay_s", 0f, 10f),
            AimToleranceDeg = Range(raw.AimToleranceDeg, F, "aim_tolerance_deg", 0.1f, 45f),
            FireIntervalS = Positive(raw.FireIntervalS, F, "fire_interval_s"),
            DamagePerShot = Positive(raw.DamagePerShot, F, "damage_per_shot"),
            KnifeHitsToDestroy = raw.KnifeHitsToDestroy is { } k && k >= 1 ? k
                : throw new DataFileException($"{F}: field \"knife_hits_to_destroy\" is required and must be 1 or more"),
        };
        if (d.PlacementProbeM < d.PlacementRangeM)
            throw new DataFileException($"{F}: field \"placement_probe_m\" must be at least \"placement_range_m\"");
        return new TurretFile { Version = raw.Version, Def = d };
    }

    static float Positive(float? v, string file, string field) =>
        v is { } x && float.IsFinite(x) && x > 0 ? x : throw new DataFileException($"{file}: field \"{field}\" is required and must be above 0");

    static float Range(float? v, string file, string field, float min, float max) =>
        v is { } x && float.IsFinite(x) && x >= min && x <= max ? x
            : throw new DataFileException($"{file}: field \"{field}\" is required and must be between {min} and {max}");

    internal sealed class TurretRaw : IVersioned
    {
        public int Version { get; set; }
        public float? Hp { get; set; }
        public float? LifetimeSeconds { get; set; }
        public float? PlacementRangeM { get; set; }
        public float? PlacementSlopeDeg { get; set; }
        public float? PlacementStepM { get; set; }
        public float? PlacementProbeM { get; set; }
        public float? SightRangeM { get; set; }
        public float? TurnRateDegPerS { get; set; }
        public float? AcquireDelayS { get; set; }
        public float? AimToleranceDeg { get; set; }
        public float? FireIntervalS { get; set; }
        public float? DamagePerShot { get; set; }
        public int? KnifeHitsToDestroy { get; set; }
    }
}
