namespace Drycastle.ShooterCore;

/// <summary>
/// bigfoot.json, schema version 1 (issue #264): every number of the Bigfoot event. Lengths are metres, speeds metres per second, angles degrees,
/// times seconds. The numbers are first guesses tuned by reading, not by playing: the fight is meant to take about a minute and nobody has run it yet.
/// </summary>
public sealed class BigfootFile : IVersioned
{
    public int Version { get; set; }

    /// <summary>The placeholder model (Quaternius "Giant", rigged, 12 joints), relative to the asset library (assets/).</summary>
    public string Model { get; set; } = "quaternius-giant/giant.glb";
    /// <summary>Uniform scale that brings the model, as it imports (4.53 units tall), to <see cref="HeightM"/>.</summary>
    public float ModelScale { get; set; } = 0.8f;
    /// <summary>Yaw added to the model so its face points to the entity's forward (-Z). The Quaternius packs face +Z, so 180.</summary>
    public float ModelYawDeg { get; set; } = 180;
    /// <summary>The drawn and hit height: twice a player (1.8 m). The hit volume is the player's capsule grown by <see cref="HitScale"/>.</summary>
    public float HeightM { get; set; } = 3.6f;
    /// <summary>Grows the player's hit capsule (<see cref="HitTarget.Scale"/>): 2 = twice as tall and as wide.</summary>
    public float HitScale { get; set; } = 2f;

    public BigfootHits Hits { get; set; } = new();
    public BigfootMovement Movement { get; set; } = new();
    public BigfootTrunk Trunk { get; set; } = new();
    public BigfootStomp Stomp { get; set; } = new();
    public BigfootVehicleThrow VehicleThrow { get; set; } = new();
    public BigfootGrab Grab { get; set; } = new();
    public BigfootWipe Wipe { get; set; } = new();
    public BigfootReward Reward { get; set; } = new();
    /// <summary>Where the event starts when the map has no area that carries <c>event spawn: bigfoot</c> (a dev aid).</summary>
    public BigfootFallbackSpawn FallbackSpawn { get; set; } = new();

    /// <summary>The damage a player takes from anything Bigfoot does (a log, the shockwave, a landing and what the launched player hits): a whole number in this range, both ends included.</summary>
    public int DamageMin { get; set; } = 2;
    public int DamageMax { get; set; } = 5;

    public static BigfootFile Load(string path) => Validate(DataFile.Load<BigfootFile>(path));

    public static BigfootFile Parse(string json) => Validate(DataFile.Parse<BigfootFile>(json, "bigfoot.json"));

    /// <summary>One roll of the damage a player takes from Bigfoot.</summary>
    public int RollDamage(Random rng) => rng.Next(DamageMin, DamageMax + 1);

