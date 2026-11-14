namespace Drycastle.ShooterCore;

/// <summary>
/// thieves.json, schema version 1: every number of the Thieves event (GLOSSARY "Thieves event"). Lengths are metres, speeds metres per second, times seconds.
/// <see cref="Gang"/> (ticket 02) holds the Gang members; the van and the Loot caps arrive with the tickets that need them (docs/events/thieves/).
/// </summary>
public sealed class ThievesFile : IVersioned
{
    public int Version { get; set; }

    public GangConfig Gang { get; set; } = new();
    /// <summary>The Raid van (ticket 03): its numbers, the road choices and the look.</summary>
    public VanConfig Van { get; set; } = new();
    /// <summary>The sequence of Raids that makes the event (ticket 03): how many, how fast, how the crew grows with the players.</summary>
    public RaidsConfig Raids { get; set; } = new();
    /// <summary>Voice lines, the speaker and the songs (ticket 09): <see cref="ThievesAudioConfig"/>.</summary>
    public ThievesAudioConfig Audio { get; set; } = new();

    /// <summary>The event's reward (ticket 10).</summary>
    public ThievesRewardConfig Reward { get; set; } = new();

    /// <summary>How the Thieves steal, how Loot is sent back, and one sub-section per kind of Loot (ticket 04 and the target kinds after it). Last section of the file.</summary>
    public LootConfig Loot { get; set; } = new();

    public static ThievesFile Load(string path) => Validate(DataFile.Load<ThievesFile>(path));

    public static ThievesFile Parse(string json) => Validate(DataFile.Parse<ThievesFile>(json, "thieves.json"));

