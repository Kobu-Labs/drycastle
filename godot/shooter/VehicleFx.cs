using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// Vehicle damage and blast visuals (ticket #103). Damage stages are particle emitters at the hood: 1 a little light gray smoke, 2 a bigger, much darker cloud,
/// 3 the dark cloud plus sparks flying out. The blast is the bazooka's (<see cref="RocketFx.Explode"/>: flash, fireball, shockwave, fire/spark/smoke/debris, light)
/// scaled by the vehicle blast radius, plus chunks of bodywork and a burning spot with a black smoke column that lingers a few seconds. Meshes, materials and
/// ramps come from the rocket cache; skipped on headless runs.
/// </summary>
public static partial class VehicleFx
{
    sealed class Cache
    {
        public readonly Gradient Light = RocketFx.Ramp((0f, new Color(0.78f, 0.78f, 0.78f, 0.55f)), (0.5f, new Color(0.7f, 0.7f, 0.7f, 0.35f)), (1f, new Color(0.6f, 0.6f, 0.6f, 0f)));
        public readonly Gradient Dark = RocketFx.Ramp((0f, new Color(0.16f, 0.15f, 0.14f, 0.9f)), (0.5f, new Color(0.1f, 0.1f, 0.1f, 0.75f)), (1f, new Color(0.05f, 0.05f, 0.05f, 0f)));
        public readonly Gradient Black = RocketFx.Ramp((0f, new Color(0.1f, 0.09f, 0.08f, 0.9f)), (0.6f, new Color(0.04f, 0.04f, 0.04f, 0.8f)), (1f, new Color(0.02f, 0.02f, 0.02f, 0f)));
        public readonly Curve Grow = RocketFx.Curve2(0.5f, 2.2f);
        public readonly BoxMesh Chunk = new() { Size = new Vector3(0.55f, 0.2f, 0.4f), Material = new StandardMaterial3D { AlbedoColor = new Color(0.16f, 0.16f, 0.17f), Roughness = 0.8f, Metallic = 0.4f } };
    }

    static Cache? _c;
    static Cache C => _c ??= new Cache();

    static CpuParticles3D Stream(Node3D parent, Mesh mesh, Gradient ramp, Curve? curve)
    {
        var p = new CpuParticles3D
        {
            Mesh = mesh, ColorRamp = ramp, ScaleAmountCurve = curve, LocalCoords = false, OneShot = false, Emitting = false,
            Direction = Vector3.Up, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            AngularVelocityMin = -90, AngularVelocityMax = 90,
        };
        parent.AddChild(p);
        return p;
    }

    /// <summary>The damage emitters of one vehicle, parked at the hood; <see cref="Set"/> picks the stage, <see cref="Near"/> switches them off for far vehicles.</summary>
    public sealed partial class Damage : Node3D
    {
        CpuParticles3D _smoke = null!, _sparks = null!;
        int _stage;
        bool _near = true;

        public static Damage Make(Node3D car, VehicleDef def) => MakeAt(car, new Vector3(def.Hood[0], def.Hood[1], def.Hood[2]));

        /// <summary>The same emitters at any point of a parent (the sentry turret's top, for one).</summary>
        public static Damage MakeAt(Node3D parent, Vector3 at)
        {
            var d = new Damage { Name = "Damage", Position = at };
            var car = parent;
            car.AddChild(d);
            var rc = RocketFx.C;
            d._smoke = Stream(d, rc.Smoke, C.Light, C.Grow);
            d._sparks = Stream(d, rc.Spark, rc.SparkRamp, rc.Shrink);
            d._sparks.Spread = 70f; d._sparks.Amount = 14; d._sparks.Lifetime = 0.7; d._sparks.Randomness = 1f;
            d._sparks.InitialVelocityMin = 2.5f; d._sparks.InitialVelocityMax = 5.5f; d._sparks.Gravity = new Vector3(0, -9.8f, 0);
            d._sparks.ScaleAmountMin = 2.5f; d._sparks.ScaleAmountMax = 4.5f;
            return d;
        }

