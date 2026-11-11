using Godot;

namespace Suchohrad.Shooter;

/// <summary>
/// Shot effects: a short bright tracer streak that flies from the muzzle to the end point at 250 m/s, a muzzle flash
/// (sphere plus a brief light) at the muzzle, and a small spark burst where a shot ends in the world. Meshes and
/// materials are built once and shared. Skipped on headless runs.
/// </summary>
public static class ShotFx
{
    public static bool Enabled => DisplayServer.GetName() != "headless";

    const float TracerSpeed = 250f, TracerLength = 4.5f, MaxShotRange = 300f;

    static BoxMesh? _tracerMesh;
    static SphereMesh? _flashMesh, _sparkMesh;
    static StandardMaterial3D? _tracerMat, _flashMat, _sparkMat;

    static StandardMaterial3D Glow(Color c) => new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = c,
        EmissionEnabled = true, Emission = c, EmissionEnergyMultiplier = 2f,
    };

    static void Init()
    {
        if (_tracerMesh != null) return;
        _tracerMat = Glow(new Color(1f, 0.92f, 0.55f));
        _flashMat = Glow(new Color(1f, 0.75f, 0.3f));
        _sparkMat = Glow(new Color(1f, 0.8f, 0.4f));
        _tracerMesh = new BoxMesh { Size = new Vector3(0.03f, 0.03f, 1f), Material = _tracerMat };
        _flashMesh = new SphereMesh { Radius = 0.12f, Height = 0.24f, RadialSegments = 12, Rings = 6, Material = _flashMat };
        _sparkMesh = new SphereMesh { Radius = 0.025f, Height = 0.05f, RadialSegments = 6, Rings = 3, Material = _sparkMat };
    }

    /// <summary>Shot from the muzzle <paramref name="from"/> to <paramref name="to"/>. Melee swings have no effect here.</summary>
    public static void Spawn(Node3D world, Vector3 from, Vector3 to, bool melee, bool impact = true, float flashScale = 1f)
    {
        if (!Enabled || melee || !GodotObject.IsInstanceValid(world) || !world.IsInsideTree()) return;
        var dir = to - from;
        var dist = dir.Length();
        if (dist < 0.05f) return;
        Init();
        dir /= dist;
        Flash(world, from, flashScale);
        Tracer(world, from, to, dir, dist);
        if (impact && dist < MaxShotRange - 1f) Impact(world, to, -dir);
    }

    static void Flash(Node3D world, Vector3 at, float scale)
    {
        var flash = new MeshInstance3D { Mesh = _flashMesh, Position = at, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Scale = Vector3.One * 1.2f * scale };
        var light = new OmniLight3D { LightColor = new Color(1f, 0.7f, 0.3f), LightEnergy = 3f, OmniRange = 7f, ShadowEnabled = false };
        flash.AddChild(light);
        world.AddChild(flash);
        Expire(world, flash, 0.06f);
    }

    static void Tracer(Node3D world, Vector3 from, Vector3 to, Vector3 dir, float dist)
    {
        var len = Mathf.Min(TracerLength, dist);
        var pivot = new Node3D();
        pivot.AddChild(new MeshInstance3D { Mesh = _tracerMesh, Scale = new Vector3(1, 1, len), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        world.AddChild(pivot);
        var up = Mathf.Abs(dir.Y) > 0.99f ? Vector3.Right : Vector3.Up;
        // the streak's tail starts at the muzzle and its head ends at the end point; it travels the distance minus its own length
        var start = from + dir * (len * 0.5f);
        var end = to - dir * (len * 0.5f);
        pivot.LookAtFromPosition(start, start + dir, up);
        var tw = pivot.CreateTween();
        tw.TweenProperty(pivot, "position", end, Mathf.Max(0.03f, (dist - len) / TracerSpeed));
        tw.TweenCallback(Callable.From(pivot.QueueFree));
    }

    /// <summary>A puff of about 8 sparks thrown back along <paramref name="normal"/>, gone after 0.3 s.</summary>
    static void Impact(Node3D world, Vector3 at, Vector3 normal)
    {
        var p = new CpuParticles3D
        {
            Mesh = _sparkMesh, Amount = 8, Lifetime = 0.3, OneShot = true, Explosiveness = 1f, Emitting = true,
            Direction = normal, Spread = 55f, InitialVelocityMin = 1.5f, InitialVelocityMax = 4.5f,
            Gravity = new Vector3(0, -9f, 0), Position = at, LocalCoords = false,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        world.AddChild(p);
        Expire(world, p, 0.5f);
    }

    /// <summary>Frees <paramref name="n"/> after <paramref name="seconds"/>.</summary>
    public static void Expire(Node3D world, Node n, float seconds) => world.GetTree().CreateTimer(seconds).Timeout += () => { if (GodotObject.IsInstanceValid(n)) n.QueueFree(); };
}
