# Events: work in progress

Status note for the events work. Not a spec: the design of the first event is in the tickets, the idea list is in `README.md` next to this file.
Written 2026-11-13 on branch `events` (worktree `.claude/worktrees/rozhlas`); the #264 rows updated 2026-11-14 on branch `bigfoot-event`.

## Where the work lives

Branch `events` = `main` + `rozhlas` (fast-forwarded, they are the same commit `36c4641`) + the three implemented tickets merged in. Nothing is pushed, no PR exists.

| Ticket | State | Original branch (local, kept) | Notes |
|---|---|---|---|
| #260 Layered ground types with attributes | implemented, merged into `events` | `ground-attributes` | ADR 0015. Pipeline output verified byte-identical. Editor UI compiles, **never run** |
| #261 Inventory: headgear slot, Bigfoot head, pickup limit | implemented, merged | `inventory-headgear` | ADR 0016, protocol 28, `/give`. Game **never run** |
| #262 Intel room and NPC announcements | implemented, merged | `intel-room` | ADR 0017, protocol 29, `/event intel`. Needs `make export`; game **never run**. No NPC built |
| #263 Runtime attribute areas: baked candidate points | implemented, branch not merged | `event-spawn-points` | option B: `attribute_points.json`, `NetServer.PickEventSpawn("bigfoot")`. Game **never run** |
| #264 Bigfoot event | implemented, on its own branch (not merged into `events`) | `bigfoot-event` | `docs/shooter-v2.md` 7c, protocol 32, `bigfoot.json`. Builds, `ShooterCore.Tests` pass. Game **never run** |
| #266 to #269 Worn items drawn, bag, ground items, Equipment screen (F), corpse chooser list | implemented, merged | `items-worn-render`, `items-bag-ground`, `equipment-screen` | ADR 0018, protocol 31. Game **never run** |
| #265 Private asset pack (VPS download, launcher password) | not started | | independent |

The merge renumbered the ADRs (0015 ground attributes, 0016 worn items, 0017 intel room) and moved the intel work to protocol 29.
Verified after the merge: `ShooterCore.Tests` 1809 pass, `shooter.csproj` and `viewer.csproj` build. **Not** run on the merged result: the pipeline tests, `make map`, the game, the editor.

## Design decisions (from the grilling session)

