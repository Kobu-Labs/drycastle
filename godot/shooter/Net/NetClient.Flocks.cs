using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// Bird flocks on the client (GLOSSARY "Flock"): the world entities of type <see cref="FlockRules.TypeId"/>. The server sends only the flock's centre (and whether it
/// is scattering); every client flies the birds itself, as boids (<see cref="BoidSwarm"/>) around that centre, and draws them as one MultiMesh per flock (one draw
/// call). Cosmetic: the birds are not hit targets and the clients' birds differ in detail. The bird is the model of wildlife.json "flocks" (its first mesh, flapped
/// by narrowing its wings, since the pack has no flap clip); without the model a small wedge stands in.
/// </summary>
public partial class NetClient
{
    /// <summary>wildlife.json: set by the host (the flock kinds; the kind index of a state is a position in <c>Flocks</c>).</summary>
    public WildlifeFile WildlifeData = new();

    sealed class FlockView
    {
        public MultiMeshInstance3D Node = null!;
        public BoidSwarm Swarm = null!;
        public float[] Phase = Array.Empty<float>();
        public Transform3D MeshLocal;
        public float FlapHz;
        public bool Started;
        public double Owed;
        public int Birds;
        public FlockSpecies Kind = null!;
    }

    readonly Dictionary<ushort, FlockView> _flocks = new();
    readonly Dictionary<string, (Mesh Mesh, Transform3D Local)> _birdMeshes = new();
    double _flockClock;

    /// <summary>The aircraft the birds fly round this frame, and where each was last frame (its velocity is the difference; the key is the entity id, a vehicle's offset past the entity ids).</summary>
    readonly List<BoidObstacle> _flockObstacles = new();
    readonly Dictionary<int, System.Numerics.Vector3> _obstacleLast = new();
    const int ObstacleVehicleKey = 1 << 16;
    /// <summary>Radius of the sphere a gunship aircraft keeps clear, metres; a flying vehicle's is its half length plus the rotor's reach.</summary>
    const float AircraftAvoidRadiusM = 12, VehicleRotorReachM = 4;

    void InitFlocks()
    {
        FlockRules.Register(WorldEntityRegistry.Shared);
        WorldEntities.Spawned += OnFlockSpawned;
        WorldEntities.Despawned += OnFlockDespawned;
        WorldEntities.DormancyChanged += OnFlockDormancy;
    }

    /// <summary>A flock outside the interest range (ADR 0025) is hidden and its boids are not flown; one that comes back starts a new swarm at its centre (the old one would fly across the map to catch up).</summary>
    void OnFlockDormancy(WorldReplica.Entity e)
    {
        if (e.Type != FlockRules.TypeId || !_flocks.TryGetValue(e.Id, out var v)) return;
        v.Node.Visible = !e.Dormant;
        if (e.Dormant) return;
        var p = e.State.Position;
        v.Swarm = new BoidSwarm(v.Birds, new System.Numerics.Vector3(p.X, p.Y, p.Z), v.Kind.Boids, e.Id);
    }

    /// <summary>The mesh of a flock kind's bird and the transform that puts it upright, centred, scaled and facing -Z; a wedge when the model is missing.</summary>
    (Mesh Mesh, Transform3D Local) BirdMesh(FlockSpecies kind)
    {
        if (_birdMeshes.TryGetValue(kind.Id, out var cached)) return cached;
        Mesh? mesh = null;
        var local = Transform3D.Identity;
        if (kind.Model != "" && ModelLib.Instance(kind.Model) is { } model)
        {
            if (model.FindChildren("*", "MeshInstance3D", true, false).FirstOrDefault() is MeshInstance3D mi && mi.Mesh != null)
            {
                mesh = mi.Mesh;
                var toRoot = Transform3D.Identity; // the mesh node's transform up to the model's root (the pack scales the node by 100)
                for (Node? n = mi; n != null && n != model; n = n.GetParent())
                    if (n is Node3D n3) toRoot = n3.Transform * toRoot;
                local = new Transform3D(Basis.FromEuler(new Vector3(0, Mathf.DegToRad(kind.ModelYawDeg), 0)) * Basis.FromScale(Vector3.One * kind.Scale) * toRoot.Basis, Vector3.Zero);
                local.Origin = -(local.Basis * mesh.GetAabb().GetCenter());
            }
            model.QueueFree();
        }
        if (mesh == null)
        {
            var wedge = new PrismMesh { Size = new Vector3(0.5f, 0.06f, 0.35f) * kind.Scale };
            mesh = wedge;
            local = new Transform3D(Basis.FromEuler(new Vector3(Mathf.Pi / 2, 0, 0)), Vector3.Zero);
        }
        return _birdMeshes[kind.Id] = (mesh, local);
    }

