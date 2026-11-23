using Godot;
using Drycastle.ShooterCore.Vehicles;

namespace Drycastle.Shooter.Net.Client;

/// <summary>How a reconcile's jump is kept out of the picture: the body, the helicopter and the amphibious transport move the picture by the difference and fade it out.</summary>
internal static class Correction
{
    /// <summary>How fast a correction's jump is faded out of the picture, per second.</summary>
    public const float FadePerSecond = 10f;

    /// <summary>
    /// The turn a correction made (yaw, pitch, roll), as the difference of the attitude before and after, the yaw wrapped. A correction that turns
    /// more than a radian (a flip, a reset) is not glided over.
    /// </summary>
    public static Vector3 AngleDelta(Vector3 before, float yawAfter, float pitchAfter, float rollAfter)
    {
        var d = new Vector3(FlightMath.Wrap(before.X - yawAfter), before.Y - pitchAfter, before.Z - rollAfter);
        return d.Length() > 1f ? Vector3.Zero : d;
    }
}
