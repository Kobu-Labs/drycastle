using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// A citizen's body on the client: a Synty peasant (<see cref="SyntyLib"/>; the model name is <see cref="CitizenModelDef.File"/>) in one of the
/// pack's 15 recolours, with the pack clips for walking, standing idle, dying and kneeling in prayer. No clip sits down, so sitting is made here:
/// the idle clip keeps playing and <see cref="SitPose"/> bends the legs and puts the pelvis on the pew. Model, recolour and clips follow from
/// the citizen's look number, the same on every client.
/// <para>
/// The pastor and the monks are the same entity with a <see cref="CitizenRole"/>: their own Synty models (holy_mass.json, mass). The pastor prays
/// standing while the Mass runs, with a gesture now and then, and flexes when it completes. A monk stands idle, and when the pastor is killed is
/// shocked and turns to where he stood. Every clip name of the Mass is in <see cref="MassClips"/>; a name that is off is skipped (SyntyLib logs it).
/// </para>
/// </summary>
public sealed partial class CitizenView : Node3D
{
    /// <summary>The clips of the Holy Mass (assets-private/synty/anim/baked). One table, so a wrong name is fixed in one place.</summary>
    static class MassClips
    {
        public const string Clap = "emote_celebrate_clapping_polite_femn", Flex = "emote_sporty_flex_combo_femn", Dab = "emote_celebrate_dab_femn";
        public const string PrayIn = "idle_pray_standing_enter_femn", PrayLoop = "idle_pray_standing_loop_femn", PrayOut = "idle_pray_standing_exit_femn";
        public const string PastorIdle = "idle_base_masc", MonkIdle = "idle_standing_masc", Walk = "walk_f_masc";
        public const string Sprint = "sprint_up25f_femn";
        /// <summary>What a living monk plays when the pastor is killed (one in three each; a few dab instead).</summary>
        public static readonly string[] MonkShock = { "emote_reproach_calm_down_femn", "emote_sad_crying_masc", "emote_taunt_shhh_femn" };
        /// <summary>The pastor's gestures between the praying clips; each is a run of clips played one after another.</summary>
        public static readonly string[][] Gestures =
        {
            new[] { "emote_angry_tantrum_stomp_femn" },
            new[] { "emote_angry_strangling_motion_femn" },
            new[] { "emote_reproach_calm_down_femn" },
            new[] { "idle_point_hand_index_f_femn" },
            new[] { "idle_thoughtful_r_enter_femn", "idle_thoughtful_l_loop_femn", "idle_thoughtful_l_exit_femn" },
            new[] { "idle_plead_f_masc" },
        };
        public static IEnumerable<string> All => new[] { Clap, Flex, Dab, PrayIn, PrayLoop, PrayOut, PastorIdle, MonkIdle, Walk }.Concat(MonkShock).Concat(Gestures.SelectMany(g => g));
    }

    static readonly string[] Idles = { "idle_standing_masc", "idle_standing_femn", "idle_base_masc", "idle_base_femn", "idle_arms_folded_casual_masc", "idle_hands_on_hips_femn" };
    static readonly string[] Deaths = { "death_back_sword", "death_left_sword" };
    const string KneelIn = "idle_pray_kneeling_enter_masc", KneelLoop = "idle_pray_kneeling_loop_masc";

    readonly CitizensConfig _cfg;
    readonly MassConfig _mass;
    readonly CitizenRole _role;
    Node3D? _model;
    AnimationPlayer? _anim;
    SitPose? _sit;
    string _walk = "", _idle = "", _death = "";
    byte _shown = 255;
    uint _hash;

    // the pastor and the monks
    bool _chanting, _praying, _shocked, _hasPastorAt;
    int _kills = -1;
    Vector3 _pastorAt;
    List<string> _seq = new();
    int _seqI;
    double _seqAt, _gestureAt;
    readonly Random _rng = new();
    CitizenState _state = CitizenState.Walking;

    CitizenView(CitizensConfig cfg, MassConfig mass, CitizenRole role) { _cfg = cfg; _mass = mass; _role = role; }

    public static CitizenView Make(CitizensConfig cfg, MassConfig mass, ushort look, CitizenRole role = CitizenRole.Citizen)
    {
        var h = look * 2654435761u;
        var v = new CitizenView(cfg, mass, role) { _hash = h };
        v.Build(cfg.Models[(int)((h >> 8) % cfg.Models.Count)], h);
        v.SetProcess(role != CitizenRole.Citizen);
        return v;
    }

