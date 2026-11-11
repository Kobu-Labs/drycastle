using Godot;
using Suchohrad.ShooterCore;

namespace Suchohrad.Shooter;

/// <summary>
/// Impact particles (EventKind.ImpactFx, tickets #152 and #154). The server picks the surface and the size, so every client
/// draws the same burst. A small pool of CPU particle emitters is reused round-robin, and so is a pool of shockwave rings
/// and of dust emitters: nothing is allocated per frame, and each piece goes idle after its one-shot lifetime (about 2 s).
/// Water impacts use the water splash (<see cref="WaterFx"/>) instead of a dry burst.
/// </summary>
public partial class NetClient
{
    const int ImpactPool = 8, RingPool = 3, DustPool = 3;
    const float ImpactLifeS = 2f;
    const int RingSegments = 24;
    const float RingInnerRadius = 0.85f;

    CpuParticles3D[]? _impacts;
    int _impactNext;
    MeshInstance3D[]? _rings;
    int _ringNext;
    CpuParticles3D[]? _dust;
    int _dustNext;
    ArrayMesh? _ringMesh;

    /// <summary>Surface colour and the burst's particle count and speed for a size (grass and earth for the terrain).</summary>
    static (Color Tint, int Amount, float Speed) ImpactLook(ImpactInfo i)
    {
        var tint = i.Surface switch
        {
            SurfaceKind.Road => new Color(0.45f, 0.45f, 0.46f),
            SurfaceKind.Building => new Color(0.72f, 0.66f, 0.52f),
            SurfaceKind.Water => new Color(0.92f, 0.96f, 1f),
            SurfaceKind.Metal => new Color(1f, 0.85f, 0.40f),
            SurfaceKind.Flesh => new Color(0.55f, 0.05f, 0.05f),
            SurfaceKind.Wood => new Color(0.46f, 0.31f, 0.17f),
            SurfaceKind.Foliage => new Color(0.24f, 0.40f, 0.15f),
            _ => new Color(0.36f, 0.30f, 0.18f), // grass and earth
        };
        return i.Size switch
        {
            ImpactSize.Puff => (tint, 10, 2.5f),
            ImpactSize.Burst => (tint, 26, 4.5f),
            _ => (tint, 60, 8f),
        };
    }

    /// <summary>The splash scale for a water impact: a bullet's small splash, a rocket's larger one, a plume's full splash.</summary>
    static float SplashSize(ImpactSize s) => s switch { ImpactSize.Puff => 0.4f, ImpactSize.Burst => 0.8f, _ => 1f };

