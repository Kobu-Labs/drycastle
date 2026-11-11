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
  weapon into the primary or the secondary position.
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

Care package perk table (killstreak, #124): Fleet Foot 22, Quick Hands 22, Swift Swap 16, Flak Jacket 14, Steady Lung 10,
Tank Hunter 8, Ghost 5, Hot Streak 3 (weights out of 100). A care-package perk lasts until death. The HUD strip shows the
loadout perk, not the care-package perk (#143).

## 4. Killstreaks

Data: `godot/shooter/data/killstreaks.json`. The Streak counter counts the owner's kills (not suicides), resets on death,
and kills made with a streak's own weapon do not advance it (`StreakWeapons`). The jeep's roof gun (vehicle gun, 250) is
not a streak weapon, so its kills do count (correction on #133).

| Streak | Kills | Unlock | Duration | Control | Notes |
|---|---|---|---|---|---|
| UAV | 4 | 0 | 60 s | none | enemy pings every second to the owner; no visible UAV model |
| Attack Missile | 5 | 4 | flies 20 s | POV | steered missile, 100 dmg, splash 12 m |
| Sentry Turret | 6 | 5 | 15 s | none | placed on the ground, 500 hp, 12 dmg per shot |
| Care Package | 6 | 6 | instant | none | drop lands, E opens it, grants a perk until death |
| Attack Helicopter | 10 | 9 | 45 s | POV | owner flies the helicopter (POV) for 45 s |
| Helicopter Helper | 15 | 11 | 60 s | none | AI helicopter, bursts at enemies with a red-dot warning |
| AC-130 | 18 | 14 | 40 s | POV | circling gunship, 25 mm / 40 mm / 105 mm |
| MOAB | 24 | 20 | 5 s countdown | none | 5 s countdown, then a blast that kills everyone in range |

Shared aircraft code: `Aircraft` (circling path, replication, shared gunship control, #125).

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

- `camo.json` (version 1): suit presets, face paint patterns, vegetation looks, tuft settings and the concealment numbers
  (`CamoSpec`, codec version 1, 30 bytes on the wire).
- Customiser UI (`CustomizationScreen.cs`, `CamoPreview.cs`) edits the look per loadout slot (#141).
- Prone stance (#139): a prone latch toggled by the prone key (a move button bit); crouch now has a transition time.
- Concealment is a number only; it is not applied in the tick, the name tags or the kill cam (section 8).

## 7. Vehicles and the world

- Vehicles (`vehicles.json`): car, motorcycle, bicycle (v1), war jeep and quad bike (#131), boat and amphibious transport
  (#132). Handling was reworked in #134 (max speed, turn radius, braking, hp). Headlights and tail lights come from the
  optional `lights` block (#137).
- Water bodies (#138, #142): the pipeline writes `water.json` into the map artifact; the server and clients swim, submerge
  and hold breath (HUD "AIR"). Surfaces, underwater tint and splashes are client-side and not yet compiled by Godot.
- Day/night (#135): one server clock, `environment.json` (start time 08:00, day length 24 minutes by default). The sun,
  moon, sky shader and ambient light follow it. Street lamps (#136) light up at night from `lamps.json`.
- Sentry turret, attack missile, MOAB, care packages and aircraft are world entities (section 9).

## 8. Known gaps (collected from the closed tickets)

Not built:
- XP gain toasts (kill, headshot, roadkill, streak kill). The server sends only `LevelUp` and the round summary; a per-kill
  XP event is needed (#143). Left open here (not trivial: a new wire event).
- Perk icon. The HUD shows the perk as a text label (#143); there is no icon asset.
- HUD XP bar; minimap size, opacity and zoom as settings (#119, #143). Enemy minimap dots are fed only by UAV pings (#121).
- Scope zoom levels above 1 (#113). Sniper sway reduction (#139): `sway_amount` is read but never applied.
- Concealment wired into the tick, name tags and kill cam; suit visible in first person; vegetation index changes nothing
  (only one tuft card ships); tufts do not follow limbs (#140, #141).
- Water: water surface mesh, underwater fog and muffling, bubbles, light shafts, remote swim animation, splash on entry
  for predicted shots (#138, #142). Holes in water bodies are not cut out of the surface (an island in a lake is covered).
- Boats and amphibious vehicles are server-only visually (primitive boxes, no CC0 model in `assets/manifest.json`).
- Turret: the knife can hit a turret through a wall; the owner leaving leaves the turret firing (kills still credit the
  departed id) (#123).
- Aircraft: no way out of a turret or gunship except the streak's own end (#125, #126); the owner's body stays shootable
  (#126, #128).
- MOAB: no killcam (none exists in the repo), no MOAB sound (#129).
- Corpse pickup and the care package crate both use E; holding E on a corpse under a falling crate can take the corpse
  first (#124).
- Steady Lung does not apply in water (#138).

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

## 9. Wire protocol

Protocol version: **8** (`Wire.ProtocolVersion`). The hello carries it; a mismatch is refused with a message naming both
versions. Version 6 was bumped in #133 because the killstreak, aircraft, turret, missile, care package, corpse and MOAB
events and world entity types are new. Version 7 added the target dummies (#148: entity type 3 and event kind 140). Version 8
added the loadout editor's autosave, rename, copy and reset (#145: event kinds 130-132, and `SaveLoadout` carries the name
and the sequence number). Before 6, the last bump was 5 (camo per loadout slot).

Event kinds (`EventKind`, one byte in the event message):

| Value | Kind | Value | Kind |
|---|---|---|---|
| 1 | Spawn | 22 | Appearance (camo) |
| 2 | Shot | 30 | MissileBlast |
| 3 | Hit | 50 | OpenCarePackage (client to server) |
| 4 | Kill | 51 | PerkGained |
| 5 | MatchState | 60 | GunshipStatus (kinds 60-69 are the aircraft streaks) |
| 6 | Explosion | 70 | Moab |
| 7 | Death | 90 | UavPing |
| 8 | Debug (client to server) | 110 | HelperWarning (kinds 110-119) |
| 9 | Ammo | 120 | Ac130Blast (kinds 120-129) |
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
dummies, #148: planned on the ground, Defeat pose when down).

Entity state (`EntityState`, one byte): Alive 0, Protected 1, InVehicle 2, Dead 3.

Snapshot flags (`Wire.F*`): PosAbs 1, PosDelta 2, Rot 4, Status 8, Hp 16.

Database migrations (`godot/ShooterServer.Persistence/migrations`, applied in order): 0001 init (players, loadouts, unlocks,
stats, weapon_kills), 0002 roadkills, 0003 camo, 0004 loadout camo, 0005 loadout name (`loadouts.name`, empty for rows
from before #145; the server gives those "Class N" on load).

Data file versions: weapons 2, every other file 1.

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
