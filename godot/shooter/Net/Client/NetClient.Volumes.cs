using Godot;
using Drycastle.Shooter.Dev;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Weapons;
using Drycastle.ShooterCore.Wildlife;

namespace Drycastle.Shooter.Net.Client;

/// <summary>
/// The F3 bounding-volume overlay (#479): which volumes a shot, a step and a bump are tested against, drawn on this client. F3 (debug_volumes, rebindable) toggles it,
/// [ and ] (debug_volumes_prev / _next) step through Hit shape, Body shape, Static colliders and ALL. Off by default and not remembered; the overlay node is built on
/// the first F3 and freed on the next, and while off nothing here runs. Every volume is built from the numbers the server tests against (HitMath, the vehicle hull
/// boxes, the animal shapes, the physics shapes of the static world), at the pose this client draws: other players and animals are interpolated, so a shot may differ by
/// the interpolation delay.
/// </summary>
public partial class NetClient
{
    /// <summary>How far from the camera volumes are drawn (m), and how often the static colliders are looked up again (s).</summary>
    const float VolumeRange = 40f, StaticRefresh = 0.25f;
    /// <summary>A static collider with a side longer than this (m) is the ground or a hall, not a prop: not drawn.</summary>
    const float StaticMaxSide = 60f;

    VolumeOverlay? _volumes;
    double _staticAt;
    readonly List<(Transform3D Xf, Shape3D Shape)> _staticShapes = new();
    readonly SphereShape3D _staticProbe = new() { Radius = VolumeRange };
    readonly PhysicsShapeQueryParameters3D _staticQuery = new();

    /// <summary>F3 toggles the overlay; [ and ] step the mode while it is on. Not under a menu. True when the key was used.</summary>
    bool HandleVolumeInput(InputEvent e)
    {
        if (e is not InputEventKey { Echo: false, Pressed: true }) return false;
        var toggle = e.IsActionPressed("debug_volumes");
        var step = _volumes == null ? 0 : e.IsActionPressed("debug_volumes_next") ? 1 : e.IsActionPressed("debug_volumes_prev") ? -1 : 0;
        if (!toggle && step == 0) return false;
        if (MenuOpen?.Invoke() == true || _hud == null) return false;
        if (toggle)
        {
            if (_volumes == null)
            {
                _volumes = new VolumeOverlay { Name = "VolumeOverlay" };
                Owned.AddChild(_volumes);
                _staticAt = 0;
            }
            else { _volumes.QueueFree(); _volumes = null; _staticShapes.Clear(); _hud.SetVolumeMode(null); return true; }
        }
        else _volumes!.Step(step);
        _hud.SetVolumeMode(_volumes!.ModeText());
        return true;
    }

    /// <summary>Once a frame while the overlay is on (not called when off).</summary>
    void UpdateVolumes()
    {
        var o = _volumes;
        if (o == null || !GodotObject.IsInstanceValid(o) || _me == null) return;
        var cam = GetViewport()?.GetCamera3D()?.GlobalPosition ?? _me.GlobalPosition;
        o.Begin(cam);
        var wantsPeople = o.Wants(VolumeKind.Hit) || o.Wants(VolumeKind.Body);
        if (wantsPeople) VolumePeople(o, cam);
        if (wantsPeople) VolumeAnimals(o, cam);
        if (wantsPeople) VolumeVehicles(o, cam);
        if (o.Wants(VolumeKind.Static)) VolumeStatic(o, cam);
        o.End();
    }

    static bool Near(Vector3 a, Vector3 cam) => a.DistanceSquaredTo(cam) <= VolumeRange * VolumeRange;

