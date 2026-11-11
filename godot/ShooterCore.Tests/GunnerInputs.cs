using System.Numerics;
using Suchohrad.ShooterCore;

namespace Suchohrad.ShooterCore.Tests;

/// <summary>
/// The input a gunner's client sends at zero lag (#161): the look (yaw, pitch) and the aim, which is the ground point that look meets
/// from the control's own muzzle, the same computation the client does. Tests with lag build their aim by hand instead.
/// </summary>
public static class GunnerInputs
{
    public static NetInput Look(GunshipControl c, float yaw, float pitch, MoveButtons buttons = MoveButtons.None, byte slot = 0)
    {
        var yawQ = Quant.Yaw(yaw);
        var pitchQ = GunshipAim.ClampPitch(Quant.Pitch(Quant.Pitch(pitch)), c.Def);
        var aim = GunshipAim.AimPoint(c.Muzzle, Quant.Yaw(yawQ), pitchQ, c.Aircraft.GroundY, GunshipAim.MaxDistance(c.Def));
        return NetInput.From(0, new MoveInput { Yaw = yaw, Buttons = buttons }, pitch, slot, aim);
    }
}
