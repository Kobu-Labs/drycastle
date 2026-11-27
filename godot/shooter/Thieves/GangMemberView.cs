using Godot;
using Drycastle.Shooter.Assets;
using Drycastle.Shooter.Citizens;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Thieves;

namespace Drycastle.Shooter.Thieves;

/// <summary>
/// A Gang member's body on the client (GLOSSARY "Gang member"). With the private Gang Warfare pack it is the kind's model (Gang boss, Enforcer, a Thief of two looks) in one of the pack's twelve atlases,
/// both picked by the look number (<see cref="GangLook"/>: every client agrees), with the pack's gang-rig clips (names in thieves.json <c>gang.view</c>); the peasants' clips do not fit this rig. It wears
/// what <see cref="GangMemberView.Dress"/> says (the Enforcer's pipe, a cigarette, the Gang boss's chain, a speaker) and holds the Loot it carries (<see cref="GangMemberView.SetCarried"/>). Without the pack (or when the
/// model fails to load) a coloured capsule per kind is drawn (the Rival crew's Gunman: rival_crew.json <c>view</c>, its gun in the right hand and the arm raised while it aims and fires, <see cref="GangMemberView.AddGun"/>), so a member is never invisible: a Thief is a plain capsule, an Enforcer carries a stick, the Gang boss is bigger and gold. A kind's size is
/// <see cref="GangKindConfig.HitScale"/>, the same number the server's hit volume uses.
/// <para>
/// The state byte says what it does; whether it is walking is read from how far it moved (the caller passes <c>moving</c>): standing, guarding and walking share the idle and walk clips. Which clip each state plays
/// is <see cref="GangMemberView.Apply"/>; the windup and the swing play a blow clip timed to land with the server's blow, or, with none, lean the body back and then forward.
/// </para>
/// </summary>
public sealed partial class GangMemberView : Node3D
{
    const float LeanBackDeg = 22f, LeanForwardDeg = -32f, KneelDeg = -35f, KneelDropM = 0.35f;

    readonly ThievesFile _data;
    readonly GangConfig _cfg;
    readonly GangView _view;
    readonly GangMemberKind _kind;
    readonly ushort _look;
    readonly string _chainModel;
    readonly bool _rival;
    readonly Node3D _pivot = new() { Name = "Pivot" };   // scaled and leaned: the body hangs under it
    Node3D? _model;
    Skeleton3D? _skeleton;
    MeshInstance3D? _capsule;
    AnimationPlayer? _anim;
    Change<GangMemberState> _shown;
    string _death = "";
    bool _femn;
    Tween? _lean;

    GangMemberView(ThievesFile data, GangMemberKind kind, ushort look, string chainModel, bool rival)
    {
        _data = data; _cfg = data.Gang; _view = data.Gang.View; _kind = kind; _look = look; _chainModel = chainModel; _rival = rival;
    }

    public GangMemberKind Kind => _kind;

    /// <summary>
    /// A body for a member of <paramref name="kind"/> and look number <paramref name="look"/>. <paramref name="chainModel"/>: the model path of the Gold chain Item (items.json), which the Thieves' Gang
    /// boss wears. <paramref name="rival"/>: a member of the Rival crew (<see cref="GangRules.RivalOf"/>), tinted rival_crew.json <c>view.tint</c> so it reads apart from the Thieves, and its Gang boss
    /// (the Rival dealer, who leaves no chain) wears none.
    /// </summary>
    public static GangMemberView Make(ThievesFile data, GangMemberKind kind, ushort look, string chainModel = "", bool rival = false)
    {
        var v = new GangMemberView(data, kind, look, chainModel, rival);
        v.Build();
        return v;
    }

    void Build()
    {
        AddChild(_pivot);
        _pivot.Scale = Vector3.One * _cfg.Of(_kind).HitScale;
        var file = GangLook.Model(_view, _kind, _look);
        _femn = GangLook.IsFemn(_view, file);
        _model = SyntyLib.Instance(file, GangLook.Atlas(_view, _look));
        if (_model != null && _rival) Tint(_model, Color.FromHtml(_data.RivalCrew.View.Tint)); // before the dress: only the body takes the crew's colour
        if (_model != null) BuildModel();
        else BuildCapsule();
        if (_kind == GangMemberKind.Enforcer) AddPipe();
        else if (_kind == GangMemberKind.Gunman) AddGun();
        SetProcess(_anim != null);
    }

    void BuildModel()
    {
        _model!.RotationDegrees = new Vector3(0, 180, 0); // the pack faces +Z, an entity's forward is -Z
        _pivot.AddChild(_model);
        _skeleton = _model.FindChildren("*", "Skeleton3D", true, false).OfType<Skeleton3D>().FirstOrDefault();
        _death = ClipOf(GangLook.Death(_view, _look));
        var lib = new AnimationLibrary();
        foreach (var name in ClipNames().Where(n => n != "").Distinct())
            if (SyntyLib.Clip(name) is { } clip) lib.AddAnimation(name, clip);
        _anim = new AnimationPlayer { Name = "Clips" };
        _model.AddChild(_anim); // root node "..": the tracks are Skeleton3D:Bone under the model
        _anim.AddAnimationLibrary("", lib);
        FindClips();
        if (_skeleton != null) { BuildCarry(); AddDress(); }
    }

