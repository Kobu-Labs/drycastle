using System.Runtime.InteropServices;
using Godot;
using Drycastle.ShooterCore;
using NVec2 = System.Numerics.Vector2;
using NVec3 = System.Numerics.Vector3;
using NVec4 = System.Numerics.Vector4;

namespace Drycastle.Shooter;

/// <summary>
/// The looks of the Unicorn (animals.json <c>"fx": "unicorn"</c>), pure client, nothing on the wire: its horn is a pearl with twinkling rainbow speckles (shader on the model's <c>Horn</c> mesh, on the dead
/// one too); a few rainbow sparkles drift up from <c>HornTip</c> while it lives (CPU particles, world space so they trail); and a rainbow ribbon hangs in the air behind it (<see cref="RainbowTrail"/>: a band
/// that turns about its path to face the camera). Child of the animal's node, built once per animal, freed with it. <see cref="Tick"/> runs after the animal is placed each frame.
/// Far from the camera the sparkles stop and the ribbon is not rebuilt (<see cref="EffectCull"/>); a ribbon with nothing left is hidden and costs nothing.
/// While the animal is in <see cref="AnimalState.Vanish"/> (immune, about to be teleported) three rainbow helixes of sparkles wind up round it, faster as the clock runs out, and its body glows; when it then
/// reappears far away (the position jump) a puff of pearl smoke and a burst of rainbow sparkles go off at the old place and at the new one.
/// </summary>
public sealed partial class UnicornFx : Node3D
{
    /// <summary>The id of this visual set in animals.json.</summary>
    public const string Id = "unicorn";

    /// <summary>Height (m) over the feet the ribbon hangs at: the unicorn's back.</summary>
    const float BodyHeight = 1.0f;
    /// <summary>Distance (m) from the camera past which the sparkles are not run (they are tiny).</summary>
    const float SparkleRange = 90f;
    const int Sparkles = 24;
    /// <summary>The vanish spiral: streams, sparkles per stream, radius and height (m) of the helix, turns over its height, turn rate (rad/s) at the start and the factor it reaches at the end, rise rate (heights/s).</summary>
    const int Streams = 3, PerStream = 14;
    const float SpiralRadius = 1.1f, SpiralRadiusEnd = 0.55f, SpiralHeight = 2.4f, SpiralTurns = 2f, SpinStart = 3f, SpinGain = 3f, RiseRate = 0.6f, SpiralSparkle = 0.26f;
    /// <summary>How long (s) after the last vanishing frame a position jump still counts as the vanish's teleport (the state and the new position arrive in one snapshot).</summary>
    const float ArriveSeconds = 1.5f;
    const float BodyGlow = 0.35f, VanishGlow = 1.6f;

    static Shader? _hornShader, _bodyShader, _trailShader, _sparkleShader;
    static ShaderMaterial? _hornMat, _trailMat, _sparkleMat;
    static Texture2D? _palette;
    static QuadMesh? _quad;
    static Gradient? _rainbow, _fade;
    static Curve? _twinkle;

    CpuParticles3D? _sparkles;
    ShaderMaterial? _body;           // this animal's own body material (its glow pulses while it vanishes)
    MultiMeshInstance3D? _spiral;    // the vanish helixes: Streams x PerStream quads, moved every frame, built on the first vanish
    float _vanishSeconds, _spin, _rise, _vanishT;
    Countdown _arrive;               // running while / shortly after a vanish: a jump then is its teleport
    NVec3 _lastFeet;
    bool _hasLast;
    readonly MeshInstance3D _ribbon = new() { CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, TopLevel = true, Visible = false, ExtraCullMargin = 100f };
    readonly ArrayMesh _mesh = new();
    readonly RainbowTrail _trail = new();
    // the ribbon's buffers: whole, reused every frame (the unused tail is degenerate triangles on vertex 0)
    readonly Vector3[] _verts = new Vector3[RainbowTrail.MaxVertices];
    readonly Color[] _colours = new Color[RainbowTrail.MaxVertices];
    readonly Vector2[] _uvs = new Vector2[RainbowTrail.MaxVertices];
    readonly int[] _indices = new int[RainbowTrail.MaxIndices];
    readonly Godot.Collections.Array _arrays = new();
    bool _meshHasSurface;

