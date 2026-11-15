using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// The Loot a Thief carries (docs/events/thieves/04, 08): the hp byte says what. The kind's prop (<see cref="LootProp.Carried"/>) rides the chest bone, so it moves with the body, and the <see cref="CarryPose"/> overlay
/// bends the arms to hold it (thieves.json <c>gang.view.carry</c>: TwoHands for a tire or a dish, UnderArm for a pet, OneHand for a connector) over the clip the member plays. A body without a skeleton (the capsule)
/// holds the prop in front of its chest. The overlay is off while the clip must have its own arms (climbing, electrocution, dead).
/// </summary>
public sealed partial class GangMemberView
{
    static readonly Vector3 CapsuleCarryAt = new(0, 1.05f, -0.38f);

    Change<byte> _carriedShown;
    Node3D? _carriedProp;
    CarryPose? _carry;
    LootKind? _carrying;
    float _carryTarget;
    Tween? _carryFade;

    /// <summary>The overlay modifier (in the skeleton, off until something is carried).</summary>
    void BuildCarry()
    {
        _carry = new CarryPose { Name = "Carry", Influence = 0f, Active = false, ChestBone = _view.Carry.ChestBone };
        _skeleton!.AddChild(_carry);
    }

    /// <summary>Shows what the member carries (<see cref="GangRules.CarriedOf"/> of its hp byte); nothing once it is dead (the Loot lies where it fell, as an entity of its own).</summary>
    public void SetCarried(byte hp)
    {
        var carried = GangRules.AliveOf(hp) ? GangRules.CarriedOf(hp) : (byte)0;
        if (!_carriedShown.Update(carried)) return;
        _carriedProp?.QueueFree();
        _carriedProp = null;
        _carrying = LootKinds.OfCarried(carried);
        _carryTarget = -1;
        if (_carrying is not { } kind) { Fade(0); return; }
        var prop = LootProp.Carried(kind);
        var data = _view.Carry.Kinds.GetValueOrDefault(kind.ToString().ToLowerInvariant());
        if (_skeleton != null && data != null && _view.Carry.ChestBone is var chest && _skeleton.FindBone(chest) >= 0)
        {
            var rest = new Transform3D(Basis.FromEuler(new Vector3(0, Mathf.Pi, 0)), V3(data.PropAt)); // the prop faces -Z like the Thief; the skeleton's rest pose faces +Z
            _carriedProp = Mount(chest, rest, prop, "Carried");
        }
        else
        {
            prop.Position = CapsuleCarryAt;
            _pivot.AddChild(prop);
            _carriedProp = prop;
        }
        UpdateCarry(_stateNow);
    }

    /// <summary>The arms: held on the prop while the state lets the overlay work, else free for the clip.</summary>
    void UpdateCarry(GangMemberState state)
    {
        if (_carry == null) return;
        var pose = _carrying is { } k && _view.Carry.Kinds.TryGetValue(k.ToString().ToLowerInvariant(), out var kind) ? _view.Carry.Poses.GetValueOrDefault(kind.Pose) : null;
        var on = pose != null && state is not (GangMemberState.Dead or GangMemberState.ClimbUp or GangMemberState.ClimbDown or GangMemberState.WorkAloft or GangMemberState.Shocked or GangMemberState.Falling or GangMemberState.Down or GangMemberState.GetUp);
        if (pose != null) Goals(pose);
        Fade(on ? 1f : 0f);
    }

    /// <summary>Where the hands go for a pose (the pet's: where the server holds it).</summary>
    void Goals(CarryPoseData pose)
    {
        var pet = _data.Loot.Pet;
        _carry!.Right = pose.PetOffset is { } o ? new Vector3(-(pet.CarryRightM + o[0]), pet.CarryUpM + o[1], pet.CarryForwardM + o[2]) : pose.Right is { } r ? V3(r) : null; // rest axes: the right hand is -X, forward +Z
        _carry.Left = pose.Left is { } l ? V3(l) : null;
        _carry.ElbowDown = pose.ElbowDown; _carry.ElbowOut = pose.ElbowOut; _carry.HandRollDeg = pose.HandRollDeg;
    }

    void Fade(float to)
    {
        if (_carry == null || to == _carryTarget) return;
        _carryTarget = to;
        _carryFade?.Kill();
        if (to > 0) _carry.Active = true;
        _carryFade = CreateTween();
        _carryFade.TweenProperty(_carry, "influence", to, _view.Carry.BlendS);
        if (to <= 0) _carryFade.TweenCallback(Callable.From(() => { if (_carryTarget <= 0 && _carry != null) _carry.Active = false; }));
    }
}
