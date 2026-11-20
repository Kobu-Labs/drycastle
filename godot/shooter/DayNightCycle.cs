using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// Client day/night. Owns the sun and moon lights, the world environment (sky, ambient, fog, exposure) and the
/// sky shader (stars, sun and moon discs). The hour comes from the server clock (welcome plus snapshot ticks);
/// offline it runs its own clock from the local environment.json. The curve lives in <see cref="DayCurve"/>.
/// </summary>
public partial class DayNightCycle : Node3D
{
    /// <summary>Azimuth of the sun's arc (degrees, Y rotation). The moon is on the opposite side.</summary>
    public const float SunAzimuthDeg = 30f;
    public const string SkyShaderPath = "res://shaders/sky_daynight.gdshader";

    /// <summary>The live cycle in the scene (null before it enters the tree or after it leaves).</summary>
    public static DayNightCycle? Current { get; private set; }

    /// <summary>
    /// The night service: 0 at full day, 1 at full night. Street lamps, headlights and window glow read this every
    /// frame; it changes smoothly through dawn and dusk.
    /// </summary>
    public float NightFactor { get; private set; }

    /// <summary>The lighting for the current hour (sun, moon, ambient, sky, fog, exposure).</summary>
    public DayLighting Lighting { get; private set; }

    /// <summary>Local clock used when no server clock is attached (offline view).</summary>
    public DayClock LocalClock { get; set; } = new();
    public float MinAmbient { get; set; } = 0.10f;

    /// <summary>TEST (/night, this client only): the moon's peak light, and the night exposure. Reset by <see cref="ResetNightTuning"/>.</summary>
    public float MoonPeak { get; set; } = DayCurve.MoonPeakEnergy;
    public float NightExposure { get; set; } = DayCurve.ExposureNight;
    float _shippedAmbient = -1f;

    public void ResetNightTuning()
    {
        MoonPeak = DayCurve.MoonPeakEnergy;
        NightExposure = DayCurve.ExposureNight;
        if (_shippedAmbient >= 0) MinAmbient = _shippedAmbient;
    }

    /// <summary>TEST (/night): sets any of the three (null keeps it). The ambient floor the map shipped with is remembered for reset.</summary>
    public void TuneNight(float? moon, float? ambient, float? exposure)
    {
        if (_shippedAmbient < 0) _shippedAmbient = MinAmbient;
        if (moon is { } m) MoonPeak = m;
        if (ambient is { } a) MinAmbient = a;
        if (exposure is { } e) NightExposure = e;
    }

    /// <summary>How much of the day's ambient light is left in a bunker room (0.2 turns 0.35 into about 0.07).</summary>
    public const float IndoorAmbient = 0.2f;

    /// <summary>0 outdoors, 1 inside a bunker room: set each frame by <see cref="BunkerLights"/>; turns the ambient light down.</summary>
    public float Indoor { get; set; }

    /// <summary>0..1: the same turn-down of the ambient light for a room that is not a bunker (the mechanic garage while its lights are off, <see cref="MechanicGarage"/>); the darker of the two counts.</summary>
    public float IndoorExtra { get; set; }

    /// <summary>0..1: the world goes dark for this client (the church while the pastor turns and the demon lives): the sun, the moon, the ambient light and the sky are turned down to almost nothing, so only fires light the scene. Set every frame by the church's decor.</summary>
    public float Gloom { get; set; }
    const float GloomFloor = 0.03f;

    /// <summary>Ambient energy a normal shooting star adds at its peak (a fireball adds 2.5x).</summary>
    const float MeteorAmbient = 0.03f;

    /// <summary>
    /// Glow (bloom) is for lamps only. The threshold is on the scene's linear HDR value before exposure: a white surface in full sun
    /// peaks at about 1.1 (sun) + 0.7 (ambient) = 1.8 and the sun disc at 1.4, so nothing lit by day or the sky reaches 2; lit lamp lenses
    /// are driven to 6 and up (<see cref="CarLights"/>). Full bloom from threshold + ramp. Exposure (0.5..1.5 x 1.0..0.9) comes after.
    /// </summary>
    public const float GlowThreshold = 2f, GlowRamp = 1f;

    readonly DirectionalLight3D _sun = new() { ShadowEnabled = true };
    readonly DirectionalLight3D _moon = new() { ShadowEnabled = false };
    readonly WorldEnvironment _worldEnv = new();
    readonly Godot.Environment _env = new();
    readonly ShaderMaterial _sky = new();
    DayClock? _serverClock;
    double _server; // the server's time, advanced by delta between snapshots
    bool _synced;
    double _elapsed; // seconds of server time (or local time offline)

    public override void _Ready()
    {
        Current = this;
        AddChild(_sun);
        AddChild(_moon);
        _sky.Shader = GD.Load<Shader>(SkyShaderPath);
        _env.BackgroundMode = Godot.Environment.BGMode.Sky;
        _env.Sky = new Sky { SkyMaterial = _sky };
        _env.AmbientLightSource = Godot.Environment.AmbientSource.Color;
        _env.FogEnabled = true;
        _env.FogSkyAffect = 0f; // at 1 (Godot's default) the fog paints over the whole sky: no sun, moon or stars. The sky's horizon is the fog colour already
        // a small tight halo: only the first mips (levels 1-3), additive, nothing below the threshold
        _env.GlowEnabled = true;
        _env.GlowBlendMode = Godot.Environment.GlowBlendModeEnum.Additive;
        _env.GlowHdrThreshold = GlowThreshold;
        _env.GlowHdrScale = GlowRamp;
        _env.GlowHdrLuminanceCap = 8f;
        _env.GlowIntensity = 0.6f;
        _env.GlowStrength = 1f;
        _env.GlowBloom = 0f;
        for (var i = 0; i < 7; i++) _env.SetGlowLevel(i, i switch { 1 => 1f, 2 => 1f, 3 => 0.5f, _ => 0f });
        _worldEnv.Environment = _env;
        AddChild(_worldEnv);
        Apply(LocalClock.HourAt(0));
    }

