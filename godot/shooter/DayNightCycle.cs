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
    public float MinAmbient { get; set; } = 0.35f;

    /// <summary>How much of the day's ambient light is left in a bunker room (0.2 turns 0.35 into about 0.07).</summary>
    public const float IndoorAmbient = 0.2f;

    /// <summary>0 outdoors, 1 inside a bunker room: set each frame by <see cref="BunkerLights"/>; turns the ambient light down.</summary>
    public float Indoor { get; set; }

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
        var l = DayCurve.Sample(hour, MinAmbient);
        Lighting = l;
        NightFactor = l.NightFactor;

        var brightness = GameSettings.Current.Brightness;

        _sun.RotationDegrees = new Vector3(-l.SunElevationDeg, SunAzimuthDeg, 0);
        _sun.LightColor = Col(l.SunColour);
        _sun.LightEnergy = l.SunEnergy;
        _sun.ShadowEnabled = l.SunEnergy > 0.01f; // shadows fade with the sun and are gone at night

        _moon.RotationDegrees = new Vector3(-l.MoonElevationDeg, SunAzimuthDeg + 180f, 0);
        _moon.LightColor = Col(l.MoonColour);
        _moon.LightEnergy = l.MoonEnergy;

        _env.AmbientLightColor = Col(l.AmbientColour);
        _env.AmbientLightEnergy = l.AmbientEnergy * Mathf.Lerp(1f, IndoorAmbient, Indoor);
        _env.TonemapExposure = l.Exposure * brightness;
        _env.FogDensity = l.FogDensity;
        _env.FogLightColor = Col(l.SkyHorizon);

        _sky.SetShaderParameter("sky_top", Col(l.SkyTop));
        _sky.SetShaderParameter("sky_horizon", Col(l.SkyHorizon));
        _sky.SetShaderParameter("sun_dir", _sun.Basis.Z);
        _sky.SetShaderParameter("sun_colour", Col(l.SunColour));
        _sky.SetShaderParameter("moon_dir", _moon.Basis.Z);
        _sky.SetShaderParameter("moon_colour", Col(l.MoonColour));
        _sky.SetShaderParameter("moon_visibility", Mathf.Clamp(l.MoonEnergy / DayCurve.MoonPeakEnergy, 0f, 1f));
        _sky.SetShaderParameter("star_visibility", l.StarVisibility);
    }

    static Color Col(System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);
}
