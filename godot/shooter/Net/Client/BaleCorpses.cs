using Godot;
using Drycastle.Shooter.Characters;
using Drycastle.Shooter.Weapons;
using Drycastle.ShooterCore.Corpses;
using Drycastle.ShooterCore.Events;
using Drycastle.ShooterCore.Net;

namespace Drycastle.Shooter.Net.Client;

/// <summary>
/// The hay bale a corpse is drawn as (#331, #351): a corpse owned by <see cref="HarvesterRules.BaleOwner"/> is a round bale, and the body the harvester caught stands out of its top.
/// The server throws the bale from the unloader and moves it; this draws it, a placeholder cylinder without the paid pack.
/// </summary>
internal sealed class BaleCorpses
{
    /// <summary>The synty prefab of a bale lying on the ground (the field's own bales use the same).</summary>
    const string BaleAsset = "pack:synty-farm/SM_Prop_Hay_Bale_Round_01";

    readonly ClientContext _ctx;
    readonly CamoClient _camo;
    /// <summary>The player a bale (corpse id) has inside it: the body is drawn standing out of the bale for as long as it lies (until it burns).</summary>
    readonly Dictionary<ushort, ushort> _body = new();

    public BaleCorpses(ClientContext ctx, CamoClient camo) { _ctx = ctx; _camo = camo; }

    /// <summary>The bale has this player inside.</summary>
    public void Carry(ushort bale, ushort victim) => _body[bale] = victim;

    /// <summary>The bale is gone or burnt out: its body goes with it.</summary>
    public void Forget(ushort bale) => _body.Remove(bale);

    /// <summary>A new round: no bale carries anybody.</summary>
    public void Clear() => _body.Clear();

