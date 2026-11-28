using System.Buffers.Binary;
using System.Numerics;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Net;

namespace Drycastle.ShooterCore.Scenery;

/// <summary>The sky's weather (GLOSSARY "Weather"): clear, or a thunderstorm. The values are sent as a byte (<see cref="EventKind.WeatherState"/>).</summary>
public enum WeatherPhase : byte { Clear = 0, Storm = 1 }

/// <summary>One lightning strike (GLOSSARY "Thunderstorm"): where it lands as seen from every player, a compass bearing and a distance. Nothing is hit.</summary>
public readonly record struct LightningStrike(float BearingDeg, float DistanceM);

/// <summary>
/// The server's weather (Weather, #230): clear for a random while, then a thunderstorm for a random while, and again. In a storm a lightning strike comes every few seconds (<see cref="Tick"/> returns it).
/// An admin forces either phase (<see cref="ForceStorm"/>, <see cref="ForceClear"/>). Pure: the clock is <c>dt</c> and the randomness is injected, so a test steps it without a game.
/// </summary>
public sealed class WeatherMachine
{
    /// <summary>Clear to Storm after a random wait, Storm to Clear when it has lasted its time; an admin may force either at any moment.</summary>
    public static readonly StateMachineSpec<WeatherPhase> Spec = StateMachineSpec<WeatherPhase>.Define("Weather.Phase", "The village's sky: clear, or a thunderstorm that comes and goes by itself", WeatherPhase.Clear)
        .Allow(WeatherPhase.Clear, WeatherPhase.Storm)
        .Allow(WeatherPhase.Storm, WeatherPhase.Clear)
        .Note(WeatherPhase.Clear, "Counts down every_minutes to the next storm.")
        .Note(WeatherPhase.Storm, "Lasts lasting_minutes; a lightning strike every strike_seconds; Thundernite can be taken; the client's sky darkens.")
        .Build();

    readonly StormLook _cfg;
    readonly Random _rng;
    readonly StateMachine<WeatherPhase> _fsm = new(Spec);
    Countdown _strike;

    public WeatherMachine(StormLook cfg, Random rng)
    {
        _cfg = cfg; _rng = rng;
        _fsm.Restart((float)(Pick(cfg.EveryMinutes) * 60));
    }

    public WeatherPhase Phase => _fsm.Current;
    /// <summary>A thunderstorm is on (what Thundernite's rule reads).</summary>
    public bool Storm => _fsm.Is(WeatherPhase.Storm);
    /// <summary>Seconds until the phase ends by itself.</summary>
    public float SecondsLeft => _fsm.Left;

    /// <summary>Raised when the phase changes (also by a force): the new phase.</summary>
    public event Action<WeatherPhase>? Changed;

    /// <summary>Forces a storm now (lasting a normal storm's time again when one is already on). True when the phase changed.</summary>
    public bool ForceStorm() => Go(WeatherPhase.Storm);

    /// <summary>Forces clear weather now (a fresh wait for the next storm). True when the phase changed.</summary>
    public bool ForceClear() => Go(WeatherPhase.Clear);

    /// <summary>Advances by <paramref name="dt"/> seconds. Returns the lightning strike of this tick, or null.</summary>
    public LightningStrike? Tick(float dt)
    {
        if (_fsm.Tick(dt)) { Go(Storm ? WeatherPhase.Clear : WeatherPhase.Storm); return null; }
        if (Storm && _strike.Tick(dt))
        {
            _strike.Start((float)Pick(_cfg.StrikeSeconds));
            return new LightningStrike((float)(_rng.NextDouble() * 360.0), (float)Pick(_cfg.StrikeDistanceM));
        }
        return null;
    }

    bool Go(WeatherPhase next)
    {
        var storm = next == WeatherPhase.Storm;
        var seconds = (float)(Pick(storm ? _cfg.LastingMinutes : _cfg.EveryMinutes) * 60);
        var changed = _fsm.Enter(next, seconds);
        if (!changed) _fsm.Restart(seconds);
        if (storm) _strike.Start((float)Pick(_cfg.StrikeSeconds)); else _strike.Cancel();
        if (changed) Changed?.Invoke(next);
        return changed;
    }

