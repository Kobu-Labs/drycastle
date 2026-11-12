using Godot;
using Suchohrad.ShooterCore;

namespace Suchohrad.Shooter;

/// <summary>
/// What a flying vehicle shows and sounds like beyond its attitude: the rotors turn at the rotor speed of its flight record (they spool up when a
/// pilot sits down and run down on the ground without one), a little faster and louder under load (climbing, tilted hard); at speed a faint disc
/// stands in for the blur of the blades; the generated <see cref="RotorSound"/> follows the same two numbers; and in a low hover the downwash
/// throws up dust, or spray over a water body. All of it is the client's own: nothing here is on the wire beyond the flight record.
/// </summary>
public partial class NetClient
{
    sealed class RotorFx
    {
        /// <summary>Main rotor turns a second at full speed (two blades: twice that is the blade beat), and the tail rotor's.</summary>
        const float MainRps = 5.2f, TailRps = 21f;
        /// <summary>The blades' sweep, for the blur discs, metres (the model's: main and tail).</summary>
        const float MainRadius = 6.7f, TailRadius = 1.25f;
        /// <summary>Below this height over the ground the downwash raises dust, and how far around the helicopter it is drawn at all, metres.</summary>
        public const float WashHeight = 11f, WashRange = 140f;
        const float SoundRange = 450f;

        Node3D _root = null!;
        Node3D? _main, _tail;
        MeshInstance3D? _mainBlur, _tailBlur;
        StandardMaterial3D? _blurMat;
        RotorSound? _sound;
        CpuParticles3D? _wash;
        float _load = 0.5f;

        static readonly Color DustTint = new(0.62f, 0.57f, 0.5f), SprayTint = new(0.88f, 0.94f, 1f);

        public static RotorFx? Find(Node3D root)
        {
            var main = root.FindChild("rotor_main", true, false) as Node3D;
            var tail = root.FindChild("rotor_tail", true, false) as Node3D;
            if (main == null && tail == null) return null;
            var fx = new RotorFx { _root = root, _main = main, _tail = tail };
            fx._blurMat = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                AlbedoColor = new Color(0.07f, 0.07f, 0.07f, 0f), CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            };
            // the discs hang on the rotors' parent, at the hubs: they do not turn, the blades inside them do
            if (main?.GetParent() is Node3D mp) fx._mainBlur = Disc(mp, main.Position, MainRadius, Vector3.Zero, fx._blurMat);
            if (tail?.GetParent() is Node3D tp) fx._tailBlur = Disc(tp, tail.Position, TailRadius, new Vector3(0, 0, Mathf.Pi / 2), fx._blurMat);
            return fx;
        }

