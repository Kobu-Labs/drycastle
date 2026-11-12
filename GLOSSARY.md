# Glossary

**Base map**:
The versioned, shared data artifact of the real village of Drycastle (terrain, roads, parcels, default buildings, landmarks, navigation data). Every mod consumes a specific version of it; it is not a game itself.
_Avoid_: world, level, scene

**Mod**:
A separate game built on the base map, with its own gameplay, database, and multiplayer server (e.g. a PvP shooter, a DayZ-style survival game, an MMO RPG).
_Avoid_: game mode, plugin

**Parcel**:
A property of the base map with a stable ID of our own (not the cadastral number): a cadastral parcel that holds a building, or one in a property group with a parcel that does. Its boundary is the cadastre's and fixed; its contents (building, props, events) can be replaced by a mod. Land that is no property is ground areas, not parcels.
_Avoid_: plot, lot, tile

**Map pipeline**:
The scripted, re-runnable process that generates the base map from source data with no manual map building.

**Parcel kind**:
The category of a parcel: building plot (it holds a building) or garden (the rest of a property).

**Ground area**:
A piece of the land outside the parcels with a ground type (field, forest, water, village, dike), cut from OSM land use and rules instead of cadastral outlines, or drawn by hand (a region with a ground type). The type picks its colour, vegetation and detail recipe. It has no fence and no stable ID.
_Avoid_: area (that is the map area: slice or village), zone, road parcel

**Ground type**:
The whole description of one kind of ground (forest, field, meadow, orchard, allotments, village, dike, water, ...): where it comes from (OSM land use or a rule), its terrain colours, detail recipe and settings, whether it is water and whether players spawn on it. Defined in `rules.yaml` (`ground.types`); you override, add and delete types in `edits.json` (`ground_types`) from the editor's Ground types tool or MCP. A change shows after the next full map build. Names are IDs and cannot be renamed.
_Avoid_: biome, terrain type

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
A killed player's body, left on the ground for a couple of minutes. Another player can walk over it to take its primary weapon in place of their own primary or secondary. A player killed by a blast or run over by a car leaves the corpse thrown: the server flies it under gravity until it lands, and clients tumble it meanwhile.
_Avoid_: ragdoll, body, drop

**Sandbox mode**:
A dev-only player state, asked for by the client (`--sandbox`) and granted by a server started with `--allow-sandbox`: godmode, unlimited ammo, every killstreak on demand. Its kills, XP and stats count for nothing.
_Avoid_: godmode (that is one part of it), debug mode

**Admin**:
A player whose player token is listed on the server (`--admin-token`). Admins may use chat commands; nobody else may. It is a server setting, not a player state: it is never granted by the client, and a guest (no token) is never an admin.
_Avoid_: operator, moderator, op

**Chat command**:
A chat line starting with "/", run by the server for an admin: `/bots` (how many bots play, live), `/spawn` (a vehicle in front of the admin), `/time` (the time of day, freeze or resume the clock), `/sandbox` (turn sandbox mode on or off for a player, by name or `#id`; only on a server started with `--allow-sandbox`), `/help`. The chat box completes them with Tab and lists the candidates and the usage above the input.
_Avoid_: console command, cheat

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

