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
    /// <summary>Anchors this many links away are considered as a goal, and ones fewer than <see cref="MinHops"/> away are only taken when nothing else is.</summary>
    public int MaxHops { get; set; }
    /// <summary>The shortest route it likes (links): it walks, not shuffles.</summary>
    public int MinHops { get; set; } = 1;
    /// <summary>Score for an anchor that sees the target.</summary>
    public float LosBonus { get; set; }
    /// <summary>Score lost per link on the way.</summary>
    public float HopCost { get; set; }
    /// <summary>Score for a goal on another surface than the one it is on (the other wall, the ceiling): it does not stay on one wall.</summary>
    public float SurfaceChangeBonus { get; set; }
    /// <summary>Score for a goal on the ceiling, plus <see cref="CeilingHungerPerS"/> for each second since it was last up there, at most <see cref="CeilingHungerCap"/>.</summary>
    public float CeilingBonus { get; set; }
    public float CeilingHungerPerS { get; set; }
    public float CeilingHungerCap { get; set; }
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
/// The aggressive roar: it stops and roars for <see cref="DurationS"/> (0 turns roars off). It roars when it wakes (<see cref="OnWake"/>: the start of the fight, and after a wipe),
/// every <see cref="IntervalS"/> seconds while it fights, and when its hits left fall to each of <see cref="HealthShares"/> (the fight's phases).
/// </summary>
public sealed class DemonRoarDef
{
    public float DurationS { get; set; }
    /// <summary>[min, max] seconds between roars while it fights.</summary>
    public float[] IntervalS { get; set; } = Array.Empty<float>();
    /// <summary>Share of the hits left (1 full) at which it roars once, e.g. [0.66, 0.33].</summary>
    public float[] HealthShares { get; set; } = Array.Empty<float>();
    public bool OnWake { get; set; }
    /// <summary>Clients shake the camera of anyone within this far.</summary>
    public float ShakeRadiusM { get; set; }
}

/// <summary>
/// The leap to an anchor on another surface (0 <see cref="Chance"/> turns it off): the opposite wall, or wall to ceiling and back. A crouch of <see cref="CrouchS"/> on the anchor, then a scripted arc
/// at <see cref="SpeedMps"/> (it bows <see cref="ArcHeightM"/> off the straight line, into the room), then <see cref="LandRecoverS"/> on the new anchor.
/// Each time it would crawl on it leaps with <see cref="Chance"/> once <see cref="CooldownS"/> has passed; once <see cref="DueS"/> seconds of fighting have passed without one it leaps at the next chance.
/// </summary>
public sealed class DemonLeapDef
{
    /// <summary>Chance, each time it would crawl on, that it leaps instead (when an anchor qualifies and the cooldown is over).</summary>
    public float Chance { get; set; }
    public float MinRangeM { get; set; }
    public float MaxRangeM { get; set; }
    public float CrouchS { get; set; }
    public float SpeedMps { get; set; }
    public float ArcHeightM { get; set; }
    public float LandRecoverS { get; set; }
    public float CooldownS { get; set; }
    /// <summary>Score for the opposite wall (normals against each other): the leap across the nave it is known for, over the one up to the ceiling.</summary>
    public float AcrossBonus { get; set; }
    /// <summary>Fighting seconds since the last leap after which the next one is certain (0: never forced).</summary>
    public float DueS { get; set; }
    /// <summary>A landing anchor's normal must not point with its own: <c>dot &lt;= MaxNormalDot</c> (-1 opposite walls only, 0 also wall and ceiling, 1 anything but the same direction).</summary>
    public float MaxNormalDot { get; set; } = 0.5f;
}

/// <summary>
/// The flame from its mouth (0 <see cref="DurationS"/> turns it off): at a target within <see cref="RangeM"/> it rears for <see cref="WindupS"/>, then breathes for
/// <see cref="DurationS"/>. The stream is a cone round an axis that follows the target at <see cref="TrackDegPerS"/>; every <see cref="TickS"/> each living player in it
/// takes <see cref="DamagePerTick"/> and is set ablaze with the file's <see cref="DemonFile.Burn"/>.
/// </summary>
public sealed class DemonFlameDef
{
    public float RangeM { get; set; }
    public float ConeHalfAngleDeg { get; set; }
    public float WindupS { get; set; }
    public float DurationS { get; set; }
    public float CooldownS { get; set; }
    public float TrackDegPerS { get; set; }
    public float TickS { get; set; }
    public float DamagePerTick { get; set; }
    /// <summary>The mouth: this far off the surface along its normal, and ahead along its facing.</summary>
    public float HeadUpM { get; set; }
    public float HeadForwardM { get; set; }
}

