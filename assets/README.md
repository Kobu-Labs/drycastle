# Asset library

Curated, redistributable 3D assets for placed objects (cars, flowers, bushes, fences, trees,
pavement, pots, walls). Placed objects reference an asset by id (see `pipeline/src/suchohrad_pipeline/edits.py`):

- `pack:<pack>/<model>`: a glTF/GLB in `assets/<pack>/`, for example `pack:kenney-car-kit/sedan`.
- `prim:<name>`: a pipeline primitive with no file (`wall`, `slab`, `pot`, `radio_tower`), built by
  `pipeline/src/suchohrad_pipeline/primitives.py`.

`manifest.json` (format_version 1) is the index: per asset `id`, `category`
(car, slab, pot, flower, bush, tree, wall, fence, other), `name`, `file` (relative to this
directory; null for primitives), `size_m` (bounding box [x, y, z] in metres, glTF axes with y up, as
measured after import in Godot), `tags`, `licence`, `source`, `pack`; plus a top-level `packs` list
(name, URL, download URL, licence, download date).

## Packs and provenance

All packs are CC0 1.0 (public domain dedication). Each pack folder keeps the licence text that
shipped with it (`LICENSE.txt`). Only a curated subset of each pack is committed (about 7 MB in total);
the full packs are not.

| Pack | Creator | Models here | Downloaded from |
|---|---|---|---|
| `kenney-car-kit` (3.1) | Kenney, https://kenney.nl/assets/car-kit | 16 vehicles | OpenGameArt mirror (see below) |
| `kenney-city-kit-suburban` (2.0) | Kenney, https://kenney.nl/assets/city-kit-suburban | fences, driveways, paths, planter, 2 trees | OpenGameArt mirror |
| `quaternius-stylized-nature-megakit` (Standard, free) | Quaternius, https://quaternius.com/packs/stylizednaturemegakit.html | 2 bushes, 4 flowers, grass, fern, plants, clover | OpenGameArt mirror |
| `kaykit-character-animations` (1.2) | Kay Lousberg, https://opengameart.org/content/kaykit-character-animations | 1 animated character GLB (Prototype Pete: head and torso, 6 bones, 30 clips incl. Idle, Walk, Run, Shooting, Defeat) | OpenGameArt (publisher's own listing) |
| `quaternius-ultimate-gun-pack` (July 2019) | Quaternius, https://opengameart.org/content/low-poly-guns-pack | pistol, assault rifle, bayonet, converted OBJ to GLB | OpenGameArt |

**Unverified, user-provided (2026-11-11):** `zsky-vehicles/` holds two vehicle models by Zsky supplied by the project owner as local files:
`motorbike.glb` ("Military Motorbike", 5 colour materials Green/Black/Silver/Yellow/Red_Motor) and `bicycle.glb` ("Bike", 4: Silver/Black/Green/Yellow_Bike).
The source page and licence are **not confirmed** and they are not claimed to be CC0; the owner must confirm the licence and attribution before release
(see `zsky-vehicles/NOTICE.txt`). They are listed in the manifest's top-level `unverified_models` array (not in `packs`/`assets`, which stay CC0-only for the
pipeline tests and the editor). Used by `godot/shooter` as the motorcycle and bicycle (`data/vehicles.json`); the raw models are authored at 1/100 scale
(node scale 100, rotated -90 about X), so vehicles.json carries the fit (scale, yaw, offset). The motorcycle faces +Z (turned 180 degrees), the bicycle -Z.

kenney.nl answered automated requests with HTTP 403, and Quaternius distributes through itch.io or
Patreon pages. Neither was circumvented. The same CC0 archives are published openly by their creators
on OpenGameArt.org (`opengameart.org/content/car-kit`, `/city-kit-suburban`, `/stylized-nature-megakit`),
which is where the files came from, on 2026-11-10, with a plain `curl` request.

