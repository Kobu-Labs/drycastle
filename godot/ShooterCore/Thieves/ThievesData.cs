using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Events;
using Drycastle.ShooterCore.Weapons;

namespace Drycastle.ShooterCore.Thieves;

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

    /// <summary>The Rival crew (rival_crew.json, #495, #496): not a key of thieves.json; the host parses that file and attaches it (<see cref="WithRivalCrew"/>), so every user of the Gang members' data has it.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Cartel.RivalCrewFile RivalCrew { get; private set; } = Cartel.RivalCrewFile.Default;

    /// <summary>Attaches rival_crew.json: its Gunman becomes this file's <see cref="GangConfig.Gunman"/> and its models <see cref="GangModels.Gunman"/> (the client draws a Gunman from them).</summary>
    public ThievesFile WithRivalCrew(Cartel.RivalCrewFile rival)
    {
        RivalCrew = rival;
        Gang.Gunman = rival.Gunman;
        Gang.View.Models.Gunman = rival.View.Models;
        return this;
    }

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
        Positive(g.Aggro.ForgetSeconds, "gang.aggro.forget_seconds"); Positive(g.Aggro.GiveUpM, "gang.aggro.give_up_m");
        Positive(g.Stuck.StuckM, "gang.stuck.stuck_m"); Positive(g.Stuck.StuckSeconds, "gang.stuck.stuck_seconds");
        if (!float.IsFinite(g.Stuck.SnapSeconds) || g.Stuck.SnapSeconds <= g.Stuck.StuckSeconds) throw new DataFileException("thieves.json: gang.stuck.snap_seconds must be more than stuck_seconds");
        Positive(g.SeparationM, "gang.separation_m"); NotNegative(g.SeparationMps, "gang.separation_mps");
        Positive(g.CorpseSeconds, "gang.corpse_seconds"); NotNegative(g.KillXp, "gang.kill_xp");
        Positive(g.TurnRateDegPerS, "gang.turn_rate_deg_per_s"); Positive(g.RepathSeconds, "gang.repath_seconds"); Positive(g.RepathM, "gang.repath_m");
        Positive(g.GuardFollowM, "gang.guard_follow_m"); Positive(g.WanderM, "gang.wander_m");
        Positive(g.IdleMinSeconds, "gang.idle_min_seconds");
        if (!(g.IdleMaxSeconds >= g.IdleMinSeconds)) throw new DataFileException($"{F}: gang.idle_max_seconds is not below idle_min_seconds");
        Positive(g.WalkGiveUpSeconds, "gang.walk_give_up_seconds"); Positive(g.FleeSeconds, "gang.flee_seconds"); Positive(g.EdgeArriveM, "gang.edge_arrive_m"); Positive(g.BoardReachM, "gang.board_reach_m");
        Positive(g.Spawn.MinM, "gang.spawn.min_m"); Positive(g.Spawn.MaxM, "gang.spawn.max_m"); Positive(g.Spawn.SpreadM, "gang.spawn.spread_m");
        if (!(g.Spawn.MaxM > g.Spawn.MinM)) throw new DataFileException($"{F}: gang.spawn.max_m is not above min_m");
        g.View.Validate(F);
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
        v.View.Validate(F);
        var r = f.Raids;
        if (r.MinVans < 1) throw new DataFileException($"{F}: raids.min_vans is at least 1");
        if (r.MaxVans < r.MinVans || r.MaxVans > RaidVanRules.MaxVans) throw new DataFileException($"{F}: raids.max_vans is between min_vans and {RaidVanRules.MaxVans}");
        NotNegative(r.ConvoyGapSeconds, "raids.convoy_gap_seconds");
        if (r.PlayersPerExtra < 1 || r.MaxExtraThieves < 0 || r.MaxExtraEnforcers < 0) throw new DataFileException($"{F}: raids.players_per_extra is at least 1, the extras are not negative");
        if (g.Raid.Size + r.MaxExtraThieves + r.MaxExtraEnforcers > GangRules.MaxCrew) throw new DataFileException($"{F}: raids can grow a crew past {GangRules.MaxCrew} members");
        if (r.RoutePick != "random") throw new DataFileException($"{F}: raids.route_pick is \"random\" (the only way so far)");
        Positive(r.RouteSnapMaxM, "raids.route_snap_max_m"); Positive(r.ParkNearChurchM, "raids.park_near_church_m"); NotNegative(r.RouteStopSearchM, "raids.route_stop_search_m"); NotNegative(r.RouteMinRoadWidthM, "raids.route_min_road_width_m");
        if (r.ParkCentre is { } pc && (pc.Length != 2 || !float.IsFinite(pc[0]) || !float.IsFinite(pc[1]))) throw new DataFileException($"{F}: raids.park_centre is [x, z]");
        foreach (var route in r.Routes)
        {
            if (route.Name == "") throw new DataFileException($"{F}: a raids.routes entry has no name");
            if (route.Waypoints.Count < 2) throw new DataFileException($"{F}: raids.routes '{route.Name}' needs at least two waypoints");
            if (route.Waypoints.Any(w => w.Length != 2 || !float.IsFinite(w[0]) || !float.IsFinite(w[1]))) throw new DataFileException($"{F}: raids.routes '{route.Name}' waypoints are [x, z]");
        }
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
    /// <summary>The Rival crew's Gunman (GLOSSARY "Gunman"): not in thieves.json; rival_crew.json's <c>gunman</c> (<see cref="ThievesFile.WithRivalCrew"/>), so a client draws it at the server's size.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public GangKindConfig Gunman { get; set; } = new() { Hits = 6, WalkSpeed = 1.6f, RunSpeed = 4.8f, HitScale = 1f, Gun = new() };
    /// <summary>How a crew that attacks players on sight notices them (the Rival crew, <see cref="ThiefGang.HostileTo"/>); a Raid attacks nobody on sight. rival_crew.json's <c>sight</c>.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public GangSight Sight { get; set; } = new();
    public GangHitWeights Hits { get; set; } = new();
    public GangAggro Aggro { get; set; } = new();
    /// <summary>When a member that is trying to get somewhere is stuck (<see cref="StuckWatch"/>).</summary>
    public GangStuck Stuck { get; set; } = new();
    /// <summary>A Thief that takes a hit which does not kill it while it carries Loot (hauling, loading, climbing down, hopping out) drops the Loot where it stands and looks for work again, so it joins the fight; the Loot lies there for a player to send back or a Thief to take up.</summary>
    public bool HitDropsLoot { get; set; } = true;
    /// <summary>How long a killed member lies before it is removed.</summary>
    public float CorpseSeconds { get; set; } = 10;
    /// <summary>XP a player gets for killing a member (any kind, any weapon; there is no kill feed, streak or assist).</summary>
    public int KillXp { get; set; } = 5;
    public float TurnRateDegPerS { get; set; } = 360;
    /// <summary>A chaser (or a follower) asks for a new route at most this often, and only when its goal moved this far from the end of the route it has.</summary>
    public float RepathSeconds { get; set; } = 1f;
    public float RepathM { get; set; } = 3f;
    /// <summary>Members of any Raid that walk are nudged apart when closer than separation_m, at most separation_mps metres a second (a pure push, no collision).</summary>
    public float SeparationM { get; set; } = 0.8f;
    public float SeparationMps { get; set; } = 2f;
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

    public GangKindConfig Of(GangMemberKind kind) => kind switch { GangMemberKind.Enforcer => Enforcer, GangMemberKind.GangBoss => GangBoss, GangMemberKind.Gunman => Gunman, _ => Thief };

    /// <summary>A shallow copy (the Rival crew's config is the Thieves' with some sections replaced: <see cref="Cartel.RivalCrewFile.GangOf"/>).</summary>
    public GangConfig Copy() => (GangConfig)MemberwiseClone();
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
    /// <summary>A kind that fights at range (the Gunman): its gun. Null: it fights hand to hand.</summary>
    public GangGun? Gun { get; set; }

    /// <summary>The same kind with another Hit count (the Rival crew's Enforcer is the Thieves' with its own).</summary>
    public GangKindConfig WithHits(float hits) => new() { Hits = hits, WalkSpeed = WalkSpeed, RunSpeed = RunSpeed, HitScale = HitScale, Melee = Melee, Gun = Gun };
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
    /// <summary>A player is fought for this long after their last hit on a member of the Raid (each further hit starts it again).</summary>
    public float ForgetSeconds { get; set; } = 8;
    /// <summary>A player this far from every living member of the Raid is let go.</summary>
    public float GiveUpM { get; set; } = 30;
}

