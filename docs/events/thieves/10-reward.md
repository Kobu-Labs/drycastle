# 10 Reward

## Facts
- Bigfoot pattern: `BigfootServer.OnDied` / `BigfootLoot.SpawnBody` (`Net/Server/BigfootEvent/BigfootServer.cs`): XP `event_win` (`levels.json`), a `Corpse` carrying an `ItemStack`.
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

## Done: notes
Code: pure `godot/ShooterCore/Thieves/ThievesCredit.cs` (`ThievesCredit`, `CreditedGangHits`, `ThievesReward`), server `godot/shooter/Net/Server/NetServer.Thieves.Reward.cs`, data `thieves.json` section `reward` (last), `EquipSlot.Neck`, `items.json` `gold_chain`. Tests `ThievesRewardTests.cs` (+ the item, bag, wire and Equipment screen tests moved to two slots). Protocol 44 (the merge renumbers).

### API for later tickets
- `NetServer.Credit` (`ThievesCredit`): `NoteVanHit(player)` (03: call where the van takes a counted hit), `NoteLootReturned(player)` (04: when the hold-to-send-back completes), `NoteGangHit(player)` (wired), `Credited`, `Settle(won, eligible)`, `Clear()`.
- A hit on a member is credited by `CreditedGangHits`, which wraps the roster as `_match.Gang` (set in `InitThievesReward`, called by `InitThieves` after `InitGangs`): any weapon path, every hit the member did not ignore (a dead body or a zero weight credits nobody).
- Hooks in `NetServer.Thieves.cs` (one line each): `InitThievesReward()`, `GiveThievesReward(won)` at the end of `ThievesFinish`, `ResetThievesReward()` in `ThievesRoundReset`.

### Decisions
- Payout: on `ThievesFinish(won: true)` (the end by 03's last-van rule, or `/event current end`) each credited player still in the match who `Scores` (no bots, no sandbox) gets `Levels.Awards.EventWin` once; a chat line names up to 8. Not won, or round reset: nobody, list cleared. No new number for the XP (it is the level data's `event_win`).
- Chain: `GangRoster.Killed` with `Kind == GangBoss` (a hit kill or a run-over; `Killed` never fires for flee, board or clear) spawns a `Corpse` at the body (owner `ThievesReward.ChainCorpseOwner` = 60001, the knife as its weapon so nothing of it can be taken, `reward.chain_corpse_seconds` = 300) carrying `gold_chain`. It appears at the kill, not after the body's 10 s, so the event ending (`ClearGangs`) cannot lose it.
- Client: a corpse of owner 60001 is drawn as an ordinary body (default look, a knife) and offers only its Item (`IsItemOnlyCorpse`: the Bigfoot's chooser path, "Tap E put on Gold chain"). `item.gold_chain` string.
- Neck slot: `EquipSlot.Neck = 1`, all item rules unchanged (they are per slot). `MaxCarried` is 8. Cosmetic: not `headshot_immune`, nothing hidden. Drawn by `CharacterView.Worn.cs` on the Neck bone (`BoneAttachment3D`, origin at the bone's bind origin, `NeckOffset` zero).
- Equipment screen: the slot cells are a column right of the figure (headgear, neck), the arrows walk it in slot order (`EquipmentNav` unchanged), Left from the bag's second row reaches the neck. Ghost = `Glyph.Kind.Neck` (a drawn necklace). Neck focus frames the head like headgear. No new text, no new colours (theme tokens only).
- Model: generated `gold_chain.glb` by `scripts/models/make_gold_chain_model.py` (the "simple generated fallback"; a private Gang Warfare model can replace it through `items.json` `model`). Icon `ui/icons/items/gold_chain.png` rendered offline by a throwaway software rasteriser (no Godot window); `tools/render_icons.gd` has the entry, so `make shooter-icons` re-renders it.

### Open
- UI standard DoD not complete: no screenshots at 1280x720 / 3840x2160 and no keyboard walkthrough (brief: nothing run). The new column is build-verified only. Check that the panel still fits at 1280x720 and that the avatar's drop target still reads well beside the column.
- Chain placement on the rig is a guess (`NeckOffset`, the model's ring 0.07 m under the Neck bone origin, front 0.15 m out): look at it on all four bodies, especially the Operator's hood and the soldiers' vests.
- Chain corpse looks like a default-look soldier with a knife lying where the boss body (10 s) lies. Ticket 08 may dress it as the boss. Not rewound, no marker.
- `gold_chain.png.import` is not committed: Godot writes it on the next import (commit it then).
- Equipment screen layout of the slot column and the ghost glyph shape were not seen.
