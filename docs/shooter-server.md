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
| `--match-set k=v,k=v` | override match options, see below |
| `--debug-cmds` | honour the client debug kill command; leave off on a public server |
| `--quit-after SECONDS` | end the server with a verdict (testing only) |

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