    /// <summary>
    /// The picture of a bale (Synty round bale, a cylinder without the pack), lying on its side on y = 0, its axis along x; no collision. The Synty mesh is centred on its origin, a cylinder along y
    /// (1.26 x 1.58 x 1.22 m): it is turned onto its side and lifted by its radius. Placed as it comes, its lower half is under the ground.
    /// </summary>
    public Node3D MakeModel()
    {
        var onSide = new Basis(Vector3.Back, Mathf.Pi / 2f); // the mesh's y (its axis) to -x, its x up
        var model = _ctx.Map?.InstantiateAsset(BaleAsset);
        if (model != null)
        {
            foreach (var co in model.FindChildren("*", "CollisionObject3D", true, false).OfType<CollisionObject3D>()) { co.CollisionLayer = 0; co.CollisionMask = 0; }
            model.Name = "Model";
            var mi = model as MeshInstance3D ?? model.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>().FirstOrDefault(); // the prefab: a plain root over the mesh, no transforms
            model.Transform = new Transform3D(onSide, new Vector3(0, mi?.Mesh is { } mesh ? -mesh.GetAabb().Position.X : BaleRadius, 0));
            return model;
        }
        return new MeshInstance3D
        {
            Name = "Model", Mesh = new CylinderMesh { TopRadius = BaleRadius, BottomRadius = BaleRadius, Height = 1.6f, RadialSegments = 12 },
            Transform = new Transform3D(onSide, new Vector3(0, BaleRadius, 0)), MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color("#c9a748"), Roughness = 1f },
        };
    }

    public CorpseView MakeCorpse(WorldReplica.Entity e)
    {
        var node = new Node3D { Name = $"Bale{e.Id}" };
        Node3D? spin = null;
        if (ShotFx.Enabled) { spin = new Node3D { Name = "Spin" }; spin.AddChild(MakeModel()); node.AddChild(spin); } // the bale tumbles about its middle: the model hangs under a node that turns
        _ctx.Owned.AddChild(node);
        return new CorpseView { Node = node, View = null, Bale = true, Motion = new BaleMotion { Spin = spin } };
    }

    /// <summary>
    /// A bale corpse as drawn (#351): the server throws it from the unloader, it lands and rolls to a stop; the position arrives at snapshot rate, so the picture follows it smoothly. It lies on its side with its
    /// axis across the way it goes and turns about that axis like a wheel (spinning in the air, by the distance covered on the ground), so it never dips into the ground; on the ground it leans with the slope
    /// under it. The caught body stands out of its top, swaying as the bale turns, upright at rest, for as long as the bale lies (until it burns). The server position is where it is looted.
    /// </summary>
    public void Render(ushort id, CorpseView v, WorldState s, Vector3 at, float dt)
    {
        var m = v.Motion!;
        v.Feet = at;
        var moving = s.PitchRad > CorpseRules.RollPitch * 0.5f;
        var flying = s.PitchRad > CorpseRules.FlightPitch * 0.5f;
        if (!m.Seen) m.Yaw = s.YawRad;
        var before = m.Seen ? m.Shown : at;
        m.Shown = !m.Seen || before.DistanceTo(at) > 6f ? at : before.Lerp(at, 1f - Mathf.Exp(-24f * dt));
        if (!moving && m.Shown.DistanceSquaredTo(at) < 1e-3f) m.Shown = at;
        m.Seen = true;
        var step = m.Shown - before;
        var along = new Vector3(step.X, 0, step.Z);
        var ds = along.Length();
        if (moving && ds > 1e-4f)
        {
            var axis = Vector3.Up.Cross(along / ds).Normalized(); // the horizontal axis across the way it goes: the bale's own axis (its local x)
            m.Yaw = Mathf.Atan2(-axis.Z, axis.X);
        }
        if (flying) m.Angle += Mathf.Clamp(dt > 0 ? ds / dt / BaleRadius : 0f, 3f, 10f) * dt;
        else if (moving) m.Angle += ds / BaleRadius; // rolling: a turn for each circumference
        else m.Angle = Mathf.Lerp(m.Angle, Mathf.Round(m.Angle / Mathf.Tau) * Mathf.Tau, 1f - Mathf.Exp(-8f * dt));
        if (flying) { m.UpTarget = Vector3.Up; m.Leaned = false; }
        else if (moving || !m.Leaned) { m.UpTarget = GroundUp(at); m.Leaned = !moving; } // at rest the slope is looked up once
        m.Up = m.Up.Lerp(m.UpTarget, 1f - Mathf.Exp(-10f * dt)).Normalized();
        v.Node.GlobalPosition = m.Shown;
        v.Node.Basis = new Basis(new Quaternion(Vector3.Up, m.Up)) * new Basis(Vector3.Up, m.Yaw);
        if (m.Spin is { } spin && GodotObject.IsInstanceValid(spin))
        {
            var turn = new Basis(Vector3.Right, m.Angle); // about its own axis; the top goes the way it rolls
            spin.Transform = new Transform3D(turn, BaleCentre - turn * BaleCentre);
            if (_body.TryGetValue(id, out var victim) && victim != 0)
            {
                m.Body ??= MakeBody(v.Node, victim);
                if (!GodotObject.IsInstanceValid(m.Body)) return;
                if (m.BodyLoose != moving) { m.BodyLoose = moving; m.Body.SetRagdoll(true, moving ? 1f : BaleBodyRestFlail, frozen: !moving); } // limbs fly while it moves, hang still at rest
                var sway = new Basis(Vector3.Right, BaleBodySwayRad * Mathf.Sin(m.Angle)); // it does not go round with the bale (the head would go under the ground): it rocks about the bale's middle
                m.Body.Transform = new Transform3D(sway, BaleCentre + sway * (new Vector3(0, BaleBodyFeetM, 0) - BaleCentre));
            }
        }
    }

    /// <summary>The terrain's up at <paramref name="at"/> (from four heights 0.6 m around it); straight up where the bale is not on the terrain (a roof, a road deck, no terrain).</summary>
    Vector3 GroundUp(Vector3 at)
    {
        if (_ctx.Map is not { } map) return Vector3.Up;
        const float e = 0.6f;
        float c = map.GroundHeight(at.X, at.Z), x0 = map.GroundHeight(at.X - e, at.Z), x1 = map.GroundHeight(at.X + e, at.Z), z0 = map.GroundHeight(at.X, at.Z - e), z1 = map.GroundHeight(at.X, at.Z + e);
        if (float.IsNaN(c + x0 + x1 + z0 + z1) || Mathf.Abs(c - at.Y) > 0.4f) return Vector3.Up;
        var up = new Vector3(x0 - x1, 2f * e, z0 - z1).Normalized();
        return up.Y < 0.7f ? Vector3.Up : up; // steeper than about 45 degrees: a step or a wall of the terrain, not a slope to lie on
    }

    /// <summary>The bale lies on its side: a cylinder of this radius, 1.6 m long along its local x; its middle is a radius up.</summary>
    const float BaleRadius = 0.63f;
    static readonly Vector3 BaleCentre = new(0, BaleRadius, 0);

    /// <summary>How high the caught body's feet are in the bale (m): the legs are in the hay, the body from the hips up stands out of the top (1.26 m). How loose its limbs hang at rest, how far it rocks (rad) as the bale turns.</summary>
    const float BaleBodyFeetM = 0.3f, BaleBodyRestFlail = 0.35f, BaleBodySwayRad = 0.5f;

    /// <summary>The body the reel caught, full size, stuck in the bale from the hips down, limbs loose (the dead player's look). It stays for as long as the bale does; <see cref="Render"/> rocks it.</summary>
    CharacterView MakeBody(Node3D parent, ushort victim)
    {
        var view = CharacterView.Make();
        _camo.Dress(view, victim);
        view.ShowThrown("");
        view.SetRagdoll(true, 1f);
        parent.AddChild(view);
        return view;
    }
}
