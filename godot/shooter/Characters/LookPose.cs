using Godot;

namespace Drycastle.Shooter.Characters;

/// <summary>
/// Where a seated rider looks (ADR 0026), over whatever clip plays: the head turns about the vertical and tips up and down, and the upper body twists over the
/// hips for a look beyond the neck's reach, so a rider on a fixed seat can look round without the figure leaving it. A skeleton modifier like
/// <see cref="ArmsReadyPose"/>: inactive (and free) at rest, which is every figure on foot. The angles are the sense of the figure's yaw (positive turns
/// left) and of its view pitch (positive looks up); the rig faces +Z and its up is +Y.
/// </summary>
public sealed partial class LookPose : SkeletonModifier3D
{
    int _torso = -1, _head = -1;
    float _torsoYaw, _headYaw, _pitch;

    /// <summary>The look limits the pose is held to, radians: the pitch, so a figure never breaks its neck whatever it is sent.</summary>
    const float MaxPitch = 1.0f;

    public static LookPose? Create(Skeleton3D sk)
    {
        int torso = sk.FindBone("Torso"), head = sk.FindBone("Head");
        if (head < 0) return null;
        return new LookPose { Name = "Look", Active = false, _torso = torso, _head = head };
    }

    /// <summary>Sets the look: the upper body's twist, the head's turn and the head's pitch, radians against the hips. All zero puts the figure back as the clip has it.</summary>
    public void Set(float torsoYaw, float headYaw, float pitch)
    {
        _torsoYaw = torsoYaw; _headYaw = headYaw; _pitch = Mathf.Clamp(pitch, -MaxPitch, MaxPitch);
        Active = Mathf.Abs(_torsoYaw) > 1e-4f || Mathf.Abs(_headYaw) > 1e-4f || Mathf.Abs(_pitch) > 1e-4f;
    }

    public override void _ProcessModificationWithDelta(double delta)
    {
        if (GetSkeleton() is not { } sk) return;
        if (_torso >= 0 && Mathf.Abs(_torsoYaw) > 1e-4f) Turn(sk, _torso, new Basis(Vector3.Up, _torsoYaw));
        // pitch first, about the rig's lateral axis, then the yaw about the vertical: the head turns on the neck and tips about the line of its ears
        Turn(sk, _head, new Basis(Vector3.Up, _headYaw) * new Basis(Vector3.Right, -_pitch));
    }

    /// <summary>Turns a bone about its own origin by <paramref name="by"/> (a rotation in the skeleton's axes); what hangs on it goes with it.</summary>
    static void Turn(Skeleton3D sk, int bone, Basis by)
    {
        var global = sk.GetBoneGlobalPose(bone);
        var parent = sk.GetBoneParent(bone);
        var parentGlobal = parent >= 0 ? sk.GetBoneGlobalPose(parent) : Transform3D.Identity;
        var turned = new Transform3D(by * global.Basis, global.Origin);
        sk.SetBonePose(bone, parentGlobal.AffineInverse() * turned);
    }
}
