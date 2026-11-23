using System.Runtime.InteropServices;
using Godot;
using Drycastle.ShooterCore.Effects;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Wildlife;
using NVec2 = System.Numerics.Vector2;
using NVec3 = System.Numerics.Vector3;
using NVec4 = System.Numerics.Vector4;

namespace Drycastle.Shooter;

/// <summary>
/// The looks of the Unicorn (animals.json <c>"fx": "unicorn"</c>), pure client, nothing on the wire. The body is mother of pearl (white, pastel rainbow bands, a rainbow rim, glitter); a rainbow mane, a forelock
/// and a long rainbow tail are built on its neck, head and tail bones (locks of hair that sway and stream out behind it as it moves, on the dead one too); its horn is a pearl with twinkling rainbow speckles
/// (shader on the model's <c>Horn</c> mesh); rainbow sparkles drift up from <c>HornTip</c> and fall off the body while it lives (CPU particles, world space so they trail); and a rainbow hangs in the air
/// behind it (<see cref="RainbowTrail"/>: a tube with volume, striped red on top to violet). Child of the animal's node, built once per animal, freed with it. <see cref="Tick"/> runs after the animal is
/// placed each frame. Far from the camera the sparkles stop and the trail is not rebuilt (<see cref="EffectCull"/>); a trail with nothing left is hidden and costs nothing.
/// While the animal is in <see cref="AnimalState.Vanish"/> (immune, about to be teleported) a whirl goes up round it: rainbow helixes of stars that widen upward and wind faster as the clock runs out, a
/// ring of pastel clouds turning the other way round its feet, stars streaming up, a light that runs through the rainbow, and its body glows; when it then reappears far away (the position jump) clouds,
/// a burst of stars, a ring of stars along the ground, a fountain of stars and a flash go off at the old place and at the new one.
/// </summary>
public sealed partial class UnicornFx : Node3D
{
    /// <summary>The id of this visual set in animals.json.</summary>
    public const string Id = "unicorn";

    /// <summary>Height (m) over the feet of the middle of the rainbow trail: the unicorn's back.</summary>
    const float BodyHeight = 1.0f;
    /// <summary>Distance (m) from the camera past which the sparkles are not run (they are tiny).</summary>
    const float SparkleRange = 90f;
    const int Sparkles = 40, TrailStars = 36;
    /// <summary>The vanish whirl: streams, stars per stream, radius (m) at the start and the end of the clock, the share of it at the feet (it widens upward to <see cref="SpiralFlare"/> of it), height (m), turns over its height, turn rate (rad/s) at the start and the factor it reaches at the end, rise rate (heights/s), star size (m).</summary>
    const int Streams = 5, PerStream = 26;
    const float SpiralRadius = 2.3f, SpiralRadiusEnd = 1.1f, SpiralFoot = 0.5f, SpiralFlare = 1.25f, SpiralHeight = 5.5f, SpiralTurns = 2.5f, SpinStart = 3f, SpinGain = 3.5f, RiseRate = 0.6f, SpiralSparkle = 0.6f;
    /// <summary>The clouds of the whirl: how many, and the width (m) of the smallest and the biggest.</summary>
    const int Clouds = 18;
    const float CloudMin = 0.7f, CloudMax = 1.5f;
    /// <summary>The mane and the tail stream out against the motion: metres of drift per m/s, at most <see cref="DriftMax"/> m.</summary>
    const float DriftPerSpeed = 0.06f, DriftMax = 0.55f;
    /// <summary>How long (s) after the last vanishing frame a position jump still counts as the vanish's teleport (the state and the new position arrive in one snapshot).</summary>
    const float ArriveSeconds = 1.5f;
    const float BodyGlow = 0.35f, VanishGlow = 1.6f;

    static Shader? _hornShader, _bodyShader, _trailShader, _sparkleShader, _maneShader;
    static ShaderMaterial? _hornMat, _trailMat, _sparkleMat;
    static Texture2D? _palette;
    static QuadMesh? _quad;
    static SphereMesh? _cloud;
    static Gradient? _rainbow, _fade, _pastel, _cloudFade;
    static Curve? _twinkle, _swell;

