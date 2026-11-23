using Drycastle.ShooterCore.Hunts;
using Drycastle.ShooterCore.Wildlife;

namespace Drycastle.Shooter.Hunts;

/// <summary>
/// Turns a species or a recorded hit into what <see cref="AnimalPicture"/> draws: the look's standing shape and its baked picture; a species with no shape
/// its own <c>animals/&lt;id&gt;.png</c>; one with neither (the paid packs' species when their folder is absent, a Mystery creature) a plain body box of the
/// proportions of its <c>hit_box</c> in animals.json (an upright box for a Mystery creature).
/// </summary>
public sealed class HuntArtResolver
{
    /// <summary>The proportions of an upright creature (a Bigfoot) as a body box: taller than long.</summary>
    const float UprightAspect = 0.5f;

    readonly AnimalShapes _shapes;
    readonly AnimalsFile _animals;

    public HuntArtResolver(AnimalShapes shapes, AnimalsFile animals) { _shapes = shapes; _animals = animals; }

    public AnimalShapes Shapes => _shapes;

    /// <summary>A species the log lists (an index id or a visible mystery id): its standing shape.</summary>
    public AnimalArt ForSpecies(string key) => Art(_shapes.Frame(key, AnimalPose.Idle, 0), key);

    /// <summary>The animal of a shape that was just picked (the sniper's card) or of a species.</summary>
    public AnimalArt Art(AnimalShape? shape, string speciesId)
    {
        var pic = shape != null ? HuntArt.Of(shape) : HuntArt.OfSpecies(speciesId);
        float aspect;
        if (shape != null) aspect = shape.Length / shape.Height;
        else if (pic != null && pic.Fill.GetHeight() > 0) aspect = pic.Fill.GetWidth() / (float)pic.Fill.GetHeight();
        else if (Mysteries.Is(speciesId)) aspect = UprightAspect;
        else if (_animals.Find(speciesId)?.HitBox is { Count: >= 3 } box && box[1] > 0) aspect = box[2] / box[1]; // [width, height, length]
        else aspect = AnimalArt.Box.Aspect;
        return new AnimalArt(shape, pic, aspect);
    }
}
