# Glossary

**Base map**:
The versioned, shared data artifact of the real village of Suchohrad (terrain, roads, parcels, default buildings, landmarks, navigation data). Every mod consumes a specific version of it; it is not a game itself.
_Avoid_: world, level, scene

**Mod**:
A separate game built on the base map, with its own gameplay, database, and multiplayer server (e.g. a PvP shooter, a DayZ-style survival game, an MMO RPG).
_Avoid_: game mode, plugin

**Parcel**:
A land unit of the base map with a stable ID of our own (not the cadastral number), covering every cadastral parcel including roads and fields. Its boundary is fixed; its contents (building, props, events) can be replaced by a mod.
_Avoid_: plot, lot, tile

**Map pipeline**:
The scripted, re-runnable process that generates the base map from source data with no manual map building.

**Parcel kind**:
The category of a parcel (building plot, garden, road, field, forest, water, other), assigned by rules.

**Replacement scene**:
What a mod supplies to replace a parcel's base contents.

**Property group**:
Several parcels that belong to one owner and are treated as one property, sharing a fence around their combined outer edge. Parcel IDs and boundaries do not change; a parcel is in at most one group.
_Avoid_: merged parcel, estate

**Hand edit**:
A deliberate, developer-made change to a parcel's or property group's contents (fence, trees, placed objects) that survives every pipeline re-run.

**Loadout**:
One of a player's five saved sets: a primary weapon, a secondary weapon, a perk and three killstreaks. Edited in game and switched on death.
_Avoid_: build, class, kit

**Perk**:
A passive modifier a loadout carries (e.g. Fleet Foot, Quick Hands, Flak Jacket, Swift Swap, Tank Hunter, Hot Streak, Steady Lung, Ghost). One perk per loadout.
_Avoid_: ability, skill, upgrade

**Killstreak**:
A reward a loadout selects (up to three), unlocked by the Streak counter reaching a kill threshold and usable until the player dies. Examples: UAV, turret, care package, attack helicopter, AC-130, MOAB.
_Avoid_: streak (alone), ultimate, power-up

**Streak counter**:
The number of kills a player has made since their last death; it decides which killstreaks are available. Only kills count, and it resets on death. Kills made by a killstreak's own weapons do not advance it.
_Avoid_: kill count, combo

**Player token**:
The client-generated GUID that is the player's identity in v2. Stored in the client config file and mapped to a Profile on the server. Not a password: whoever holds it is that player.
_Avoid_: account, login, user id

**Profile**:
A player's persistent server-side record, keyed by player token: display name, XP, Level, Unlocks and Loadouts.
_Avoid_: account, save, player data

**XP / Level**:
Experience points earned from play; a Level is reached at each XP threshold. Reaching a Level unlocks weapons, perks and killstreaks.
_Avoid_: rank, prestige

**Unlock**:
A weapon, perk or killstreak that a player's Level makes available to their Loadouts.
_Avoid_: reward, achievement

**Street lamp**:
A lamp post of the base map, placed from OSM street lamp nodes or along roads by the pipeline (`lamps.json`). In the shooter it is solid and lights up at night (see the night factor); the viewer does not show it.
_Avoid_: light, streetlight, lamp post

**Corpse**:
A killed player's body, left on the ground for a couple of minutes. Another player can walk over it to take its primary weapon in place of their own primary or secondary.
_Avoid_: ragdoll, body, drop

**Care package**:
The killstreak that gives its user a random Perk; better perks are rarer.
_Avoid_: supply drop, crate

**Weapon category**:
A class of primary weapon (machine guns, assault rifles, shotguns, SMGs, sniper rifles) that shares a base recoil pattern; individual weapons may override it. Pistols form the secondary category.
_Avoid_: weapon type, class

**Boat**:
A water-only vehicle: it floats on a water body, has no drive on land (it coasts to a stop), and never runs anyone over.
_Avoid_: ship, watercraft

**Amphibious transport**:
A four-seat vehicle that drives on land like the war jeep (and runs people over there) and floats at reduced speed on water.
_Avoid_: APC, amphib
