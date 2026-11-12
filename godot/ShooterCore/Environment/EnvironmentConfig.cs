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
    public float MinAmbient { get; set; } = 0.35f;

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
