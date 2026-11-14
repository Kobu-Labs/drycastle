namespace Drycastle.ShooterCore;

/// <summary>
/// thieves.json, schema version 1: every number of the Thieves event (GLOSSARY "Thieves event"). Lengths are metres, speeds metres per second, times seconds.
/// <see cref="Gang"/> (ticket 02) holds the Gang members; the van and the Loot caps arrive with the tickets that need them (docs/events/thieves/).
/// </summary>
public sealed class ThievesFile : IVersioned
{
    public int Version { get; set; }

    public GangConfig Gang { get; set; } = new();

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
        Positive(g.WalkGiveUpSeconds, "gang.walk_give_up_seconds"); Positive(g.FleeSeconds, "gang.flee_seconds"); Positive(g.EdgeArriveM, "gang.edge_arrive_m");
        Positive(g.Spawn.MinM, "gang.spawn.min_m"); Positive(g.Spawn.MaxM, "gang.spawn.max_m"); Positive(g.Spawn.SpreadM, "gang.spawn.spread_m");
        if (!(g.Spawn.MaxM > g.Spawn.MinM)) throw new DataFileException($"{F}: gang.spawn.max_m is not above min_m");
        if (g.View.Models.Count == 0) throw new DataFileException($"{F}: gang.view.models lists at least one model");
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
    public List<string> Death { get; set; } = new() { "death_back_sword", "death_left_sword" };
}
