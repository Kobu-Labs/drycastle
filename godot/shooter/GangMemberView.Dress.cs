using Godot;
using Drycastle.ShooterCore.Thieves;

namespace Drycastle.Shooter;

/// <summary>
/// What a member wears, each a <see cref="BoneAttachment3D"/> on the skeleton (docs/events/thieves/08, thieves.json <c>gang.view.dress</c>): the Enforcer's pipe in the right hand, a lit cigarette at the mouth of one
/// member in three, the Gang boss's gold chain at the neck, and the speaker on the hip (put on by <see cref="MountSpeaker"/> when the audio says the member has one). Offsets are in the rest pose's axes from the bone's
/// origin, so a bone's own roll does not matter; the pipe's grip alone is in its hand bone's frame (the pack's weapon convention). A prop whose file is missing is left out (the pipe is a stick then).
/// </summary>
public sealed partial class GangMemberView
{
    static Vector3 V3(float[] a) => new(a[0], a[1], a[2]);
    static Basis Rot(float[] deg) => Basis.FromEuler(new Vector3(Mathf.DegToRad(deg[0]), Mathf.DegToRad(deg[1]), Mathf.DegToRad(deg[2])));

    /// <summary>The attachment of <paramref name="child"/> to <paramref name="bone"/>, placed at <paramref name="rest"/> (a transform in the skeleton's rest pose) and following the bone from there.</summary>
    BoneAttachment3D? Mount(string bone, Transform3D rest, Node3D child, string name)
    {
        if (_skeleton == null || _skeleton.FindBone(bone) is var i && i < 0) { child.Free(); return null; }
        var att = new BoneAttachment3D { Name = name, BoneName = bone };
        _skeleton.AddChild(att);
        var holder = new Node3D { Name = "Holder", Transform = _skeleton.GetBoneGlobalRest(i).AffineInverse() * rest };
        holder.AddChild(child);
        att.AddChild(holder);
        return att;
    }

    /// <summary>A rest-pose transform <paramref name="offset"/> from the origin of <paramref name="bone"/>, turned by <paramref name="rotDeg"/> (Euler degrees).</summary>
    Transform3D RestAt(string bone, float[] offset, float[] rotDeg)
    {
        var i = _skeleton!.FindBone(bone);
        return new Transform3D(Rot(rotDeg), (i < 0 ? Vector3.Zero : _skeleton.GetBoneGlobalRest(i).Origin) + V3(offset));
    }

    void AddDress()
    {
        var d = _view.Dress;
        if (GangLook.Smokes(_view, _look) && SyntyLib.Instance(d.Cigarette.Model, d.Cigarette.Atlas) is { } cig)
            Mount(d.Cigarette.Bone, RestAt(d.Cigarette.Bone, d.Cigarette.Offset, d.Cigarette.RotationDeg), cig, "Cigarette");
        if (_kind == GangMemberKind.GangBoss && _chainModel != "" && ModelLib.Instance(_chainModel) is { } chain)
        {
            chain.Scale = Vector3.One * d.Chain.Scale;
            Mount(d.Chain.Bone, RestAt(d.Chain.Bone, d.Chain.Offset, d.Chain.RotationDeg), chain, "Chain");
        }
    }

    /// <summary>The Enforcer's rusty pipe: the pack's pipe model gripped in the right hand (the grip is data), else a thin cylinder (also on the capsule, at its right hip).</summary>
    void AddPipe()
    {
        var pipe = _view.Dress.Pipe;
        Node3D body;
        if (_skeleton != null && _skeleton.FindBone(pipe.Bone) >= 0 && SyntyLib.Instance(pipe.Model, pipe.Atlas) is { } model)
        {
            model.Transform = new Transform3D(Rot(pipe.ModelRotationDeg), V3(pipe.ModelOffset));
            body = model;
        }
        else body = Stick();
        if (_skeleton != null && _skeleton.FindBone(pipe.Bone) >= 0)
        {
            var att = new BoneAttachment3D { Name = "PipeHand", BoneName = pipe.Bone };
            _skeleton.AddChild(att);
            var grip = new Node3D { Name = "Grip", Transform = new Transform3D(new Basis(new Quaternion(pipe.Rotation[0], pipe.Rotation[1], pipe.Rotation[2], pipe.Rotation[3]).Normalized()), V3(pipe.Position)) };
            att.AddChild(grip);
            grip.AddChild(body);
            if (body.Name == "Pipe") body.Position = new Vector3(0, 0.3f, 0); // the stick's middle, along the blade axis of the grip
        }
        else
        {
            _pivot.AddChild(body);
            body.Position = new Vector3(0.38f, 0.8f, -0.25f);
            body.RotationDegrees = new Vector3(-30, 0, 0);
        }
    }

    static MeshInstance3D Stick() => new()
    {
        Name = "Pipe", Mesh = new CylinderMesh { TopRadius = 0.035f, BottomRadius = 0.035f, Height = 0.95f },
        MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color("#5a3b2a"), Roughness = 1f, Metallic = 0.3f },
    };

    BoneAttachment3D? _speaker;

    /// <summary>
    /// The speaker on this member's hip (the model of thieves.json <c>dress.speaker</c>, scaled down), or null when there is no model or skeleton (the audio then draws its box). The node returned is the
    /// attachment: freeing it takes the speaker off.
    /// </summary>
    public Node3D? MountSpeaker()
    {
        if (_speaker != null && IsInstanceValid(_speaker)) return _speaker;
        var s = _view.Dress.Speaker;
        if (_skeleton == null || SyntyLib.Instance(s.Model, s.Atlas) is not { } model) return null;
        model.Scale = Vector3.One * s.Scale;
        return _speaker = Mount(s.Bone, RestAt(s.Bone, s.Offset, s.RotationDeg), model, "Speaker");
    }
}
