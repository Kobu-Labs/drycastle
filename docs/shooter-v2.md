# Shooter v2: overview

Status: all tickets of "Map: Shooter v2" (#104) are closed, most of them "resolved in part". This page is the entry point:
what the game now has, where the numbers live, the wire protocol, and what is still open. Server operation (flags,
database, firewall) is in `docs/shooter-server.md`. Terms are defined in `GLOSSARY.md`; the decisions are in
`docs/adr/0003` (persistence and identity) and `docs/adr/0004` (water bodies in the map artifact).

Nothing in this document was checked by running the game. The unit tests in `godot/ShooterCore.Tests` cover the shared
simulation, the data files and the wire codecs; the Godot scenes, shaders and HUD layouts are compiled but unseen.

## 1. Loadouts

- A player has five loadout slots. Each has a primary weapon, a secondary weapon, a perk and three killstreaks (see the
  glossary). Each slot also carries the camouflage of the player's suit and face paint (#141).
- Edited in the loadout menu (#118) at any time; the active slot is applied at the next spawn, so a switch made while
  alive takes effect on death.
- The server validates every loadout against the data files (weapon slots and unlock levels, perk and streak ids) before
  it stores it (`LoadoutRules`, `LoadoutEditor`).
- Hotbar: primary, secondary and knife (`LoadoutHotbar`). Keys 1-3 and the next-weapon cycle use it. The bazooka is not a
  loadout item, so players cannot select it; see section 9. Smoke arenas and `--debug-cmds` servers keep every weapon on
  the hotbar (`LoadoutHotbar.All`).
- Corpses (#130): a killed player leaves a corpse holding its primary. Walking onto it and pressing E swaps the corpse's
  weapon into the primary or the secondary position. A corpse also carries the item its player wore (section 7b).
- Loadout autosave, rename, copy and reset (#145). Each loadout has a name (1-24 characters, trimmed, `LoadoutRules.ValidateName`,
  default "Class 1" .. "Class 5"). The editor saves on its own after a pause: every `SaveLoadout` carries a client sequence
  number (`seq`), and the server answers each one with a `LoadoutAck` event so the client can match answers. Copy (`CopyLoadout`)
  and reset to the starter kit (`ResetLoadout`) are server-validated events; a copy keeps the destination's name and slot.
  Dev mode (`--unlock-all`, #149) reports every unlock and the `dev/unlock_all` marker, and the starter kits are the showcase kits.

## 2. Weapons

Registry: `godot/shooter/data/weapons.json` (version 2). Categories and their recoil defaults are in `category_defaults`.
Weapon categories: machine guns, assault rifles, SMGs, shotguns, snipers (primary); pistols (secondary); the bazooka and the
knife are special.

| Category | Weapons (unlock level) |
|---|---|
| Assault rifles | AK-47 (0, starter), M4A1 (2), G36 (4), AUG (6) |
| Machine guns | RPK (3), PKM (8), M249 (10), M60 (12) |
| SMGs | UMP45 (4), MP5 (6), P90 (9) |
| Shotguns | Remington 870 (7, pump), SPAS-12 (8, pump), Benelli M4 (10, semi), AA-12 (14, full auto) |
| Snipers | Sniper (5), M82 (7), SVD (9), AWM (11) |
| Pistols | M1911 "pistol" (0, starter), Glock 17 (3), Desert Eagle (5) |

Mechanics added in v2:
- Recoil per category with per-weapon overrides (#110). Client camera and viewmodel kick follow the shared simulation.
- Shotguns fire a pellet fan (`PelletFan`, event 12). The seed travels, so server and clients derive the same pellets
  (#112). Pump and shell reloads are flags on the weapon.
- Snipers have a scope (`scope_fov`) and breath hold (`breath_hold_seconds`, `breath_recover_seconds`). Zoom levels
  beyond one are not implemented (#113).
- Iron sights (hold zoom) ease the gun to centre and fade the crosshair; the sniper keeps its scope (commit bda2271).
- Bullet holes (64-decal ring buffer, fade after 15 s) and sparks oriented along the surface normal on hitscan hits (commit 189b6bf).

### Time to kill (balance pass, #133)

Shots to kill a 100 hp body (no headshot) and the time from the first shot to the killing shot, at 5/15/30/45/60 m, from
the shipped `weapons.json`. Computed with the same falloff as `HitMath.HitscanDamage`. Shotguns are approximate (the pellet
fan is a Monte Carlo in the game; the figures here use a 3000-trial estimate).

| Weapon | 5 m | 15 m | 30 m | 45 m | 60 m |
|---|---|---|---|---|---|
| pistol | 5 / 0.60 s | 5 / 0.60 | 5 / 0.60 | 5 / 0.60 | 6 / 0.75 |
| glock17 | 6 / 0.55 | 6 / 0.55 | 7 / 0.66 | 8 / 0.77 | 8 / 0.77 |
| deagle | 3 / 0.70 | 3 / 0.70 | 3 / 0.70 | 3 / 0.70 | 3 / 0.70 |
| ak47 | 4 / 0.30 | 4 / 0.30 | 4 / 0.30 | 4 / 0.30 | 4 / 0.30 |
| m4a1 / aug | 4 / 0.26 | 4 / 0.26 | 4 / 0.26 | 4 / 0.26 | 5 / 0.34 |
| g36 | 5 / 0.32 | 5 / 0.32 | 5 / 0.32 | 5 / 0.32 | 5 / 0.32 |
| rpk | 4 / 0.30 | 4 / 0.30 | 4 / 0.30 | 4 / 0.30 | 5 / 0.40 |
| m249 | 5 / 0.32 | 5 / 0.32 | 5 / 0.32 | 6 / 0.40 | 6 / 0.40 |
| m60 | 4 / 0.33 | 4 / 0.33 | 4 / 0.33 | 5 / 0.44 | 5 / 0.44 |
| pkm | 4 / 0.28 | 4 / 0.28 | 4 / 0.28 | 5 / 0.37 | 5 / 0.37 |
| mp5 | 5 / 0.30 | 5 / 0.30 | 8 / 0.53 | 10 / 0.67 | 10 / 0.67 |
| ump45 | 4 / 0.30 | 5 / 0.40 | 8 / 0.70 | 8 / 0.70 | 8 / 0.70 |
| p90 | 6 / 0.33 | 6 / 0.33 | 8 / 0.46 | 12 / 0.73 | 12 / 0.73 |
| remington870 | 1 / 0 | 7 / 5.1 | 51 / 42.5 | 114 / 96 | 203 / 172 |
| spas12 | 1 / 0 | 7 / 4.2 | 51 / 35 | 114 / 79 | 203 / 141 |
| benelli_m4 | 1 / 0 | 7 / 2.4 | 51 / 20 | 114 / 45 | 203 / 81 |
| aa12 | 2 / 0.2 | 12 / 2.2 | 86 / 17 | 193 / 38 | 345 / 69 |
| sniper | 2 / 1.40 | 2 / 1.40 | 2 / 1.40 | 2 / 1.40 | 2 / 1.40 |
| m82 | 1 / 0 | 1 / 0 | 1 / 0 | 1 / 0 | 1 / 0 |
| awm | 1 / 0 | 1 / 0 | 1 / 0 | 1 / 0 | 1 / 0 |
| svd | 2 / 0.40 | 2 / 0.40 | 2 / 0.40 | 2 / 0.40 | 2 / 0.40 |

Changes made in #133 (JSON only, plus the test expectations that pin them):
- **Desert Eagle damage 60 to 45.** At 60 it was a 2-shot kill at every range and the best pistol everywhere (the only
  dominating weapon found). It is now a 3-shot kill with a long-range niche (best at 60 m).
- **Benelli M4 fire interval 0.3 to 0.4 s.** It was faster than the other shotguns at every range from 15 m on, so it was
  the category's best at every range. It now shares the 15 m lead with the AA-12 and is best from 30 m out, where shotgun
  kills are already slow.

`CategoryBalanceTests` (new) fails if one weapon is the unique fastest kill in its category at every sampled range.

Left alone on purpose (see section 10): M60 is strictly slower than the PKM at every range; M249 is strictly slower than the
RPK at every range; the starter sniper is the slowest of the four snipers; the shotgun falloff makes anything past 15 m a
long fight.

## 3. Perks

Data: `godot/shooter/data/perks.json`. One perk per loadout. The modifier is read through `PerkModifiers` (`PerkData.cs`).

| Perk | Unlock | Effect | Wired |
|---|---|---|---|
| Fleet Foot | 0 | run speed x1.12 | yes |
| Quick Hands | 0 | reload speed x1.33 (reload time divided) | yes |
| Flak Jacket | 3 | explosive damage taken x0.8 | yes |
| Swift Swap | 5 | weapon swap speed x1.5 | yes |
| Tank Hunter | 8 | damage dealt to vehicles x1.25 | yes |
| Hot Streak | 12 | every killstreak threshold lowered by 1 kill (never below 1) | yes |
| Steady Lung | 15 | breath hold time x1.5 | yes, but not in water (#138) |
| Ghost | 20 | UAV visibility x0 (enemy UAV pings do not show you) | yes |

Perk numbers are the first set from the brief (#115), not tuned by play. Two of them scale with the streak they are
attached to: Hot Streak removes 1 kill from the 4-kill UAV (25 %) and from the 24-kill MOAB (4 %). Whether that is the
intended feel is an open question (section 10).

Care package reward (#151, replaces the perk table of #124): opening a crate grants a random killstreak from
`killstreaks.json` `streak_weights` (weights out of 97): UAV 30, Attack Missile 22, Turret 18, Helicopter Helper 10,
Attack Helicopter 9, AC-130 6, MOAB 2. Better streaks are rarer. The care package never gives another care package, and
Hot Streak does not affect the reward. The table is validated at load: every id must be a streak, care_package is refused,
and each weight is 1 or more.
- The reward goes into one of three held reward slots at once, without touching the Streak counter. It is used with the
  Killstreak key after the loadout's streaks. A full set replaces the oldest reward, and the toast says what was replaced.
- Rewards are lost on death, like earned streaks.
- The opener gets a toast with the streak's icon (`IconRegistry`, `streaks/<id>.png`) that names the key. The loadout
  tracker does not show reward slots yet (open).
- The crate is freed as soon as it is opened (the server despawns it with reason Opened and the client frees its node).
  An unopened crate is freed when it expires.
- Opening is a hold of E (2 s). A radial ring (`RadialProgress`, labelled "Opening...") fills over the hold and drains
  on release. Its reduce-motion path is an instant drain. No numeric countdown.

## 4. Killstreaks

Data: `godot/shooter/data/killstreaks.json`. The Streak counter counts the owner's kills (not suicides), resets on death,
and kills made with a streak's own weapon do not advance it (`StreakWeapons`). The jeep's roof gun (vehicle gun, 250) is
not a streak weapon, so its kills do count (correction on #133).

| Streak | Kills | Unlock | Duration | Control | Notes |
|---|---|---|---|---|---|
| UAV | 4 | 0 | 60 s | none | enemy pings every second to the owner; no visible UAV model |
| Attack Missile | 5 | 4 | dives to the ground (about 7-13 s, #155) | POV | steered missile from 450 m, dive limited to -32 to -80 deg, infrared view, 100 dmg, splash 12 m |
| Sentry Turret | 6 | 5 | 15 s | none | placed on the ground, 500 hp, 12 dmg per shot |
| Care Package | 6 | 6 | instant | none | drop lands, hold E 2 s opens it (crate gone), grants a random killstreak (weighted, section 3) |
| Attack Helicopter | 10 | 9 | 45 s | POV | owner flies the helicopter (POV) for 45 s |
| Helicopter Helper | 15 | 11 | 60 s | none | AI helicopter, bursts at enemies with a red-dot warning |
| AC-130 | 18 | 14 | 40 s | POV | circling gunship, 25 mm / 40 mm / 105 mm, unlimited ammo, no heat (#157) |
| MOAB | 24 | 20 | 5 s countdown | none | 5 s countdown, then a blast that kills everyone in range |

Attack missile time to ground (450 m spawn, 64 m/s, flat ground, no steering = 450 / (64 x sin(pitch))):

| Dive pitch | Time to ground |
|---|---|
| -10 deg (old shallowest, retired) | 40.5 s |
| -32 deg (shallowest, shipped) | 13.3 s |
| -45 deg (launch dive) | 9.9 s |
| -80 deg (steepest) | 7.1 s |

Shared aircraft code: `Aircraft` (circling path, replication, shared gunship control, #125).

Gunship turrets (#157): no ammo and no heat. The streak's time limit is the only limit; the fire interval paces each weapon,
and an interval shorter than the input period fires every round due (up to `GunshipControl.MaxRoundsPerInput` per input).
Every cannon round has a blast (`splash` in killstreaks.json: full damage at the hit point, falloff to `edge_damage` at the
radius). The owner is never hurt by its own blast, and the blast uses the vehicles.json rocket multiplier on vehicles. The
routing is pure (`GunshipRouting.Route` and `GunshipRouting.Burst`, tested in `GunshipRoutingTests`). Direct damage per second
on a body (the hit point), and rounds to destroy a 300 hp car (direct hit, rocket multiplier 1.5; Tank Hunter x1.25 is not in these):

| Weapon | Interval | Damage | Direct DPS | Blast radius | Car rounds |
|---|---|---|---|---|---|
| Helicopter cannon (m82) | 0.15 s | 75 | 500 | 3 m | 3 |
| AC-130 25 mm (m249) | 0.04 s | 50 | 1250 | 3 m | 4 |
| AC-130 40 mm (m82) | 0.2 s | 80 | 400 | 6 m | 3 |
| AC-130 105 mm shell (bazooka) | 1.0 s | 150 | 150 | 14 m | 2 |

Turret aim is world space and ground-stabilised (#160). The aim is a world point: the look (the mouse's world yaw and pitch) meets
the ground plane at the orbit centre's height (`GunshipAim.AimPoint`; a plane, not a physics raycast, so the client and the server
compute the same point), or lies the longest weapon range out when it meets no ground that near. The turret's view is the direction
from the muzzle to that point (`GunshipAim.View`), so the orbit never moves the reticle. Only the pitch is limited (`pitch_min_deg`,
`pitch_max_deg`); the airframe-relative yaw limit is gone (it pinned the view as the airframe turned, which was the bug). The mouse
move is applied to the view on screen, zoom-scaled (`GunshipAim.Turn`: x4 is a quarter of the speed), and re-anchors the point; a
still mouse keeps it. The camera's yaw is unwrapped (`GunshipAim.Unwrap`) so it is continuous through +-pi and every heading;
`GunshipGroundAimTests` sweeps the heading through 720 degrees and the aim through +-pi.

The aim is sent, not re-derived (#161). The client computes the point from its own muzzle (its interpolated, lagging aircraft) and
sends it with every input while a turret is steered (`NetInput.HasAim`, the wire's flags byte; protocol 15). The server does not
rebuild the point from the look angles: doing that from its own, later muzzle intersected the same ray from a point the platform
had travelled on, so every round landed the platform's travel over the lag to one side of the reticle (about 10 m at 70 m/s and
150 ms, which the old scheme shows in `GunshipAimLagTests`). `GunshipControl.OnInput` takes the point, bounds it (`GunshipAim.ClampAim`:
no farther than the orbit's reach plus the longest weapon range (3000 m, so any point on the map) plus a 50 m lag allowance, measured from the orbit centre, and never
below the ground plane), and fires from its muzzle along the view to it. The bound is from the centre and not the muzzle on purpose:
a stabilised point is out of range of the moving muzzle for part of every orbit, and a clamp to the muzzle would pull it off the
reticle. A turret with no aim yet does not fire. Parallax remains: the rounds leave the server's muzzle, which is a few metres from
the client's, so the ray differs by a few tenths of a degree, but the impact is on the reticle's point (the lag test: within 0.5 m).
The helicopter gunner and the AC-130 share this path; the attack missile is steered by its own input and is unchanged.

Tracers (#160): a cannon round's and a shell's tracer speed and length come from killstreaks.json (`tracer_speed_mps`, `tracer_length_m`;
cannon 1500 m/s and 14 m; the 105 mm shell is 1500 m/s too, so a shell reaches 800 m in about 0.53 s and its 3000 m range in about 2 s).
They ride the GunshipShots message and `ShotFx.Spawn` takes them as optional parameters (the generic 250 m/s stays for every other
weapon). Rockets have no tracer data. A gunship's impact is its own message (GunshipImpacts), so its tracer does not also draw the
generic spark (`ShotFx`'s 300 m cut-off would skip it for a long round). A sky shot's tracer is drawn only to
`GunshipFxLimits.MaxSkyTracerM` (1500 m); the round still flies its full range. The cannon rounds' damage is still
instant (their 3000 m flight is at most 2 s, but a delay would make the 0.3 s hits at 450 m feel laggy). The 105 mm shell is the
exception (#162): it bursts when its tracer arrives. The detonation is at fire time plus distance over `tracer_speed_mps`
(`ShellFlight.Plan`, read from the same weapon def as the visual, so they agree), within one tick; the server queues it in a
`TimedQueue<PendingShell>` and `DetonateShells` runs once a tick, before the coalesced FX are sent, so the blast, the impact and the
hit markers come at arrival. The point and the route are fixed at the fire (a shell does not home on a moving target). A shell still in
the air is lost on a round start or end (`_shells.Clear()`); its owner leaving does not stop it. A shell whose flight is one tick or less
is resolved at once.

Gunship rockets (#159) have their own 8 m blast: a `splash` block on the rockets entry in killstreaks.json (radius 8, full damage
to 2 m, edge 20, no self damage). The rocket sim takes it per launch (`Rocket.Blast`, `ProjectileMath.WithBlast`), so the bazooka
keeps weapons.json's 6 m, and the explosion's radius rides the Explosion event (protocol 12).

Gunship rocket flight (protocol 16): the rockets entry has `projectile_speed_mps` (250, default 250). A rocket launched with it flies
at that speed and lives out its whole range: its life is `RocketFlight.LifeFor(range_m, speed)` (range over speed, plus 0.5 s), carried
in the server's `Rocket` (`Speed`, `Life`; zero keeps the weapon's own). Before this, a gunship rocket flew the bazooka's 40 m/s and
expired after 3.75 s, about 150 m out, so it never reached a target 400 m or more away. The Launch event's velocity is the flight
(speed and direction), so clients animate it at that speed; a snapshot no longer resets a rocket's speed to the bazooka's. The
per-tick rule is `RocketFlight.Step` (pure, tested in `RocketFlightTests`); the bazooka keeps weapons.json's 40 m/s and 3.75 s. The enemy markers are a red diamond
per visible enemy player and target dummy, the nearest to the reticle larger. One helper draws them for both the gunship overlay
and the attack missile's infrared view (`EnemyMarkers`, from the remote replicas), so the two cannot drift apart.

Coalesced gunship FX (#159): damage is applied when a round resolves, as before, but the clients are told once per server tick
(30 Hz), not once per round. Per tick, each gunner sends at most one shots message (up to 8 tracers; the first 8 of the tick are
drawn), everyone gets at most one impacts message (up to 6; every kill is kept, then the biggest of the rest), and each gunner
gets at most one blasts message (up to 8 blasts, the biggest kept) carrying its hit-marker flags (hit, headshot, kill), so the
hit marker is one per tick and not one per round. The batch is `GunshipFxBatch` (pure, tested in `GunshipFxTests`), flushed at the
end of the tick. At 25 rounds a second a gunner lands about one round a tick, so the coalescing does not save bytes at the normal rate. Measured
for one gunner's full-rate 25 mm second (25 rounds, each with a tracer, an impact and a 3 m blast, 40 bytes of packet overhead
per message): 4875 bytes a second coalesced. The old per-round events for the same second come to about 4775 bytes (Shot 31,
ImpactFx 18 and Ac130Blast 22 bytes each, envelope included, plus the same overhead). The test asserts under the 6000 bytes a second budget. The
saving is in bursts (a stall of input, or several gunners in a tick), where the caps bound the messages to three per gunner per
tick. Server-side damage is not covered by a test in ShooterCore.Tests (the resolution is in Godot); it is applied when each round
resolves, before the tick's flush.

Reachability (#133, estimate): UAV and the turret are reachable in a first life at level 0-5. The 18 and 24 kill streaks
need most of a 25-kill round in a single life; with `score_goal` 25 the MOAB is effectively a match-deciding streak.

## 5. Progression

Data: `godot/shooter/data/levels.json` (XP curve, awards, unlock table). Storage: SQLite on the dedicated server (ADR 0003).

- XP curve: `xp_to_reach` lists the total XP for levels 0 to 40 (150 for level 1, 1,120 for level 6, 7,680 for level 20,
  46,390 for level 40).
- Awards: kill 100, headshot +25, assist 40 (10 s window), streak kill 20, roadkill 130, match win 250, match top three 100.
- Unlocks: the level-0 kit is the AK-47, the M1911, Fleet Foot and Quick Hands. Each category has unlocks at several levels
  (no category is gated behind level 12 or higher; the first shotgun is level 7). Killstreak unlocks match the killstreak
  table above.
- Level-up toast and round XP summary exist (`LevelUp`, `XpSummary`). A per-kill XP toast does not (section 8).

Estimate for the pacing check (#133): one match with about 10 kills and a win is roughly 1,200 XP, which is level 6 after
one match and level 20 after about six good matches. No telemetry exists yet.

## 6. Camouflage and customisation

- `camo.json` (version 1): bodies, skin tones, headgear looks, suit presets, face paint patterns, vegetation looks, tuft settings and the
  concealment numbers (`CamoSpec`, codec version 2, 33 bytes on the wire; stored version 1 specs of 30 bytes still decode, with the default character).
- Character layer: `bodies` lists the player models (`assets/animated-characters`: Soldier M/F, Rifleman, Operator; unverified licence, see
  `assets/README.md`). They share one rig, so animation and the weapon grip are the same for all. Each model is flat-colour surfaces named by
  material; a body entry says which materials are the `suit` (camo shader), the `gear` (vest and belts, a dark tint of the suit's secondary colour),
  the `headgear` (helmet: as modelled, covered in the suit pattern, or hidden, `headgear` list order is fixed) and the `hair`. `Skin` and `Face` take
  the chosen skin tone (the boots, which the model puts in `Skin`, are darkened by a vertex colour added when the model loads). The model has a blank
  face plate, so two dark eyes are added on the head bone. Foliage tufts are scattered on the actual suit, gear, hair and
  headgear triangles (the scalp when no helmet is shown) and ride on the bones, so they follow the animation. The scatter is a fixed sequence per
  body region: a region's density is its tuft count per square metre, the layers are the cards per tuft, the size is the card length and the
  vegetation index is the card's shape and hang (grass, fern, bush), so each control changes one thing and nothing else moves. Face paint is a
  Decal on the face plate; its coverage is how much of the face the marks fill. The suit's contrast is the distance between its two colours
  (half is the colours as picked).
- Arms: the rig has no rifle clips, so `ArmsReadyPose` (a skeleton modifier) poses both arms on the weapon over any clip with a two-bone solve
  (right hand at the chest, left hand on the foregrip); pistols use the right arm only, a knife swing plays `SwordSlash`.
- Hit volume: `PlayerHitBox` is measured from these models (3.147 units tall in their T-pose, scaled to 1.8 m); the 0.56 m radius is unchanged.
- Customiser UI (`CustomizationScreen.cs`, `CamoPreview.cs`) edits the look per loadout slot (#141).
- Prone stance (#139): a prone latch toggled by the prone key (a move button bit); crouch now has a transition time.
- Concealment is a number only; it is not applied in the tick, the name tags or the kill cam (section 8).

## 7. Vehicles and the world

- Vehicles (`vehicles.json`): car, motorcycle, bicycle (v1), war jeep and quad bike (#131), and the amphibious transport
  (#132; a boat type was shipped with it and later removed). Handling was reworked in #134 (max speed, turn radius, braking, hp). Headlights and tail lights come from the
  optional `lights` block (#137).
- Seat switch: while seated in any vehicle the number keys (the `slot_1` to `slot_4` actions, rebindable) take that seat, and a seat
  schematic shows the seats (bottom right). Client request: event kind 161 (u8 seat index); the server answers the riders with kind 162 (who
  sits in each seat) whenever that changes; protocol 20. `VehicleSeat.TrySwitch` is the rule, `SeatSchematic` the layout.
- Bus (`bus`): a 12 m land vehicle with a generated model (`scripts/make_bus_model.py`), one on the map and in the sandbox vehicle list. It is data
  only: a driver seat and three passenger seats (the seat limit is four), the usual driving model with a wide turn circle (`turn_radius` 9.5), smoke
  from the rear where its engine is. It is boarded like a car, within 3 m of its centre, so along the middle of either side (the middle door is there),
  not at the front door. Its doors are separate model nodes that nothing moves yet.
- Utility helicopter (ADR 0006): the flown vehicle (`utility_helicopter`, medium `air`), see "Utility helicopter" below.
- Amphibious transport (ADR 0010, 0011, 0012): an open three-seat vehicle with its own driving model on land and water, an armed seat and a
  ring gun, and riders the hull does not cover (`amphibious`, medium `amphibious`), see "Amphibious transport" below.
- Water bodies (#138, #142): the pipeline writes `water.json` into the map artifact; the server and clients swim, submerge
  and hold breath (HUD "AIR"). Surfaces, underwater tint and splashes are client-side and not yet compiled by Godot.
- Day/night (#135): one server clock, `environment.json` (start time 08:00, day length 24 minutes by default). The sun,
  moon, sky shader and ambient light follow it. Street lamps (#136) light up at night from `lamps.json`.
- Sentry turret, attack missile, MOAB, care packages and aircraft are world entities (section 9).
- Trees and props (#158): trunks are solid, crowns and bushes are shot-only cover (`CollisionLayers`: bullets, pellets, rockets,
  turret sight and the client's decals stop on a crown; players and cars drive through). Tree solids come from `trees.json`
  (map 0.6.0). Placed objects (`objects.json`) get one box each by category: flowers none, bushes shot-only, trees a trunk and a
  crown, everything else (cars, walls, slabs, pots, the radio tower) solid. Hits on trees are classed wood (trunk) or foliage
  (crown). Still open: fences without collision in the village profile take no shots either (a pipeline choice), a parcel
  replaced at runtime keeps its trees' solids, and an edited tree is not in the game until the map is re-exported.

### Utility helicopter

One spawns on the map (`spawn_count` 1, on flat ground with room for the rotor) and it is in the sandbox vehicle list. The model is generated
(`scripts/make_utility_helicopter_model.py`). The pilot's client predicts it and the server corrects it (ADR 0006); everyone else sees it
interpolated, pitching and rolling from the flight record of the snapshot.

**Controls** (the rebindable movement actions; nothing new to bind):

| Input | Does |
|---|---|
| W / S (`move_forward`, `move_back`) | Tilt the nose down / up: fly forward / backward |
| A / D (`move_left`, `move_right`) | Bank: slide sideways |
| Mouse X | Turns the view at once; the nose swings round to the view (at most `max_yaw_rate_deg` a second) |
| Mouse Y | Looks up and down around the helicopter; it does not fly |
| Space (`jump`) | Climb. On the ground with the rotor at speed: take off |
| Ctrl (`crouch`) | Descend |
| Hands off | Levels, flares to a stop and holds the hover at its height |
| E (`interact`), 1-2 (`slot_1`..) , H | Get out, switch seat, landing light, as in every vehicle |

Motion comes from attitude: the keys ask for a tilt (ramped, so a tap is a small correction), the airframe swings to it on a critically
damped spring, and the tilt accelerates it. In forward flight a turn banks itself and the direction of travel swings round with the nose.
The chase camera turns with the mouse at once and follows the airframe on a spring; it takes a share of the pitch and roll and widens with
speed (both off with reduce motion).

**Handling** is all in `vehicles.json` (`top_speed`, `acceleration` and the `flight` block; each key's summary in `VehicleFlightDef` says what
turning it up does). `dotnet test ShooterCore.Tests --filter The_feel_in_numbers --logger "console;verbosity=detailed"` prints the resulting
times and distances without the game; `FlightTests` holds the limits.

**Door gun.** The second seat is the door gunner's, behind a pintle-mounted machine gun in the right door (`door_gun` in `vehicles.json`: pivot,
rest direction, arc; `DoorGunRules`). The gunner's view is what the gun is asked to point at, and it stays world-stable while the helicopter
turns under it; the gun points there as far as its arc allows (70 degrees either side of straight out, 55 down, 12 up), and the view is held to
the arc as well. The arc is in the airframe's frame, so it banks with it, and it clears the hull, the skids, the tail and the full sweep of the
blades (`DoorGunTests` checks every aim). The rounds are the jeep's roof gun's (`gun`: the same damage, interval, spread and range, a 100-round
belt), hitscan from the muzzle, and its kills are ordinary kills (kill-feed weapon "Mounted MG", special weapon 250). The aim reaches every
client as a turret record (entity type 4, as the flak's, here the mount's yaw and the gun's pitch against the airframe), so the gun follows the
aim on everyone's model; the tracer leaves the drawn muzzle, the barrel recoils and the muzzle flashes. The gunner's ammo is event 160.

**What it shows and sounds like** (client only, `NetClient.FlightFx.cs`; nothing but the flight record is on the wire). The rotors turn at the
record's rotor speed: they spool up when a pilot sits down (`spool_up_time`) and run down on the ground without one, a little faster under load
(climbing, tilted hard), with a faint disc for the blur of the blades at speed. The rotor sound is generated (`RotorSound`, as the killstreak
aircraft's) and its blade beat, engine tone and level follow the rotor speed and the load. The body pitches and rolls for everyone. Damage
smoke comes from the engine exhaust (`hood`). In a hover under 11 m the downwash raises dust on the ground, or spray over a water body (off
with reduce motion). Riders sit (the crouch pose every rider has) and do not walk.

*Who can be shot in their seat.* The door gunner (`exposed_gunner`): they sit in the open door, so a shot that reaches their body hits them
and not the hull (`GunnerCover`), and killing them silences the gun. The pilot is behind the nose and the glazing and is covered by the hull,
as the driver of every other vehicle is: to stop the pilot, destroy the helicopter.

**Decisions on the edges.**

- *Nobody in the pilot seat, in the air.* The stabiliser flies: it levels, flares to a stop and holds the hover. With riders aboard it holds
  for `pilotless_hover_time` (4 s, time to take seat 1), then sinks at `autorotate_descent` (3 m/s), slowed by the cushion to a touchdown that
  costs nothing. Taking the pilot seat at any point catches it. With nobody aboard it sinks at once; the usual abandon timer then returns it.
- *Getting out in the air.* Allowed. The rider leaves through the door on their own side at the cabin floor and falls from there with no
  speed of their own; the fall damage rules decide what it costs, so a low hover is free and height kills.
- *Out of hit points in flight.* It blows up where it is, like every vehicle: everyone aboard dies, credited to whoever dealt the last damage
  (or to nobody, for a crash nobody caused), the blast hurts what is near, and it returns after `destroyed_return_time`.
- *Ceiling.* `ceiling` (160 m) above the ground under it; the climb fades out over the last `ceiling_fade` metres. Where there is no ground in
  reach under it, it does not climb.
- *Map edge.* The flyable area is the box around the map's parcels. Within `edge_margin` (60 m) the speed towards the edge is held down, to
  nothing at the edge; along it and away from it the helicopter is free. The pilot's prompt says "Edge of the map: turn back".
- *Buildings and trees.* The hull box (the hit volume) and a rotor disc of `rotor_radius` (4.5 m: well inside the 6.7 m blades, on purpose)
  collide with the solid world: ground, buildings, tree trunks, placed solids. Shot-only cover (tree crowns, bushes) does not stop it, as it
  does not stop a car. The helicopter stops against what it hits and slides along it; the speed a surface takes in one tick is the impact,
  and the crash damage rules apply to it (`crash_min_speed_loss`, `crash_damage_per_speed`: full speed into a wall is about a third of its
  hit points). A touchdown under power is always under the threshold. The tail boom and the blade tips beyond the disc do not collide, and
  it does not collide with players or run anyone over. It does bump other vehicles (see "Vehicle against vehicle" in `docs/vehicle-collision-and-entering.md`).
- *Water.* The surface is a floor: it does not sink. While the skids are in a water body it loses `water_damage_per_second` (60) hit points
  a second, and it can lift out again. The cushion treats the surface as ground.

### Amphibious transport

Two spawn on the map (`spawn_count` 2, near a shore where there is one) and it is in the sandbox vehicle list. The model is generated
(`scripts/make_amphibious_model.py`): a boat-shaped hull on four wheels, no roof, no doors, low sides, a propeller under the stern. Three seats
(it had four): the driver front left, an armed seat beside them, and a gunner standing in a ring over the rear. The driver's client predicts it
and the server corrects it (ADR 0010); everyone else, its riders included, sees it interpolated.

**Controls** (the rebindable movement actions; nothing new to bind):

| Input | On its wheels | Afloat |
|---|---|---|
| W / S (`move_forward`, `move_back`) | Accelerate / brake, then reverse | Ahead / astern (there is no brake: astern is how it stops) |
| A / D (`move_left`, `move_right`) | Steer (ramped: a tap is a small correction) | Helm: lazy, and it turns from rest under power |
| Space (`jump`) | Handbrake: stops it and lets the tail slide | Nothing |
| Mouse | Looks round the vehicle; the camera swings round with it on its own | The same |
| E (`interact`), 1-3 (`slot_1`..), H | Get out, switch seat, headlights, as in every vehicle | The same |

**Driving** is `DriveModel` (pure, in ShooterCore), not the car model the other vehicles share. On land it pulls hard from a standstill and eases
off towards top speed, the brake bites at once, and the steering sets a circle that widens with speed and is held to what the tyres grip, so it
is quick at low speed and calm at top speed. In a water body it floats (wherever the water over the ground is at least `float_depth` deep; the
village's ponds are only 0.2 to 0.9 m deep in the terrain, so depth decides no more than that) and handles like a boat: thrust against a drag that grows
with the square of the speed, a lazy helm, a stern that swings out, sideways speed the water takes away slowly. Going in fast, that same drag
checks it hard, and the hull drops to its waterline on a spring and comes up again; coming out, the wheels haul it up the bank over a few
ticks. It lies on the slope under its four wheels; a ledge higher than `step_height` under a wheel is a wall, a drop deeper than `snap_down`
puts it in the air. Walls, trunks and buildings stop the hull box, which is swept half a metre clear of the ground. It does not collide with
players or other vehicles (no vehicle does); it runs people over on land only.

**Handling** is all in `vehicles.json`: `top_speed`, `acceleration`, `brake`, `turn_radius`, `high_speed_radius_factor`, `water_top_speed`,
`water_acceleration`, and the `drive` block (`land`, `water`, `body`, `camera`; each key's summary in `VehicleDriveDef` and its parts says what
turning it up does). `dotnet test ShooterCore.Tests --filter "DriveTests.The_feel_in_numbers" --logger "console;verbosity=detailed"` prints the
resulting times and distances without the game; `DriveTests` holds the limits. With the shipped numbers:

| | Land | Water |
|---|---|---|
| Top speed | 22 m/s (79 km/h), reverse 6.6 m/s | 7 m/s (25 km/h), astern about 5 m/s |
| To 10 m/s / 90 % of top | 1.7 s / 5.2 s | (never 10) / 4.6 s |
| Stop from top speed | brake 27 m in 2.4 s; coasting 109 m | full astern 9 m in 3.1 s; lifting off it still does 3.8 m/s after 2 s |
| Circle | 6 m at 3 m/s, 9 m at 8 m/s, 57 m at top speed (the grip limit) | 12 m at 5.4 m/s, sliding 18 degrees off its heading |
| Going in at top speed | | checked at up to 17 m/s², 12.5 m/s a second after the splash |

**What is drawn** (client only; nothing but the drive record and the players' own records is on the wire). The body's weight is `DriveBody`
(pure, tested): on its wheels the tail squats under throttle, the nose dives under braking and the body leans out of a turn, over wheels that
stay on the ground; afloat the bow rises under power, the hull heels out of a turn and bobs, harder just after it has gone in, settling to a
gentle bob at rest. The wheels roll with the speed and the front pair steer, the propeller turns in the water, a wake trails from the stern
and spray flies from the bow in proportion to the speed (off with reduce motion), it goes in with a splash, and damage smoke comes from the
engine in the stern (`hood`). The driver's camera is the helicopter's spring camera (`drive.camera`), and it swings round with the vehicle
(`turn_follow`, `turn_response`), because here the keys steer. Riders are drawn on their seats on the drawn body, so they lean and bob with it
and stay aboard the vehicle its driver sees in the present; they sit (the crouch pose), the gunner stands, and they do not walk. A rider who
aims (the armed seat, the ring gun) faces where they aim and their weapon is pitched to it.

**Armed seat** (seat 2, ADR 0011). Its rider keeps their own loadout and fights with it from the seat, in first person: fire, reload, aim and
scope, recoil and breath as on foot, the weapon in the view, ordinary kills with that weapon. It is a property of the seat (`"armed": true`),
so the bus's passengers stay unarmed.

- *Changing weapon:* the mouse wheel, as on foot. The number keys stay the seat switch, as in every seat of every vehicle.
- *How far round:* the seat's `aim` arc, in the vehicle's frame: from 60 degrees left of the nose round the right side to 150 degrees, 40
  down, 70 up. The view is held inside it. Not across the driver, not back through the gunner.
- *Spread:* the vehicle's speed widens it as the rider's own speed does on foot: nothing at a standstill, the weapon's whole movement spread
  at `spread_full_speed` (16 m/s). The seat gives no stance bonus. The crosshair shows it.
- *The knife:* as on foot, from the seat, against anyone in reach who is not aboard.

**Ring gun** (seat 3). The vehicle gun and door gun rules (`gun`, `door_gun`; the helicopter's belt and the jeep's round), on a ring: the gun
rides `ring_radius` out from the ring's centre and swings round the gunner, who stands there and turns with it. The gunner's view aims it, all
the way round, 35 down and 55 up; within `guard_arc_deg` (50) of the nose it goes no lower than `guard_min_pitch_deg` (-4), coming down to the
full depression over the next `guard_blend_deg` (20), so the barrel rides up over the two in front (`AmphibiousTransportTests` sweeps every aim
against their bodies and the hull). Its camera is at the model's sight, its aim reaches every client as a turret record, the tracer leaves the
drawn muzzle, the barrel recoils and the muzzle flashes, and the gunner's ammo is event 160: all as the door gun's. Kill-feed weapon
"Mounted MG".

*Who can be shot in their seat.* All three (ADR 0012): each seat is `"exposed": true`. A bullet, a pellet, a rocket or a killstreak round that
reaches a rider's body hits that rider and not the hull, and the hull takes nothing from it; one that misses them hits the hull. The hit box
is the hull only, 1.2 m high, so the riders stand out of it and are the drawn figures: seated ones as tall as a crouch, the gunner standing.
Riders of one vehicle never hit each other, and their own hull is not in their way. A rider who dies frees their seat; if it was the driver,
the vehicle coasts to a stop. Blast was already unprotected for every vehicle (a splash reaches every body in range, through hulls) and is
unchanged; when the vehicle blows up, everyone aboard dies with it.

**Decisions on the edges.**

- *The flak truck and the helicopter.* Their gunner's exposure is unchanged, including that the jeep's roof gun and the killstreak guns do not
  reach them through the hull; those weapons do reach the transport's riders. One to unify later, if wanted.
- *A rider's own rocket.* It passes their vehicle's hull and spares their fellow riders, direct hit and blast, while the shooter still rides
  it. The shooter takes their own blast as on foot, and so does the vehicle.
- *Shallow water.* In a pond shallower than its draft it still floats; its wheels are drawn under the bed.
- *No driver.* It is stepped hands off every tick and coasts, on land or water. A driver whose inputs stop arriving for 6 ticks is driven the
  same way until they resume.

## 7b. Items and equipment (#261)

The shooter has no inventory bag: the weapons are the hotbar, and what a player wears is **Equipment**, one item per equipment slot (GLOSSARY). Today
there is one slot, headgear, and one item, the Bigfoot head. Decisions: ADR 0016.

- **Data.** `items.json` (`ItemsFile`, `ShooterCore/Items`): `id`, `name`, `slot` (`headgear`), `pickup_limit` (leave out for no limit) and the
  effects as plain fields (`headshot_immune`). An item travels on the wire as its position in the list plus one, so new items are appended. A new
  event item is a line in the file, plus a field only when it has a new kind of effect.
- **Bigfoot head.** Worn in place of a helmet. A headshot on its wearer is resolved as a body hit (`HitTarget.HeadGuarded`, read by `HitMath.Raycast`):
  the weapon's body damage, no headshot multiplier, hit marker, kill-cause or bonus XP, for hitscan and shotgun pellets alike. Rockets, blasts
  and the vehicle guns never had a headshot. What a player wears is state, not pose: lag compensation (ADR 0013) rewinds where they stood, not this.
- **Death and the corpse.** At death the worn item comes off and the corpse carries it (`Corpse.Item`), with the pickups already counted, so the
  player respawns bare headed. The corpse keeps its weapon as before; the two are separate takes. The item lives as long as the corpse (120 s). The
  corpse cap evicts the oldest corpse without an item first.
- **Taking it.** Tap the interact key at a corpse that carries an item: the chooser gets a third row, key 3, "Put on the Bigfoot head" (or "..., leaving
  your X" when the slot is full: what was worn goes onto the corpse with its count). Holding the key still takes the weapon only. The prompt names the
  item. Same conditions as the weapon: alive, on foot, in reach.
- **The pickup limit.** The Bigfoot head has `pickup_limit` 3. Every take counts on the item, which keeps its count when it drops again on the next
  corpse, so it can be taken three times over its whole life. The next attempt removes it from the corpse, gives the taker nothing, and shows the
  toast "The item crumbles in your hands". Other items set their own limit or none (no limit: it never crumbles).
- **What lasts.** Nothing is stored (ADR 0003's profile is untouched). A death drops the item on the corpse; leaving the match loses it; a new round
  keeps it on the player (the round's corpses, and any item on them, go); a reconnect starts bare.
- **HUD.** A "Headgear / name" chip sits beside the health bar while an item is worn, and is hidden otherwise. There is no model for the Bigfoot head
  yet, so other players still see the helmet; only the wearer's server-side hit rule and HUD chip change.
- **Granting it for tests.** An admin's `/give <item> [player]` puts a fresh item (no pickups counted) on a living player, or on the admin.
- **Wire (protocol 28).** Event kinds 190 `PickUpCorpseItem` (client to server, u16 corpse), 191 `CorpseItem` (server to everyone: u16 corpse, u8 item or
  0; sent when a corpse that carries one spawns, when it changes, and to a joiner for each corpse that carries one), 192 `Equipment` (server to the
  wearer: u8 count, one u8 item per slot) and 193 `ItemNotice` (server to one player: u8 reason, 1 = crumbled). Codecs: `ItemEvents`.

## 8. Known gaps (collected from the closed tickets)

Not built:
- XP gain toasts (kill, headshot, roadkill, streak kill). The server sends only `LevelUp` and the round summary; a per-kill
  XP event is needed (#143). Left open here (not trivial: a new wire event).
- Perk icon. The HUD shows the perk as a text label (#143); there is no icon asset.
- HUD XP bar; minimap size, opacity and zoom as settings (#119, #143). Enemy minimap dots are fed by UAV pings (#121) and gunfire blips: an enemy's gun shot within `gunfire_blip_radius` (match.json, 150 m) shows a fading diamond for `gunfire_blip_seconds` (1.5 s), one per shooter per `gunfire_blip_interval` (0.25 s). No wire event of its own: every client already receives each Shot and PelletFan with the shooter and origin (rules in `GunfireBlipRules`, tracker `GunfireBlips`). Knife, streak weapons and vehicle guns do not blip; Ghost does not hide it; no protocol bump.
- Scope zoom levels above 1 (#113). Sniper sway reduction (#139): `sway_amount` is read but never applied.
- Concealment wired into the tick, name tags and kill cam; suit visible in first person; tufts do not follow limbs (#140, #141).
- Water: water surface mesh, underwater fog and muffling, bubbles, light shafts, remote swim animation, splash on entry
  for predicted shots (#138, #142). Holes in water bodies are not cut out of the surface (an island in a lake is covered).
- Turret: the knife can hit a turret through a wall; the owner leaving leaves the turret firing (kills still credit the
  departed id) (#123).
- Aircraft: no way out of a turret or gunship except the streak's own end (#125, #126); the owner's body stays shootable
  (#126, #128).
- MOAB: no killcam (none exists in the repo), no MOAB sound (#129).
- Corpse pickup and the care package crate both use E; holding E on a corpse under a falling crate can take the corpse
  first (#124).
- Steady Lung does not apply in water (#138).
- Care package (#151): the reward streaks are not shown in the loadout tracker (only the toast names them). The corpse
  pickup hold still uses its own prompt, not `RadialProgress`. The ring, the toast icon and the crate despawn on open were
  not checked in the running game (the brief forbade it).

Not verified (compiled, not run; the brief for these tickets forbade running the game):
- Shaders: `water_surface.gdshader` and `underwater.gdshader` (shore fade uses `hint_depth_texture`, which needs the Forward+
  or Mobile renderer; the project uses the default renderer), `sky_daynight.gdshader`, `camo_suit.gdshader`,
  `camo_tuft.gdshader`, `gunship_thermal.gdshader`. The first real compile is the first check.
- Layout, scenes and HUD positions: streak panel, minimap texture size, XP strip, breath bar, care package prompt, gunship
  overlay, the aircraft overlays. Visual fit of the jeep and quad models.
- Lamp energy (2.2 peak), headlight cones and offsets, day curve tuning.

ui-work definition of done not met on most UI tickets (#118, #119, #122, #123, #124, #126, #127, #128, #129, #130, #139,
#141, #143). Met by code review only: Esc behaviour, theme tokens, `Strings` keys, every state present. Not done anywhere:
keyboard-only walkthrough, screenshots at 1280x720 and 3840x2160 (the briefs forbade them). Gamepad is not supported in
the game at all, so the gamepad part of the standard cannot be met yet.

Tests and checks that fail or are known to fail:
- `VehicleTests.ShippedFileHasCarMotorcycleAndBicycleWithTheAgreedSpawnCounts` expects 8 cars, the data has 12. It was
  failing on the integration branch before #133 and is left alone.
- `pipeline/tests/test_edits.py` fails on the shipped `edits.json` (reported on #138; unrelated to shooter).
- The smoke and bot scripts were reviewed statically in #133, not run (section 9).

Server and persistence:
- The server does not enable `PRAGMA foreign_keys`, so `ON DELETE CASCADE` never fires (see `docs/shooter-server.md`).
- Guest profiles earn XP in memory only (ADR 0003).
- Persistence writes are fire-and-forget on a background thread; a failed write is only logged.

## 8b. Chat and admin commands

T (rebindable) or "/" opens the chat box (bottom left, bare outlined text with no panel or instruction line; it is hidden until there is a message; the mouse is free and the match reads no keys while it is open). Enter sends, Esc or a click away
closes, Up and Down walk through what was sent this session, Tab completes a command or its argument and cycles through the candidates (Shift+Tab back), and the candidates and the command's
usage are listed above the input. A command that cannot be valid is shown with the reason and not sent. A line is limited to 200 characters and a player to a burst of 5 then about 1 a second.

Commands are for admins. The dev server (`make shooter-server`) passes `--admin-all`, so every player is one (`ADMIN=0` turns it off); never pass it on a release server. Otherwise start the server with `--admin-token TOKEN[,TOKEN...]`, where a token is the `player_token` in the `[connect]` section of the client's settings file (Godot user data
folder, `shooter_settings.cfg`). They run on the server, so they work live: `/bots N` (0 to 64) changes the roster on the next tick (the first time bots are wanted after a start with none, the navigation is
baked once, and the server pauses for that, the admin is told); `/spawn vehicle` puts a vehicle in front of the admin (up to 32 a run, shared with the sandbox's 8; spawned vehicles are not removed);
`/time HH:MM|dawn|morning|noon|dusk|night|midnight|freeze|resume` sets the day clock and sends it to every client at once (event 183); `/restart [ffa|tdm]` starts a fresh round now (scores, lives and world entities reset), in the current match mode or, with an argument, in that one (the teams are regrouped, everyone is told; the mode reaches clients in the next scoreboard). `/event rozhlas` starts the village loudspeaker broadcast on every client now (event 184, no payload; each client starts its own copy at once and the scheduled broadcast waits until it ends); `/give <item> [player]` puts an item of `items.json` on a living player (by name or `#id`) or on the admin, a fresh one with no pickups counted, replacing what was worn in its slot (section 7b); `/help [command]` lists them for anyone.
The grammar, completion and history are pure code in `ShooterCore/Chat` (`ChatCommands`, `ChatCompleter`, `ChatHistory`) with tests in `ChatTests`; `NetServer.Chat.cs` runs the commands, `ChatBox.cs` draws the window.

## 9. Wire protocol

Protocol version: **24** (`Wire.ProtocolVersion`, whose summary lists every bump). The hello carries it; a mismatch is refused with a message naming both
versions. Version 24 (the amphibious transport, ADR 0010 to 0012): snapshot entity type 6 (Drive: a driven vehicle's speeds along and across its
heading and vertically, the slope it stands on, its steering, throttle and flags, `DriveRecord`, ids from 0x5000), the driver's inputs step it
one for one as the pilot's do the helicopter, a turret record for its ring gun, and the server answers a seated player's Fire, Reload and weapon
change when their seat is armed (no new fields for that; the bump is the behaviour change). Version 23 (chat and admin commands): event kinds 180 (a line or command from a player), 181 (a chat log line), 182 (admin flag and spawnable vehicles for a joiner) and 183 (the day clock changed live). Version 21 (the utility helicopter, ADR 0006): snapshot entity type 5 (Flight: a flying vehicle's velocity, pitch, roll, rotor speed and
flags, `FlightRecord`, ids from 0x6000), and the pilot's inputs step the helicopter one for one, so the snapshot's input acknowledgement is the
helicopter's too. Versions 17 to 20: delta snapshots against the acknowledged tick (17), the flak truck's turret record and aircraft hit points
(19), seat switching (20). Version 16 (gunship rocket flight): a gunship rocket flies at its own `projectile_speed_mps` and lives out its range, and
clients take its speed from the Launch velocity (no new fields; the bump is the behaviour change). Version 15 (#161) gives the input a flags byte (bit 0: the gunner's aim follows, a ground point as three int32 quantised
positions, 12 bytes). An input is 12 bytes, or 24 with the aim; the packet is up to 3 inputs. Only a steered turret sends an aim.
Version 14 grows the camo spec to 33 bytes (codec 2: body, skin tone, headgear; migration 0006). Version 13 (#160) adds each tracer's speed (u16 m/s) and length (u16 cm) to GunshipShots (tracer 29 bytes, was 25).
Version 12 (#159) coalesces the gunship FX: the per-round Shot, ImpactFx and Ac130Blast of the turrets are replaced by
GunshipShots (121), GunshipImpacts (122) and GunshipBlasts (123), one per gunner (or per tick) per server tick, and kind 120 is
retired. The Explosion payload grew by a f32 blast radius (21 bytes), so a gunship rocket's 8 m reaches the clients. Layouts are in
`GunshipFxCodec`. Version 11 (#157) cut the GunshipStatus payload to weapon and zoom (2 bytes): the turret has no ammo and no heat. Version 10 (#156) added the hello flags byte (bit 0: sandbox requested), event kinds 150 (SandboxStatus, server to the joining player), 151 (SandboxGrant: hold a streak), 152 (SandboxAction: heal, teleport, vehicle, target respawn, use the held streak), 153 (SandboxState: the [SANDBOX] name tags), 154 (SandboxHeld) and 155 (SandboxNotice). The F4 panel (sandbox_panel, rebindable) is client-side only. Version 9 (#151) retired the care package's PerkGained (kind 51) and added StreakGranted (kind 23). Version 6 was bumped in #133 because the killstreak, aircraft, turret, missile, care package, corpse and MOAB
events and world entity types are new. Version 7 added the target dummies (#148: entity type 3 and event kind 140). Version 8
added the loadout editor's autosave, rename, copy and reset (#145: event kinds 130-132, and `SaveLoadout` carries the name
and the sequence number). Before 6, the last bump was 5 (camo per loadout slot).

Event kinds (`EventKind`, one byte in the event message):

| Value | Kind | Value | Kind |
|---|---|---|---|
| 1 | Spawn | 22 | Appearance (camo) |
| 2 | Shot | 30 | MissileBlast |
| 3 | Hit | 50 | OpenCarePackage (client to server) |
| 4 | Kill | 51 | retired (was PerkGained, protocol 8) |
| 5 | MatchState | 60 | GunshipStatus (kinds 60-69 are the aircraft streaks) |
| 6 | Explosion | 70 | Moab |
| 7 | Death | 90 | UavPing |
| 8 | Debug (client to server) | 110 | HelperWarning (kinds 110-119) |
| 9 | Ammo | 121 | GunshipShots, per gunner per tick (kinds 120-129; 120 retired, #159) |
| | | 122 | GunshipImpacts, per tick |
| | | 123 | GunshipBlasts, per gunner per tick, with hit flags |
| 10 | Launch | | |
| 11 | VehicleExplosion | | |
| 12 | PelletFan | | |
| 13 | WorldSpawn | | |
| 14 | WorldDespawn | | |
| 15 | LevelUp | | |
| 16 | XpSummary | | |
| 17 | StreakEarned | | |
| 18 | StreakUsed | | |
| 19 | StreakEnded | | |
| 20 | PickUpCorpse (client to server) | | |
| 21 | Hotbar | 130 | CopyLoadout (client to server, kinds 130-139) |
| | | 131 | ResetLoadout (client to server) |
| | | 132 | LoadoutAck (server to the editing player) |
| | | 140 | TargetRoster (server to one player on join, kinds 140-149) |
| | | 190 | PickUpCorpseItem (client to server, kinds 190-199 are items and equipment, #261) |
| | | 191 | CorpseItem (server to everyone, and to a joiner) |
| | | 192 | Equipment (server to the wearer) |
| | | 193 | ItemNotice (server to one player) |

Message tags (`Wire.T*`): Hello 1, Welcome 2, Join 3, Leave 4, Event 5, Profile 6, SaveLoadout 7, SelectSlot 8, Error 9,
SaveCamo 10, Input 11, Snapshot 20, WorldSnapshot 21. SaveLoadout is slot, six item strings, name (string), seq (u32 LE).

Special weapon ids (kill-feed `weapon` byte, `SpecialWeapons`): Ac130 244, Helper 245, AttackHelicopter 246, Turret 247,
Missile 248, Moab 249, VehicleGun 250, Vehicle 252, Debug 253, Void 254, Fall 255, Drown 251. Killstreak ids (`StreakWeapons`)
are 244-249.

World entity types (`WorldEntityRegistry`, one byte per entity): corpse 1, attack missile 10, turret 20, care package 30,
aircraft 40. Players and vehicles keep their own snapshot code.

Move buttons (`MoveButtons`, bits in the input message): Jump 1, Sprint 2, Crouch 4, Fire 8, Aim 16, Reload 32, Use 64,
Switch 128, Headlights 256, Prone 512, Killstreak 1024.

Snapshot entity types (`EntityType`, one byte in a snapshot entry): Player 0, Projectile 1, Vehicle 2, Target 3 (the target
dummies, #148: planned on the ground, Defeat pose when down), Turret 4 (the aim of a vehicle's gun, `FlakRecord`), Flight 5 (a flying
vehicle's velocity and attitude, `FlightRecord`), Drive 6 (a driven vehicle's speeds, slope, steering and whether it floats, `DriveRecord`).

Entity state (`EntityState`, one byte): Alive 0, Protected 1, InVehicle 2, Dead 3.

Snapshot flags (`Wire.F*`): PosAbs 1, PosDelta 2, Rot 4, Status 8, Hp 16.

Database migrations (`godot/ShooterServer.Persistence/migrations`, applied in order): 0001 init (players, loadouts, unlocks,
stats, weapon_kills), 0002 roadkills, 0003 camo, 0004 loadout camo, 0005 loadout name (`loadouts.name`, empty for rows
from before #145; the server gives those "Class N" on load).

Data file versions: weapons 2, every other file 1 (`items.json` included).

## 10. Open design questions

Each is a decision for the owner; the code currently follows the first option in each.

1. Crash damage (#134): at top speed a car takes about 304 from one wall hit against 300 hp, and a motorcycle about 240
   against 120 hp. Lower `crash_damage_per_speed` (4 halves every crash) or accept one-hit wrecks?
2. Bicycle run-over (#134): at 8.33 m/s it barely clears `run_over_min_speed` and does about 3 damage. Raise the bicycle's
   speed, or lower the minimum?
3. Hot Streak (#115): it removes one kill from every streak. That is 25 % off the UAV but 4 % off the MOAB. Keep, or make it a
   percentage?
4. MOAB counter (#129): the brief says the counter "resets as on any activation". The glossary and the framework reset on
   death only. Which one?
5. MOAB death (#129): dying cancels the countdown (as the other streaks end on owner death). Or should the blast go off?
6. Vehicle gun (#143 and the correction on #133): its kills count toward the Streak counter now. Confirm.
7. Shotgun range (#112, #133): pellets are 3 degrees wide with a 0.35 m body, so a shotgun is a one-shot kill at 5 m, about
   7 shots at 15 m and 50+ shots at 30 m. Intended, or should the spread widen?
8. Dominated weapons (#133): the M60 is strictly slower than the PKM at every range, the M249 strictly slower than the RPK,
   the Glock strictly slower than the M1911 from 30 m out, and the starter sniper strictly slower than all three others.
   Later unlocks should probably be better at something. Give the late guns a niche, or accept it?
9. Killstreak pacing (#133): MOAB at 24 and AC-130 at 18 need most of a 25-kill round in one life. Keep the counts, or lower
   them, or let the score goal set them?
10. Smoke-arena and debug hotbar (#133): every weapon is on the hotbar when `--debug-cmds` or a smoke arena runs. Acceptable
    for dev servers, or should there be a separate dev-only flag?
11. Protocol 6 (#133): this breaks v5 clients on purpose. Confirm, or keep 5 and accept that a v5 client shows unknown
    events as nothing.
12. Map #104 pointer: the "Decisions so far" line still needs a one-line pointer to the #133 resolution. That map is not
    edited here (per the brief).