    public static BigfootFile Validate(BigfootFile f)
    {
        const string F = "bigfoot.json";
        void Positive(float v, string what) { if (!(v > 0) || !float.IsFinite(v)) throw new DataFileException($"{F}: {what} must be positive"); }
        void NotNegative(float v, string what) { if (!(v >= 0) || !float.IsFinite(v)) throw new DataFileException($"{F}: {what} must not be negative"); }
        if (!f.Model.EndsWith(".glb", StringComparison.Ordinal) || f.Model.StartsWith('/') || f.Model.Contains("..", StringComparison.Ordinal))
            throw new DataFileException($"{F}: model is a .glb path inside the asset library");
        Positive(f.ModelScale, "model_scale"); Positive(f.HeightM, "height_m"); Positive(f.HitScale, "hit_scale");
        if (f.DamageMin < 1 || f.DamageMax < f.DamageMin) throw new DataFileException($"{F}: damage_min is at least 1 and not above damage_max");

        var h = f.Hits;
        Positive(h.PerPlayer, "hits.per_player"); Positive(h.ParticipantRadiusM, "hits.participant_radius_m");
        if (h.MinPlayers < 1) throw new DataFileException($"{F}: hits.min_players is at least 1");
        NotNegative(h.ExplosiveWeight, "hits.explosive_weight"); NotNegative(h.VehicleWeaponWeight, "hits.vehicle_weapon_weight");
        if (!(h.SecondPhaseAt > 0 && h.SecondPhaseAt < h.ThirdPhaseAt && h.ThirdPhaseAt < h.EnrageAt && h.EnrageAt < 1))
            throw new DataFileException($"{F}: hits.second_phase_at < third_phase_at < enrage_at, all between 0 and 1");
        Positive(h.EnrageSpeedMultiplier, "hits.enrage_speed_multiplier"); Positive(h.EnrageCooldownMultiplier, "hits.enrage_cooldown_multiplier");
        NotNegative(h.HealthRegainPerSecond, "hits.health_regain_per_second");

        var m = f.Movement;
        Positive(m.WalkSpeed, "movement.walk_speed");
        if (m.RunSpeed < m.WalkSpeed) throw new DataFileException($"{F}: movement.run_speed is not below walk_speed");
        Positive(m.TurnRateDegPerS, "movement.turn_rate_deg_per_s"); Positive(m.RunBeyondM, "movement.run_beyond_m");
        Positive(m.MaxStepM, "movement.max_step_m"); Positive(m.BodyRadiusM, "movement.body_radius_m"); Positive(m.RetargetSeconds, "movement.retarget_seconds");
        Positive(m.StopDistanceM, "movement.stop_distance_m"); Positive(m.GiveUpM, "movement.give_up_m");
        NotNegative(h.SecondPhaseAfterSeconds, "hits.second_phase_after_seconds"); NotNegative(h.ThirdPhaseAfterSeconds, "hits.third_phase_after_seconds");
        NotNegative(h.EnrageAfterSeconds, "hits.enrage_after_seconds");

        var t = f.Trunk;
        Positive(t.MinRangeM, "trunk.min_range_m");
        if (t.MaxRangeM <= t.MinRangeM) throw new DataFileException($"{F}: trunk.max_range_m is above min_range_m");
        Positive(t.CooldownSeconds, "trunk.cooldown_seconds"); Positive(t.WindupSeconds, "trunk.windup_seconds"); Positive(t.SpeedMps, "trunk.speed_mps");
        Positive(t.RadiusM, "trunk.radius_m"); Positive(t.LifeSeconds, "trunk.life_seconds"); NotNegative(t.AimLeadSeconds, "trunk.aim_lead_seconds");

        var s = f.Stomp;
        Positive(s.TriggerM, "stomp.trigger_m"); Positive(s.CooldownSeconds, "stomp.cooldown_seconds"); Positive(s.WindupSeconds, "stomp.windup_seconds");
        Positive(s.WaveSpeedMps, "stomp.wave_speed_mps"); Positive(s.WaveMaxRadiusM, "stomp.wave_max_radius_m"); Positive(s.WaveBandM, "stomp.wave_band_m");
        Positive(s.WaveHeightM, "stomp.wave_height_m"); NotNegative(s.KnockbackMps, "stomp.knockback_mps"); NotNegative(s.KnockbackLiftMps, "stomp.knockback_lift_mps");

        var v = f.VehicleThrow;
        Positive(v.ContactM, "vehicle_throw.contact_m"); Positive(v.WindupSeconds, "vehicle_throw.windup_seconds"); Positive(v.CooldownSeconds, "vehicle_throw.cooldown_seconds");
        Positive(v.MinDistanceM, "vehicle_throw.min_distance_m"); Positive(v.HoldHeightM, "vehicle_throw.hold_height_m");
        if (v.MaxDistanceM < v.MinDistanceM) throw new DataFileException($"{F}: vehicle_throw.max_distance_m is not below min_distance_m");
        Positive(v.FlightSeconds, "vehicle_throw.flight_seconds"); NotNegative(v.SpinRadPerS, "vehicle_throw.spin_rad_per_s");
        if (v.PlacementTries < 1) throw new DataFileException($"{F}: vehicle_throw.placement_tries is at least 1");

        var g = f.Grab;
        Positive(g.RangeM, "grab.range_m"); Positive(g.CooldownSeconds, "grab.cooldown_seconds"); Positive(g.ReachSeconds, "grab.reach_seconds");
        Positive(g.HoldSeconds, "grab.hold_seconds"); Positive(g.LaunchFlightSeconds, "grab.launch_flight_seconds"); NotNegative(g.LaunchGraceSeconds, "grab.launch_grace_seconds");
        Positive(g.LaunchHitRadiusM, "grab.launch_hit_radius_m"); Positive(g.HandHeightM, "grab.hand_height_m"); Positive(g.HandForwardM, "grab.hand_forward_m");
        Positive(g.RandomLaunchSpeedMps, "grab.random_launch_speed_mps"); Positive(g.MinLaunchDistanceM, "grab.min_launch_distance_m");

        var w = f.Wipe;
        Positive(w.WakeRadiusM, "wipe.wake_radius_m"); Positive(w.DespawnMinutes, "wipe.despawn_minutes"); Positive(w.DespawnRadiusM, "wipe.despawn_radius_m");

        var r = f.Reward;
        if (r.HeadItem == "") throw new DataFileException($"{F}: reward.head_item is empty");
        Positive(r.CorpseSeconds, "reward.corpse_seconds"); Positive(r.DeathSeconds, "reward.death_seconds");
        Positive(r.TrailLengthM, "reward.trail_length_m"); Positive(r.TrailSpacingM, "reward.trail_spacing_m");
        if (r.TrailLengthM / r.TrailSpacingM > BigfootTrail.MaxPrints) throw new DataFileException($"{F}: reward.trail_length_m / trail_spacing_m is at most {BigfootTrail.MaxPrints} footprints");
        if (r.TrailTries < 1) throw new DataFileException($"{F}: reward.trail_tries is at least 1");
        NotNegative(r.CreditMinHits, "reward.credit_min_hits");

        var fb = f.FallbackSpawn;
        Positive(fb.MinDistanceM, "fallback_spawn.min_distance_m");
        if (fb.MaxDistanceM < fb.MinDistanceM) throw new DataFileException($"{F}: fallback_spawn.max_distance_m is not below min_distance_m");
        return f;
    }
}

