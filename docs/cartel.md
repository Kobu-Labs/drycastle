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
- Rank-up: a toast plus a chat line to everyone.
- Rank unlocks: postponed (see v2).

## Who runs a job

- **ffa**: solo only. Squads stay team-deathmatch only.
- **tdm**: the squad. Squads gain a **leader**: the creator; when the leader leaves, the longest-standing member takes over, and the running job goes with them. The leader is marked in the Squads screen and on the scoreboard. A player in no squad runs jobs alone.
- Only the leader starts a job. The reward goes to every squad member at the moment of success, wherever they are (a member who joined midway gets it, one who left does not).
- A squad mate who is not in the cartel shares the objective and can help, earns nothing, and gets the hint "Join the cartel at the Contact".
- One running job per squad (or solo player). Many jobs can run at once, beside any Event. Two jobs never share a drop point, a target spot or a house.
- Only the job's players see its markers and tracker. Everyone sees the duffels on carriers' bodies. Outsiders can interfere by shooting; they get nothing special.

## Difficulty and reward

- **Tier**: the Contact lists jobs in tiers; the leader's rank unlocks the higher ones. Higher tier: more guards, tighter timers, more Respect.
- **Squad size** scales enemy counts, duffel counts and so on (everyone gets the full reward).
- Reward depends on tier only. Respect only: no global XP, and no XP for killing job NPCs (no `GangKill`).
- Heists: base Respect plus a bonus per loaded duffel above the minimum, times the tier. A failed job pays nothing.

## Failure

- A job fails when its timer runs out (a safety for heists, the pressure for simple jobs), at a round reset, or when the leader quits the cartel.
- Death fails it: solo, the player's death; in a squad, the **leader's** death (other members respawn and carry on; a carried duffel drops where they fell).
- Heists also fail when the Cartel van is destroyed.
- No Respect penalty, no cooldown. A job's NPCs, duffels and van are removed when it ends.

## Contact

- A placed object (position and facing) set by hand in the map editor near the plantation. A map without one has no cartel, with no error.
- Cannot be hurt: no weapon is tested against him (as a Companion).
- Stands alone in v1. #366 later puts the patch gang round him as guards: they never start a fight and fight back only at whoever attacks them (the Gang member rule), member or not.
- E opens a non-pausing panel (as the Hunter's Log): join / quit, rank and Respect bar, the job list by tier (locked tiers show the rank they need), Start (leader only; others see "Your leader starts jobs"). UI follows `docs/standards/ui-ux.md` and the `ui-work` skill.
- During a job: a HUD tracker (objective line, timer) and a marker on the minimap and in the world.

## Simple jobs (v1)

- **Delivery**: carry one duffel from the Contact to a drop point before the timer runs out.
- **Hit**: kill a Rival dealer who stands outdoors at a random spot (zone kind `cartel_hit`) with a few guards (Gunmen and Enforcers). The guards attack the job's players on sight within a radius and ignore everyone else unless attacked. The target dying by anyone's hand is success.

## Heists (v1)

### Template

1. The leader starts it at the Contact; the Cartel van appears on the road beside the Contact.
2. The squad drives the van to the objective (Harvest: the plantation itself).
3. They fill duffels and throw them along a chain into the van's back: **loaded**.
4. Assaults arrive at the objective from a grace time on.
5. The heist succeeds when the van, with at least the minimum loaded, reaches a random drop point (far from the objective) and stops there. Respect counts what is loaded at that moment. Assaults stop once the van leaves the objective area (no chase in v1).

### Duffels

- One per player. Carrying one: no sprint, slower by its weight (weed light, cash medium, product heavy); the carrier can still shoot.
- Hold a key to charge a throw, about 6 to 12 m by weight; it lands as a lying duffel (a world entity). Only the job's players pick them up.
- No vehicle seat while carrying (throw it in the van first). Death drops it where the carrier fell.
- Only the job's own Cartel van takes duffels.

### Cartel van

- A new vehicle `cartel_van`, the Gang Warfare `SM_Veh_Van_01` imported by flow 1 of `docs/standards/car-design.md` (drive model, ADR 0036): driver and passenger seats behind glass, a rear cargo volume that takes duffels.
- Never dealt (`spawn_count` 0); only a heist puts one down, and it goes when the job ends.
- Only the job's players can take a seat in it or throw duffels in; everyone else is refused.
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

### Trap house

- The server picks a random hollow house (ADR 0040) with road access for the van, not used by another job, no player inside.
- Dressed for the job and stripped after: Gang Warfare props by room type (tables with product and cash, mattresses, crates, graffiti). Tables and crates are solid (box colliders, cover). The first furniture in hollow houses: its own small ADR.
- **Posted defenders**: each holds a spot in a room (picked from the floor plan), turns and shoots, never walks (no NPC goes inside a house today: the navigation mesh stops at the 0.95 m doorways).
- Duffels: product and cash on the tables, plus the safe.
- **Safe and drill**: hold E to place a drill; it runs a tier time (about 90 s) and jams at random one to three times; a job player holds E to fix it. Open, the safe gives bonus cash duffels.

## Docs, ADRs, state machines

- ADRs: 0045 (cartel rank and Respect persist on the Profile, and a Job is not an Event), 0046 (trap house dressing). Possibly later: NPC gunfire at players (server time, no rewind, against ADR 0013's player-shot rewind).
- Glossary: the terms above are in; still to change when built: Squad gains a leader (still tdm-only); Plantation's hold during a Harvest; Gang member gains Gunman and Rival dealer and ranged fighting; Event's _Avoid_ names Job and Heist.
- State machines (`StateMachineSpec<T>`, `docs/state-machines.md`): Job.Run, Cartel.Membership, Heist.Phase, Duffel (carried, flying, lying, loaded), Drill (placed, running, jammed, done), the Gunman and posted defender states, the Assault clock. No bool flags.
- Data: `cartel.json` (ranks and curve, tiers, timers, counts, Respect, weights and throw ranges, Assault sizes and timing, drill times and jams).
- Map: zone kinds `cartel_drop` and `cartel_hit`; the Contact as a placed object. Built: the kinds (`rules.yaml` `zones`, exported to `zone_points.json`, drawn in the Zones tool, group cartel); the Contact is the first placed object of the marker `prim:cartel_contact` (Objects tool, category other; its arrow is his facing, `contact.json` `facing`), hidden in the game, pass-through, with a Gang Warfare look (`contact.json` `view`, `Cartel/ContactView.cs`). The server reads all of it as `ShooterCore.Cartel.CartelMap` (`MapAreas.Cartel`): `Contact` (null: no cartel), `DropPoints`, `HitSpots`, `PickDrop`/`PickHit(rng, filter)`, `CartelMap.Apart(points, m)` to keep jobs apart.

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

- The duffel throw key (rebindable, clear of the existing binds).
- Every number (`cartel.json`), the Contact's model, the prop list and the Assault mix.