/// <summary>
/// The pounce (0 <see cref="PinS"/> turns it off): a cut-scene attack. At a living player inside the church, in sight, on the ground and within <see cref="RangeM"/> the demon crouches
/// (<see cref="WindS"/>, the victim is marked), leaps at them along a homing arc, knocks them flat and pins them for <see cref="PinS"/> seconds, biting at their face; then their health is
/// set to <see cref="LeaveHp"/> whatever it was, they stand with <see cref="ImmunityS"/> of immunity to the demon's attacks, and it leaps away. <see cref="Chance"/> is rolled at the end of
/// each dwell once <see cref="CooldownS"/> has passed and <see cref="FirstAfterS"/> fighting seconds are over. The rest is the client's look: where the camera lies, the bite.
/// </summary>
public sealed class DemonPounceDef
{
    public float RangeM { get; set; }
    public float CooldownS { get; set; }
    /// <summary>The first pounce comes no sooner than this many fighting seconds after it woke.</summary>
    public float FirstAfterS { get; set; }
    /// <summary>Chance, at the end of a dwell, that it pounces (when a victim qualifies and the cooldown is over).</summary>
    public float Chance { get; set; }
    public float WindS { get; set; }
    /// <summary>How long the victim is pinned; 0 turns the attack off.</summary>
    public float PinS { get; set; }
    public int LeaveHp { get; set; }
    /// <summary>After the release the victim is immune to the demon's fire and flame this long.</summary>
    public float ImmunityS { get; set; }
    /// <summary>How far ahead of its root (the middle of its body, at scale 1) its head hangs over the victim: where the server first puts the demon; the clients then place the head exactly.</summary>
    public float HeadForwardM { get; set; }
    /// <summary>The client's look (seconds, metres). The camera lies <see cref="CameraHeightM"/> off the floor, the demon's muzzle <see cref="FaceDistanceM"/> above it.</summary>
    public float LockS { get; set; } = 0.25f;
    public float DropS { get; set; } = 0.3f;
    public float RiseS { get; set; } = 0.6f;
    public float CameraHeightM { get; set; } = 0.25f;
    public float FaceDistanceM { get; set; } = 0.62f;
    public float BitesPerS { get; set; } = 2.5f;
    public float LungeM { get; set; } = 0.06f;
}

/// <summary>The fight around the brain: the turning, the climb to the walls, the doors and the corpse. Seconds and metres.</summary>
public sealed class DemonFightDef
{
    /// <summary>The pause between the third pastor kill and the start of the burning.</summary>
    public float TransformDelayS { get; set; } = 1f;
    /// <summary>The whole turning: the priest burns until <see cref="TransformSwapS"/>, the demon stands and roars, and drops on all fours in the last <see cref="TransformDropS"/>. It cannot be hit meanwhile.</summary>
    public float TransformS { get; set; } = 7f;
    public float TransformSwapS { get; set; } = 3f;
    public float TransformDropS { get; set; } = 1f;
    /// <summary>After the turning it leaps from the floor to its first anchor in this time (hittable, not yet fighting).</summary>
    public float ClimbS { get; set; } = 1.5f;
    /// <summary>The doors shut again this long after a player is inside while it waits (they never shut on the first step).</summary>
    public float DoorGraceS { get; set; } = 3f;
    /// <summary>The dead demon stays this long, then the event is over.</summary>
    public float CorpseS { get; set; } = 8f;
    /// <summary>The middle of its body is this far off the surface it holds on to; the hit volume is centred there.</summary>
    public float BodyHeightM { get; set; } = 0.5f;
}

