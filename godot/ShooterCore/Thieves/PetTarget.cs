namespace Drycastle.ShooterCore;

// Ticket 05 (pets) puts the pet target kind here: an IStealSource class (see TireTarget.cs for the pattern), registered by one line in NetServer.Thieves.Loot.cs (InitLoot), with its numbers in
// PetLootConfig and the "pet" sub-section of the thieves.json "loot" section. Nothing else of the Loot code changes.

/// <summary>The <c>loot.pet</c> sub-section of thieves.json. Ticket 05 adds its numbers here.</summary>
public sealed class PetLootConfig : LootKindConfig
{
    public PetLootConfig() { StealSeconds = 4f; }
}