/// <summary>The hit count (not hit points): how many hits kill it, how a hit is weighted, and where the phases sit.</summary>
public sealed class BigfootHits
{
    /// <summary>Hits to kill it for one participant; the count is this times the participants (<see cref="BigfootRules.HitsNeeded"/>). A rifle lands about 5 hits a second on a target this big, so 300 is about a minute alone.</summary>
    public float PerPlayer { get; set; } = 300;
    /// <summary>The fewest participants the count is worked out for (a lone player counts as this many).</summary>
    public int MinPlayers { get; set; } = 1;
    /// <summary>A living player within this distance of it takes part in the fight (scales the count, and is credited).</summary>
    public float ParticipantRadiusM { get; set; } = 90;
    /// <summary>What one explosive hit counts (a rocket's blast, a missile, the MOAB): bullets count 1.</summary>
    public float ExplosiveWeight { get; set; } = 25;
    /// <summary>What one hit of a vehicle weapon counts (the jeep's roof gun, the flak, the door gun, the gunship and the helicopters' guns).</summary>
    public float VehicleWeaponWeight { get; set; } = 4;
    /// <summary>The fraction of the hit count taken at which the second phase (adds the stomp and the vehicle throw) and the third (adds the grab and the launch) begin, and at which it enrages.</summary>
    public float SecondPhaseAt { get; set; } = 0.30f;
    public float ThirdPhaseAt { get; set; } = 0.60f;
    public float EnrageAt { get; set; } = 0.85f;
    /// <summary>The same three steps by the clock: seconds of the fight (awake, with a target) after which they begin whatever damage it took. 0: never by time.</summary>
    public float SecondPhaseAfterSeconds { get; set; } = 20;
    public float ThirdPhaseAfterSeconds { get; set; } = 45;
    public float EnrageAfterSeconds { get; set; } = 90;
    /// <summary>Enraged: it moves this much faster and its cooldowns are this much of what they were.</summary>
    public float EnrageSpeedMultiplier { get; set; } = 1.3f;
    public float EnrageCooldownMultiplier { get; set; } = 0.6f;
    /// <summary>After a wipe it is healed this many hit counts a second until full (0: at once).</summary>
    public float HealthRegainPerSecond { get; set; }
}

public sealed class BigfootMovement
{
    public float WalkSpeed { get; set; } = 3.4f;
    public float RunSpeed { get; set; } = 6.8f;
    /// <summary>Farther than this from its target it runs, nearer it walks.</summary>
    public float RunBeyondM { get; set; } = 16;
    public float TurnRateDegPerS { get; set; } = 150;
    /// <summary>The largest rise or drop of the ground in one step it follows; a bigger one stops it (a wall of earth, the edge of the map).</summary>
    public float MaxStepM { get; set; } = 1.4f;
    /// <summary>How far in front of it the solid world (a building) stops it. Trees do not.</summary>
    public float BodyRadiusM { get; set; } = 1.1f;
    /// <summary>It keeps its target this long before it looks for a nearer one (a target that died is dropped at once).</summary>
    public float RetargetSeconds { get; set; } = 4;
    /// <summary>It stops this close to its target (the target's capsule is in its way).</summary>
    public float StopDistanceM { get; set; } = 3f;
    /// <summary>A player farther than this is no target: it gives a runner up and stands until someone is nearer.</summary>
    public float GiveUpM { get; set; } = 150;
}

