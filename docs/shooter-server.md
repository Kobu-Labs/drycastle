# Shooter: export and dedicated server guide

Releasing to players (client zip, server deploy): see `docs/release.md`.

Status: the export presets and Makefile targets are written but the actual export has NOT been verified
(no Godot export templates were installed when this was written). Treat the first `make shooter-export` as the test.

## 1. Build a map

The shooter loads a map folder produced by the pipeline (default `out/village/map`):

```
make map                     # see docs/pipeline.md; produces out/village/map
```

Exports bundle the full village map (`out/village/map`, see `docs/release.md`) as `map/` next to the binary. Server and clients pick the folder with
`--map DIR`, else `DRYCASTLE_MAP`, else `map/` next to an exported binary (`../../out/village/map` when run from source). Server and clients must use the same map build.

## 2. Export

One-time: install the Godot 4.7.2 **mono** export templates (Editor > Manage Export Templates, or unpack the
`.tpz` into `~/.local/share/godot/export_templates/4.7.2.stable.mono/`). Also needs the .NET SDK.

```
make shooter-export-check   # presets present? templates installed? (exit 1 with a message if not)
make shooter-export         # all three presets into out/shooter/
```

| Preset | Output |
|---|---|
| Windows Client | `out/shooter/windows/drycastle-shooter.exe` (+ `.pck`, .NET data folder) |
| Linux Client | `out/shooter/linux-client/drycastle-shooter.x86_64` |
| Linux Server | `out/shooter/linux-server/drycastle-server.x86_64` (dedicated server export, no rendering resources) |

Presets live in `godot/shooter/export_presets.cfg`. What is packaged where:

