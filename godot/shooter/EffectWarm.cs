using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// Draws every pooled effect once during the loading screen so the engine compiles their pipelines before play (#457): the first shot, explosion, backblast,
/// casing, bullet hole, muzzle flash, flak burst and splash of a match would otherwise stall a frame each. They are drawn in a small SubViewport with its own
/// world, camera and shadow light (same anti-aliasing as the main view), so nothing shows to the player; the viewport is freed afterwards. The statics the
/// effects keep (meshes, materials, ramps, the RocketFx cache, the noise textures) stay built; the pooled nodes are rebuilt under the real world at first use.
/// </summary>
public static class EffectWarm
{
    /// <summary>Frames the viewport is drawn for: pooled nodes are made on the first, drawn from the second, the engine's pipeline work finishes after.</summary>
    public const int Frames = 4;

    /// <summary>Builds the warm-up world under <paramref name="host"/>; <see cref="Finish"/> frees it after <see cref="Frames"/> frames. Null on a headless run.</summary>
    public static SubViewport? Begin(Node host)
    {
        if (!ShotFx.Enabled) return null;
        var main = host.GetViewport();
        var vp = new SubViewport
        {
            Name = "EffectWarm", Size = new Vector2I(96, 96), OwnWorld3D = true, RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            Msaa3D = main.Msaa3D, ScreenSpaceAA = main.ScreenSpaceAA, UseTaa = main.UseTaa,
        };
        var root = new Node3D { Name = "World" };
        vp.AddChild(root);
        host.AddChild(vp);

        root.AddChild(new Camera3D { Current = true, Far = 300f });
        root.AddChild(new DirectionalLight3D { ShadowEnabled = true, RotationDegrees = new Vector3(-50, 30, 0) });
        // a floor for the dust and casing rays, which look for the ground below a blast
        var floor = new StaticBody3D { CollisionLayer = CollisionLayers.World, Position = new Vector3(0, -1f, -6) };
        floor.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(40, 1, 40) }, Position = new Vector3(0, -0.5f, 0) });
        root.AddChild(floor);

        var ahead = new Vector3(0, 0, -6);
        ShotFx.Spawn(root, new Vector3(0.2f, 0, -1), ahead, false);                       // tracer, spark burst
        ShotFx.Spark(root, ahead, Vector3.Up);
        RocketFx.Explode(root, new Vector3(0, -0.8f, -8), 2f);                           // the blast kit's meshes, light and four emitters, the dust
        RocketFx.Backblast(root, new Vector3(0.5f, 0, -3), Vector3.Forward);
        RocketFx.Make(root);                                                                // rocket body, flame, light, smoke trail
        FlakFx.Burst(root, new Vector3(-1, 1, -10), 2f);
        WaterFx.Footstep(root, new Vector3(1, -0.5f, -5));
        CasingFx.Eject(root, new Vector3(0, -0.5f, -3), Vector3.Forward, CasingKind.Heavy);
        BulletHoles.Place(root, new Vector3(0, -1f, -5), Vector3.Up);
        var flash = new MuzzleFlash { Position = new Vector3(0, 0, -2) };
        root.AddChild(flash);
        flash.Flash();
        flash.SetProcess(false); // stays up for the warm-up frames
        ImpactParticles(root);
        return vp;
    }

    /// <summary>The impact debris and dust of <c>NetClient.Impacts</c>: its pools are made on the first impact, so the same material variants are drawn here.</summary>
    static void ImpactParticles(Node3D root)
    {
        var debris = new StandardMaterial3D { VertexColorUseAsAlbedo = true, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
        var dust = new StandardMaterial3D { VertexColorUseAsAlbedo = true, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha };
        Add(root, new BoxMesh { Size = new Vector3(0.12f, 0.12f, 0.12f), Material = debris }, 12, 1f, new Vector3(-0.5f, 0, -4));
        Add(root, new SphereMesh { Radius = 0.45f, Height = 0.9f, RadialSegments = 6, Rings = 3, Material = dust }, 24, 0.2f, new Vector3(0.5f, 0, -4));
    }

    static void Add(Node3D root, Mesh mesh, int amount, float explosive, Vector3 at)
    {
        var p = new CpuParticles3D { Emitting = false, Visible = false, OneShot = true, Explosiveness = explosive, Amount = amount, Lifetime = 2.0, Mesh = mesh, Direction = Vector3.Up, Spread = 45f, InitialVelocityMin = 1f, InitialVelocityMax = 2f, Position = at };
        root.AddChild(p);
        ParticleFx.Fire(p);
    }

    /// <summary>Frees the warm-up world once it has been drawn.</summary>
    public static void Finish(SubViewport? vp)
    {
        if (vp != null && GodotObject.IsInstanceValid(vp)) vp.QueueFree();
    }
}
