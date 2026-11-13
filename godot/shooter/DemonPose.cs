using Godot;

namespace Drycastle.Shooter;

/// <summary>
/// The demon's crawl, laid over whatever clip plays (no pack clip crawls): the body goes prone on all fours with the belly towards the surface it holds
/// on to, the spine horizontal with a lifted chest and neck, the head free to look where <see cref="LookYaw"/> and <see cref="LookPitch"/> say (the view
/// moves them: tracking, twitches, over-rotation), and each hand and foot reaches a foothold on the surface plane through a two-bone IK that puts the
/// joints where they would be and turns the bones to them (shortest arc from the bone's direction in the pose, so it does not depend on how the rig's bones
/// are oriented). The footholds step in a diagonal gait driven by the distance travelled (<see cref="Gait"/>, in cycles), not by time, so the feet do not slide.
/// <c>Influence</c> fades the whole thing in: 0 is the upright animation, 1 the crawl, which is the drop on all fours.
/// <para>
/// Frame: skeleton space, the character faces +Z, y is up (as <see cref="SitPose"/>). The view turns that frame so +Y is the surface normal and +Z the
/// way it crawls; the surface is the plane y = 0, the left side is +X. Every length is measured from the rig's rest pose, so another rig of the same bones works.
/// </para>
/// </summary>
public sealed partial class DemonPose : SkeletonModifier3D
{
    /// <summary>Distance round one gait cycle at full speed, metres: the stance (60 %) covers the feet's travel from the front to the back of their reach.</summary>
    public const float CycleM = 1.3f;
    const float StanceShare = 0.6f;

    /// <summary>The hips' height as a share of the arm's length: low enough that the hands reach the surface from the raised shoulders.</summary>
    public float BodyShare = 0.78f;

    // inputs, set by the view every frame
    /// <summary>Gait phase in cycles (distance travelled / <see cref="CycleM"/>).</summary>
    public float Gait;
    /// <summary>0 still, 1 crawling at speed: how far the footholds swing and lift.</summary>
    public float Move;
    /// <summary>0..1: the chest lifts and the hands come off the surface (an arch, a roar).</summary>
    public float Rear;
    /// <summary>0..1: the right arm goes back for the throw.</summary>
    public float Wind;
    /// <summary>0..1: the right arm flung forward (the throw itself).</summary>
    public float Fling;
    /// <summary>0..1: dead, the limbs slack and spread, the body down.</summary>
    public float Limp;
    /// <summary>The chest's breathing and flinching, radians added to the spine.</summary>
    public float Breath;
    /// <summary>The head's look, radians from straight ahead (+Z): yaw to the left (+X), pitch up (+Y). Not clamped here.</summary>
    public float LookYaw, LookPitch, HeadRoll;

    sealed class Limb
    {
        public int A, B, C, Foot = -1;
        public float L1, L2, Side;
        public bool Arm, Right;
    }

    bool _init, _ok;
    int _hips = -1, _head = -1;
    int[] _spine = Array.Empty<int>();
    float[] _spineBend = Array.Empty<float>();
    float _spineLen, _armLen;
    Vector3 _headFace = Vector3.Back;
    readonly List<Limb> _limbs = new();

    // the bend of each bone from the hips up that carries the spine up from horizontal to the head looking ahead: they sum to -90 degrees
    static readonly string[] SpineNames = { "Spine_01", "Spine_02", "Spine_03", "Neck", "Head" };
    static readonly float[] Bend = { -5f, -8f, -8f, -30f, -39f };

