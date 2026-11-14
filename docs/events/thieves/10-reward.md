# 10 Reward

## Facts
- Bigfoot pattern: `OnBigfootDied` / `SpawnBigfootReward` (`NetServer.Bigfoot.cs`): XP `event_win` (`levels.json`), a `Corpse` carrying an `ItemStack`.
- `items.json` is append-only (the wire sends the list position). `EquipSlot` has only `Headgear` (`Items/ItemData.cs`). Worn Items: ADR 0016; Bag and corpses: ADR 0018. Worn Item drawing: `CharacterView.Worn.cs`. Equipment screen (F) shows the slots.
- Corpses are capped (`MaxCorpses` 16).

## Do
- Credit list on the event: players who landed a hit on any gang member or van, or sent Loot back. At the win each gets `event_win` XP. Nobody credited: no XP.
- Item `gold_chain`: appended to `items.json`, new `EquipSlot.Neck`, cosmetic, `pickup_limit` 3. Drawn on the wearer's neck (private model with a simple generated fallback). Icon.
- Each Gang boss killed leaves the chain: on its body as a lootable corpse-with-Item (as the Bigfoot's), lasting `chain_corpse_seconds`. A boss who boards and leaves, or flees off the map, leaves nothing.
- Equipment screen: the Neck slot appears (invoke the `ui-work` skill first; `docs/standards/ui-ux.md`).
- Glossary: Gold chain entry; `/event` entry mentions `thieves`.

## Accept
- Tests: credit rules; one chain per killed boss, none for a boss who left; Neck slot wear/unwear and Bag rules as for Headgear; pickup limit.
- Protocol bumped (new slot on the wire).
