using Godot;
using Suchohrad.ShooterCore;

namespace Suchohrad.Shooter;

/// <summary>
/// Impact particles for gunship rounds and shells (EventKind.ImpactFx, ticket #152). The server picks the surface and the
/// size, so every client draws the same burst. A small pool of CPU particle emitters is reused round-robin (no per-frame
/// allocation); each burst runs its one-shot lifetime (about 2 s) and then stays idle until reused. A plume also raises a
/// flat shockwave ring that grows and fades.
/// </summary>
public partial class NetClient
{
    const int ImpactPool = 8;
    const float ImpactLifeS = 2f;

    CpuParticles3D[]? _impacts;
    int _impactNext;
    MeshInstance3D? _ring;

    /// <summary>Surface colour (linear, sRGB-ish) and the burst's particle count and speed for a size.</summary>
    static (Color Tint, int Amount, float Speed) ImpactLook(ImpactInfo i)
    {
        var tint = i.Surface switch
        {
            SurfaceKind.Road => new Color(0.45f, 0.45f, 0.46f),
            SurfaceKind.Building => new Color(0.72f, 0.66f, 0.52f),
            SurfaceKind.Water => new Color(0.92f, 0.96f, 1f),
            SurfaceKind.Metal => new Color(1f, 0.85f, 0.40f),
            SurfaceKind.Flesh => new Color(0.55f, 0.05f, 0.05f),
            _ => new Color(0.36f, 0.30f, 0.18f), // grass and earth
        };
        return i.Size switch
        {
            ImpactSize.Puff => (tint, 10, 2.5f),
            ImpactSize.Burst => (tint, 26, 4.5f),
            _ => (tint, 60, 8f),
        };
    }

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

    void OnImpactFx(ImpactInfo i)
    {
        if (World == null) return;
        _impacts ??= BuildImpactPool();
        var (tint, amount, speed) = ImpactLook(i);
        var p = _impacts[_impactNext];
        _impactNext = (_impactNext + 1) % _impacts.Length;
        p.GlobalPosition = new Vector3(i.At.X, i.At.Y, i.At.Z);
        p.Color = tint;
        p.Amount = amount;
        p.InitialVelocityMin = speed * 0.5f;
        p.InitialVelocityMax = speed;
        p.Restart();
        p.Emitting = true;
        if (i.Size == ImpactSize.Plume) ShockwaveRing(p.GlobalPosition);
    }

    /// <summary>A flat ring on the ground that grows and fades in about half a second (one ring, reused).</summary>
    void ShockwaveRing(Vector3 at)
    {
        if (_ring == null)
        {
            _ring = new MeshInstance3D
            {
                Mesh = new CylinderMesh { TopRadius = 1f, BottomRadius = 1f, Height = 0.05f },
                MaterialOverride = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, AlbedoColor = new Color(0.9f, 0.85f, 0.7f, 0.6f) },
            };
            World.AddChild(_ring);
        }
        _ring.GlobalPosition = at + Vector3.Up * 0.1f;
        _ring.Scale = new Vector3(0.5f, 1f, 0.5f);
        _ring.Visible = true;
        var tween = CreateTween();
        tween.TweenProperty(_ring, "scale", new Vector3(10f, 1f, 10f), 0.5f);
        tween.TweenCallback(Callable.From(() => { if (_ring != null) _ring.Visible = false; }));
    }
}