    void Init(Skeleton3D s)
    {
        _init = true;
        _hips = s.FindBone("Hips"); _head = s.FindBone("Head");
        var neck = s.FindBone("Neck");
        if (_hips < 0 || _head < 0 || neck < 0) { GD.PrintErr("demon: the skeleton has no Hips, Neck and Head; no crawl"); return; }
        var list = new List<int>(); var bend = new List<float>();
        for (var i = 0; i < SpineNames.Length; i++)
            if (s.FindBone(SpineNames[i]) is var b and >= 0) { list.Add(b); bend.Add(Mathf.DegToRad(Bend[i])); }
        _spine = list.ToArray(); _spineBend = bend.ToArray();
        _spineLen = (s.GetBoneGlobalRest(neck).Origin - s.GetBoneGlobalRest(_hips).Origin).Length();
        _headFace = (s.GetBoneGlobalRest(_head).Basis.Inverse() * Vector3.Back).Normalized();
        foreach (var side in new[] { "L", "R" })
        {
            Add(s, $"Shoulder_{side}", $"Elbow_{side}", $"Hand_{side}", null, true, side == "R");
            Add(s, $"UpperLeg_{side}", $"LowerLeg_{side}", $"Ankle_{side}", $"Ball_{side}", false, side == "R");
        }
        _armLen = _limbs.Where(l => l.Arm).Select(l => l.L1 + l.L2).DefaultIfEmpty(0.55f).Average();
        _ok = _limbs.Count == 4;
        if (!_ok) GD.PrintErr("demon: the skeleton lacks arm or leg bones; no crawl");
    }

    void Add(Skeleton3D s, string a, string b, string c, string? foot, bool arm, bool right)
    {
        int ia = s.FindBone(a), ib = s.FindBone(b), ic = s.FindBone(c);
        if (ia < 0 || ib < 0 || ic < 0) return;
        var pa = s.GetBoneGlobalRest(ia).Origin; var pb = s.GetBoneGlobalRest(ib).Origin; var pc = s.GetBoneGlobalRest(ic).Origin;
        _limbs.Add(new Limb { A = ia, B = ib, C = ic, Foot = foot == null ? -1 : s.FindBone(foot), L1 = pa.DistanceTo(pb), L2 = pb.DistanceTo(pc), Side = pa.X >= 0 ? 1 : -1, Arm = arm, Right = right });
    }

    public override void _ProcessModificationWithDelta(double delta)
    {
        if (GetSkeleton() is not { } s) return;
        if (!_init) Init(s);
        if (!_ok) return;

        var body = BodyShare * _armLen * (1 - 0.75f * Limp);
        var step = Move * MathF.Sin(Gait * Mathf.Tau * 2f); // two sways per cycle
        // hips: pitched forward 90 degrees about the left-right axis (the spine now runs along +Z), centred over the surface point, a little sway and bob
        var hips = s.GetBoneGlobalPose(_hips);
        hips.Basis = new Basis(Vector3.Right, Mathf.Pi / 2) * hips.Basis;
        hips.Origin = new Vector3(0.04f * Move * MathF.Sin(Gait * Mathf.Tau), body + 0.02f * Move * MathF.Abs(step), -_spineLen * 0.5f);
        s.SetBoneGlobalPose(_hips, hips);
        // the spine curves up to a head that looks ahead; Rear arches it further, Breath moves it, Limp lets the head drop
        for (var i = 0; i < _spine.Length; i++)
        {
            var share = (i + 1f) / _spine.Length;
            var extra = -0.55f * Rear * (i < 3 ? 1 : 0.4f) + Breath * share - 0.5f * Limp * share * share;
            var g = s.GetBoneGlobalPose(_spine[i]);
            g.Basis = new Basis(Vector3.Right, _spineBend[i] + extra) * g.Basis;
            s.SetBoneGlobalPose(_spine[i], g);
        }
        Head(s);

        foreach (var l in _limbs) Reach(s, l);
    }

    /// <summary>The head turns to look at the yaw and pitch, round its own forward (so a tilt is a tilt of the face, not of the neck).</summary>
    void Head(Skeleton3D s)
    {
        var g = s.GetBoneGlobalPose(_head);
        var cur = (g.Basis * _headFace).Normalized();
        var cp = MathF.Cos(LookPitch);
        var want = new Vector3(MathF.Sin(LookYaw) * cp, MathF.Sin(LookPitch), MathF.Cos(LookYaw) * cp).Normalized();
        g.Basis = new Basis(new Quaternion(cur, want)) * g.Basis;
        if (MathF.Abs(HeadRoll) > 1e-3f) g.Basis = new Basis(want, HeadRoll) * g.Basis;
        s.SetBoneGlobalPose(_head, g);
    }