    public static ThievesFile Validate(ThievesFile f)
    {
        const string F = "thieves.json";
        void Positive(float v, string what) { if (!(v > 0) || !float.IsFinite(v)) throw new DataFileException($"{F}: {what} must be positive"); }
        void NotNegative(float v, string what) { if (!(v >= 0) || !float.IsFinite(v)) throw new DataFileException($"{F}: {what} must not be negative"); }
        var g = f.Gang;
        if (g.Raid.Thieves < 0 || g.Raid.Enforcers < 0 || g.Raid.GangBosses < 0 || g.Raid.Size < 1) throw new DataFileException($"{F}: gang.raid counts are not negative and at least one member");
        if (g.Raid.Size > GangRules.MaxCrew) throw new DataFileException($"{F}: gang.raid has more than {GangRules.MaxCrew} members");
        foreach (var (name, k) in new[] { ("thief", g.Thief), ("enforcer", g.Enforcer), ("gang_boss", g.GangBoss) })
        {
            Positive(k.Hits, $"gang.{name}.hits"); Positive(k.WalkSpeed, $"gang.{name}.walk_speed"); Positive(k.RunSpeed, $"gang.{name}.run_speed"); Positive(k.HitScale, $"gang.{name}.hit_scale");
            Positive(k.Melee.Damage, $"gang.{name}.melee.damage"); Positive(k.Melee.ReachM, $"gang.{name}.melee.reach_m");
            Positive(k.Melee.WindupSeconds, $"gang.{name}.melee.windup_seconds");
            if (!(k.Melee.IntervalSeconds > k.Melee.WindupSeconds)) throw new DataFileException($"{F}: gang.{name}.melee.interval_seconds is longer than windup_seconds");
        }
        var h = g.Hits;
        Positive(h.HeadshotWeight, "gang.hits.headshot_weight"); NotNegative(h.ExplosiveWeight, "gang.hits.explosive_weight"); NotNegative(h.VehicleWeaponWeight, "gang.hits.vehicle_weapon_weight");
        Positive(g.Aggro.CrowdM, "gang.aggro.crowd_m"); Positive(g.Aggro.GiveUpM, "gang.aggro.give_up_m");
        if (!(g.Aggro.GiveUpM > g.Aggro.CrowdM)) throw new DataFileException($"{F}: gang.aggro.give_up_m is longer than crowd_m");
        Positive(g.CorpseSeconds, "gang.corpse_seconds"); NotNegative(g.KillXp, "gang.kill_xp");
        Positive(g.TurnRateDegPerS, "gang.turn_rate_deg_per_s"); Positive(g.RepathSeconds, "gang.repath_seconds"); Positive(g.RepathM, "gang.repath_m");
        Positive(g.GuardFollowM, "gang.guard_follow_m"); Positive(g.WanderM, "gang.wander_m");
        Positive(g.IdleMinSeconds, "gang.idle_min_seconds");
        if (!(g.IdleMaxSeconds >= g.IdleMinSeconds)) throw new DataFileException($"{F}: gang.idle_max_seconds is not below idle_min_seconds");
        Positive(g.WalkGiveUpSeconds, "gang.walk_give_up_seconds"); Positive(g.FleeSeconds, "gang.flee_seconds"); Positive(g.EdgeArriveM, "gang.edge_arrive_m"); Positive(g.BoardReachM, "gang.board_reach_m");
        Positive(g.Spawn.MinM, "gang.spawn.min_m"); Positive(g.Spawn.MaxM, "gang.spawn.max_m"); Positive(g.Spawn.SpreadM, "gang.spawn.spread_m");
        if (!(g.Spawn.MaxM > g.Spawn.MinM)) throw new DataFileException($"{F}: gang.spawn.max_m is not above min_m");
        if (g.View.Models.Count == 0) throw new DataFileException($"{F}: gang.view.models lists at least one model");
        var v = f.Van;
        Positive(v.SpeedMps, "van.speed_mps"); Positive(v.ParkSpeedMps, "van.park_speed_mps"); Positive(v.AccelMps2, "van.accel_mps2"); Positive(v.DecelMps2, "van.decel_mps2"); Positive(v.TurnDegPerS, "van.turn_deg_per_s");
        if (v.ParkSpeedMps > v.SpeedMps) throw new DataFileException($"{F}: van.park_speed_mps is above speed_mps");
        Positive(v.Hits, "van.hits"); Positive(v.HitScale, "van.hit_scale"); NotNegative(v.HitOffsetM, "van.hit_offset_m");
        Positive(v.Hit.HeadshotWeight, "van.hit.headshot_weight"); NotNegative(v.Hit.ExplosiveWeight, "van.hit.explosive_weight"); NotNegative(v.Hit.VehicleWeaponWeight, "van.hit.vehicle_weapon_weight");
        Positive(v.HalfWidthM, "van.half_width_m"); Positive(v.HalfLengthM, "van.half_length_m"); Positive(v.HeightM, "van.height_m");
        if (v.Capacity < 1 || v.Capacity > RaidVanRules.MaxLoad) throw new DataFileException($"{F}: van.capacity is between 1 and {RaidVanRules.MaxLoad}");
        Positive(v.StaySeconds, "van.stay_seconds"); Positive(v.BoardWaitSeconds, "van.board_wait_seconds"); Positive(v.DoorSeconds, "van.door_seconds");
        Positive(v.CrewStaggerSeconds, "van.crew_stagger_seconds"); Positive(v.WreckSeconds, "van.wreck_seconds"); NotNegative(v.DoorOutM, "van.door_out_m");
        Positive(v.HonkMinSeconds, "van.honk_min_seconds");
        if (!(v.HonkMaxSeconds >= v.HonkMinSeconds)) throw new DataFileException($"{F}: van.honk_max_seconds is below honk_min_seconds");
        Positive(v.HonkNearM, "van.honk_near_m"); Positive(v.HonkNearGapSeconds, "van.honk_near_gap_seconds");
        Positive(v.MinRoadWidthM, "van.min_road_width_m"); Positive(v.StopRadiusM, "van.stop_radius_m"); Positive(v.StopStepM, "van.stop_step_m"); Positive(v.StopInsetM, "van.stop_inset_m");
        NotNegative(v.UsedApartM, "van.used_apart_m"); NotNegative(v.UTurnPenaltyM, "van.u_turn_penalty_m"); Positive(v.ExitStraightM, "van.exit_straight_m"); Positive(v.RamMassScale, "van.ram_mass_scale"); NotNegative(v.RamFallbackDamage, "van.ram_fallback_damage");
        Positive(v.View.HonkHearM, "van.view.honk_hear_m");
        var r = f.Raids;
        if (r.Count < 1) throw new DataFileException($"{F}: raids.count is at least 1");
        if (r.MaxVans < 1) throw new DataFileException($"{F}: raids.max_vans is at least 1");
        Positive(r.NextRaidSeconds, "raids.next_raid_seconds"); NotNegative(r.GapSeconds, "raids.gap_seconds");
        if (r.PlayersPerExtra < 1 || r.MaxExtraThieves < 0 || r.MaxExtraEnforcers < 0) throw new DataFileException($"{F}: raids.players_per_extra is at least 1, the extras are not negative");
        if (g.Raid.Size + r.MaxExtraThieves + r.MaxExtraEnforcers > GangRules.MaxCrew) throw new DataFileException($"{F}: raids can grow a crew past {GangRules.MaxCrew} members");
        f.Audio.Validate(F);
        if (f.Reward.ChainItem == "") throw new DataFileException($"{F}: reward.chain_item names an item of items.json");
        Positive(f.Reward.ChainCorpseSeconds, "reward.chain_corpse_seconds");
        f.Loot.Validate(F);
        return f;
    }
}