Bigfoot (first event, ticket #264):
- Placeholder model: `~/downloads/Giant by Quaternius - BldaiPtyJa.glb` (rigged, 12 joints; Attack, Death, HitRecieve, Idle, Jump, Run, Walk). Scaled to about twice player height. Stomp, throw and grab reuse the one Attack clip plus procedural motion until the real model exists.
- A new world entity with a server-side brain, not a bot.
- Damage model is a **hit count**, not HP: dies after N hits, N scales with participating players, tuned for about 1 minute. Every bullet hit counts 1, no headshot bonus; explosive and vehicle-weapon hits count a configurable larger weight. Ramming it does nothing. Numbers live in a JSON data file.
- Three phases at hit-count thresholds plus an enrage near the end: (1) trunk throws and chase, (2) adds stomp shockwave (jump avoids it) and vehicle throws (scripted arc, no damage), (3) adds grab, flail, launch at another player.
- All damage Bigfoot deals to players is 2 to 5.
- Movement: ignores the navmesh, straight-line chase snapped to terrain, tramples trees, nearest-player target with a sticky target. Decided 2026-11-13: trees are ignored (it walks through and tramples them), buildings are solid.
- Wipe: if every participant dies it heals and waits. Despawns after X minutes with no player nearby, no reward.
- Started with `/event bigfoot`, ended with `/event current end`. **No scheduler and no announcement in this ticket** (both postponed). Reward is the footsteps to a corpse carrying the Bigfoot head, plus XP. The event waits for the head (#261), which is done.

Spawn areas (tickets #260, #263):
- A ground type is a bundle of attributes (Appearance, Spawning, Water, Events). Areas stack; the topmost type with a terrain attribute decides the look, other attributes add up. First events attribute: `event_spawn: bigfoot`.
- **Decided (#263):** option B. The pipeline bakes candidate points per runtime attribute (allowlist `runtime_points` in `rules.yaml`, first `event_spawn: bigfoot`) into `attribute_points.json`; the server picks one with `NetServer.PickEventSpawn`. Count is a density rule in data; only buildings and water need clearance (Bigfoot tramples trees). See `docs/pipeline.md`, "Runtime attributes".
- #260 stores `spawns` and `event_spawn` of stacked layers (`ground_layers` in `parcels.json`). #263 resolves `event_spawn`; a `spawns` carried by a layer is still stored and unused (the props stage reads the ground area's type only).

Private assets (ticket #265):
- Paid packs (Synty) must never enter this **public** repo or public GitHub Releases. Plan: gitignored `assets-private/`, a CI guard against tracking it, a pack published to the VPS and downloaded by the launcher with a password, a fallback to the CC0 placeholder models.
- **Open:** one shared password or per-friend keys (recommended: per-friend), a domain name for HTTPS, an encrypted `.pck`, and a question to Synty support about giving files to friends without their own licences.

## Bigfoot (#264): what is built, what is open

Built on `bigfoot-event` (cut from `events`): `/event bigfoot`, `/event current end|cancel`, the lifecycle (`EventRun`, `EventBook`), the Bigfoot as a world entity with its brain
(hit count, three phases and an enrage, logs, stomp and shockwave, vehicle throw, grab and launch, wipe, despawn), the shots reaching it through the hit pipeline (a scaled
`HitTarget`, rewound), the reward (XP, footsteps, a corpse that carries the head), the client's giant (placeholder Quaternius model, procedural motion on the Attack clip),
`bigfoot.json`. No scheduler, no announcement beyond a system chat line, no HUD.
Choices where the ticket was silent are in `docs/shooter-v2.md` 7c and `data/bigfoot.json`; the main ones: participants are the living players within 90 m and the count only
goes up; credit is 3 hits or being near at the kill; the body lies 8 s and then the footsteps (about 120 m, wandering) lead to a corpse that lasts 15 minutes; only ground
vehicles are thrown; a launched player's fall damage is replaced by 2-5; the dev fallback spawn is 55-85 m from a random player.
Open:
- **An area with `event spawn: bigfoot`** must be drawn (the village has none): until then every start is the fallback.
- **Intel board:** `AnnounceIntel` / `WithdrawIntel` is not called by the Bigfoot (the ticket says no announcement). It is one call each at start and end.
- **Sounds:** no roar, no stomp, no throw sound exists; `NetClient.Bigfoot.cs` `BigfootSounds` reuses the closest ones.
- **A boss bar or any HUD** for the hit count or the phase: none (the hit marker and the roar are the feedback).
- **The final model and animations** replace the placeholder (a clean swap is `NetClient.Bigfoot.cs` and `bigfoot.json`); the Giant's licence is unverified (`assets/quaternius-giant/NOTICE.txt`).
- **Launched player's client** does not know the launch velocity (it reconciles to the server, as for a blast's knockback); a better hand-off would send it in `BigfootFx`.
- Tuning: every number in `bigfoot.json` is a first guess (the fight is meant to take about a minute: 300 hits per participant).
- The corpse and its footsteps are not on the minimap or the tactical map.

## To check by hand (no agent ran the game)

1. Editor, #260: Ground types tool (attribute groups, Create layer), Regions page (ground stack, Up/Down), the overlay (`V` toggles it). Needs screenshots at 1280x720 and 3840x2160 and a keyboard-only pass, see `docs/ground-types-ui-notes.md` "Not verified".
2. `make export`, then as admin `/event intel`: toast and chat line, then the bunker `w378900206` (about (-406, 111) local metres), down three ladders to the screen. Also `make shooter-intel-shots DIR=out/intel`.
3. `/give bigfoot_head`, then check headshots become body hits, death drops it on the corpse, the third pickup still works and the fourth crumbles.
4. Bigfoot (#264), as admin on a server with `--bots` and a client or two: `/event bigfoot` (the log says FALLBACK), then the giant is the right size, faces its way of walking, and its clips play; shoot it (the marker shows, headshots no extra, a rocket counts a lot); watch the phases (a roar), the stomp telegraph and ring (jump it), logs, standing a car beside it (it throws it, the driver is not rubber-banded for the whole flight), a grab and launch (the held player's view, the landing), die to it as the only player (it heals and waits), `/event current end` (chat line, XP in the end-of-round summary, 8 s later the footsteps, follow them to the corpse, take the head from it), `/event current cancel`.
5. `make test` (pipeline) and a `make map` on `events` to confirm the byte-identical result still holds with the newest `main` commits.

## Not built yet

- An NPC announcement, intel-screen browsing, a base marker on the minimap, new rozhlas audio saying "suspicious activity" (re-record `announcement.*`, run `make rozhlas`).
- The event scheduler, and the other events in `README.md`.

## Housekeeping

- Add the `ui` label to issue #261. Close #260, #261, #262 with a comment once merged to `main`.
- Leftover scratch worktrees from the #260 comparison (`baseline`, `cand`, `e1`, `e2`, `e3`) sit in the old session's scratchpad under `/tmp/claude-1000/`. Remove them with `git worktree remove --force <path>` after deleting their `data/village/raw` and `data/village/ortho` symlinks (or `git worktree prune` once `/tmp` is cleared).
- The source branches `ground-attributes`, `inventory-headgear`, `intel-room` and their worktrees under `.claude/worktrees/agent-*` are kept; delete them once `events` is merged.
