using System.Numerics;
using Drycastle.ShooterCore.Data;

namespace Drycastle.ShooterCore.Cartel;

/// <summary>
/// A Heist kind's Rival chase cars (cartel.json <c>jobs.&lt;kind&gt;.chase</c>; GLOSSARY "Chase car", docs/cartel.md "Chase cars"): when they come, how big their crew is, where they are put down, how they
/// fight, when they stop and when they give up. Lengths metres, speeds metres a second, times seconds. The defaults are the shipped numbers (first guesses, nothing was played).
/// </summary>
public sealed class ChaseData
{
    /// <summary>The vehicle type of vehicles.json a chase car is, and its skin (an index of that type's <c>skins</c>: the car's darkest atlas, the dark red one).</summary>
    public string VehicleType { get; set; } = "car";
    public int Skin { get; set; } = 3;

    /// <summary>The first chase car comes this long after the van left the objective with the minimum (0: the moment it leaves), then one every <see cref="IntervalSeconds"/>, while fewer than <see cref="MaxAlive"/> are in the fight.</summary>
    public float FirstAfterSeconds { get; set; } = 0;
    public float IntervalSeconds { get; set; } = 22;
    public int MaxAlive { get; set; } = 3;
    /// <summary>A due car that found no spot asks again this soon (the interval starts when one is put down).</summary>
    public float SpawnRetrySeconds { get; set; } = 3;

    /// <summary>The Gunmen in one car per tier (tier 1 first), plus <see cref="GunmenPerExtraMember"/> per party member beyond the first, at most <see cref="MaxGunmen"/> (1 to 3: the car has a driver and three more seats). The driver is a Rival Enforcer, the car's one melee member: a full car is 4, an Enforcer and 3 Gunmen.</summary>
    public int[] Gunmen { get; set; } = { 3, 3, 3, 3, 3 };
    public int GunmenPerExtraMember { get; set; } = 1;
    public int MaxGunmen { get; set; } = 3;

    /// <summary>
    /// A car is put down on open drivable ground (a road, a field, a meadow: nothing solid, no water) at least <see cref="SpawnPlayerMinM"/> from every player, out of the party's sight, apart from the other chase cars
    /// (<see cref="SpawnApartM"/>), AHEAD of the van first: <see cref="SpawnAheadMinM"/> to <see cref="SpawnAheadMaxM"/> along the van's way to the drop, an AMBUSH: <see cref="AmbushSideM"/> to either side of the way
    /// (past a road's edge) on open ground, facing along the way, in no sight rule (only no vehicle beside it); it waits there until the van has passed it (<see cref="WaitMaxSeconds"/> at most) and follows. When nothing
    /// ahead fits (the drop is near) behind it: <see cref="SpawnBackMinM"/> to <see cref="SpawnBackMaxM"/> of its trail behind it, out of the party's sight, on the nearest road within <see cref="SpawnRoadM"/> of a point of that
    /// way or on open ground at the point itself or <see cref="SpawnSpreadM"/> to either side of it.
    /// </summary>
    public float SpawnPlayerMinM { get; set; } = 150;
    public float SpawnAheadMinM { get; set; } = 150;
    public float SpawnAheadMaxM { get; set; } = 400;
    public float SpawnBackMinM { get; set; } = 150;
    public float SpawnBackMaxM { get; set; } = 500;
    public float SpawnRoadM { get; set; } = 30;
    public float SpawnApartM { get; set; } = 25;
    public float SpawnSpreadM { get; set; } = 40;
    /// <summary>An ambush car stands this far to the side of the van's way (past the road's edge for a car), facing along the way.</summary>
    public float AmbushSideM { get; set; } = 4.5f;
    /// <summary>A car that waits (an ambush, or one that pulled aside for a head-on van) drives on after this long when the van has not passed (a safety).</summary>
    public float WaitMaxSeconds { get; set; } = 40;
    /// <summary>A van that comes at a chasing car head-on (faster than <see cref="StopSpeedMps"/>, the headings more than 120 degrees apart, ahead of its nose) within this far makes it pull aside and wait.</summary>
    public float MeetM { get; set; } = 70;

    /// <summary>The Gunmen shoot from the windows only while the car is within <see cref="ShootRangeM"/> of the van; while it moves faster than <see cref="MovingMps"/> every round is turned off its line by up to <see cref="MovingAimErrorDeg"/> instead of the gun's own aim error.</summary>
    public float ShootRangeM { get; set; } = 40;
    public float MovingAimErrorDeg { get; set; } = 9;
    public float MovingMps { get; set; } = 2;

