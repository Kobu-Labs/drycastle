namespace Drycastle.ShooterCore;

/// <summary>
/// Where a bullet entered a hunted animal, as the hit diagram draws it (GLOSSARY "Hit diagram"), in the units of <see cref="HuntShot"/>'s wire:
/// <see cref="Along"/> and <see cref="Height"/> 0..255 for 0..1 of the hit shape (or body box), the flank, whether it met the head, and the
/// <see cref="Pose"/> and <see cref="Frame"/> of the shape it was tested against (<see cref="HuntShot.NoShape"/>: the body box).
/// </summary>
public readonly record struct HitDiagram(byte Along, byte Height, bool RightFlank, bool Head, byte Pose, byte Frame)
{
    public float AlongF => Along / 255f;
    public float HeightF => Height / 255f;

    public static HitDiagram From(in HuntShotMsg m) => new(Quantize(m.Along), Quantize(m.Height), m.RightFlank, m.Head, m.Pose, m.Frame);

    static byte Quantize(float v) => (byte)MathF.Round(Math.Clamp(v, 0f, 1f) * 255f);
}

/// <summary>
/// One hunt (GLOSSARY "Hunt"): a player's logged kill of a wild animal, or catch of a mystery creature. <see cref="Species"/> is the index id (or the
/// mystery id), <see cref="Exact"/> the animals.json species and <see cref="Variant"/> its look ("" the base one, else a variant id: a moose bull).
/// <see cref="Kg"/> is null for a mystery, <see cref="DistanceM"/> for a run over and a catch, <see cref="Diagram"/> where no shape was hit.
/// <see cref="Id"/> is 0 until the store has the row.
/// </summary>
public sealed record HuntRecord(long Id, long PlayerId, string Species, string Exact, string Variant, float? Kg, float? DistanceM, string Weapon,
    HitDiagram? Diagram, float X, float Y, float Z, DateTime AtUtc)
{
    public bool IsMystery => Mysteries.Is(Species);

    /// <summary>Whether it can hold a longest-shot record: a distance, and not a run over.</summary>
    public bool CountsForLongest => DistanceM is not null && Weapon != HuntWeapons.RunOver;

    /// <summary>Weights and distances are kept to a tenth: what the board shows, and what a tie means.</summary>
    public static float Round(float v) => MathF.Round(v, 1);

    /// <summary>The hunt of a weighed wild animal; null when its species is not one the log keeps.</summary>
    public static HuntRecord? ForAnimal(long player, string animalId, string variantId, float kg, float? distanceM, string weapon, HitDiagram? diagram, float x, float y, float z, DateTime atUtc)
    {
        if (IndexSpecies.Of(animalId) is not { } index || !(kg > 0)) return null;
        var run = weapon == HuntWeapons.RunOver;
        return new HuntRecord(0, player, index, animalId, variantId, Round(kg), run || distanceM is null ? null : Round(distanceM.Value), weapon, run ? null : diagram, x, y, z, atUtc);
    }

    /// <summary>The catch of a mystery creature (its Event won): no weight, no distance, no hit diagram.</summary>
    public static HuntRecord ForMystery(long player, string mystery, float x, float y, float z, DateTime atUtc) =>
        new(0, player, mystery, mystery, "", null, null, HuntWeapons.Event, null, x, y, z, atUtc);
}

/// <summary>A stored hunt with the name its hunter has now (players.name).</summary>
public sealed record HuntRow(HuntRecord Hunt, string Hunter);