    public override void _ExitTree()
    {
        if (Current == this) Current = null;
    }

    /// <summary>Networked: the server's clock arrived (welcome). The tick sets where in the day we are.</summary>
    public void SetServerClock(DayClock clock, uint tick)
    {
        _serverClock = clock;
        SetServerTick(tick);
    }

    /// <summary>Networked: called on every snapshot with the server tick.</summary>
    public void SetServerTick(uint tick)
    {
        _server = tick / (double)Wire.TickRate;
        if (!_synced || Math.Abs(_server - _elapsed) > 2) { _elapsed = _server; _synced = true; } // first snapshot or a jump: snap
    }

    public override void _Process(double delta)
    {
        if (_serverClock == null) _elapsed += delta;
        else
        {
            // between snapshots the clock keeps running, and eases onto the server's tick instead of jumping to it
            _server += delta;
            _elapsed += delta + (_server - _elapsed - delta) * Math.Min(1.0, delta * 4.0);
        }
        var clock = _serverClock ?? LocalClock;
        Apply(clock.HourAt(_elapsed));
    }

    void Apply(double hour)
    {
        var l = DayCurve.Sample(hour, MinAmbient, MoonPeak, NightExposure);
        Lighting = l;
        NightFactor = l.NightFactor;

        var brightness = GameSettings.Current.Brightness;

        _sun.RotationDegrees = new Vector3(-l.SunElevationDeg, SunAzimuthDeg, 0);
        _sun.LightColor = Col(l.SunColour);
        var gloom = Mathf.Lerp(1f, GloomFloor, Mathf.Clamp(Gloom, 0f, 1f));
        _sun.LightEnergy = l.SunEnergy * gloom;
        _sun.ShadowEnabled = l.SunEnergy > 0.01f; // shadows fade with the sun and are gone at night
        // Grazing light (dawn/dusk) stretches each shadow texel over a long strip of ground: self-shadow stripes (acne).
        // Bias grows as the sun drops; at 30 deg and up it is Godot's default.
        var low = 1f - Mathf.Clamp(l.SunElevationDeg / 30f, 0f, 1f);
        _sun.ShadowNormalBias = Mathf.Lerp(2f, 5f, low);
        _sun.ShadowBias = Mathf.Lerp(0.1f, 0.3f, low);

        _moon.RotationDegrees = new Vector3(-l.MoonElevationDeg, SunAzimuthDeg + 180f, 0);
        _moon.LightColor = Col(l.MoonColour);
        _moon.LightEnergy = l.MoonEnergy * gloom;

        _env.AmbientLightColor = Col(l.AmbientColour);
        // shooting stars: only once it is already dark; each one lights the ground a little
        var meteors = Meteors.NightGate(l.NightFactor) > 0f ? Meteors.Active(Time.GetTicksMsec() / 1000.0) : new List<Meteor>();
        var gate = Meteors.NightGate(l.NightFactor);
        var meteorGlow = Meteors.Glow(meteors) * gate * MeteorAmbient;
        _env.AmbientLightEnergy = (l.AmbientEnergy * Mathf.Lerp(1f, IndoorAmbient, Mathf.Max(Indoor, IndoorExtra)) + meteorGlow * (1f - Mathf.Max(Indoor, IndoorExtra))) * gloom;
        _env.BackgroundEnergyMultiplier = Mathf.Lerp(1f, 0.12f, Mathf.Clamp(Gloom, 0f, 1f));
        _env.TonemapExposure = l.Exposure * brightness;
        _env.FogDensity = l.FogDensity;
        _env.FogLightColor = Col(l.SkyHorizon);

        _sky.SetShaderParameter("sky_top", Col(l.SkyTop));
        _sky.SetShaderParameter("sky_horizon", Col(l.SkyHorizon));
        _sky.SetShaderParameter("sun_dir", _sun.Basis.Z);
        _sky.SetShaderParameter("sun_colour", Col(l.SunColour));
        _sky.SetShaderParameter("moon_dir", _moon.Basis.Z);
        _sky.SetShaderParameter("moon_colour", Col(l.MoonColour));
        _sky.SetShaderParameter("moon_visibility", l.NightFactor);
        _sky.SetShaderParameter("star_visibility", l.StarVisibility);
        // the tonemap exposure darkens the sky with the world: the things in the sky are lifted back by it, the sky colour stays with the fog
        _sky.SetShaderParameter("lift", 1f / Mathf.Max(l.Exposure, 0.05f));
        var a = new Vector4[Meteors.MaxActive];
        var b = new Vector4[Meteors.MaxActive];
        for (var i = 0; i < meteors.Count; i++)
        {
            var m = meteors[i];
            a[i] = new Vector4(m.From.X, m.From.Y, m.From.Z, m.Progress);
            b[i] = new Vector4(m.To.X, m.To.Y, m.To.Z, m.Brightness * gate * Mathf.Lerp(1f, GloomFloor, Mathf.Clamp(Gloom, 0f, 1f)));
        }
        _sky.SetShaderParameter("meteor_a", a);
        _sky.SetShaderParameter("meteor_b", b);
    }

    static Color Col(System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);
}
