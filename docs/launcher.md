# Launcher and release pipeline

Players install the launcher once (`drycastle-launcher-windows.zip`, `-linux.zip` or `-macos.zip`); it downloads and updates the game from GitHub Releases of Kobu-Labs/drycastle
(public repo, unauthenticated API). Code: `godot/launcher` (Godot UI), `godot/LauncherCore` (logic, tested in `ShooterCore.Tests/Launcher/LauncherTests.cs`).

## Flow at a glance

```
maintainer                      GitHub Releases (Kobu-Labs/drycastle)            player
----------                      -------------------------------------            ------
make map
scripts/release/publish-map.sh  ------> map-<date>-<hash>   (map.zip, map.json)
commit map-version.txt
PR main -> release, merge ------> deploys the server to the VPS, THEN publishes v0.<run> (game zips for Windows + Linux + macOS, manifest.json)
tag launcher-vX.Y.Z     ------> launcher-vX.Y.Z     (launcher zips)
                                                                                 download launcher once, run it
                                        <------ newest release of the channel (API) + manifest.json
                                        <------ game zip / map zip, only what changed, sha256 checked
                                                                                 Play -> game starts from the install folder
```

**Player:** download the launcher zip from the Releases page (`docs/launcher-readme.txt` is inside it), unzip, run. First start installs the game and map; later starts update only
what changed, then Play. **Maintainer, release (game and server together):** open a PR `main` -> `release` and merge it; the pipeline deploys the server, then publishes the release. For a test build without deploying, run the `release` workflow manually on a branch.
**Maintainer, map change:** the first two lines above, commit `map-version.txt`, push, then release as above. **Maintainer, launcher change:** tag `launcher-vX.Y.Z` and tell players to re-download.

## What is published

| Release | Made by | Contains |
|---|---|---|
| `v0.<run>` (stable) | merging a PR into the `release` branch (`.github/workflows/release.yml`, which also deploys the server first) | `drycastle-game-windows.zip`, `drycastle-game-linux.zip`, `drycastle-game-macos.zip` (no map), `manifest.json` |
| `dev-<run>` (prerelease) | manual run of the `release` workflow on any branch (no deploy) | same, channel `dev`; last 15 kept |
| `map-<date>-<hash>` (prerelease) | you, `scripts/release/publish-map.sh` | `map.zip`, `map.json` |
| `launcher-vX.Y.Z` (prerelease, so it is never GitHub "latest") | pushing a tag `launcher-v*` (`launcher.yml`) | `drycastle-launcher-windows.zip`, `drycastle-launcher-linux.zip`, `drycastle-launcher-macos.zip` |

`manifest.json` (schema 2) lists one build per platform (`windows`, `linux`, `macos`: exe and game zip) and the map release, each with sha256 and size. The launcher picks the build for its own OS. Players on **stable** get the newest non-prerelease
(`v0.<run>`, fetched through the API's `releases/latest` so map and dev releases never hide it); players on **dev** get the newest of the last 100. Nothing is published on a plain push to `main`: only the `release` branch (or a manual run) produces release assets. Deploying and releasing are one pipeline, see `docs/release.md` ("Release pipeline").

## The map

The village map is generated from gitignored data, so CI cannot build it. After `make map`, run `scripts/release/publish-map.sh`
(zips it, publishes a `map-*` release, writes `map-version.txt`), then commit `map-version.txt`. Game builds pick the map named there, so game and map
move together. Until the first map is published, `map-version.txt` holds a placeholder: the release job stays green, publishes nothing and prints a warning.
Most game updates then download only the game zip; a map change downloads the map zip only.

## The private asset pack

Paid assets (Synty models, voices, songs) are never in the repo, a game zip or a GitHub release (the CI step `scripts/release/check-no-private-assets.sh` fails when one is tracked).
They travel as one **encrypted** zip from the game host:

- `make private-pack` (`scripts/release/make_private_pack.py`) zips the runtime subset of `assets-private/` (its `INCLUDE` list is the definition of the pack).
- `scripts/release/publish-private-pack.sh user@host` encrypts it with the pack password (`openssl enc -aes-256-cbc -pbkdf2`, 600 000 iterations), uploads it to
  `/opt/drycastle/pack/` (served by the `pack` service of `deploy/docker-compose.yml`: plain HTTP, TCP 27080, no listing) and the unencrypted files to
  `/opt/drycastle/assets-private/` (mounted into the game server), and writes **`private-pack.json`**. Commit that file; the release job copies it into
  `manifest.json` as `"pack"` (id, encrypted file url/sha256/size, `plain_sha256`, `iterations`). No `private-pack.json`: the release has no pack.
- The launcher fetches it only when a **pack password** is set in Settings (`settings.json`, plain text), checks the sha256, decrypts
  (`LauncherCore/PackCrypto.cs`), proves the password by `plain_sha256`, and extracts to `game/assets-private/` (kept across game updates like the map;
  `installed.json` `packId`). The password never travels, so no HTTPS and no domain are needed; it must be long and random (the file can be attacked offline).
- The pack never blocks the game: no password = not requested ("Private assets: locked" on the main screen); wrong password, host down or a corrupt
  download = the game and map install, then a message and Play still works. The game falls back to placeholder models and silence.
- One shared password. To change it: publish again with the new one and release; installs that already have that pack keep it.

## Launcher behaviour

Installs under `%LOCALAPPDATA%/Drycastle` on Windows or `~/.local/share/Drycastle` on Linux or `~/Library/Application Support/Drycastle` on macOS (`game/`, `installed.json`, `settings.json`). Downloads are sha256-checked, extracted to a staging folder and swapped in;
a failed, cancelled or interrupted update leaves the previous install playable. Offline with an install: Play still works. Settings: channel, auto-update, UI scale, asset pack password (hidden, with "Show the password").
Esc closes Settings or cancels a download; it never quits.

## Deviations from the UI standard

While the password field is being edited, the first Esc ends the edit (Godot's LineEdit) and the next goes back. Settings use Apply/Revert with live UI-scale preview; Back/Esc with unsaved changes asks for a second press to discard. No UI sounds, no reduce-motion option (nothing animates), window size is not remembered.

## Not covered

- The launcher does not update itself (it is a thin shell; ship a new `launcher-v*` and tell players).
- Windows and Linux x86_64, macOS universal (Apple Silicon 11.0+, Intel 10.15+). The Windows exe is unsigned (SmartScreen: More info, Run anyway). The Linux build is untested on a real machine; it reuses the existing Linux client export.
- macOS: both `.app` bundles are only ad-hoc signed (Godot's built-in signing; Apple Silicon refuses unsigned code), not notarized (needs a paid Apple Developer account). A launcher zip downloaded in a browser is quarantined, so the first start needs the `xattr -dr com.apple.quarantine` step or Privacy & Security > Open Anyway (steps in the launcher README). The game itself is fetched by the launcher, carries no quarantine flag and starts without a prompt. The game zip holds `Drycastle Shooter.app` with `assets/` and `map/` beside it (`GamePaths.cs` looks outside the bundle). The projects enable `import_etc2_astc` because Godot refuses arm64/universal exports without it (it adds nothing measurable to the `.pck`). Exported from Linux CI, never run on a real Mac yet; the launcher starts the bundle's inner executable directly rather than through `open`, so check window focus on the first real run.
- Local check: `make launcher-test`, `make launcher-export`, `make shooter-package-game`. The Linux game exe gets its execute bit from the launcher right before it starts. Nothing was run end to end against real GitHub yet.