    CpuParticles3D[] BuildImpactPool()
    {
        var pool = new CpuParticles3D[ImpactPool];
        var mat = new StandardMaterial3D { VertexColorUseAsAlbedo = true, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
        for (var k = 0; k < pool.Length; k++)
        {
            var p = new CpuParticles3D
            {
                Emitting = false, OneShot = true, Explosiveness = 1f, Lifetime = ImpactLifeS * 0.4f,
                Direction = Vector3.Up, Spread = 70f, Gravity = new Vector3(0, -9.8f, 0),
                Mesh = new BoxMesh { Size = new Vector3(0.12f, 0.12f, 0.12f), Material = mat },
            };
            World.AddChild(p);
            pool[k] = p;
        }
        return pool;
    }

    /// <summary>Dust that hangs and drifts up after a plume: soft, slow, larger than the debris.</summary>
    CpuParticles3D[] BuildDustPool()
    {
        var pool = new CpuParticles3D[DustPool];
        var mat = new StandardMaterial3D { VertexColorUseAsAlbedo = true, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha };
        for (var k = 0; k < pool.Length; k++)
        {
            var p = new CpuParticles3D
            {
                Emitting = false, OneShot = true, Explosiveness = 0.2f, Amount = 24, Lifetime = ImpactLifeS,
                Direction = Vector3.Up, Spread = 45f, InitialVelocityMin = 0.6f, InitialVelocityMax = 1.6f,
                Gravity = new Vector3(0, 0.8f, 0), ScaleAmountMin = 0.8f, ScaleAmountMax = 1.6f,
                Mesh = new SphereMesh { Radius = 0.45f, Height = 0.9f, RadialSegments = 6, Rings = 3, Material = mat },
            };
            World.AddChild(p);
            pool[k] = p;
        }
        return pool;
    }

    void OnImpactFx(ImpactInfo i)
    {
        if (World == null) return;
        var at = new Vector3(i.At.X, i.At.Y, i.At.Z);
        if (i.Surface == SurfaceKind.Water)
        {
            WaterFx.Splash(World, at, SplashSize(i.Size)); // the proper splash, scaled by the round (ticket #154)
            return;
        }
        _impacts ??= BuildImpactPool();
        var (tint, amount, speed) = ImpactLook(i);
        var p = _impacts[_impactNext];
        _impactNext = (_impactNext + 1) % _impacts.Length;
        p.GlobalPosition = at;
        p.Color = tint;
        p.Amount = amount;
        p.InitialVelocityMin = speed * 0.5f;
        p.InitialVelocityMax = speed;
        p.Restart();
        p.Emitting = true;
        if (i.Size != ImpactSize.Plume) return;
        ShockwaveRing(at);
        DustLift(at, tint);
    }

    /// <summary>A flat annulus on the ground that grows and fades out in about half a second; a pool of <see cref="RingPool"/> rings is reused.</summary>
    void ShockwaveRing(Vector3 at)
    {
        if (_rings == null)
        {
            _ringMesh = BuildRingMesh();
            _rings = new MeshInstance3D[RingPool];
            for (var k = 0; k < _rings.Length; k++)
            {
                var m = new MeshInstance3D
                {
                    Mesh = _ringMesh, Visible = false,
                    MaterialOverride = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, CullMode = BaseMaterial3D.CullModeEnum.Disabled, AlbedoColor = new Color(0.9f, 0.85f, 0.7f, 0f) },
                };
                World.AddChild(m);
                _rings[k] = m;
            }
        }
        var ring = _rings[_ringNext];
        _ringNext = (_ringNext + 1) % _rings.Length;
        var mat = (StandardMaterial3D)ring.MaterialOverride;
        ring.GlobalPosition = at + Vector3.Up * 0.1f;
        ring.Scale = new Vector3(0.5f, 1f, 0.5f);
        ring.Visible = true;
        var tween = CreateTween();
        tween.SetParallel();
        tween.TweenProperty(ring, "scale", new Vector3(10f, 1f, 10f), 0.5f);
        tween.TweenMethod(Callable.From<float>(a => mat.AlbedoColor = new Color(0.9f, 0.85f, 0.7f, a)), 0.6f, 0f, 0.5f);
        tween.Chain().TweenCallback(Callable.From(() => { if (ring.IsInsideTree()) ring.Visible = false; }));
    }

    /// <summary>A flat ring of 24 segments (inner radius <see cref="RingInnerRadius"/>, outer 1), built once and shared by the pool.</summary>
    static ArrayMesh BuildRingMesh()
    {
        var verts = new Vector3[RingSegments * 2];
        var index = new int[RingSegments * 6];
        for (var s = 0; s < RingSegments; s++)
        {
            var a = Mathf.Tau * s / RingSegments;
            var c = Mathf.Cos(a); var n = Mathf.Sin(a);
            verts[2 * s] = new Vector3(c * RingInnerRadius, 0, n * RingInnerRadius);
            verts[2 * s + 1] = new Vector3(c, 0, n);
            var next = (s + 1) % RingSegments;
            var i0 = 2 * s; var i1 = 2 * s + 1; var i2 = 2 * next; var i3 = 2 * next + 1;
            index[6 * s] = i0; index[6 * s + 1] = i1; index[6 * s + 2] = i3;
            index[6 * s + 3] = i0; index[6 * s + 4] = i3; index[6 * s + 5] = i2;
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts;
        arrays[(int)Mesh.ArrayType.Index] = index;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    /// <summary>Dust lifted by a plume, from the dust pool (one emitter per plume, reused round-robin).</summary>
    void DustLift(Vector3 at, Color tint)
    {
        _dust ??= BuildDustPool();
        var d = _dust[_dustNext];
        _dustNext = (_dustNext + 1) % _dust.Length;
        d.GlobalPosition = at + Vector3.Up * 0.3f;
        d.Color = tint.Lerp(new Color(0.6f, 0.56f, 0.5f), 0.6f);
        d.Restart();
        d.Emitting = true;
    }
}