/// <summary>
/// holy_mass_demon.json, schema version 1: the demon of the Holy Mass event, its fireball and the burn (the fireball's, and the devil's curse's).
/// Optional like items.json; a separate file until it is merged with holy_mass.json. Every number lives here.
/// </summary>
public sealed class DemonFile : IVersioned
{
    public int Version { get; set; }
    /// <summary>How big the demon is: 1 is the pack's model. The view scales the model, and every length that belongs to its body (<see cref="BodyM"/>, <see cref="MuzzleM"/>,
    /// <see cref="FlameUpM"/>, <see cref="FlameForwardM"/>, the hit volume) follows; the numbers in the sections below stay those of a scale 1 demon.</summary>
    public float Scale { get; set; } = 1f;
    public DemonHitsDef Hits { get; set; } = new();
    public DemonHitWeights HitWeights { get; set; } = new();
    public DemonCrawlDef Crawl { get; set; } = new();
    public DemonFightDef Fight { get; set; } = new();
    public FireballDef Fireball { get; set; } = new();
    public DemonRoarDef Roar { get; set; } = new();
    public DemonLeapDef Leap { get; set; } = new();
    public DemonFlameDef Flame { get; set; } = new();
    /// <summary>Optional: without the section (or with <see cref="DemonPounceDef.PinS"/> 0) there is no pounce.</summary>
    public DemonPounceDef Pounce { get; set; } = new();
    public BurnDef Burn { get; set; } = new();

    public static DemonFile Load(string path) => Validate(DataFile.Load<DemonFile>(path));

    public static DemonFile Parse(string json) => Validate(DataFile.Parse<DemonFile>(json, "holy_mass_demon.json"));

    /// <summary>The number of hits that kill the demon with this many players inside (at least one is assumed).</summary>
    /// <summary>The middle of the body off the surface it holds on to.</summary>
    public float BodyM => Fight.BodyHeightM * Scale;
    /// <summary>The fireball's launch point and the sight lines' start: this far off the surface along its normal.</summary>
    public float MuzzleM => Fireball.MuzzleOffsetM * Scale;
    /// <summary>The mouth: off the surface along the normal, and ahead along the facing.</summary>
    public float FlameUpM => Flame.HeadUpM * Scale;
    public float FlameForwardM => Flame.HeadForwardM * Scale;

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

        Pos(f.Scale, "scale");
        var h = f.Hits;
        Pos(h.Base, "hits.base"); NonNeg(h.PerExtraPlayer, "hits.per_extra_player");
        if (h.Min < 1 || h.Max < h.Min) throw new DataFileException($"{F}: \"hits.min\" must be 1 or more and \"hits.max\" at least \"hits.min\"");
        var w = f.HitWeights;
        if (w.Bullet < 1 || w.Explosive < 1 || w.Vehicle < 1) throw new DataFileException($"{F}: every \"hit_weights\" entry is required and must be 1 or more");

        var c = f.Crawl;
        Pos(c.SpeedMps, "crawl.speed_mps"); Pos(c.PreferredRangeM, "crawl.preferred_range_m"); Pos(c.RetargetS, "crawl.retarget_s");
        NonNeg(c.LosBonus, "crawl.los_bonus"); NonNeg(c.HopCost, "crawl.hop_cost"); NonNeg(c.RevisitPenalty, "crawl.revisit_penalty");
        NonNeg(c.Jitter, "crawl.jitter"); NonNeg(c.RearmDelayS, "crawl.rearm_delay_s");
        NonNeg(c.SurfaceChangeBonus, "crawl.surface_change_bonus"); NonNeg(c.CeilingBonus, "crawl.ceiling_bonus");
        NonNeg(c.CeilingHungerPerS, "crawl.ceiling_hunger_per_s"); NonNeg(c.CeilingHungerCap, "crawl.ceiling_hunger_cap");
        if (c.MaxHops < 1) throw new DataFileException($"{F}: \"crawl.max_hops\" is required and must be 1 or more");
        if (c.MinHops < 1 || c.MinHops > c.MaxHops) throw new DataFileException($"{F}: \"crawl.min_hops\" must be between 1 and \"crawl.max_hops\"");
        if (c.DwellS is not { Length: 2 } d || !(d[0] > 0) || d[1] < d[0] || !float.IsFinite(d[1]))
            throw new DataFileException($"{F}: \"crawl.dwell_s\" must be [min, max] with 0 < min <= max");

        var ft = f.Fight;
        Pos(ft.TransformS, "fight.transform_s"); Pos(ft.TransformSwapS, "fight.transform_swap_s"); Pos(ft.TransformDropS, "fight.transform_drop_s");
        Pos(ft.ClimbS, "fight.climb_s"); Pos(ft.CorpseS, "fight.corpse_s"); Pos(ft.BodyHeightM, "fight.body_height_m");
        NonNeg(ft.TransformDelayS, "fight.transform_delay_s"); NonNeg(ft.DoorGraceS, "fight.door_grace_s");
        if (ft.TransformSwapS + ft.TransformDropS >= ft.TransformS) throw new DataFileException($"{F}: \"fight.transform_swap_s\" and \"transform_drop_s\" must fit inside \"transform_s\"");

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

