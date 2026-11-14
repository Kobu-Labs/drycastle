using Godot;

namespace Drycastle.Shooter;

// Ticket 05 (pets): a carried pet is the animal itself under the Thief's arm (drawn from the replicated position), and a pet is never lying Loot (it walks home): these stubs stand until it says otherwise.
public static partial class LootProp
{
    static Node3D CarriedPet() => Stub(new Color(0.9f, 0.6f, 0.25f), 0.3f);
    static Node3D LyingPet() => Stub(new Color(0.9f, 0.6f, 0.25f), 0.3f);
}
