using Godot;

namespace Suchohrad.Shooter;

/// <summary>
/// The limbs of a body thrown by a blast or a car (the ragdoll): arms and legs flail about while it is in the air, over a neutral clip, and hang
/// loose where they were when it lands. Shoulders and hips swing freely; elbows and knees only bend the way the rig bends them (axes measured
/// from the Run clip), so nothing folds backwards. Runs as a skeleton modifier, after the animation.
/// </summary>
public sealed partial class RagdollFlail : SkeletonModifier3D
{
    sealed class Joint
    {
        public int Bone;
        public Quaternion Rest;
        /// <summary>A hinge (elbow, knee) bends about <see cref="Axis"/> by <see cref="Lo"/>..<see cref="Hi"/> radians; a free joint swings by <see cref="Amp"/> about each local axis.</summary>
        public bool Hinge;
        public Vector3 Axis, Amp, Freq, Phase;
        public float Lo, Hi;
    }

    readonly List<Joint> _joints = new();
    float _t;
    float _blend; // 0..1 since the flail began, so the limbs leave the clip pose smoothly
    const float BlendTime = 0.12f;

    /// <summary>0 = the clip as it is, 1 = full flail. A landed body keeps a lower value so the limbs lie loose.</summary>
    public float Amount { get; set; }
    /// <summary>Hold the pose (a body at rest): time stops, so the limbs stay where they landed.</summary>
    public bool Frozen { get; set; }

    /// <returns>Null when the rig lacks the limb bones.</returns>
    public static RagdollFlail? Create(Skeleton3D sk)
    {
        var f = new RagdollFlail { Name = "RagdollFlail", Active = true };
        var rng = new RandomNumberGenerator();
        rng.Randomize();
        Vector3 Phase() => new(rng.Randf() * Mathf.Tau, rng.Randf() * Mathf.Tau, rng.Randf() * Mathf.Tau);
        void Free(string bone, Vector3 amp, Vector3 freq)
        {
            var i = sk.FindBone(bone);
            if (i >= 0) f._joints.Add(new Joint { Bone = i, Rest = sk.GetBoneRest(i).Basis.GetRotationQuaternion(), Amp = amp, Freq = freq, Phase = Phase() });
        }
        void Hinge(string bone, Vector3 axis, float lo, float hi, float freq)
        {
            var i = sk.FindBone(bone);
            if (i >= 0) f._joints.Add(new Joint { Bone = i, Rest = sk.GetBoneRest(i).Basis.GetRotationQuaternion(), Hinge = true, Axis = axis.Normalized(), Lo = lo, Hi = hi, Freq = new Vector3(freq, 0, 0), Phase = Phase() });
        }
        foreach (var side in new[] { "L", "R" })
        {
            Free("UpperArm." + side, new Vector3(1.2f, 0.9f, 1.3f), new Vector3(7f, 5.5f, 6.5f));
            Free("UpperLeg." + side, new Vector3(0.9f, 0.4f, 0.6f), new Vector3(6.5f, 5f, 7f));
            Hinge("LowerLeg." + side, Vector3.Right, 0.1f, 1.6f, 8f); // knees bend about +X
        }
        Hinge("LowerArm.L", new Vector3(0.19f, -0.76f, -0.62f), 0.1f, 1.4f, 9f);
        Hinge("LowerArm.R", new Vector3(-0.07f, 0.33f, 0.94f), 0.1f, 1.4f, 9.5f);
        Free("Torso", new Vector3(0.25f, 0.3f, 0.25f), new Vector3(4f, 3.5f, 4.5f));
        Free("Head", new Vector3(0.45f, 0.35f, 0.3f), new Vector3(8f, 6f, 7f));
        return f._joints.Count == 0 ? null : f;
    }

    public override void _ProcessModificationWithDelta(double delta)
    {
        if (GetSkeleton() is not { } sk) return;
        if (Amount <= 0.001f) { _blend = 0f; return; }
        _blend = Mathf.Min(1f, _blend + (float)delta / BlendTime); // eases in from the clip pose instead of snapping to the bind pose
        if (!Frozen) _t += (float)delta;
        foreach (var j in _joints)
        {
            Quaternion q;
            if (j.Hinge)
            {
                var s = 0.5f + 0.5f * Mathf.Sin(_t * j.Freq.X + j.Phase.X); // 0..1
                q = new Quaternion(j.Axis, Mathf.Lerp(0f, Mathf.Lerp(j.Lo, j.Hi, s), Amount));
            }
            else
            {
                var e = new Vector3(
                    j.Amp.X * Mathf.Sin(_t * j.Freq.X + j.Phase.X),
                    j.Amp.Y * Mathf.Sin(_t * j.Freq.Y + j.Phase.Y),
                    j.Amp.Z * Mathf.Sin(_t * j.Freq.Z + j.Phase.Z)) * Amount;
                q = Quaternion.FromEuler(e);
            }
            sk.SetBonePoseRotation(j.Bone, _blend >= 1f ? j.Rest * q : sk.GetBonePoseRotation(j.Bone).Slerp(j.Rest * q, _blend));
        }
    }
}