    /// <summary>Every clip this member can play (a name that is off is skipped, SyntyLib logs it).</summary>
    IEnumerable<string> ClipNames()
    {
        var c = _view.Clips;
        var names = new List<string> { c.Idle, c.Walk, c.Run, c.Sprint, c.Steal, c.StealLoop, c.ClimbUp, c.ClimbDown, c.ClimbWork, c.KnockdownBegin, c.KnockdownLoop, c.KnockdownEnd, c.StunLoop, c.HopIn, c.HopOut, c.Land };
        names.AddRange(_view.Blows.Of(_kind).Select(b => b.Clip));
        names.AddRange(_view.Gestures.Taunts);
        names.Add(_view.Gestures.Drink);
        return names.Select(ClipOf).Append(_death);
    }

    /// <summary>A clip name of the data for this body (masc or femn where it says <c>{g}</c>).</summary>
    string ClipOf(string template) => GangLook.ClipName(template, _femn);

    /// <summary>The fallback body: a capsule in the kind's colour, a dark nose on the side it faces (so its turning reads), the height of a player times the kind's scale.</summary>
    void BuildCapsule()
    {
        var v = _view;
        var h = v.CapsuleHeightM;
        var colour = Color.FromHtml(_kind switch { GangMemberKind.Enforcer => v.EnforcerColor, GangMemberKind.GangBoss => v.GangBossColor, GangMemberKind.Gunman => _data.RivalCrew.View.GunmanColor, _ => v.ThiefColor });
        if (_rival && _kind != GangMemberKind.Gunman) colour *= Color.FromHtml(_data.RivalCrew.View.Tint); // a Rival Enforcer or dealer is not the Thieves' (the dealer not gold)
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

    /// <summary>The atlas materials tinted for the Rival crew, one per atlas and tint (every Rival member of an atlas shares it).</summary>
    static readonly Dictionary<(Material, Color), StandardMaterial3D> Tinted = new();

    /// <summary>Multiplies the albedo of every surface of <paramref name="model"/> by <paramref name="tint"/> (the atlas stays: the pack's look, in the crew's colour).</summary>
    static void Tint(Node3D model, Color tint)
    {
        foreach (var m in model.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>())
        {
            if (m.MaterialOverride is not StandardMaterial3D atlas) continue;
            if (!Tinted.TryGetValue((atlas, tint), out var mat))
            {
                mat = (StandardMaterial3D)atlas.Duplicate();
                mat.AlbedoColor = atlas.AlbedoColor * tint;
                Tinted[(atlas, tint)] = mat;
            }
            m.MaterialOverride = mat;
        }
    }

    /// <summary>
    /// Sits the body down (a crew member in the van): the pack has no sitting clip, so the pelvis drops to <paramref name="pelvisM"/> above the feet and the legs bend (<see cref="SitPose"/>) over the idle clip.
    /// A capsule is squashed to about two thirds of its height.
    /// </summary>
    public void Sit(float pelvisM)
    {
        if (_skeleton != null) _skeleton.AddChild(new SitPose { Name = "Sit", PelvisM = pelvisM });
        else _pivot.Scale = new Vector3(_pivot.Scale.X, _pivot.Scale.Y * 0.65f, _pivot.Scale.Z);
        Show(GangMemberState.Idle, false);
    }

    /// <summary>
    /// Seated or standing as the entity says (the state byte's bit 7, <see cref="GangRules.SeatedOf"/>: a Rival chase car's rider): the same pose as the van's crew (<see cref="SitPose"/>, the pelvis at
    /// thieves.json van.view seat_pelvis_m), whatever the member does (it aims and fires seated), and no walk clip while it rides (the caller passes it as not moving). Standing again takes the pose off.
    /// </summary>
    public void Ride(bool seated)
    {
        if (!_seated.Update(seated, out var before)) return;
        if (seated)
        {
            if (_skeleton != null) { _rideSit = new SitPose { Name = "Ride", PelvisM = _data.Van.View.SeatPelvisM }; _skeleton.AddChild(_rideSit); }
            else _pivot.Scale = new Vector3(_pivot.Scale.X, _pivot.Scale.Y * 0.65f, _pivot.Scale.Z);
        }
        else if (before)
        {
            if (_rideSit != null) { _rideSit.QueueFree(); _rideSit = null; }
            else _pivot.Scale = new Vector3(_pivot.Scale.X, _pivot.Scale.Y / 0.65f, _pivot.Scale.Z);
        }
    }

    Change<bool> _seated;
    SitPose? _rideSit;

    /// <summary>Shows what the member is doing. <paramref name="moving"/> says whether it is going somewhere (stands, guards and walks share two clips). A body first seen dead lies at once.</summary>
    public void Show(GangMemberState state, bool moving)
    {
        var changed = _shown.Update(state, out var before);
        var first = _firstShow;
        _firstShow = false;
        _stateNow = state; _movingNow = moving;
        if (changed) { NoteState(before, state, first); }
        Apply(state, moving, first);
        UpdateCarry(state);
        if (!changed) return;
        Lean(state);
        Knockdown(state, first);
        SlideToWall(state, first);
        if (state == GangMemberState.Dead) Fall(first);
    }

    bool _firstShow = true;
    GangMemberState _stateNow;
    bool _movingNow;

    /// <summary>Windup and swing lean the body (the capsule's whole telegraph; a model with a blow clip does not), kneeling bends it when there is no kneel clip.</summary>
    void Lean(GangMemberState state)
    {
        var deg = state switch { GangMemberState.Windup when !_hasBlow => LeanBackDeg, GangMemberState.Swing when !_hasBlow => LeanForwardDeg, GangMemberState.Steal when !_hasSteal => KneelDeg, _ => 0f };
        _lean?.Kill();
        _pivot.Position = new Vector3(0, state == GangMemberState.Steal && !_hasSteal ? -KneelDropM : 0f, 0); // kneeling at a target: bent forward and lowered
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