/// <summary>The Gang members (GLOSSARY "Gang member"): the crew of a Raid, the three kinds, their Hit counts, melee, aggro and looks.</summary>
public sealed class GangConfig
{
    /// <summary>The crew of one Raid. Ticket 03 scales the counts by the number of players.</summary>
    public GangRaidCounts Raid { get; set; } = new();
    public GangKindConfig Thief { get; set; } = new() { Hits = 20, WalkSpeed = 1.8f, RunSpeed = 5.5f, HitScale = 1f, Melee = new() { Damage = 10 } };
    public GangKindConfig Enforcer { get; set; } = new() { Hits = 35, WalkSpeed = 1.6f, RunSpeed = 5f, HitScale = 1.04f, Melee = new() { Damage = 25 } };
    public GangKindConfig GangBoss { get; set; } = new() { Hits = 80, WalkSpeed = 1.4f, RunSpeed = 4.5f, HitScale = 1.15f, Melee = new() { Damage = 10 } };
    public GangHitWeights Hits { get; set; } = new();
    public GangAggro Aggro { get; set; } = new();
    /// <summary>How long a killed member lies before it is removed.</summary>
    public float CorpseSeconds { get; set; } = 10;
    /// <summary>XP a player gets for killing a member (any kind, any weapon; there is no kill feed, streak or assist).</summary>
    public int KillXp { get; set; } = 5;
    public float TurnRateDegPerS { get; set; } = 360;
    /// <summary>A chaser (or a follower) asks for a new route at most this often, and only when its goal moved this far from the end of the route it has.</summary>
    public float RepathSeconds { get; set; } = 1f;
    public float RepathM { get; set; } = 3f;
    /// <summary>An Enforcer guarding a Thief stays within this distance of it.</summary>
    public float GuardFollowM { get; set; } = 3.5f;
    /// <summary>A Thief with nothing to steal idles for a random time between these, then walks to a point within <see cref="WanderM"/> of the Raid's post (the stand-in for ticket 04's seeking).</summary>
    public float IdleMinSeconds { get; set; } = 3;
    public float IdleMaxSeconds { get; set; } = 8;
    public float WanderM { get; set; } = 12;
    /// <summary>A walk that has not arrived after this long is given up (the member stands again).</summary>
    public float WalkGiveUpSeconds { get; set; } = 20;
    /// <summary>The longest a member runs for the map edge before it is removed where it is, and how near the edge point it counts as arrived.</summary>
    public float FleeSeconds { get; set; } = 180;
    public float EdgeArriveM { get; set; } = 3;
    /// <summary>A member called back by the van counts as boarded within this of the side door point.</summary>
    public float BoardReachM { get; set; } = 1.5f;
    public GangSpawn Spawn { get; set; } = new();
    public GangView View { get; set; } = new();

