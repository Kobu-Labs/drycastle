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
    const float OutsideMarginM = 3f; // a body this close outside a view plane still counts as in view
    const float OffscreenMinM = 20f; // closer than this a body out of view is still heard, shadowed and about to turn into view

    static ulong _frame = ulong.MaxValue;
    static bool _have;
    static Vector3 _eye;
    static readonly Plane[] Planes = new Plane[6];
    const int Recheck = 8; // a creature's stride is worked out again this often (frames), not every frame

    /// <summary>Takes the camera's place and view planes once a frame (the first call of a frame does; the others return at once): the per-creature tests after it are plain arithmetic, no engine calls.</summary>
    public static void BeginFrame(Camera3D? cam)
    {
        var f = Engine.GetProcessFrames();
        if (f == _frame) return;
        _frame = f;
        _have = cam != null;
        if (cam == null) return;
        _eye = cam.GlobalPosition;
        var fr = cam.GetFrustum();
        for (var i = 0; i < Planes.Length && i < fr.Count; i++) Planes[i] = fr[i];
    }

    /// <summary>Whether the stride <paramref name="cached"/> of creature <paramref name="id"/> is to be worked out again (<see cref="Of"/>) this frame: it has none yet, or its recheck frame is here.</summary>
    public static bool Stale(byte cached, ushort id) => cached == 0 || (_frame + id) % Recheck == 0;

    /// <summary>Frames between two updates of a creature at <paramref name="at"/> (1 = every frame): by its distance from the camera and whether it is in view.</summary>
    public static byte Of(Vector3 at)
    {
        if (!_have) return 1;
        var d2 = _eye.DistanceSquaredTo(at);
        var stride = d2 < FullRateM * FullRateM ? 1 : d2 < HalfRateM * HalfRateM ? 2 : d2 < QuarterRateM * QuarterRateM ? 4 : FarStride;
        if (d2 > OffscreenMinM * OffscreenMinM && stride < OffscreenStride)
            foreach (var p in Planes)
                if (p.DistanceTo(at) > OutsideMarginM) { stride = OffscreenStride; break; }
        return (byte)stride;
    }

    /// <summary>Whether the creature <paramref name="id"/> is updated this frame: the ids are spread over the frames so the work does not come in one lump.</summary>
    public static bool Due(ushort id, int stride) => stride <= 1 || (_frame + id) % (ulong)stride == 0;

    /// <summary>
    /// Plays a creature's clips at its rate: every frame while <paramref name="slow"/> is false, else by hand once the stride is up with the time it missed
    /// (<paramref name="owed"/> collects it). Returns the time to step the creature's own motion by this frame (0 when it is not due).
    /// </summary>
    public static double Pace(AnimationPlayer? anim, ref bool manual, ref double owed, double dt, bool slow, bool due)
    {
        if (!slow)
        {
            if (manual) { manual = false; if (anim != null) anim.CallbackModeProcess = AnimationMixer.AnimationCallbackModeProcess.Idle; }
            var step = owed + dt;
            owed = 0;
            return step;
        }
        owed += dt;
        if (!due) return 0;
        if (anim != null)
        {
            if (!manual) { manual = true; anim.CallbackModeProcess = AnimationMixer.AnimationCallbackModeProcess.Manual; }
            anim.Advance(owed);
        }
        var run = owed;
        owed = 0;
        return run;
    }
}
