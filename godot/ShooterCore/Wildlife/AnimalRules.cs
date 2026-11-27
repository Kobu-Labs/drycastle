using System.Numerics;
using Drycastle.ShooterCore.Net;

namespace Drycastle.ShooterCore.Wildlife;

/// <summary>Animal world entity rules: the wire type id and the state byte the clients read.</summary>
public static class AnimalRules
{
    /// <summary>Wire id of the animal world entity type (1 corpse, 10 missile, 20 turret, 30 care package, 40 aircraft).</summary>
    public const byte TypeId = 50;

    /// <summary>State byte: bits 0-3 the <see cref="AnimalState"/>, bit 4 flips on every hit that did not kill (a client plays its hit reaction when the bit changes), bits 5-7 the size step (<see cref="SizeOfStep"/>; constant for the animal's life).</summary>
    public const byte StateMask = 15, HitToggle = 16, SizeShift = 5;

    /// <summary>Size steps the state byte carries (0 .. <see cref="SizeSteps"/> - 1), and the scale of the smallest and the largest.</summary>
    /// <summary>Metres between two replicated positions of an animal above which it was teleported (<see cref="Animal.Teleport"/>), not moved: the client's buffer starts over so it is not drawn sliding there. The fastest thing an animal does is a thrown body.</summary>
    public const float TeleportM = 40f;

    public const int SizeSteps = 8;
    public const float MinSize = 0.7f, MaxSize = 1.4f;

    /// <summary>The weight roll of a new animal, 0..1: the mean of two uniform draws (a triangle), so it leans to the middle but the big and the small ones are not rare. Draws exactly two values from <paramref name="rng"/>.</summary>
    public static float RollSize(Random rng) => (float)((rng.NextDouble() + rng.NextDouble()) / 2.0);

    /// <summary>The size step of a roll: 0 .. <see cref="SizeSteps"/> - 1.</summary>
    public static int StepOf(float roll) => Math.Clamp((int)MathF.Round(Math.Clamp(roll, 0f, 1f) * (SizeSteps - 1)), 0, SizeSteps - 1);

    /// <summary>The scale (1 = the species' model and boxes as measured) of a size step.</summary>
    public static float SizeOfStep(int step) => MinSize + (MaxSize - MinSize) * Math.Clamp(step, 0, SizeSteps - 1) / (SizeSteps - 1);

    /// <summary>The size step in a state byte (what a client scales the model by, for a species with a weight).</summary>
    public static int SizeStepOf(byte state) => state >> SizeShift;

    /// <summary>The weight in kg of a roll within a species' range [min, max].</summary>
    public static float WeightOf(IReadOnlyList<float> range, float roll) => range[0] + (range[1] - range[0]) * Math.Clamp(roll, 0f, 1f);

    /// <summary>The owner field of an animal's replicated state: the species index in the low byte, the variant index (which model of the species) in the high byte.</summary>
    public static ushort Look(int species, int variant) => (ushort)(species | variant << 8);
    public static int SpeciesOf(ushort look) => look & 0xFF;
    public static int VariantOf(ushort look) => look >> 8;

    /// <summary>
    /// Whether an animal in this state stands where it is, and so is a body a player on foot cannot walk through: idle, eating, sitting, barking. One that walks or runs is not (the server and a
    /// client would see it in different places a moment apart, and the player would be stopped by a body that is no longer there); it stops or steers for a player itself.
    /// </summary>
    public static bool Stands(AnimalState state) => state is AnimalState.Idle or AnimalState.Eat or AnimalState.Sit or AnimalState.Bark or AnimalState.Vanish or AnimalState.Crashed;

    public static AnimalState StateOf(byte state) => (AnimalState)(state & StateMask);
    public static bool HitToggled(byte state) => (state & HitToggle) != 0;

    public static void Register(WorldEntityRegistry registry)
    {
        registry.RegisterOnce(new WorldEntityDef(TypeId, "animal", HasHp: true, Interest: true,
            SeenFromAfar: s => StateOf(s.State) is AnimalState.Flying or AnimalState.Falling)); // the Phoenix in the sky: a light the whole map sees
    }
}
