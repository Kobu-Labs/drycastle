using Godot;
using Suchohrad.ShooterCore;

namespace Suchohrad.Shooter;

/// <summary>
/// A remote player's body: the KayKit animated character (rig has idle/walk/run/shoot/death) with the held weapon on the
/// right hand slot. Falls back to the original capsule when the model is missing (headless runs).
/// </summary>
public sealed partial class CharacterView : Node3D
{
    const string Model = "kaykit-character-animations/animated-character.glb";
    /// <summary>The model is a head-and-torso figure about 1.1 m tall; scaled up so it fills the 1.8 m capsule (hit boxes are capsules).</summary>
    const float Scale = 1.6f;
    AnimationPlayer? _anim;
    Node3D? _model;
    bool _swim;
    BoneAttachment3D? _hand;
    Node3D? _held;
    string _heldId = "";
    string _playing = "";
    public bool HasModel { get; private set; }
    public string CurrentAnim => _playing;

    public static CharacterView Make()
    {
        var v = new CharacterView();
        v.Build();
        return v;
    }

    void Build()
    {
        var m = ModelLib.Instance(Model);
        if (m == null)
        {
            AddChild(new MeshInstance3D { Mesh = new CapsuleMesh { Radius = MoveConsts.CapsuleRadius, Height = MoveConsts.CapsuleHeight }, Position = new Vector3(0, MoveConsts.CapsuleHeight / 2, 0) });
            return;
        }
        HasModel = true;
        _model = m;
        m.Scale = Vector3.One * Scale;
        m.RotationDegrees = new Vector3(0, 180, 0); // glTF characters face +Z, the game's forward is -Z
        AddChild(m);
        _anim = m.FindChildren("*", "AnimationPlayer", true, false).FirstOrDefault() as AnimationPlayer;
        if (_anim != null)
            foreach (var n in new[] { "Idle", "Walk", "Run", "Shooting(1h)", "Shooting(2h)" })
                if (_anim.HasAnimation(n)) _anim.GetAnimation(n).LoopMode = Animation.LoopModeEnum.Linear;
        if (m.FindChildren("*", "Skeleton3D", true, false).FirstOrDefault() is Skeleton3D sk && sk.FindBone("handSlotRight") >= 0)
        {
            _hand = new BoneAttachment3D { BoneName = "handSlotRight" };
            sk.AddChild(_hand);
        }
        Play("Idle");
    }

    /// <summary>Swimming pose: the body leans forward (<see cref="UnderwaterFx.SwimLeanDegrees"/>) and moves at the slow walk; stands up again when not swimming.</summary>
    public void SetSwim(bool swimming)
    {
        if (swimming == _swim) return;
        _swim = swimming;
        if (_model != null) _model.RotationDegrees = new Vector3(swimming ? UnderwaterFx.SwimLeanDegrees : 0f, 180, 0);
        if (_anim != null) _anim.SpeedScale = swimming ? 0.5f : 1f;
    }

    /// <summary>Update from snapshot state: horizontal speed (m/s), whether it fired in the last moments, dead, and the weapon id it holds.</summary>
    public void Apply(float speed, bool firing, bool dead, string weaponId)
    {
        SetWeapon(dead ? "" : weaponId);
        if (!HasModel) return;
        var twoHanded = weaponId is "ak47" or "bazooka" or "sniper" or "m82" or "awm" or "svd";
        Play(dead ? "Defeat"
            : firing ? (weaponId == "knife" ? "Attack(1h)" : twoHanded ? "Shooting(2h)" : "Shooting(1h)")
            : _swim ? (speed > 0.6f ? "Walk" : "Idle") // no swim clip in the rig: the slow walk, see SetSwim
            : speed > 6.2f ? "Run" : speed > 0.6f ? "Walk" : "Idle");
    }

    void Play(string anim)
    {
        if (_anim == null || anim == _playing || !_anim.HasAnimation(anim)) return;
        _playing = anim;
        _anim.Play(anim, 0.12);
    }

    /// <summary>Barrel tip of the held weapon in world space, or null when nothing is held.</summary>
    public Vector3? MuzzlePosition => _held != null && GodotObject.IsInstanceValid(_held) && _held.IsInsideTree() ? _held.GetNodeOrNull<Node3D>("Muzzle")?.GlobalPosition : null;

    /// <summary>Flash the held weapon's muzzle.</summary>
    public void Flash(float scale = 1f) => MuzzleFlash.Of(_held)?.Flash(scale);

    void SetWeapon(string id)
    {
        if (id == _heldId || _hand == null) return;
        _heldId = id;
        _held?.QueueFree();
        _held = id == "" ? null : WeaponFx.Make(id);
        if (_held == null) return;
        // the hand slot sits at the end of the arm; the weapon is built in metres and the rig is scaled by Scale
        _held.Scale = Vector3.One / Scale;
        _hand.AddChild(_held);
    }
}
