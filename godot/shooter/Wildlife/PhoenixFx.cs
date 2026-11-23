using Godot;
using Drycastle.Shooter.Audio;
using Drycastle.Shooter.Effects;
using Drycastle.Shooter.Menu;
using Drycastle.Shooter.Weapons;
using Drycastle.ShooterCore.Effects;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Wildlife;

namespace Drycastle.Shooter.Wildlife;

/// <summary>
/// The looks of the Phoenix (animals.json <c>"fx": "phoenix"</c>), pure client, nothing on the wire: a warm flickering light on the bird, a glow with the bird's direction (a tail of fire behind it and a bar of fire along its wings, sized by the camera distance so the night sky shows it from across the map) and CPU particles in world space, by phase
/// (<see cref="AnimalState"/>, GLOSSARY "Phoenix"). Flying: a thin trail of embers. Falling: a heavy streak of flame behind it and smoke, the light swelling. Crashed: a ring of fire on the ground round it,
/// rising embers, smoke and a flickering light. Burning (the second hit): a tall column of flame, a flash and a bright light that die down over the burn's length. Dead: the body is ash, a few embers
/// rise for a while, and a small glow lingers. Child of the animal's node (origin = the body centre while it flies, the ground when it sits), built once per animal and freed with it; <see cref="Tick"/> runs
/// after the animal is placed each frame. Meshes, ramps and curves are shared (<see cref="RocketFx.C"/>'s smoke and the rest are built once); every emitter is hidden until it runs
/// (<see cref="ParticleFx"/>) and none runs beyond <see cref="EffectCull.EffectRange"/> of the camera; nothing allocates per frame.
/// </summary>
public sealed partial class PhoenixFx : Node3D
{
    /// <summary>The id of this visual set in animals.json.</summary>
    public const string Id = "phoenix";

    /// <summary>The flame's own colour (the light and the ash's glow).</summary>
    static readonly Color Flame = new(1f, 0.45f, 0.12f);

    /// <summary>Seconds the embers of a dead Phoenix keep rising, and the seconds the ash's glow takes to sink to its lowest.</summary>
    const float DeadEmberSeconds = 4f, AshFadeSeconds = 25f;
    /// <summary>How far (m) a crash-site warning light is built from the camera.</summary>
    const float WarningRange = 400f;

    /// <summary>The base size range of the puffs of the emitters whose size changes with the phase.</summary>
    static readonly (float Min, float Max) ColumnSize = (1.5f, 3.2f), StreakSize = (1.1f, 2.1f), RiseSize = (0.6f, 1.3f);

    static SphereMesh? _ember, _flame;
    static Gradient? _emberRamp, _flameRamp, _smokeRamp;
    static Curve? _shrink, _grow;

    CpuParticles3D? _embers, _streak, _smoke, _ring, _rise, _column;
    OmniLight3D? _light;
    MeshInstance3D? _halo, _tail, _wings;
    StandardMaterial3D? _haloMat;
    ShaderMaterial? _tailMat, _wingMat;
    static Shader? _glowShader;
    static readonly StringName PAxis = "axis", PStart = "start", PLen = "len", PWidth = "width", PSym = "sym", PPower = "power", PMotion = "motion";
    readonly List<MeshInstance3D> _bodies = new();
    Change<AnimalState> _phase;
    float _clock, _t, _burnSeconds = 6f;
    Countdown _flash;

    /// <summary>Dresses the Phoenix <paramref name="animal"/> (its node) whose model <paramref name="model"/> is loaded (may be null); null when there is nothing to draw (headless). <paramref name="burnSeconds"/> is the flame's length (wildlife.json).</summary>
    public static PhoenixFx? Attach(Node3D animal, Node3D? model, float burnSeconds)
    {
        if (!ShotFx.Enabled) return null;
        var fx = new PhoenixFx { Name = "PhoenixFx", _burnSeconds = MathF.Max(burnSeconds, 0.5f) };
        animal.AddChild(fx);
        fx.Build(model);
        return fx;
    }

    /// <summary>Additive and unshaded: the particle material of the kit (<see cref="RocketFx.Part"/>) that adds its light to what is behind it, so flames and embers glow on a night sky.</summary>
    static SphereMesh Glow(float radius)
    {
        var m = RocketFx.Part(new Color(1, 1, 1), true);
        m.BlendMode = BaseMaterial3D.BlendModeEnum.Add;
        m.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        return new SphereMesh { Radius = radius, Height = radius * 2, RadialSegments = 8, Rings = 4, Material = m };
    }