        var r = f.Roar;
        if (r.DurationS > 0)
        {
            if (r.IntervalS is not { Length: 2 } ri || !(ri[0] > 0) || ri[1] < ri[0] || !float.IsFinite(ri[1]))
                throw new DataFileException($"{F}: \"roar.interval_s\" must be [min, max] with 0 < min <= max");
            foreach (var share in r.HealthShares) if (!(share > 0 && share < 1)) throw new DataFileException($"{F}: every \"roar.health_shares\" entry must be between 0 and 1");
            NonNeg(r.ShakeRadiusM, "roar.shake_radius_m");
        }
        var lp = f.Leap;
        if (lp.Chance > 0)
        {
            if (lp.Chance > 1) throw new DataFileException($"{F}: \"leap.chance\" must be between 0 and 1");
            Pos(lp.MinRangeM, "leap.min_range_m"); Pos(lp.MaxRangeM, "leap.max_range_m"); Pos(lp.CrouchS, "leap.crouch_s"); Pos(lp.SpeedMps, "leap.speed_mps");
            NonNeg(lp.ArcHeightM, "leap.arc_height_m"); NonNeg(lp.LandRecoverS, "leap.land_recover_s"); NonNeg(lp.CooldownS, "leap.cooldown_s");
            if (lp.MinRangeM >= lp.MaxRangeM) throw new DataFileException($"{F}: \"leap.min_range_m\" must be below \"leap.max_range_m\"");
            NonNeg(lp.DueS, "leap.due_s"); NonNeg(lp.AcrossBonus, "leap.across_bonus");
            if (!(lp.MaxNormalDot >= -1 && lp.MaxNormalDot < 1)) throw new DataFileException($"{F}: \"leap.max_normal_dot\" must be from -1 up to (not including) 1");
        }
        var fl = f.Flame;
        if (fl.DurationS > 0)
        {
            Pos(fl.RangeM, "flame.range_m"); Pos(fl.WindupS, "flame.windup_s"); Pos(fl.TrackDegPerS, "flame.track_deg_per_s"); Pos(fl.TickS, "flame.tick_s");
            Pos(fl.DamagePerTick, "flame.damage_per_tick"); NonNeg(fl.CooldownS, "flame.cooldown_s"); NonNeg(fl.HeadUpM, "flame.head_up_m"); NonNeg(fl.HeadForwardM, "flame.head_forward_m");
            if (!(fl.ConeHalfAngleDeg > 0 && fl.ConeHalfAngleDeg < 90)) throw new DataFileException($"{F}: \"flame.cone_half_angle_deg\" must be between 0 and 90");
        }

        var pn = f.Pounce;
        if (pn.PinS > 0)
        {
            Pos(pn.RangeM, "pounce.range_m"); Pos(pn.WindS, "pounce.wind_s"); NonNeg(pn.CooldownS, "pounce.cooldown_s"); NonNeg(pn.FirstAfterS, "pounce.first_after_s");
            NonNeg(pn.ImmunityS, "pounce.immunity_s"); NonNeg(pn.HeadForwardM, "pounce.head_forward_m");
            if (!(pn.Chance > 0 && pn.Chance <= 1)) throw new DataFileException($"{F}: \"pounce.chance\" must be above 0 and at most 1");
            if (pn.LeaveHp < 1 || pn.LeaveHp > MoveConsts.MaxHealth) throw new DataFileException($"{F}: \"pounce.leave_hp\" must be between 1 and {MoveConsts.MaxHealth}");
            Pos(pn.LockS, "pounce.lock_s"); Pos(pn.DropS, "pounce.drop_s"); Pos(pn.RiseS, "pounce.rise_s"); Pos(pn.CameraHeightM, "pounce.camera_height_m");
            Pos(pn.FaceDistanceM, "pounce.face_distance_m"); Pos(pn.BitesPerS, "pounce.bites_per_s"); NonNeg(pn.LungeM, "pounce.lunge_m");
        }

        Pos(f.Burn.Dps, "burn.dps"); Pos(f.Burn.DurationS, "burn.duration_s"); Pos(f.Burn.TickIntervalS, "burn.tick_interval_s");
        if (f.Burn.TickIntervalS > f.Burn.DurationS) throw new DataFileException($"{F}: \"burn.tick_interval_s\" must not exceed \"burn.duration_s\"");
        return f;
    }
}