    void Build(CitizenModelDef def, uint h)
    {
        var atlas = $"fantasy-characters/textures/PolygonFantasyCharacters_0{1 + (h >> 12) % 5}_{"ABC"[(int)((h >> 16) % 3)]}.png";
        _model = _role switch
        {
            CitizenRole.Pastor => SyntyLib.Instance(_mass.PastorModel, _mass.Atlas),
            CitizenRole.Monk => SyntyLib.Instance(_mass.MonkModel, _mass.Atlas),
            _ => SyntyLib.Instance(def.File, atlas),
        };
        if (_model == null) return;
        _model.RotationDegrees = new Vector3(0, 180, 0); // the pack faces +Z, an entity's forward is -Z
        AddChild(_model);
        _walk = _role != CitizenRole.Citizen ? MassClips.Walk : def.File.Contains("female") ? "walk_f_femn" : "walk_f_masc";
        _idle = _role switch { CitizenRole.Pastor => MassClips.PastorIdle, CitizenRole.Monk => MassClips.MonkIdle, _ => Idles[(int)((h >> 20) % (uint)Idles.Length)] };
        _death = Deaths[(int)((h >> 24) % (uint)Deaths.Length)];
        var lib = new AnimationLibrary();
        var names = new List<string> { _walk, _idle, _death, KneelIn, KneelLoop, MassClips.Clap, MassClips.Sprint };
        if (_role != CitizenRole.Citizen) names.AddRange(MassClips.All);
        foreach (var name in names.Distinct())
            if (SyntyLib.Clip(name) is { } clip)
            {
                if (name == MassClips.Sprint) clip.LoopMode = Animation.LoopModeEnum.Linear; // looped whatever the manifest says
                lib.AddAnimation(name, clip);
            }
        _anim = new AnimationPlayer { Name = "Clips" };
        _model.AddChild(_anim); // root node "..": the tracks are Skeleton3D:Bone under the model
        _anim.AddAnimationLibrary("", lib);
        if (_role == CitizenRole.Citizen && _model.FindChildren("*", "Skeleton3D", true, false).FirstOrDefault() is Skeleton3D skeleton)
        {
            _sit = new SitPose { Name = "Sit", Influence = 0, PelvisM = _cfg.SeatHeightM + 0.09f };
            skeleton.AddChild(_sit);
        }
    }

    /// <summary>Shows what the citizen is doing. A body that is already seated, kneeling or dead when first seen takes its final pose at once.</summary>
    public void Show(CitizenState state)
    {
        if ((byte)state == _shown) return;
        var first = _shown == 255;
        var was = _state;
        _shown = (byte)state;
        _state = state;
        if (_model == null || _anim == null) return;
        var blend = first ? 0 : 0.25;
        if (_sit != null)
        {
            var to = state == CitizenState.Seated ? 1f : 0f;
            if (first) _sit.Influence = to;
            else CreateTween().TweenProperty(_sit, "influence", to, state == CitizenState.Seated ? 0.7 : state == CitizenState.Fleeing ? 0.25 : was == CitizenState.Seated ? 0.6 : 0.2); // standing up after the Mass takes a moment
        }
        switch (state)
        {
            case CitizenState.Seated: Play(_idle, blend); break;
            case CitizenState.Kneeling:
                if (first || !_anim.HasAnimation(KneelIn)) Play(KneelLoop, blend);
                else { Play(KneelIn, blend); _anim.Queue(KneelLoop); }
                break;
            case CitizenState.Dead:
                _seq.Clear(); _praying = false; _shocked = false;
                Play(_death, blend);
                if (first && _anim.HasAnimation(_death)) _anim.Seek(_anim.GetAnimation(_death).Length, true);
                break;
            case CitizenState.Standing:
                _seq.Clear(); _praying = false;
                if (_role == CitizenRole.Citizen) Play(_idle, blend); else RefreshStanding(first);
                break;
            case CitizenState.Clapping:
                _seq.Clear(); _praying = false;
                Play(_role == CitizenRole.Pastor ? MassClips.Flex : MassClips.Clap, blend);
                break;
            case CitizenState.Fleeing:
                _seq.Clear(); _praying = false;
                if (_anim.HasAnimation(MassClips.Sprint)) Play(MassClips.Sprint, blend);
                else if (_anim.HasAnimation(_walk)) _anim.Play(_walk, blend, Math.Max(1f, _cfg.FleeSpeed / _cfg.WalkSpeed)); // no sprint clip yet: the walk, sped up
                break;
            default: _seq.Clear(); _praying = false; Play(_walk, blend); break;
        }
    }

