using System.Globalization;

namespace Drycastle.ShooterCore;

/// <summary>
/// environment.json: the day/night clock defaults. Server flags (--time, --day-length, --freeze-time) override these
/// through <see cref="ToClock"/>. Times are "HH:MM" in 24 hour format.
/// </summary>
public sealed class EnvironmentConfig : IVersioned
{
    public int Version { get; set; }
    public string StartTime { get; set; } = "08:00";
    public double DayLengthMinutes { get; set; } = DayClock.DefaultDayLengthMinutes;
    /// <summary>When set, the clock is frozen at this time of day.</summary>
    public string? FrozenTime { get; set; }
    /// <summary>Ambient light floor at night so the world is never pitch black (0 would be fully dark).</summary>
    public float MinAmbient { get; set; } = 0.10f;
    /// <summary>How a street lamp lights up at night (the lens, the beam): the same kind of numbers as a vehicle's <c>lights</c>.</summary>
    public LampLook Lamp { get; set; } = new();
    /// <summary>The shootable plate of a road sign (ADR 0038).</summary>
    public SignPlateLook SignPlate { get; set; } = new();

    public static EnvironmentConfig Parse(string json)
    {
        var c = DataFile.Parse<EnvironmentConfig>(json, "environment.json");
        c.Validate();
        return c;
    }

    public static EnvironmentConfig Load(string path)
    {
        var c = DataFile.Load<EnvironmentConfig>(path);
        c.Validate();
        return c;
    }

    void Validate()
    {
        ParseHhMm(StartTime, "start_time");
        if (FrozenTime != null) ParseHhMm(FrozenTime, "frozen_time");
        if (!(DayLengthMinutes > 0)) throw new DataFileException("environment.json: day_length_minutes must be above 0");
        if (MinAmbient < 0 || MinAmbient > 1) throw new DataFileException("environment.json: min_ambient must be 0..1");
        if (!(Lamp.RangeM > 0) || !(Lamp.AngleDeg > 0 && Lamp.AngleDeg < 90) || Lamp.Energy < 0 || Lamp.LensEnergy < 0)
            throw new DataFileException("environment.json: lamp needs range_m above 0, angle_deg 0..90 (exclusive), energy and lens_energy 0 or more");
        if (!(Lamp.HitRadiusM > 0) || !(Lamp.RespawnSeconds > 0) || !(Lamp.FlickerSeconds > 0))
            throw new DataFileException("environment.json: lamp needs hit_radius_m, respawn_seconds and flicker_seconds above 0");
        if (!(SignPlate.HitRadiusM > 0) || !(SignPlate.RespawnSeconds > 0))
            throw new DataFileException("environment.json: sign_plate needs hit_radius_m and respawn_seconds above 0");
    }

    /// <summary>"HH:MM" to minute of the day (0..1439). Throws <see cref="DataFileException"/> naming the field on bad input.</summary>
    public static int ParseHhMm(string s, string what = "time")
    {
        var p = s.Split(':');
        if (p.Length != 2 || p[0].Length != 2 || p[1].Length != 2
            || !int.TryParse(p[0], NumberStyles.None, CultureInfo.InvariantCulture, out var h)
            || !int.TryParse(p[1], NumberStyles.None, CultureInfo.InvariantCulture, out var m)
            || h < 0 || h > 23 || m < 0 || m > 59)
            throw new DataFileException($"{what}: \"{s}\" is not a time of day, expected HH:MM");
        return h * 60 + m;
    }

    /// <summary>
    /// The clock the server runs. <paramref name="timeOverride"/> is the --time flag; <paramref name="freeze"/> is
    /// --freeze-time. The file's frozen_time also freezes the clock, at that time unless --time is given.
    /// </summary>
    public DayClock ToClock(string? timeOverride = null, double? dayLengthMinutesOverride = null, bool freeze = false)
    {
        var frozen = freeze || FrozenTime != null;
        var time = timeOverride ?? (frozen ? FrozenTime ?? StartTime : StartTime);
        var minutes = dayLengthMinutesOverride ?? DayLengthMinutes;
        if (!(minutes > 0)) throw new DataFileException("--day-length must be above 0 minutes");
        return new DayClock
        {
            StartMinute = ParseHhMm(time, "--time"),
            DayLengthSeconds = minutes * 60.0,
            Frozen = frozen,
        };
    }
}

/// <summary>
/// The light of one street lamp at full night (environment.json <c>lamp</c>): a spot beam straight down from the luminaire, like a vehicle headlamp
/// (<c>BeamRange</c>/<c>BeamAngle</c> in vehicles.json), and a lit lens that blooms. <see cref="AngleDeg"/> is the half angle of the cone.
/// Tuned by eye: nothing here was seen in the game.
/// </summary>
public sealed class LampLook
{
    public double RangeM { get; set; } = 12;
    public double AngleDeg { get; set; } = 55;
    public double Energy { get; set; } = 4;
    public string Colour { get; set; } = "#ffc773";
    /// <summary>Emission of the lens at full night; above the bloom threshold (2) on purpose (ADR 0030).</summary>
    public double LensEnergy { get; set; } = 6;
    /// <summary>A pole lamp is broken by a round that passes within this many metres of its head.</summary>
    public double HitRadiusM { get; set; } = 0.25;
    /// <summary>A broken pole lamp stays dark this long, then flickers for <see cref="FlickerSeconds"/> and is lit again (ADR 0037).</summary>
    public float RespawnSeconds { get; set; } = 120;
    public float FlickerSeconds { get; set; } = 3;
}

/// <summary>
/// The shootable plate of a road sign (environment.json <c>sign_plate</c>, ADR 0038). A round that passes within <see cref="HitRadiusM"/> of the plate's centre
/// (the asset's "plates" point) bends it; it hangs bent for <see cref="RespawnSeconds"/> and is whole again. Tuned by eye: nothing here was seen in the game.
/// </summary>
public sealed class SignPlateLook
{
    public double HitRadiusM { get; set; } = 0.3;
    public float RespawnSeconds { get; set; } = 120;
}
