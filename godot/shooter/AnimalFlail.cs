using Godot;
using Drycastle.ShooterCore.Wildlife;

namespace Drycastle.Shooter;

/// <summary>
/// The loose limbs of an animal a vehicle threw: legs, neck, head, tail and ears (<see cref="AnimalFlailBones"/>) swing by their own weight as the body flies, rolls and lands,
/// instead of holding the clip's pose on a body turned whole. Each bone's far end is a point with inertia and weight in the world, pulled back to where the clip has it and held
/// within the bone's swing; the bone is turned to point at it. Not physics of the body: where the animal is and how it rolls is as before (the server's place, the client's
/// tumble), so nothing here can move it. A picture of each client's own. Runs as a skeleton modifier, after the animation; costs nothing while <see cref="Amount"/> is 0.
/// </summary>
public sealed partial class AnimalFlail : SkeletonModifier3D
{
    sealed class Joint
    {
        public int Bone;
        /// <summary>The bone's far end in its own frame: where its first child begins, or for a last bone the way it came from its parent.</summary>
        public Vector3 TipLocal;
        public float MaxAngle, Pull;
        public Vector3 Tip, Vel, PrevOrigin;
        public bool Seeded;
    }

    /// <summary>The weight on a loose end (m/s²), how fast its motion against the body dies (1/s), how fast the looseness comes and goes (1/s), and the longest step taken (s).</summary>
    const float Gravity = 20f, Damping = 4f, BlendRate = 6f, MaxStep = 0.05f;

    readonly List<Joint> _joints = new();
    float _blend;
    bool _seeded;

    /// <summary>0 = the clip as it is, 1 = fully loose. A landed body keeps a lower value: the limbs lie slack.</summary>
    public float Amount { get; set; }
    /// <summary>The ground's height under a body that lies (world): no loose end goes under it. Null in the air.</summary>
    public float? FloorY { get; set; }

    /// <returns>Null when the rig has no bone that swings.</returns>
    public static AnimalFlail? Create(Skeleton3D sk)
    {
        var f = new AnimalFlail { Name = "AnimalFlail", Active = true };
        for (var b = 0; b < sk.GetBoneCount(); b++) // parents come before their children: a neck is turned before the head on it
        {
            if (AnimalFlailBones.Classify(sk.GetBoneName(b)) is not { } kind) continue;
            var children = sk.GetBoneChildren(b);
            var rest = sk.GetBoneRest(b);
            var tip = children.Length > 0 ? sk.GetBoneRest(children[0]).Origin : rest.Basis.Orthonormalized().Inverse() * rest.Origin;
            if (tip.LengthSquared() < 1e-8f) continue;
            var swing = AnimalFlailBones.Of(kind);
            f._joints.Add(new Joint { Bone = b, TipLocal = tip, MaxAngle = swing.MaxAngle, Pull = swing.Pull });
        }
        return f._joints.Count == 0 ? null : f;
    }

    public override void _ProcessModificationWithDelta(double delta)
    {
        if (GetSkeleton() is not { } sk) return;
        var dt = Mathf.Min((float)delta, MaxStep);
        if (dt <= 0) return;
        _blend = Mathf.MoveToward(_blend, Mathf.Clamp(Amount, 0f, 1f), BlendRate * dt);
        if (_blend <= 0.001f)
        {
            if (_seeded) { foreach (var j in _joints) j.Seeded = false; _seeded = false; } // the next throw starts from the clip's pose
            return;
        }
        _seeded = true;
        var world = sk.GlobalTransform;
        var turn = world.Basis.GetRotationQuaternion();
        var keep = Mathf.Exp(-Damping * dt);
        foreach (var j in _joints)
        {
            var g = sk.GetBoneGlobalPose(j.Bone); // after the clip, and after the bones above it were turned
            var origin = world * g.Origin;
            var clipTip = world * (g * j.TipLocal);
            var arm = clipTip - origin;
            var len = arm.Length();
            if (len < 1e-5f) continue;
            var clipDir = arm / len;
            if (!j.Seeded) { j.Tip = clipTip; j.Vel = Vector3.Zero; j.PrevOrigin = origin; j.Seeded = true; }
            var carried = (origin - j.PrevOrigin) / dt; // the body's own motion at this joint: the end is damped against it, not against the world
            j.PrevOrigin = origin;
            j.Vel += (Vector3.Down * Gravity + (clipTip - j.Tip) * j.Pull) * dt;
            j.Vel = carried + (j.Vel - carried) * keep;
            j.Tip += j.Vel * dt;
            if (FloorY is { } floor && j.Tip.Y < floor) { j.Tip.Y = floor; if (j.Vel.Y < 0) j.Vel.Y = 0; } // it rests on the ground
            var to = j.Tip - origin;
            var dir = to.LengthSquared() > 1e-10f ? to.Normalized() : clipDir;
            var max = j.MaxAngle * _blend;
            var angle = clipDir.AngleTo(dir);
            if (angle > max)
            {
                var axis = clipDir.Cross(dir);
                dir = axis.LengthSquared() > 1e-10f ? clipDir.Rotated(axis.Normalized(), max) : clipDir;
                angle = max;
            }
            j.Tip = origin + dir * len; // the bone keeps its length
            if (angle < 1e-4f) continue;
            var q = turn.Inverse() * new Quaternion(clipDir, dir) * turn; // the turn from the clip's direction to the loose one, in the skeleton's frame
            g.Basis = new Basis(q) * g.Basis;
            sk.SetBoneGlobalPose(j.Bone, g);
        }
    }
}