    static void EnsureShared()
    {
        _ember ??= Glow(0.12f);
        _flame ??= Glow(0.35f);
        _emberRamp ??= RocketFx.Ramp((0f, new Color(1f, 0.95f, 0.6f, 1f)), (0.5f, new Color(1f, 0.5f, 0.12f, 0.9f)), (1f, new Color(0.7f, 0.12f, 0.03f, 0f)));
        _flameRamp ??= RocketFx.Ramp((0f, new Color(1f, 0.9f, 0.55f, 0.9f)), (0.3f, new Color(1f, 0.5f, 0.1f, 0.8f)), (0.7f, new Color(0.8f, 0.14f, 0.03f, 0.45f)), (1f, new Color(0.3f, 0.04f, 0.02f, 0f)));
        _smokeRamp ??= RocketFx.Ramp((0f, new Color(0.45f, 0.2f, 0.1f, 0.55f)), (0.35f, new Color(0.2f, 0.15f, 0.14f, 0.55f)), (1f, new Color(0.08f, 0.08f, 0.09f, 0f))); // lit by the fire at first, then soot
        _shrink ??= RocketFx.Curve2(1f, 0.1f);
        _grow ??= RocketFx.Curve2(0.6f, 2.2f);
    }

    CpuParticles3D Emitter(string name, int amount, double lifetime, Mesh mesh, Gradient ramp, Curve scale, Vector3 direction, float spread, float vMin, float vMax, Vector3 gravity, float sMin, float sMax)
    {
        var p = new CpuParticles3D
        {
            Name = name, Visible = false, Emitting = false, Mesh = mesh, ColorRamp = ramp, ScaleAmountCurve = scale, Amount = amount, Lifetime = lifetime, LocalCoords = false, Randomness = 0.5f,
            Direction = direction, Spread = spread, InitialVelocityMin = vMin, InitialVelocityMax = vMax, Gravity = gravity, ScaleAmountMin = sMin, ScaleAmountMax = sMax,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(p);
        return p;
    }

    void Build(Node3D? model)
    {
        EnsureShared();
        // flying: a thin shower of embers off the wings, sinking slowly behind it
        _embers = Emitter("Embers", 20, 1.8, _ember!, _emberRamp!, _shrink!, Vector3.Down, 180, 0.05f, 0.5f, new Vector3(0, -0.6f, 0), 0.5f, 1.2f);
        _embers.EmissionShape = CpuParticles3D.EmissionShapeEnum.Box; _embers.EmissionBoxExtents = new Vector3(1.4f, 0.2f, 0.9f);
        // falling: a streak of flame torn off behind it (+Z is behind the model), thick smoke after it
        _streak = Emitter("Streak", 56, 1.1, _flame!, _flameRamp!, _shrink!, Vector3.Back, 18, 1.5f, 4f, new Vector3(0, 1.0f, 0), StreakSize.Min, StreakSize.Max);
        _streak.EmissionShape = CpuParticles3D.EmissionShapeEnum.Sphere; _streak.EmissionSphereRadius = 0.7f;
        _smoke = Emitter("Smoke", 24, 3.2, RocketFx.C.Smoke, _smokeRamp!, _grow!, Vector3.Up, 35, 0.4f, 1.4f, new Vector3(0, 0.5f, 0), 0.8f, 1.6f);
        _smoke.EmissionShape = CpuParticles3D.EmissionShapeEnum.Sphere; _smoke.EmissionSphereRadius = 0.6f;
        // crashed: fire on the ground in a ring round the bird (its 6 m clear circle), embers rising from it
        _ring = Emitter("Ring", 30, 0.9, _flame!, _flameRamp!, _shrink!, Vector3.Up, 10, 0.5f, 1.5f, new Vector3(0, 1.6f, 0), 0.5f, 1.0f);
        _ring.EmissionShape = CpuParticles3D.EmissionShapeEnum.Ring; _ring.EmissionRingAxis = Vector3.Up; _ring.EmissionRingRadius = 2.6f; _ring.EmissionRingInnerRadius = 1.8f; _ring.EmissionRingHeight = 0.2f;
        _ring.Position = new Vector3(0, 0.1f, 0);
        _rise = Emitter("Rise", 20, 2.6, _ember!, _emberRamp!, _shrink!, Vector3.Up, 25, 1.5f, 3.5f, new Vector3(0, 0.6f, 0), RiseSize.Min, RiseSize.Max);
        _rise.EmissionShape = CpuParticles3D.EmissionShapeEnum.Sphere; _rise.EmissionSphereRadius = 1.8f; _rise.Position = new Vector3(0, 0.3f, 0);
        // burning: the big flame
        _column = Emitter("Column", 70, 1.4, _flame!, _flameRamp!, _shrink!, Vector3.Up, 12, 4f, 9f, new Vector3(0, 2f, 0), ColumnSize.Min, ColumnSize.Max);
        _column.EmissionShape = CpuParticles3D.EmissionShapeEnum.Sphere; _column.EmissionSphereRadius = 1.3f; _column.Position = new Vector3(0, 0.5f, 0);
        _light = new OmniLight3D { Name = "Light", LightColor = Flame, LightEnergy = 0f, OmniRange = 12f, ShadowEnabled = false, Visible = false };
        AddChild(_light);
        // a soft additive glow sprite round the bird, HDR so it blooms (ADR 0030): a 3 m bird at 150 m is a few pixels, this is what the night sky shows. Its size follows the camera distance (see Tick).
        _haloMat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, BlendMode = BaseMaterial3D.BlendModeEnum.Add, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled, AlbedoColor = new Color(3.2f, 1.2f, 0.3f, 1f), NoDepthTest = false, DisableReceiveShadows = true, DisableFog = true,
            AlbedoTexture = new GradientTexture2D { Fill = GradientTexture2D.FillEnum.Radial, FillFrom = new Vector2(0.5f, 0.5f), FillTo = new Vector2(1f, 0.5f), Width = 64, Height = 64, Gradient = RocketFx.Ramp((0f, new Color(1, 1, 1, 1f)), (0.25f, new Color(1, 1, 1, 0.45f)), (1f, new Color(1, 1, 1, 0f))) },
        };
        _halo = new MeshInstance3D { Name = "Halo", Mesh = new QuadMesh { Size = Vector2.One, Material = _haloMat }, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Visible = false };
        AddChild(_halo);
        // the glow's direction (shaders/phoenix_glow.gdshader): a tail of fire behind it (+Z) and a bar of fire along the wings (X), each turned about its own axis to the camera. They bank and dive with the bird
        _glowShader ??= GD.Load<Shader>("res://shaders/phoenix_glow.gdshader");
        (_tail, _tailMat) = Streak("Tail", Vector3.Back, 0f);
        (_wings, _wingMat) = Streak("Wings", Vector3.Right, 1f);
        if (model != null)
            foreach (var n in model.FindChildren("*", "MeshInstance3D", true, false))
                if (n is MeshInstance3D mi) _bodies.Add(mi);
    }

