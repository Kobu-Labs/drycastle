using Godot;
using Drycastle.Shooter.Effects;
using Drycastle.Shooter.Weapons;
using Drycastle.ShooterCore.Buildings.Destruction;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Effects;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Buildings.Destruction;

/// <summary>
/// The cosmetic debris of the hollow houses (D7, WP-G), the client's <see cref="IHouseDebris"/>. Every Fallen component of a carve is one Jolt rigid body (its faces in the
/// palette's colours, its box cover as collider) placed where it stood in the house. Debris collides with the static world only (layer none, mask
/// <see cref="CollisionLayers.World"/>): never with players, vehicles, shots or other debris. Its first impact over debris.break_impulse (the speed change of one physics
/// tick, m/s) splits it with <see cref="Fracture.Split"/> into pieces that keep its velocity plus a small outward kick; with second_break a large piece may split once more.
/// Small pieces, the dust group, crumbled and carved cells go up as dust (<see cref="DebrisDust"/>); a large part crashing also raises a blast dust cloud (<see cref="DustFx"/>).
/// At rest a piece fades (sinks and turns clear after fade_after_s over fade_s) or, in stay mode, is frozen into its house's rubble mesh (<see cref="DebrisRubble"/>), at
/// most stay_cap_cells over the map with the oldest fading. At most max_bodies bodies; a part further than max_distance_m from the camera is dust only. A headless client
/// (<see cref="ShotFx.Enabled"/> false) does nothing. Lifecycle: <see cref="DebrisMachine.Spec"/>. Nothing is allocated per frame; building a body allocates.
/// <para>Add it to the world at the origin (identity transform): bodies and rubble are its children in world coordinates.</para>
/// </summary>
public partial class DebrisNode : Node3D, IHouseDebris
{
    /// <summary>A fallen part this big (cells) raises a blast dust cloud when it crashes.</summary>
    const int CrashCloudCells = 150;

    DebrisConfig _debris = new();
    FractureConfig _fracture = new();
    Node3D? _dustWorld;
    bool _stay;
    float _gravity = 9.8f;
    long _nextId;
    DebrisDust? _dust;
    PhysicsMaterial? _physics;

    readonly List<DebrisPiece> _pieces = new();
    readonly Dictionary<ushort, DebrisRubble> _rubble = new();
    readonly Dictionary<long, ushort> _rubbleHouse = new();
    readonly RubbleLedger _ledger = new();
    // scratch, reused
    readonly List<Quad> _quads = new();
    readonly List<(NVec3 Min, NVec3 Max)> _boxes = new();
    readonly List<int> _withBody = new(), _asDust = new();
    readonly List<long> _evicted = new();
    readonly List<int[]> _groups = new();

    /// <summary>
    /// The debris of a map. <paramref name="dustWorld"/> is the node the blast dust lives under (the client's <c>ClientContext.Owned</c>, as RocketFx is given);
    /// null: a crashing part raises no dust cloud (the grains still fly).
    /// </summary>
    public static DebrisNode Create(DestructionConfig cfg, Node3D? dustWorld)
    {
        var node = new DebrisNode { Name = "HouseDebris", _debris = cfg.Debris, _fracture = cfg.Fracture, _dustWorld = dustWorld, _stay = cfg.Debris.Rubble == RubbleMode.Stay };
        return node;
    }

    public override void _Ready()
    {
        TopLevel = true;
        GlobalTransform = Transform3D.Identity;
        if (!ShotFx.Enabled) { SetProcess(false); SetPhysicsProcess(false); return; }
        _gravity = ProjectSettings.GetSetting("physics/3d/default_gravity", 9.8f).AsSingle();
        _physics = new PhysicsMaterial { Friction = 0.9f, Bounce = 0.05f };
        _dust = new DebrisDust(this);
    }

    bool Live => ShotFx.Enabled && _dust != null && IsInsideTree();

    // ---- IHouseDebris -------------------------------------------------------------------------------------------------------------

    public void Fall(HouseGrid g, CarveResult r, NVec3 blastMap, uint seed)
    {
        if (!Live) return;
        var cam = GetViewport()?.GetCamera3D();
        Vector3? eye = cam?.GlobalPosition;
        var far = _debris.MaxDistanceM;
        var blast = DebrisMesh.Gd(DebrisRules.GodotOf(blastMap));
        Func<NVec3, Vector3> inHouse = p => DebrisMesh.Gd(DebrisRules.GodotOf(g.ToMap(p)));
        _dust!.Cells(g, r.Carved, inHouse, eye, far);
        _dust.Cells(g, r.Crumbled, inHouse, eye, far);
        var basis = new Basis(Vector3.Up, g.Yaw);
        for (var k = 0; k < r.Fallen.Count; k++)
        {
            var cells = r.Fallen[k];
            if (cells.Length == 0) continue;
            var centroid = DebrisRules.Centroid(g, cells);
            var at = inHouse(centroid);
            if ((eye is { } e && e.DistanceTo(at) > far) || !FreeBody())
            {
                _dust.Cells(g, cells, inHouse, eye, far);
                continue;
            }
            var pieceSeed = DebrisRules.SplitSeed(seed, Lowest(cells), k);
            var spin = new Vector3(Jitter(pieceSeed, 0), Jitter(pieceSeed, 1), Jitter(pieceSeed, 2)) * 0.6f;
            var push = DebrisMesh.Gd(DebrisRules.BlastPush(DebrisMesh.Nv(at), DebrisMesh.Nv(blast)));
            Spawn(g, cells, 0, pieceSeed, centroid, new Transform3D(basis, at), push, spin);
        }
    }

