using Godot;
using Drycastle.Shooter.Audio;
using Drycastle.Shooter.Scenery;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Scenery;

namespace Drycastle.Shooter.Net.Client.Village;

/// <summary>
/// The weather on the client (Weather, #230, GLOSSARY "Thunderstorm"): the server's phase (<see cref="EventKind.WeatherState"/>) sets what the sky fades toward (<see cref="DayNightCycle.SetStorm"/>); a strike
/// (<see cref="EventKind.Lightning"/>) flashes the sky at once (stronger when near) and plays the thunder after distance / 343 s, a close crack and its roll under ~400 m, a low rumble farther, quieter the farther it was.
/// While the sky is dark (<see cref="DayNightCycle.Storm"/>) rain falls round the local camera (one GPU emitter following it, its amount scaled by the storm; none in a bunker or the garage) and the rain loop plays on its own voice:
/// level with the storm, lower and muffled (a low-pass on its own bus) when sheltered. The numbers are <see cref="StormSky"/>'s and environment.json <c>storm</c>.
/// </summary>
public sealed class WeatherClient
{
    const string RainSound = "weather.rain", RainBus = "WeatherRain";
    /// <summary>The box the rain falls in round the camera: width and depth, how high above the camera it starts, and how far it falls.</summary>
    const float BoxWidth = 40f, BoxHeight = 14f, FallMetres = 26f, DropSpeed = 24f;

    readonly ClientContext _ctx;
    readonly StormLook _look;
    GpuParticles3D? _rain;
    AudioStreamPlayer? _voice;
    AudioEffectLowPassFilter? _lowPass;
    bool _voiceTried;

    public WeatherClient(ClientContext ctx, StormLook look) { _ctx = ctx; _look = look; }

    public void OnEvent(EventKind kind, byte[] payload)
    {
        if (kind == EventKind.WeatherState && WeatherCodec.TryDecodeState(payload, out var phase))
            DayNightCycle.Current?.SetStorm(phase == WeatherPhase.Storm);
        else if (kind == EventKind.Lightning && WeatherCodec.TryDecodeStrike(payload, out var strike)) Strike(strike);
    }

    void Strike(LightningStrike strike)
    {
        DayNightCycle.Current?.Flash(StormSky.FlashStrength(strike.DistanceM));
        var db = StormSky.ThunderDb(strike.DistanceM);
        var sound = StormSky.ThunderSound(strike.DistanceM);
        var tree = _ctx.Root.GetTree();
        if (tree == null) return;
        tree.CreateTimer(StormSky.ThunderDelaySeconds(strike.DistanceM)).Timeout += () => Sfx.Play2D(sound, db);
    }

    /// <summary>Every frame: the rain round the camera and its sound, from how dark the sky is and where the player is.</summary>
    public void Render()
    {
        var cycle = DayNightCycle.Current;
        var storm = cycle?.Storm ?? 0f;
        var indoor = cycle == null ? 0f : Mathf.Max(cycle.Indoor, cycle.IndoorExtra);
        if (storm < 0.01f && _rain == null && _voice == null) return;
        UpdateRainSound(storm, Mathf.Max(indoor, _ctx.Driving ? 0.5f : 0f));
        UpdateRainFall(storm, indoor);
    }

    void UpdateRainSound(float storm, float shelter)
    {
        var db = StormSky.RainDb(storm, shelter, _look.RainDb, _look.RainIndoorDb);
        if (_voice == null)
        {
            if (db <= StormSky.RainSilentDb || _voiceTried || !Sfx.Enabled) return;
            _voiceTried = true; // an unusable voice (audio off, file missing) is not asked for every frame
            _voice = Sfx.OwnVoice(RainSound);
            if (_voice == null) return;
            _voice.Bus = RainBusName();
        }
        if (_lowPass != null) _lowPass.CutoffHz = StormSky.RainCutoffHz(shelter);
        if (db <= StormSky.RainSilentDb) { if (_voice.Playing) _voice.Stop(); return; }
        _voice.VolumeDb = Sfx.BaseDb(RainSound) + db;
        if (!_voice.Playing) _voice.Play();
    }

    /// <summary>A bus that sends to SFX with a low-pass: the rain heard through a roof. Falls back to SFX when it cannot be made.</summary>
    string RainBusName()
    {
        var i = AudioServer.GetBusIndex(RainBus);
        if (i < 0)
        {
            AudioServer.AddBus();
            i = AudioServer.BusCount - 1;
            AudioServer.SetBusName(i, RainBus);
            AudioServer.SetBusSend(i, "SFX");
            AudioServer.AddBusEffect(i, new AudioEffectLowPassFilter { CutoffHz = 20000f, Db = AudioEffectFilter.FilterDB.Filter12Db });
        }
        _lowPass = AudioServer.GetBusEffect(i, 0) as AudioEffectLowPassFilter;
        return RainBus;
    }

    void UpdateRainFall(float storm, float indoor)
    {
        var on = storm >= 0.01f && indoor < 0.5f && _look.RainAmount > 0;
        if (_rain == null)
        {
            if (!on || !Sfx.Enabled) return; // headless runs draw nothing
            _rain = MakeRain();
            _ctx.Owned.AddChild(_rain);
        }
        _rain.Emitting = on;
        if (!on) return;
        _rain.AmountRatio = Mathf.Clamp(storm, 0.02f, 1f);
        if (_ctx.Root.GetViewport().GetCamera3D() is { } cam) _rain.GlobalPosition = cam.GlobalPosition + new Vector3(0, BoxHeight, 0);
    }

    /// <summary>One GPU emitter of thin vertical streaks: a flat box over the camera, the drops falling straight down. Drawn in world space, so the camera moves through the rain.</summary>
    GpuParticles3D MakeRain()
    {
        var material = new StandardMaterial3D
        {
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha, BillboardMode = BaseMaterial3D.BillboardModeEnum.FixedY, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            AlbedoColor = new Color(0.85f, 0.9f, 1f, 0.45f), Roughness = 1f,
            EmissionEnabled = true, Emission = new Color(0.5f, 0.55f, 0.65f), EmissionEnergyMultiplier = 0.25f, // still a faint grey streak at night
        };
        var drops = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box, EmissionBoxExtents = new Vector3(BoxWidth / 2f, 0.5f, BoxWidth / 2f),
            Direction = Vector3.Down, Spread = 1.5f, Gravity = Vector3.Zero, InitialVelocityMin = DropSpeed * 0.9f, InitialVelocityMax = DropSpeed * 1.1f,
        };
        return new GpuParticles3D
        {
            Name = "StormRain", TopLevel = true, Emitting = false, LocalCoords = false, Amount = _look.RainAmount, Lifetime = FallMetres / DropSpeed, Preprocess = FallMetres / DropSpeed,
            ProcessMaterial = drops, DrawPass1 = new QuadMesh { Size = new Vector2(0.012f, 0.7f), Material = material },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            VisibilityAabb = new Aabb(new Vector3(-BoxWidth, -FallMetres - 4f, -BoxWidth), new Vector3(BoxWidth * 2f, FallMetres + 8f, BoxWidth * 2f)),
        };
    }
}
