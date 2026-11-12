using Godot;
using Drycastle.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter;

/// <summary>
/// Shot effects: a short bright tracer streak that flies from the muzzle to the end point at 250 m/s, and a small spark burst where a shot ends in the world. (Muzzle flashes live on the weapon models, see MuzzleFlash.)
/// Meshes and materials are built once and shared. Tracers and sparks come from fixed pools reused round-robin (#169), and an effect whose span is out of range of the camera is not built (<see cref="EffectCull"/>). Skipped on headless runs.
/// </summary>
public static class ShotFx
{
    static bool? _enabled;
    /// <summary>Headless runs build no effects. The display server does not change while the game runs, so its name is read once.</summary>
    public static bool Enabled => _enabled ??= DisplayServer.GetName() != "headless";

    const float TracerSpeed = 250f, TracerLength = 4.5f, MaxShotRange = 300f;
    const int TracerPool = 32, SparkPool = 16, SparkAmount = 8;

    static BoxMesh? _tracerMesh;
    static SphereMesh? _sparkMesh;
    static StandardMaterial3D? _tracerMat, _sparkMat;

    // the pools hang under one node in the world they were built for; a new world (a new match) builds fresh ones
    static Node3D? _poolRoot, _poolWorld;
    static Node3D[]? _tracers;
    static Tween?[]? _tracerTweens;
    static int _tracerNext;
    static CpuParticles3D[]? _sparks;
    static int _sparkNext;

    static StandardMaterial3D Glow(Color c) => new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = c,
        EmissionEnabled = true, Emission = c, EmissionEnergyMultiplier = 2f,
    };

    static void Init()
    {
        if (_tracerMesh != null) return;
        _tracerMat = Glow(new Color(1f, 0.92f, 0.55f));
        _sparkMat = Glow(new Color(1f, 0.8f, 0.4f));
        _tracerMesh = new BoxMesh { Size = new Vector3(0.03f, 0.03f, 1f), Material = _tracerMat };
        _sparkMesh = new SphereMesh { Radius = 0.025f, Height = 0.05f, RadialSegments = 6, Rings = 3, Material = _sparkMat };
    }

    /// <summary>Builds the pools under <paramref name="world"/>, once per world.</summary>
    static void EnsurePools(Node3D world)
    {
        if (_poolRoot != null && GodotObject.IsInstanceValid(_poolRoot) && _poolWorld == world) return;
        _poolRoot = new Node3D { Name = "ShotFxPool" };
        world.AddChild(_poolRoot);
        _poolWorld = world;
        _tracers = new Node3D[TracerPool];
        _tracerTweens = new Tween?[TracerPool];
        for (var k = 0; k < TracerPool; k++)
        {
            var pivot = new Node3D { Visible = false };
            pivot.AddChild(new MeshInstance3D { Mesh = _tracerMesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
            _poolRoot.AddChild(pivot);
            _tracers[k] = pivot;
        }
        _sparks = new CpuParticles3D[SparkPool];
        for (var k = 0; k < SparkPool; k++)
        {
            var p = new CpuParticles3D
            {
                Emitting = false, Visible = false, Mesh = _sparkMesh, Amount = SparkAmount, Lifetime = 0.3, OneShot = true, Explosiveness = 1f,
                Spread = 55f, InitialVelocityMin = 1.5f, InitialVelocityMax = 4.5f, Gravity = new Vector3(0, -9f, 0),
                LocalCoords = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            _poolRoot.AddChild(p);
            _sparks[k] = p;
        }
    }

    /// <summary>True when the span is close enough to the camera to be seen (no camera: built anyway).</summary>
    static bool InRange(Node3D world, Vector3 a, Vector3 b)
    {
        var cam = world.GetViewport()?.GetCamera3D();
        return cam == null || EffectCull.Near(Nv(cam.GlobalPosition), Nv(a), Nv(b));
    }

    static NVec3 Nv(Vector3 v) => new(v.X, v.Y, v.Z);

    /// <summary>Shot from the muzzle <paramref name="from"/> to <paramref name="to"/>. Melee swings have no effect here.</summary>
    /// <paramref name="speed"/> and <paramref name="length"/> set the tracer's flight (m/s) and streak (m) for a weapon that has its own (the gunship, #160); null keeps the generic ones.</summary>
    public static void Spawn(Node3D world, Vector3 from, Vector3 to, bool melee, bool impact = true, Vector3? normal = null, float? speed = null, float? length = null, float width = 1f)
    {
        if (!Enabled || melee || !GodotObject.IsInstanceValid(world) || !world.IsInsideTree()) return;
        var dir = to - from;
        var dist = dir.Length();
        if (dist < 0.05f) return;
        Init();
        if (!InRange(world, from, to)) return;
        EnsurePools(world);
        dir /= dist;
        Tracer(from, to, dir, dist, speed ?? TracerSpeed, length ?? TracerLength, width);
        if (impact && dist < MaxShotRange - 1f) Burst(to, normal ?? -dir);
    }

    static void Tracer(Vector3 from, Vector3 to, Vector3 dir, float dist, float speed, float length, float width)
    {
        var len = Mathf.Min(length, dist);
        var k = _tracerNext;
        _tracerNext = (_tracerNext + 1) % _tracers!.Length;
        _tracerTweens![k]?.Kill(); // a tracer still in flight in this slot is taken over by the new shot
        var pivot = _tracers[k];
        var up = Mathf.Abs(dir.Y) > 0.99f ? Vector3.Right : Vector3.Up;
        // the streak's tail starts at the muzzle and its head ends at the end point; it travels the distance minus its own length
        var start = from + dir * (len * 0.5f);
        var end = to - dir * (len * 0.5f);
        pivot.LookAtFromPosition(start, start + dir, up);
        pivot.Scale = new Vector3(width, width, len); // look_at resets the basis, so the streak's length (and the width, a multiple of the 3 cm mesh) is set after it
        pivot.Visible = true;
        var tw = pivot.CreateTween();
        _tracerTweens[k] = tw;
        tw.TweenProperty(pivot, "position", end, Mathf.Max(0.03f, (dist - len) / speed));
        tw.TweenCallback(Callable.From(() => pivot.Visible = false));
    }

    /// <summary>A puff of about 8 sparks thrown back along <paramref name="normal"/>, from the spark pool (gone after 0.3 s).</summary>
    static void Burst(Vector3 at, Vector3 normal)
    {
        var p = _sparks![_sparkNext];
        _sparkNext = (_sparkNext + 1) % _sparks.Length;
        p.Direction = normal;
        p.GlobalPosition = at;
        ParticleFx.Fire(p);
    }

    /// <summary>Spark burst only (no tracer), for shotgun pellets that each end in the world.</summary>
    public static void Spark(Node3D world, Vector3 at, Vector3 normal)
    {
        if (!Enabled || !GodotObject.IsInstanceValid(world) || !world.IsInsideTree()) return;
        Init();
        if (!InRange(world, at, at)) return;
        EnsurePools(world);
        Burst(at, normal);
    }

    /// <summary>Frees <paramref name="n"/> after <paramref name="seconds"/>.</summary>
    public static void Expire(Node3D world, Node n, float seconds) => world.GetTree().CreateTimer(seconds).Timeout += () => { if (GodotObject.IsInstanceValid(n)) n.QueueFree(); };
}
