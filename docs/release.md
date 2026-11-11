# Releasing the shooter

Players normally get builds through the launcher and CI releases: see `docs/launcher.md`. This page is the manual path (and the server deploy).

How to ship a build to players and run a public server. Details of the server flags are in `docs/shooter-server.md`.
Status: `make shooter-package` builds the Windows zip (79 MB) and the Linux server starts from the export layout. A run of the Windows client itself is still untested (see "Still unverified").

## What ships

| Piece | Who runs it | Where |
|---|---|---|
| Windows client zip (`suchohrad-windows.zip`) | players | built by `make shooter-package` |
| Linux dedicated server | you | a host with a public **UDP** port (default 27015) |

The transport is ENet, which is UDP. Railway and similar HTTP/TCP-only hosts cannot expose it; use a VPS (Hetzner,
DigitalOcean) or Fly.io (UDP supported).
The server address is not baked into the client: players type it on the connect screen, which remembers it.

## One-time setup

- Godot 4.7.2 **mono** export templates (Editor > Manage Export Templates, or unpack
  `Godot_v4.7.2-stable_mono_export_templates.tpz` from github.com/godotengine/godot-builds into
  `~/.local/share/godot/export_templates/4.7.2.stable.mono/`), and the .NET SDK.
- `godot/shooter/shooter.sln` must exist (it is committed): the mono export refuses to pack C# without it.
- `zip` on the build machine.
- `make shooter-export-check` verifies presets and templates.

## Cutting a release

1. Make sure `main` is green: `make shooter-test` (and `make test` for the pipeline if the map changed).
2. Build the full map if it changed: `make map AREA=village`. Every export bundles `out/village/map` as `map/` next to the binary (override with `SHOOTER_PKG_MAP=<dir>`), and `make shooter-export` stops if it is missing. The server and the clients must run the **same map build**, which the bundling guarantees as long as you deploy the server from the same export.
3. Build the client package: `make shooter-package`.
   Output: `out/shooter/suchohrad-windows.zip`, containing the exe, `.pck`, .NET data, `assets/`, `map/`, `README.txt`
   (from `docs/player-readme.txt`). `make shooter-export` also produces the Linux client and server in `out/shooter/`.
4. Check it on a clean machine or folder: unzip, double-click `suchohrad-shooter.exe`, connect to the server.
5. Publish the zip (GitHub Release on Kobu-Labs/drycastle, tagged `vX.Y.Z`, is the default) and send the link.
   Tell players about the SmartScreen prompt: "More info", then "Run anyway" (the exe is unsigned).

## Deploying the server (Docker, with PostgreSQL)

The server runs in Docker next to a PostgreSQL container (`deploy/`). Progression lives in Postgres (volume `pgdata`); the game port is
UDP 27015; Postgres is reachable only from the server container.

One-time on the VPS: install Docker with the compose plugin and `rsync` (Debian 13: `apt-get install docker.io docker-compose rsync`); open **UDP 27015**
in the host and provider firewalls (and SSH). Nothing else is exposed (Postgres is not published).

1. `make shooter-export` (bundles the server binary, `assets/` and the village `map/`).
2. `SSH_OPTS="-i ~/.ssh/suchohrad-vps -o IdentitiesOnly=yes" deploy/deploy.sh user@host` (SSH_OPTS only if you use a dedicated key) copies the export and `deploy/` to `/opt/suchohrad` on the VPS (its `rsync --delete` only touches
   that export copy), creates `deploy/.env` with a random database password on the first run, and runs `docker compose up -d --build`.
3. Check: `ssh user@host 'cd /opt/suchohrad/deploy && docker compose logs server'` should show
   `server: progression db postgres shooter@db:5432/shooter` and the map line.

The server flags are the compose `command:` (see `docs/shooter-server.md`); keep `--unlock-all`, `--allow-sandbox` and `--debug-cmds` off.
The database URL comes from `SUCHOHRAD_DB`, so the password is never on a command line. `GAME_PORT` in `.env` changes the host port.

- **Backups:** `docker compose exec -T db pg_dump -U shooter shooter > shooter-$(date +%F).sql` (cron it). Deleting the `pgdata` volume
  (`docker compose down -v`) wipes all progression.
- **Restore:** `docker compose exec -T db psql -U shooter shooter < shooter-DATE.sql` into an empty database.
- The wire protocol version must match between server and client. A new protocol means a new client zip and a redeploy together.
- **Without Docker/Postgres:** the server also accepts `--db ./shooter.db` (SQLite) as before.

### Current production server

netcup VPS 500 G12.5 (Vienna, Debian 13, 2 vCores, 4 GB), `root@159.195.244.164`, key `~/.ssh/suchohrad-vps`, stack in `/opt/suchohrad/deploy`.
Players connect to `159.195.244.164`, port 27015. Check it from anywhere with the headless bot:
`godot --headless --path godot/shooter -- --connect 159.195.244.164 --name probe --map out/village/map --headless-bot --bot-seconds 8`
(it ends with `SHOOTER_NET_CLIENT_FAIL` when alone on the server; "welcome" and "snapshots N" in its output are the success signs).

## Updating

- Server-only change (balance in `data/*.json` is in the `.pck`, so it is a client change too): rebuild, redeploy, restart.
  Back up the database first (see above); migrations apply at start.
- Any client-visible change, new map build, or protocol bump: repeat "Cutting a release" and redeploy the server.

## Still unverified

- The Windows client has not been run from an unzipped folder (assets and map found next to the exe).
- The exported Linux server prints `ERROR: Pages in use exist at exit in PagedAllocator` and dumps core when it ends via
  `--quit-after`; the match itself ran fine. Check whether it also happens on a normal stop (SIGTERM) before relying on it.
- CI now builds release artifacts and a launcher updates players: see `docs/launcher.md` (not yet run against real GitHub).