    /// <summary>A person: the capsule of the movement for contact (Body) and for shots (Hit, the same capsule plus the head sphere), as HitMath builds it.</summary>
    static void VolumePerson(VolumeOverlay o, Vector3 feet, Stance stance, float yaw, float scale, string name)
    {
        var t = new HitTarget(0, new System.Numerics.Vector3(feet.X, feet.Y, feet.Z), stance, yaw, Scale: scale);
        var (a, b) = HitMath.Axis(t);
        var r = HitMath.BodyRadius(t);
        Vector3 G(System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);
        o.Capsule(VolumeKind.Body, G(a), G(b), r * 0.97f, name); // a hair smaller, so the two stay apart where they are the same shape (people)
        o.Capsule(VolumeKind.Hit, G(a), G(b), r, name, tag: !o.Wants(VolumeKind.Body));
        var head = HitMath.HeadCenter(t);
        o.Sphere(VolumeKind.Hit, G(head), PlayerHitBox.HeadRadius * scale, "", tag: false);
    }

    void VolumePeople(VolumeOverlay o, Vector3 cam)
    {
        if (_me!.GlobalPosition is var me && Near(me, cam) && !Dead) VolumePerson(o, me, _me.State.Stance, _me.Yaw, 1f, "you");
        foreach (var (id, r) in _remotes)
        {
            if (r.Node == null || !GodotObject.IsInstanceValid(r.Node) || !r.Node.Visible || r.State == EntityState.Dead) continue;
            var at = r.Node.GlobalPosition;
            if (Near(at, cam)) VolumePerson(o, at, r.Stance, r.Node.GlobalRotation.Y, 1f, _names.TryGetValue(id, out var n) ? n : $"player {id}");
        }
        foreach (var c in _citizens.Views) VolumeNpc(o, c, cam, "citizen");
        foreach (var g in _gang.Values) VolumeNpc(o, g.View, cam, "gang");
        foreach (var d in _demons.Values) VolumeNpc(o, d.View, cam, "demon");
    }

    static void VolumeNpc(VolumeOverlay o, Node3D? view, Vector3 cam, string name)
    {
        if (view == null || !GodotObject.IsInstanceValid(view) || !view.Visible) return;
        var at = view.GlobalPosition;
        if (Near(at, cam)) VolumePerson(o, at, Stance.Stand, view.GlobalRotation.Y, view.GlobalTransform.Basis.Scale.Y, name);
    }

    void VolumeAnimals(VolumeOverlay o, Vector3 cam)
    {
        foreach (var (id, v) in _animals)
        {
            if (!WorldEntities.Entities.TryGetValue(id, out var e) || e.Dormant || !v.Node.Visible) continue;
            var feet = v.Node.GlobalPosition;
            if (!Near(feet, cam)) continue;
            var yaw = v.Node.GlobalRotation.Y;
            var box = v.HitBox; // the look's box times its size
            if (box.Count < 3) continue;
            var state = (AnimalState)(e.State.State & 0x1F);
            // Body shape: the box on the ground plane (width, length), centred BodyAhead ahead, as the server builds it
            var bodyAhead = v.Def.BodyAheadOf(v.Variant) * v.Size;
            var fwd = new Vector3(-MathF.Sin(yaw), 0, -MathF.Cos(yaw));
            o.Box(VolumeKind.Body, new Transform3D(new Basis(Vector3.Up, yaw), feet + fwd * bodyAhead + Vector3.Up * (box[1] / 2)), new Vector3(box[0], box[1], box[2]), v.Def.Id);
            // Hit shape: the outline in the pose it is drawn in (the first frame of eating: the clip time is the server's), else the hit box
            var shape = WildlifeKeeper.PoseOf(state) is { } pose ? AnimalShapes.At(v.Def.ShapeKey(v.Variant), pose, 0) : null;
            if (shape is { } s) o.Outline(VolumeKind.Hit, s.Shape, feet, yaw, v.Size, v.Def.Id);
            else
            {
                var hitFwd = fwd * (v.Def.HitBoxAhead * v.Size);
                o.Box(VolumeKind.Hit, new Transform3D(new Basis(Vector3.Up, yaw), feet + hitFwd + Vector3.Up * (box[1] / 2)), new Vector3(box[0], box[1], box[2]) * 1.01f, v.Def.Id + " (box)");
            }
        }
    }