    public void SetRubble(bool stay)
    {
        if (stay == _stay) return;
        _stay = stay;
        if (!Live) return;
        if (stay)
        {
            // the bodies waiting to fade go to the rubble instead
            foreach (var p in _pieces)
                if (p.Body != null && p.Phase.Is(DebrisPhase.Resting) && !p.Pending) Pend(p);
            return;
        }
        // the rubble sinks away as it is, house by house; the bodies waiting to be merged fade instead
        foreach (var (_, rubble) in _rubble)
        {
            foreach (var p in rubble.Pending) { p.Pending = false; p.Phase.SetLeft(_debris.FadeAfterS); }
            rubble.Pending.Clear();
            if (rubble.Pieces.Count > 0) Ghost(rubble.Node, rubble.Node, 0.6f);
            else rubble.Node.QueueFree();
        }
        _rubble.Clear();
        _rubbleHouse.Clear();
        _ledger.Clear();
    }

    public void ResetAll()
    {
        foreach (var p in _pieces) if (GodotObject.IsInstanceValid(p.Root)) p.Root.QueueFree();
        _pieces.Clear();
        foreach (var (_, rubble) in _rubble) if (GodotObject.IsInstanceValid(rubble.Node)) rubble.Node.QueueFree();
        _rubble.Clear();
        _rubbleHouse.Clear();
        _ledger.Clear();
    }

    // ---- frames -------------------------------------------------------------------------------------------------------------------

    public override void _PhysicsProcess(double delta)
    {
        var dt = (float)delta;
        var fall = new Vector3(0, -_gravity * dt, 0);
        for (var i = _pieces.Count - 1; i >= 0; i--)
        {
            if (i >= _pieces.Count) continue; // a break replaced pieces at the end
            var p = _pieces[i];
            if (p.Body is not { } body || !p.Phase.IsAny(DebrisPhase.Falling, DebrisPhase.Broken)) continue;
            p.Phase.Tick(dt);
            var v = body.LinearVelocity;
            var change = (v - (p.PrevVelocity + fall * body.GravityScale)).Length();
            if (DebrisRules.Breaks(p.Generation, p.Cells.Length, p.Phase.Elapsed, change, _debris, _fracture))
            {
                Break(i, p, body, v);
                continue;
            }
            p.PrevVelocity = v;
            p.StillFor = DebrisRules.Still(DebrisMesh.Nv(v), DebrisMesh.Nv(body.AngularVelocity)) ? p.StillFor + dt : 0f;
            if (DebrisRules.Rests(body.Sleeping, p.StillFor, p.Phase.Elapsed)) Rest(p, body);
        }
    }

    public override void _Process(double delta)
    {
        var dt = (float)delta;
        for (var i = _pieces.Count - 1; i >= 0; i--)
        {
            var p = _pieces[i];
            if (p.Phase.Is(DebrisPhase.Resting))
            {
                if (p.Phase.Tick(dt) && !_stay && !p.Pending) StartFade(p);
                continue;
            }
            if (!p.Phase.Is(DebrisPhase.Fading)) continue;
            p.Phase.Tick(dt);
            var t = _debris.FadeS > 0 ? Mathf.Clamp(p.Phase.Elapsed / _debris.FadeS, 0f, 1f) : 1f;
            if (GodotObject.IsInstanceValid(p.Root))
            {
                p.Root.Position = p.FadeFrom + Vector3.Down * (p.SinkM * t);
                p.Look.Transparency = t;
            }
            if (!p.Phase.Expired) continue;
            if (GodotObject.IsInstanceValid(p.Root)) p.Root.QueueFree();
            RemoveAt(i);
        }
        if (_rubble.Count == 0) return;
        foreach (var (_, rubble) in _rubble)
            if (rubble.Merge.Tick(dt)) MergeRubble(rubble);
    }

    // ---- bodies -------------------------------------------------------------------------------------------------------------------

