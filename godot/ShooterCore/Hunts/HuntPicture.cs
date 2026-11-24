using Drycastle.ShooterCore.Wildlife;

namespace Drycastle.ShooterCore.Hunts;

/// <summary>
/// How a recorded hit is drawn: the standing shape of the animal (null: its baked picture, else its body box), the entry marked on it (0 rear to 1 front, 0 ground to 1 top),
/// whether it met the head. The one rule of the hit card after a sniper kill, now for any logged hunt (<see cref="HuntPicture.Resolve"/>).
/// </summary>
public readonly record struct HitPictureSpec(string ShapeKey, string SpeciesId, AnimalShape? Standing, bool HasMark, float Along, float Height, bool Head);

public static class HuntPicture
{
    /// <summary>
    /// The picture of a hunt: the look's standing shape; the hit moved from the shape it was tested against onto it (a head hit on the head, a body hit on
    /// the same place of the body); with the body box tested (<see cref="HuntShot.NoShape"/>) the species' picture drawn by the box. No diagram (a run over, a
    /// catch) no mark; a hidden hunt none either (the caller draws the blob).
    /// </summary>
    public static HitPictureSpec Resolve(AnimalShapes shapes, HuntView h)
    {
        var key = HuntFormat.ShapeKey(h.Exact, h.Variant);
        var standing = shapes.Frame(key, AnimalPose.Idle, 0);
        if (h.Diagram is not { } d) return new HitPictureSpec(key, h.Exact, standing, false, 0, 0, false);
        float along = d.AlongF, height = d.HeightF;
        if (standing != null && d.Pose != HuntShot.NoShape && shapes.Frame(key, (AnimalPose)d.Pose, d.Frame) is { } hitOn)
            (along, height) = hitOn.MapOnto(standing, along, height, d.Head);
        else if (d.Pose == HuntShot.NoShape) standing = null;
        return new HitPictureSpec(key, h.Exact, standing, true, along, height, d.Head);
    }
}
