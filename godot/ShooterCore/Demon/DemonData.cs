namespace Drycastle.ShooterCore;

/// <summary>How many hits the demon takes: <c>round(Base + PerExtraPlayer * (players - 1))</c>, kept in <c>[Min, Max]</c>.</summary>
public sealed class DemonHitsDef
{
    public float Base { get; set; }
    public float PerExtraPlayer { get; set; }
    public int Min { get; set; }
    public int Max { get; set; }
}

/// <summary>What one hit counts, by weapon kind. No headshot bonus, so there is no field for one.</summary>
public sealed class DemonHitWeights
{
    public int Bullet { get; set; }
    public int Explosive { get; set; }
    public int Vehicle { get; set; }
}

/// <summary>How the demon crawls and picks where to go. Metres, seconds.</summary>
public sealed class DemonCrawlDef
{
    public float SpeedMps { get; set; }
    /// <summary>[min, max] seconds it rests on an anchor before it moves on.</summary>
    public float[] DwellS { get; set; } = Array.Empty<float>();
    /// <summary>The distance to its target it likes to keep.</summary>
    public float PreferredRangeM { get; set; }
    /// <summary>Anchors this many links away are considered as a goal.</summary>
    public int MaxHops { get; set; }
    /// <summary>Score for an anchor that sees the target.</summary>
    public float LosBonus { get; set; }
    /// <summary>Score lost per link on the way.</summary>
    public float HopCost { get; set; }
    /// <summary>Score lost for an anchor it was on a moment ago.</summary>
    public float RevisitPenalty { get; set; }
    /// <summary>Random score added to each candidate (seeded), so it is not predictable.</summary>
    public float Jitter { get; set; }
    /// <summary>It keeps its target this long before it takes the nearest again (sooner when the target dies).</summary>
    public float RetargetS { get; set; }
    /// <summary>After a wipe it heals and stays still this long, then wakes when a player is inside.</summary>
    public float RearmDelayS { get; set; }
}

/// <summary>A fireball's blast, like a weapon's splash (<see cref="SplashDef"/>): <see cref="FireballDef.Damage"/> inside <see cref="FullDamageRadiusM"/>, down to <see cref="EdgeDamage"/> at <see cref="RadiusM"/>.</summary>
public sealed class FireballSplashDef
{
    public float RadiusM { get; set; }
    public float FullDamageRadiusM { get; set; }
    public float EdgeDamage { get; set; }
    public float KnockbackSpeed { get; set; }
}

/// <summary>The demon's fireball: an arcing projectile. Metres, seconds.</summary>
public sealed class FireballDef
{
    public float SpeedMps { get; set; }
    public float GravityMps2 { get; set; }
    /// <summary>Flight time after which it vanishes.</summary>
    public float LifeS { get; set; }
    public float CooldownS { get; set; }
    /// <summary>The wind-up between the telegraph and the launch.</summary>
    public float TelegraphS { get; set; }
    public float MinRangeM { get; set; }
    public float MaxRangeM { get; set; }
    /// <summary>The launch point: this far from the anchor along its normal.</summary>
    public float MuzzleOffsetM { get; set; }
    /// <summary>The height above a target's feet it aims at.</summary>
    public float AimHeightM { get; set; }
    /// <summary>0 aims where the target is, 1 where it will be if it keeps its velocity.</summary>
    public float LeadFactor { get; set; }
    /// <summary>Direct hit damage; the splash falls off from it.</summary>
    public float Damage { get; set; }
    public FireballSplashDef Splash { get; set; } = new();
    /// <summary>Everyone the blast hurts (and a direct hit) is set ablaze with the file's <see cref="DemonFile.Burn"/>.</summary>
    public bool Ignites { get; set; }
}

/// <summary>
/// holy_mass_demon.json, schema version 1: the demon of the Holy Mass event, its fireball and the burn (the fireball's, and the devil's curse's).
/// Optional like items.json; a separate file until it is merged with holy_mass.json. Every number lives here.
/// </summary>
public sealed class DemonFile : IVersioned
{
    public int Version { get; set; }
    public DemonHitsDef Hits { get; set; } = new();
    public DemonHitWeights HitWeights { get; set; } = new();
    public DemonCrawlDef Crawl { get; set; } = new();
    public FireballDef Fireball { get; set; } = new();
    public BurnDef Burn { get; set; } = new();

    public static DemonFile Load(string path) => Validate(DataFile.Load<DemonFile>(path));

    public static DemonFile Parse(string json) => Validate(DataFile.Parse<DemonFile>(json, "holy_mass_demon.json"));