    void Spawn(HouseGrid g, int[] cells, int generation, uint seed, NVec3 centroidGrid, Transform3D xf, Vector3 linear, Vector3 angular)
    {
        var arrays = DebrisMesh.Faces(g, cells, centroidGrid, _quads);
        var body = new RigidBody3D
        {
            Name = "Debris", CollisionLayer = 0, CollisionMask = CollisionLayers.World, Mass = DebrisRules.Mass(cells.Length),
            PhysicsMaterialOverride = _physics, Transform = xf, LinearVelocity = linear, AngularVelocity = angular, AngularDamp = 0.5f,
        };
        DebrisMesh.Collider(body, g, cells, centroidGrid, _boxes);
        var look = new MeshInstance3D { Mesh = DebrisMesh.Mesh(arrays) };
        body.AddChild(look);
        AddChild(body);
        var p = new DebrisPiece
        {
            Id = ++_nextId, Grid = g, Cells = cells, Generation = generation, Seed = seed, CentroidGrid = centroidGrid,
            Root = body, Body = body, Look = look, Arrays = arrays, PrevVelocity = linear,
        };
        if (generation > 0) p.Phase.Enter(DebrisPhase.Broken);
        _pieces.Add(p);
    }

    /// <summary>Replaces piece <paramref name="i"/> by its split: the large pieces get bodies (as many as max_bodies leaves), the rest and the dust group go up as dust.</summary>
    void Break(int i, DebrisPiece p, RigidBody3D body, Vector3 v)
    {
        var g = p.Grid;
        p.Phase.Enter(DebrisPhase.Broken);
        var xf = body.GlobalTransform;
        var w = body.AngularVelocity;
        // where it hit: the cell furthest along the way it was moving, in the grid frame
        var dirLocal = xf.Basis.Inverse() * (p.PrevVelocity.LengthSquared() > 1e-4f ? p.PrevVelocity : Vector3.Down);
        var impactGrid = DebrisRules.Leading(g, p.Cells, DebrisRules.GridOf(DebrisMesh.Nv(dirLocal), 1f));
        var groups = Fracture.Split(g, p.Cells, impactGrid, DebrisRules.SplitSeed(p.Seed, Lowest(p.Cells), p.Generation + 1), _fracture);
        var parentLocal = DebrisRules.LocalOf(p.CentroidGrid, g.Cell);
        Func<NVec3, Vector3> inBody = q => xf * DebrisMesh.Gd(DebrisRules.LocalOf(q, g.Cell) - parentLocal);

        if (p.Generation == 0 && p.Cells.Length >= CrashCloudCells && _dustWorld is { } world && GodotObject.IsInstanceValid(world))
            DustFx.Blast(world, inBody(impactGrid), Mathf.Clamp(Mathf.Pow(p.Cells.Length, 1f / 3f) * g.Cell * 1.5f, 1.5f, 6f), DustRules.SpeedOr(0f));

        body.QueueFree();
        RemoveAt(i);

        _groups.Clear();
        Span<int> sizes = groups.Count <= 128 ? stackalloc int[groups.Count] : new int[groups.Count];
        var n = 0;
        for (var k = 0; k < groups.Count; k++)
        {
            var cells = groups[k];
            if (cells.Length == 0) continue;
            if (DebrisRules.IsDust(g, cells, k, groups.Count, _fracture)) { _dust!.Cells(g, cells, inBody, null, 0f); continue; }
            _groups.Add(cells);
            sizes[n++] = cells.Length;
        }
        var free = _debris.MaxBodies - CountBodies();
        DebrisRules.Budget(sizes[..n], free, _withBody, _asDust);
        foreach (var k in _asDust) _dust!.Cells(g, _groups[k], inBody, null, 0f);
        foreach (var k in _withBody)
        {
            var cells = _groups[k];
            var c = DebrisRules.Centroid(g, cells);
            var offsetLocal = DebrisMesh.Gd(DebrisRules.LocalOf(c, g.Cell) - parentLocal);
            var offsetWorld = xf.Basis * offsetLocal;
            var vel = DebrisMesh.Gd(DebrisRules.PieceVelocity(DebrisMesh.Nv(v), DebrisMesh.Nv(w), DebrisMesh.Nv(offsetWorld), NVec3.UnitY));
            Spawn(g, cells, p.Generation + 1, DebrisRules.SplitSeed(p.Seed, Lowest(cells), k), c, new Transform3D(xf.Basis, xf.Origin + offsetWorld), vel, w);
        }
    }

    void Rest(DebrisPiece p, RigidBody3D body)
    {
        p.Phase.Enter(DebrisPhase.Resting, _debris.FadeAfterS);
        body.FreezeMode = RigidBody3D.FreezeModeEnum.Kinematic;
        body.Freeze = true;
        body.CollisionMask = 0;
        if (_stay) Pend(p);
    }

