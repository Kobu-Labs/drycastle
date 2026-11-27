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

    /// <summary>The first chase car comes this long after the van left the objective with the minimum, then one every <see cref="IntervalSeconds"/>, while fewer than <see cref="MaxAlive"/> are in the fight.</summary>
    public float FirstAfterSeconds { get; set; } = 20;
    public float IntervalSeconds { get; set; } = 45;
    public int MaxAlive { get; set; } = 2;
    /// <summary>A due car that found no spot asks again this soon (the interval starts when one is put down).</summary>
    public float SpawnRetrySeconds { get; set; } = 3;

    /// <summary>The Gunmen in one car per tier (tier 1 first), plus <see cref="GunmenPerExtraMember"/> per party member beyond the first, at most <see cref="MaxGunmen"/> (1 to 3: the car has a driver and three more seats). The driver is a Rival Enforcer.</summary>
    public int[] Gunmen { get; set; } = { 1, 1, 2, 2, 3 };
    public int GunmenPerExtraMember { get; set; } = 1;
    public int MaxGunmen { get; set; } = 3;

    /// <summary>A car is put down on a road at least <see cref="SpawnPlayerMinM"/> from every player, out of the party's sight, apart from the other chase cars (<see cref="SpawnApartM"/>), on the van's way back from the objective: <see cref="SpawnBackMinM"/> to <see cref="SpawnBackMaxM"/> of its trail behind the van, the nearest road within <see cref="SpawnRoadM"/> of it.</summary>
    public float SpawnPlayerMinM { get; set; } = 150;
    public float SpawnBackMinM { get; set; } = 150;
    public float SpawnBackMaxM { get; set; } = 500;
    public float SpawnRoadM { get; set; } = 30;
    public float SpawnApartM { get; set; } = 25;

    /// <summary>The Gunmen shoot from the windows only while the car is within <see cref="ShootRangeM"/> of the van; while it moves faster than <see cref="MovingMps"/> every round is turned off its line by up to <see cref="MovingAimErrorDeg"/> instead of the gun's own aim error.</summary>
    public float ShootRangeM { get; set; } = 40;
    public float MovingAimErrorDeg { get; set; } = 9;
    public float MovingMps { get; set; } = 2;

    /// <summary>The car stops and the crew gets out when the van is slower than <see cref="StopSpeedMps"/> within <see cref="StopWithinM"/> of it, when its driver is dead (it rolls; <see cref="StopSeconds"/> at most), when it has made no 1 m for <see cref="StalledSeconds"/>, or, with the van off the road, when it reached the road point nearest the van (<see cref="ArriveM"/>).</summary>
    public float StopSpeedMps { get; set; } = 5;
    public float StopWithinM { get; set; } = 30;
    public float StopSeconds { get; set; } = 12;
    public float StalledSeconds { get; set; } = 6;
    public float ArriveM { get; set; } = 8;
    /// <summary>The van is off the road when no road is within this of it.</summary>
    public float OffRoadM { get; set; } = 15;

    /// <summary>How it drives: it keeps <see cref="FollowM"/> from the van (it slows below that and never aims at the van's body), speeds up by <see cref="CatchUpGain"/> per metre of gap, at most <see cref="MaxSpeedMps"/>; the way is planned again every <see cref="RepathSeconds"/>.</summary>
    public float FollowM { get; set; } = 14;
    public float CatchUpGain { get; set; } = 1;
    public float MaxSpeedMps { get; set; } = 38;
    public float RepathSeconds { get; set; } = 1;

    /// <summary>A car more than <see cref="GiveUpM"/> from the van for <see cref="GiveUpSeconds"/> gives up and is removed with its crew.</summary>
    public float GiveUpM { get; set; } = 250;
    public float GiveUpSeconds { get; set; } = 20;

    /// <summary>A destroyed car's wreck lies this long, then goes (before the type's destroyed_return_time brings it back).</summary>
    public float WreckSeconds { get; set; } = 20;

    /// <summary>The crew sits in the car as standing figures, their feet this far below the seat's point (so the torso is at the window, the legs in the body); stepping out they stand this far beyond the door side.</summary>
    public float SeatDropM { get; set; } = 0.5f;
    public float ExitSideM { get; set; } = 0.7f;

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
            (FirstAfterSeconds, "first_after_seconds"), (SpawnRetrySeconds, "spawn_retry_seconds"), (SpawnBackMinM, "spawn_back_min_m"), (SpawnPlayerMinM, "spawn_player_min_m"), (SpawnApartM, "spawn_apart_m"),
            (MovingAimErrorDeg, "moving_aim_error_deg"), (MovingMps, "moving_mps"), (StalledSeconds, "stalled_seconds"), (SeatDropM, "seat_drop_m"), (ExitSideM, "exit_side_m"),
        })
            if (!float.IsFinite(v) || v < 0) throw Bad($"{name} must be 0 or more");
        foreach (var (v, name) in new[]
        {
            (IntervalSeconds, "interval_seconds"), (SpawnBackMaxM, "spawn_back_max_m"), (SpawnRoadM, "spawn_road_m"), (ShootRangeM, "shoot_range_m"), (StopSpeedMps, "stop_speed_mps"), (StopWithinM, "stop_within_m"),
            (StopSeconds, "stop_seconds"), (ArriveM, "arrive_m"), (OffRoadM, "off_road_m"), (FollowM, "follow_m"), (CatchUpGain, "catch_up_gain"), (MaxSpeedMps, "max_speed_mps"), (RepathSeconds, "repath_seconds"),
            (GiveUpM, "give_up_m"), (GiveUpSeconds, "give_up_seconds"), (WreckSeconds, "wreck_seconds"),
        })
            if (!float.IsFinite(v) || v <= 0) throw Bad($"{name} must be more than 0");
        if (SpawnBackMaxM < SpawnBackMinM) throw Bad("spawn_back_max_m is below spawn_back_min_m");
        if (GiveUpM <= StopWithinM) throw Bad("give_up_m must be more than stop_within_m");
    }
}

