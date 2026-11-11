using Godot;
using Num = System.Numerics;
using Suchohrad.ShooterCore;

namespace Suchohrad.Shooter;

/// <summary>
/// Street lamps of the base map (lamps.json). Poles and heads are primitive MultiMeshes; each lamp gets a glow
/// billboard in one MultiMesh (hidden far away by visibility range); a pool of <see cref="PoolSize"/> real lights
/// follows the camera's nearest lamps. Everything is driven by <see cref="DayNightCycle.NightFactor"/>. The lamps
/// are solid: one thin box per pole, the same on the server. The curve and the pool assignment are in
/// <see cref="LampCurve"/> and <see cref="LampPool"/>.
/// </summary>
public partial class StreetLamps : Node3D
{
    public const int PoolSize = 12;
    public const float PoleHeight = 5f;
    /// <summary>Lights fade out between 60% and 100% of this distance from the camera.</summary>
    const float PoolMaxDistance = 90f;
    const float LightRange = 14f;
    /// <summary>Pool light energy at full night, right under the lamp.</summary>
    const float LightPeak = 2.2f;
    /// <summary>Energy per second toward the target, so a light fades in or out in about half a second.</summary>
    const float LightRate = 2f;
    const float RefreshSeconds = 0.2f;
    const float GlowVisibleTo = 260f;
    static readonly Color LampColour = new(1f, 0.78f, 0.45f);

    /// <summary>Foot of each pole on the ground (Godot axes, Y up).</summary>
    public IReadOnlyList<Vector3> Bases { get; init; } = Array.Empty<Vector3>();

    /// <summary>Headless server: the pole colliders only, no visuals, light pool or per-frame work.</summary>
    public bool CollisionOnly { get; init; }

    Vector3[] _heads = Array.Empty<Vector3>();
    Num.Vector3[] _headsNum = Array.Empty<Num.Vector3>();
    readonly OmniLight3D[] _lights = new OmniLight3D[PoolSize];
    readonly int[] _slots = new int[PoolSize];
    readonly int[] _shown = new int[PoolSize];
    StandardMaterial3D _headMat = new();
    StandardMaterial3D _glowMat = new();
    double _refreshIn;
    float _level = -1f;

    public override void _Ready()
    {
        if (Bases.Count == 0) { SetProcess(false); return; }
        if (CollisionOnly) { SetProcess(false); BuildCollision(); return; }
        _heads = Bases.Select(b => b + Vector3.Up * (PoleHeight - 0.04f)).ToArray();
        _headsNum = _heads.Select(h => new Num.Vector3(h.X, h.Y, h.Z)).ToArray();
        BuildVisuals();
        BuildCollision();
        BuildPool();
        Array.Fill(_slots, -1);
        Array.Fill(_shown, -2);
        if (DisplayServer.GetName() == "headless") SetProcess(false); // the light pool is visual only; the headless server keeps the collision
    }

    public override void _Process(double delta)
    {
        float level = LampCurve.Level(DayNightCycle.Current?.NightFactor ?? 0f);
        if (level != _level) ApplyLevel(level);

        var cam = GetViewport().GetCamera3D();
        _refreshIn -= delta;
        if (_refreshIn <= 0 && cam != null)
        {
            _refreshIn = RefreshSeconds;
            var eye = cam.GlobalPosition;
            LampPool.Refresh(_slots, _headsNum, new Num.Vector3(eye.X, eye.Y, eye.Z), PoolMaxDistance);
        }

        var eyePos = cam?.GlobalPosition ?? Vector3.Zero;
        for (int s = 0; s < PoolSize; s++)
        {
            var light = _lights[s];
            int idx = _slots[s];
            if (idx != _shown[s])
            {
                // A new lamp: move the light there and start it from dark, so it never pops on at a lamp.
                _shown[s] = idx;
                light.LightEnergy = 0f;
                if (idx >= 0) light.Position = _heads[idx];
            }
            float target = idx >= 0 && cam != null
                ? LampPool.Energy(level, eyePos.DistanceTo(_heads[idx]), PoolMaxDistance) * LightPeak
                : 0f;
            light.LightEnergy = Mathf.MoveToward(light.LightEnergy, target, (float)delta * LightRate);
        }
    }

    void ApplyLevel(float level)
    {
        _level = level;
        _headMat.EmissionEnergyMultiplier = 0.1f + 3f * level;
        _glowMat.AlbedoColor = new Color(LampColour.R, LampColour.G, LampColour.B, 0.85f * level);
    }

    void BuildVisuals()
    {
        var poleMat = new StandardMaterial3D { AlbedoColor = new Color(0.2f, 0.21f, 0.23f) };
        var poleMesh = new CylinderMesh { TopRadius = 0.05f, BottomRadius = 0.07f, Height = PoleHeight, RadialSegments = 6, Rings = 1 };
        AddChild(Batch(poleMesh, Bases.Select(b => new Transform3D(Basis.Identity, b + Vector3.Up * (PoleHeight / 2))), poleMat));

        _headMat = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.55f, 0.55f, 0.52f),
            EmissionEnabled = true,
            Emission = LampColour,
        };
        var headMesh = new BoxMesh { Size = new Vector3(0.42f, 0.08f, 0.22f) };
        AddChild(Batch(headMesh, _heads.Select(h => new Transform3D(Basis.Identity, h)), _headMat));

        var glowTex = new GradientTexture2D
        {
            Width = 64,
            Height = 64,
            Fill = GradientTexture2D.FillEnum.Radial,
            FillFrom = new Vector2(0.5f, 0.5f),
            FillTo = new Vector2(1f, 0.5f),
            Gradient = new Gradient { Colors = new[] { Colors.White, new Color(1f, 1f, 1f, 0f) }, Offsets = new[] { 0f, 1f } },
        };
        _glowMat = new StandardMaterial3D
        {
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BlendMode = BaseMaterial3D.BlendModeEnum.Add,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
            AlbedoTexture = glowTex,
            AlbedoColor = new Color(LampColour.R, LampColour.G, LampColour.B, 0f),
            NoDepthTest = false,
        };
        var glow = Batch(new QuadMesh { Size = new Vector2(2.4f, 2.4f) },
            _heads.Select(h => new Transform3D(Basis.Identity, h - Vector3.Up * 0.15f)), _glowMat);
        glow.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        glow.VisibilityRangeEnd = GlowVisibleTo;
        glow.VisibilityRangeEndMargin = 20f;
        AddChild(glow);
        ApplyLevel(0f);
    }

    static MultiMeshInstance3D Batch(Mesh mesh, IEnumerable<Transform3D> xforms, Material mat)
    {
        var list = xforms.ToList();
        var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = mesh, InstanceCount = list.Count };
        for (int i = 0; i < list.Count; i++) mm.SetInstanceTransform(i, list[i]);
        return new MultiMeshInstance3D { Multimesh = mm, MaterialOverride = mat };
    }

    void BuildCollision()
    {
        var body = new StaticBody3D { Name = "LampPoles" };
        var shape = new BoxShape3D { Size = new Vector3(0.2f, PoleHeight, 0.2f) };
        foreach (var b in Bases)
            body.AddChild(new CollisionShape3D { Shape = shape, Position = b + Vector3.Up * (PoleHeight / 2) });
        AddChild(body);
    }

    void BuildPool()
    {
        for (int s = 0; s < PoolSize; s++)
        {
            _lights[s] = new OmniLight3D
            {
                LightColor = LampColour,
                OmniRange = LightRange,
                ShadowEnabled = false,
                LightEnergy = 0f,
            };
            AddChild(_lights[s]);
        }
    }
}
