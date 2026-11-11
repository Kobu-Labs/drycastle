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

**Sandbox mode**:
A dev-only player state, asked for by the client (`--sandbox`) and granted by a server started with `--allow-sandbox`: godmode, unlimited ammo, every killstreak on demand. Its kills, XP and stats count for nothing.
_Avoid_: godmode (that is one part of it), debug mode

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

**Hotbar**:
The weapons a player can select: the primary, the secondary and the knife of the active loadout (keys 1-3 and the next-weapon cycle). A corpse swap changes its primary or secondary position. Smoke arenas and `--debug-cmds` servers hold every weapon.
_Avoid_: inventory, toolbar, slots

**Streak weapon**:
A weapon owned by a killstreak (the UAV's helper rounds, the attack missile, the turret, the MOAB, the aircraft guns). Kills made with one do not advance the Streak counter. The jeep's roof gun is not a streak weapon.
_Avoid_: killstreak weapon (ambiguous with the streak itself)

**Pellet fan**:
The pellets of one shotgun shot, sent as a seed and an aim so every client rebuilds the same spread. Pellets are individual hitscan rays against a 0.35 m body.
_Avoid_: spread (alone), buckshot

**Camouflage**:
A loadout's suit look and face paint (`camo.json`): a base colour, a pattern and ghillie tufts. Chosen in the customiser and stored per loadout slot.
_Avoid_: skin, outfit, cosmetic

**Concealment**:
The factor (stance, stillness, movement) by which camouflage reduces how far away a player can be spotted. Only the number exists; it is not yet applied in the server tick.
_Avoid_: stealth, invisibility

**Prone**:
The lowest stance: the player lies down and spreads less. Toggled by the prone key (a latch, not a hold).
_Avoid_: lay down, crawl

**Water body**:
A lake, pond or river area from the map pipeline (`water.json`), with a surface height. The shooter swims in it, and boats float on it.
_Avoid_: pond (alone), lake (alone), pool

**Breath**:
The air meter under water: it runs down while the player is submerged (shown as AIR on the HUD). Also the sniper's breath hold while scoped.
_Avoid_: oxygen, stamina

**Headlights**:
A vehicle's front and tail lights, from the optional `lights` block in `vehicles.json`. The driver toggles them with the headlights key (H).
_Avoid_: lamps (for vehicles), beams

**Day clock**:
The server's day/night clock: one 24-hour day of `day_length_minutes` real minutes, started at `start_time` (or `--time`), optionally frozen (`--freeze-time`). Clients get it in the welcome message.
_Avoid_: time of day (alone), game time

**Night factor**:
A value from the day clock that drives street lamp light, headlight and window glow.
_Avoid_: darkness, night mode

**Aircraft**:
A flying entity that circles the map at a set altitude and radius: the attack helicopter, the helicopter helper and the AC-130 gunship. It is replicated as one world entity type, and its guns are controlled by the owner.
_Avoid_: plane (for helicopters), drone

**Sentry turret**:
The killstreak that places a 500 hp gun on the ground for 15 seconds. It turns to targets within its sight range and shoots them.
_Avoid_: turret (alone, ambiguous with the vehicle gun), gun emplacement

**UAV**:
The 4-kill killstreak: for 60 seconds the owner receives enemy pings on the minimap, one every second. Enemies with Ghost are not shown.
_Avoid_: drone, radar

**Attack missile**:
The 5-kill killstreak: a steered rocket the owner flies from its point of view until it hits or its 20 seconds run out.
_Avoid_: rocket (alone), missile strike

**Helicopter helper**:
The 15-kill killstreak: an AI helicopter that fires short bursts at enemies, warning each target with a red dot about a second before.
_Avoid_: helicopter (alone), support

**MOAB**:
The 24-kill killstreak: a 5-second countdown, then a blast that kills everyone in range, ignoring spawn protection.
_Avoid_: nuke, bomb

**Smoke arena**:
A flat test map (far from the village) run by one of the `--*-smoke` flags for scripted bot checks. Dev only; not a game mode.
_Avoid_: test map, sandbox

**World entity**:
A non-player thing the server replicates on one channel: a corpse, a turret, an attack missile, a care package or an aircraft, identified by a type byte.
_Avoid_: object, prop, actor

**Protocol version**:
The number in the hello and welcome messages. A client and a server must match, or the server refuses the client with both numbers in the message.
_Avoid_: build number, version (alone)
