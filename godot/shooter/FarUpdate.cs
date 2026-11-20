using Godot;

namespace Drycastle.Shooter;

/// <summary>
/// How often a far or off-screen thing needs its animation (client perf, #449): the time between two updates from where the camera is.
/// Near things (and anything within <see cref="NearOffScreen"/> m, seen or not) update every frame; beyond <see cref="Near"/> m on screen they
/// update at 30 Hz, beyond <see cref="Far"/> m at 15 Hz; beyond <see cref="NearOffScreen"/> m and outside the view at 5 Hz, so what comes back into view is at most a fifth of a second behind.
/// Shared by anything that steps its own animation by hand (<see cref="CharacterView"/>).
/// </summary>
public static class FarUpdate
{
    public const float Near = 30f, Far = 60f, NearOffScreen = 10f;
    public const float MidInterval = 1f / 30f, FarInterval = 1f / 15f, OffScreenInterval = 0.2f;

    /// <summary>Seconds between updates for something <paramref name="height"/> m tall standing at <paramref name="at"/>; 0 means every frame.</summary>
    public static float Interval(Camera3D? cam, Vector3 at, float height)
    {
        if (cam == null) return 0f;
        var d2 = at.DistanceSquaredTo(cam.GlobalPosition);
        if (d2 <= NearOffScreen * NearOffScreen) return 0f;
        var seen = cam.IsPositionInFrustum(at) || cam.IsPositionInFrustum(at + Vector3.Up * (height * 0.5f)) || cam.IsPositionInFrustum(at + Vector3.Up * height);
        if (!seen) return OffScreenInterval;
        if (d2 <= Near * Near) return 0f;
        return d2 <= Far * Far ? MidInterval : FarInterval;
    }
}