    /// <summary>The car stops and the crew gets out when the van is slower than <see cref="StopSpeedMps"/> within <see cref="StopWithinM"/> of it, when its driver is dead (it rolls; <see cref="StopSeconds"/> at most), or when it has made no 1 m for <see cref="StalledSeconds"/> (after it tried to get free).</summary>
    public float StopSpeedMps { get; set; } = 5;
    public float StopWithinM { get; set; } = 30;
    public float StopSeconds { get; set; } = 12;
    public float StalledSeconds { get; set; } = 6;
    /// <summary>A car wedged (under 1 m/s, the van beyond <see cref="FollowM"/>) for <see cref="UnstickAfterSeconds"/> backs out for <see cref="ReverseSeconds"/>, steering as to turn its nose to its way, and drives on; the count of tries goes back to 0 once it has driven 3 s at 3 m/s; after <see cref="MaxUnsticks"/> tries it stops at <see cref="StalledSeconds"/> as before.</summary>
    public float UnstickAfterSeconds { get; set; } = 2.5f;
    public float ReverseSeconds { get; set; } = 1.5f;
    public int MaxUnsticks { get; set; } = 3;

    /// <summary>
    /// A crew on foot whose van is more than <see cref="RemountM"/> away (its nearest living member) and pulling away (the gap grows faster than <see cref="PullMps"/>, smoothed) for <see cref="RemountConfirmSeconds"/> walks back
    /// to its car and gets in (the seats as they were), if the car is not wrecked, no player sits in it and the driver lives; the walk is given <see cref="RemountSeconds"/> and, when it fails, tried again after
    /// <see cref="RemountRetrySeconds"/>.
    /// </summary>
    public float RemountM { get; set; } = 60;
    public float PullMps { get; set; } = 1.5f;
    public float RemountConfirmSeconds { get; set; } = 1;
    public float RemountSeconds { get; set; } = 30;
    public float RemountRetrySeconds { get; set; } = 10;
    /// <summary>A crew on foot whose nearest living member is more than this from the van for <see cref="RemountConfirmSeconds"/> walks back to its car (no "pulling away" needed) when the car is whole, free and its driver lives.</summary>
    public float RemountFarM { get; set; } = 150;

    /// <summary>
    /// A stuck car lets its crew out only within this of the van (they walk the rest). Farther away it is LOST (<see cref="ChaseState.Lost"/>): the crew stays seated, the car brakes and keeps planning; it is relocated
    /// (a fresh spot by its kind's spawn rules) when no human can see it and every player is at least <see cref="SpawnPlayerMinM"/> from it. A car with no <see cref="StallProgressM"/> closer to the van in
    /// <see cref="StallSeconds"/> while it is farther than this and its crew rides is stalled and relocated the same way (within <see cref="StallNearM"/> of the van is close enough). No fresh spot: again after
    /// <see cref="RelocateRetrySeconds"/>; <see cref="RelocateMaxTries"/> failures in a row remove it with its crew.
    /// </summary>
    public float OutWithinM { get; set; } = 60;
    public float RelocateRetrySeconds { get; set; } = 3;
    public int RelocateMaxTries { get; set; } = 5;
    public float StallProgressM { get; set; } = 10;
    public float StallSeconds { get; set; } = 10;
    public float StallNearM { get; set; } = 40;

    /// <summary>
    /// How it drives: it keeps <see cref="FollowM"/> from the van (it slows below that and never aims at the van's body), speeds up by <see cref="CatchUpGain"/> per metre of gap, at most <see cref="MaxSpeedMps"/>; the way
    /// (the roads where they bring it closer, open ground where that is shorter: <see cref="OffRoadData"/>) is planned again every <see cref="RepathSeconds"/>. The speed is an NPC's input only: 84 km/h, 1.4 times the loaded Cartel van's
    /// 60 km/h; further than <see cref="FarM"/> behind it speeds up to <see cref="FarSpeedMps"/> (33 m/s, 120 km/h: the car's 44.4 and the Cartel van's 33 reach it) to catch up. The car type's own top speed is above or at it,
    /// so it is no allowance on the drive model; the car a player drives is not limited by it.
    /// </summary>
    public float FollowM { get; set; } = 14;
    public float CatchUpGain { get; set; } = 1;
    public float MaxSpeedMps { get; set; } = 23.33f; // 84 km/h
    public float FarM { get; set; } = 100;
    public float FarSpeedMps { get; set; } = 33; // 120 km/h
    public float RepathSeconds { get; set; } = 1.5f;
    /// <summary>
    /// The pilot's speed through bends: it may be as fast as sqrt(<see cref="CornerGrip"/> x the car type's max_lateral_accel x the bend's radius) at every bend ahead and brakes before it (<see cref="ChasePilot.CurveSpeed"/>).
    /// On a narrow road edge (under off_road track_width_m: a track, a service road) it keeps to <see cref="TrackSpeedMps"/>, on open ground (no road edge within track_width_m of it) to <see cref="OpenSpeedMps"/>;
    /// the max_speed_mps / far_speed_mps cap holds on wide roads.
    /// </summary>
    public float CornerGrip { get; set; } = 0.7f;
    public float TrackSpeedMps { get; set; } = 13;
    public float OpenSpeedMps { get; set; } = 12;

