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
A piece of the land outside the parcels with a ground type (field, forest, water, village, dike), cut from OSM land use and rules instead of cadastral outlines, or drawn by hand (a region with a ground stack). It has the terrain of exactly one ground type, which picks its colour, vegetation and detail recipe, and it can lie under attribute layers. It has no fence and no stable ID.
_Avoid_: area (that is the map area: slice or village), zone, road parcel

**Ground type**:
A named bundle of attributes: the whole description of one kind of ground (forest, field, meadow, orchard, allotments, village, dike, water, ...) or of a layer over ground (a Bigfoot zone). It says where it comes from (OSM land use or a rule, or only drawn by hand) and ticks the attributes it carries. Defined in `rules.yaml` (`ground.types`); you override, add and delete types in `edits.json` (`ground_types`) from the editor's Ground types tool or MCP. A change shows after the next full map build. Names are IDs and cannot be renamed.
_Avoid_: biome, terrain type

**Ground attribute**:
One thing a ground type can carry, from a fixed list (each needs something in the pipeline or the game to use it), grouped by relation: Appearance (terrain colours, detail recipe and settings), Spawning (players may spawn), Water (a body of water), Events (which events may spawn there, first Bigfoot). Appearance and Water decide the terrain: one type does, per point. Spawning and Events add up over every type that lies there.
_Avoid_: property, flag, tag

**Ground stack**:
The ground types of one hand-drawn region, lowest first; between regions the higher ID is on top. The topmost type with appearance or water attributes decides the terrain of the area, exactly as a single type would; the attributes that add up are taken from every type in the stack. A stack of only such types is an **attribute layer**: it changes nothing about how the ground looks, and the editor draws it as a translucent overlay.
_Avoid_: layer stack, z-order

**Replacement scene**:
What a mod supplies to replace a parcel's base contents.

**Property group**:
Several parcels that belong to one owner and are treated as one property, sharing a fence around their combined outer edge. Parcel IDs and boundaries do not change; a parcel is in at most one group.
_Avoid_: merged parcel, estate

**Hand edit**:
A deliberate, developer-made change to a parcel's or property group's fence, or a placed object, that survives every pipeline re-run.

**Placed object**:
A car, pole, wall piece or other asset put on the map by hand, at an absolute position, listed in the top-level `objects` of `edits.json`. It belongs to no parcel: a pole beside a road stands on ground, which is no parcel, as well as a car in a yard. The parcel it stands in is derived from its position and only names its group node in the game.
_Avoid_: parcel object, prop (a prop is generated: a fence or a tree)

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
A killed player's body, left on the ground for a couple of minutes. Another player can walk over it to take its primary weapon in place of their own primary or secondary. A player killed by a blast or run over by a car leaves the corpse thrown: the server flies it under gravity until it lands, and clients tumble it meanwhile. It also carries every Item its player wore or carried in the Bag (up to seven), which another player can take one at a time (see Pickup limit).
_Avoid_: ragdoll, body, drop

**Sandbox mode**:
A dev-only player state, asked for by the client (`--sandbox`) and granted by a server started with `--allow-sandbox`: godmode, unlimited ammo, every killstreak on demand. Its kills, XP and stats count for nothing.
_Avoid_: godmode (that is one part of it), debug mode

**Admin**:
A player whose player token is listed on the server (`--admin-token`). Admins may use chat commands; nobody else may. It is a server setting, not a player state: it is never granted by the client, and a guest (no token) is never an admin.
_Avoid_: operator, moderator, op

**Chat command**:
A chat line starting with "/", run by the server for an admin: `/bots` (how many bots play, live), `/spawn` (a vehicle in front of the admin), `/time` (the time of day, freeze or resume the clock), `/restart` (a new round now, optionally in the other match mode), `/sandbox` (turn sandbox mode on or off for a player, by name or `#id`; only on a server started with `--allow-sandbox`), `/give` (give an Item to a player, or to yourself: worn if the slot is empty, else in the Bag), `/help`. The chat box completes them with Tab and lists the candidates and the usage above the input.
_Avoid_: console command, cheat

**Care package**:
The killstreak that gives its user a random Perk; better perks are rarer.
_Avoid_: supply drop, crate

**Weapon category**:
A class of primary weapon (machine guns, assault rifles, shotguns, SMGs, sniper rifles) that shares a base recoil pattern; individual weapons may override it. Pistols form the secondary category.
_Avoid_: weapon type, class

