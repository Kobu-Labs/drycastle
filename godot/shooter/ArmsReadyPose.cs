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
    /// <summary>Where the right hand holds the weapon, metres in the model's own frame (x is to the character's left, z forward). These bodies have big heads and short arms: the shoulders are at 1.07 m, so the chest is about 0.85 m, not the 1.3 m of a realistic figure. The torso's front is about 0.13 m ahead of the spine, so the hand is held well in front of it, or the weapon's butt (0.16 m behind the hand) goes through the body.</summary>
    static readonly Vector3 RightHand = new(-0.06f, 0.85f, 0.36f);
    /// <summary>How far ahead of the right hand the left hand reaches along the barrel, metres (the arms are short: a full arm is about 0.54 m).</summary>
    const float Foregrip = 0.12f;

    sealed class Arm
    {
        public int Upper, Lower, Fist;
        public Basis BindUpper, BindLower, BindFist;
        public Vector3 BindUpperDir, BindLowerDir;
        public float Upper2Lower, Lower2Fist;
        public float Out; // sign of the side the elbow points to
    }

    /// <summary>Which way the weapon points (model frame: z forward), a little above level.</summary>
    static readonly Vector3 Barrel = new Vector3(0, 0.04f, 1).Normalized();
    /// <summary>How far behind the right hand the weapon's origin (its butt end) sits for a two-handed weapon and a pistol, metres.</summary>
    const float ButtBehindHand = 0.16f, GripBehindHand = 0.03f;

    Arm? _right, _left;

    /// <summary>The node the weapon hangs on while armed. Placed here, not on the fist bone, so the weapon points exactly along <see cref="Barrel"/> whatever the hand's roll: the hands are posed onto it, it is not posed by them.</summary>
    public Node3D? Aim { get; set; }
    float _rigPerMetre = 1f;

    float _recoil;

    /// <summary>One shot: the weapon kicks back and up and the hands follow, then settles (about 0.1 s).</summary>
    public void Kick() => _recoil = 1f;

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
        _recoil = Mathf.Max(0f, _recoil - (float)delta * 9f);
        // a shot pushes the weapon back along the barrel and lifts the muzzle; the targets move, so the arms recoil with it
        var barrel = (Barrel + Vector3.Up * (_recoil * 0.12f)).Normalized();
        var rightTarget = (RightHand - barrel * (_recoil * 0.05f)) * _rigPerMetre;
        Solve(sk, _right, rightTarget, out _);
        if (TwoHanded) Solve(sk, _left, rightTarget + barrel * (Foregrip * _rigPerMetre), out _);
        if (Aim != null)
        {
            var z = -barrel;
            var x = Vector3.Up.Cross(z).Normalized();
            var origin = rightTarget - barrel * ((TwoHanded ? ButtBehindHand : GripBehindHand) * _rigPerMetre);
            Aim.Transform = new Transform3D(new Basis(x, z.Cross(x), z), origin);
        }
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