/// <summary>Phase one: it hauls tree trunks at the players (an arc, no homing).</summary>
public sealed class BigfootTrunk
{
    public float MinRangeM { get; set; } = 2.5f;
    public float MaxRangeM { get; set; } = 55;
    public float CooldownSeconds { get; set; } = 3.2f;
    /// <summary>The wind-up before the release, during which it stands and the clients play the Attack clip.</summary>
    public float WindupSeconds { get; set; } = 0.8f;
    public float SpeedMps { get; set; } = 24;
    /// <summary>A log hits a player whose body passes within this distance of its centre line, besides the body's own radius.</summary>
    public float RadiusM { get; set; } = 0.6f;
    public float LifeSeconds { get; set; } = 6;
    /// <summary>It aims at where the target will be this many seconds from the release.</summary>
    public float AimLeadSeconds { get; set; } = 0.35f;
}

/// <summary>Phase two: the stomp, a ground shockwave that knocks back players standing on the ground. A jump avoids it.</summary>
public sealed class BigfootStomp
{
    /// <summary>It stomps when a player is nearer than this.</summary>
    public float TriggerM { get; set; } = 10;
    public float CooldownSeconds { get; set; } = 7;
    /// <summary>The telegraph: the wind-up animation and sound, before the ground shakes.</summary>
    public float WindupSeconds { get; set; } = 1.1f;
    public float WaveSpeedMps { get; set; } = 14;
    public float WaveMaxRadiusM { get; set; } = 24;
    /// <summary>The width of the ring that hurts: a player the ring crosses is hit once.</summary>
    public float WaveBandM { get; set; } = 1.6f;
    /// <summary>A player whose feet are higher than this above the ground when the ring crosses them is untouched (they jumped).</summary>
    public float WaveHeightM { get; set; } = 0.5f;
    public float KnockbackMps { get; set; } = 9;
    public float KnockbackLiftMps { get; set; } = 4.5f;
}

/// <summary>Phase two: it throws a vehicle it is in contact with, on a scripted arc, with nobody and nothing hurt.</summary>
public sealed class BigfootVehicleThrow
{
    /// <summary>A vehicle whose centre is nearer than this (horizontally) is in contact.</summary>
    public float ContactM { get; set; } = 4.5f;
    public float WindupSeconds { get; set; } = 1.6f;
    /// <summary>The vehicle is lifted this high above the Bigfoot's feet (over its head) before it is thrown.</summary>
    public float HoldHeightM { get; set; } = 4.2f;
    /// <summary>The wait between two throws, whichever vehicles.</summary>
    public float CooldownSeconds { get; set; } = 4;
    public float MinDistanceM { get; set; } = 25;
    public float MaxDistanceM { get; set; } = 60;
    public float FlightSeconds { get; set; } = 2.4f;
    /// <summary>The vehicle turns about its vertical axis this fast while it flies.</summary>
    public float SpinRadPerS { get; set; } = 2.5f;
    /// <summary>It picks a random direction and distance up to this many times, and throws where it finds ground (it gives up and throws short otherwise).</summary>
    public int PlacementTries { get; set; } = 8;
}

/// <summary>Phase three: it picks a player up, flails with them and launches them, at another player when there is one.</summary>
public sealed class BigfootGrab
{
    public float RangeM { get; set; } = 4.5f;
    public float CooldownSeconds { get; set; } = 11;
    public float ReachSeconds { get; set; } = 0.7f;
    /// <summary>How long it holds and flails with the player before the launch.</summary>
    public float HoldSeconds { get; set; } = 2.5f;
    /// <summary>The launch is aimed to land on another player in this time. Without one it throws the player this fast in a random direction.</summary>
    public float LaunchFlightSeconds { get; set; } = 1.7f;
    public float RandomLaunchSpeedMps { get; set; } = 25;
    /// <summary>The shortest throw: another player nearer than this only gives the direction, the launched one lands this far away.</summary>
    public float MinLaunchDistanceM { get; set; } = 45;
    /// <summary>For this long after a launch the launched player's fall damage is the launch's own (2 to 5) and not the fall's.</summary>
    public float LaunchGraceSeconds { get; set; } = 5;
    /// <summary>The launched player hurts another player they pass within this distance of (once each).</summary>
    public float LaunchHitRadiusM { get; set; } = 1.3f;
    /// <summary>Where the held player is: this far above its feet and in front of it.</summary>
    public float HandHeightM { get; set; } = 2.7f;
    public float HandForwardM { get; set; } = 1.4f;
}