    public GangKindConfig Of(GangMemberKind kind) => kind switch { GangMemberKind.Enforcer => Enforcer, GangMemberKind.GangBoss => GangBoss, _ => Thief };
}

public sealed class GangRaidCounts
{
    public int GangBosses { get; set; } = 1;
    public int Enforcers { get; set; } = 2;
    public int Thieves { get; set; } = 5;
    public int Size => GangBosses + Enforcers + Thieves;
}

/// <summary>One kind of Gang member. <see cref="Hits"/> is its Hit count (GLOSSARY "Hit count"): not hit points but the weighted hits it takes before it dies.</summary>
public sealed class GangKindConfig
{
    public float Hits { get; set; } = 20;
    public float WalkSpeed { get; set; } = 1.8f;
    /// <summary>Chasing and fleeing (a player sprints at 8, walks at 5: members can be outrun).</summary>
    public float RunSpeed { get; set; } = 5.5f;
    /// <summary>Grows the hit volume (<see cref="HitTarget.Scale"/>) and the drawn body: 1 is a player.</summary>
    public float HitScale { get; set; } = 1f;
    public GangMelee Melee { get; set; } = new();
}

/// <summary>A swing: the windup (a player can step out of reach in it), the damage if the target is still within reach when it lands, and the time from one swing's start to the next (one a second).</summary>
public sealed class GangMelee
{
    public float Damage { get; set; } = 10;
    public float ReachM { get; set; } = 2;
    public float WindupSeconds { get; set; } = 0.45f;
    public float IntervalSeconds { get; set; } = 1f;
}

/// <summary>What a hit on a member counts (the Bigfoot's rules, <see cref="BigfootRules"/>, with a headshot of 2): a bullet 1, an explosive or a vehicle weapon a weight, ramming kills outright.</summary>
public sealed class GangHitWeights
{
    public float HeadshotWeight { get; set; } = 2;
    public float ExplosiveWeight { get; set; } = 12;
    public float VehicleWeaponWeight { get; set; } = 3;
}

/// <summary>When a Raid takes a player as its enemy and when it lets go.</summary>
public sealed class GangAggro
{
    /// <summary>A player this near a Thief who is working (stealing or hauling, ticket 04) is in the way.</summary>
    public float CrowdM { get; set; } = 4;
    /// <summary>A player this far from every living member of the Raid is let go.</summary>
    public float GiveUpM { get; set; } = 30;
}

/// <summary>Where a dev-fallback crew appears (<c>/event thieves</c> before the van exists): a road point between these distances of the admin, the crew spread this far round it.</summary>
public sealed class GangSpawn
{
    public float MinM { get; set; } = 25;
    public float MaxM { get; set; } = 60;
    public float SpreadM { get; set; } = 3;
}

/// <summary>How a member is drawn (client only). The models are stand-ins from the Synty peasants until the Gang Warfare pack (ticket 08); without the private pack a coloured capsule is drawn.</summary>
public sealed class GangView
{
    /// <summary>Models under assets-private/synty/glb (the same as holy_mass.json's citizens), picked by the look number.</summary>
    public List<string> Models { get; set; } = new() { "peasant_male_01", "peasant_male_01", "peasant_female_01" };
    public GangClips Clips { get; set; } = new();
    /// <summary>The capsule fallback: colour (html) and height in metres of a scale 1 member, per kind.</summary>
    public string ThiefColor { get; set; } = "#7d6b4f";
    public string EnforcerColor { get; set; } = "#8a3b32";
    public string GangBossColor { get; set; } = "#c9a227";
    public float CapsuleHeightM { get; set; } = 1.8f;
}