        public void Set(int stage)
        {
            if (stage == _stage) return;
            _stage = stage;
            switch (stage)
            {
                case 1: Configure(_smoke, C.Light, 14, 1.8, 0.2f, 0.4f, 0.6f, 1.2f, 25f, 0.6f); break;
                case >= 2: Configure(_smoke, C.Dark, stage == 2 ? 34 : 44, 2.8, 0.5f, 0.9f, 1.0f, 2.2f, 30f, 0.9f); break;
            }
            Refresh();
        }

        public void Near(bool near) { if (near == _near) return; _near = near; Refresh(); }

        void Refresh()
        {
            _smoke.Emitting = _near && _stage >= 1;
            _sparks.Emitting = _near && _stage >= 3;
        }

        /// <summary>The vehicle is gone: stop and let the remaining particles die.</summary>
        public void Stop() { _stage = 0; _smoke.Emitting = false; _sparks.Emitting = false; }

        static void Configure(CpuParticles3D p, Gradient ramp, int amount, double life, float sMin, float sMax, float vMin, float vMax, float spread, float rise)
        {
            p.ColorRamp = ramp; p.Amount = amount; p.Lifetime = life;
            p.ScaleAmountMin = sMin * 1.2f; p.ScaleAmountMax = sMax * 1.6f;
            p.InitialVelocityMin = vMin; p.InitialVelocityMax = vMax; p.Spread = spread; p.Gravity = new Vector3(0, rise, 0);
        }
    }

    /// <summary>The blast of a destroyed vehicle at <paramref name="at"/>: the bazooka blast at the vehicle's radius, bodywork chunks, then a burning spot with black smoke for a few seconds.</summary>
    public static void Explode(Node3D world, Vector3 at, float radius)
    {
        if (!ShotFx.Enabled || !GodotObject.IsInstanceValid(world) || !world.IsInsideTree()) return;
        RocketFx.Explode(world, at, radius);
        var rc = RocketFx.C;
        var r = Mathf.Max(1f, radius);
        var root = new Node3D { Position = at };
        world.AddChild(root);

        var chunks = new CpuParticles3D
        {
            Mesh = C.Chunk, Amount = 9, Lifetime = 1.6, OneShot = true, Explosiveness = 1f, Emitting = true, LocalCoords = false,
            Direction = Vector3.Up, Spread = 75, InitialVelocityMin = r * 0.7f, InitialVelocityMax = r * 1.5f, Gravity = new Vector3(0, -14f, 0),
            ScaleAmountMin = 0.6f, ScaleAmountMax = 1.5f, AngularVelocityMin = -360, AngularVelocityMax = 360, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        root.AddChild(chunks);

        var fire = Stream(root, rc.Fire, rc.FireRamp, rc.Grow);
        fire.Amount = 18; fire.Lifetime = 0.9; fire.Spread = 25; fire.InitialVelocityMin = 1f; fire.InitialVelocityMax = 2.5f;
        fire.Gravity = new Vector3(0, 1.5f, 0); fire.ScaleAmountMin = 0.8f; fire.ScaleAmountMax = 1.4f; fire.Emitting = true;
        var column = Stream(root, rc.Smoke, C.Black, C.Grow);
        column.Amount = 26; column.Lifetime = VehicleBlastFx.ColumnLife; column.Spread = 12; column.InitialVelocityMin = 1.5f; column.InitialVelocityMax = 3f;
        column.Gravity = new Vector3(0, 0.3f, 0); column.ScaleAmountMin = 1.0f; column.ScaleAmountMax = 1.8f; column.Emitting = true;

        var light = new OmniLight3D { LightColor = new Color(1f, 0.5f, 0.15f), LightEnergy = 3f, OmniRange = r * 1.6f, ShadowEnabled = false, Position = new Vector3(0, 0.6f, 0) };
        root.AddChild(light);
        var tl = light.CreateTween();
        tl.TweenProperty(light, "light_energy", 0f, VehicleBlastFx.FreeAfter - 0.4f).SetDelay(0.4f);

        var tree = world.GetTree();
        tree.CreateTimer(VehicleBlastFx.EmitSeconds).Timeout += () =>
        {
            if (GodotObject.IsInstanceValid(fire)) fire.Emitting = false;
            if (GodotObject.IsInstanceValid(column)) column.Emitting = false;
        };
        ShotFx.Expire(world, root, VehicleBlastFx.FreeAfter); // the whole blast is freed here, whatever is still in the air
    }
}
