namespace Drycastle.ShooterCore;

// Ticket 07 (poles) puts the connector target kind here: an IStealSource class (see TireTarget.cs for the pattern; a pole needs a climb, so its StealTarget carries a Top), registered by one line in
// NetServer.Thieves.Loot.cs (InitLoot), with its numbers in ConnectorLootConfig and the "connector" sub-section of the thieves.json "loot" section. Nothing else of the Loot code changes.

/// <summary>The <c>loot.connector</c> sub-section of thieves.json. Ticket 07 adds its numbers here.</summary>
public sealed class ConnectorLootConfig : LootKindConfig
{
    public ConnectorLootConfig() { StealSeconds = 6f; }
}