    double Pick(double[] range) => range[0] + _rng.NextDouble() * (range[1] - range[0]);
}

/// <summary>The codecs of the weather events: the phase (<see cref="EventKind.WeatherState"/>) and a lightning strike (<see cref="EventKind.Lightning"/>).</summary>
public static class WeatherCodec
{
    /// <summary>Server to everyone on a change, and to a joiner (<see cref="EventKind.WeatherState"/>): u8 <see cref="WeatherPhase"/>.</summary>
    public static byte[] EncodeState(WeatherPhase phase) => new[] { (byte)phase };

    public static bool TryDecodeState(ReadOnlySpan<byte> p, out WeatherPhase phase)
    {
        phase = WeatherPhase.Clear;
        if (p.Length != 1 || !Enum.IsDefined((WeatherPhase)p[0])) return false;
        phase = (WeatherPhase)p[0];
        return true;
    }

    /// <summary>Server to everyone (<see cref="EventKind.Lightning"/>): u16 bearing in tenths of a degree (0..3599), u16 distance in metres (at most 65535).</summary>
    public static byte[] EncodeStrike(LightningStrike s)
    {
        var b = new byte[4];
        var tenths = (int)MathF.Round(((s.BearingDeg % 360f) + 360f) % 360f * 10f) % 3600;
        BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)tenths);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), (ushort)Math.Clamp((int)MathF.Round(s.DistanceM), 0, ushort.MaxValue));
        return b;
    }

    public static bool TryDecodeStrike(ReadOnlySpan<byte> p, out LightningStrike strike)
    {
        strike = default;
        if (p.Length != 4) return false;
        var tenths = BinaryPrimitives.ReadUInt16LittleEndian(p);
        if (tenths >= 3600) return false;
        strike = new LightningStrike(tenths / 10f, BinaryPrimitives.ReadUInt16LittleEndian(p[2..]));
        return true;
    }
}

/// <summary>
/// How a thunderstorm looks and sounds on the client, pure so a test checks the numbers: the day's lighting turned toward a storm (<see cref="Apply"/>), the flash of a strike (<see cref="FlashAt"/>,
/// <see cref="FlashStrength"/>) and its thunder, which arrives late and quieter the farther the strike was (<see cref="ThunderDelaySeconds"/>, <see cref="ThunderDb"/>). Tuned by eye: nothing here was seen in the game.
/// </summary>
public static class StormSky
{
    public const float SunFactor = 0.2f, MoonFactor = 0.3f, AmbientFactor = 0.6f, FogFactor = 3f;
    /// <summary>The overcast sky by day (linear); at night it is scaled down by the night factor.</summary>
    public static readonly Vector3 Grey = new(0.18f, 0.19f, 0.21f);
    /// <summary>The sky and the ambient light at the peak of a flash.</summary>
    public static readonly Vector3 FlashSky = new(0.85f, 0.9f, 1f);
    public const float FlashAmbient = 1.2f, FlashSkyMix = 0.7f;

    public const float SpeedOfSoundMps = 343f;
    public const float NearM = 150f, FarM = 1500f, FarDb = -18f;
    /// <summary>How long a flash lasts: three quick flickers.</summary>
    public const float FlashSeconds = 0.35f;
    static readonly (float At, float Amp)[] Flickers = { (0f, 1f), (0.11f, 0.55f), (0.22f, 0.8f) };

