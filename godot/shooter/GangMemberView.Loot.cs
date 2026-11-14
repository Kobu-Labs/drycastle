using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>The Loot a Thief carries (docs/events/thieves/04): the hp byte says what, and the kind's prop (<see cref="LootProp.Carried"/>) is held in front of the chest while it is alive. The prop is a child of the body's pivot, so it leans and kneels with it.</summary>
public sealed partial class GangMemberView
{
    static readonly Vector3 CarryAt = new(0, 1.05f, -0.38f);

    Change<byte> _carriedShown;
    Node3D? _carriedProp;

    /// <summary>Shows what the member carries (<see cref="GangRules.CarriedOf"/> of its hp byte); nothing once it is dead (the Loot lies where it fell, as an entity of its own).</summary>
    public void SetCarried(byte hp)
    {
        var carried = GangRules.AliveOf(hp) ? GangRules.CarriedOf(hp) : (byte)0;
        if (!_carriedShown.Update(carried)) return;
        _carriedProp?.QueueFree();
        _carriedProp = null;
        if (LootKinds.OfCarried(carried) is not { } kind) return;
        _carriedProp = LootProp.Carried(kind);
        _carriedProp.Position = CarryAt;
        _pivot.AddChild(_carriedProp);
    }
}