    /// <summary>A car more than <see cref="GiveUpM"/> from the van for <see cref="GiveUpSeconds"/> gives up and is removed with its crew.</summary>
    public float GiveUpM { get; set; } = 250;
    public float GiveUpSeconds { get; set; } = 20;

    /// <summary>A destroyed car's wreck lies this long, then goes (before the type's destroyed_return_time brings it back).</summary>
    public float WreckSeconds { get; set; } = 20;

    /// <summary>The crew sits in the car (drawn with the seated pose, the pelvis this far above the feet: thieves.json van.view seat_pelvis_m), their feet this far below the seat's point so the hips are on it; stepping out they stand this far beyond the door side.</summary>
    public float SeatDropM { get; set; } = 0.42f;
    public float ExitSideM { get; set; } = 0.7f;

    /// <summary>A copy to change a few numbers of (the Assault van drives on the chase car's numbers with its own vehicle, skin and give-up distance). The arrays are shared: do not edit them.</summary>
    public ChaseData Copy() => (ChaseData)MemberwiseClone();

    /// <summary>Checks the numbers; <paramref name="what"/> names the section (<c>jobs.harvest.chase</c>).</summary>
    public void Validate(string what, int tiers)
    {
        DataFileException Bad(string text) => new($"{what}: {text}");
        if (string.IsNullOrWhiteSpace(VehicleType)) throw Bad("vehicle_type names a vehicles.json type");
        if (Skin < 0) throw Bad("skin must be 0 or more");
        CartelTiers.Check(Gunmen, tiers, $"{what}: gunmen", n => n >= 1, "at least 1");
        if (GunmenPerExtraMember < 0) throw Bad("gunmen_per_extra_member must be 0 or more");
        if (MaxGunmen is < 1 or > 3) throw Bad("max_gunmen must be 1 to 3 (the car has a driver and three more seats)");
        if (MaxAlive < 1) throw Bad("max_alive must be at least 1");
        foreach (var (v, name) in new[]
        {
            (FirstAfterSeconds, "first_after_seconds"), (SpawnRetrySeconds, "spawn_retry_seconds"), (SpawnAheadMinM, "spawn_ahead_min_m"), (SpawnBackMinM, "spawn_back_min_m"), (SpawnPlayerMinM, "spawn_player_min_m"), (SpawnApartM, "spawn_apart_m"), (SpawnSpreadM, "spawn_spread_m"),
            (MovingAimErrorDeg, "moving_aim_error_deg"), (MovingMps, "moving_mps"), (StalledSeconds, "stalled_seconds"), (SeatDropM, "seat_drop_m"), (ExitSideM, "exit_side_m"),
        })
            if (!float.IsFinite(v) || v < 0) throw Bad($"{name} must be 0 or more");
        foreach (var (v, name) in new[]
        {
            (IntervalSeconds, "interval_seconds"), (SpawnAheadMaxM, "spawn_ahead_max_m"), (SpawnBackMaxM, "spawn_back_max_m"), (SpawnRoadM, "spawn_road_m"), (ShootRangeM, "shoot_range_m"), (StopSpeedMps, "stop_speed_mps"), (StopWithinM, "stop_within_m"),
            (StopSeconds, "stop_seconds"), (UnstickAfterSeconds, "unstick_after_seconds"), (ReverseSeconds, "reverse_seconds"), (RemountM, "remount_m"), (PullMps, "pull_mps"), (RemountConfirmSeconds, "remount_confirm_seconds"), (RemountSeconds, "remount_seconds"), (RemountRetrySeconds, "remount_retry_seconds"), (FollowM, "follow_m"), (CatchUpGain, "catch_up_gain"), (MaxSpeedMps, "max_speed_mps"), (RepathSeconds, "repath_seconds"), (AmbushSideM, "ambush_side_m"), (WaitMaxSeconds, "wait_max_seconds"), (MeetM, "meet_m"), (FarM, "far_m"), (FarSpeedMps, "far_speed_mps"),
            (GiveUpM, "give_up_m"), (GiveUpSeconds, "give_up_seconds"), (WreckSeconds, "wreck_seconds"),
            (RemountFarM, "remount_far_m"), (CornerGrip, "corner_grip"), (TrackSpeedMps, "track_speed_mps"), (OpenSpeedMps, "open_speed_mps"), (OutWithinM, "out_within_m"), (RelocateRetrySeconds, "relocate_retry_seconds"), (StallProgressM, "stall_progress_m"), (StallSeconds, "stall_seconds"), (StallNearM, "stall_near_m"),
        })
            if (!float.IsFinite(v) || v <= 0) throw Bad($"{name} must be more than 0");
        if (SpawnBackMaxM < SpawnBackMinM) throw Bad("spawn_back_max_m is below spawn_back_min_m");
        if (SpawnAheadMaxM < SpawnAheadMinM) throw Bad("spawn_ahead_max_m is below spawn_ahead_min_m");
        if (FarSpeedMps < MaxSpeedMps) throw Bad("far_speed_mps must be at least max_speed_mps");
        if (MaxUnsticks < 0) throw Bad("max_unsticks must be 0 or more");
        if (GiveUpM <= StopWithinM) throw Bad("give_up_m must be more than stop_within_m");
        if (RelocateMaxTries < 1) throw Bad("relocate_max_tries must be at least 1");
        if (RemountFarM >= GiveUpM) throw Bad("remount_far_m must be less than give_up_m (the crew walks back before it is given up)");
        if (RemountM >= GiveUpM) throw Bad("remount_m must be less than give_up_m (the crew walks back before it is given up)");
    }
}