/// <summary>The progress check (thieves.json <c>gang.stuck</c>): under stuck_m covered in stuck_seconds a member takes another spot or a fresh route; still stuck at snap_seconds it is moved to the nearest walkable point.</summary>
public sealed class GangStuck
{
    public float StuckM { get; set; } = 1f;
    public float StuckSeconds { get; set; } = 3f;
    public float SnapSeconds { get; set; } = 6f;
}

/// <summary>Where a dev-fallback crew appears (<c>/event thieves</c> before the van exists): a road point between these distances of the admin, the crew spread this far round it.</summary>
public sealed class GangSpawn
{
    public float MinM { get; set; } = 25;
    public float MaxM { get; set; } = 60;
    public float SpreadM { get; set; } = 3;
}

/// <summary>The convoy of the event (GLOSSARY "Thieves event", "Raid"): how many vans come, how far apart they are launched, and how a crew grows with the players.</summary>
public sealed class RaidsConfig
{
    /// <summary>The wave has at least this many vans, and at most <see cref="MaxVans"/> (at most <see cref="RaidVanRules.MaxVans"/>).</summary>
    public int MinVans { get; set; } = 1;
    public int MaxVans { get; set; } = 3;
    /// <summary>The next van of the wave is launched this long after the one before: they drive the same route in a line (at the van's speed, seconds x speed = metres apart).</summary>
    public float ConvoyGapSeconds { get; set; } = 3;
    /// <summary>The crew grows by one extra member for every this many players beyond the first (Thieves first, then Enforcers), up to the maxima below.</summary>
    public int PlayersPerExtra { get; set; } = 2;
    public int MaxExtraThieves { get; set; } = 3;
    public int MaxExtraEnforcers { get; set; } = 1;

