# Releasing the shooter

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

## Deploying the server

1. Copy `out/shooter/linux-server/` to the host (binary, `.pck`, .NET data, `assets/`, `map/`). Nothing else is needed.
2. Start it (public flags only; leave off `--unlock-all`, `--allow-sandbox`, `--debug-cmds`):
   ```
   ./suchohrad-server.x86_64 --headless -- --role server --port 27015 --db /srv/suchohrad/data/shooter.db
   ```
3. Open UDP 27015 in the host firewall and the provider's firewall.
4. Run it under a supervisor (systemd or a container with a restart policy). Mount `shooter.db` on persistent storage and
   back it up: deleting it wipes all player progression.
5. The wire protocol version must match between server and client. A new protocol means shipping a new client zip
   and redeploying the server together.

## Updating

- Server-only change (balance in `data/*.json` is in the `.pck`, so it is a client change too): rebuild, redeploy, restart.
  Back up `shooter.db` first; migrations apply at start.
- Any client-visible change, new map build, or protocol bump: repeat "Cutting a release" and redeploy the server.

## Still unverified

- The Windows client has not been run from an unzipped folder (assets and map found next to the exe).
- The exported Linux server prints `ERROR: Pages in use exist at exit in PagedAllocator` and dumps core when it ends via
  `--quit-after`; the match itself ran fine. Check whether it also happens on a normal stop (SIGTERM) before relying on it.
- No CI builds release artifacts yet.
