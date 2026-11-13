# The bag holds six items, a corpse holds them all, and a dropped item lies on the ground

Decided for the Equipment screen work (issue #267, after #261). **Amends** ADR 0016, which had one item per corpse and no place to carry an item unworn; the rest of 0016 stands (match state, never the profile; the pickup count belongs to the item; the headshot rule).

**The bag.** A player carries up to six **Items** without wearing them in a **Bag** (`Bag`, `godot/ShooterCore/Items/Bag.cs`): six cells, each empty or one `ItemStack` (item and pickup count). Like `Equipment` it is match state in the server's memory with the player's session and never reaches the profile (ADR 0003 stays the profile's only store). A new round keeps it, leaving the match loses it. Moves between the bag and a worn slot are **free**: no pickup is counted. Equipping an item whose slot is taken swaps the worn item into the cell the new one left, so a full bag never refuses it; taking an item off needs an empty cell, and with none the server refuses it and tells the player ("Your bag is full").

**Death.** Everything worn and bagged goes onto the corpse, worn first and then the bag in cell order. A player carries at most one worn item per slot plus six bagged ones, so a corpse holds up to `ItemRules.MaxCarried` = 7 items, the headgear slot being the only one today. `Corpse.Items` replaces the single `Corpse.Item`.

**Taking from a corpse.** The taker names the item by its position in the corpse's list (`PickUpCorpseItem` carries the index). It goes into its slot when that is empty, else into the first empty bag cell; with the slot taken and the bag full it is **swapped**, as before: it is worn and the item that was worn takes its place in the corpse's list. Each take counts one pickup (`ItemRules.Take`), the swap counts none. The pickup limit and the crumbling are unchanged: an item taken as often as it may be is gone from the corpse, and the taker reads that it crumbles.

**Ground items.** A player can drop a worn or bagged item. It lands on the ground at their feet, with no throw, at the terrain height under them (the server casts a ray down and keeps the height it started from when nothing is below). A player in a vehicle, in any seat and in a helicopter in the air as well, drops it on the ground under the vehicle. Anyone can take it with the interact key, the dropper too, and taking it counts one pickup. It goes into its slot, else the bag; with both full **nothing is taken and nothing is counted**: the corpse's swap has a corpse to leave the displaced item on, and a ground item has none, and counting a refused attempt (or letting it crumble) would punish a player who could not have taken it anyway. A ground item expires on the corpse timer (`CorpseRules.LifetimeSeconds`) and shares the corpse cap (`CorpseRules.MaxCorpses`) in `CorpseBook`: when the map is full the oldest thing that carries no item goes first (a corpse with none), then the oldest of those that do, a ground item or a corpse alike. A round end clears them with every other world entity.

**On the wire (protocol 31).**
- A ground item is a **world entity** (type 2, `GroundItem`), not a pair of events: it then spawns, despawns and reaches a joiner with everything else on the world channel, with no catch-up code of its own. ADR 0016 kept a corpse's item out of the corpse's world state because ADR 0002's codec is shared by every entity type; a new type pays nothing for its own use of the fields, so here the item is the state byte (the items.json position, as the item is named everywhere) and the pickup count is the hp byte (clamped to 255 on the wire).
- A corpse's items stay in a reliable event (`CorpseItems`, 191: u16 corpse, u8 count, then item and pickups per entry), sent when the corpse spawns with items, when its list changes (an empty list says the last is gone) and to a joiner. It replaces `CorpseItem` and carries the pickup counts.
- What the owner wears and carries is one event (`Equipment`, 192: worn slots, then bag cells, item and pickups each), so a swap between the two is never seen half done. The pickup count now travels to the owner (the screens show it); other players never see it.
- The wishes of the client are `PickUpCorpseItem` (190, with the index), `EquipItem` (200, a cell), `UnequipItem` (201, a slot), `DropItem` (202, worn slot or cell) and `TakeGroundItem` (203, a world entity id). The server checks every one (alive, in reach for a take, the cell or slot exists) and answers with the new `Equipment` event or an `ItemNotice` (`Crumbled`, `BagFull`). A client never changes its own state before the answer.
- `/give` puts the item in its slot, else the bag, and refuses with both full.

## Considered

- **A ground item as two reliable events (spawn, despawn) with a catch-up for the joiner.** Rejected: the world channel already has ids, expiry, the round-end clear, the entity cap and the joiner's full set; the events would copy each of them.
- **Swapping when both are full for a ground item too, the displaced item dropping where the taken one lay.** Rejected: it makes the take an exchange the player did not ask to make and puts a second entity in the spawn path of a pickup; a player who wants it drops one item first.
- **Counting a pickup on equip, unequip and drop.** Rejected: the limit is meant to bound how often an item changes hands (ADR 0016), and moving it between a bag and a slot of the same player is not a hand-over.
- **A pickup count per corpse list entry that resets at death.** Rejected for the reason in ADR 0016: the limit would never be reached.
- **A bag that outlives the match (in the profile).** Rejected as in 0016: event rewards must not outlive their event.

## Consequences

- The Equipment screen and the corpse chooser read their state from `NetClient.Items.cs` (`WornItems`, `BagContents`, `CorpseItems(id)`, `ItemsChanged`, `CorpseItemsChanged`) and send wishes with `RequestEquip`, `RequestUnequip`, `RequestDropWorn`, `RequestDropBagged`, `RequestTakeGroundItem` and `RequestTakeCorpseItem`. The existing single item row of the chooser takes index 0.
- A corpse can now carry an event reward and six other items, and the cap protects every carrier alike: a busy fight can still remove a carrier once only carriers are left, the oldest first.
- Protocol 31: a client of 30 or older is refused at the hello.