Shooter models (2026-11-11): used by `godot/shooter` (`CharacterFx.cs`, `WeaponFx.cs`). The KayKit Adventurers
character pack (itch.io, CC0 on the publisher's page) needs itch's download flow and was not fetched, so the
remote player is the animated character bundled in the KayKit Animations pack (same rig and clips). The Quaternius
pack ships OBJ/FBX/Blend only, so the three used guns were converted OBJ+MTL to GLB (geometry and diffuse colours
unchanged). It has no knife (the bayonet stands in) and no launcher (the bazooka is a code-built tube). The pack
archive has no licence file; `LICENSE.txt` records the CC0 shown on its OpenGameArt page. No first-person arms pack
is used: the viewmodel is the weapon alone, lower right.

Not included: the Quaternius trees (their bark textures alone are 8 to 10 MB per tree type, over the repository
budget for this ticket), buildings, rocks, mushrooms and pebbles. Quaternius's Pro/Source tier models
were not used (not part of the free download).

## Sounds (shooter, #90)

Audio files for `godot/shooter` live in the same pack folders (OGG only, loaded at run time like the models) and are listed in
the manifest's top-level `sounds` array (id `sfx:<pack>/<file>`, pack, source, licence, download date, `generated`, `used_for`);
`godot/shooter/data/sounds.json` maps event ids to files, bus, volume, pitch variance and 3D range. All CC0, downloaded 2026-11-11 with plain `curl`:

| Pack | Source | Used for |
|---|---|---|
| `kenney-interface-sounds` | opengameart.org/content/interface-sounds (Kenney) | UI sounds, hit tick, kill confirm, dry-fire click |
| `kenney-music-jingles` | opengameart.org/node/22000 (Kenney) | round start and end stingers |
| `free-firearm-sound-library` | opengameart.org/content/the-free-firearm-sound-library | pistol and AK shots (single shots cut from long takes) |
| `oga-combat-sound-effects-150` | opengameart.org/node/195932 (compilation, per-file sources in its `LICENSE.txt`) | knife, impacts, death, weapon switch, bazooka explosion (layered) |
| `fantozzi-footsteps` | opengameart.org/content/fantozzis-footsteps-grasssand-stone | footsteps (one surface) |
| `oga-chiptune-adventures` | opengameart.org/node/74001 (Juhani Junkala) | menu music loop |
| `suchohrad-generated-sounds` | generated by a script (not in the repo) | PLACEHOLDERS: vehicle engine loop, rocket flight loop, bazooka launch, three reloads |

Not downloaded: kenney.nl itself answers automated requests with 403 (the OpenGameArt copies above were used); the muted.io Performance Cars
pack (planned for engines) is behind a Ko-fi page that serves a Cloudflare JavaScript challenge, so the engine is a placeholder.
Sonniss is excluded (redistribution clause).

## Style: flat colour, and what the packs really contain

The brief is flat-colour low-poly. `make assets-check` loads every asset headless in Godot (all 40 pass; the Kenney Car Kit GLBs load fine despite a forum report of failures elsewhere, Godot ignores their harmless `KHR_texture_transform`). Findings:

- Kenney packs: geometry is flat shaded, but every GLB references one shared palette texture
  (`Textures/colormap.png`, a 12 KB swatch atlas), not per-material colours. Visually flat; technically
  a texture dependency, so keep the `Textures/` folder next to the models.
- Quaternius MegaKit: leaf, flower and grass models use alpha-cut texture cards (diffuse PNGs), so they are
  stylised but not flat-colour. They are included because no flat-colour pack covers bushes and flowers.
- Primitives are strictly flat per-face colours.

Kenney City Kit (Suburban) models are authored at a small scale (a tree is 0.77 m tall, a fence
section 0.5 m wide); the editor needs to scale them. Sizes in the manifest are the raw imported sizes.

## Adding assets

1. Check the licence is CC0 (or compatible) and permits redistribution. Do not commit paid or unclear-licence files.
2. Put the glTF/GLB (and any textures/`.bin` it references, keeping relative paths) in `assets/<pack>/`, plus
   the pack's licence text as `LICENSE.txt`. Commit a subset only.
3. Add an entry to `manifest.json` (and a `packs` entry for a new pack). Measure `size_m` with the check below.
4. Run `make assets-check` (needs Godot 4, headless) and `cd pipeline && uv run pytest`. The tests check
   unique ids, existing files, valid categories and CC0 licences.
5. A new primitive goes in `primitives.py` (`PRIMITIVES` registry) and needs a `prim:` manifest entry; a test
   keeps the two in sync.

`validate_asset_ref(ref, manifest)` in `pipeline/src/suchohrad_pipeline/assets.py` checks an asset reference
against the manifest.