    void StartFade(DebrisPiece p)
    {
        p.Phase.Enter(DebrisPhase.Fading, _debris.FadeS);
        if (p.Body is { } body) { body.Freeze = true; body.CollisionMask = 0; }
        p.FadeFrom = p.Root.Position;
        p.SinkM = SinkOf(p.Cells.Length, p.Grid.Cell);
    }

    /// <summary>A mesh with no body that sinks and fades out (an evicted rubble piece, a house's rubble when stay is turned off).</summary>
    void Ghost(Node3D root, GeometryInstance3D look, float sinkM)
    {
        var p = new DebrisPiece { Id = ++_nextId, Grid = null!, Cells = [], Root = root, Look = look, SinkM = sinkM, FadeFrom = root.Position };
        p.Phase.Reset(DebrisPhase.Fading);
        p.Phase.SetLeft(_debris.FadeS);
        _pieces.Add(p);
    }

    static float SinkOf(int cells, float cell) => Mathf.Clamp(Mathf.Pow(Mathf.Max(1, cells), 1f / 3f) * cell, 0.3f, 1.5f);

    // ---- stay rubble --------------------------------------------------------------------------------------------------------------

    void Pend(DebrisPiece p)
    {
        if (!_rubble.TryGetValue(p.Grid.Index, out var rubble)) _rubble[p.Grid.Index] = rubble = new DebrisRubble(this, p.Grid.Index);
        p.Pending = true;
        p.Phase.SetLeft(float.PositiveInfinity);
        rubble.Pending.Add(p);
        rubble.Merge.StartIfReady(DebrisRubble.MergeAfterS);
    }

    void MergeRubble(DebrisRubble rubble)
    {
        if (rubble.Pending.Count == 0) return;
        foreach (var p in rubble.Pending)
        {
            var baked = DebrisRubble.Bake(p);
            rubble.Pieces.Add(baked);
            _rubbleHouse[baked.Id] = p.Grid.Index;
            _ledger.Add(baked.Id, baked.Cells, _debris.StayCapCells, _evicted);
            foreach (var id in _evicted) Evict(id, rubble);
            if (GodotObject.IsInstanceValid(p.Root)) p.Root.QueueFree();
            var at = _pieces.IndexOf(p);
            if (at >= 0) RemoveAt(at);
        }
        rubble.Pending.Clear();
        rubble.Rebuild();
    }

    /// <summary>Takes rubble piece <paramref name="id"/> out of its house's mesh and lets it fade as a ghost.</summary>
    void Evict(long id, DebrisRubble merging)
    {
        if (!_rubbleHouse.Remove(id, out var house) || !_rubble.TryGetValue(house, out var rubble)) return;
        var k = rubble.Pieces.FindIndex(q => q.Id == id);
        if (k < 0) return;
        var piece = rubble.Pieces[k];
        rubble.Pieces.RemoveAt(k);
        var ghost = new MeshInstance3D { Name = "RubbleGhost", Mesh = DebrisMesh.Mesh(piece.Vertices, piece.Normals, piece.Colours) };
        AddChild(ghost);
        Ghost(ghost, ghost, SinkOf(piece.Cells, piece.Cell));
        if (rubble != merging) rubble.Rebuild();
    }

    // ---- budget -------------------------------------------------------------------------------------------------------------------

    int CountBodies()
    {
        var n = 0;
        foreach (var p in _pieces) if (p.Counts) n++;
        return n;
    }

    /// <summary>Whether a new body fits under max_bodies, after making room by retiring the longest-resting body (fade, or merge into the rubble).</summary>
    bool FreeBody()
    {
        if (CountBodies() < _debris.MaxBodies) return true;
        DebrisPiece? oldest = null;
        foreach (var p in _pieces)
            if (p.Counts && p.Phase.Is(DebrisPhase.Resting) && (oldest == null || p.Phase.Elapsed > oldest.Phase.Elapsed)) oldest = p;
        if (oldest == null) return false;
        if (oldest.Pending && _rubble.TryGetValue(oldest.Grid.Index, out var rubble)) { rubble.Merge.Cancel(); MergeRubble(rubble); }
        else StartFade(oldest);
        return CountBodies() < _debris.MaxBodies;
    }

    void RemoveAt(int i)
    {
        var last = _pieces.Count - 1;
        _pieces[i] = _pieces[last];
        _pieces.RemoveAt(last);
    }

    static int Lowest(int[] cells)
    {
        var lo = int.MaxValue;
        foreach (var c in cells) if (c < lo) lo = c;
        return lo;
    }

    /// <summary>-1..1 from a seed and a channel.</summary>
    static float Jitter(uint seed, uint channel)
    {
        var h = (seed ^ (channel * 0x9E3779B9u)) * 0x85EBCA6Bu;
        h ^= h >> 13; h *= 0xC2B2AE35u; h ^= h >> 16;
        return h / (float)uint.MaxValue * 2f - 1f;
    }
}
