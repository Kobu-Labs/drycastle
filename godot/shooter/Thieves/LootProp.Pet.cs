using Godot;

namespace Drycastle.Shooter.Thieves;

// Ticket 05 (pets): a carried pet is the animal itself, drawn by AnimalsClient at the position the server moves it to (under the Thief's arm, a held pose: AnimalState.Carried), so the carried prop is empty.
// A pet is never lying Loot (it walks home when its carrier is killed): LyingPet is never asked for and stays the stub.
public static partial class LootProp
{
    static Node3D CarriedPet() => new() { Name = "CarriedPet" };
    static Node3D LyingPet() => Stub(new Color(0.9f, 0.6f, 0.25f), 0.3f);
}