/// <summary>Synty clip names (anim/baked) by what a member does; an empty or missing name plays nothing (windup and swing then lean the body in code).</summary>
public sealed class GangClips
{
    public string Idle { get; set; } = "idle_standing_masc";
    public string Walk { get; set; } = "walk_f_masc";
    public string Run { get; set; } = "sprint_up25f_femn";
    public string Windup { get; set; } = "";
    public string Swing { get; set; } = "";
    /// <summary>The Thief kneeling at a target (Steal; empty: the body is bent forward and lowered) and on a climb (tickets 06 and 07; empty: a held pose, the body stays as it was).</summary>
    public string Steal { get; set; } = "";
    public string ClimbUp { get; set; } = "";
    public string ClimbDown { get; set; } = "";
    public string ClimbWork { get; set; } = "";
    public List<string> Death { get; set; } = new() { "death_back_sword", "death_left_sword" };
}

/// <summary>The Raids of the event, one after another (GLOSSARY "Raid"): how many, when the next one comes, and how a crew grows with the players.</summary>
public sealed class RaidsConfig
{
    /// <summary>The Raids the event consists of; it is won when the last van is gone.</summary>
    public int Count { get; set; } = 3;
    /// <summary>The most vans in the village at once.</summary>
    public int MaxVans { get; set; } = 2;
    /// <summary>The next Raid comes this long after the last one began, even when that van is still there (while fewer than <see cref="MaxVans"/> are).</summary>
    public float NextRaidSeconds { get; set; } = 150;
    /// <summary>When a van leaves or is destroyed, the next Raid comes at the latest this long after it.</summary>
    public float GapSeconds { get; set; } = 8;
    /// <summary>The crew grows by one extra member for every this many players beyond the first (Thieves first, then Enforcers), up to the maxima below.</summary>
    public int PlayersPerExtra { get; set; } = 2;
    public int MaxExtraThieves { get; set; } = 3;
    public int MaxExtraEnforcers { get; set; } = 1;

    /// <summary>The crew of a Raid for <paramref name="players"/> human players: the base counts of <see cref="GangConfig.Raid"/> plus the extras.</summary>
    public GangRaidCounts CrewFor(GangRaidCounts baseCrew, int players)
    {
        var extra = Math.Max(0, players - 1) / PlayersPerExtra;
        var thieves = Math.Min(extra, MaxExtraThieves);
        var enforcers = Math.Min(extra - thieves, MaxExtraEnforcers);
        return new GangRaidCounts { GangBosses = baseCrew.GangBosses, Enforcers = baseCrew.Enforcers + enforcers, Thieves = baseCrew.Thieves + thieves };
    }
}