/// <summary>When it heals and waits, and when it goes away.</summary>
public sealed class BigfootWipe
{
    /// <summary>After a wipe it stands and waits until a living player comes within this distance.</summary>
    public float WakeRadiusM { get; set; } = 60;
    /// <summary>It despawns, with no reward, when no living player has been within <see cref="DespawnRadiusM"/> for this many minutes.</summary>
    public float DespawnMinutes { get; set; } = 10;
    public float DespawnRadiusM { get; set; } = 250;
}

public sealed class BigfootReward
{
    /// <summary>The item (items.json id) the corpse carries.</summary>
    public string HeadItem { get; set; } = "bigfoot_head";
    /// <summary>How long the corpse with the head, and the footsteps that lead to it, last: much longer than a player's corpse (120 s), since it has to be found.</summary>
    public float CorpseSeconds { get; set; } = 900;
    /// <summary>The body lies where it fell (the death clip) this long, then it is gone and the footsteps begin.</summary>
    public float DeathSeconds { get; set; } = 8;
    public float TrailLengthM { get; set; } = 120;
    public float TrailSpacingM { get; set; } = 1.9f;
    /// <summary>It tries this many headings for a trail that runs over ground the whole way, and takes the best.</summary>
    public int TrailTries { get; set; } = 24;
    /// <summary>A player who hit it at least this many times is credited with the win, as is a living one within the participant radius when it fell.</summary>
    public int CreditMinHits { get; set; } = 3;
}

public sealed class BigfootFallbackSpawn
{
    public float MinDistanceM { get; set; } = 55;
    public float MaxDistanceM { get; set; } = 85;
}

/// <summary>The rules of the Bigfoot that need no world: the hit count, the phase, the weight of a hit, and the ids it uses on the wire.</summary>
public static class BigfootRules
{
    /// <summary>Wire id of the Bigfoot world entity type and of the log it throws (1 corpse, 2 ground item, 10 missile, 20 turret, 30 care package, 40 aircraft, 50 animal).</summary>
    public const byte TypeId = 60, TrunkTypeId = 61;

    /// <summary>
    /// The id the Bigfoot has as a hit target (shots, blasts and the Hit event name it by it). Players live in 1 to 0x3FFF, vehicles above, the target dummies
    /// from 50000 to 50063, so this one is clear of all of them. It is also the owner of the corpse the Bigfoot leaves, which is how a client knows to draw a giant.
    /// </summary>
    public const ushort TargetId = 60000;

    /// <summary>Phase and flags in the replicated state byte: bits 0-3 the <see cref="BigfootAct"/>, bit 4 flips on every hit that did not kill, bits 5-6 the phase (0 to 2), bit 7 enraged.</summary>
    public const byte ActMask = 15, HitToggle = 16, PhaseShift = 5, PhaseMask = 3, Enraged = 128;

    /// <summary>Registers the Bigfoot and its log once (idempotent): the Bigfoot carries its hit count left in the hp byte, the log nothing.</summary>
    public static void Register(WorldEntityRegistry registry)
    {
        if (registry.Find(TypeId) == null) registry.Register(new WorldEntityDef(TypeId, "bigfoot", HasHp: true));
        if (registry.Find(TrunkTypeId) == null) registry.Register(new WorldEntityDef(TrunkTypeId, "bigfoot trunk", HasHp: false));
    }

    public static BigfootAct ActOf(byte state) => (BigfootAct)(state & ActMask);
    public static bool HitToggled(byte state) => (state & HitToggle) != 0;
    public static int PhaseOf(byte state) => (state >> PhaseShift) & PhaseMask;
    public static bool IsEnraged(byte state) => (state & Enraged) != 0;

    public static byte Pack(BigfootAct act, bool hitToggle, int phase, bool enraged) =>
        (byte)((byte)act | (hitToggle ? HitToggle : 0) | (Math.Clamp(phase, 0, 2) << PhaseShift) | (enraged ? Enraged : 0));

