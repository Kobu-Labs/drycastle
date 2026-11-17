# Hunter's Log (#336)

Design source for the Hunter's Log: a server-wide, browsable book of every wild animal a player hunted. Visual reference (mockup, example data): https://claude.ai/artifact/8Jekqn9GcnSdQkngerLQH9

## Scope
- A **Hunt** = one logged kill of a wild Animal that has `weight_kg` in `animals.json`, by a player, any weapon (a run-over too), plus a **Mystery** catch (below). Not pets, livestock, bird flocks, or kills by non-players.
- Persisted forever in the progression DB (Postgres migration; SQLite and in-memory stores too). Server is the only writer.
- No XP, no ranks, no achievements/marks. Records, Life List, announcements only.

## Index species
| Index id | Source animals (`animals.json`) |
|---|---|
| Deer | `deer`, `doe` (merged: one record, one Life List tile) |
| Stag | `stag` |
| Moose, Bear, Boar, Wolf, Fox, Raccoon, Skunk, Rabbit | same id |

The row stores both the index id and the exact species id (and variant, e.g. moose bull). Mapping lives in one table in ShooterCore.

## Mystery creatures
Bigfoot (live: an Event, caught when the Event is won and the player is credited), Unicorn (live: a rare wild animal, caught by killing it, see below), Scylla (todo, no data yet). Registry in ShooterCore so Scylla only needs a catch hook later. No kg (null).
- **Unicorn:** a rare wild animal (`rare` in `wildlife.json`, GLOSSARY "Unicorn"; one on the map at a time, respawns `respawn_seconds` after a kill). It is killed like any animal and goes through `OnWildKilled`: `HuntRecord.ForUnicorn` makes the row (species and exact `unicorn`, variant "", kg null, distance rounded, none for a run over, hit diagram unless run over, any weapon). It holds a first catch and a longest shot like any mystery; redaction of a hidden row drops the name, variant and diagram as before. No announcement of its spawn.
- **Redaction (server-side rule):** a viewer who has not caught creature X is never sent X's name or shape: the server sends a hidden id and the client draws only a "?" (no shape) and a blurred placeholder name. Applies to Life List tiles, Records cards, All hunts (species column, filter, hit card), kill feed / announcements.
- Viewer/owner cases: both caught = full; viewer caught, owner not = real name/outline; viewer not caught, owner caught = hidden, the "?" in the accent; neither = hidden, the "?" dim.
- Hunter, time, distance, weapon stay visible on a hidden row. Hidden species sort last and share one filter option ("Mystery hunts").
- The viewer's first catch unlocks it everywhere at once (old rows, other players' tiles).

## Row (one per hunt)
player, index species, exact species, variant, kg (null for mystery), distance m (null for run over), weapon (id; `run_over` for a vehicle), hit diagram (along, height, head, flank, pose, frame; null when unknown, e.g. run over), position, time. Hit diagram is stored for any hitscan kill that has one (today only sniper kills build it, see `HuntShot`; generalise to every hitscan weapon).

## Records
Per index species: heaviest, longest shot (run overs and null distance excluded). Ties: the earlier hunt keeps it. Mystery species: first catch and longest shot.

## Announcements (server-wide)
- New record (heaviest or longest shot beaten).
- First time: a player's first kill of an index species, or first mystery catch.
- A first kill that is also a record is one line. Ordinary hunts are not announced.
- Mystery lines are per-viewer redacted (blurred name for anyone who has not caught it).
- Shown in the kill feed and a toast for the shooter.

## Queries (client asks, server answers, read-only, paged)
- `hunts(page, size 50, filter species|weapon|player, sort column + direction)`
- `records()`
- `player(id)`: totals, heaviest, longest shot, Life List per species (best kg, longest shot, caught at)
All answers are already redacted for the asking player.

## UI (overlay, not fullscreen)
- Non-pausing panel centred (horizontally and vertically) over about 60% of the screen's width, 85% of its height, on its own top layer above the HUD panels, the scoreboard and the kill feed (the feed stays where it is); the world stays visible and live around it. Like Equipment: mouse released, movement keys work, firing does not. Closes with its key, Esc, death, match end, a turret/missile view, or when the chat box opens (the panel would cover it).
- Rebindable action `hunters_log`, default J. Also reachable from the pause menu. No on-screen control-hint text, no key hints.
- Tabs, in order: **My Log** (default), **Records**, **All hunts**. For another player the first tab reads "<Name>'s Log" and a back button returns to My Log.
- My Log: name/title, tiles (Hunts, Species, Heaviest, Longest), Life List grid (index species + mysteries in one grid). Tile = the animal's silhouette: outline only until hunted, filled once hunted, best kg on one line, longest shot on the next. Hidden mystery tile: the "?" and a blurred placeholder name, no other text (no shape).
- Records: card per species with two pictures: heaviest and longest shot, each the recorded hit (animal facing left, bullet entry marked, HEAD noted), holder, weapon. "All <species> hunts" jumps to All hunts filtered. Unhunted species: outline only.
- All hunts: table, newest first, sortable headers, filters (species, weapon, player), paginated (Prev/Next/numbers). Selecting a row shows its hit card. Player names open that player's log. Own rows tinted.
- Designed loading, empty and error (Retry) states. Empty: the player's own empty My Log shows nothing extra; an empty All hunts table shows only its short title ("No hunts yet"); no hint texts. The hit card is hidden while no row is selected. Everything meets `docs/standards/ui-ux.md` (Esc goes up one level, keyboard + gamepad navigation, theme tokens only, states).
- Silhouettes are the pictures baked in `animal_shapes.json` (the hit diagram's own); species without a baked picture fall back to the body box.

## Out of scope here
The mixed deer herd (deer + doe + 1-3 stags per herd) is a separate change to `wildlife.json` / `WildlifeKeeper.SpawnHerd`.

## Client UI as built (`HuntersLogScreen`)
Same as above, plus:
- Pure logic in ShooterCore (`Hunts/`): `HuntLogNav` (pages, Esc), `HuntLoader`/`HuntLogData` (request ids, stale answers dropped, 10 s give-up, Retry), `HuntTableState` (sort, filters, paging), `HuntFormat`/tile and card models, `HuntPicture` (hit picture), `HuntArtMath` (outline). Specs `HuntersLog.Page`, `HuntersLog.Load` in `docs/state-machines.md`.
- Keys: J (`hunters_log`), pause menu entry, Q/E, bumpers, Page Up/Down change tab, the pad's B and Esc go up one level (another player's log first goes to My Log, then close). The kill feed does not move.
- A Life List tile and a Records "All <species> hunts" button jump to All hunts (that species, heaviest first); a player's log has "All <name>'s hunts". A hidden creature's tile jumps to the "Mystery hunts" filter.
- The player filter lists the players the client has seen in answers so far (the server sends no player list).
- In sandbox nothing is logged (the server writes nothing to the database for a sandbox player): a one-line notice under the tabs, "Sandbox: hunts are not logged", shows on every tab while the player is in sandbox (the client knows it from `SandboxGranted`). The server records as before.
- Everything clickable (rows, sort headers, tiles, buttons, the player-name link in a row) shows a border on hover, plus the focus ring; margins are fixed, so nothing shifts. `ContentButton` (tile, row) takes its minimum size from its content.
- Table rows carry a "Record kg" / "Record shot" pill when the hunt holds that record (read from the Records answer); own rows are tinted and say "(you)".
- A Mystery creature's tile reads "Caught <when>" and its longest shot (no weight). One without a baked picture is a plain body box (an upright one for a Mystery).

