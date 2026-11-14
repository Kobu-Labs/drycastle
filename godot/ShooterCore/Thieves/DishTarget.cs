namespace Drycastle.ShooterCore;

// Ticket 06 (dishes) puts the dish target kind here: an IStealSource class (see TireTarget.cs for the pattern; a dish needs a climb, so its StealTarget carries a Top), registered by one line in
// NetServer.Thieves.Loot.cs (InitLoot), with its numbers in DishLootConfig and the "dish" sub-section of the thieves.json "loot" section. Nothing else of the Loot code changes.

/// <summary>The <c>loot.dish</c> sub-section of thieves.json. Ticket 06 adds its numbers here.</summary>
public sealed class DishLootConfig : LootKindConfig
{
    public DishLootConfig() { StealSeconds = 8f; }
}