**Seat switch**:
Moving to another seat of the vehicle you sit in, without getting out: while seated, the number keys (the weapon slot keys) take that seat (key 2 is the second seat of the vehicle's `seats` list), and a small top-down schematic in the bottom right shows each seat with its number (free: outlined; yours: filled, double ring; taken: struck through). A vehicle with no `seats` list has the one driver seat. A free seat is taken at once; a taken one is refused. Leaving the driver seat leaves the vehicle without input, so it coasts to a stop (a utility helicopter in the air holds a hover, then sets itself down); a gunner's turret keeps its aim. One player can drive, go to the turret and drive again.
_Avoid_: seat change, swap seat

**Hotbar**:
The weapons a player can select: the primary, the secondary and the knife of the active loadout (keys 1-3 and the next-weapon cycle). A corpse swap changes its primary or secondary position. Smoke arenas and `--debug-cmds` servers hold every weapon.
_Avoid_: inventory, toolbar, slots

**Streak weapon**:
A weapon owned by a killstreak (the UAV's helper rounds, the attack missile, the turret, the MOAB, the aircraft guns). Kills made with one do not advance the Streak counter. The jeep's roof gun and the utility helicopter's door gun are not streak weapons.
_Avoid_: killstreak weapon (ambiguous with the streak itself)

**Pellet fan**:
The pellets of one shotgun shot, sent as a seed, an aim and a cone so every client rebuilds the same fan. Pellets are individual hitscan rays against a 0.35 m body. The cone follows the shot spread rule.
_Avoid_: spread (alone), buckshot

**Shot spread**:
The half-angle of the cone a shot can land in. It starts from the weapon's own spread (base plus growth per shot, or the pellet cone of a shotgun), grows with the shooter's speed up to sprint speed and is at its widest in the air, and is then reduced by crouch, prone and aiming. The server and the crosshair use the same rule, so the crosshair gap is the cone.
_Avoid_: accuracy, bloom, recoil (recoil moves the aim; spread scatters around it)

**Camouflage**:
A loadout's look (`camo.json`): the character (body model, skin tone, headgear), the suit's base colour and pattern, ghillie tufts and face paint. Chosen in the customiser and stored per loadout slot.
_Avoid_: outfit, cosmetic

**Shot-only cover**:
Collision that stops bullets, pellets, rockets and sight lines but not players or cars: tree crowns and bushes. Tree trunks and placed solids are solid for everyone.
_Avoid_: hitbox (for a prop), foliage collision

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
A killstreak's flying entity that circles the map at a set altitude and radius: the attack helicopter, the helicopter helper and the AC-130 gunship. Nobody boards or flies it; its owner only works its guns. It is replicated as one world entity type. It has hit points (600 for the helicopter, 1500 for the gunship) and can be shot down by flak: its streak then ends and the owner is released from the gun. The utility helicopter is not an aircraft: it is a vehicle.
_Avoid_: plane (for helicopters), drone, helicopter (alone)

**Utility helicopter**:
The vehicle a player flies: boarded, left and destroyed like any other vehicle, and flown freely by whoever sits in its pilot seat (the vehicle's driver seat). Its second seat is the door gunner's. Hands off it levels itself, stops and holds a hover; with nobody in the pilot seat it hovers briefly, then sets itself down.
_Avoid_: helicopter (alone), chopper, Huey, aircraft (that is the circling killstreak entity), flyable helicopter

**Door gun**:
The utility helicopter's machine gun, on a pintle in its right door and worked from the gunner seat. The gunner's view aims it inside an arc that keeps its rounds off the helicopter itself; its rounds are the jeep's roof gun's, and its kills are ordinary kills. The door gunner sits in the open, like the flak truck's gunner: a shot that reaches the body hits it, while the pilot stays behind the hull.
_Avoid_: turret (alone), minigun, side gun

**Flak truck**:
A heavy six-wheeled land vehicle with a driver seat and a gunner seat. The gunner's view aims a twin 35 mm turret (elevation 5 to 85 degrees, all the way round) that fires airburst shells at aircraft; the turret and the barrels turn on the model for everyone. It is weak against the ground (a shell that lands does 30 damage in a 4 m blast): it is there for aircraft. Its kills are ordinary kills, not the streak's, like the jeep's roof gun. It has 1200 hp. The gunner sits in an open turret: a shot that reaches his body hits him (not the hull around him) and can kill him, which frees the turret; the driver and a passenger stay behind the hull, as in every other vehicle.
_Avoid_: AA gun, anti-air vehicle, SPAAG

**Bus**:
A 12 m land vehicle modelled on the regional bus that serves the village: a driver seat and three passenger seats, no weapon, slow to turn and heavy to run into. It is boarded, left and destroyed like any other vehicle; its doors do not open, riders are placed on their seats.
_Avoid_: coach, autobus, transport (that is the amphibious one)

**Airburst**:
A flak shell's end by an aircraft: the shell flies straight at 700 m/s, and once it passes within 12 m of an aircraft it bursts at the closest approach. An aircraft within 15 m of the burst takes 150 damage out to 4 m, falling to 40 at the edge. The gunner has to lead the aircraft, which flies a known circle. A shell that meets no aircraft ends on the ground with a small blast (4 m, weak), or runs out of range.
_Avoid_: proximity fuse (the fuse is how it works, the burst is the thing), flak burst

**Sentry turret**:
The killstreak that places a 500 hp gun on the ground for 15 seconds. It turns to targets within its sight range and shoots them. It is removed when its owner dies or leaves the match, so it never fires or credits kills for an absent owner.
_Avoid_: turret (alone, ambiguous with the vehicle gun), gun emplacement

**UAV**:
The 4-kill killstreak: for 60 seconds the owner receives enemy pings on the minimap, one every second. Enemies with Ghost are not shown.
_Avoid_: drone, radar

**Attack missile**:
The 5-kill killstreak: a rocket dropped high above the owner that the owner steers from an infrared point of view. It can only dive (never level, never straight down), so it reaches the ground within about 7 to 13 seconds and detonates there.
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
**Map outline**:
The ground the village map covers: a square around the centre by default, or any polygon (`village.polygon`) of absolute S-JTSK points. Only what touches it is in the map; the terrain stops at it.
_Avoid_: border, boundary, area (an "area" is the slice or the village)

**Region**:
A hand-drawn shape of any form, independent of parcel boundaries, painted in one colour over the ground inside it. It changes only colour: parcels, roads, buildings and everything standing on the ground are unaffected.
_Avoid_: zone, area (an "area" is the slice or the village)

**Sketch**:
A named group (ROAD, FENCE_PLAN, ...) of the temporary marks a developer draws on the ground in the map editor to point at places in a prompt. Several sketches exist at once, each holding any number of **marks**: points (A, B, ...) and lines (L1, L2, ...; a closed line means the ground inside it). Mark names are unique across all sketches; a prompt can name one mark ("at A"), or a whole sketch ("everything in sketch ROAD"). The developer draws in the editor, and Claude draws through the MCP server (`sketch_add_*`, e.g. "highlight this road under sketch ROAD"). A sketch is never map content and is not a hand edit; it lives in `out/sketch.json` only.
_Avoid_: annotation, drawing, region (a region is saved and paints the ground), layer

**Building detail**:
A hand-written description of one real building's geometry beyond its OSM footprint (`pipeline/config/buildings/<OSM id>.yaml`): wall openings, roof overhang, read from reference photos. The shell stays the rule-generated one; the detail adds to it and never changes the footprint.

**Opening**:
A window, door or garage door in a wall of a building with a detail: a real hole in the wall with recessed reveals, located by footprint edge, distance along it, size and sill height. Each carries a confidence (low/medium/high) for how well the photos pin it down.

**Window pane**:
The glass of a window opening, exported as its own node `window_<building>_<id>` and listed in `windows.json` (position, size, facade, `breakable`), so the game can make it transparent or breakable. In the shooter it is a glass pane. Doors and garage doors are part of the building and have no pane node.

**Glass pane**:
A flat piece of breakable glass with hit points that the server owns: a window pane of a building (`windows.json`, unless its `breakable` is false) or a pane of a vehicle type (the `glass` list in `vehicles.json`). It is a rectangle, or, for the generated vehicles (bus, flak truck, helicopter), a convex outline polygon inside its bounding rectangle, and there it is also its own node `glass_N` in the model. Bullets and blasts take its hit points; below full it shows a crack (see Crack stage), at zero it is shattered. It has no collider of its own.
_Avoid_: window (for a vehicle's), glass (alone), windowpane

**Open side**:
A place where the body of a vehicle type has no wall and no glass (the `open_sides` list in `vehicles.json`): the two sides of the utility helicopter's cabin. A bullet gets into and out of the vehicle through it as through a glass pane; it has no hit points and nothing is drawn for it.
_Avoid_: opening (that is a hole in a building's wall), door (the helicopter's cockpit doors are solid with a glass pane)

**Crack stage**:
How far the crack of a standing glass pane has spread, from the fraction of its hit points left: 0 at full hit points, 1 as soon as it is damaged, one more at each fraction of `crack_stages` in `glass.json`.
_Avoid_: damage stage (that is a vehicle's smoke), crack level

**Shattered**:
The state of a glass pane with no hit points left: its fragments fly out once, and it then shows an opening (the model's own glass node of the pane is hidden). A bullet goes through a vehicle's pane whether it is shattered or not. It stays shattered until its vehicle returns repaired or a new round starts.
_Avoid_: broken, destroyed (that is a vehicle or an aircraft)

**Bunker**:
One of the village's three concrete WW2 fortifications: a building of the base map, and the only kind of building a player can go inside. It has a ground floor and three floors under it.
_Avoid_: pillbox, fortification

**Flood wall**:
The concrete wall between the river and the village, built from placed pieces (`prim:floodwall_*`): a ledge on the village side that players walk on, an upper wall with a railing behind it, access points and road gates. Its village side is the piece's `-y`.
_Avoid_: levee, dike, river wall

**Dike**:
The earth embankment north of the village, with a gravel track on top and thick grass on its sides; the cadastral parcel P-0073. Distinct from the flood wall, which is the concrete wall between the river and the village.
_Avoid_: levee, flood wall (that is the concrete wall)

**Ladder**:
How a player moves between the floors of a bunker: they get on with the interact key, climb with forward and back, and step off at either end. They cannot fire on it.
_Avoid_: stairs, climbing (that is the verb)

**Shelter**:
What a bunker's room gives the players in it: a blast outside the room does not hurt them, and a blast inside it hurts nobody outside. The MOAB is not stopped.
_Avoid_: blast cover, shot-only cover (that stops shots, not blasts), protection (that is spawn protection)

**Match mode**:
How a round is scored and won, set by `mode` in `match.json`: free-for-all (`ffa`, the default: every player alone) or team deathmatch (`tdm`).
_Avoid_: game mode, playlist

**Team**:
One of the two sides (A and B) of a team deathmatch round. A joining player goes to the smaller team, and at each round start the teams are evened out to differ by at most one. Teammates do not hurt each other unless `friendly_fire` is on. A team's score is the sum of its members' kills (team kills and suicides excluded); the team that reaches `team_score_goal`, or leads when time runs out, wins. Sandbox players belong to no team.
_Avoid_: squad, clan, faction

**Friend / enemy colours**:
The blue (friend) and orange (enemy) the HUD uses, seen from the local player's team, for names in the kill feed and scoreboard and for the minimap dots. A team tag ("A"/"B") always accompanies the colour, and both colours have a colour-blind set (`TeamPalette`).