    /// <summary>The number of hits that kill the demon with this many players inside (at least one is assumed).</summary>
    public int HitsNeeded(int players)
    {
        var n = MathF.Round(Hits.Base + Hits.PerExtraPlayer * (Math.Max(players, 1) - 1));
        return (int)Math.Clamp(n, Hits.Min, Hits.Max);
    }

    public int WeightOf(DemonHitKind kind) => kind switch
    {
        DemonHitKind.Explosive => HitWeights.Explosive,
        DemonHitKind.Vehicle => HitWeights.Vehicle,
        _ => HitWeights.Bullet,
    };

    public static DemonFile Validate(DemonFile f)
    {
        const string F = "holy_mass_demon.json";
        static void Pos(float v, string field) { if (!float.IsFinite(v) || v <= 0) throw new DataFileException($"{F}: \"{field}\" is required and must be above 0"); }
        static void NonNeg(float v, string field) { if (!float.IsFinite(v) || v < 0) throw new DataFileException($"{F}: \"{field}\" must be 0 or more"); }

        var h = f.Hits;
        Pos(h.Base, "hits.base"); NonNeg(h.PerExtraPlayer, "hits.per_extra_player");
        if (h.Min < 1 || h.Max < h.Min) throw new DataFileException($"{F}: \"hits.min\" must be 1 or more and \"hits.max\" at least \"hits.min\"");
        var w = f.HitWeights;
        if (w.Bullet < 1 || w.Explosive < 1 || w.Vehicle < 1) throw new DataFileException($"{F}: every \"hit_weights\" entry is required and must be 1 or more");

        var c = f.Crawl;
        Pos(c.SpeedMps, "crawl.speed_mps"); Pos(c.PreferredRangeM, "crawl.preferred_range_m"); Pos(c.RetargetS, "crawl.retarget_s");
        NonNeg(c.LosBonus, "crawl.los_bonus"); NonNeg(c.HopCost, "crawl.hop_cost"); NonNeg(c.RevisitPenalty, "crawl.revisit_penalty");
        NonNeg(c.Jitter, "crawl.jitter"); NonNeg(c.RearmDelayS, "crawl.rearm_delay_s");
        if (c.MaxHops < 1) throw new DataFileException($"{F}: \"crawl.max_hops\" is required and must be 1 or more");
        if (c.DwellS is not { Length: 2 } d || !(d[0] > 0) || d[1] < d[0] || !float.IsFinite(d[1]))
            throw new DataFileException($"{F}: \"crawl.dwell_s\" must be [min, max] with 0 < min <= max");

        var b = f.Fireball;
        Pos(b.SpeedMps, "fireball.speed_mps"); Pos(b.GravityMps2, "fireball.gravity_mps2"); Pos(b.LifeS, "fireball.life_s");
        Pos(b.CooldownS, "fireball.cooldown_s"); Pos(b.TelegraphS, "fireball.telegraph_s"); Pos(b.MaxRangeM, "fireball.max_range_m");
        NonNeg(b.MinRangeM, "fireball.min_range_m"); NonNeg(b.MuzzleOffsetM, "fireball.muzzle_offset_m"); NonNeg(b.AimHeightM, "fireball.aim_height_m");
        Pos(b.Damage, "fireball.damage");
        if (b.MinRangeM >= b.MaxRangeM) throw new DataFileException($"{F}: \"fireball.min_range_m\" must be below \"fireball.max_range_m\"");
        if (!(b.LeadFactor >= 0 && b.LeadFactor <= 1)) throw new DataFileException($"{F}: \"fireball.lead_factor\" must be between 0 and 1");
        var s = b.Splash;
        Pos(s.RadiusM, "fireball.splash.radius_m"); NonNeg(s.FullDamageRadiusM, "fireball.splash.full_damage_radius_m");
        NonNeg(s.EdgeDamage, "fireball.splash.edge_damage"); NonNeg(s.KnockbackSpeed, "fireball.splash.knockback_speed");
        if (s.FullDamageRadiusM > s.RadiusM) throw new DataFileException($"{F}: \"fireball.splash.full_damage_radius_m\" must not exceed \"radius_m\"");

        Pos(f.Burn.Dps, "burn.dps"); Pos(f.Burn.DurationS, "burn.duration_s"); Pos(f.Burn.TickIntervalS, "burn.tick_interval_s");
        if (f.Burn.TickIntervalS > f.Burn.DurationS) throw new DataFileException($"{F}: \"burn.tick_interval_s\" must not exceed \"burn.duration_s\"");
        return f;
    }
}