    /// <summary>The clips the placeholder model has, by their last name (the model names them "EnemyArmature|EnemyArmature|EnemyArmature|Attack"); a client matches on the ending.</summary>
    public const string ClipIdle = "Idle", ClipWalk = "Walk", ClipRun = "Run", ClipAttack = "Attack", ClipHit = "HitRecieve", ClipDeath = "Death", ClipJump = "Jump";
    public static readonly string[] Clips = { ClipIdle, ClipWalk, ClipRun, ClipAttack, ClipHit, ClipDeath, ClipJump };

    /// <summary>The hits needed to kill it for <paramref name="participants"/> players (at least <see cref="BigfootHits.MinPlayers"/>).</summary>
    public static int HitsNeeded(BigfootHits h, int participants) => (int)MathF.Ceiling(h.PerPlayer * Math.Max(participants, h.MinPlayers));

    /// <summary>The phase (0 to 2) for a fraction of the hit count taken.</summary>
    public static int PhaseAt(BigfootHits h, float taken) => taken >= h.ThirdPhaseAt ? 2 : taken >= h.SecondPhaseAt ? 1 : 0;

    /// <summary>The phase (0 to 2) for the seconds the fight has lasted.</summary>
    public static int PhaseAfter(BigfootHits h, float seconds) =>
        h.ThirdPhaseAfterSeconds > 0 && seconds >= h.ThirdPhaseAfterSeconds ? 2 : h.SecondPhaseAfterSeconds > 0 && seconds >= h.SecondPhaseAfterSeconds ? 1 : 0;

    public static bool EnrageAfter(BigfootHits h, float seconds) => h.EnrageAfterSeconds > 0 && seconds >= h.EnrageAfterSeconds;

    public static bool EnrageAt(BigfootHits h, float taken) => taken >= h.EnrageAt;

    /// <summary>What a hit counts, by what made it.</summary>
    public static float Weight(BigfootHits h, HitSource source) => source switch
    {
        HitSource.Bullet => 1f,
        HitSource.Explosive => h.ExplosiveWeight,
        HitSource.VehicleWeapon => h.VehicleWeaponWeight,
        _ => 0f,
    };

    /// <summary>
    /// What kind of hit a weapon byte is (<see cref="Weapon"/>: a weapons.json index, or a <see cref="SpecialWeapons"/> id). A rocket is explosive, so are the
    /// missile and the MOAB; the vehicle guns, the flak, the helicopters' and the AC-130's guns are vehicle weapons; the sentry turret fires bullets; a
    /// vehicle's ram or crash, a fall, a drowning and the void do nothing to it.
    /// </summary>
    public static HitSource SourceOf(byte weapon, int weaponCount, Func<byte, bool> isProjectile)
    {
        if (weapon < weaponCount) return isProjectile(weapon) ? HitSource.Explosive : HitSource.Bullet;
        return weapon switch
        {
            SpecialWeapons.Missile or SpecialWeapons.Moab => HitSource.Explosive,
            SpecialWeapons.VehicleGun or SpecialWeapons.Flak or SpecialWeapons.Ac130 or SpecialWeapons.AttackHelicopter or SpecialWeapons.Helper => HitSource.VehicleWeapon,
            SpecialWeapons.Turret or SpecialWeapons.Debug => HitSource.Bullet,
            _ => HitSource.None,
        };
    }
}

/// <summary>What a hit on the Bigfoot is made by, which sets what it counts.</summary>
public enum HitSource : byte { None, Bullet, Explosive, VehicleWeapon }

/// <summary>What the Bigfoot is doing: the low four bits of its replicated state byte. The clients pick the clip and the procedural motion from it.</summary>
public enum BigfootAct : byte
{
    /// <summary>Standing (it waits after a wipe, and when nobody is near).</summary>
    Idle = 0,
    Walk = 1,
    Run = 2,
    /// <summary>Winding up a log throw: the Attack clip, leaning back, then forward at the release.</summary>
    TrunkWindup = 3,
    /// <summary>Winding up the stomp: the telegraph (the Attack clip and a sound), arms and body up.</summary>
    StompWindup = 4,
    /// <summary>The stomp lands: the shockwave runs.</summary>
    Stomp = 5,
    /// <summary>Winding up a vehicle throw.</summary>
    VehicleWindup = 6,
    /// <summary>Reaching for a player.</summary>
    GrabReach = 7,
    /// <summary>Holding the player in its hand and flailing: the player is the entity's owner field.</summary>
    Hold = 8,
    /// <summary>The launch: a short Attack clip, then back to the chase.</summary>
    Launch = 9,
    Dead = 10,
}
