using System.Numerics;

namespace Suchohrad.ShooterCore;

/// <summary>
/// The server's time of day. A 24 hour day takes <see cref="DayLengthSeconds"/> of real time, starting at
/// <see cref="StartMinute"/>; when <see cref="Frozen"/> the hour never moves. Pure data and maths, no scene.
/// </summary>
public sealed class DayClock
{
    public const double DefaultDayLengthMinutes = 24; // 1 real minute = 1 game hour

    /// <summary>Minute of the day at elapsed time 0, 0..1439.</summary>
    public int StartMinute { get; init; } = 8 * 60;
    public double DayLengthSeconds { get; init; } = DefaultDayLengthMinutes * 60;
    public bool Frozen { get; init; }

    /// <summary>Hour of day in [0, 24) after <paramref name="elapsedSeconds"/> of server time.</summary>
    public double HourAt(double elapsedSeconds)
    {
        var start = StartMinute / 60.0;
        if (Frozen || DayLengthSeconds <= 0) return start;
        var h = (start + elapsedSeconds * 24.0 / DayLengthSeconds) % 24.0;
        return h < 0 ? h + 24 : h;
    }

    public double HourAtTick(long tick, int tickRate) => HourAt(tick / (double)tickRate);

    /// <summary>Lighting, sky and fog for the given hour (see <see cref="DayCurve"/> for the numbers).</summary>
    public DayLighting Sample(double hour, float minAmbient) => DayCurve.Sample(hour, minAmbient);
}

/// <summary>
/// The lighting curve, sampled per hour. Colours are linear RGB 0..1. Numbers are tuned by eye for the flat
/// low-poly look; every one is named here so it can be retuned in one place.
/// </summary>
public readonly record struct DayLighting(
    float SunElevationDeg,
    float SunEnergy,
    Vector3 SunColour,
    float MoonElevationDeg,
    float MoonEnergy,
    Vector3 MoonColour,
    float AmbientEnergy,
    Vector3 AmbientColour,
    Vector3 SkyTop,
    Vector3 SkyHorizon,
    float StarVisibility,
    float Exposure,
    float FogDensity,
    float NightFactor);

public static class DayCurve
{
    /// <summary>Highest the sun gets at noon, degrees above the horizon.</summary>
    public const float MaxSunElevationDeg = 60f;
    /// <summary>Peak moon light at full night (sun energy at noon is about 1.1).</summary>
    public const float MoonPeakEnergy = 0.3f;
    /// <summary>Ambient energy at full day; night goes down to the configured min_ambient.</summary>
    public const float AmbientDayEnergy = 0.7f;
    /// <summary>Exposure at full day and at full night; night is lifted so shapes stay readable.</summary>
    public const float ExposureDay = 1.0f, ExposureNight = 1.35f;
    public const float FogDay = 0.0006f, FogNight = 0.0012f;

    static readonly Vector3 AmbientDay = new(1f, 1f, 1f), AmbientNight = new(0.45f, 0.52f, 0.75f);
    static readonly Vector3 SunDawn = new(1f, 0.62f, 0.38f), SunNoon = new(1f, 0.97f, 0.9f);
    static readonly Vector3 MoonLight = new(0.6f, 0.7f, 0.95f);
    static readonly Vector3 SkyTopDay = new(0.33f, 0.56f, 0.9f), SkyTopNight = new(0.015f, 0.02f, 0.06f);
    static readonly Vector3 HorizonDay = new(0.72f, 0.84f, 0.95f), HorizonNight = new(0.06f, 0.07f, 0.12f);
    static readonly Vector3 HorizonDusk = new(0.95f, 0.55f, 0.32f);

    /// <summary>
    /// sin of the sun's angle on its arc: 0 at 06:00 and 18:00, +1 at noon, -1 at midnight. Linear in the
    /// angle, so the sun moves at a constant rate through the day.
    /// </summary>
    public static float SunSine(double hour) => (float)Math.Sin((hour - 6.0) / 24.0 * Math.PI * 2.0);

    public static float Smoothstep(float a, float b, float x)
    {
        var t = Math.Clamp((x - a) / (b - a), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>0 at full day, 1 at full night. Transition band is sun sine -0.15..+0.25 (about 05:00 to 07:30, 16:30 to 19:00).</summary>
    public static float NightFactorAt(double hour) => 1f - Smoothstep(-0.15f, 0.25f, SunSine(hour));

    public static DayLighting Sample(double hour, float minAmbient)
    {
        var s = SunSine(hour);
        var day = Smoothstep(-0.15f, 0.25f, s);
        var night = 1f - day;
        var dusk = MathF.Max(0f, 1f - MathF.Abs(s) / 0.3f); // warm band right at the horizon

        var sunElev = MaxSunElevationDeg * s;
        var sunColour = Vector3.Lerp(SunDawn, SunNoon, Smoothstep(0f, 0.6f, s));
        var sunEnergy = 1.1f * Smoothstep(-0.05f, 0.2f, s);

        // the moon sits on the opposite side of the sky to the sun, and only lights the night
        var moonElev = -sunElev * 0.8f;
        var moonEnergy = MoonPeakEnergy * Smoothstep(-0.05f, 0.2f, -s);

        // ambient never drops below minAmbient, so the night stays playable
        var ambientEnergy = minAmbient + (AmbientDayEnergy - minAmbient) * day;
        var ambientColour = Vector3.Lerp(AmbientNight, AmbientDay, day);

        var skyTop = Vector3.Lerp(SkyTopNight, SkyTopDay, day);
        var horizon = Vector3.Lerp(HorizonNight, HorizonDay, day);
        horizon = Vector3.Lerp(horizon, HorizonDusk, dusk * 0.5f);

        return new DayLighting(
            SunElevationDeg: sunElev,
            SunEnergy: sunEnergy,
            SunColour: sunColour,
            MoonElevationDeg: moonElev,
            MoonEnergy: moonEnergy,
            MoonColour: MoonLight,
            AmbientEnergy: ambientEnergy,
            AmbientColour: ambientColour,
            SkyTop: skyTop,
            SkyHorizon: horizon,
            StarVisibility: Smoothstep(0.4f, 1f, night),
            Exposure: ExposureDay + (ExposureNight - ExposureDay) * night,
            FogDensity: FogDay + (FogNight - FogDay) * night,
            NightFactor: night);
    }
}