/// <summary>The rules of a Heist's chase cars that are plain numbers (docs/cartel.md "Chase cars").</summary>
public static class ChaseRules
{
    /// <summary>A car's whole crew for a party of <paramref name="size"/>: the driver (an Enforcer, the one melee member) and its Gunmen.</summary>
    public static int Crew(ChaseData d, int tier, int size) => 1 + Gunmen(d, tier, size);

    /// <summary>The Gunmen in one car for a party of <paramref name="size"/>: the tier's plus gunmen_per_extra_member for each member past the first, 1 to max_gunmen.</summary>
    public static int Gunmen(ChaseData d, int tier, int size) =>
        Math.Clamp(CartelTiers.Of(d.Gunmen, tier) + d.GunmenPerExtraMember * Math.Max(0, size - 1), 1, d.MaxGunmen);

    /// <summary>
    /// The speed the car wants while it follows a van on the road: the van's, plus catch_up_gain for each metre the gap is past follow_m (minus for each short of it), between standing and max_speed_mps (84 km/h),
    /// or far_speed_mps (120 km/h) while the gap is more than far_m (the rubber band: a car left far behind catches up). A car closer than follow_m slows below the van's speed and falls back; it never closes on the van's body.
    /// </summary>
    public static float FollowSpeed(ChaseData d, float gapM, float vanSpeed) =>
        Math.Clamp(vanSpeed + (gapM - d.FollowM) * d.CatchUpGain, 0f, gapM > d.FarM ? d.FarSpeedMps : d.MaxSpeedMps);

    /// <summary>The unit vector of a Godot yaw on the ground (x, z): the way a vehicle with that yaw faces.</summary>
    public static Vector2 Forward(float yaw) => new(-MathF.Sin(yaw), -MathF.Cos(yaw));

    /// <summary>The van has passed the car: the car lies behind the van along the van's heading (<paramref name="vanDir"/>, a unit vector; none, a van that stands, is not passed).</summary>
    public static bool Passed(Vector2 car, Vector2 van, Vector2 vanDir) => Vector2.Dot(car - van, vanDir) < 0f;

    /// <summary>The headings of a head-on meeting are more than this far apart (degrees).</summary>
    public const float HeadOnDeg = 120f;

    /// <summary>
    /// The van comes at the car head-on: it moves faster than stop_speed_mps, is within meet_m, ahead of the car's nose (along its forward axis) and the two headings are more than 120 degrees apart.
    /// </summary>
    public static bool HeadOn(ChaseData d, Vector2 car, float carYaw, Vector2 van, Vector2 vanDir, float vanSpeed)
    {
        if (vanSpeed <= d.StopSpeedMps) return false;
        var to = van - car;
        if (to.Length() > d.MeetM) return false;
        var fwd = Forward(carYaw);
        return Vector2.Dot(to, fwd) > 0f && Vector2.Dot(fwd, vanDir) < MathF.Cos(HeadOnDeg * MathF.PI / 180f);
    }

    /// <summary>The aim error of a Gunman's round now: moving_aim_error_deg while the car moves faster than moving_mps, else null (the gun's own).</summary>
    public static float? AimErrorDeg(ChaseData d, float carSpeed) => MathF.Abs(carSpeed) > d.MovingMps ? d.MovingAimErrorDeg : null;
}
