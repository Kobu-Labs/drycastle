# Cartel (#437)

Design grilled with the owner on 2026-11-27. Related: #366 (gang at the marihuana patch), #398 (bus jobs), #436 (ambulance jobs), #358 (bots in vehicles). Nothing here is built.

## Terms (in `GLOSSARY.md`)

- **Cartel**: the drug cartel a player can work for. Opt-in.
- **Cartel rank**: a player's position, a personal ladder: Runner, Dealer, Sicario, Capo, Patrón (names, curve in `cartel.json`).
- **Respect**: the cartel's own XP; the only thing a job pays.
- **Contact**: the cartel NPC at the plantation; where you join, quit and start jobs.
- **Job**: one task from a giver, with an objective, a reward and a failure. The generic core; the cartel is its first job line (#398 and #436 reuse it later). Not an Event.
- **Heist**: a Payday-like Job: bag the goods, load the van, drive it to a drop point, under Assaults.
- **Duffel**: a carried bag of goods (weed, cash, product). Not the Bag (the six-cell inventory) and not Loot (the Thieves' stolen things).
- **Cartel van**: the drivable getaway van of a heist.
- **Drop point**: where a delivery or a loaded van is handed over (zone kind `cartel_drop`).
- **Rival crew**: the gang that sends the Assaults and holds trap houses.
- **Assault**: one rival van that comes in to attack a heist's objective.
- **Gunman**: a new Gang member kind that fights at range.
- **Trap house**: a hollow house a rival crew holds for a Trap house heist.
- **Rival dealer**: the target of a Hit.

## Membership and rank

- Opt-in at the Contact. Quitting is allowed; rank and Respect stay frozen until a rejoin.
- Rank and Respect persist on the Profile (ADR 0003 store, a migration in both SQLite and Postgres).
- Bots never join. A guest (no stored Profile) cannot join. A sandbox player can run jobs and earns nothing.
- Rank-up: a toast to everyone (no chat line: only players' messages are broadcast, #499).
- Rank unlocks: postponed (see v2).

## Who runs a job

- **ffa**: solo only. Squads stay team-deathmatch only.
- **tdm**: the squad. Squads gain a **leader**: the creator; when the leader leaves, the longest-standing member takes over, and the running job goes with them. The leader is marked in the Squads screen and on the scoreboard. A player in no squad runs jobs alone.
- Only the leader starts a job. The reward goes to every squad member at the moment of success, wherever they are (a member who joined midway gets it, one who left does not).
- A squad mate who is not in the cartel shares the objective and can help, earns nothing, and gets the hint "Join the cartel at the Contact" (a guest, who cannot join, reads why instead: no saved profile). Joining midway turns the hint off at once (the Job message goes again when a member's flags change).
- A player is in one party at most: a solo player who joins a squad leaves their solo job (it fails, nobody left to run it).
- One running job per squad (or solo player). Many jobs can run at once, beside any Event. Two jobs never share a drop point, a target spot or a house.
- Only the job's players see its markers and tracker. Everyone sees the duffels on carriers' bodies. Outsiders can interfere by shooting; they get nothing special.

## Difficulty and reward

- **Tier**: the Contact lists jobs in tiers; the leader's rank unlocks the higher ones. Higher tier: more guards, tighter timers, more Respect.
- **Squad size** scales enemy counts, duffel counts and so on (everyone gets the full reward).
- Reward depends on tier only. Respect only: no global XP, and no XP for killing job NPCs (no `GangKill`).
- Heists: base Respect plus a bonus per loaded duffel above the minimum, times the tier. A failed job pays nothing.

## Failure

- A job fails when its timer runs out (a safety for heists, the pressure for simple jobs), when the round ends (at the end screen, not at the next round) or resets, or when the leader quits the cartel.
- Death fails it: solo, the player's death; in a squad, the **leader's** death (other members respawn and carry on; a carried duffel drops where they fell).
- Heists also fail when the Cartel van is destroyed.
- No Respect penalty, no cooldown. A job's NPCs, duffels and van are removed when it ends.
- An admin's `/cartel end fail` fails it too ("Ended by an admin", `JobFailReason.Admin`): a test, see "Testing".

## Contact

- A placed object (position and facing) set by hand in the map editor near the plantation. A map without one has no cartel, with no error.
- Cannot be hurt: no weapon is tested against him (as a Companion).
- Stands alone in v1. #366 later puts the patch gang round him as guards: they never start a fight and fight back only at whoever attacks them (the Gang member rule), member or not.
- E opens a non-pausing panel (as the Hunter's Log): join / quit, rank and Respect bar, the job list by tier (locked tiers show the rank they need), Start (leader only; others see "Your leader starts jobs"). UI follows `docs/standards/ui-ux.md` and the `ui-work` skill.
- During a job: a HUD tracker (objective line, timer) and a marker on the minimap and in the world.
- Built (#494, round 2): the server sends a Job's party its objective line and marker (`IJobObjective.Objective` / `Marker`) as event 37 `CartelObjective` when either changes (the marker when it moved `marker_resend_m`) and again to all after a party change. The Contact's key is `KeyCompetitor.Contact` (his reach keeps the plantation's hold off it); the client gives him the key after every other prompt (`CartelClient`), and his panel is `Cartel/ContactPanel.cs` over `ContactPanelModel` (ShooterCore). The tracker is `CartelTracker` (machine `Cartel.Tracker`), drawn by `HeadsUp/Prompts/JobTracker.cs`; the marker is `MapMarkers.Job` (a star, `MapGlyphs.Job`, `Maps/JobMarker.cs`). A rank-up (event 36) is a toast for everyone. Not screenshot-checked yet.
- Built (review fixes, protocol 91): who is told what each tick is `ShooterCore/Jobs/JobFeed` (unit-tested), membership and Respect `CartelMembers`, the Contact's requests `CartelServer`. A refused join, quit or start answers the panel with event 38 `CartelRefused` (the refusal's code and words), shown in Start's (or Join's) reason line until the next build. Every reason (a failure, a refused start, join or quit, why no Respect) has one key and one text in `CartelWords` (ShooterCore), which the server writes and the client registers. The test kind starts only for an admin. Server text that names the interact key writes `{interact}` (`JobText`); the tracker shows the bound key.

## Simple jobs (v1)

- **Delivery**: its duffel lies on the **Cartel pallet** by the Contact (never in anyone's hands); carry it to a drop point before the timer runs out. The pallet has four bag spots, one per running Delivery; a fifth is refused ("The pallet is full."). A map without a pallet has no Delivery. The line and the marker are on the pallet ("Take the duffel from the pallet") until a party player carries it, then at the drop point. Loaded in a vehicle (**Cargo vehicles**) the line says to drive it to the drop point, take it out (the throw key) and hand it over, and the marker stays at the drop point; the hand-over is unchanged (carry it, hold the interact key within `radius_m`). The Delivery fails ("The objective was lost") when its duffel no longer exists for any reason but the Job's own end.
- **Hit**: kill a Rival dealer who stands outdoors at a random spot (zone kind `cartel_hit`) with a few guards (Gunmen and Enforcers). The guards attack the job's players on sight within a radius and ignore everyone else unless attacked. The target dying by anyone's hand is success.
- Built (#494, #497): `DeliveryJobKind` and `HitJobKind` (ShooterCore/Cartel, cartel.json `jobs.delivery`, `jobs.hit`), points claimed by `CartelSpots` (book key `cartel_drop:<i>` / `cartel_hit:<i>`), the party the Job's `JobParty` (the one set of a Job, refreshed by the `JobBook` each tick), the server's world `CartelJobWorld`. Delivery: a `product` Duffel lying on a free pallet spot (`CartelPallet`, book key `pallet:<i>`, freed when the Job ends; `DuffelBook.Place`; the spot is `pallet.spots` of cartel.json turned with the pallet); a party player picks it up (the interact key), then hold the interact key 2 s within 4 m of the drop point (the client's prompt "Hold <key>: hand the duffel over" and the hold ring, `CartelClient`, from `jobs.delivery` `radius_m` / `hold_seconds`). Hit: the dealer is the Rival crew's Gang boss (no new kind), guards on a ring round him; marker on the spot, then on him (`Hit.Target`). Test with `/cartel job delivery|hit [tier]`.

## Heists (v1)

### Template

1. The leader starts it at the Contact; the Cartel van appears on the road beside the Contact.
2. The squad drives the van to the objective (Harvest: the plantation itself).
3. They fill duffels and throw them along a chain into the van's back: **loaded**.
4. Assaults arrive at the objective from a grace time on.
5. The heist succeeds when the van, with at least the minimum loaded, reaches a random drop point (far from the objective) and stops there. Respect counts what is loaded at that moment. Assaults stop once the van leaves the objective area (no chase in v1).

### Duffels

- One per player. Carrying one: no sprint, slower by its weight (weed light, cash medium, product heavy); the carrier can still shoot.
- One press of the throw key (X) throws it at once, 8 m on flat ground (`duffels.json` `throw_m`), the same for every kind: the weight slows the carrier only. No charge, and no putting it down at the feet; it lands as a lying duffel (a world entity). Only the job's players pick them up.
- No vehicle seat while carrying (throw it in the van first). Death drops it where the carrier fell.
- Built: `DuffelBook.Throw` (the arc: `lift_deg`, the elevation limits, wall stop, flight cap; a throw into a cargo volume that takes it loads), the throw key a plain fresh press (`Edge` in `DuffelServer.OnInput` and `DuffelsClient`; the machine `Duffel.ThrowKey` and the client's charge ring are gone). `DuffelBook.Drop` remains only for what the player does not choose (death, a seat, leaving the party).
- Loaded in a vehicle (below), a duffel is drawn on its slot there on every client, fixed to the vehicle; no seat is taken by the carrier either way.
- Built (Delivery rework): `DuffelBook` has every live cargo vehicle as a cargo volume (`VehicleCargo`, ShooterCore/Cartel), a Duffel is `Loaded` with its `Volume`, `Slot` and `LoadOrder`; `DuffelBook.Takeable` / `TakeOut` take the last loaded one of the party out, `RemoveLoadedIn` destroys a lost vehicle's. Wire: a loaded duffel's owner field is the vehicle's world id and bits 4-7 of its state byte the slot (`DuffelRules.Pack` / `SlotOf`).

### Cargo vehicles

Which vehicles take Duffels, and how (vehicles.json `cargo_volume`; `VehicleCargoVolumeDef`, `CargoVolume`; protocol 92):

- Types: `truck` (8, an open bed), `car` (1, the boot), `golf_mk2` (2, the hatch), `cartel_van` (12, the bay). No other vehicle takes bags. A cargo area has an **access point** on the ground at its end (`access`, `access_radius_m`): where a player stands to work the doors and to take a bag out.
- **Doors** (`cargo_volume.doors`: the boot lid, the hatch, the van's rear doors; machine `Cargo.Doors`, `CargoDoors`): Closed or Open on the server. A player on foot within the radius of the access point presses the interact key (E) to open or shut them, anybody's vehicle; they stay as left, also while driving; a spawn, a reset, a respawn or a destruction shuts them. Everywhere else round the vehicle E still enters it. Every client swings the doors (`CargoDoorsView`) from event 45 `CargoDoors` (u16 vehicle, u8 open; a joiner is told each open one). The prompt is "<key>  Open the boot" / "Close the boot" (`label` per type: boot, hatch, rear doors). The truck has no doors: always open, no door prompt.
- **Who has the key at the access point** (`KeyCompetitor.CargoDoors`, `InputServer`): a lying Duffel's pick-up first (a bag that fell beside the vehicle is picked up with the same key), then the Loot and the village doors, then the cargo doors, then the vehicle's seat (and a carrier's "no seat" notice). So at the access point E works the doors, not the seat; one step away it enters. The plantation's hold gives way to all of them.
- **Loading** (`VehicleCargo`, `CargoServer`): a thrown duffel is loaded when its flight meets the volume's box (grown by `duffels.json` `cargo_margin_m`, so a throw that lands against the open doors still counts) and the volume accepts it: doors open (or none), fewer than `capacity` loaded, the party allowed (any party's for an ordinary vehicle; the Cartel van only its own Job's, `Restrict`) and, for a `from_top` bed (the truck), the duffel came down through the top face. One that meets a cargo vehicle it does not accept stops as at a wall and falls straight down beside it (a truck's bed met from the side or the tail; a closed boot, a full one, another party's van); one that meets a shut lid's top slides off by the nearest side. It is never loaded.
- **Loaded duffels** take the first free slot (`slots`), are drawn there (`DuffelsClient.PlaceLoaded`, on the hull as it leans) with no physics and no animation, seen through the open doors and the windows.
- **Taking out**: a party player on foot with empty hands within the radius of the access point of a vehicle whose doors are open (or a truck) presses the throw key (X): the last loaded duffel of their party in it goes into their hands. Not their party's: nothing. The server tells the client when that would work (event 44 `DuffelTake`: vehicle, kind) for the prompt "<key>  Take a duffel out" and the predicted burden; the take-out prompt outranks the doors' prompt (both keys still work).
- A vehicle that is destroyed, retired, despawned or reset (a spawn, a respawn, a fall out of the world, a new round) loses its loaded duffels (removed from the world: `ServerCar.LifeEnded`). A Delivery whose duffel is gone fails "The objective was lost"; a Heist fails when its van is destroyed (`HeistFacts.VanDestroyed`), as before.
- A Heist counts only its own Cartel van's bay (`VehicleCargo.Loaded`, the one count: a duffel taken out lowers it) and tells the party at the minimum and the maximum; there is no second count.
- Not screenshot- or play-checked: the doors' look, the slots' fit, the access radius and the prompts want a human look (docs/standards/car-design.md "Hand back for a human look").

### Cartel van

- A new vehicle `cartel_van`, the Gang Warfare `SM_Veh_Van_01` imported by flow 1 of `docs/standards/car-design.md` (drive model, ADR 0036): driver and passenger seats behind glass, a rear cargo volume that takes duffels.
- Never dealt (`spawn_count` 0); only a heist puts one down, and it goes when the job ends.
- Only the job's players can take a seat in it, and only the job's own party's duffels load into its bay (its rear doors work like any cargo vehicle's: the key at the access point, shut at first; **Cargo vehicles**); everyone else is refused a seat.
- High hit points, destructible; destroyed, the heist fails.

### Assaults

- Assaults: Rival crew vans (the Raid van's road driving, ADR 0021) arrive after a grace time, then every N s; size by squad size and tier.
- Melee members reuse the Enforcer. New kind **Gunman**: a Gang Warfare pistol, SMG or shotgun, aim error from data, line of sight through the World query, real rounds that hurt players.
- Assaults stay outdoors on the navigation mesh (they shoot through doors and windows) and stop when the van leaves the objective area.
- Loud only: no stealth in v1.

### Harvest

- The Contact's own plantation. During the job, holding E 3 s anywhere in the plantation fills a weed duffel in the player's hands (no High); outside a job the hold gives High as now.
- A minimum and a maximum duffel count (tier, squad size). Assaults come in from the road side.
- Folds in the old "Defend the plantation" idea.
- Built (#495): the Heist template in `ShooterCore/Cartel/Heist.cs` (machines `Heist.Phase` and `Heist.Assaults`, `HeistRules`, `PlantationArea`; its Drop point is claimed by `CartelSpots.Claim` as a simple Job's points are, from the objective), the server's `HeistServer` / `HeistRun` (`IHeistGoal` is where a kind plugs its objective in; `VanBay` the van's bay, the one count of what is loaded) and `HarvestJobKind` (`IPlantationFill` on the plantation's hold: nothing fills before the van reaches the plantation, a hold while carrying or with the maximum filled does not count); Assaults are Raid vans of the Rival crew (`RaidServer.LaunchAssault`), kept by `HeistAssaults` (ShooterCore/Cartel: one is in the fight while its crew lives or is still in the van, so a standing van with a dead crew does not hold `max_assaults_alive`; once the bodies are gone its van is removed and frees its Raid van slot). A hit on an Assault van or member earns no part in the Thieves event; the Rival crew travels in bit 7 of the Gang member hp byte, and the client tints it (`rival_crew.json` `view.tint`) and puts no Gold chain on the Rival dealer. Numbers in `cartel.json` `jobs.harvest`; `/cartel job harvest [tier]` to test.

### Trap house

- The server picks a random hollow house (ADR 0040) with road access for the van, not used by another job, no player inside.
- Dressed for the job and stripped after: Gang Warfare props by room type (tables with product and cash, mattresses, crates, graffiti). Tables and crates are solid (box colliders, cover). The first furniture in hollow houses: its own small ADR.
- Built (#496): `TrapHousePlanner` (ShooterCore/Cartel; props, posts, safe and Duffel spots from the floor plan and a seed), numbers and props in `godot/shooter/data/trap_house.json`, `TrapHouseChoice` (which house), `TrapHouseServer` (dress / strip, events 66-67, joiner catch-up, round reset strips), `TrapHousesNode` (boxes on server and client, models on the client), admin `/traphouse [nearest|<house id>|off]`. No mattress or graffiti in Gang Warfare: mattresses are the Military pack's, graffiti is text on the wall.
- **Posted defenders**: each holds a spot in a room (picked from the floor plan), turns and shoots, never walks (no NPC goes inside a house today: the navigation mesh stops at the 0.95 m doorways).
- Duffels: product and cash on the tables, plus the safe.
- **Safe and drill**: hold E to place a drill; it runs a tier time (about 90 s) and jams at random one to three times; a job player holds E to fix it. Open, the safe gives bonus cash duffels.
- Built (#496): `Drill` (machine `Drill.Run`: armed, running, jammed, done) and `DrillRules` (ShooterCore/Cartel; run seconds and cash Duffels by tier, the jams' moments, the holds, who may hold and from where), numbers in `trap_house.json` `drill`; `DrillServer` (`Arm(house, party, tier)`, `Disarm`, `Of`, `Done` event with the paid-out Duffels; the hold of E via `KeyCompetition`, a corpse or ground item on the stand spot giving it up (`DrillKey`); a stripped house or a round reset disarms; events 62 DrillState and 63 DrillCleared to the players within `near_m`, 64 DrillReach to one player); `DrillsClient` (the drill on the safe prop, Apocalypse `SM_Prop_Drill_01` or a box, the running loop, the jam's sparks and clunk, the safe's door swings open, the hold ring and prompt) and the HUD's Drill panel for party players near it; admin `/drill [tier|off]` arms the nearest dressed safe for the admin.
- Built (#496, the heist): `TrapHouseJobKind` (server, kind `trap_house` on the Heist template) with `TrapHouseHeistData` / `TrapHouseHeistRules` (ShooterCore/Cartel/TrapHouseHeist.cs), numbers in `cartel.json` `jobs.trap_house`. Begin: `Pick` (a house with at least the minimum's Duffel spots; planned only, dressed once the template gave a run, so a refused start shows nothing), book key `trap_house:<id>`, then the template (van at the Contact, drop from the house); the tables hold the maximum less the safe's cash (never under the minimum); posted Gunmen and Enforcers on the dressing's posts (tier, squad size, `max_defenders`, the posts); the safe armed for the party. The area: `area_m` round the house's centre or the road spot where the van parks. Line adds the Drill's state while loading; the marker points at the safe while the Drill waits to be set, runs or is jammed. End strips the house, disarms the safe, removes the defenders (the template the van, Duffels, Assaults). `IHeistGoal` gained `Line` / `Marker` overrides. `/cartel job trap_house [tier]` to test.

## Testing

Admin chat commands (`/cartel`, `CartelServer.Chat`; the words in `ShooterCore/Cartel/CartelAdmin.cs`), on the admin's own standing and Job, so a Job's late stages need no play-through:

- `/cartel join | quit | respect <n> | status`; `/cartel job <kind> [tier]` starts any kind at any tier anywhere (the test kind `test` only for admins).
- On the running Job the admin is in: `/cartel end win` (succeeds and pays as a met objective) or `end fail` (fails, "Ended by an admin"); `/cartel time <seconds>` sets the seconds left (1 to 3600; the party's tracker is sent the Job again); `/cartel assault` sends an Assault on a heist now, through the clock's own launch and `max_assaults_alive` (the clock goes on as it was); `/cartel goto` puts the admin (alive, on foot) on the first surface under the Job's marker (the van's bay marker lands under the van).
- `/duffel <kind>`, `/drill [tier|off]`, `/traphouse`, `/gunman` for the parts on their own.

## Docs, ADRs, state machines

- ADRs: 0046 (cartel rank and Respect persist on the Profile, and a Job is not an Event), 0047 (trap house dressing). Possibly later: NPC gunfire at players (server time, no rewind, against ADR 0013's player-shot rewind).
- Glossary: the terms above are in; still to change when built: Squad gains a leader (still tdm-only); Plantation's hold during a Harvest; Gang member gains Gunman and Rival dealer and ranged fighting; Event's _Avoid_ names Job and Heist.
- State machines (`StateMachineSpec<T>`, `docs/state-machines.md`): Job.Run, Cartel.Membership, Heist.Phase, Duffel (carried, flying, lying, loaded), Drill (placed, running, jammed, done), the Gunman and posted defender states, the Assault clock. No bool flags.
- Data: `cartel.json` (ranks and curve, tiers, timers, counts, Respect, weights and throw ranges, Assault sizes and timing, drill times and jams).
- Map: zone kinds `cartel_drop` and `cartel_hit`; the Contact and the pallet as placed objects. Built: the kinds (`rules.yaml` `zones`, exported to `zone_points.json`, drawn in the Zones tool, group cartel); the Contact is the first placed object of the marker `prim:cartel_contact` (Objects tool, category other; its arrow is his facing, `contact.json` `facing`), hidden in the game, pass-through, with a Gang Warfare look (`contact.json` `view`, `Cartel/ContactView.cs`). The pallet is the first placed object of the marker `prim:cartel_pallet` (pipeline primitive, Objects tool, category other; 1.441 x 1.418 x 0.235 m, the Gang Warfare `SM_Prop_Pallet_01`'s size, its four bag spots in gold); unlike the Contact's marker it is solid like any placed prop (a low box the size of the pallet: it can be stepped on, shots stop on it), hidden in the game and drawn as that model (`Cartel/PalletView.cs`, `cartel.json` `pallet`: `spots`, `top_m`, `model`, `atlas`). The server reads all of it as `ShooterCore.Cartel.CartelMap` (`MapAreas.Cartel`): `Contact` (null: no cartel), `Pallet` (null: no Delivery), `DropPoints`, `HitSpots`, `CartelMap.Pick(pool, rng, filter)` and `CartelMap.Apart(points, m)` (the filter that keeps jobs apart), used by `CartelSpots.Claim`.

## Tickets (v1, sub-issues of #437)

1. #494 Cartel core: membership, Cartel rank and Respect, the Contact, Jobs, Duffels, Delivery
2. #495 Cartel Harvest heist: Cartel van, loading Duffels, Assaults, Gunman (blocked by #494)
3. #496 Cartel Trap house heist: dressed hollow house, posted defenders, safe and Drill (blocked by #495)
4. #497 Cartel Hit job: Rival dealer outdoors with guards (blocked by #495)

## v2 (not in v1)

- **Debt collection**: a debtor NPC in a hollow house to intimidate or beat up.
- **Cook**: a meth lab job (Payday's Rats / Cook Off): add ingredients in the order the Contact radios; a wrong mix blows up.
- **Stealth**: detection, an alarm that starts the Assaults.
- **Defenders that walk inside houses**: a walker on the floor plan's room graph.
- **Rank unlocks**: unlock rows on rank-up (draft: Dealer gold pistol, Sicario machine pistol, Capo gold rifle, Patrón the Technical call-in; masks are another idea).
- **Technical call-in**: needs the vehicle; bots in it need #358.
- **Contested seats**: limited server-wide positions on top of the personal ladder.
- **Phone**: take jobs from anywhere.
- **Chase Assaults**: rival vans follow the loaded Cartel van.
- **#366**: the patch gang as the Contact's guards.

## Left to implementation

- The duffel throw key (rebindable, clear of the existing binds): X, `duffel_throw` (#494).
- Every number (`cartel.json`), the Contact's model, the prop list and the Assault mix.