    void Reach(Skeleton3D s, Limb l)
    {
        var a = s.GetBoneGlobalPose(l.A).Origin;
        // diagonal gait: a left arm moves with the right leg, a right arm with the left leg
        var phase = Gait + (l.Arm == l.Right ? 0.5f : 0f);
        var u = phase - MathF.Floor(phase);
        float reach, lift;
        if (u < StanceShare) { reach = 1 - 2 * u / StanceShare; lift = 0; }
        else { var k = (u - StanceShare) / (1 - StanceShare); reach = -1 + 2 * k; lift = MathF.Sin(k * MathF.PI); }
        var half = 0.5f * StanceShare * CycleM * (l.Arm ? 0.8f : 1f);
        var ground = l.Arm ? 0.05f : 0.08f;
        var t = new Vector3(a.X + l.Side * (l.Arm ? 0.1f : 0.16f), ground, a.Z + (l.Arm ? 0.2f : -0.12f) + half * reach * Move);
        t.Y += lift * 0.2f * Move;
        // rearing: the hands come off the surface and up
        if (l.Arm) { t.Y += Rear * 0.5f; t.Z -= Rear * 0.1f; }
        // the throw: the right hand goes back and up, then flings forward
        if (l.Arm && l.Right)
        {
            var wind = new Vector3(a.X + 0.22f, a.Y + 0.55f * _armLen, a.Z - 0.55f * _armLen);
            var fling = new Vector3(a.X + 0.1f, a.Y - 0.05f, a.Z + 1.05f * _armLen);
            t = t.Lerp(wind, Wind);
            t = t.Lerp(fling, Fling);
        }
        // dead: the limbs spread out and lie
        if (Limp > 0) t = t.Lerp(new Vector3(a.X + l.Side * 0.75f * _armLen, 0.05f, a.Z + (l.Arm ? 0.3f : -0.4f)), Limp);
        // the joints bend away from the body and up; knees come forward, elbows go back
        var pole = new Vector3(l.Side, 1.2f, l.Arm ? -0.4f : 0.6f);
        Solve(s, l, t, pole);
        if (l.Foot >= 0) Aim(s, l.C, l.Foot, new Vector3(0, 0.05f, 1)); // the foot lies flat, toes ahead
    }

    /// <summary>Two-bone IK: the joint goes to where a limb of these two lengths bends when its end is at <paramref name="target"/>, towards <paramref name="pole"/>.</summary>
    static void Solve(Skeleton3D s, Limb l, Vector3 target, Vector3 pole)
    {
        var a = s.GetBoneGlobalPose(l.A).Origin;
        var d = target - a;
        var dist = d.Length();
        var max = l.L1 + l.L2 - 0.004f;
        if (dist < 1e-3f) return;
        if (dist > max) { target = a + d / dist * max; dist = max; }
        var dir = (target - a) / dist;
        var along = (l.L1 * l.L1 - l.L2 * l.L2 + dist * dist) / (2 * dist);
        var h = MathF.Sqrt(MathF.Max(l.L1 * l.L1 - along * along, 0));
        var perp = pole - dir * pole.Dot(dir);
        perp = perp.LengthSquared() < 1e-6f ? dir.Cross(Vector3.Up) : perp;
        perp = perp.Normalized();
        var joint = a + dir * along + perp * h;
        Aim(s, l.A, l.B, joint - a);
        Aim(s, l.B, l.C, target - s.GetBoneGlobalPose(l.B).Origin);
    }

    /// <summary>Turns a bone, about its own joint, by the shortest arc from the way it points now (to its child) to <paramref name="want"/>.</summary>
    static void Aim(Skeleton3D s, int bone, int child, Vector3 want)
    {
        var g = s.GetBoneGlobalPose(bone);
        var cur = s.GetBoneGlobalPose(child).Origin - g.Origin;
        if (cur.LengthSquared() < 1e-8f || want.LengthSquared() < 1e-8f) return;
        g.Basis = new Basis(new Quaternion(cur.Normalized(), want.Normalized())) * g.Basis;
        s.SetBoneGlobalPose(bone, g);
    }
}