    void VolumeVehicles(VolumeOverlay o, Vector3 cam)
    {
        if (Vehicles == null) return;
        foreach (var v in _cars.Values)
        {
            var feet = new Vector3(v.Pos.X, v.Pos.Y, v.Pos.Z);
            if (!Near(feet, cam)) continue;
            var def = Vehicles.Vehicles[v.Kind];
            var (pitch, roll) = def.Tilts ? HullTiltOf(v, def) : (0f, 0f);
            var frame = new Transform3D(Basis.FromEuler(new Vector3(pitch, v.Yaw, roll)), feet);
            foreach (var b in def.HullBoxes)
            {
                var size = new Vector3(b.HalfWidth * 2, b.Y1 - b.Y0, b.HalfLength * 2);
                var local = new Vector3(b.Cx, (b.Y0 + b.Y1) / 2, b.Cz);
                o.Box(VolumeKind.Body, frame * new Transform3D(Basis.Identity, local), size, def.Id);
                o.Box(VolumeKind.Hit, frame * new Transform3D(Basis.Identity, local), size * 1.015f, def.Id, tag: !o.Wants(VolumeKind.Body));
            }
            if (def.Flight is { } f) // the helicopter's rotor disc: a Body shape of its own, level
                o.Disc(VolumeKind.Body, feet + Vector3.Up * f.RotorHeight, Vector3.Up, f.RotorRadius, def.Id + " rotor");
        }
    }

    /// <summary>Static colliders within range: props, tree trunks and crowns, buildings that are boxes. Looked up four times a second from the physics space.</summary>
    void VolumeStatic(VolumeOverlay o, Vector3 cam)
    {
        if (Now - _staticAt > StaticRefresh)
        {
            _staticAt = Now;
            _staticShapes.Clear();
            _staticProbe.Radius = VolumeRange;
            _staticQuery.Shape = _staticProbe;
            _staticQuery.Transform = new Transform3D(Basis.Identity, cam);
            _staticQuery.CollisionMask = CollisionLayers.World | CollisionLayers.ShotOnly;
            var hits = World.GetWorld3D().DirectSpaceState.IntersectShape(_staticQuery, 400);
            var seen = new HashSet<(ulong, int)>();
            foreach (var h in hits)
            {
                if (h["collider"].AsGodotObject() is not CollisionObject3D co || !seen.Add((co.GetInstanceId(), h["shape"].AsInt32()))) continue;
                var idx = h["shape"].AsInt32();
                var owner = co.ShapeFindOwner(idx);
                var shape = co.ShapeOwnerGetShapeCount(owner) > 0 ? co.ShapeOwnerGetShape(owner, 0) : null; // one shape per owner (a CollisionShape3D)
                if (shape == null) continue;
                _staticShapes.Add((co.GlobalTransform * co.ShapeOwnerGetTransform(owner), shape));
            }
        }
        foreach (var (xf, shape) in _staticShapes)
        {
            switch (shape)
            {
                case BoxShape3D b when b.Size.X < StaticMaxSide && b.Size.Z < StaticMaxSide: o.Box(VolumeKind.Static, xf, b.Size, "box"); break;
                case SphereShape3D s: o.Sphere(VolumeKind.Static, xf.Origin, s.Radius * xf.Basis.Scale.X, "sphere"); break;
                case CapsuleShape3D c:
                {
                    var half = MathF.Max(c.Height / 2 - c.Radius, 0) * xf.Basis.Y.Length();
                    var up = xf.Basis.Y.Normalized();
                    o.Capsule(VolumeKind.Static, xf.Origin - up * half, xf.Origin + up * half, c.Radius * xf.Basis.Scale.X, "capsule"); break;
                }
                case CylinderShape3D c: o.Cylinder(VolumeKind.Static, xf, c.Radius * xf.Basis.Scale.X, c.Height * xf.Basis.Scale.Y, "cylinder"); break;
                case ConvexPolygonShape3D p when p.Points.Length > 0: // its hull is not drawn: the box around its points
                {
                    Vector3 lo = p.Points[0], hi = p.Points[0];
                    foreach (var pt in p.Points) { lo = lo.Min(pt); hi = hi.Max(pt); }
                    o.Box(VolumeKind.Static, xf * new Transform3D(Basis.Identity, (lo + hi) / 2), hi - lo, "convex");
                    break;
                }
            }
        }
    }
}
