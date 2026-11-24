using Godot;
using Drycastle.ShooterCore.Scenery;
using Num = System.Numerics;

namespace Drycastle.Shooter.Scenery;

/// <summary>
/// Street lamps of the base map (lamps.json), and the lamps of placed utility poles (<see cref="PoleHeads"/>: the pole
/// and its luminaire are the placed object's own model, so only the lit plate and the light are added
/// here). Poles and heads are primitive MultiMeshes; every lamp wears a lit lens that blooms, like a car's (<see cref="LampKit"/>); a pool of
/// <see cref="PoolSize"/> real spot beams pointing straight down follows the camera's nearest lamps (the rest are lens only). Everything is driven by <see cref="DayNightCycle.NightFactor"/>. The lamps
/// are solid: one thin box per pole, the same on the server. The curve and the pool assignment are in
/// <see cref="LampCurve"/> and <see cref="LampPool"/>.
/// </summary>
public partial class StreetLamps : Node3D
{
    public const int PoolSize = 12;
    public const float PoleHeight = 5f;
    /// <summary>Lights fade out between 60% and 100% of this distance from the camera.</summary>
    const float PoolMaxDistance = 90f;
    /// <summary>Energy per second toward the target, so a light fades in or out in about half a second.</summary>
    const float LightRate = 2f;
    const float RefreshSeconds = 0.2f;

    /// <summary>Foot of each pole on the ground (Godot axes, Y up).</summary>
    public IReadOnlyList<Vector3> Bases { get; init; } = Array.Empty<Vector3>();

    /// <summary>
    /// Lamps that already have a pole and a luminaire of their own (placed utility poles): the point under each
    /// luminaire and the way its pole is turned (degrees about Y). They get a lit plate, a glow and a share of the
    /// light pool like the others, but no pole, no head box and no collider.
    /// </summary>
    public IReadOnlyList<(Vector3 Position, float YawDeg)> PoleHeads { get; init; } = Array.Empty<(Vector3, float)>();

    /// <summary>The beam and lens numbers (environment.json <c>lamp</c>).</summary>
    public LampLook Look { get; init; } = new();

    /// <summary>Headless server: the pole colliders only, no visuals, light pool or per-frame work.</summary>
    public bool CollisionOnly { get; init; }

    /// <summary>The lamps of this map on this client (null on a server that builds none); the network client reads it to break and relight pole lamps (ADR 0037).</summary>
    public static StreetLamps? Current { get; private set; }

    Vector3[] _heads = Array.Empty<Vector3>();
    Num.Vector3[] _headsNum = Array.Empty<Num.Vector3>();
    readonly SpotLight3D[] _lights = new SpotLight3D[PoolSize];
    readonly int[] _slots = new int[PoolSize];
    readonly int[] _shown = new int[PoolSize];
    StandardMaterial3D _headMat = new();
    // pole lamps are shootable (ADR 0037): the server says when one breaks and when it flickers back; the flicker itself is local, a pure function of its age
    LampState[] _state = Array.Empty<LampState>();
    float[] _age = Array.Empty<float>();
    bool[] _off = Array.Empty<bool>(); // by head index: broken, so the pool never gives it a beam
    bool[] _dark = Array.Empty<bool>(); // by pole lamp: no lit lens and no beam right now (broken, or a dark step of its flicker)
    MultiMesh? _plates;
    Transform3D[] _plateXf = Array.Empty<Transform3D>();
    int _flickering;
    double _refreshIn;
    float _level = -1f;

    public override void _EnterTree() => Current = this;

    public override void _ExitTree()
    {
        if (Current == this) Current = null;
    }

