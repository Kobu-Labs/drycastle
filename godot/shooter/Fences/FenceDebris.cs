using Godot;
using Drycastle.Shooter.Weapons;
using Drycastle.ShooterCore.Buildings.Destruction;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Fences;
using Drycastle.ShooterCore.Fsm;
using MapLoaderT = Drycastle.MapLoader.MapLoader;

namespace Drycastle.Shooter.Fences;

/// <summary>
/// The cosmetic debris of the thin fences (docs/fence-destruction.md F5, WP-V). A broken piece is one Jolt rigid body, its section's piece width x its height x its thickness, standing where
/// the piece stood and pushed the way the server says (the message's push, or fence_destruction.json debris.push_speed) with a tip-over spin. Its look: a two-sided quad of the map's
/// texture material (vertex colour = the section's tint, UVs as the panel was drawn) for a picket or wire, a tinted box for plain. Like the houses' debris it collides with the static world
/// only (layer none, mask <see cref="CollisionLayers.World"/>). Lifecycle: <see cref="DebrisMachine.Spec"/> (Falling, Resting, Fading). At rest the body is freed and the look stays as a static
/// mesh: fade mode lets it lie debris.fade_after_s, then sink away over debris.fade_s; stay mode (the house rubble mode, <see cref="SetRubble"/>) keeps at most debris.stay_cap, the
/// oldest fading. At most debris.max_bodies bodies fall at once and a piece further than debris.max_distance_m from the camera gets none. A headless client does nothing.
/// <para>Add it at the origin (identity transform): its children are in world coordinates.</para>
/// </summary>
public partial class FenceDebris : Node3D
{
    /// <summary>A body's collider is at least this thick (a picket is thinner), metres.</summary>
    const float MinColliderM = 0.05f;
    /// <summary>Up kick of a fresh piece (m/s) and the tip-over spin: base plus per m/s of push (rad/s).</summary>
    const float KickUpMps = 0.6f, SpinBase = 1.5f, SpinPerMps = 0.5f;
    /// <summary>How far a fading piece sinks (m).</summary>
    const float SinkM = 0.3f;

    sealed class Piece
    {
        public required Node3D Root;
        public RigidBody3D? Body;
        public required MeshInstance3D Look;
        public readonly StateMachine<DebrisPhase> Phase = new(DebrisMachine.Spec);
        public float StillFor;
        public Vector3 FadeFrom;
        /// <summary>Stay mode: lying there for good (until evicted by the cap or the mode ends).</summary>
        public bool Stayed;
    }

    FenceDebrisConfig _cfg = new();
    MapLoaderT _map = null!;
    bool _stay;
    PhysicsMaterial? _physics;
    readonly List<Piece> _pieces = new();
    readonly Queue<Piece> _stayed = new();
    int _bodies;

    /// <summary>The debris of a map's thin fences.</summary>
    public static FenceDebris Create(FenceDestructionConfig cfg, MapLoaderT map) => new() { Name = "FenceDebris", _cfg = cfg.Debris, _map = map };

    public override void _Ready()
    {
        TopLevel = true;
        GlobalTransform = Transform3D.Identity;
        if (!ShotFx.Enabled) { SetProcess(false); SetPhysicsProcess(false); return; }
        _physics = new PhysicsMaterial { Friction = 0.9f, Bounce = 0.05f };
    }

    bool Live => ShotFx.Enabled && _physics != null && IsInsideTree();

    /// <summary>The house rubble mode: stay or fade. Pieces already lying follow it.</summary>
    public void SetRubble(bool stay)
    {
        if (stay == _stay) return;
        _stay = stay;
        if (!Live) return;
        if (stay)
        {
            foreach (var p in _pieces)
                if (p.Phase.Is(DebrisPhase.Resting) && !p.Stayed) Keep(p);
            return;
        }
        // the ones lying for good fade as if they had just come to rest
        foreach (var p in _stayed) { p.Stayed = false; p.Phase.SetLeft(_cfg.FadeAfterS); }
        _stayed.Clear();
    }

