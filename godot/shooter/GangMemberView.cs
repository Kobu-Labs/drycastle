using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// A Gang member's body on the client (GLOSSARY "Gang member"). With the private Synty pack it is one of the peasant bodies of <see cref="GangView.Models"/> (a stand-in until
/// the Gang Warfare pack, ticket 08), picked by the look number, with the pack's clips for standing, walking, running and dying (names in thieves.json). Without it (or
/// when the model fails to load) a coloured capsule per kind is drawn, so a member is never invisible: a Thief is a plain capsule, an Enforcer carries a stick, the Gang boss
/// is bigger and gold. A kind's size is <see cref="GangKindConfig.HitScale"/>, the same number the server's hit volume uses.
/// <para>
/// The state byte says what it does; whether it is walking is read from how far it moved (the caller passes <c>moving</c>): standing, guarding and walking share the idle and walk
/// clips. The windup and the swing play the clips named in thieves.json, or, with none, lean the body back and then forward.
/// </para>
/// </summary>
public sealed partial class GangMemberView : Node3D
{
    const string AtlasDir = "fantasy-characters/textures/";
    const float LeanBackDeg = 22f, LeanForwardDeg = -32f, KneelDeg = -35f, KneelDropM = 0.35f;

    readonly GangConfig _cfg;
    readonly GangMemberKind _kind;
    readonly uint _hash;
    readonly Node3D _pivot = new() { Name = "Pivot" };   // scaled and leaned: the body hangs under it
    Node3D? _model;
    MeshInstance3D? _capsule;
    AnimationPlayer? _anim;
    Change<GangMemberState> _shown;
    string _clip = "";
    string _death = "";
    Tween? _lean;

    GangMemberView(GangConfig cfg, GangMemberKind kind, ushort look) { _cfg = cfg; _kind = kind; _hash = look * 2654435761u; }

    public GangMemberKind Kind => _kind;

    public static GangMemberView Make(GangConfig cfg, GangMemberKind kind, ushort look)
    {
        var v = new GangMemberView(cfg, kind, look);
        v.Build();
        return v;
    }

    void Build()
    {
        AddChild(_pivot);
        _pivot.Scale = Vector3.One * _cfg.Of(_kind).HitScale;
        var view = _cfg.View;
        var file = view.Models[(int)((_hash >> 8) % (uint)view.Models.Count)];
        var atlas = $"{AtlasDir}PolygonFantasyCharacters_0{1 + (_hash >> 12) % 5}_{"ABC"[(int)((_hash >> 16) % 3)]}.png";
        _model = SyntyLib.Instance(file, atlas);
        if (_model != null) BuildModel(file);
        else BuildCapsule();
        if (_kind == GangMemberKind.Enforcer) AddStick();
    }

    void BuildModel(string file)
    {
        var clips = _cfg.View.Clips;
        _model!.RotationDegrees = new Vector3(0, 180, 0); // the pack faces +Z, an entity's forward is -Z
        _pivot.AddChild(_model);
        _death = clips.Death.Count == 0 ? "" : clips.Death[(int)((_hash >> 24) % (uint)clips.Death.Count)];
        var lib = new AnimationLibrary();
        foreach (var name in new[] { clips.Idle, clips.Walk, clips.Run, clips.Windup, clips.Swing, clips.Steal, clips.ClimbUp, clips.ClimbDown, clips.ClimbWork, clips.KnockdownBegin, clips.KnockdownLoop, clips.KnockdownEnd, clips.StunLoop, _death }.Where(n => n != "").Distinct())
            if (SyntyLib.Clip(name) is { } clip)
            {
                if (name == clips.Run) clip.LoopMode = Animation.LoopModeEnum.Linear; // looped whatever the manifest says
                lib.AddAnimation(name, clip);
            }
        _anim = new AnimationPlayer { Name = "Clips" };
        _model.AddChild(_anim); // root node "..": the tracks are Skeleton3D:Bone under the model
        _anim.AddAnimationLibrary("", lib);
    }

    /// <summary>The fallback body: a capsule in the kind's colour, a dark nose on the side it faces (so its turning reads), the height of a player times the kind's scale.</summary>
    void BuildCapsule()
    {
        var v = _cfg.View;
        var h = v.CapsuleHeightM;
        var colour = Color.FromHtml(_kind switch { GangMemberKind.Enforcer => v.EnforcerColor, GangMemberKind.GangBoss => v.GangBossColor, _ => v.ThiefColor });
        var mat = new StandardMaterial3D { AlbedoColor = colour, Roughness = 0.9f };
        _capsule = new MeshInstance3D { Name = "Capsule", Mesh = new CapsuleMesh { Radius = 0.3f, Height = h }, MaterialOverride = mat, Position = new Vector3(0, h / 2, 0) };
        _pivot.AddChild(_capsule);
        var nose = new MeshInstance3D
        {
            Name = "Nose", Mesh = new BoxMesh { Size = new Vector3(0.16f, 0.1f, 0.2f) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = colour.Darkened(0.55f), Roughness = 0.9f },
            Position = new Vector3(0, h * 0.82f, -0.3f),
        };
        _pivot.AddChild(nose);
    }