    void OnFlockSpawned(WorldReplica.Entity e)
    {
        if (e.Type != FlockRules.TypeId) return;
        var kindIndex = FlockRules.SpeciesOf(e.State.Owner);
        if (kindIndex >= WildlifeData.Flocks.Count)
        {
            GD.PrintErr($"client: flock {e.Id} has kind {kindIndex}, wildlife.json has {WildlifeData.Flocks.Count}; not drawn (data files differ from the server's?)");
            return;
        }
        var kind = WildlifeData.Flocks[kindIndex];
        var birds = Math.Max(1, FlockRules.BirdsOf(e.State.Owner));
        var (mesh, local) = BirdMesh(kind);
        var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = mesh, InstanceCount = birds };
        var node = new MultiMeshInstance3D { Name = $"Flock{e.Id}", Multimesh = mm, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, ExtraCullMargin = 100 };
        Owned.AddChild(node);
        var p = e.State.Position;
        var rng = new Random(e.Id);
        var phase = new float[birds];
        for (var i = 0; i < birds; i++) phase[i] = (float)rng.NextDouble() * Mathf.Tau;
        _flocks[e.Id] = new FlockView { Node = node, Swarm = new BoidSwarm(birds, new System.Numerics.Vector3(p.X, p.Y, p.Z), kind.Boids, e.Id), Phase = phase, MeshLocal = local, FlapHz = 3.2f, Birds = birds, Kind = kind };
        node.Visible = !e.Dormant; // spawned outside the interest range: hidden until a keyframe shows it
    }

    void OnFlockDespawned(ushort id)
    {
        if (!_flocks.Remove(id, out var v)) return;
        v.Node.QueueFree();
    }

    /// <summary>The aircraft of this frame as obstacles for the birds: the gunship aircraft (world entities) and the flying vehicles, each with the velocity it had since last frame.</summary>
    void CollectFlockObstacles(double delta, double rt)
    {
        _flockObstacles.Clear();
        void Add(int key, Godot.Vector3 at, float radius)
        {
            var p = new System.Numerics.Vector3(at.X, at.Y, at.Z);
            var vel = delta > 1e-4 && _obstacleLast.TryGetValue(key, out var last) ? (p - last) / (float)delta : System.Numerics.Vector3.Zero;
            _obstacleLast[key] = p;
            _flockObstacles.Add(new BoidObstacle(p, vel, radius));
        }
        foreach (var (id, _) in _aircraft)
            if (WorldEntities.Entities.TryGetValue(id, out var e) && e.Buf.At(rt) is { } s) Add(id, new Godot.Vector3(s.Pos.X, s.Pos.Y, s.Pos.Z), AircraftAvoidRadiusM);
        if (Vehicles != null)
            foreach (var (id, v) in _cars)
                if (!v.Destroyed && v.Kind < Vehicles.Vehicles.Count && Vehicles.Vehicles[v.Kind] is { Medium: VehicleMedium.Air } def && GodotObject.IsInstanceValid(v.Node))
                    Add(ObstacleVehicleKey + id, v.Node.GlobalPosition, def.HalfLength + VehicleRotorReachM);
        if (_obstacleLast.Count > _flockObstacles.Count + 8) _obstacleLast.Clear(); // an aircraft that is gone leaves its last place behind; start over rather than grow
    }

    /// <summary>Flies every flock's boids one frame around the flock's centre (round the aircraft) and writes the birds' transforms.</summary>
    void RenderFlocks(double delta)
    {
        if (_flocks.Count == 0 || _lastTick == 0) return;
        _flockClock += delta;
        var rt = RenderTickNow();
        CollectFlockObstacles(delta, rt);
        var cam = GetViewport().GetCamera3D();
        foreach (var (id, v) in _flocks)
        {
            if (!WorldEntities.Entities.TryGetValue(id, out var e) || e.Dormant) continue;
            var stride = v.Started ? CreatureLod.Stride(cam, v.Node.GlobalPosition) : 1;
            v.Owed += delta;
            if (!CreatureLod.Due(id, stride)) continue; // far or out of view: the birds keep their places until the flock's turn
            var step = v.Owed; v.Owed = 0;
            v.Started = true;
            var goal = e.Buf.At(rt) is { } s ? new System.Numerics.Vector3(s.Pos.X, s.Pos.Y, s.Pos.Z) : e.State.Position;
            v.Swarm.Step((float)Math.Min(step, 0.1), goal, e.State.State == (byte)FlockState.Scatter, _flockObstacles);
            var mm = v.Node.Multimesh;
            for (var i = 0; i < v.Swarm.Count; i++)
            {
                var p = v.Swarm.Positions[i]; var vel = v.Swarm.Velocities[i];
                var dir = new Vector3(vel.X, vel.Y, vel.Z);
                if (dir.LengthSquared() < 1e-6f) dir = Vector3.Forward;
                var look = Basis.LookingAt(dir.Normalized(), Vector3.Up);
                var flap = 1f - 0.45f * Mathf.Abs(Mathf.Sin((float)_flockClock * v.FlapHz * Mathf.Pi + v.Phase[i])); // the wings fold in and out
                var t = new Transform3D(look * Basis.FromScale(new Vector3(flap, 1, 1)), new Vector3(p.X, p.Y, p.Z)) * v.MeshLocal;
                mm.SetInstanceTransform(i, t);
            }
        }
    }
}
