using Godot;
using Drycastle.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter;

/// <summary>
/// Spent casings (#307): a gun that fires throws a casing out of its port; it flies, bounces, clinks, lies on the floor for 20 s and fades. One <see cref="CasingSim"/> per world
/// (the arc, the bounces and the clock are its rules, tested without Godot) drawn by one MultiMesh of <see cref="CasingSim.Capacity"/> instances: a casing that lies still costs nothing
/// per frame (only a slot whose transform changed is written), and a floor ray is cast only for a casing close to the floor. Casings are world-space, spawned only within
/// <see cref="SpawnRange"/> of the camera, and play the <c>casing.tink</c> sound on each landing (throttled and low priority in sounds.json). Skipped on headless runs.
/// </summary>
public static partial class CasingFx
{
    /// <summary>Metres from the camera within which a casing is thrown at all.</summary>
    public const float SpawnRange = 60f;

    static Pool? _pool;

    /// <summary>
    /// A casing leaves a gun at <paramref name="port"/>, whose barrel points along <paramref name="forward"/>; it is thrown to the gun's right and up, and keeps
    /// <paramref name="inherit"/> (m/s, the vehicle it was fired from).
    /// </summary>
    public static void Eject(Node3D world, Vector3 port, Vector3 forward, CasingKind kind, Vector3 inherit = default)
    {
        if (!ShotFx.Enabled || !GodotObject.IsInstanceValid(world) || !world.IsInsideTree()) return;
        var cam = world.GetViewport()?.GetCamera3D();
        if (cam != null && cam.GlobalPosition.DistanceSquaredTo(port) > SpawnRange * SpawnRange) return;
        EnsurePool(world).Eject(port, forward, kind, inherit);
    }

    /// <summary>The casing a hand weapon of this category ejects, or null (melee, launchers).</summary>
    public static CasingKind? KindOf(WeaponDef? w) => w == null ? null : CasingProfiles.Of(w.Category);

    static Pool EnsurePool(Node3D world)
    {
        if (_pool != null && GodotObject.IsInstanceValid(_pool) && _pool.GetParent() == world) return _pool;
        _pool = new Pool { Name = "CasingPool" };
        world.AddChild(_pool);
        return _pool;
    }

    sealed partial class Pool : Node3D
    {
        readonly CasingSim _sim = new();
        readonly RandomNumberGenerator _rng = new();
        MultiMesh _mm = null!;
        PhysicsRayQueryParameters3D? _q;

        public override void _Ready()
        {
            _rng.Randomize();
            var mesh = new CylinderMesh { TopRadius = 0.5f, BottomRadius = 0.5f, Height = 1f, RadialSegments = 6, Rings = 0 };
            mesh.Material = new StandardMaterial3D { VertexColorUseAsAlbedo = true, Roughness = 0.4f, Metallic = 0.3f };
            _mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true, Mesh = mesh, InstanceCount = CasingSim.Capacity };
            var zero = new Transform3D(Basis.FromScale(Vector3.Zero), Vector3.Zero);
            for (var i = 0; i < CasingSim.Capacity; i++) _mm.SetInstanceTransform(i, zero);
            AddChild(new MultiMeshInstance3D { Multimesh = _mm, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, CustomAabb = new Aabb(new Vector3(-6000, -500, -6000), new Vector3(12000, 3000, 12000)) });
        }

        public void Eject(Vector3 port, Vector3 forward, CasingKind kind, Vector3 inherit)
        {
            var p = CasingProfiles.Of(kind);
            var fwd = forward.LengthSquared() < 1e-6f ? Vector3.Forward : forward.Normalized();
            var right = fwd.Cross(Vector3.Up);
            right = right.LengthSquared() < 1e-4f ? Vector3.Right : right.Normalized();
            var at = port - fwd * p.PortBack + right * 0.03f;
            var vel = right * (p.Eject * _rng.RandfRange(0.7f, 1.1f)) + Vector3.Up * _rng.RandfRange(1.2f, 2.2f) - fwd * _rng.RandfRange(0f, 0.6f)
                + new Vector3(_rng.RandfRange(-0.3f, 0.3f), 0, _rng.RandfRange(-0.3f, 0.3f)) + inherit;
            var slot = _sim.Spawn(N(at), N(vel), _rng.RandfRange(0f, Mathf.Tau), _rng.RandfRange(-22f, 22f), kind, Floor);
            _mm.SetInstanceColor(slot.Index, new Color(p.Colour0, p.Colour1, p.Colour2));
        }

        static NVec3 N(Vector3 v) => new(v.X, v.Y, v.Z);

        /// <summary>The floor under a point: the world and the vehicles' solid boxes (a vehicle's roof is no place to keep a casing). Not the shot-only cover (tree crowns).</summary>
        bool Floor(NVec3 from, float depth, out float y, out bool onVehicle)
        {
            y = 0; onVehicle = false;
            _q ??= PhysicsRayQueryParameters3D.Create(Vector3.Zero, Vector3.Zero);
            _q.From = new Vector3(from.X, from.Y, from.Z);
            _q.To = new Vector3(from.X, from.Y - depth, from.Z);
            _q.CollisionMask = CollisionLayers.World | CollisionLayers.VehicleSolid;
            var hit = GetWorld3D().DirectSpaceState.IntersectRay(_q);
            if (hit.Count == 0) return false;
            y = hit["position"].AsVector3().Y;
            onVehicle = hit["collider"].As<CollisionObject3D>() is { } c && (c.CollisionLayer & CollisionLayers.VehicleSolid) != 0;
            return true;
        }

        public override void _Process(double delta)
        {
            _sim.Step((float)Mathf.Min(delta, 0.05), Floor);
            foreach (var i in _sim.Impacts)
            {
                var db = CasingProfiles.Of(i.Kind).DbOffset + Mathf.Clamp((i.Speed - 3f) * 1.5f, -9f, 0f);
                Sfx.Play3D("casing.tink", new Vector3(i.At.X, i.At.Y, i.At.Z), db);
            }
            foreach (var s in _sim.Slots)
            {
                if (!s.Dirty) continue;
                s.Dirty = false;
                var p = CasingProfiles.Of(s.Kind);
                var k = s.Scale;
                // the cylinder's axis is Y: turned to X (lying along the heading), tilted end over end about Z, then yawed
                var rot = new Basis(Vector3.Up, s.Yaw) * new Basis(Vector3.Back, s.Tumble - Mathf.Pi * 0.5f);
                var basis = new Basis(rot.Column0 * (p.Diameter * k), rot.Column1 * (p.Length * k), rot.Column2 * (p.Diameter * k));
                _mm.SetInstanceTransform(s.Index, new Transform3D(basis, new Vector3(s.Pos.X, s.Pos.Y, s.Pos.Z)));
            }
        }
    }
}
