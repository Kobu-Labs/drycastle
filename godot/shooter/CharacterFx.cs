using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// A remote player's body: one of the animated character models listed in camo.json <c>bodies</c> (one shared 23-bone rig with Idle, Walk,
/// Run, Death and melee clips), holding its weapon in the right fist. While armed the arms are posed on the weapon by
/// <see cref="ArmsReadyPose"/> over whatever clip plays. Falls back to the capsule when the model is missing (headless runs).
/// The look (body, skin tone, headgear, suit, foliage, face paint) is applied by <see cref="SetCamo"/>, see CharacterView.Camo.cs.
/// </summary>
public sealed partial class CharacterView : Node3D
{
    /// <summary>The model worn until a look arrives: the first body of camo.json (the default look).</summary>
    public const string DefaultModel = "animated-characters/soldier-male.glb";
    /// <summary>The model is 3.147 units tall as authored (measured, see <see cref="PlayerHitBox"/>); scaled to the 1.8 m stand height so the drawn body matches the hit volume.</summary>
    const float Scale = PlayerHitBox.DrawScale;
    AnimationPlayer? _anim;
    Node3D? _model;
    Skeleton3D? _skeleton;
    MeshInstance3D? _mesh;
    string _modelPath = "";
    Stance _stance;
    bool _swim;
    BoneAttachment3D? _hand;
    Node3D? _grip;
    ArmsReadyPose? _arms;
    RagdollFlail? _flail;
    /// <summary>A thrown body (the ragdoll): the limbs flail over a neutral clip instead of the death clip playing.</summary>
    bool _ragdoll;
    float _flailAmount;
    bool _flailFrozen;
    Node3D? _aim;
    bool _aimed, _shooting;
    float _fireTimer;
    /// <summary>Flash the muzzle on every animated shot. Off for real remote players (the server's shot events flash them); on for the pose demo.</summary>
    public bool FlashOnShot;
    Node3D? _held;
    string _heldId = "";
    string _playing = "";
    string _lastWeapon = "";
    bool _lastFiring, _lastDead;
    float _lastSpeed;
    public bool HasModel { get; private set; }
    public string CurrentAnim => _playing;

    public static CharacterView Make()
    {
        var v = new CharacterView();
        v.Build(DefaultModel);
        return v;
    }

    /// <summary>Loads <paramref name="path"/> as the body, replacing the current one. Keeps the stance, swim pose, weapon and animation. False (nothing changed) when the model is missing.</summary>
    bool Build(string path)
    {
        var m = ModelLib.Instance(path);
        if (m == null)
        {
            if (_model == null)
                AddChild(new MeshInstance3D { Mesh = new CapsuleMesh { Radius = MoveConsts.CapsuleRadius, Height = MoveConsts.CapsuleHeight }, Position = new Vector3(0, MoveConsts.CapsuleHeight / 2, 0) });
            return false;
        }
        _held?.QueueFree();
        _held = null;
        _heldId = "";
        ClearCamo();
        Array.Clear(_wornNodes); // they hang on the old skeleton, which goes with the old model
        _model?.QueueFree();
        _modelPath = path;
        HasModel = true;
        _model = m;
        m.Scale = Vector3.One * Scale;
        AddChild(m);
        _anim = m.FindChildren("*", "AnimationPlayer", true, false).FirstOrDefault() as AnimationPlayer;
        if (_anim != null)
            foreach (var n in new[] { "Idle", "Walk", "Run", "Walk_Carry", "Run_Carry" })
                if (_anim.HasAnimation(n)) _anim.GetAnimation(n).LoopMode = Animation.LoopModeEnum.Linear;
        _skeleton = m.FindChildren("*", "Skeleton3D", true, false).FirstOrDefault() as Skeleton3D;
        _mesh = m.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>().FirstOrDefault();
        _hand = null; _grip = null; _arms = null; _aim = null; _aimed = false;
        if (_skeleton != null && _mesh?.Skin != null)
        {
            PrepareMesh(_mesh, path);
            BuildHandSlot(_skeleton, _mesh.Skin);
            BuildEyes(_skeleton, _mesh);
            RebuildWorn(); // what is worn goes on the new body
        }
        _flail = _skeleton != null ? RagdollFlail.Create(_skeleton) : null;
        if (_flail != null) { _skeleton!.AddChild(_flail); ApplyFlail(); }
        _playing = "";
        ApplyPose();
        Apply(_lastSpeed, _lastFiring, _lastDead, _lastWeapon);
        return true;
    }