    (MeshInstance3D, ShaderMaterial) Streak(string name, Vector3 axis, float sym)
    {
        var mat = new ShaderMaterial { Shader = _glowShader };
        mat.SetShaderParameter(PAxis, axis);
        mat.SetShaderParameter(PSym, sym);
        // the shader moves the quad far from the node: the margin keeps it drawn while the bird itself is off the screen
        var mi = new MeshInstance3D { Name = name, Mesh = new QuadMesh { Size = Vector2.One, Material = mat }, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, ExtraCullMargin = 400f, Visible = false };
        AddChild(mi);
        return (mi, mat);
    }

    /// <summary>The first hit landed (the fall begins): a flash of light on the bird.</summary>
    public void Hit() => _flash.Start(0.4f);

    /// <summary>One frame, after the animal is placed: <paramref name="state"/> is its phase. Emitters are switched only when their wish changes; far from the camera all of them are off.</summary>
    public void Tick(float dt, AnimalState state)
    {
        if (_light == null || _embers == null || _streak == null || _smoke == null || _ring == null || _rise == null || _column == null) return;
        if (_phase.Update(state)) { _t = 0; if (state == AnimalState.Dead) Ash(); }
        _t += dt; _clock += dt;
        _flash.Tick(dt);
        var near = Sfx.ListenerDistance(GlobalPosition) <= EffectCull.EffectRange;
        if (_halo != null && _tail != null && _wings != null)
        {
            var sky = state is AnimalState.Flying or AnimalState.Falling;
            var glows = sky || state is AnimalState.Crashed or AnimalState.Burning;
            var cam = GetViewport()?.GetCamera3D();
            _halo.Visible = glows && cam != null;
            _tail.Visible = _wings.Visible = sky && cam != null;
            if (_halo.Visible)
            {
                var d = cam!.GlobalPosition.DistanceTo(GlobalPosition);
                var still = GameSettings.Current.ReduceMotion;
                var flick = still ? 1f : 0.85f + 0.15f * MathF.Sin(_clock * 7f) * MathF.Sin(_clock * 3.1f + 1f);
                // in the sky the round glow is only the hot core (the tail and the wings give the shape); on the ground it is the fire's glow. Sizes follow the distance: the bird is a few pixels from afar, the glow never is
                var size = sky ? Math.Clamp(d * 0.03f, 5f, 60f) : Math.Clamp(d * 0.06f, 7f, 45f) * (state == AnimalState.Burning ? 1.6f : 1f);
                _halo.Scale = new Vector3(size, size, 1f);
                _halo.Position = Vector3.Up * (sky ? 0f : 0.8f);
                _haloMat!.AlbedoColor = new Color(3.2f * flick, 1.2f * flick, 0.3f * flick, state == AnimalState.Burning ? 1f : 0.85f);
                if (sky)
                {
                    var dive = state == AnimalState.Falling;
                    var grow = dive ? Math.Clamp(_t / 1.2f, 0f, 1f) : 0f; // the fall tears a longer, hotter tail off it
                    var tail = MathF.Max(34f + 30f * grow, d * (0.09f + 0.06f * grow));
                    _tailMat!.SetShaderParameter(PStart, -1.5f);
                    _tailMat.SetShaderParameter(PLen, tail);
                    _tailMat.SetShaderParameter(PWidth, MathF.Max(6f + 3f * grow, d * 0.022f));
                    _tailMat.SetShaderParameter(PPower, flick * (1f + 0.8f * grow));
                    _tailMat.SetShaderParameter(PMotion, still ? 0f : 1f);
                    var span = MathF.Max(13f, d * 0.05f);
                    _wingMat!.SetShaderParameter(PStart, -span * 0.5f);
                    _wingMat.SetShaderParameter(PLen, span);
                    _wingMat.SetShaderParameter(PWidth, MathF.Max(4.5f, d * 0.016f));
                    _wingMat.SetShaderParameter(PPower, flick * 0.8f);
                    _wingMat.SetShaderParameter(PMotion, still ? 0f : 1f);
                }
            }
        }
        var burn = Math.Clamp(1f - _t / _burnSeconds, 0f, 1f); // 1 at the second hit, 0 when the flame is out
        var flying = state == AnimalState.Flying;
        var falling = state == AnimalState.Falling;
        var crashed = state is AnimalState.Crashed or AnimalState.Burning;
        var burning = state == AnimalState.Burning;
        Wish(_embers, near && (flying || falling));
        Wish(_streak, near && falling);
        Wish(_smoke, near && (falling || crashed));
        Wish(_ring, near && crashed);
        Wish(_rise, near && (crashed || state == AnimalState.Dead && _t < DeadEmberSeconds));
        Wish(_column, near && burning && burn > 0.02f);
        if (burning && near) Shrink(_column, 0.25f + 0.75f * burn, ColumnSize.Min, ColumnSize.Max);
        if (state == AnimalState.Dead && near) Shrink(_rise, Math.Clamp(1f - _t / DeadEmberSeconds, 0.1f, 1f), RiseSize.Min, RiseSize.Max);
        if (falling && near) Shrink(_streak, 0.6f + 0.4f * Math.Clamp(_t / 1.5f, 0f, 1f), StreakSize.Min, StreakSize.Max);
        _smoke.Position = falling ? Vector3.Zero : new Vector3(0, 0.6f, 0);

        var energy = state switch
        {
            AnimalState.Flying => 4f,
            AnimalState.Falling => 2f + 2.5f * Math.Clamp(_t / 2f, 0f, 1f),
            AnimalState.Crashed => 2.2f,
            AnimalState.Burning => 2.2f + 7f * burn * burn,
            AnimalState.Dead => 0.7f * Math.Clamp(1f - _t / AshFadeSeconds, 0.3f, 1f),
            _ => 0f,
        };
        var range = state switch { AnimalState.Flying => 40f, AnimalState.Falling => 40f, AnimalState.Burning => 12f + 10f * burn, AnimalState.Dead => 6f, _ => 14f };
        if (_flash.Running) energy += 5f * (_flash.Left / 0.4f);
        // the flicker of fire; a player who reduced motion gets a steady light
        var flicker = GameSettings.Current.ReduceMotion ? 1f : 1f + 0.16f * MathF.Sin(_clock * 13f) + 0.1f * MathF.Sin(_clock * 23.7f + 2f) + (crashed ? 0.08f * MathF.Sin(_clock * 41f) : 0f);
        var lit = near && energy > 0f;
        if (_light.Visible != lit) _light.Visible = lit;
        if (lit) { _light.LightEnergy = energy * flicker; _light.OmniRange = range; }
    }