    public override void _Ready()
    {
        if (Bases.Count == 0 && PoleHeads.Count == 0) { SetProcess(false); return; }
        if (CollisionOnly) { SetProcess(false); BuildCollision(); return; }
        // the light pool and the glow treat both kinds alike: the lamps of lamps.json first, then the pole lamps
        _heads = Bases.Select(b => b + Vector3.Up * (PoleHeight - 0.04f)).Concat(PoleHeads.Select(h => h.Position)).ToArray();
        _headsNum = _heads.Select(h => new Num.Vector3(h.X, h.Y, h.Z)).ToArray();
        _state = new LampState[PoleHeads.Count];
        _age = new float[PoleHeads.Count];
        _off = new bool[_heads.Length];
        _dark = new bool[PoleHeads.Count];
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
            LampPool.Refresh(_slots, _headsNum, new Num.Vector3(eye.X, eye.Y, eye.Z), PoolMaxDistance, _off);
        }
        if (_flickering > 0) StepFlicker((float)delta);

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
                ? LampPool.Energy(level, eyePos.DistanceTo(_heads[idx]), PoolMaxDistance) * (float)Look.Energy
                : 0f;
            // a flickering lamp snaps on and off; every other one fades
            var pole = idx - Bases.Count;
            if (idx >= 0 && pole >= 0 && _state[pole] == LampState.Flickering) light.LightEnergy = _dark[pole] ? 0f : target;
            else light.LightEnergy = Mathf.MoveToward(light.LightEnergy, target, (float)delta * LightRate);
        }
    }

    /// <summary>The server says pole lamp <paramref name="lamp"/> (its place in the placed objects' lamp list) is now in <paramref name="state"/>.</summary>
    public void SetPoleLamp(int lamp, LampState state)
    {
        if ((uint)lamp >= (uint)_state.Length || _state[lamp] == state) return;
        if (_state[lamp] == LampState.Flickering) _flickering--;
        _state[lamp] = state;
        _age[lamp] = 0f;
        if (state == LampState.Flickering) _flickering++;
        var head = Bases.Count + lamp;
        _off[head] = state == LampState.Broken;
        if (_off[head]) for (var s = 0; s < PoolSize; s++) if (_slots[s] == head) _slots[s] = -1; // its beam goes at once, not at the next refresh
        SetDark(lamp, state == LampState.Broken || (state == LampState.Flickering && !LampFlicker.On(lamp, 0f, (float)Look.FlickerSeconds)));
    }

    /// <summary>Every pole lamp not listed is lit (the join catch-up, a new round).</summary>
    public void SyncPoleLamps(IReadOnlyList<LampStateMsg> unlit)
    {
        for (var i = 0; i < _state.Length; i++) SetPoleLamp(i, LampState.Lit);
        foreach (var m in unlit) SetPoleLamp(m.Index, m.State);
    }

    void StepFlicker(float dt)
    {
        var seconds = (float)Look.FlickerSeconds;
        for (var i = 0; i < _state.Length; i++)
        {
            if (_state[i] != LampState.Flickering) continue;
            _age[i] += dt;
            if (_age[i] >= seconds) SetPoleLamp(i, LampState.Lit); // steady on; the server's Lit says the same
            else SetDark(i, !LampFlicker.On(i, _age[i], seconds));
        }
    }

    /// <summary>A dark lamp has no lit lens (its plate shrunk to nothing) and no beam.</summary>
    void SetDark(int lamp, bool dark)
    {
        if (_dark[lamp] == dark) return;
        _dark[lamp] = dark;
        _plates?.SetInstanceTransform(lamp, dark ? new Transform3D(Basis.FromScale(Vector3.Zero), _plateXf[lamp].Origin) : _plateXf[lamp]);
    }

    void ApplyLevel(float level)
    {
        _level = level;
        _headMat.EmissionEnergyMultiplier = 0.1f + (float)Look.LensEnergy * level;
    }

    void BuildVisuals()
    {
        var poleMat = new StandardMaterial3D { AlbedoColor = new Color(0.2f, 0.21f, 0.23f) };
        var poleMesh = new CylinderMesh { TopRadius = 0.05f, BottomRadius = 0.07f, Height = PoleHeight, RadialSegments = 6, Rings = 1 };
        AddChild(Batch(poleMesh, Bases.Select(b => new Transform3D(Basis.Identity, b + Vector3.Up * (PoleHeight / 2))), poleMat));

        _headMat = LampKit.Lens(new Color(Look.Colour), 0f);
        _headMat.AlbedoColor = new Color(0.55f, 0.55f, 0.52f);
        var headMesh = new BoxMesh { Size = new Vector3(0.42f, 0.08f, 0.22f) };
        AddChild(Batch(headMesh, _heads.Take(Bases.Count).Select(h => new Transform3D(Basis.Identity, h)), _headMat));
        if (PoleHeads.Count > 0)
        {
            // a thin lit plate under the luminaire of each pole lamp, turned with its pole (the arm reaches along the pole's +Z)
            var plate = new BoxMesh { Size = new Vector3(0.16f, 0.02f, 0.34f) };
            _plateXf = PoleHeads.Select(h => new Transform3D(new Basis(Vector3.Up, Mathf.DegToRad(h.YawDeg)), h.Position)).ToArray();
            var lit = Batch(plate, _plateXf, _headMat);
            _plates = lit.Multimesh;
            lit.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            AddChild(lit);
        }

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
            // a spot shines along its own -Z: tipped to shine straight down
            _lights[s] = LampKit.Beam(new Transform3D(new Basis(Vector3.Right, -Mathf.Pi / 2), Vector3.Zero), new Color(Look.Colour),
                (float)Look.RangeM, (float)Look.AngleDeg, 0f);
            _lights[s].Visible = true; // the pool slot is always on; its energy is what fades
            AddChild(_lights[s]);
        }
    }
}