    /// <summary>The Enforcer's rusty pipe: a thin cylinder in the right hand (the hand bone of the model, else at the right hip of the capsule).</summary>
    void AddStick()
    {
        var stick = new MeshInstance3D
        {
            Name = "Pipe", Mesh = new CylinderMesh { TopRadius = 0.035f, BottomRadius = 0.035f, Height = 0.95f },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color("#5a3b2a"), Roughness = 1f, Metallic = 0.3f },
        };
        var skeleton = _model?.FindChildren("*", "Skeleton3D", true, false).OfType<Skeleton3D>().FirstOrDefault();
        if (skeleton != null && skeleton.FindBone("Hand_R") >= 0)
        {
            var hand = new BoneAttachment3D { Name = "PipeHand", BoneName = "Hand_R" };
            skeleton.AddChild(hand);
            hand.AddChild(stick);
            stick.Position = new Vector3(0, 0.3f, 0);
        }
        else
        {
            _pivot.AddChild(stick);
            stick.Position = new Vector3(0.38f, 0.8f, -0.25f);
            stick.RotationDegrees = new Vector3(-30, 0, 0);
        }
    }

    /// <summary>Shows what the member is doing. <paramref name="moving"/> says whether it is going somewhere (stands, guards and walks share two clips). A body first seen dead lies at once.</summary>
    public void Show(GangMemberState state, bool moving)
    {
        var changed = _shown.Update(state);
        PlayFor(state, moving);
        if (!changed) return;
        var first = _firstShow;
        _firstShow = false;
        Lean(state);
        Knockdown(state, first);
        if (state == GangMemberState.Dead) Fall(first);
    }

    bool _firstShow = true;

    void PlayFor(GangMemberState state, bool moving)
    {
        var c = _cfg.View.Clips;
        var want = state switch
        {
            GangMemberState.Dead => _death,
            GangMemberState.Chase or GangMemberState.Flee or GangMemberState.Board or GangMemberState.Haul => c.Run,
            GangMemberState.Windup => c.Windup != "" ? c.Windup : c.Idle,
            GangMemberState.Swing => c.Swing != "" ? c.Swing : c.Idle,
            GangMemberState.Steal => c.Steal != "" ? c.Steal : c.Idle,
            GangMemberState.ClimbUp => c.ClimbUp,
            GangMemberState.ClimbDown => c.ClimbDown,
            GangMemberState.WorkAloft => c.ClimbWork,
            GangMemberState.Shocked => c.StunLoop,
            GangMemberState.Falling => c.KnockdownBegin,
            GangMemberState.Down => c.KnockdownLoop,
            GangMemberState.GetUp => c.KnockdownEnd,
            _ => moving ? c.Walk : c.Idle,
        };
        if (want == "" && state is GangMemberState.ClimbUp or GangMemberState.ClimbDown or GangMemberState.WorkAloft or GangMemberState.Shocked or GangMemberState.Falling or GangMemberState.Down or GangMemberState.GetUp) { _anim?.Pause(); _clip = ""; return; } // no climb clip yet: a held pose
        if (_anim == null || want == "" || want == _clip) return;
        _clip = want;
        if (!_anim.HasAnimation(want)) return;
        _anim.Play(want, state == GangMemberState.Dead ? 0.1 : 0.2);
        if (state == GangMemberState.Dead && _firstShow) _anim.Seek(_anim.GetAnimation(want).Length, true); // already dead when first seen: lies at once
    }

    /// <summary>Windup and swing lean the body (the capsule's whole telegraph; a model with clips for them leans too, a little).</summary>
    void Lean(GangMemberState state)
    {
        var c = _cfg.View.Clips;
        var deg = state switch { GangMemberState.Windup when c.Windup == "" => LeanBackDeg, GangMemberState.Swing when c.Swing == "" => LeanForwardDeg, GangMemberState.Steal when c.Steal == "" => KneelDeg, _ => 0f };
        _lean?.Kill();
        _pivot.Position = new Vector3(0, state == GangMemberState.Steal && c.Steal == "" ? -KneelDropM : 0f, 0); // kneeling at a target: bent forward and lowered
        if (state == GangMemberState.Dead)
        {
            if (_capsule == null) _pivot.RotationDegrees = Vector3.Zero; // a model plays its death clip upright (a capsule falls: Fall)
            return;
        }
        _lean = CreateTween();
        _lean.TweenProperty(_pivot, "rotation_degrees:x", deg, state == GangMemberState.Swing ? 0.08 : 0.25);
    }

    /// <summary>A capsule has no death clip: it falls forward and lies. (A model plays its clip.)</summary>
    void Fall(bool first)
    {
        _lean?.Kill();
        if (_capsule == null) return; // a model plays its death clip
        var target = new Vector3(-90, 0, 0);
        _pivot.Position = new Vector3(0, 0.3f, 0); // lying on its side: the capsule's radius off the ground
        if (first) { _pivot.RotationDegrees = target; return; }
        _lean = CreateTween();
        _lean.TweenProperty(_pivot, "rotation_degrees", target, 0.35).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
    }
}
