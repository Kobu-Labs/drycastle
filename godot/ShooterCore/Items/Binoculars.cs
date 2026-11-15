namespace Drycastle.ShooterCore;

/// <summary>
/// The binoculars (GLOSSARY "Binoculars"): an item carried in the Bag (items.json <c>binoculars</c>), raised and lowered with one key. Raised, the
/// view is narrowed to <see cref="FovDeg"/> and the middle of it reads the distance to what is under it, the number a sniper dials as the Zero
/// (<see cref="BulletDrop"/>). Client only: the distance is measured against the world the client draws, nothing travels.
/// </summary>
public static class Binoculars
{
    /// <summary>Vertical FOV while raised, degrees (about 8x of the default view).</summary>
    public const float FovDeg = 9f;
    /// <summary>Farthest distance read, metres; beyond it (the sky) there is no reading.</summary>
    public const float MaxRangeM = 1500f;
    /// <summary>Seconds between two measurements, so the number does not flicker.</summary>
    public const double MeasureSeconds = 0.1;

    /// <summary>The number shown for a measured distance: whole metres, or null with nothing in range.</summary>
    public static int? Reading(float? metres) => metres is { } m && float.IsFinite(m) && m <= MaxRangeM ? (int)MathF.Round(m) : null;

    /// <summary>True when one of the carried items is a pair of binoculars.</summary>
    public static bool Carried(ItemsFile items, IEnumerable<int?> bagItems)
    {
        foreach (var i in bagItems)
            if (i is { } index && items.At(index) is { Binoculars: true }) return true;
        return false;
    }
}