    /// <summary>Dresses the animal <paramref name="animal"/> (its node, feet at the origin) whose model <paramref name="model"/> is loaded; null when there is nothing to draw (headless).</summary>
    public static UnicornFx? Attach(Node3D animal, Node3D? model, float vanishSeconds = 2f)
    {
        if (!ShotFx.Enabled || model == null) return null;
        var fx = new UnicornFx { Name = "UnicornFx", _vanishSeconds = MathF.Max(vanishSeconds, 0.1f) };
        animal.AddChild(fx);
        fx.Build(model);
        return fx;
    }

    /// <summary>The whole horse in rainbow colours: the body mesh (every mesh but the horn) gets the body shader, fed the palette texture of its own material.</summary>
    void DressBody(Node model)
    {
        foreach (var n in model.FindChildren("*", "MeshInstance3D", true, false))
        {
            if (n is not MeshInstance3D { Mesh: { } mesh } mi || mi.Name == "Horn") continue;
            if (_body == null)
            {
                _bodyShader ??= GD.Load<Shader>("res://shaders/unicorn_body.gdshader");
                if (_palette == null && mesh.GetSurfaceCount() > 0 && mesh.SurfaceGetMaterial(0) is BaseMaterial3D { AlbedoTexture: { } palette }) _palette = palette;
                if (_palette == null) return;
                _body = new ShaderMaterial { Shader = _bodyShader }; // one per animal: its glow is its own
                _body.SetShaderParameter("palette", _palette);
            }
            mi.MaterialOverride = _body;
        }
    }

    void Build(Node3D model)
    {
        _hornShader ??= GD.Load<Shader>("res://shaders/unicorn_horn.gdshader");
        _hornMat ??= new ShaderMaterial { Shader = _hornShader };
        if (model.FindChild("Horn", true, false) is MeshInstance3D horn) horn.MaterialOverride = _hornMat;
        DressBody(model);
        if (model.FindChild("HornTip", true, false) is Node3D tip)
        {
            _sparkles = MakeSparkles();
            tip.AddChild(_sparkles);
        }
        _trailShader ??= GD.Load<Shader>("res://shaders/unicorn_trail.gdshader");
        _trailMat ??= new ShaderMaterial { Shader = _trailShader };
        _ribbon.Mesh = _mesh;
        _ribbon.MaterialOverride = _trailMat;
        AddChild(_ribbon);
        _ribbon.TopLevel = true; // world space: the ribbon stays where the unicorn was
        _ribbon.GlobalTransform = Transform3D.Identity;
        _arrays.Resize((int)Mesh.ArrayType.Max);
    }

    /// <summary>The sparkle quad, rainbow, fade and twinkle every sparkle of the species shares, made on first use.</summary>
    static void EnsureShared()
    {
        _sparkleShader ??= GD.Load<Shader>("res://shaders/unicorn_sparkle.gdshader");
        _sparkleMat ??= new ShaderMaterial { Shader = _sparkleShader };
        _quad ??= new QuadMesh { Size = Vector2.One, Material = _sparkleMat };
        _rainbow ??= RocketFx.Ramp(Enumerable.Range(0, RainbowTrail.Stripes).Select(i =>
        {
            var c = RainbowTrail.HueRgb(RainbowTrail.RowHue(i)); // the same seven colours as the trail
            return (i / (float)(RainbowTrail.Stripes - 1), new Color(c.X, c.Y, c.Z));
        }).ToArray());
        _fade ??= RocketFx.Ramp((0f, new Color(1, 1, 1, 0)), (0.15f, new Color(1, 1, 1, 1)), (0.7f, new Color(1, 1, 1, 0.8f)), (1f, new Color(1, 1, 1, 0)));
        _twinkle ??= TwinkleCurve();
    }