        static MeshInstance3D Disc(Node3D parent, Vector3 at, float radius, Vector3 rotation, Material mat)
        {
            var d = new MeshInstance3D
            {
                Mesh = new CylinderMesh { TopRadius = radius, BottomRadius = radius, Height = 0.02f, RadialSegments = 32, Rings = 1 },
                MaterialOverride = mat, Position = at, Rotation = rotation, Visible = false,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            parent.AddChild(d);
            return d;
        }

        /// <summary>
        /// One frame. <paramref name="rotor"/> is the rotor speed 0..1, <paramref name="load"/> how hard it works 0..1; <paramref name="wash"/> is the
        /// point on the ground or water under it when the downwash reaches it (null: too high, too far, or reduce motion), with its strength 0..1.
        /// </summary>
        public void Step(float dt, float rotor, float load, float listenerDistance, (Vector3 At, float Strength, bool Water)? wash)
        {
            _load += (load - _load) * Mathf.Min(1f, dt * 3f);
            var speed = rotor * (0.93f + 0.07f * _load);
            _main?.RotateY(Mathf.Tau * MainRps * speed * dt);
            _tail?.RotateX(Mathf.Tau * TailRps * speed * dt);
            var blur = Mathf.SmoothStep(0.35f, 0.9f, rotor) * 0.2f;
            if (_blurMat != null) _blurMat.AlbedoColor = new Color(0.07f, 0.07f, 0.07f, blur);
            if (_mainBlur != null) _mainBlur.Visible = blur > 0.01f;
            if (_tailBlur != null) _tailBlur.Visible = blur > 0.01f;

            // sound: the blade beat and the engine tone rise with the rotor, and a little more with the load; silent and gone when it has stopped
            if (Sfx.Enabled && rotor > 0.02f && listenerDistance < SoundRange)
            {
                if (_sound == null)
                {
                    _sound = new RotorSound { Position = _main?.Position ?? Vector3.Up * 3f };
                    _root.AddChild(_sound);
                    _sound.MaxDistance = SoundRange; _sound.UnitSize = 28f; // after it is ready: its own defaults are an aircraft's, heard across the map
                }
                _sound.BladeHz = 2f * MainRps * speed;
                _sound.EngineHz = 38f + 52f * rotor + 14f * _load;
                _sound.Level = rotor * (0.42f + 0.25f * _load);
            }
            else if (_sound != null) { _sound.QueueFree(); _sound = null; }

            if (wash is { } w)
            {
                _wash ??= MakeWash(_root);
                _wash.GlobalPosition = w.At + Vector3.Up * 0.25f;
                var tint = w.Water ? SprayTint : DustTint;
                _wash.Color = new Color(tint.R, tint.G, tint.B, 0.18f + 0.5f * w.Strength);
                _wash.Emitting = true;
            }
            else if (_wash != null) _wash.Emitting = false;
        }

        /// <summary>The vehicle is gone (destroyed): nothing turns, sounds or blows until it is back.</summary>
        public void Stop()
        {
            if (_sound != null) { _sound.QueueFree(); _sound = null; }
            if (_wash != null) _wash.Emitting = false;
        }

        /// <summary>The downwash: soft puffs born on a ring under the rotor and pushed outwards along the ground. The dust pool's look, one emitter per helicopter.</summary>
        static CpuParticles3D MakeWash(Node3D root)
        {
            var mat = new StandardMaterial3D { VertexColorUseAsAlbedo = true, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha };
            var p = new CpuParticles3D
            {
                TopLevel = true, LocalCoords = false, Emitting = false, OneShot = false, Amount = 36, Lifetime = 1.3, Randomness = 0.6f,
                EmissionShape = CpuParticles3D.EmissionShapeEnum.Ring, EmissionRingAxis = Vector3.Up, EmissionRingRadius = 4.2f, EmissionRingInnerRadius = 2.2f, EmissionRingHeight = 0.1f,
                Direction = Vector3.Right, Spread = 180f, Flatness = 1f, InitialVelocityMin = 1.5f, InitialVelocityMax = 4f,
                RadialAccelMin = 3f, RadialAccelMax = 7f, Gravity = new Vector3(0, 0.5f, 0), DampingMin = 1f, DampingMax = 2f,
                ScaleAmountMin = 0.8f, ScaleAmountMax = 1.8f, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Mesh = new SphereMesh { Radius = 0.45f, Height = 0.9f, RadialSegments = 6, Rings = 3, Material = mat },
            };
            root.AddChild(p);
            return p;
        }
    }

    Godot.Collections.Array<Rid>? _washExclude;

    /// <summary>The frame's rotor, sound and downwash of one flying vehicle, drawn at <paramref name="p"/> with attitude <paramref name="rot"/>.</summary>
    void StepFlightFx(CarView v, VehicleDef def, Vector3 p, Vector3 rot, float dt)
    {
        if (v.Rotor == null || FlightOf(v) is not { } s) return;
        var f = def.Flight!;
        var tilt = Mathf.Max(Mathf.Abs(rot.X), Mathf.Abs(rot.Z)) / (f.MaxPitchDeg * FlightMath.Deg2Rad);
        var load = Mathf.Clamp(0.45f + 0.45f * s.Velocity.Y / f.ClimbRate + 0.3f * tilt, 0f, 1f);
        var distance = Sfx.ListenerDistance(p);
        (Vector3, float, bool)? wash = null;
        if (s.Rotor > 0.6f && distance < RotorFx.WashRange && !GameSettings.Current.ReduceMotion && World != null)
        {
            // what is under it: the ground (or a roof), or the surface of a water body above that
            var q = PhysicsRayQueryParameters3D.Create(p + Vector3.Up, p + Vector3.Down * (RotorFx.WashHeight + 1f));
            q.CollisionMask = CollisionLayers.World;
            if (_me != null) q.Exclude = _washExclude ??= new Godot.Collections.Array<Rid> { _me.GetRid() };
            var hit = World.GetWorld3D().DirectSpaceState.IntersectRay(q);
            var surface = Water.BodyAt(p.X, p.Z)?.SurfaceY;
            float? under = hit.Count > 0 ? hit["position"].AsVector3().Y : null;
            var wet = surface is { } sy && (under == null || sy > under);
            if (wet) under = surface;
            if (under is { } y && p.Y - y < RotorFx.WashHeight)
                wash = (new Vector3(p.X, y, p.Z), s.Rotor * (1f - Mathf.Clamp((p.Y - y) / RotorFx.WashHeight, 0f, 1f)), wet);
        }
        v.Rotor.Step(dt, s.Rotor, load, distance, wash);
    }
}