    /// <summary>The right fist carries the weapon; a pivot turns the weapon's barrel (-Z) along the forearm with the weapon's up on the character's up.</summary>
    void BuildHandSlot(Skeleton3D sk, Skin skin)
    {
        var bind = BindPoses(sk, skin);
        var fist = sk.FindBone("Fist.R");
        var lower = sk.FindBone("LowerArm.R");
        if (fist < 0 || lower < 0 || !bind.ContainsKey(fist) || !bind.ContainsKey(lower)) return;
        _hand = new BoneAttachment3D { Name = "HandSlot", BoneName = "Fist.R" };
        sk.AddChild(_hand);
        // in the T-pose the right forearm runs along -X; the weapon's barrel must follow it and its up stay up
        var arm = (bind[fist].Origin - bind[lower].Origin).Normalized();
        var z = -arm;
        var y = Vector3.Up;
        var x = y.Cross(z).Normalized();
        var weapon = new Basis(x, y, z).Orthonormalized();
        _grip = new Node3D { Name = "Grip", Transform = new Transform3D(bind[fist].Basis.Inverse() * weapon, new Vector3(0, 0.1f, 0)) };
        _hand.AddChild(_grip);
        _arms = ArmsReadyPose.Create(sk, bind, 1f / Scale);
        if (_arms != null)
        {
            _aim = new Node3D { Name = "Aim" };
            sk.AddChild(_aim);
            _arms.Aim = _aim;
            sk.AddChild(_arms);
        }
    }

    /// <summary>The bind-pose (T-pose) global transform of every skinned bone, by bone index.</summary>
    static Dictionary<int, Transform3D> BindPoses(Skeleton3D sk, Skin skin)
    {
        var d = new Dictionary<int, Transform3D>();
        for (var i = 0; i < skin.GetBindCount(); i++)
        {
            var bone = skin.GetBindBone(i);
            if (bone < 0) bone = sk.FindBone(skin.GetBindName(i));
            if (bone >= 0) d[bone] = skin.GetBindPose(i).AffineInverse();
        }
        return d;
    }

    /// <summary>
    /// Remote stance pose, matching the hit volume (<see cref="HitMath.Axis"/>). Prone rolls the figure onto its front, rests it on the
    /// ground and centres it on the snapshot position. Crouch squashes the figure to the crouch height (no crouch animation exists).
    /// Stand is upright.
    /// </summary>
    public void SetStance(Stance stance)
    {
        if (stance == _stance) return;
        _stance = stance;
        ApplyPose();
    }

    /// <summary>
    /// How far up or down the held weapon points, radians (a rider aiming from a vehicle seat); 0 puts it level again, as everyone on foot holds it.
    /// </summary>
    public void SetAimPitch(float pitch)
    {
        if (_arms != null) _arms.Pitch = Mathf.Clamp(pitch, -1.1f, 1.1f);
    }

    /// <summary>Swimming pose: the body leans forward (<see cref="UnderwaterFx.SwimLeanDegrees"/>) and moves at the slow walk; stands up again when not swimming.</summary>
    public void SetSwim(bool swimming)
    {
        if (swimming == _swim) return;
        _swim = swimming;
        ApplyPose();
    }

    /// <summary>Places the model for the current stance and swim state. The glTF characters face +Z, the game's forward is -Z, hence the 180 degrees.</summary>
    void ApplyPose()
    {
        if (_model == null) return;
        var prone = _stance == Stance.Prone;
        _model.RotationDegrees = new Vector3(_swim ? UnderwaterFx.SwimLeanDegrees : prone ? 90 : 0, 180, 0);
        _model.Position = prone ? new Vector3(0, PlayerHitBox.ProneLift, PlayerHitBox.ProneCentreShift) : Vector3.Zero;
        var squash = _stance == Stance.Crouch ? MovementStep.CapsuleHeight(Stance.Crouch) / PlayerHitBox.StandHeight : 1f;
        _model.Scale = new Vector3(Scale, Scale * squash, Scale);
        if (_anim != null) _anim.SpeedScale = _swim ? 0.5f : 1f;
    }

    /// <summary>A corpse (ticket #130): the death pose, held at its last frame, with the weapon it carries in the hand. <see cref="Apply"/> hides weapons while dead, so corpses use this.</summary>
    public void ShowCorpse(string weaponId)
    {
        SetWeapon(weaponId);
        SetAimed(false);
        Play("Death", 0);
        if (_anim != null && _anim.HasAnimation("Death")) _anim.Seek(_anim.GetAnimation("Death").Length, true);
    }

