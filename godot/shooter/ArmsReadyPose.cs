using Godot;

namespace Suchohrad.Shooter;

/// <summary>
/// Poses the arms of a character on its weapon, over whatever clip plays (the rig has no rifle clips). The right hand goes to a fixed point
/// at the chest and, for two-handed weapons, the left hand to the foregrip further along the right forearm's line, each with a two-bone solve
/// (shoulder, elbow, hand). Bone rolls come from the shortest turn of the T-pose bind orientation, so the hands keep a sensible twist.
/// Runs as a skeleton modifier, after the animation. Inactive when nothing is held, when swinging a knife and when dead.
/// </summary>
public sealed partial class ArmsReadyPose : SkeletonModifier3D
{
    /// <summary>Where the right hand holds the weapon, metres in the model's own frame (x is to the character's left, z forward).</summary>
    static readonly Vector3 RightHand = new(-0.12f, 1.28f, 0.34f);
    /// <summary>How far ahead of the right hand the left hand reaches along the barrel, metres.</summary>
    const float Foregrip = 0.42f;

    sealed class Arm
    {
        public int Upper, Lower, Fist;
        public Basis BindUpper, BindLower, BindFist;
        public Vector3 BindUpperDir, BindLowerDir;
        public float Upper2Lower, Lower2Fist;
        public float Out; // sign of the side the elbow points to
    }

    Arm? _right, _left;
    float _rigPerMetre = 1f;

    /// <summary>The left arm reaches for the foregrip too.</summary>
    public bool TwoHanded { get; set; } = true;

    /// <param name="bind">Bind-pose global transform by bone index (<c>CharacterView.BindPoses</c>).</param>
    /// <param name="rigPerMetre">Rig units per metre (1 / the model's draw scale).</param>
    /// <returns>Null when the rig lacks the arm bones.</returns>
    public static ArmsReadyPose? Create(Skeleton3D sk, Dictionary<int, Transform3D> bind, float rigPerMetre)
    {
        var right = MakeArm(sk, bind, "R");
        var left = MakeArm(sk, bind, "L");
        if (right == null || left == null) return null;
        return new ArmsReadyPose { Name = "ArmsReady", Active = false, _right = right, _left = left, _rigPerMetre = rigPerMetre };
    }

    static Arm? MakeArm(Skeleton3D sk, Dictionary<int, Transform3D> bind, string side)
    {
        int upper = sk.FindBone("UpperArm." + side), lower = sk.FindBone("LowerArm." + side), fist = sk.FindBone("Fist." + side);
        if (upper < 0 || lower < 0 || fist < 0 || !bind.ContainsKey(upper) || !bind.ContainsKey(lower) || !bind.ContainsKey(fist)) return null;
        var u = bind[upper];
        var l = bind[lower];
        var f = bind[fist];
        return new Arm
        {
            Upper = upper, Lower = lower, Fist = fist,
            BindUpper = u.Basis, BindLower = l.Basis, BindFist = f.Basis,
            BindUpperDir = (l.Origin - u.Origin).Normalized(), BindLowerDir = (f.Origin - l.Origin).Normalized(),
            Upper2Lower = (l.Origin - u.Origin).Length(), Lower2Fist = (f.Origin - l.Origin).Length(),
            Out = side == "R" ? -1f : 1f,
        };
    }

    public override void _ProcessModificationWithDelta(double delta)
    {
        if (GetSkeleton() is not { } sk || _right == null || _left == null) return;
        var hand = Solve(sk, _right, RightHand * _rigPerMetre, out var forward);
        if (TwoHanded) Solve(sk, _left, hand + forward * (Foregrip * _rigPerMetre), out _);
    }

    /// <summary>Bends the arm so its hand reaches <paramref name="target"/> (skeleton space). Returns where the hand ends up and the forearm's direction.</summary>
    static Vector3 Solve(Skeleton3D sk, Arm a, Vector3 target, out Vector3 forearm)
    {
        var shoulder = sk.GetBoneGlobalPose(a.Upper).Origin;
        var d = target - shoulder;
        var reach = Mathf.Clamp(d.Length(), Mathf.Abs(a.Upper2Lower - a.Lower2Fist) + 0.01f, a.Upper2Lower + a.Lower2Fist - 0.001f);
        var u = d.Length() > 0.0001f ? d.Normalized() : Vector3.Forward;
        // the elbow sits down and out from the shoulder-hand line
        var pole = new Vector3(a.Out * 0.5f, -1f, -0.3f);
        pole = (pole - u * pole.Dot(u)).Normalized();
        var cosA = Mathf.Clamp((a.Upper2Lower * a.Upper2Lower + reach * reach - a.Lower2Fist * a.Lower2Fist) / (2f * a.Upper2Lower * reach), -1f, 1f);
        var sinA = Mathf.Sqrt(1f - cosA * cosA);
        var elbow = shoulder + a.Upper2Lower * (u * cosA + pole * sinA);
        var hand = shoulder + u * reach;
        var upperDir = (elbow - shoulder).Normalized();
        var lowerDir = (hand - elbow).Normalized();
        forearm = lowerDir;

        var lowerTurn = new Basis(new Quaternion(a.BindLowerDir, lowerDir));
        SetGlobalRotation(sk, a.Upper, new Basis(new Quaternion(a.BindUpperDir, upperDir)) * a.BindUpper);
        SetGlobalRotation(sk, a.Lower, lowerTurn * a.BindLower);
        SetGlobalRotation(sk, a.Fist, lowerTurn * a.BindFist);
        return hand;
    }

    static void SetGlobalRotation(Skeleton3D sk, int bone, Basis global)
    {
        var parent = sk.GetBoneParent(bone);
        var parentBasis = parent >= 0 ? sk.GetBoneGlobalPose(parent).Basis.Orthonormalized() : Basis.Identity;
        sk.SetBonePoseRotation(bone, (parentBasis.Inverse() * global.Orthonormalized()).GetRotationQuaternion());
    }
}
