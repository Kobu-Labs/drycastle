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

kenney.nl answered automated requests with HTTP 403, and Quaternius distributes through itch.io or
Patreon pages. Neither was circumvented. The same CC0 archives are published openly by their creators
on OpenGameArt.org (`opengameart.org/content/car-kit`, `/city-kit-suburban`, `/stylized-nature-megakit`),
which is where the files came from, on 2026-11-10, with a plain `curl` request.

Not included: the Quaternius trees (their bark textures alone are 8 to 10 MB per tree type, over the repository
budget for this ticket), buildings, rocks, mushrooms and pebbles. Quaternius's Pro/Source tier models
were not used (not part of the free download).

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