    /// <summary>The Mass as the client knows it (called every frame; it acts on changes). <paramref name="chantAt"/> is where the pastor stands.</summary>
    public void SetMass(bool chanting, int pastorKills, Vector3 chantAt)
    {
        if (_role == CitizenRole.Citizen) return;
        _pastorAt = chantAt; _hasPastorAt = true;
        var killed = _kills >= 0 && pastorKills > _kills;
        _kills = pastorKills;
        if (chanting == _chanting && !killed) return;
        _chanting = chanting;
        if (_role == CitizenRole.Monk && _state != CitizenState.Dead) // a dead monk stays dead: only the living react
        {
            if (killed && !chanting) Shock();
            else if (chanting && _shocked) Unshock();
        }
        if (_state == CitizenState.Standing) RefreshStanding(false);
    }

    /// <summary>The pastor and monks standing: the pastor prays while the Mass runs, otherwise both stand idle.</summary>
    void RefreshStanding(bool first)
    {
        if (_anim == null) return;
        if (_role == CitizenRole.Pastor && _chanting)
        {
            if (_praying) return;
            _praying = true;
            StartSeq(first ? 0 : 0.3, MassClips.PrayIn, MassClips.PrayLoop);
            _gestureAt = Now() + NextGesture();
        }
        else if (_role == CitizenRole.Pastor && _praying)
        {
            _praying = false;
            StartSeq(0.3, MassClips.PrayOut, _idle);
        }
        else if (!_shocked) { _seq.Clear(); Play(_idle, first ? 0 : 0.3); }
    }

    /// <summary>A monk sees the pastor killed: calm-down or crying (a few dab), turned to the pastor's spot until the Mass goes on.</summary>
    void Shock()
    {
        if (_model == null || _anim == null) return;
        _shocked = true;
        var dab = new Random((int)(_hash ^ ((uint)_kills * 7919u))).NextDouble() < _mass.MonkDabShare;
        var pool = MassClips.MonkShock.Where(n => _anim.HasAnimation(n)).ToArray(); // a clip that is not baked yet is not picked
        var clip = dab ? MassClips.Dab : pool.Length > 0 ? pool[(int)((_hash >> 4) % (uint)pool.Length)] : _idle;
        StartSeq(0.2, clip, _idle);
        Face(true);
    }

    void Unshock()
    {
        _shocked = false;
        Face(false);
        StartSeq(0.3, _idle);
    }

    /// <summary>Turns the body to the pastor's spot, or back to where the entity faces.</summary>
    void Face(bool towardsPastor)
    {
        if (_model == null) return;
        var target = Mathf.Pi;
        if (towardsPastor && _hasPastorAt && IsInsideTree())
        {
            var d = _pastorAt - GlobalPosition;
            var want = Mathf.Atan2(-d.X, -d.Z);
            target = Mathf.Pi + Mathf.Wrap(want - GlobalRotation.Y, -Mathf.Pi, Mathf.Pi);
        }
        CreateTween().TweenProperty(_model, "rotation:y", target, 0.45);
    }

    double NextGesture() => _mass.GestureMinSeconds + _rng.NextDouble() * (_mass.GestureMaxSeconds - _mass.GestureMinSeconds);

    static double Now() => Time.GetTicksMsec() / 1000.0;

    /// <summary>Plays clips one after another; the last one stays (a loop keeps looping, a still clip holds its last frame).</summary>
    void StartSeq(double blend, params string[] clips)
    {
        _seq = clips.Where(c => _anim!.HasAnimation(c)).ToList();
        _seqI = 0;
        if (_seq.Count > 0) PlaySeq(blend);
    }

    void PlaySeq(double blend)
    {
        var a = _anim!.GetAnimation(_seq[_seqI]);
        Play(_seq[_seqI], blend);
        _seqAt = Now() + (a.LoopMode == Animation.LoopModeEnum.None ? a.Length : Math.Min(a.Length * 2, 3));
    }

    public override void _Process(double delta)
    {
        if (_anim == null || _seq.Count == 0) return;
        var now = Now();
        if (_seqI + 1 < _seq.Count) { if (now >= _seqAt) { _seqI++; PlaySeq(0.25); } return; }
        if (_praying && _chanting && now >= _gestureAt)
        {
            var g = MassClips.Gestures[_rng.Next(MassClips.Gestures.Length)];
            StartSeq(0.3, g.Append(MassClips.PrayLoop).ToArray());
            _gestureAt = now + NextGesture();
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
    /// <summary>How far behind the seat point the hip joints are. The Synty pew's backrest is 0.1 m behind its middle, so the hips sit a little in front.</summary>
    public float BackM = -0.03f;
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