    CpuParticles3D MakeSparkles()
    {
        EnsureShared();
        var calm = GameSettings.Current.ReduceMotion ? 0f : 1f;
        return new CpuParticles3D
        {
            Name = "Sparkles", Visible = false, Emitting = false, Mesh = _quad, Amount = Sparkles, Lifetime = 1.8, Explosiveness = 0f, Randomness = 0.5f, LocalCoords = false,
            EmissionShape = CpuParticles3D.EmissionShapeEnum.Sphere, EmissionSphereRadius = 0.12f, Direction = Vector3.Up, Spread = 180f,
            InitialVelocityMin = 0.05f * calm, InitialVelocityMax = 0.35f * calm, Gravity = new Vector3(0, 0.3f * calm, 0),
            TangentialAccelMin = -0.6f * calm, TangentialAccelMax = 0.6f * calm, DampingMin = 0.2f, DampingMax = 0.5f,
            ScaleAmountMin = 0.07f, ScaleAmountMax = 0.16f, ScaleAmountCurve = _twinkle, ColorInitialRamp = _rainbow, ColorRamp = _fade,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
    }

    static Curve TwinkleCurve()
    {
        var c = new Curve();
        c.AddPoint(new Vector2(0, 0.3f)); c.AddPoint(new Vector2(0.25f, 1f)); c.AddPoint(new Vector2(0.5f, 0.55f)); c.AddPoint(new Vector2(0.75f, 1f)); c.AddPoint(new Vector2(1, 0.2f));
        return c;
    }

    /// <summary>The ring of sparkles of the vanish spiral: one quad per sparkle, coloured per instance, drawn with the horn's star sparkle.</summary>
    MultiMeshInstance3D MakeSpiral()
    {
        EnsureShared();
        var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true, Mesh = _quad, InstanceCount = Streams * PerStream };
        var inst = new MultiMeshInstance3D { Name = "VanishSpiral", Multimesh = mm, Visible = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, ExtraCullMargin = 2f };
        AddChild(inst);
        return inst;
    }

    /// <summary>
    /// One frame of the vanish: the helixes wind up round the body (a stream starts a third of a turn after the one before; each sparkle rises along it, so the whole spiral climbs), the radius draws in and the
    /// turn speeds up as <paramref name="progress"/> (0..1 of the clock) runs out; the body glows more. Sparkles are tiny at the ends of the helix and full in the middle, in the colours of the rainbow from red at the feet to violet.
    /// </summary>
    void DrawSpiral(float dt, float progress)
    {
        var calm = GameSettings.Current.ReduceMotion ? 0.3f : 1f;
        _spin += dt * (SpinStart * (1f + SpinGain * progress * progress)) * calm;
        _rise = (_rise + dt * RiseRate * (1f + progress) * calm) % 1f;
        var radius = Mathf.Lerp(SpiralRadius, SpiralRadiusEnd, progress * progress * progress);
        var arrive = Mathf.Clamp(_vanishT / 0.25f, 0f, 1f); // fades in over the first quarter second
        var mm = _spiral!.Multimesh;
        for (var s = 0; s < Streams; s++)
            for (var k = 0; k < PerStream; k++)
            {
                var f = (k / (float)PerStream + _rise) % 1f; // 0 the feet .. 1 the top
                var angle = _spin + s * Mathf.Tau / Streams + f * SpiralTurns * Mathf.Tau;
                var at = new Vector3(Mathf.Cos(angle) * radius, 0.1f + f * SpiralHeight, Mathf.Sin(angle) * radius);
                var size = SpiralSparkle * Mathf.Sin(f * Mathf.Pi) * arrive + 0.001f;
                var i = s * PerStream + k;
                mm.SetInstanceTransform(i, new Transform3D(Basis.Identity.Scaled(Vector3.One * size), at));
                var c = RainbowTrail.HueRgb(RainbowTrail.VioletHue * ((f + s / (float)Streams) % 1f));
                mm.SetInstanceColor(i, new Color(c.X, c.Y, c.Z, 1f));
            }
        _body?.SetShaderParameter("glow", BodyGlow + (VanishGlow - BodyGlow) * Mathf.Clamp(_vanishT / 0.5f, 0f, 1f) * (0.75f + 0.25f * Mathf.Sin(_vanishT * 14f)));
    }

    /// <summary>Puff of pearl smoke and a burst of rainbow sparkles at <paramref name="at"/> (the animal's feet): where it vanished and where it appeared. Nothing out of the effect range.</summary>
    void Poof(Vector3 at)
    {
        if (Sfx.ListenerDistance(at) > EffectCull.EffectRange || GetParent()?.GetParent() is not Node3D world || !world.IsInsideTree()) return;
        PetFx.Puff(world, at + Vector3.Up * 0.9f, 1.9f);
        EnsureShared();
        var calm = GameSettings.Current.ReduceMotion ? 0f : 1f;
        var burst = new CpuParticles3D
        {
            Mesh = _quad, Amount = 32, Lifetime = 1.1, OneShot = true, Explosiveness = 1f, Randomness = 0.4f, LocalCoords = false, Emitting = true,
            EmissionShape = CpuParticles3D.EmissionShapeEnum.Sphere, EmissionSphereRadius = 0.4f, Direction = Vector3.Up, Spread = 180f,
            InitialVelocityMin = 1.2f * calm, InitialVelocityMax = 4f * calm, Gravity = new Vector3(0, -0.8f * calm, 0), DampingMin = 0.5f, DampingMax = 1.2f,
            ScaleAmountMin = 0.12f, ScaleAmountMax = 0.3f, ScaleAmountCurve = _twinkle, ColorInitialRamp = _rainbow, ColorRamp = _fade,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Position = at + Vector3.Up * 1.0f,
        };
        world.AddChild(burst);
        ShotFx.Expire(world, burst, 1.5f);
    }

