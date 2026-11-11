# Shooter: export and dedicated server guide

Status: the export presets and Makefile targets are written but the actual export has NOT been verified
(no Godot export templates were installed when this was written). Treat the first `make shooter-export` as the test.

## 1. Build a map

The shooter loads a map folder produced by the pipeline (default `out/map`):

```
make map AREA=<area>        # see docs/pipeline.md; produces out/map
```

The map is NOT bundled into any export. Server and clients each pick the folder at start with
`--map DIR` or the `SUCHOHRAD_MAP` environment variable (default `../../out/map` relative to the project, which does
not exist next to an exported binary). Server and clients must use the same map build.

## 2. Export

One-time: install the Godot 4.7.2 **mono** export templates (Editor > Manage Export Templates, or unpack the
`.tpz` into `~/.local/share/godot/export_templates/4.7.2.stable.mono/`). Also needs the .NET SDK.

```
make shooter-export-check   # presets present? templates installed? (exit 1 with a message if not)
make shooter-export         # all three presets into out/shooter/
```

| Preset | Output |
|---|---|
| Windows Client | `out/shooter/windows/suchohrad-shooter.exe` (+ `.pck`, .NET data folder) |
| Linux Client | `out/shooter/linux-client/suchohrad-shooter.x86_64` |
| Linux Server | `out/shooter/linux-server/suchohrad-server.x86_64` (dedicated server export, no rendering resources) |

Presets live in `godot/shooter/export_presets.cfg`. What is packaged where:

- `data/*.json` (match, weapons, vehicles, sounds) is included in the `.pck` and read via `res://data/`.
- Sounds and models are NOT in the pck: the game reads them from the asset library on disk. `make shooter-export`
  copies `assets/` to an `assets/` folder next to each binary. Point the game at it:
  `SUCHOHRAD_ASSETS=/path/to/assets` (otherwise it looks for `../../assets` relative to the binary, which is wrong
  for an export). The server needs it too: it validates that every sound file in `sounds.json` exists.
- The map folder is external (see above).

## 3. Start the headless server

```
export SUCHOHRAD_ASSETS=$PWD/assets
./suchohrad-server.x86_64 --headless -- --role server --port 27015 --map /srv/suchohrad/map
```

During development, `make shooter-server MAP=out/map` does the same from source.

Flags (after `--`):

| Flag | Meaning |
|---|---|
| `--role server` | run as the dedicated server (default role is client) |
| `--port N` | UDP listen port, default 27015 |
| `--map DIR` | map folder (else `SUCHOHRAD_MAP`) |
| `--db PATH` | progression database file, default `./shooter.db` (relative to the working directory); created and migrated at start, the server refuses to start if it cannot be opened |
| `--time HH:MM` | start time of day for the day/night clock; overrides `start_time` in `environment.json` (default `08:00`) |
| `--day-length MIN` | real minutes for one 24-hour day; overrides `day_length_minutes` in `environment.json` (default `24`) |
| `--freeze-time` | hold the clock at its start time (or at `--time`), so the sky does not move |
| `--match-set k=v,k=v` | override match options, see below |
| `--debug-cmds` | honour the client debug kill command and give every player the full weapon list on the hotbar (dev aid); leave off on a public server |
| `--unlock-all` | dev mode: every item unlocked, see the next section. Also on when `SUCHOHRAD_UNLOCK_ALL` is `1` or `true`. Leave off on a public server |
| `--quit-after SECONDS` | end the server with a verdict (testing only) |

### Dev mode: everything unlocked (`--unlock-all`)

For testing features that sit behind levels. With it on, the server:

- counts every weapon (the bazooka and the knife are special and stay out), perk and killstreak as unlocked, for loadout
  validation and for the profile sent at the handshake, so the loadout menu shows nothing locked;
- gives each new or guest player five showcase loadouts (`DevUnlocks.DevKit`: assault rifle, sniper, shotgun, SMG and LMG
  with the big streaks), so every class can be tried at once;
- still runs XP, levels and stats normally;
- never writes the fake unlocks or the showcase loadouts to the database. Stored loadouts are used as they are; a player
  with no stored loadout gets the showcase kit each time they join until they save one of their own.

The client shows "Dev: all unlocked" in the top-right corner of the loadout screen and the pause menu while the server reports it.

Release default: **off**. A server started from an export without the flag or the variable behaves as before.
The Makefile dev targets turn it on: `shooter-server` passes `--unlock-all`, and the `shooter-*-smoke` targets export
`SUCHOHRAD_UNLOCK_ALL`. Set `UNLOCK_ALL=0` on the command line to turn it off (`make shooter-server UNLOCK_ALL=0`).
`make shooter-client` has no server flag; the indicator comes from the server it joins. `make shooter-export-check` does not
set it, and nothing in the export does.

The clock flags are read in `Shooter.ServerClock` (`godot/shooter/Shooter.cs`) and resolved by `EnvironmentConfig.ToClock`
(`godot/ShooterCore/Environment/EnvironmentConfig.cs`). The clock is owned by the server and sent to each client in the
welcome message (protocol 3 and later), so clients never need the flags.
Example: `./suchohrad-server.x86_64 --headless -- --role server --port 27015 --map /srv/suchohrad/map --db /srv/suchohrad/shooter.db --time 22:00 --freeze-time`.

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

The dedicated server keeps profiles, loadouts, unlocks and stats in one SQLite file (ADR 0003). Schema changes are numbered
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
`players`. The schema declares `ON DELETE CASCADE`, but the server does not switch on `PRAGMA foreign_keys`, so deleting a
`players` row alone leaves the others behind (see `docs/shooter-v2.md`, known gaps). To reset everyone, stop the server and
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
| `vehicles.json` | vehicle types: speed, handling, hp, seats, gun, `lights` blocks | `VehicleData.cs` |
| `match.json` | round rules (see the match options table above) | `MatchData.cs` |
| `sounds.json` | sound ids and file names (validated against `assets/` by the server) | `SoundData.cs` |

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

Windows: `New-NetFirewallRule -DisplayName "Suchohrad server" -Direction Inbound -Protocol UDP -LocalPort 27015 -Action Allow`.

## 5. Join

- Connect screen: start the client, Play, enter address, port (27015) and name, Connect.
- Straight in: `suchohrad-shooter --connect host:port --name NAME` (port optional, default 27015).

The client needs the same `--map`/`SUCHOHRAD_MAP` and `SUCHOHRAD_ASSETS` as described above.

## Known gaps

- Export is unverified (see the top). If the dedicated-server preset needs tuning, edit it in the Godot editor
  (Project > Export) and commit the resulting `export_presets.cfg`.
- No automatic asset or map download: clients need the map folder installed by hand.