    /// <summary>
    /// A thrown body (ticket: the ragdoll): the limbs flail by <paramref name="amount"/> (0..1) over a neutral clip, frozen once it lies still, with the
    /// weapon it carries. Off again with <c>on = false</c> (the death clip or the normal clips play).
    /// </summary>
    public void SetRagdoll(bool on, float amount = 0, bool frozen = false)
    {
        _ragdoll = on; _flailAmount = on ? amount : 0; _flailFrozen = frozen;
        ApplyFlail();
        if (on) Play("Idle", 0);
    }

    void ApplyFlail()
    {
        if (_flail == null) return;
        _flail.Amount = _flailAmount;
        _flail.Frozen = _flailFrozen;
    }

    /// <summary>A thrown corpse: the weapon it carries in the hand and the limbs flailing (<see cref="SetRagdoll"/> drives them). <see cref="ShowCorpse"/> is for a body that fell where it stood.</summary>
    public void ShowThrown(string weaponId)
    {
        SetWeapon(weaponId);
        SetAimed(false);
        SetRagdoll(true, _flailAmount, _flailFrozen);
    }

    /// <summary>Update from snapshot state: horizontal speed (m/s), whether it fired in the last moments, dead, and the weapon id it holds.</summary>
    public void Apply(float speed, bool firing, bool dead, string weaponId)
    {
        _lastSpeed = speed; _lastFiring = firing; _lastDead = dead; _lastWeapon = weaponId;
        SetWeapon(dead ? "" : weaponId);
        if (!HasModel) return;
        var knife = weaponId == "knife";
        var swing = firing && knife && !dead;
        var armed = !dead && weaponId != "" && !swing;
        var twoHanded = weaponId is not ("pistol" or "glock17" or "deagle" or "knife"); // pistols are one-handed, every long gun is two-handed
        if (_arms != null) _arms.TwoHanded = twoHanded;
        SetAimed(armed);
        _shooting = armed && firing;
        Play(dead ? (_ragdoll ? "Idle" : "Death")
            : swing ? "SwordSlash"
            : _swim ? (speed > 0.6f ? "Walk" : "Idle") // no swim clip in the rig: the slow walk, see SetSwim
            : speed > 6.2f ? "Run" : speed > 0.6f ? "Walk" : "Idle");
    }

    /// <summary>While a gun is firing the weapon kicks about every 0.11 s (a steady automatic rate; the real shots arrive as events, this is the pose only).</summary>
    public override void _Process(double delta)
    {
        if (!_shooting || _arms == null) { _fireTimer = 0; return; }
        _fireTimer -= (float)delta;
        if (_fireTimer > 0) return;
        _fireTimer = 0.11f;
        _arms.Kick();
        if (FlashOnShot) Flash();
    }

    /// <summary>Armed and posed: the weapon hangs on the aim node and the arms are posed onto it. Otherwise it sits in the right fist and the clip plays as it is.</summary>
    void SetAimed(bool aimed)
    {
        if (_arms == null || _aim == null) return;
        _arms.Active = aimed;
        if (aimed == _aimed) return;
        _aimed = aimed;
        if (_held != null && GodotObject.IsInstanceValid(_held)) _held.Reparent(WeaponParent(), false);
    }

    Node3D? WeaponParent() => _aimed ? _aim : _grip;

    void Play(string anim, double blend = 0.12)
    {
        if (_anim == null || anim == _playing || !_anim.HasAnimation(anim)) return;
        _playing = anim;
        _anim.Play(anim, blend);
    }

    /// <summary>Barrel tip of the held weapon in world space, or null when nothing is held.</summary>
    public Vector3? MuzzlePosition => _held != null && GodotObject.IsInstanceValid(_held) && _held.IsInsideTree() ? _held.GetNodeOrNull<Node3D>("Muzzle")?.GlobalPosition : null;

    /// <summary>Flash the held weapon's muzzle.</summary>
    public void Flash(float scale = 1f) => MuzzleFlash.Of(_held)?.Flash(scale);

    void SetWeapon(string id)
    {
        if (id == _heldId || WeaponParent() == null) return;
        _heldId = id;
        _held?.QueueFree();
        _held = id == "" ? null : WeaponFx.Make(id);
        if (_held == null) return;
        // the weapon is built in metres and the rig is scaled by Scale
        _held.Scale = Vector3.One / Scale;
        WeaponParent()!.AddChild(_held);
    }
}