/// <summary>The Raid van (GLOSSARY "Raid van", ADR 0021): a scripted entity that follows the road graph, not a vehicle. Lengths are metres, speeds metres per second, times seconds.</summary>
public sealed class VanConfig
{
    /// <summary>Driving speed on the way in and out, the creep it parks at, and how fast it speeds up and brakes (it brakes by the distance left to the stop).</summary>
    public float SpeedMps { get; set; } = 13;
    public float ParkSpeedMps { get; set; } = 1.5f;
    public float AccelMps2 { get; set; } = 4;
    public float DecelMps2 { get; set; } = 4;
    /// <summary>How fast its heading turns towards the road; a van that is not facing its way slows down (down to a quarter) so a corner is not cut sideways.</summary>
    public float TurnDegPerS { get; set; } = 110;
    /// <summary>Its Hit count (GLOSSARY "Hit count"): a bullet 1, a headshot is a body hit, an explosive and a vehicle weapon the weights below; ramming it counts nothing.</summary>
    public float Hits { get; set; } = 300;
    public GangHitWeights Hit { get; set; } = new() { HeadshotWeight = 1, ExplosiveWeight = 25, VehicleWeaponWeight = 6 };
    /// <summary>Its hit volume: two prone player capsules grown by hit_scale, hit_offset_m ahead of and behind its centre (the weapon paths know capsules only).</summary>
    public float HitScale { get; set; } = 2.2f;
    public float HitOffsetM { get; set; } = 0.9f;
    /// <summary>The box: half width, half length, height. What rams, shoves vehicles aside and what a parked van is solid with.</summary>
    public float HalfWidthM { get; set; } = 1.1f;
    public float HalfLengthM { get; set; } = 2.8f;
    public float HeightM { get; set; } = 2.5f;
    /// <summary>How much Loot it takes before it leaves (the counter ticket 04 feeds), and the longest it stays after it stopped.</summary>
    public int Capacity { get; set; } = 8;
    public float StaySeconds { get; set; } = 240;
    /// <summary>When it must leave, the living crew is called back and the van waits at most this long for them; the door takes door_seconds to open and to close.</summary>
    public float BoardWaitSeconds { get; set; } = 25;
    public float DoorSeconds { get; set; } = 1.5f;
    /// <summary>One crew member gets out every this many seconds once the door opens.</summary>
    public float CrewStaggerSeconds { get; set; } = 0.8f;
    /// <summary>A destroyed van lies this long, then goes.</summary>
    public float WreckSeconds { get; set; } = 25;
    /// <summary>The side door: how far outside the van's side the crew steps out (the door point), a little ahead of the centre.</summary>
    public float DoorOutM { get; set; } = 1.1f;
    public float DoorAlongM { get; set; } = 0.4f;
    /// <summary>It honks at random between these, and when a player is within honk_near_m (not more often than the gap).</summary>
    public float HonkMinSeconds { get; set; } = 9;
    public float HonkMaxSeconds { get; set; } = 22;
    public float HonkNearM { get; set; } = 25;
    public float HonkNearGapSeconds { get; set; } = 6;
    /// <summary>The roads it may use: at least this wide (footpaths are out). It stops where most targets are within stop_radius_m, on candidates every stop_step_m at least stop_inset_m inside the map, not within used_apart_m of a stop used earlier this event.</summary>
    public float MinRoadWidthM { get; set; } = 3;
    public float StopRadiusM { get; set; } = 80;
    public float StopStepM { get; set; } = 20;
    public float StopInsetM { get; set; } = 30;
    public float UsedApartM { get; set; } = 60;
    /// <summary>Turning round costs this much extra when the way out is planned (it drives on by preference); with no road out it drives this far straight on.</summary>
    public float UTurnPenaltyM { get; set; } = 150;
    public float ExitStraightM { get; set; } = 150;
    /// <summary>Ramming: the van's mass over the car's for the run-over damage (<c>vehicles.json</c> numbers), and the damage when there is no such table.</summary>
    public float RamMassScale { get; set; } = 3;
    public float RamFallbackDamage { get; set; } = 100;
    public VanView View { get; set; } = new();
}

/// <summary>How the van is drawn (client only): a box van of boxes and cylinders, no private assets.</summary>
public sealed class VanView
{
    public string BodyColor { get; set; } = "#b9b3a2";
    public string CabColor { get; set; } = "#8c3f33";
    public string DoorColor { get; set; } = "#9b9585";
    public string WreckColor { get; set; } = "#2b2724";
    /// <summary>How far from the camera a honk is heard (the sound is a stand-in with no range of its own).</summary>
    public float HonkHearM { get; set; } = 180;
}

/// <summary>What the event pays. The win's XP is the level data's event-win award (levels.json), the same for every event, so it is not a number here.</summary>
public sealed class ThievesRewardConfig
{
    /// <summary>The items.json id of the Item a killed Gang boss leaves on his body (the Gold chain).</summary>
    public string ChainItem { get; set; } = "gold_chain";
    /// <summary>How long that body lies, seconds, before it and the chain are gone.</summary>
    public float ChainCorpseSeconds { get; set; } = 300;
}
