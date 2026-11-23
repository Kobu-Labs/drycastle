using System.Numerics;

namespace Drycastle.ShooterCore.Vehicles;

/// <summary>
/// How a trailer lies on the ground (#482): its pitch and roll and the place of its origin, from the ground heights under it. One function for the server's hull (bullets, blast, the on-foot solid box)
/// and for the client's drawing and its own copy of that box, so both turn the same way. The trailer stands on its axle, turned about the axle's contact point: pitch from how high the drawbar eye is
/// over the axle's ground (the hitch's height when pulled; loose, the eye's height on the trailer plus the ground's rise from the axle to the eye), roll from the ground left and right of the axle.
/// </summary>
public static class TrailerAttitude
{
    /// <summary>The most the trailer is turned either way, radians.</summary>
    public const float MaxTilt = 0.45f;

    /// <summary>Pitch (positive: drawbar end up), roll (positive: right side up), the axle's contact point and where the trailer's origin is when it is turned about that point.</summary>
    public readonly record struct Result(float Pitch, float Roll, Vector3 Axle, Vector3 Origin);

    /// <summary>
    /// The attitude: <paramref name="eyeAboveAxleGround"/> as <see cref="TowModel.PitchFor"/> takes it; <paramref name="groundLeft"/> and <paramref name="groundRight"/> the ground at
    /// <paramref name="halfSpan"/> to either side of the axle's contact (<paramref name="axleGround"/> is the ground at it, <paramref name="axleX"/>/<paramref name="axleZ"/> its place).
    /// </summary>
    public static Result Of(in TowGeometry g, float yaw, float axleX, float axleZ, float axleGround, float eyeAboveAxleGround, float groundLeft, float groundRight, float halfSpan)
    {
        var pitch = TowModel.PitchFor(g, eyeAboveAxleGround, MaxTilt);
        var roll = Math.Clamp(MathF.Atan2(groundRight - groundLeft, 2 * halfSpan), -MaxTilt, MaxTilt);
        var axle = new Vector3(axleX, axleGround, axleZ);
        return new Result(pitch, roll, axle, OriginAbout(axle, g.EyeZ + g.Drawbar, yaw, pitch, roll));
    }

    /// <summary>The origin of a trailer whose axle (<paramref name="axleBack"/> metres behind it) is at <paramref name="axle"/> and which is turned by this attitude.</summary>
    public static Vector3 OriginAbout(Vector3 axle, float axleBack, float yaw, float pitch, float roll) =>
        axle - FlightMath.Rotate(new Vector3(0, 0, axleBack), yaw, pitch, roll);
}