    /// <summary>
    /// The fixed routes a van comes in on and leaves by (<see cref="RaidRoutes"/>), in the map's local ground metres; empty: the old automatic choice (a random road at the map edge, the stop with the most targets).
    /// Each wave picks one at random (<see cref="RoutePick"/>), every van of it follows the roads through its waypoints, then on to its own stop near the church (<see cref="ParkNearChurchM"/>) and leaves the same way back.
    /// </summary>
    public List<RaidRouteConfig> Routes { get; set; } = new();
    /// <summary>How a wave picks its route: "random" (the only way so far).</summary>
    public string RoutePick { get; set; } = "random";
    /// <summary>The roads a route may be driven over: at least this wide (0: all, footpaths too, because the owner drew the routes over them; the van's own <see cref="VanConfig.MinRoadWidthM"/> applies from the route's end to the stop).</summary>
    public float RouteMinRoadWidthM { get; set; } = 0;
    /// <summary>A waypoint farther than this from the road graph makes its route unusable (skipped, logged).</summary>
    public float RouteSnapMaxM { get; set; } = 30;
    /// <summary>The van parks on a road within this distance of the church (the door of church.json) or of <see cref="ParkCentre"/>, preferring the main road its route continues on.</summary>
    public float ParkNearChurchM { get; set; } = 100;
    /// <summary>Overrides the church as the centre the van parks near: [x, z] in the map's local ground metres (a map without church.json). Null: the church; with neither the van parks at the end of its route.</summary>
    public float[]? ParkCentre { get; set; }
    /// <summary>With no centre to park near (or no stop found near it): the van parks at the end of its route, or, when that spot fails, the nearest working spot back along it within this distance.</summary>
    public float RouteStopSearchM { get; set; } = 40;

    /// <summary>How many vans the wave has: a random number from <see cref="MinVans"/> to <see cref="MaxVans"/> (inclusive).</summary>
    public int VansFor(Random rng) => rng.Next(MinVans, MaxVans + 1);

    /// <summary>The crew of a Raid for <paramref name="players"/> human players: the base counts of <see cref="GangConfig.Raid"/> plus the extras.</summary>
    public GangRaidCounts CrewFor(GangRaidCounts baseCrew, int players)
    {
        var extra = Math.Max(0, players - 1) / PlayersPerExtra;
        var thieves = Math.Min(extra, MaxExtraThieves);
        var enforcers = Math.Min(extra - thieves, MaxExtraEnforcers);
        return new GangRaidCounts { GangBosses = baseCrew.GangBosses, Enforcers = baseCrew.Enforcers + enforcers, Thieves = baseCrew.Thieves + thieves };
    }
}

/// <summary>One fixed route of the Raids (<see cref="RaidsConfig.Routes"/>): a name and the waypoints [x, z] in the map's local ground metres (x east, z south), in the order the van drives them.</summary>
public sealed class RaidRouteConfig
{
    public string Name { get; set; } = "";
    public List<float[]> Waypoints { get; set; } = new();
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

    /// <summary>A shallow copy (a Heist's Assault van is the Raid van that never leaves: another stay).</summary>
    public VanConfig Copy() => (VanConfig)MemberwiseClone();
}

