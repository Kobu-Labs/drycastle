using Godot;

namespace Drycastle.Shooter.Thieves;

/// <summary>
/// The carry overlay of a Gang member (docs/events/thieves/08): a <see cref="SkeletonModifier3D"/> on the gang rig that reaches the hands to <see cref="Right"/> and <see cref="Left"/> with a two-bone arm (the rig's
/// Shoulder_x is the upper arm, Elbow_x the forearm), over whatever clip is playing; <c>Influence</c> fades it in and out. The goals are in the rest pose's axes (the model faces +Z, up +Y, its right hand is -X) and ride
/// the chest bone, so the hands move with the body the clip animates. An arm whose goal is null is left to the clip; a goal beyond reach is reached for with the arm straight. The elbow bends towards a pole
/// (<see cref="ElbowDown"/> and <see cref="ElbowOut"/>, weights in the chest's frame), and the hand rolls about the forearm (<see cref="HandRollDeg"/>, the right hand; the left the other way: positive turns the palms inward).
/// The shape of the poses (TwoHands, UnderArm, OneHand) is data: thieves.json <c>gang.view.carry.poses</c>.
/// </summary>
public sealed partial class CarryPose : SkeletonModifier3D
{
    public string ChestBone = "Spine_03";
    public Vector3? Right, Left;
    public float ElbowDown = 1f, ElbowOut = 0.4f, HandRollDeg;

    public override void _ProcessModificationWithDelta(double delta)
    {
        var s = GetSkeleton();
        if (s == null) return;
        var chest = s.FindBone(ChestBone);
        if (chest < 0) return;
        var follow = s.GetBoneGlobalPose(chest) * s.GetBoneGlobalRest(chest).AffineInverse(); // the rest pose moved onto the body as it is animated now
        if (Right is { } r) Reach(s, "R", follow, r, -1f);
        if (Left is { } l) Reach(s, "L", follow, l, 1f);
    }

    /// <summary>One arm to a goal. <paramref name="outX"/> is the side the elbow goes out to (-1 right, +1 left, in the rest pose's X).</summary>
    void Reach(Skeleton3D s, string side, Transform3D follow, Vector3 restGoal, float outX)
    {
        int up = s.FindBone("Shoulder_" + side), el = s.FindBone("Elbow_" + side), ha = s.FindBone("Hand_" + side);
        if (up < 0 || el < 0 || ha < 0) return;
        Transform3D restUp = s.GetBoneGlobalRest(up), restEl = s.GetBoneGlobalRest(el), restHa = s.GetBoneGlobalRest(ha);
        var upperRest = restEl.Origin - restUp.Origin;
        var lowerRest = restHa.Origin - restEl.Origin;
        float l1 = upperRest.Length(), l2 = lowerRest.Length();
        var shoulder = s.GetBoneGlobalPose(up).Origin;
        var d = follow * restGoal - shoulder;
        var dist = d.Length();
        if (dist < 0.05f || l1 < 1e-3f || l2 < 1e-3f) return;
        var dir = d / dist;
        var reach = Mathf.Clamp(dist, Mathf.Abs(l1 - l2) + 0.02f, l1 + l2 - 0.01f);
        var along = (l1 * l1 - l2 * l2 + reach * reach) / (2f * reach);   // the elbow's foot on the shoulder-hand line, then out to the side by the upper arm's remaining length
        var height = Mathf.Sqrt(Mathf.Max(0f, l1 * l1 - along * along));
        var pole = follow.Basis * new Vector3(outX * ElbowOut, -ElbowDown, 0f);
        pole -= dir * pole.Dot(dir);
        if (pole.LengthSquared() < 1e-6f) pole = dir.Cross(Vector3.Up);
        pole = pole.Normalized();
        var elbow = shoulder + dir * along + pole * height;
        var hand = shoulder + dir * reach;
        var qUp = new Quaternion(upperRest.Normalized(), (elbow - shoulder).Normalized());
        var qLow = new Quaternion(lowerRest.Normalized(), (hand - elbow).Normalized());
        s.SetBoneGlobalPose(up, new Transform3D(new Basis(qUp) * restUp.Basis, shoulder));
        s.SetBoneGlobalPose(el, new Transform3D(new Basis(qLow) * restEl.Basis, elbow));
        var roll = new Basis((hand - elbow).Normalized(), Mathf.DegToRad(-outX * HandRollDeg));
        s.SetBoneGlobalPose(ha, new Transform3D(roll * new Basis(qLow) * restHa.Basis, hand));
    }
}