**Amphibious transport**:
An open three-seat vehicle, a boat-shaped hull on four wheels: a driver, an armed seat beside them and a ring gun over the rear. On land it drives (and runs people over there); in a water body it floats and drives like a boat (a lazy helm, a stern that swings out), slower. Nothing covers its riders: all three sit in exposed seats.
_Avoid_: APC, amphib, Schwimmwagen, duck

**Predicted vehicle**:
A vehicle whose driver's client steps its own copy with the driver's inputs, so the driver sees it in the present rather than about 100 ms late: the utility helicopter (ADR 0006), the amphibious transport (ADR 0010) and the car (ADR 0014). Everyone else, the riders included, sees a predicted vehicle drawn from the snapshots; the other ground vehicles are drawn from the snapshots for the driver too.
_Avoid_: client-side vehicle, smooth vehicle

**Armed seat**:
A passenger seat whose rider keeps their own loadout and fights with it from the seat, in first person, aiming with the view inside the seat's arc. The mouse wheel changes the weapon there, since the number keys are the seat switch. The amphibious transport's front right seat is one; the bus's passenger seats are not.
_Avoid_: shotgun seat, gunner seat (that seat works the vehicle's own gun), fighting seat

**Exposed seat**:
A seat the hull does not cover: a shot that reaches its rider's body hits the rider and not the hull, and a shot that misses them hits the hull. The flak truck's and the utility helicopter's gunner seats are exposed, and all three seats of the amphibious transport. Riders of one vehicle never hit each other.
_Avoid_: open seat, unprotected seat

**Ring gun**:
The amphibious transport's machine gun, on a ring mount over the rear compartment and worked from the gunner seat by a gunner who stands inside the ring. The gunner's view aims it all the way round; over the two riders in front it cannot be depressed. Its rounds are the jeep's roof gun's, and its kills are ordinary kills.
_Avoid_: turret (alone), pintle gun, rear gun

**Seat switch**:
Moving to another seat of the vehicle you sit in, without getting out: while seated, the number keys (the weapon slot keys) take that seat (key 2 is the second seat of the vehicle's `seats` list), and a small top-down schematic in the bottom right shows each seat with its number (free: outlined; yours: filled, double ring; taken: struck through). A vehicle with no `seats` list has the one driver seat. A free seat is taken at once; a taken one is refused. Leaving the driver seat leaves the vehicle without input, so it coasts to a stop (a utility helicopter in the air holds a hover, then sets itself down); a gunner's turret keeps its aim. One player can drive, go to the turret and drive again.
_Avoid_: seat change, swap seat

**Hotbar**:
The weapons a player can select: the primary, the secondary and the knife of the active loadout (keys 1-3 and the next-weapon cycle). A corpse swap changes its primary or secondary position. Smoke arenas and `--debug-cmds` servers hold every weapon.
_Avoid_: inventory, toolbar, slots

**Item**:
Something a player wears that is not a weapon, perk or killstreak, defined in `items.json`. The first Items are event rewards (the Bigfoot head). A player wears at most one Item per equipment slot and carries the rest in the Bag.
_Avoid_: loot, gear, drop (as a noun)

**Equipment**:
What a player wears right now: one Item per equipment slot, of which the headgear slot is the only one. It belongs to the match and not to the Profile: death puts the worn Items on the Corpse, leaving the match loses it, and a new round keeps it. What is carried unworn is the Bag.
_Avoid_: inventory (the weapons are the Hotbar), outfit (that is Camouflage)

**Bag**:
Six cells that hold Items a player carries without wearing them. Match state like Equipment, never in the Profile. Moving an Item between the Bag and an equipment slot is free (no pickup is counted): equipping into a taken slot swaps the worn Item into the Bag, and taking an Item off with a full Bag is refused. Death puts everything in it on the Corpse; leaving the match loses it, a new round keeps it.
_Avoid_: inventory, backpack, stash

**Ground item**:
An Item lying on the ground after a player dropped it from their Equipment or Bag. It lands at their feet with no throw, or on the ground under their vehicle (any seat, a helicopter in the air included). Anyone, the dropper too, takes it with the interact key; each take counts one pickup, and with the slot and the Bag full nothing is taken. It expires on the Corpse timer and shares the Corpse cap, which removes things that carry no Item first.
_Avoid_: loot, drop (as a noun), pickup (that is the act)

**Headgear slot**:
The equipment slot on the head: an Item in it is worn instead of a helmet. Not the Camouflage's headgear, which is only the look of the helmet; the slot is gameplay.
_Avoid_: helmet slot, hat slot

**Bigfoot head**:
The first Item: headgear, the reward for defeating the Bigfoot event. A headshot on its wearer is a body hit: the weapon's body damage, with no headshot multiplier, marker or bonus XP. It has a Pickup limit of three.
_Avoid_: Bigfoot mask, Bigfoot helmet

**Pickup limit**:
How many times an Item can be taken from a Corpse or the ground over its whole life. Moving it between the Bag and an equipment slot or dropping it counts nothing. The count belongs to the Item and travels with it from corpse to wearer or Bag to corpse or ground; the attempt after the last pickup makes it crumble (it is gone, and the player reads "The item crumbles in your hands"). An Item with no limit never crumbles.
_Avoid_: use count, durability, charges

**Streak weapon**:
A weapon owned by a killstreak (the UAV's helper rounds, the attack missile, the turret, the MOAB, the aircraft guns). Kills made with one do not advance the Streak counter. The jeep's roof gun, the utility helicopter's door gun and the amphibious transport's ring gun are not streak weapons.
_Avoid_: killstreak weapon (ambiguous with the streak itself)

**Pellet fan**:
The pellets of one shotgun shot, sent as a seed, an aim and a cone so every client rebuilds the same fan. Pellets are individual hitscan rays against a 0.35 m body. The cone follows the shot spread rule.
_Avoid_: spread (alone), buckshot

**Shot spread**:
The half-angle of the cone a shot can land in. It starts from the weapon's own spread (base plus growth per shot, or the pellet cone of a shotgun), grows with the shooter's speed up to sprint speed and is at its widest in the air, and is then reduced by crouch, prone and aiming. From an armed seat the vehicle's speed stands in for the shooter's own, and the seat gives no stance bonus. The server and the crosshair use the same rule, so the crosshair gap is the cone.
_Avoid_: accuracy, bloom, recoil (recoil moves the aim; spread scatters around it)

**Camouflage**:
A loadout's look (`camo.json`): the character (body model, skin tone, headgear: the helmet's look, not the Headgear slot), the suit's base colour and pattern, ghillie tufts and face paint. Chosen in the customiser and stored per loadout slot.
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
A lake, pond or river area from the map pipeline (`water.json`), with a surface height. The shooter swims in it, and the amphibious transport floats on it.
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
A heavy six-wheeled land vehicle with a driver seat and a gunner seat. The gunner's view aims a twin 35 mm turret (elevation 5 to 85 degrees, all the way round) that fires airburst shells at aircraft; the turret and the barrels turn on the model for everyone. It is weak against the ground (a shell that lands does 30 damage in a 4 m blast): it is there for aircraft. Its kills are ordinary kills, not the streak's, like the jeep's roof gun. It has 1200 hp. The gunner sits in an open turret: a shot that reaches his body hits him (not the hull around him) and can kill him, which frees the turret; the driver and a passenger stay behind the hull, as in every closed vehicle.
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

**Gunfire blip**:
A short-lived mark on the minimap and the tactical map at the spot an enemy fired from, shown when the shot is within 150 m of the local player (match.json `gunfire_blip_radius`), for 1.5 seconds with a fade, at most one per shooter every 0.25 seconds. Only enemies' gun shots blip (in team deathmatch teammates never do, in free-for-all everyone else does); the knife, streak weapons and vehicle guns do not, and Ghost does not hide it. Shown out of sight too. It is a hollow diamond, not the UAV's filled ping.
_Avoid_: radar ping, UAV ping, shot marker

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
A non-player thing the server replicates on one channel: a corpse, a ground item, a turret, an attack missile, a care package or an aircraft, identified by a type byte.
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
One of the village's three concrete WW2 fortifications: a building of the base map, and the only kind of building a player can go inside. It has a ground floor and three floors under it; the lowest is its command post, and in the **Base** that is the **Intel room**.
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
How a round is scored and won, set by `mode` in `match.json` at server start, or by an admin's `/restart`: free-for-all (`ffa`, the default: every player alone) or team deathmatch (`tdm`).
_Avoid_: game mode, playlist

**Team**:
One of the two sides (A and B) of a team deathmatch round. A joining player goes to the smaller team, and at each round start the teams are evened out to differ by at most one. Teammates do not hurt each other unless `friendly_fire` is on. A team's score is the sum of its members' kills (team kills and suicides excluded); the team that reaches `team_score_goal`, or leads when time runs out, wins. Sandbox players belong to no team.
_Avoid_: squad, clan, faction

**Friend / enemy colours**:
The blue (friend) and orange (enemy) the HUD uses, seen from the local player's team, for names in the kill feed and scoreboard and for the minimap dots. A team tag ("A"/"B") always accompanies the colour, and both colours have a colour-blind set (`TeamPalette`).

**Tactical map**:
The full-screen, north-up map sheet of the whole base map that the M key (rebindable, `map`) opens and closes; Esc also closes it. It is view only and does not pause anything. Terrain, parcels and roads come from the same data as the minimap; a square grid labels columns A, B, C... from the west and rows 1, 2, 3... from the south, so a square has a short reference such as "C7" (the cell is 50, 100 or 200 m, chosen so there are about 10 to 20 a side; these are not S-JTSK coordinates). The margin shows a compass rose, the player's heading in degrees, the grid reference and position in metres from the south-west corner, a scale bar and a legend. Markers (the player's arrow, vehicles, friends in team deathmatch, UAV pings, gunfire blips) are the minimap's, drawn from the same data. It closes on the respawn screen, at the end of a match and in a missile or turret view.
_Avoid_: world map, big map, full map

**Animal**:
A creature of the world that is not a player: a deer, a dog, a cow. It has hit points and can be shot dead (`animals.json` lists the species, their hit points, speeds and models). It stands, eats, wanders around the spot it spawned at, and runs from players, vehicles and the shot that hurt it. A killed animal lies for `corpse_seconds` with its death clip held on the last frame, then is removed. It is a world entity (wire type 50) the server owns; clients only draw it. Not a target dummy (a player-shaped practice target) and not a corpse (what a killed player leaves).
_Avoid_: NPC (alone), mob, critter

**Citizen**:
A villager of the Holy Mass: leaves a house, walks the navigation mesh to the church door, follows the church's scripted route to its seat and sits. Any hit kills it in one (it lies for `corpse_seconds`, nothing replaces it); it never reacts to combat, and bots do not see it. The server tells who killed it (the event's sinner rule). A world entity (wire type 51) with a pure brain; `holy_mass.json` holds its numbers and bodies, `church.json` (door, aisle, seats) its destination. Not a bot (bots are players with loadouts), not an Animal, not a target dummy.
_Avoid_: NPC (alone), villager, pedestrian

