# Worn items are match state, not profile; an item's pickup count is the item's

Event rewards (the Bigfoot head, issue #261) can be worn and dropped, which the shooter had no place for. We decided that **Equipment** (what a player wears, one item per slot, `items.json`) lives in the server's memory with the player's session and never reaches the profile: ADR 0003's store keeps only display name, XP, level, unlocks and loadouts, and an event reward must not outlive its event. A death takes the item off onto the corpse, leaving the match loses it, a new round keeps it, and a corpse that expires (or is evicted by the corpse cap) takes it along; the cap evicts corpses without an item first so a busy fight does not wipe a reward.

The **pickup count** belongs to the item, not to the corpse or the player. A dropped item keeps it, so "at most three pickups" holds over the item's whole life however many times it changes hands, and the attempt after the last makes it crumble. The limit is a field of the item definition (`pickup_limit`), absent for no limit: event items set it, other items need not.

A headshot on a wearer of a `headshot_immune` item is resolved as a **body hit** (the weapon's body damage, no marker, no bonus XP), not as no damage: `HitTarget.HeadGuarded` makes `HitMath.Raycast` report it, so every hitscan and pellet path agrees. What a player wears is not rewound by the lag compensation of ADR 0013 (only where they stood is): the shooter's screen cannot show it either way, and the present state is the one the server knows.

An item is announced by its own reliable events (a corpse's item, the wearer's equipment, a notice) and not in the corpse's world state (ADR 0002's world codec is shared by every entity type; a byte for one type's item is not worth a layout change for all). A joiner is told the items of the corpses already on the map. What a player wears is public: every client draws it on that character, so the wearer's equipment is also sent to everyone else (event 194, `WornItems`) and to a joiner for each player already wearing something; the pickup count stays on the server.

## Considered

- **Persisting worn items in the profile.** Rejected: nothing else in the profile is event state, and a persistent Bigfoot head defeats the three-pickups rule.
- **Headshots deal no damage.** Rejected as the simplest reading of "prevents headshot damage": a wearer would take no damage from the most common aimed hit. Body damage removes the bonus and keeps the wearer killable. Reversible in `HitMath.Raycast` if the event wants it.
- **The count on the corpse.** Rejected: the item would reset on each new corpse and the limit would never be reached.