    /// <summary>
    /// One frame, after the animal is placed: <paramref name="alive"/> false once it is dead (no more sparkles, no more trail, what hangs fades); <paramref name="vanishing"/> while it is in
    /// <see cref="AnimalState.Vanish"/> (the spiral).
    /// </summary>
    public void Tick(float dt, bool alive, bool vanishing = false)
    {
        var feet = GlobalPosition;
        var jumped = _hasLast && NVec3.Distance(new NVec3(feet.X, feet.Y, feet.Z), _lastFeet) > RainbowTrail.TeleportMetres;
        if (jumped && alive && _arrive.Running) { Poof(new Vector3(_lastFeet.X, _lastFeet.Y, _lastFeet.Z)); Poof(feet); _arrive.Cancel(); }
        _lastFeet = new NVec3(feet.X, feet.Y, feet.Z);
        _hasLast = true;
        if (vanishing && alive) { _arrive.Start(ArriveSeconds); _vanishT += dt; }
        else { _arrive.Tick(dt); _vanishT = 0; }
        TickSpiral(dt, vanishing && alive, feet);
        var body = new NVec3(feet.X, feet.Y + BodyHeight, feet.Z);
        _trail.Update(dt, body, alive);
        var near = Sfx.ListenerDistance(feet) <= EffectCull.EffectRange;
        if (_sparkles != null)
        {
            var on = alive && near && Sfx.ListenerDistance(feet) <= SparkleRange;
            if (_sparkles.Emitting != on) ParticleFx.Run(_sparkles, on);
        }
        if (_trail.Empty || !near || GetViewport()?.GetCamera3D() is not { } cam)
        {
            if (_ribbon.Visible && (_trail.Empty || !near)) _ribbon.Visible = false;
            return;
        }
        var camAt = cam.GlobalPosition;
        if (!_trail.Build(new NVec3(camAt.X, camAt.Y, camAt.Z), AsN(_verts), AsN(_colours), AsN(_uvs), _indices, out var vc, out var ic))
        {
            _ribbon.Visible = false;
            return;
        }
        for (var i = vc; i < _verts.Length; i++) _verts[i] = _verts[vc - 1];
        Array.Clear(_indices, ic, _indices.Length - ic);
        _arrays[(int)Mesh.ArrayType.Vertex] = _verts;
        _arrays[(int)Mesh.ArrayType.Color] = _colours;
        _arrays[(int)Mesh.ArrayType.TexUV] = _uvs;
        _arrays[(int)Mesh.ArrayType.Index] = _indices;
        if (_meshHasSurface) _mesh.ClearSurfaces();
        _mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, _arrays);
        _meshHasSurface = true;
        _ribbon.Visible = true;
    }

    /// <summary>The spiral on while vanishing and near the camera, hidden (and the glow back to normal) otherwise.</summary>
    void TickSpiral(float dt, bool on, Vector3 feet)
    {
        on &= Sfx.ListenerDistance(feet) <= EffectCull.EffectRange;
        if (!on)
        {
            if (_spiral is { Visible: true }) { _spiral.Visible = false; _body?.SetShaderParameter("glow", BodyGlow); }
            return;
        }
        _spiral ??= MakeSpiral();
        _spiral.Visible = true;
        DrawSpiral(dt, Mathf.Clamp(_vanishT / _vanishSeconds, 0f, 1f));
    }

    // Godot's Vector3/Vector2/Color have the layout of System.Numerics' Vector3/Vector2/Vector4: the maths fills the Godot buffers in place.
    static Span<NVec3> AsN(Vector3[] a) => MemoryMarshal.Cast<Vector3, NVec3>(a);
    static Span<NVec2> AsN(Vector2[] a) => MemoryMarshal.Cast<Vector2, NVec2>(a);
    static Span<NVec4> AsN(Color[] a) => MemoryMarshal.Cast<Color, NVec4>(a);
}