/// <summary>
/// How the van is drawn (client only). With the private pack the model <see cref="Model"/> (+Z forward, the door on its x less than 0 side, origin at the middle on the ground) turned to face -Z, wearing
/// <see cref="Atlas"/>; the door node slides from <see cref="DoorClosed"/> to <see cref="DoorOpen"/> (the model's own coordinates). Without it a box van of boxes and cylinders (the colours). The model is drawn at
/// its own size: the box numbers above (half_width_m, half_length_m, height_m) are the model's, not the other way round. A left door (the server may pick one) is the mirrored model.
/// </summary>
public sealed class VanView
{
    public string BodyColor { get; set; } = "#b9b3a2";
    public string CabColor { get; set; } = "#8c3f33";
    public string DoorColor { get; set; } = "#9b9585";
    public string WreckColor { get; set; } = "#2b2724";
    /// <summary>How far from the camera a honk is heard (the sound is a stand-in with no range of its own).</summary>
    public float HonkHearM { get; set; } = 360;
    /// <summary>The honk's loudness on top of its sound's own (dB), and how much quieter it is at <see cref="HonkHearM"/> (it fades linearly with the distance).</summary>
    public float HonkDb { get; set; } = 6;
    public float HonkFadeDb { get; set; } = 12;
    /// <summary>The model under assets-private/synty/glb (without .glb), its atlas under synty/, and the node that is the sliding door ("" = the model has no door to slide).</summary>
    public string Model { get; set; } = "";
    public string Atlas { get; set; } = "";
    public string DoorNode { get; set; } = "door";
    /// <summary>The door node's position shut and fully open, in the model's own coordinates (a right door; metres).</summary>
    public float[] DoorClosed { get; set; } = { 0, 0, 0 };
    public float[] DoorOpen { get; set; } = { 0, 0, 0 };
    /// <summary>Lifts the model off the ground (the model's wheels reach below its origin), metres.</summary>
    public float ModelLiftM { get; set; }
    /// <summary>What the model's paint is multiplied by when it is the wreck (a dark charred tint; html), and how much each damage stage dims it.</summary>
    public string WreckTint { get; set; } = "#3a3532";
    public float StageDim { get; set; } = 0.07f;
    /// <summary>Where the crew sits inside (a right-door van: x east of the middle, z ahead is negative, yaw degrees the way the seat faces, 0 forward, 90 to the left). The first seats are filled first (the cab, then the door's bench).</summary>
    public List<VanSeat> Seats { get; set; } = new();
    /// <summary>The floor the seats stand on (metres above the ground), and how high the hips of a sitting member are above it.</summary>
    public float SeatFloorM { get; set; } = 0.45f;
    public float SeatPelvisM { get; set; } = 0.42f;
    /// <summary>The crew is drawn sitting only when the camera is within this of the van (metres).</summary>
    public float SeatDrawM { get; set; } = 70;

    public void Validate(string file)
    {
        if (!(SeatFloorM >= 0) || !(SeatPelvisM > 0) || !(SeatDrawM >= 0)) throw new DataFileException($"{file}: van.view seat_floor_m is not negative, seat_pelvis_m positive, seat_draw_m not negative");
        if (Seats.Count > RaidVanRules.MaxInside) throw new DataFileException($"{file}: van.view.seats lists at most {RaidVanRules.MaxInside} seats (the wire's crew count)");
        if (Model != "" && Atlas == "") throw new DataFileException($"{file}: van.view.atlas names the model's atlas");
        if (DoorClosed.Length != 3 || DoorOpen.Length != 3) throw new DataFileException($"{file}: van.view.door_closed and door_open are three numbers");
        if (!(HonkHearM > 0)) throw new DataFileException($"{file}: van.view.honk_hear_m must be positive");
        if (!float.IsFinite(HonkDb) || !(HonkFadeDb >= 0)) throw new DataFileException($"{file}: van.view.honk_db is a number and honk_fade_db is not negative");
        if (!(StageDim >= 0 && StageDim < 0.3f)) throw new DataFileException($"{file}: van.view.stage_dim is between 0 and 0.3");
    }
}

/// <summary>One seat in a van: <see cref="X"/> across (a right-door van), <see cref="Z"/> along (negative ahead of the middle), <see cref="YawDeg"/> the way it faces.</summary>
public sealed class VanSeat
{
    public float X { get; set; }
    public float Z { get; set; }
    public float YawDeg { get; set; }
}

/// <summary>What the event pays. The win's XP is the level data's event-win award (levels.json), the same for every event, so it is not a number here.</summary>
public sealed class ThievesRewardConfig
{
    /// <summary>The items.json id of the Item a killed Gang boss leaves on his body (the Gold chain).</summary>
    public string ChainItem { get; set; } = "gold_chain";
    /// <summary>How long that body lies, seconds, before it and the chain are gone.</summary>
    public float ChainCorpseSeconds { get; set; } = 300;
}