**Rozhlas**:
The village loudspeaker broadcast: a song, the announcement, "OPAKUJEM" (I repeat), then the announcement again, all through the same bad-loudspeaker sound, heard across the village as one shared recording, not one per pole. Baked from clean recordings by `scripts/make_rozhlas.py`; on the air at a clock-derived moment, so every client hears the same thing. Louder and clearer the closer the player is to the nearest megaphone pole, quieter and duller the further; poles next to each other do not add up. An **Event announcement** also starts it, and so does an admin's `/event rozhlas`.
_Avoid_: PA system, radio, announcer

**Base**:
The one bunker, named in `intel.json`, that players are told to report to when something is happening. It is an ordinary bunker; what makes it the base is that its command post is the intel room.
_Avoid_: HQ, headquarters, camp, outpost

**Intel room**:
The command post (the floor three levels down) of the base, with the intel screen on one wall. The pipeline reserves that wall and keeps furniture off it (`intel` in `bunkers.json`). It is reached like any bunker floor, by the ladders.
_Avoid_: briefing room, war room, control room

**Intel screen**:
The big military-styled wall screen in the intel room. For each announced event it shows what it is, when it begins (a countdown, then "under way"), the risks, where (a grid reference and a map of the surroundings; none for an event with no place) and an emblem of its kind. With several events it shows one at a time. With none it says all is quiet. An admin's test event carries a "not a real event" tag. It is a client-only texture; nothing on it can be used or shot.
_Avoid_: terminal, monitor, TV

**Intel board**:
The server's list of announced events (each: name, kind, start time, description, risks, an optional place), sent whole to every client at each change and to a joiner. The intel screen draws it. An event is on it from its announcement until it is over. This is the one thing an event gives the announcement; the event itself is none of its business.
_Avoid_: event list, schedule

**Event announcement**:
What tells players that something was detected: a toast and a chat line ("Suspicious activity detected. Report to the base for more information.", with the base's grid reference when the map has a sheet) and the **Rozhlas** broadcast. It never says what the event is: that is for the intel screen.
_Avoid_: alert, notification, warning (the HUD's helicopter warning is another thing)