    /// <summary>Frees every piece (round reset, leaving the server).</summary>
    public void ResetAll()
    {
        foreach (var p in _pieces) if (GodotObject.IsInstanceValid(p.Root)) p.Root.QueueFree();
        _pieces.Clear();
        _stayed.Clear();
        _bodies = 0;
    }

    /// <summary>
    /// Piece <paramref name="piece"/> of <paramref name="s"/> falls. <paramref name="push"/> is the flat direction it is pushed (map frame, length 0..1; none: to a side of the fence picked by the piece),
    /// <paramref name="speed"/> its speed in m/s.
    /// </summary>
    public void Fall(FenceSectionInfo s, int piece, Vector2 push, float speed)
    {
        if (!Live || _bodies >= _cfg.MaxBodies) return;
        var map = _map.Root.IsInsideTree() ? _map.Root.GlobalTransform : Transform3D.Identity;
        var at = map * ToGodot(s.PieceCentre(piece));
        if (GetViewport()?.GetCamera3D() is { } cam && cam.GlobalPosition.DistanceTo(at) > _cfg.MaxDistanceM) return;
        var (from, to) = s.PieceSpan(piece);
        var width = to - from;
        if (width <= 0.001f) return;
        var height = s.Top - s.Bottom;
        var thickness = Mathf.Max(s.ThicknessM, 0.01f);
        // local x along the fence, y up, z across it
        var along = new Vector3(s.Dir.X, 0, s.Dir.Y);
        var basis = map.Basis * new Basis(along, Vector3.Up, new Vector3(-s.Dir.Y, 0, s.Dir.X));
        var dir = new Vector3(push.X, 0, push.Y);
        if (dir.LengthSquared() < 0.01f) dir = new Vector3(-s.Dir.Y, 0, s.Dir.X) * ((s.Index + piece & 1) == 0 ? 1 : -1);
        dir = (map.Basis * dir).Normalized();
        var linear = dir * speed + Vector3.Up * KickUpMps;
        var spin = Vector3.Up.Cross(dir) * (SpinBase + SpinPerMps * speed);
        var body = new RigidBody3D
        {
            Name = "FencePiece", CollisionLayer = 0, CollisionMask = CollisionLayers.World, Mass = Mathf.Max(1f, width * height * thickness * 500f),
            PhysicsMaterialOverride = _physics, Transform = new Transform3D(basis, at), LinearVelocity = linear, AngularVelocity = spin, AngularDamp = 0.5f,
        };
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(width, height, Mathf.Max(thickness, MinColliderM)) } });
        var look = new MeshInstance3D { Mesh = LookMesh(s, from, to, width, height, thickness), MaterialOverride = LookMaterial(s) };
        body.AddChild(look);
        AddChild(body);
        _pieces.Add(new Piece { Root = body, Body = body, Look = look });
        _bodies++;
    }

    static Vector3 ToGodot(System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);

    static Color TintOf(uint tint) => Color.Color8((byte)(tint >> 16), (byte)(tint >> 8), (byte)tint);

    /// <summary>The map's texture material of a picket or wire section (the flat vertex-colour look when the map has not got it); plain is flat.</summary>
    Material LookMaterial(FenceSectionInfo s) =>
        s.Kind is FenceKind.Picket or FenceKind.Wire && _map.LibraryMaterial(FenceDestructionConfig.KindKey(s.Kind)) is { } mat ? mat : MapLoaderT.SharedFlatMaterial;

    /// <summary>A two-sided quad (picket, wire; the panel's UVs) or a box (plain), in the body's frame, vertex colour = the tint.</summary>
    static ArrayMesh LookMesh(FenceSectionInfo s, float from, float to, float width, float height, float thickness)
    {
        var colour = TintOf(s.Tint);
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        if (s.Kind == FenceKind.Plain)
        {
            arrays = new BoxMesh { Size = new Vector3(width, height, thickness) }.GetMeshArrays();
            var n = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array().Length;
            var colours = new Color[n];
            Array.Fill(colours, colour);
            arrays[(int)Mesh.ArrayType.Color] = colours;
        }
        else
        {
            float hw = width / 2, hh = height / 2;
            float u0 = s.Uv.X + s.Uv.Y * from, u1 = s.Uv.X + s.Uv.Y * to, vBottom = s.Uv.Z, vTop = s.Uv.W;
            Vector3 bl = new(-hw, -hh, 0), br = new(hw, -hh, 0), tr = new(hw, hh, 0), tl = new(-hw, hh, 0);
            Vector2 ubl = new(u0, vBottom), ubr = new(u1, vBottom), utr = new(u1, vTop), utl = new(u0, vTop);
            // Godot's front faces are clockwise seen from the front: the first three faces +z, the others -z
            arrays[(int)Mesh.ArrayType.Vertex] = new[] { tl, tr, br, tl, br, bl, tl, br, tr, tl, bl, br };
            arrays[(int)Mesh.ArrayType.TexUV] = new[] { utl, utr, ubr, utl, ubr, ubl, utl, ubr, utr, utl, ubl, ubr };
            var normals = new Vector3[12];
            Array.Fill(normals, Vector3.Back, 0, 6);
            Array.Fill(normals, Vector3.Forward, 6, 6);
            arrays[(int)Mesh.ArrayType.Normal] = normals;
            var colours = new Color[12];
            Array.Fill(colours, colour);
            arrays[(int)Mesh.ArrayType.Color] = colours;
        }
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    public override void _PhysicsProcess(double delta)
    {
        var dt = (float)delta;
        foreach (var p in _pieces)
        {
            if (p.Body is not { } body || !p.Phase.Is(DebrisPhase.Falling)) continue;
            p.Phase.Tick(dt);
            var still = body.LinearVelocity.LengthSquared() < DebrisRules.StillSpeed * DebrisRules.StillSpeed && body.AngularVelocity.LengthSquared() < DebrisRules.StillSpin * DebrisRules.StillSpin;
            p.StillFor = still ? p.StillFor + dt : 0f;
            if (DebrisRules.Rests(body.Sleeping, p.StillFor, p.Phase.Elapsed)) Rest(p);
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
                if (p.Phase.Tick(dt) && !p.Stayed) StartFade(p);
                continue;
            }
            if (!p.Phase.Is(DebrisPhase.Fading)) continue;
            p.Phase.Tick(dt);
            var t = _cfg.FadeS > 0 ? Mathf.Clamp(p.Phase.Elapsed / _cfg.FadeS, 0f, 1f) : 1f;
            if (GodotObject.IsInstanceValid(p.Root))
            {
                p.Root.Position = p.FadeFrom + Vector3.Down * (SinkM * t);
                p.Look.Transparency = t;
            }
            if (!p.Phase.Expired) continue;
            if (GodotObject.IsInstanceValid(p.Root)) p.Root.QueueFree();
            var last = _pieces.Count - 1;
            _pieces[i] = _pieces[last];
            _pieces.RemoveAt(last);
        }
    }

    /// <summary>Comes to rest: the body is freed (no physics for a piece that lies), its look stays where it lies.</summary>
    void Rest(Piece p)
    {
        p.Phase.Enter(DebrisPhase.Resting, _cfg.FadeAfterS);
        if (p.Body is { } body)
        {
            p.Look.Reparent(this); // keeps its world pose
            p.Root = p.Look;
            body.QueueFree();
            p.Body = null;
            _bodies--;
        }
        if (_stay) Keep(p);
    }

    /// <summary>Stay mode: this piece lies for good; over stay_cap the oldest ones fade.</summary>
    void Keep(Piece p)
    {
        p.Stayed = true;
        p.Phase.SetLeft(float.PositiveInfinity);
        _stayed.Enqueue(p);
        while (_stayed.Count > Math.Max(1, _cfg.StayCap))
        {
            var old = _stayed.Dequeue();
            old.Stayed = false;
            StartFade(old);
        }
    }

    void StartFade(Piece p)
    {
        p.Phase.Enter(DebrisPhase.Fading, _cfg.FadeS);
        p.FadeFrom = p.Root.Position;
    }
}