/// <summary>The rules of a Heist's chase cars that are plain numbers (docs/cartel.md "Chase cars").</summary>
public static class ChaseRules
{
    /// <summary>The Gunmen in one car for a party of <paramref name="size"/>: the tier's plus gunmen_per_extra_member for each member past the first, 1 to max_gunmen.</summary>
    public static int Gunmen(ChaseData d, int tier, int size) =>
        Math.Clamp(CartelTiers.Of(d.Gunmen, tier) + d.GunmenPerExtraMember * Math.Max(0, size - 1), 1, d.MaxGunmen);

    /// <summary>
    /// The speed the car wants while it follows a van on the road: the van's, plus catch_up_gain for each metre the gap is past follow_m (minus for each short of it), between standing and max_speed_mps.
    /// A car closer than follow_m slows below the van's speed and falls back; it never closes on the van's body.
    /// </summary>
    public static float FollowSpeed(ChaseData d, float gapM, float vanSpeed) =>
        Math.Clamp(vanSpeed + (gapM - d.FollowM) * d.CatchUpGain, 0f, d.MaxSpeedMps);

    /// <summary>The speed the car wants on the way to the road point nearest an off-road van, <paramref name="distanceM"/> from it: braking for the stop (about 6 m/s squared), at least a crawl, at most max_speed_mps.</summary>
    public static float ApproachSpeed(ChaseData d, float distanceM) =>
        Math.Clamp(MathF.Sqrt(2f * 6f * MathF.Max(0f, distanceM - d.ArriveM)), 3f, d.MaxSpeedMps);

    /// <summary>The aim error of a Gunman's round now: moving_aim_error_deg while the car moves faster than moving_mps, else null (the gun's own).</summary>
    public static float? AimErrorDeg(ChaseData d, float carSpeed) => MathF.Abs(carSpeed) > d.MovingMps ? d.MovingAimErrorDeg : null;
}