    CpuParticles3D? _sparkles, _trailStars;
    ShaderMaterial? _body;           // this animal's own body material (its glow pulses while it vanishes)
    ShaderMaterial? _hair;           // this animal's own mane and tail material (its drift is its own)
    MultiMeshInstance3D? _spiral;    // the vanish helixes: Streams x PerStream quads, moved every frame, built on the first vanish
    MultiMeshInstance3D? _clouds;    // the ring of clouds of the whirl
    CpuParticles3D? _risers;         // stars streaming up inside the whirl
    OmniLight3D? _light;             // the whirl's light
    Vector3 _velocity;               // smoothed, m/s: what the mane and the tail stream out against
    float _vanishSeconds, _spin, _rise, _vanishT;
    Countdown _arrive;               // running while / shortly after a vanish: a jump then is its teleport
    NVec3 _lastFeet;
    bool _hasLast;
    readonly MeshInstance3D _ribbon = new() { CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, TopLevel = true, Visible = false, ExtraCullMargin = 100f };
    readonly ArrayMesh _mesh = new();
    readonly RainbowTrail _trail = new();
    // the ribbon's buffers: whole, reused every frame (the unused tail is degenerate triangles on vertex 0)
    readonly Vector3[] _verts = new Vector3[RainbowTrail.MaxVertices];
    readonly Vector3[] _normals = new Vector3[RainbowTrail.MaxVertices];
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
        BuildHair(model);
        if (model.FindChild("HornTip", true, false) is Node3D tip)
        {
            _sparkles = MakeSparkles();
            tip.AddChild(_sparkles);
        }
        _trailStars = MakeTrailStars();
        AddChild(_trailStars);
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
        _cloud ??= new SphereMesh { Radius = 0.5f, Height = 1f, RadialSegments = 8, Rings = 4, Material = RocketFx.Part(new Color(1, 1, 1)) };
        _pastel ??= RocketFx.Ramp(Enumerable.Range(0, RainbowTrail.Stripes).Select(i => (i / (float)(RainbowTrail.Stripes - 1), Pastel(RainbowTrail.RowHue(i), 1f))).ToArray());
        _cloudFade ??= RocketFx.Ramp((0f, new Color(1, 1, 1, 0.95f)), (0.6f, new Color(1, 1, 1, 0.7f)), (1f, new Color(1, 1, 1, 0)));
        _swell ??= RocketFx.Curve2(0.6f, 1.4f);
    }

    /// <summary>A hue washed out to a cloud's pastel.</summary>
    static Color Pastel(float hue, float alpha)
    {
        var c = RainbowTrail.HueRgb(hue, 0.32f);
        return new Color(c.X, c.Y, c.Z, alpha);
    }

    /// <summary>The buffers of the hair that hangs on one bone, in the bone's own space.</summary>
    sealed class HairMesh
    {
        public readonly List<Vector3> Verts = new(), Normals = new();
        public readonly List<Color> Colours = new();
        public readonly List<Vector2> Uvs = new();
        public readonly List<int> Indices = new();
    }

    /// <summary>
    /// One lock of hair: a four-sided tube along <paramref name="path"/> (0 the root .. 1 the tip, in the skeleton's space: x to the animal's left, y up, z to its head; the model's own units, a horse is
    /// ~0.45 long), <paramref name="radius"/> thick, moved into the bone's space by <paramref name="toBone"/>. <paramref name="across"/> is any direction the lock never runs along (it fixes the tube's twist).
    /// </summary>
    static void AddLock(HairMesh m, Transform3D toBone, Func<float, Vector3> path, Func<float, float> radius, int rings, Vector3 across, NVec3 colour, float phase)
    {
        var first = m.Verts.Count;
        for (var i = 0; i < rings; i++)
        {
            var u = i / (float)(rings - 1);
            var p = path(u);
            var t = (path(MathF.Min(u + 0.05f, 1f)) - path(MathF.Max(u - 0.05f, 0f))).Normalized();
            var a = t.Cross(across).Normalized();
            var b = t.Cross(a);
            var r = radius(u);
            var c = new Color(colour.X, colour.Y, colour.Z).Lerp(Colors.White, 0.3f * u); // lighter toward the tip
            for (var k = 0; k < 4; k++)
            {
                var n = k switch { 0 => a, 1 => b, 2 => -a, _ => -b };
                m.Verts.Add(toBone * (p + n * r));
                m.Normals.Add((toBone.Basis * n).Normalized());
                m.Colours.Add(c);
                m.Uvs.Add(new Vector2(phase, u));
            }
        }
        for (var i = 0; i < rings - 1; i++)
            for (var k = 0; k < 4; k++)
            {
                int v0 = first + i * 4 + k, v1 = first + i * 4 + (k + 1) % 4, v2 = v0 + 4, v3 = v1 + 4;
                m.Indices.AddRange(new[] { v0, v1, v2, v1, v3, v2 });
            }
    }

    /// <summary>
    /// The mane, the forelock and the tail. The places are measured on the pack's horse in its rest pose (the skeleton's space, the model's units: withers at y 0.28 z 0.085, poll at y 0.356 z 0.162, dock at
    /// y 0.243 z -0.118): a lock down each side of the neck at eight points of the crest, red at the poll to violet at the withers; two locks over the forehead beside the horn; seven long strands of tail,
    /// one per colour. Every lock hangs on the bone nearest its root (a <see cref="BoneAttachment3D"/>), so it follows the neck, the head and the dock as they are animated.
    /// </summary>
    void BuildHair(Node3D model)
    {
        if (model.FindChildren("*", "Skeleton3D", true, false).OfType<Skeleton3D>().FirstOrDefault() is not { } sk) return;
        var byBone = new Dictionary<int, HairMesh>();
        (HairMesh Mesh, Transform3D ToBone)? On(string part)
        {
            for (var b = 0; b < sk.GetBoneCount(); b++)
                if (sk.GetBoneName(b).Contains(part))
                {
                    if (!byBone.TryGetValue(b, out var m)) byBone[b] = m = new HairMesh();
                    return (m, sk.GetBoneGlobalRest(b).AffineInverse());
                }
            return null;
        }
        const int Crest = 8;
        for (var j = 0; j < Crest; j++)
        {
            var t = j / (float)(Crest - 1); // 0 the poll .. 1 the withers
            if (On(t < 0.3f ? "Neck_Top" : t < 0.6f ? "Neck_03" : t < 0.85f ? "Neck_02" : "Neck_01") is not { } on) continue;
            var root = new Vector3(0, Mathf.Lerp(0.356f, 0.286f, t), Mathf.Lerp(0.162f, 0.086f, t));
            var colour = RainbowTrail.HueRgb(t * RainbowTrail.VioletHue);
            var (reach, length) = (Mathf.Lerp(0.038f, 0.058f, t), 0.085f + 0.02f * MathF.Sin(j * 1.9f)); // out past the side of the neck (wider at the shoulder), and down
            foreach (var side in new[] { -1f, 1f })
                AddLock(on.Mesh, on.ToBone,
                    u => root + new Vector3(side * (0.005f + reach * MathF.Sin(MathF.Min(u * 1.6f, 1f) * MathF.PI / 2)), 0.035f * u - (0.035f + length) * u * u, -0.03f * u),
                    u => 0.014f * (1f - 0.8f * u), 6, Vector3.Back, colour, (j * 0.37f + (side > 0 ? 0.5f : 0f)) % 1f);
        }
        if (On("Head_Top") is { } head)
            foreach (var side in new[] { -1f, 1f })
                AddLock(head.Mesh, head.ToBone,
                    u => new Vector3(side * (0.01f + 0.022f * MathF.Sin(u * MathF.PI / 2)), 0.362f + 0.02f * u - 0.07f * u * u, 0.176f + 0.03f * u),
                    u => 0.011f * (1f - 0.8f * u), 5, Vector3.Back, RainbowTrail.HueRgb(side > 0 ? 0.92f : 0.86f), side > 0 ? 0.2f : 0.7f);
        if (On("Tail_01") is { } tail)
        {
            const int Strands = RainbowTrail.Stripes;
            for (var i = 0; i < Strands; i++)
            {
                var fan = (i - (Strands - 1) / 2f) / ((Strands - 1) / 2f); // -1 .. 1 across the tail
                AddLock(tail.Mesh, tail.ToBone,
                    u => new Vector3(fan * 0.045f * MathF.Pow(u, 0.7f), 0.243f + 0.03f * MathF.Sin(u * MathF.PI) - 0.2f * MathF.Pow(u, 1.5f), -0.118f - (0.15f - 0.03f * MathF.Abs(fan)) * MathF.Sin(u * MathF.PI / 2)),
                    u => 0.002f + 0.017f * (0.45f + u) * MathF.Pow(1f - u, 0.6f), 8, Vector3.Right, RainbowTrail.HueRgb(i / (float)(Strands - 1) * RainbowTrail.VioletHue), i / (float)Strands);
            }
        }
        if (byBone.Count == 0) return;
        _maneShader ??= GD.Load<Shader>("res://shaders/unicorn_mane.gdshader");
        _hair = new ShaderMaterial { Shader = _maneShader };
        foreach (var (bone, m) in byBone)
        {
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = m.Verts.ToArray();
            arrays[(int)Mesh.ArrayType.Normal] = m.Normals.ToArray();
            arrays[(int)Mesh.ArrayType.Color] = m.Colours.ToArray();
            arrays[(int)Mesh.ArrayType.TexUV] = m.Uvs.ToArray();
            arrays[(int)Mesh.ArrayType.Index] = m.Indices.ToArray();
            var mesh = new ArrayMesh();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            var at = new BoneAttachment3D { Name = $"Hair{bone}" };
            sk.AddChild(at);
            at.BoneIdx = bone;
            // the locks sway and stream past the box of their rest shape: a margin so they are not culled at the screen's edge
            at.AddChild(new MeshInstance3D { Name = "Locks", Mesh = mesh, MaterialOverride = _hair, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, ExtraCullMargin = 1f });
        }
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
            ScaleAmountMin = 0.1f, ScaleAmountMax = 0.24f, ScaleAmountCurve = _twinkle, ColorInitialRamp = _rainbow, ColorRamp = _fade,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
    }

    /// <summary>Stars that come off the whole body and are left behind in the air, sinking slowly: they fill the rainbow trail when it moves and hang round it when it stands.</summary>
    CpuParticles3D MakeTrailStars()
    {
        EnsureShared();
        var calm = GameSettings.Current.ReduceMotion ? 0f : 1f;
        return new CpuParticles3D
        {
            Name = "TrailStars", Visible = false, Emitting = false, Mesh = _quad, Amount = TrailStars, Lifetime = 2.6, Explosiveness = 0f, Randomness = 0.6f, LocalCoords = false,
            Position = new Vector3(0, BodyHeight, 0), EmissionShape = CpuParticles3D.EmissionShapeEnum.Box, EmissionBoxExtents = new Vector3(0.4f, 0.6f, 0.9f), Direction = Vector3.Up, Spread = 180f,
            InitialVelocityMin = 0.05f * calm, InitialVelocityMax = 0.4f * calm, Gravity = new Vector3(0, -0.25f * calm, 0), DampingMin = 0.2f, DampingMax = 0.5f,
            ScaleAmountMin = 0.12f, ScaleAmountMax = 0.34f, ScaleAmountCurve = _twinkle, ColorInitialRamp = _rainbow, ColorRamp = _fade,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
    }

    static Curve TwinkleCurve()
    {
        var c = new Curve();
        c.AddPoint(new Vector2(0, 0.3f)); c.AddPoint(new Vector2(0.25f, 1f)); c.AddPoint(new Vector2(0.5f, 0.55f)); c.AddPoint(new Vector2(0.75f, 1f)); c.AddPoint(new Vector2(1, 0.2f));
        return c;
    }

    /// <summary>
    /// The parts of the vanish whirl, built on the first vanish: the helixes (one quad per star, coloured per instance, drawn with the horn's star sparkle), the ring of clouds (one sphere per cloud),
    /// the stars streaming up from a ring round the feet, and the light.
    /// </summary>
    void MakeSpiral()
    {
        EnsureShared();
        var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true, Mesh = _quad, InstanceCount = Streams * PerStream };
        _spiral = new MultiMeshInstance3D { Name = "VanishSpiral", Multimesh = mm, Visible = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, ExtraCullMargin = 4f };
        AddChild(_spiral);
        var cm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true, Mesh = _cloud, InstanceCount = Clouds };
        _clouds = new MultiMeshInstance3D { Name = "VanishClouds", Multimesh = cm, Visible = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, ExtraCullMargin = 4f };
        AddChild(_clouds);
        var calm = GameSettings.Current.ReduceMotion ? 0.3f : 1f;
        _risers = new CpuParticles3D
        {
            Name = "VanishStars", Visible = false, Emitting = false, Mesh = _quad, Amount = 80, Lifetime = 1.4, Explosiveness = 0f, Randomness = 0.5f, LocalCoords = false,
            EmissionShape = CpuParticles3D.EmissionShapeEnum.Ring, EmissionRingAxis = Vector3.Up, EmissionRingRadius = 1.3f, EmissionRingInnerRadius = 0.5f, EmissionRingHeight = 0.2f,
            Position = new Vector3(0, 0.2f, 0), Direction = Vector3.Up, Spread = 12f, InitialVelocityMin = 2f * calm, InitialVelocityMax = 5f * calm, Gravity = Vector3.Zero,
            TangentialAccelMin = 1f * calm, TangentialAccelMax = 3f * calm,
            ScaleAmountMin = 0.16f, ScaleAmountMax = 0.42f, ScaleAmountCurve = _twinkle, ColorInitialRamp = _rainbow, ColorRamp = _fade,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_risers);
        _light = new OmniLight3D { Name = "VanishLight", Visible = false, LightEnergy = 0f, OmniRange = 11f, ShadowEnabled = false, Position = new Vector3(0, 1.4f, 0) };
        AddChild(_light);
    }

    /// <summary>
    /// One frame of the vanish. The helixes wind up round the body (a stream starts a fifth of a turn after the one before; each star rises along it, so the whole whirl climbs) and widen upward like a
    /// funnel; the radius draws in and the turn speeds up as <paramref name="progress"/> (0..1 of the clock) runs out. Stars are tiny at the ends of a helix and full in the middle, in the colours of the
    /// rainbow. The clouds ring the feet, turn the other way, bob, and climb and close in toward the end. The light runs through the rainbow and brightens; the body glows more.
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
                var r = radius * Mathf.Lerp(SpiralFoot, SpiralFlare, f);
                var at = new Vector3(Mathf.Cos(angle) * r, 0.1f + f * SpiralHeight, Mathf.Sin(angle) * r);
                var size = SpiralSparkle * MathF.Sqrt(Mathf.Sin(f * Mathf.Pi)) * (0.6f + 0.4f * ((k * 7) % 5) / 4f) * arrive + 0.001f;
                var i = s * PerStream + k;
                mm.SetInstanceTransform(i, new Transform3D(Basis.Identity.Scaled(Vector3.One * size), at));
                var c = RainbowTrail.HueRgb(RainbowTrail.VioletHue * ((f + s / (float)Streams) % 1f));
                mm.SetInstanceColor(i, new Color(c.X, c.Y, c.Z, 1f));
            }
        var cm = _clouds!.Multimesh;
        for (var i = 0; i < Clouds; i++)
        {
            var own = (i * 0.618f) % 1f; // each cloud's own share: size, height, wobble
            var angle = -_spin * 0.45f + i * Mathf.Tau / Clouds;
            var r = radius * (0.85f + 0.35f * own) * (1f - 0.35f * progress * progress);
            var y = 0.35f + 1.1f * own + 0.15f * Mathf.Sin(_vanishT * 2.5f * calm + i) + 2.2f * own * progress * progress;
            var size = Mathf.Lerp(CloudMin, CloudMax, (i * 0.381f) % 1f) * arrive * (0.9f + 0.1f * Mathf.Sin(_vanishT * 3f + i * 2f)) + 0.001f;
            cm.SetInstanceTransform(i, new Transform3D(Basis.Identity.Scaled(new Vector3(size, size * 0.7f, size)), new Vector3(Mathf.Cos(angle) * r, y, Mathf.Sin(angle) * r)));
            cm.SetInstanceColor(i, Pastel(i / (float)Clouds * RainbowTrail.VioletHue + _vanishT * 0.15f, 0.85f * arrive));
        }
        var lc = RainbowTrail.HueRgb(_vanishT * 0.8f, 0.6f);
        _light!.LightColor = new Color(lc.X, lc.Y, lc.Z);
        _light.LightEnergy = arrive * (1.5f + 3.5f * progress);
        _body?.SetShaderParameter("glow", BodyGlow + (VanishGlow - BodyGlow) * Mathf.Clamp(_vanishT / 0.5f, 0f, 1f) * (0.75f + 0.25f * Mathf.Sin(_vanishT * 14f)));
    }

    /// <summary>
    /// Where it vanished and where it appeared (<paramref name="at"/>: the animal's feet): a burst of pastel clouds, a burst of rainbow stars, a ring of stars racing out along the ground, a fountain of
    /// stars straight up and a flash of light. Nothing out of the effect range.
    /// </summary>
    void Poof(Vector3 at)
    {
        if (Sfx.ListenerDistance(at) > EffectCull.EffectRange || GetParent()?.GetParent() is not Node3D world || !world.IsInsideTree()) return;
        EnsureShared();
        var calm = GameSettings.Current.ReduceMotion ? 0.2f : 1f;
        var root = new Node3D { Position = at };
        world.AddChild(root);
        CpuParticles3D Stars(int amount, float life, float scaleMin, float scaleMax) => new()
        {
            Mesh = _quad, Amount = amount, Lifetime = life, OneShot = true, Explosiveness = 1f, Randomness = 0.4f, LocalCoords = false, Emitting = true,
            ScaleAmountMin = scaleMin, ScaleAmountMax = scaleMax, ScaleAmountCurve = _twinkle, ColorInitialRamp = _rainbow, ColorRamp = _fade,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        root.AddChild(new CpuParticles3D
        {
            Name = "Clouds", Mesh = _cloud, Amount = 22, Lifetime = 1.5, OneShot = true, Explosiveness = 1f, Randomness = 0.4f, LocalCoords = false, Emitting = true,
            EmissionShape = CpuParticles3D.EmissionShapeEnum.Sphere, EmissionSphereRadius = 0.9f, Position = new Vector3(0, 1.0f, 0), Direction = Vector3.Up, Spread = 180f,
            InitialVelocityMin = 1.2f * calm, InitialVelocityMax = 4f * calm, Gravity = new Vector3(0, 0.5f * calm, 0), DampingMin = 1.5f, DampingMax = 3f,
            ScaleAmountMin = CloudMin, ScaleAmountMax = CloudMax, ScaleAmountCurve = _swell, ColorInitialRamp = _pastel, ColorRamp = _cloudFade,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
        var burst = Stars(110, 1.7f, 0.2f, 0.6f);
        burst.Name = "Burst"; burst.Position = new Vector3(0, 1.0f, 0);
        burst.EmissionShape = CpuParticles3D.EmissionShapeEnum.Sphere; burst.EmissionSphereRadius = 0.5f; burst.Direction = Vector3.Up; burst.Spread = 180f;
        burst.InitialVelocityMin = 2.5f * calm; burst.InitialVelocityMax = 9f * calm; burst.Gravity = new Vector3(0, -1.5f * calm, 0); burst.DampingMin = 1f; burst.DampingMax = 2.5f;
        root.AddChild(burst);
        var ring = Stars(56, 1.0f, 0.25f, 0.5f); // flat: out along the ground, all at one speed
        ring.Name = "Ring"; ring.Position = new Vector3(0, 0.3f, 0); ring.Randomness = 0f;
        ring.Direction = Vector3.Forward; ring.Spread = 180f; ring.Flatness = 1f;
        ring.InitialVelocityMin = 9f * calm; ring.InitialVelocityMax = 10f * calm; ring.Gravity = Vector3.Zero; ring.DampingMin = 6f; ring.DampingMax = 6f;
        root.AddChild(ring);
        var fountain = Stars(48, 1.6f, 0.2f, 0.5f);
        fountain.Name = "Fountain"; fountain.Position = new Vector3(0, 0.5f, 0); fountain.Explosiveness = 0.7f;
        fountain.Direction = Vector3.Up; fountain.Spread = 7f;
        fountain.InitialVelocityMin = 7f * calm; fountain.InitialVelocityMax = 16f * calm; fountain.Gravity = new Vector3(0, -7f * calm, 0);
        root.AddChild(fountain);
        var light = new OmniLight3D { LightColor = new Color(1f, 0.85f, 1f), LightEnergy = 6f, OmniRange = 14f, ShadowEnabled = false, Position = new Vector3(0, 1.4f, 0) };
        root.AddChild(light);
        light.CreateTween().TweenProperty(light, "light_energy", 0f, 0.7f).SetDelay(0.1f);
        ShotFx.Expire(world, root, 2.2f);
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
        // the mane and the tail stream out against the way it goes (not across a teleport, not once dead)
        var moved = _hasLast && !jumped && alive && dt > 1e-5f ? (feet - new Vector3(_lastFeet.X, _lastFeet.Y, _lastFeet.Z)) / dt : Vector3.Zero;
        _velocity = _velocity.Lerp(moved, 1f - MathF.Exp(-dt * 6f));
        _hair?.SetShaderParameter("drift", (-_velocity * DriftPerSpeed).LimitLength(DriftMax) * (GameSettings.Current.ReduceMotion ? 0.3f : 1f));
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
        if (_trailStars != null && _trailStars.Emitting != (alive && near)) ParticleFx.Run(_trailStars, alive && near);
        if (_trail.Empty || !near)
        {
            if (_ribbon.Visible && (_trail.Empty || !near)) _ribbon.Visible = false;
            return;
        }
        if (!_trail.Build(AsN(_verts), AsN(_normals), AsN(_colours), AsN(_uvs), _indices, out var vc, out var ic))
        {
            _ribbon.Visible = false;
            return;
        }
        for (var i = vc; i < _verts.Length; i++) _verts[i] = _verts[vc - 1];
        Array.Clear(_indices, ic, _indices.Length - ic);
        _arrays[(int)Mesh.ArrayType.Vertex] = _verts;
        _arrays[(int)Mesh.ArrayType.Normal] = _normals;
        _arrays[(int)Mesh.ArrayType.Color] = _colours;
        _arrays[(int)Mesh.ArrayType.TexUV] = _uvs;
        _arrays[(int)Mesh.ArrayType.Index] = _indices;
        if (_meshHasSurface) _mesh.ClearSurfaces();
        _mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, _arrays);
        _meshHasSurface = true;
        _ribbon.Visible = true;
    }

    /// <summary>The whirl on while vanishing and near the camera, hidden (and the glow back to normal) otherwise.</summary>
    void TickSpiral(float dt, bool on, Vector3 feet)
    {
        on &= Sfx.ListenerDistance(feet) <= EffectCull.EffectRange;
        if (!on)
        {
            if (_spiral is { Visible: true })
            {
                _spiral.Visible = false; _clouds!.Visible = false; _light!.Visible = false;
                ParticleFx.Run(_risers!, false);
                _body?.SetShaderParameter("glow", BodyGlow);
            }
            return;
        }
        if (_spiral == null) MakeSpiral();
        if (!_spiral!.Visible)
        {
            _spiral.Visible = true; _clouds!.Visible = true; _light!.Visible = true;
            ParticleFx.Run(_risers!, true);
        }
        DrawSpiral(dt, Mathf.Clamp(_vanishT / _vanishSeconds, 0f, 1f));
    }

    // Godot's Vector3/Vector2/Color have the layout of System.Numerics' Vector3/Vector2/Vector4: the maths fills the Godot buffers in place.
    static Span<NVec3> AsN(Vector3[] a) => MemoryMarshal.Cast<Vector3, NVec3>(a);
    static Span<NVec2> AsN(Vector2[] a) => MemoryMarshal.Cast<Vector2, NVec2>(a);
    static Span<NVec4> AsN(Color[] a) => MemoryMarshal.Cast<Color, NVec4>(a);
}
