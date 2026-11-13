using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// A citizen's body on the client: a Synty peasant (<see cref="SyntyLib"/>; the model name is <see cref="CitizenModelDef.File"/>) in one of the
/// pack's 15 recolours, with the pack clips for walking, standing idle, dying and kneeling in prayer. No clip sits down, so sitting is made here:
/// the idle clip keeps playing and <see cref="SitPose"/> bends the legs and puts the pelvis on the pew. Model, recolour and clips follow from
/// the citizen's look number, the same on every client.
/// </summary>
public sealed partial class CitizenView : Node3D
{
    static readonly string[] Idles = { "idle_standing_masc", "idle_standing_femn", "idle_base_masc", "idle_base_femn", "idle_arms_folded_casual_masc", "idle_hands_on_hips_femn" };
    static readonly string[] Deaths = { "death_back_sword", "death_left_sword" };
    const string KneelIn = "idle_pray_kneeling_enter_masc", KneelLoop = "idle_pray_kneeling_loop_masc";

    readonly CitizensConfig _cfg;
    Node3D? _model;
    AnimationPlayer? _anim;
    SitPose? _sit;
    string _walk = "", _idle = "", _death = "";
    byte _shown = 255;

    CitizenView(CitizensConfig cfg) { _cfg = cfg; }

    public static CitizenView Make(CitizensConfig cfg, ushort look)
    {
        var h = look * 2654435761u;
        var v = new CitizenView(cfg);
        v.Build(cfg.Models[(int)((h >> 8) % cfg.Models.Count)], h);
        return v;
    }

    void Build(CitizenModelDef def, uint h)
    {
        var atlas = $"fantasy-characters/textures/PolygonFantasyCharacters_0{1 + (h >> 12) % 5}_{"ABC"[(int)((h >> 16) % 3)]}.png";
        _model = SyntyLib.Instance(def.File, atlas);
        if (_model == null) return;
        _model.RotationDegrees = new Vector3(0, 180, 0); // the pack faces +Z, an entity's forward is -Z
        AddChild(_model);
        _walk = def.File.Contains("female") ? "walk_f_femn" : "walk_f_masc";
        _idle = Idles[(int)((h >> 20) % (uint)Idles.Length)];
        _death = Deaths[(int)((h >> 24) % (uint)Deaths.Length)];
        var lib = new AnimationLibrary();
        foreach (var name in new[] { _walk, _idle, _death, KneelIn, KneelLoop })
            if (SyntyLib.Clip(name) is { } clip) lib.AddAnimation(name, clip);
        _anim = new AnimationPlayer { Name = "Clips" };
        _model.AddChild(_anim); // root node "..": the tracks are Skeleton3D:Bone under the model
        _anim.AddAnimationLibrary("", lib);
        if (_model.FindChildren("*", "Skeleton3D", true, false).FirstOrDefault() is Skeleton3D skeleton)
        {
            _sit = new SitPose { Name = "Sit", Influence = 0, PelvisM = _cfg.SeatHeightM + 0.1f };
            skeleton.AddChild(_sit);
        }
    }

    /// <summary>Shows what the citizen is doing. A body that is already seated, kneeling or dead when first seen takes its final pose at once.</summary>
    public void Show(CitizenState state)
    {
        if ((byte)state == _shown) return;
        var first = _shown == 255;
        _shown = (byte)state;
        if (_model == null || _anim == null) return;
        var blend = first ? 0 : 0.25;
        if (_sit != null)
        {
            var to = state == CitizenState.Seated ? 1f : 0f;
            if (first) _sit.Influence = to;
            else CreateTween().TweenProperty(_sit, "influence", to, state == CitizenState.Seated ? 0.7 : 0.2);
        }
        switch (state)
        {
            case CitizenState.Seated: Play(_idle, blend); break;
            case CitizenState.Kneeling:
                if (first || !_anim.HasAnimation(KneelIn)) Play(KneelLoop, blend);
                else { Play(KneelIn, blend); _anim.Queue(KneelLoop); }
                break;
            case CitizenState.Dead:
                Play(_death, blend);
                if (first && _anim.HasAnimation(_death)) _anim.Seek(_anim.GetAnimation(_death).Length, true);
                break;
            default: Play(_walk, blend); break;
        }
    }

    void Play(string clip, double blend)
    {
        if (_anim!.HasAnimation(clip)) _anim.Play(clip, blend);
    }
}

/// <summary>
/// Sitting, laid over whatever clip plays (no pack clip sits): the pelvis goes to <see cref="PelvisM"/> above the feet and a little back, the
/// thighs swing forward to level and the shins hang down again. <c>Influence</c> fades it in and out, which is the sitting down and standing up.
/// Skeleton space: the character faces +Z, y is up.
/// </summary>
public sealed partial class SitPose : SkeletonModifier3D
{
    public float PelvisM = 0.55f;
    public float BackM = 0.12f;
    const float Bend = 1.45f; // radians, a little short of level so the feet reach the floor

    public override void _ProcessModificationWithDelta(double delta)
    {
        var s = GetSkeleton();
        if (s == null) return;
        var hips = s.FindBone("Hips");
        if (hips < 0) return;
        var g = s.GetBoneGlobalPose(hips);
        g.Origin = new Vector3(g.Origin.X, PelvisM, -BackM);
        s.SetBoneGlobalPose(hips, g);
        foreach (var side in new[] { "L", "R" })
        {
            Turn(s, s.FindBone("UpperLeg_" + side), -Bend);
            Turn(s, s.FindBone("LowerLeg_" + side), Bend);
        }
    }

    /// <summary>Turns a bone about its own joint round the skeleton's left-right axis.</summary>
    static void Turn(Skeleton3D s, int bone, float angle)
    {
        if (bone < 0) return;
        var g = s.GetBoneGlobalPose(bone);
        g.Basis = new Basis(Vector3.Right, angle) * g.Basis;
        s.SetBoneGlobalPose(bone, g);
    }
}
