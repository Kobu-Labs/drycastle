using Godot;

namespace Drycastle.Shooter;

/// <summary>
/// How often the client updates a creature (animal, flock, citizen, Gang member): every frame near the camera, every few frames far off or out of view.
/// A creature that is skipped keeps its last place and pose; the frame it is due again it takes the time it missed in one step, so what is shown
/// is the same, a little coarser, at a distance where that cannot be seen. Client side only: the server's creatures and their state are not touched.
/// </summary>
public static class CreatureLod
{
    const float FullRateM = 35f;
    const float HalfRateM = 70f;
    const float QuarterRateM = 130f;
    const int FarStride = 6;
    const int OffscreenStride = 3;
    const float OffscreenMinM = 20f; // closer than this a body out of view is still heard, shadowed and about to turn into view

    /// <summary>Frames between two updates of a creature at <paramref name="at"/> as seen from <paramref name="cam"/> (1 = every frame).</summary>
    public static int Stride(Camera3D? cam, Vector3 at)
    {
        if (cam == null) return 1;
        var d = cam.GlobalPosition.DistanceTo(at);
        var stride = d < FullRateM ? 1 : d < HalfRateM ? 2 : d < QuarterRateM ? 4 : FarStride;
        if (d > OffscreenMinM && stride < OffscreenStride && !cam.IsPositionInFrustum(at)) stride = OffscreenStride;
        return stride;
    }

    /// <summary>Whether the creature <paramref name="id"/> is updated this frame: the ids are spread over the frames so the work does not come in one lump.</summary>
    public static bool Due(ushort id, int stride) => stride <= 1 || (Engine.GetProcessFrames() + id) % (ulong)stride == 0;

    /// <summary>
    /// Plays a creature's clips at its rate: every frame while <paramref name="slow"/> is false, else by hand once the stride is up with the time it missed
    /// (<paramref name="owed"/> collects it). Returns the time to step the creature's own motion by this frame (0 when it is not due).
    /// </summary>
    public static double Pace(AnimationPlayer? anim, ref double owed, double dt, bool slow, bool due)
    {
        if (!slow)
        {
            if (anim != null && anim.CallbackModeProcess != AnimationMixer.AnimationCallbackModeProcess.Idle) anim.CallbackModeProcess = AnimationMixer.AnimationCallbackModeProcess.Idle;
            var step = owed + dt;
            owed = 0;
            return step;
        }
        owed += dt;
        if (!due) return 0;
        if (anim != null)
        {
            if (anim.CallbackModeProcess != AnimationMixer.AnimationCallbackModeProcess.Manual) anim.CallbackModeProcess = AnimationMixer.AnimationCallbackModeProcess.Manual;
            anim.Advance(owed);
        }
        var run = owed;
        owed = 0;
        return run;
    }
}
