using Godot;

namespace Suchohrad.Shooter;

/// <summary>
/// The airburst of a flak shell: the black smoke ball that anti-aircraft fire leaves in the sky. A short orange-white flash at the burst, then a
/// clump of near-black puffs that swell, hang, drift up a little and thin out over about 4 s. Meshes and materials are built once and shared; the
/// puffs are tweened (no particles). Skipped on headless runs. The damage reach of a burst is the server's (vehicles.json flak burst_radius), this
/// is only its look: <c>puff_radius</c> is about the size of the whole cloud.
/// </summary>
public static class FlakFx
{
    const int Puffs = 9;
    const float Life = 4f;

    static SphereMesh? _ball;
    static StandardMaterial3D? _flashMat, _smokeMat;

    static void Init()
    {
        if (_ball != null) return;
        _ball = new SphereMesh { Radius = 0.5f, Height = 1f, RadialSegments = 10, Rings = 6 };
        _flashMat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            AlbedoColor = new Color(1f, 0.78f, 0.4f, 1f), EmissionEnabled = true, Emission = new Color(1f, 0.6f, 0.2f), EmissionEnergyMultiplier = 3f,
        };
        _smokeMat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.PerVertex, Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            AlbedoColor = new Color(0.05f, 0.05f, 0.055f, 0.92f), Roughness = 1f,
        };
    }

    /// <summary>A flak burst of about <paramref name="radius"/> metres at <paramref name="at"/> in <paramref name="world"/>.</summary>
    public static void Burst(Node3D world, Vector3 at, float radius)
    {
        if (!ShotFx.Enabled || !GodotObject.IsInstanceValid(world) || !world.IsInsideTree()) return;
        Init();
        var off = GeometryInstance3D.ShadowCastingSetting.Off;
        var root = new Node3D { Position = at };
        world.AddChild(root);
        var r = Mathf.Max(2f, radius);
        var rng = new RandomNumberGenerator();
        rng.Randomize();

        // the flash: white-orange, gone in 0.15 s, with a light that dies at once
        var flash = new MeshInstance3D { Mesh = _ball, MaterialOverride = _flashMat, Scale = Vector3.One * r * 0.2f, CastShadow = off };
        root.AddChild(flash);
        var tf = flash.CreateTween().SetParallel();
        tf.TweenProperty(flash, "scale", Vector3.One * r * 0.7f, 0.15f).SetEase(Tween.EaseType.Out);
        tf.TweenProperty(flash, "transparency", 1f, 0.15f);
        var light = new OmniLight3D { LightColor = new Color(1f, 0.6f, 0.25f), LightEnergy = 6f, OmniRange = r * 2.5f, ShadowEnabled = false };
        root.AddChild(light);
        light.CreateTween().TweenProperty(light, "light_energy", 0f, 0.2f);

        // the smoke: a clump of black puffs, each swelling to its own size and drifting up as it thins; they are separate nodes so each can have its own transparency
        for (var k = 0; k < Puffs; k++)
        {
            var dir = new Vector3(rng.RandfRange(-1f, 1f), rng.RandfRange(-0.7f, 1f), rng.RandfRange(-1f, 1f)).Normalized();
            var offset = dir * r * rng.RandfRange(0.0f, 0.32f);
            var size = r * rng.RandfRange(0.32f, 0.52f);
            var delay = rng.RandfRange(0f, 0.12f);
            var puff = new MeshInstance3D { Mesh = _ball, MaterialOverride = (Material)_smokeMat!.Duplicate(), Scale = Vector3.One * size * 0.15f, Position = offset * 0.3f, CastShadow = off };
            root.AddChild(puff);
            var grow = puff.CreateTween().SetParallel();
            grow.TweenProperty(puff, "scale", Vector3.One * size, 0.5f).SetDelay(delay).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Cubic);
            grow.TweenProperty(puff, "position", offset + new Vector3(0, r * 0.25f, 0), Life).SetDelay(delay);
            grow.TweenProperty(puff, "transparency", 1f, Life * 0.55f).SetDelay(delay + Life * 0.45f);
        }
        ShotFx.Expire(world, root, Life + 0.3f);
    }
}