- `data/*.json` (match, weapons, vehicles, sounds) is included in the `.pck` and read via `res://data/`.
- Sounds and models are NOT in the pck: the game reads them from the asset library on disk. `make shooter-export`
  copies `assets/` to an `assets/` folder next to each binary. The game looks for it next to the
  binary in an export (`DRYCASTLE_ASSETS=/path/to/assets` overrides; from source it uses the repo's `assets/`). The server needs it too: it validates that every sound file in `sounds.json` exists.
- The map folder is external (see above).

## 3. Start the headless server

```
export DRYCASTLE_ASSETS=$PWD/assets
./drycastle-server.x86_64 --headless -- --role server --port 27015 --map /srv/drycastle/map
```

During development, `make shooter-server MAP=out/village/map` does the same from source.

**Runtime dependency: ffmpeg.** The rozhlas live feed (a player's voice and the combat sounds over the village megaphones) runs each mic-live phase
through an `ffmpeg` subprocess on the server, with the same filter chain as the baked announcement (`godot/shooter/data/rozhlas_voice.json`). `deploy/Dockerfile`
installs it; on a bare machine install it from the package manager (`dnf install ffmpeg`, `apt install ffmpeg`), or point `DRYCASTLE_FFMPEG` at the
executable. Without it the server runs normally: the feed logs `rozhlas feed: cannot start ffmpeg ...` once per phase and stays silent (never unprocessed audio).
The feed uses a fourth ENet channel (3), so the firewall rule for the one UDP port is enough; it runs server to client (frame byte 229), and the set holder's voice runs client to server on the same channel (byte 228, accepted only from the holder while the mic is hijacked). Combat mix: while the mic is live, shots, launches, blasts and deaths inside the Urad volume are mixed into the feed as dry samples of the client's own sound ids; the server decodes the .ogg files of `sounds.json` with ffmpeg in the background (mono 16 kHz, first variant, table volume and pitch; a file that fails to decode is logged once and its sounds are not rebroadcast). The hijack adds a mic thump and a feedback squeal at the kill, rising static over its last 10 s and a click at its end (all synthesised). Admin tests: `/rozhlas feedtest`; `/rozhlas combattest` (a shot, a blast and a death, wherever you stand); `/rozhlas hijack`, then `/rozhlas take` (hold V on your client to talk) and `/rozhlas release` stand in for killing the announcer and holding E at the dictation table until the Urad exists.

Flags (after `--`):

| Flag | Meaning |
|---|---|
| `--role server` | run as the dedicated server (default role is client) |
| `--port N` | UDP listen port, default 27015 |
| `--map DIR` | map folder (else `DRYCASTLE_MAP`) |
| `--db TARGET` | progression database: a SQLite file path (default `./shooter.db`, relative to the working directory), or a PostgreSQL URL `postgres://user:pass@host:5432/db` or Npgsql connection string. Env `DRYCASTLE_DB` is used when the flag is absent (keeps the password off the command line). Created and migrated at start (Postgres: retries for about a minute while the database comes up); the server refuses to start if it cannot be opened |
| `--time HH:MM` | start time of day for the day/night clock; overrides `start_time` in `environment.json` (default `08:00`) |
| `--day-length MIN` | real minutes for one 24-hour day; overrides `day_length_minutes` in `environment.json` (default `24`) |
| `--freeze-time` | hold the clock at its start time (or at `--time`), so the sky does not move |
| `--match-set k=v,k=v` | override match options, see below |
| `--targets N` | plant N stationary target dummies ("Target 01" ...) around the spawn area, 0-64; default 0 from `match.json`. The dummies are not players: they are outside the roster, the player cap, the score goal and the round end, and they respawn 3 s after a kill on the same spot. Planned at start on the ground under the map colliders (rays from above, steep faces rejected; a roof counts as ground where a ray meets one first); no cover or wall spots yet. Wire: entity type 3 in snapshots, event kind 140 (`TargetRoster`) on join. Code: `godot/shooter/Net/NetServer.Targets.cs`. `make shooter-server` passes `TARGETS` (default 24; `make shooter-server TARGETS=0` for none) |
| `--debug-cmds` | honour the client debug kill command and give every player the full weapon list on the hotbar (dev aid); leave off on a public server |
| `--unlock-all` | dev mode: every item unlocked, see the next section. Also on when `DRYCASTLE_UNLOCK_ALL` is `1` or `true`. Leave off on a public server |
| `--allow-sandbox` | grant sandbox mode to clients that ask for it (#156): godmode, unlimited ammo, the whole hotbar, every killstreak on demand (no kill requirement, no cap). A sandbox player's kills and XP are not scored, nothing is written to the database, the round never ends on their kills and they rank last with a `[SANDBOX]` tag. The panel's "Take damage" checkbox turns the godmode off for that player (off by default): they are hurt, die and respawn like anyone, the rest of sandbox mode stays, their death writes no stat and grants no XP, and the HUD badge reads "SANDBOX - TAKING DAMAGE". A server without the flag refuses the request with a notice. `make shooter-server` passes it (`SANDBOX=1` is the default; `SANDBOX=0` for none). Leave off on a public server |
| `--bots N` | N bots (0-64) play while at least one human is in the match (#214); an empty server runs none. A bot is a player without a connection: the server feeds it the input its brain chooses (`BotBrain` in `ShooterCore/Bots`, bridge in `NetServer.Bots.cs`), so movement, weapons, damage, corpses and the kill feed are the player's, and clients need no change. Bots count as players for `min_players`, the score and the round end; they earn no XP and have no stats, and killing one grants no XP and writes no kill stat (the streak counter does advance). They use no vehicles or killstreaks and ignore target dummies. About a third of them carry the RPG as their secondary weapon and fire it at a target in sight with a far steadier aim than their gun (#348). Each bot walks to the nearest other player on a navigation mesh the server bakes from the map colliders (`BotNav.cs`; tree trunks are left out, a bot steers round them). The first start with bots on a map version bakes it (seconds to half a minute) and keeps the tiles in `--nav-cache DIR` (default `navcache` in the Godot user data folder); later starts load them. Every ten seconds the log has the routes answered and their cost, and one status word per bot (walking, advancing, fighting, blocked, ...). They need no peer slot, so they do not count against the 32 player limit. `make shooter-server BOTS=8` |
| `--cluster-spawns` | keep one dense cluster of spawn points (the loss test's cluster). Dev aid for trying bots on the full map, where spawns are otherwise spread over the whole village. `make shooter-server BOTS=8 CLUSTER=1` |
| `--quit-after SECONDS` | end the server with a verdict (testing only) |

### Dev mode: everything unlocked (`--unlock-all`)

For testing features that sit behind levels. With it on, the server:

- counts every weapon (the knife is special and stays out), perk and killstreak as unlocked, for loadout
  validation and for the profile sent at the handshake, so the loadout menu shows nothing locked;
- gives each new or guest player five showcase loadouts (`DevUnlocks.DevKit`: assault rifle, sniper, shotgun, SMG and LMG
  with the big streaks), so every class can be tried at once;
- still runs XP, levels and stats normally;
- never writes the fake unlocks or the showcase loadouts to the database. Stored loadouts are used as they are; a player
  with no stored loadout gets the showcase kit each time they join until they save one of their own.

The client shows "Dev: all unlocked" in the top-right corner of the loadout screen and the pause menu while the server reports it.

Release default: **off**. A server started from an export without the flag or the variable behaves as before.
The Makefile dev targets turn it on: `shooter-server` passes `--unlock-all`, and the `shooter-*-smoke` targets export
`DRYCASTLE_UNLOCK_ALL`. Set `UNLOCK_ALL=0` on the command line to turn it off (`make shooter-server UNLOCK_ALL=0`).
`make shooter-client` has no server flag; the indicator comes from the server it joins. `make shooter-export-check` does not
set it, and nothing in the export does.

The clock flags are read in `Shooter.ServerClock` (`godot/shooter/Shooter.cs`) and resolved by `EnvironmentConfig.ToClock`
(`godot/ShooterCore/Environment/EnvironmentConfig.cs`). The clock is owned by the server and sent to each client in the
welcome message (protocol 3 and later), so clients never need the flags.
Example: `./drycastle-server.x86_64 --headless -- --role server --port 27015 --map /srv/drycastle/map --db /srv/drycastle/shooter.db --time 22:00 --freeze-time`.

### Match options (`--match-set`)

Defaults come from `godot/shooter/data/match.json`; override any of them per server, e.g.
`--match-set score_goal=10,time_limit=300`.

| Key | Default | Meaning |
|---|---|---|
| `score_goal` | 25 | kills to win the round |
| `time_limit` | 600 | round length in seconds |
| `respawn_delay` | 3 | seconds dead before respawn |
| `spawn_protection` | 3 | seconds of invulnerability after spawning |
| `end_screen_time` | 10 | seconds the end-of-round screen shows before the next round |
| `min_spawn_distance` | 40 | metres from other players a spawn point must have |
| `min_players` | 1 | players needed before a round runs |

A bad key or value makes the server refuse to start with an error naming the entry.

### Progression database (`--db`)

The dedicated server keeps profiles, loadouts, unlocks and stats in one SQLite file or one PostgreSQL database (ADR 0003; the Docker deployment in `docs/release.md` uses Postgres, with its schema in `migrations-pg/`). Schema changes are numbered
`.sql` migrations in `godot/ShooterServer.Persistence/migrations/`, applied once each in order at start. Back the file up
like any other data; deleting it wipes every player's progression. Writes run on a background thread and a failed write
is logged to the server output, never thrown into the match. The netcode reads a profile at join (3 s timeout, then a guest profile), applies the active loadout at each spawn (perk, hotbar), writes XP and kill stats, and sends `LevelUp` and round XP summary events. Guests earn XP in memory only.
Players are identified by a client-generated token, not an account (ADR 0003: anyone with the token is that player).

Migrations shipped so far (applied in order, each once, recorded in the database): `0001_init.sql` (players, loadouts,
unlocks, stats, weapon_kills), `0002_roadkills.sql`, `0003_camo.sql` (camo per player), `0004_loadout_camo.sql` (camo moved to
each loadout slot). A new migration is the next number; never edit a shipped one.

Running the database by hand: it is a plain SQLite 3 file, so `sqlite3 shooter.db '.tables'` lists the tables and
`sqlite3 shooter.db '.backup shooter-backup.db'` takes a safe copy while the server runs (WAL mode; the `-wal` and `-shm`
side files belong with the main file, never copy only one). To reset one player, stop the server, then delete their rows
from `loadouts`, `unlocks`, `stats`, `weapon_kills`, `camo` and `loadout_camo` (keyed by `player_id`) and finally from
`players`. The schema declares `ON DELETE CASCADE` and the server switches on `PRAGMA foreign_keys` for its own connections, but
the `sqlite3` shell does not unless you run `PRAGMA foreign_keys=ON;` first, so by hand deleting a `players` row alone
leaves the others behind. To reset everyone, stop the server and
delete the file.

### Data files (`godot/shooter/data/`, packaged into the `.pck` by `export_presets.cfg`)

Every file has a `version` field that is checked at load; a bad value or key stops the server (or the client) with an error
that names the file and the entry. The export presets pack `data/*.json`, and `make shooter-export-check` fails if any of the
files below is missing.

| File | What it holds | Read by |
|---|---|---|
| `weapons.json` (v2) | weapon registry: categories and their recoil defaults, every weapon's damage, falloff, fire interval, magazine, recoil, unlock level | `WeaponData.cs`, `PlayerWeapons` |
| `perks.json` | the eight perks and the stat each one modifies | `PerkData.cs` |
| `levels.json` | XP curve (`xp_to_reach`), XP awards, and the unlock table (weapons, perks and killstreaks by level) | `LevelsData.cs` |
| `killstreaks.json` | streak table (kills, unlock level, duration, control), missile, helper, gunship and AC-130 tuning | `KillstreakData.cs`, `Aircraft` |
| `turret.json` | sentry turret: hp, lifetime, placement, aim and fire numbers | `TurretEntity.cs` |
| `camo.json` | camouflage presets, patterns, tuft (ghillie) settings and the concealment numbers | `CamoSpec.cs`, `CamoEditor.cs` |
| `environment.json` | day/night clock start, day length and the minimum ambient light | `EnvironmentConfig.cs` |
| `vehicles.json` | vehicle types: speed, handling, hp, seats, gun, `lights` blocks, `glass` panes, `spawn_count` (dealt at random) and `fixed_spots` (always at `{x, z, yaw_deg?}`, facing along the nearest road without a yaw) | `VehicleData.cs` |
| `glass.json` | glass panes: hit points, crack stages, blast damage, whether a shattered vehicle pane exposes the riders, the shatter animation (see `docs/glass.md`) | `GlassConfig.cs` |
| `match.json` | round rules (see the match options table above) | `MatchData.cs` |
| `sounds.json` | sound ids and file names (validated against `assets/` by the server) | `SoundData.cs` |
| `rozhlas_voice.json` | the announcement's voice chain (ffmpeg filters in order, their values, the echo tap ranges); read by the bake (`scripts/make_rozhlas.py`) and by the live feed, so they cannot drift | `RozhlasVoice.cs`, `RozhlasFeed.cs` |

Sounds, models and textures are not in the `.pck`; they are read from `assets/` on disk (section 2).
Shaders (`godot/shooter/shaders/*.gdshader`) are compiled by Godot from the project resources, so they are in the export
as long as they are loaded from a literal `res://` path. Water, camo and day/night shaders have not yet been compiled by
a real Godot build (see `docs/shooter-v2.md`).

## 4. Firewall

The server listens on **27015/udp** (or your `--port`). Open it and forward it on the router if the server is behind NAT.

```
sudo firewall-cmd --add-port=27015/udp --permanent && sudo firewall-cmd --reload   # Fedora / firewalld
sudo ufw allow 27015/udp                                                           # Ubuntu / ufw
```

Windows: `New-NetFirewallRule -DisplayName "Drycastle server" -Direction Inbound -Protocol UDP -LocalPort 27015 -Action Allow`.

## 5. Join

- Connect screen: start the client, Play, enter address, port (27015) and name, Connect.
- Straight in: `drycastle-shooter --connect host:port --name NAME` (port optional, default 27015).

The client needs the same `--map`/`DRYCASTLE_MAP` and `DRYCASTLE_ASSETS` as described above.

## Known gaps

- Export is unverified (see the top). If the dedicated-server preset needs tuning, edit it in the Godot editor
  (Project > Export) and commit the resulting `export_presets.cfg`.
- No automatic asset or map download: clients need the map folder installed by hand.

## Performance log

Every 10 s the server prints one `server: stats` line (tick p50/p99, the phases, GC) and, since the `Prof` timers, these `key=value` lines
(`NetServer.Prof.cs`, `ShooterCore/Diagnostics/Prof.cs`). The tick lines are only printed while somebody plays; the memory line always.

| Line | Meaning |
|---|---|
| `server: prof scopes ms/s\|calls/s\|kb/s name=A\|B\|C ...` | each timed part of the tick: A milliseconds of it per second of play (10 ms/s is 1 % of a core), B calls per second, C kilobytes the game thread allocated in it per second. Scopes nest: `entities` holds `animal_step`, which holds `animal_body_ahead`, which holds `animal_crowd`; `fire` holds `shot_targets`; `bot_brain` holds `bot_sight`, which holds `world_shot`. They do not sum to the tick |
| `server: prof worst tick=N ms=X gc0=.. gc_pause_ms=.. ticks_with_gc=.. worst_without_gc_ms=.. phases_us ... \| scopes_us ...` | the slowest tick of the period: the collections and the GC pause that fell in it, its phases and scopes in microseconds (those under 50 us left out); and for the period, how many ticks had a collection and the slowest tick that had none. Says what a hitch was made of, and whether it was the collector |
| `server: prof alloc kb/s poll=.. match=.. ...` | kilobytes the game thread allocated per second in each phase of the tick |
| `server: prof nav ms/s\|calls/s snap=A\|B path=A\|B` | the bot navigation thread: snapping points to the mesh, and the path searches. With `DRYCASTLE_NAV_COMPARE=1` in the environment (measurement only) every fourth route is also asked without the snaps: `raw=A\|B raw_same=N raw_diff=N raw_maxdiff_m=X` |
| `server: prof threads cpu_pct main=.. bot_nav=.. total=..` | CPU of each kind of thread in percent of one core (from `/proc/self/task`; Linux only) |
| `server: prof mem rss_mb=.. heap_mb=.. gc_committed_mb=.. objects=.. nodes=.. orphans=.. rays_s=.. live_targets_s=..` | process memory, the managed heap, the engine's object and node counts (a count that only grows is a leak), physics rays and target lists per second. `godot_static_mb` is 0 in a release export (the engine does not count it there) |

A new scope: a value in `Prof.S` with its name in `Prof.Names`, then `var t = Prof.Begin(); ...; Prof.End(Prof.S.X, t);` around the code (game thread only).

Function-level profile on the VPS (`linux-perf` is installed): start the server with `DOTNET_PerfMapEnabled=1` and `DOTNET_EnableWriteXorExecute=0`
in its environment (a compose override; one is kept in `/opt/drycastle/backups/pre-metrics-20261117/perf-test.override.yml`), then on the host
`perf record -e cpu-clock -F 499 -g -p <server pid> -- sleep 60`. The profiler itself slows the server (the navigation worker's time doubled), so read the
`prof` lines from a period without it.
