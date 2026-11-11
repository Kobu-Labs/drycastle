# Launcher and release pipeline

Players install the launcher once (`suchohrad-launcher-windows.zip`); it downloads and updates the game from GitHub Releases of Kobu-Labs/drycastle
(public repo, unauthenticated API). Code: `godot/launcher` (Godot UI), `godot/LauncherCore` (logic, tested in `ShooterCore.Tests/LauncherTests.cs`).

## Flow at a glance

```
maintainer                      GitHub Releases (Kobu-Labs/drycastle)            player
----------                      -------------------------------------            ------
make map AREA=village
scripts/publish-map.sh  ------> map-<date>-<hash>   (map.zip, map.json)
commit map-version.txt
push to main            ------> dev-<run>           (game zips for Windows + Linux, manifest.json)
tag vX.Y.Z              ------> vX.Y.Z              (same, stable)
tag launcher-vX.Y.Z     ------> launcher-vX.Y.Z     (launcher zips)
                                                                                 download launcher once, run it
                                        <------ newest release of the channel (API) + manifest.json
                                        <------ game zip / map zip, only what changed, sha256 checked
                                                                                 Play -> game starts from the install folder
```

**Player:** download the launcher zip from the Releases page (`docs/launcher-readme.txt` is inside it), unzip, run. First start installs the game and map; later starts update only
what changed, then Play. **Maintainer, game update:** merge to `main` and wait for the `release` job. **Maintainer, stable release:** tag a green commit `vX.Y.Z`.
**Maintainer, map change:** the first two lines above, commit `map-version.txt`, push. **Maintainer, launcher change:** tag `launcher-vX.Y.Z` and tell players to re-download.

## What is published

| Release | Made by | Contains |
|---|---|---|
| `dev-<run>` (prerelease) | every push to `main` (`.github/workflows/release.yml`) | `suchohrad-game-windows.zip`, `suchohrad-game-linux.zip` (no map), `manifest.json` |
| `vX.Y.Z` | pushing a tag `vX.Y.Z` (same workflow) | same, channel `stable` |
| `map-<date>-<hash>` (prerelease) | you, `scripts/publish-map.sh` | `map.zip`, `map.json` |
| `launcher-vX.Y.Z` (prerelease, so it is never GitHub "latest") | pushing a tag `launcher-v*` (`launcher.yml`) | `suchohrad-launcher-windows.zip`, `suchohrad-launcher-linux.zip` |

`manifest.json` (schema 2) lists one build per platform (`windows`, `linux`: exe and game zip) and the map release, each with sha256 and size. The launcher picks the build for its own OS. Players on **stable** get the newest non-prerelease
(`vX.Y.Z`, fetched through the API's `releases/latest` so map and dev releases never hide it); players on **dev** get the newest of the last 100. The last 15 `dev-*` releases are kept. There is no release branch: cut a stable release by tagging a green commit.

## The map

The village map is generated from gitignored data, so CI cannot build it. After `make map AREA=village`, run `scripts/publish-map.sh`
(zips it, publishes a `map-*` release, writes `map-version.txt`), then commit `map-version.txt`. Game builds pick the map named there, so game and map
move together. Until the first map is published, `map-version.txt` holds a placeholder: the release job stays green, publishes nothing and prints a warning.
Most game updates then download only the game zip; a map change downloads the map zip only.

## Launcher behaviour

Installs under `%LOCALAPPDATA%/Suchohrad` on Windows or `~/.local/share/Suchohrad` on Linux (`game/`, `installed.json`, `settings.json`). Downloads are sha256-checked, extracted to a staging folder and swapped in;
a failed, cancelled or interrupted update leaves the previous install playable. Offline with an install: Play still works. Settings: channel, auto-update, UI scale.
Esc closes Settings or cancels a download; it never quits.

## Deviations from the UI standard

Settings use Apply/Revert with live UI-scale preview; Back/Esc with unsaved changes asks for a second press to discard. No UI sounds, no reduce-motion option (nothing animates), window size is not remembered.

## Not covered

- The launcher does not update itself (it is a thin shell; ship a new `launcher-v*` and tell players).
- The dedicated server is not deployed by CI. A protocol bump (`Wire.ProtocolVersion`, shown in the launcher) still needs `deploy/deploy.sh` with the same build.
- Windows and Linux x86_64 only (no macOS). The Windows exe is unsigned (SmartScreen: More info, Run anyway). The Linux build is untested on a real machine; it reuses the existing Linux client export.
- Local check: `make launcher-test`, `make launcher-export`, `make shooter-package-game`. The Linux game exe gets its execute bit from the launcher right before it starts. Nothing was run end to end against real GitHub yet.