    /// <summary>Starts or stops an emitter when its wish changed (hidden until it first runs; what is in the air dies out).</summary>
    static void Wish(CpuParticles3D p, bool on)
    {
        if (p.Emitting != on) ParticleFx.Run(p, on);
    }

    /// <summary>Scales an emitter's puffs from their base size range (no restart: only the size of the ones to come changes).</summary>
    static void Shrink(CpuParticles3D p, float k, float min, float max)
    {
        p.ScaleAmountMin = min * k;
        p.ScaleAmountMax = max * k;
    }

    /// <summary>The body goes to ash: every mesh of the model dark, a faint ember glow left that sinks (<see cref="Tick"/>).</summary>
    void Ash()
    {
        foreach (var mi in _bodies) if (GodotObject.IsInstanceValid(mi)) mi.Visible = false; // the flame consumed it: embers rise where it lay, the entity ends a few seconds later (DeadSeconds)
    }

    /// <summary>A mineral drop's minute is over: a short puff of fire where it lay (the client no longer sees the node). Not built far from the camera.</summary>
    public static void FirePuff(Node3D world, Vector3 at)
    {
        if (!ShotFx.Enabled || !GodotObject.IsInstanceValid(world) || !world.IsInsideTree() || Sfx.ListenerDistance(at) > WarningRange) return;
        EnsureShared();
        var drift = GameSettings.Current.ReduceMotion ? 0f : 1f;
        var p = new CpuParticles3D
        {
            Mesh = _flame, ColorRamp = _flameRamp, ScaleAmountCurve = _shrink, Amount = 24, Lifetime = 0.9, OneShot = true, Explosiveness = 1f, Emitting = true, LocalCoords = false,
            Direction = Vector3.Up, Spread = 60, Gravity = new Vector3(0, 1.2f * drift, 0), InitialVelocityMin = 1.5f * drift, InitialVelocityMax = 3.5f * drift, ScaleAmountMin = 0.8f, ScaleAmountMax = 1.5f,
            EmissionShape = CpuParticles3D.EmissionShapeEnum.Sphere, EmissionSphereRadius = 0.4f, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Position = at + Vector3.Up * 0.4f,
        };
        world.AddChild(p);
        var light = new OmniLight3D { LightColor = new Color(1f, 0.45f, 0.12f), LightEnergy = 3f, OmniRange = 8f, ShadowEnabled = false, Position = at + Vector3.Up * 0.8f };
        world.AddChild(light);
        var tween = light.CreateTween();
        tween.TweenProperty(light, "light_energy", 0.0, 0.8);
        tween.TweenCallback(Callable.From(light.QueueFree));
        ShotFx.Expire(world, p, 1.4f);
    }

    /// <summary>
    /// Where the fall will end (event <see cref="EventKind.PhoenixFall"/>): a red light at the crash site that swells as the bird comes down, a warning in the dark. Freed a moment after the landing, where the
    /// Crashed Phoenix's own fire takes over. Not built far from the camera.
    /// </summary>
    public static void CrashWarning(Node3D world, Vector3 site, float seconds)
    {
        if (!ShotFx.Enabled || !GodotObject.IsInstanceValid(world) || !world.IsInsideTree() || Sfx.ListenerDistance(site) > WarningRange) return;
        var light = new OmniLight3D { Name = "CrashWarning", LightColor = new Color(1f, 0.3f, 0.08f), LightEnergy = 0f, OmniRange = 18f, ShadowEnabled = false, Position = site + Vector3.Up * 1.5f };
        world.AddChild(light);
        var tween = light.CreateTween();
        tween.TweenProperty(light, "light_energy", 3.5, Math.Max(seconds, 0.2f)).SetEase(Tween.EaseType.In);
        tween.TweenInterval(0.6);
        tween.TweenCallback(Callable.From(light.QueueFree));
    }
}