    /// <summary><paramref name="storm"/> 0..1 (how far the sky has darkened) and <paramref name="flash"/> 0..1 (a strike's flash now) applied to the lighting of the hour.</summary>
    public static DayLighting Apply(DayLighting l, float storm, float flash)
    {
        storm = Math.Clamp(storm, 0f, 1f);
        flash = Math.Clamp(flash, 0f, 1f);
        if (storm <= 0f && flash <= 0f) return l;
        var grey = Grey * (1f - 0.9f * l.NightFactor); // by night the sky is dark already
        var top = Vector3.Lerp(l.SkyTop, grey, storm);
        var horizon = Vector3.Lerp(l.SkyHorizon, grey * 1.15f, storm);
        top = Vector3.Lerp(top, FlashSky, flash * FlashSkyMix);
        horizon = Vector3.Lerp(horizon, FlashSky, flash * FlashSkyMix);
        return l with
        {
            SunEnergy = l.SunEnergy * Lerp(1f, SunFactor, storm),
            MoonEnergy = l.MoonEnergy * Lerp(1f, MoonFactor, storm),
            AmbientEnergy = l.AmbientEnergy * Lerp(1f, AmbientFactor, storm) + flash * FlashAmbient,
            FogDensity = l.FogDensity * Lerp(1f, FogFactor, storm),
            StarVisibility = l.StarVisibility * (1f - storm), // overcast: no stars
            SkyTop = top,
            SkyHorizon = horizon,
        };
    }

    /// <summary>The brightness 0..1 of a flash <paramref name="age"/> seconds after the strike: a quick decay at each of three flickers; 0 once it is over.</summary>
    public static float FlashAt(float age)
    {
        if (age < 0f || age >= FlashSeconds) return 0f;
        var v = 0f;
        foreach (var (at, amp) in Flickers)
            if (age >= at) v = Math.Max(v, amp * MathF.Exp(-(age - at) / 0.045f));
        return v;
    }

    /// <summary>How strong the flash of a strike this far away is, 1 at <see cref="NearM"/> and closer down to a quarter at <see cref="FarM"/> and farther.</summary>
    public static float FlashStrength(float distanceM) => Lerp(1f, 0.25f, Fraction(distanceM));

    /// <summary>Seconds until the thunder reaches the player: sound travels 343 m/s.</summary>
    public static float ThunderDelaySeconds(float distanceM) => MathF.Max(distanceM, 0f) / SpeedOfSoundMps;

    /// <summary>The thunder's level offset: 0 dB at <see cref="NearM"/> and closer, <see cref="FarDb"/> at <see cref="FarM"/> and farther.</summary>
    public static float ThunderDb(float distanceM) => Lerp(0f, FarDb, Fraction(distanceM));

    /// <summary>A strike nearer than this many metres is heard as a close crack and its roll; farther, as a low rumble.</summary>
    public const float NearThunderM = 400f;
    public const string ThunderNearSound = "weather.thunder.near", ThunderFarSound = "weather.thunder.far";

    /// <summary>The sounds.json id of the thunder of a strike this far away.</summary>
    public static string ThunderSound(float distanceM) => distanceM < NearThunderM ? ThunderNearSound : ThunderFarSound;

    /// <summary>Below this the rain loop is stopped, not played quietly.</summary>
    public const float RainSilentDb = -80f;

    /// <summary>
    /// The rain loop's level offset: silent while the storm is barely there (<paramref name="storm"/> under 1%), then the amplitude follows the storm (-40 dB at 1%, <paramref name="rainDb"/> at 1), and
    /// <paramref name="indoorDb"/> more the more sheltered the player is (<paramref name="shelter"/> 0..1).
    /// </summary>
    public static float RainDb(float storm, float shelter, float rainDb, float indoorDb)
    {
        storm = Math.Clamp(storm, 0f, 1f);
        if (storm < 0.01f) return RainSilentDb;
        return rainDb + 20f * MathF.Log10(storm) + indoorDb * Math.Clamp(shelter, 0f, 1f);
    }

    /// <summary>The low-pass cutoff of the rain bus: open outdoors, 700 Hz in full shelter (rain on a roof), a log blend between.</summary>
    public static float RainCutoffHz(float shelter) => 20000f * MathF.Pow(700f / 20000f, Math.Clamp(shelter, 0f, 1f));

    static float Fraction(float distanceM) => Math.Clamp((distanceM - NearM) / (FarM - NearM), 0f, 1f);
    static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
