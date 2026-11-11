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
| `quaternius-ultimate-gun-pack` (July 2019) | Quaternius, https://opengameart.org/content/low-poly-guns-pack | pistols, assault rifles, bullpups, SMGs, shotguns, snipers, bayonet (OBJ converted to GLB); sniper-rifle.glb is from a later separate download of the same author | OpenGameArt |

**Unverified, user-provided (2026-11-11):** `zsky-vehicles/` holds two vehicle models by Zsky supplied by the project owner as local files:
`motorbike.glb` ("Military Motorbike", 5 colour materials Green/Black/Silver/Yellow/Red_Motor) and `bicycle.glb` ("Bike", 4: Silver/Black/Green/Yellow_Bike).
The source page and licence are **not confirmed** and they are not claimed to be CC0; the owner must confirm the licence and attribution before release
(see `zsky-vehicles/NOTICE.txt`). They are listed in the manifest's top-level `unverified_models` array (not in `packs`/`assets`, which stay CC0-only for the
pipeline tests and the editor). Used by `godot/shooter` as the motorcycle and bicycle (`data/vehicles.json`); the raw models are authored at 1/100 scale
(node scale 100, rotated -90 about X), so vehicles.json carries the fit (scale, yaw, offset). The motorcycle faces +Z (turned 180 degrees), the bicycle -Z.

**Unverified, user-provided (2026-11-11):** `animated-characters/` holds four player bodies picked from a 50-model archive of low-poly animated characters the
owner downloaded (`soldier-male`, `soldier-female`, `rifleman-male` = BlueSoldier_Male, `operator` = Ninja_Sand), repacked from glTF to GLB without changes.
All share one 23-bone rig authored in a T-pose (about 3.15 units tall; arms along x) with 17 clips (Idle, Walk, Run, Death, SwordSlash, ...). They are flat
colour with no textures or UVs: the `Face` surface is a blank plate and `Skin` is a dark grey that also covers the hands, head and boots, so
`godot/shooter` recolours them (skin tone, suit camouflage, headgear) per `data/camo.json` `bodies`. The author and licence are **not confirmed** (see
`animated-characters/NOTICE.txt`); they are in the manifest's `unverified_models`. They replace the KayKit placeholder as the remote player body
(`CharacterFx.cs`); the KayKit file stays for its licence record and as the fallback is the capsule.

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
| `free-firearm-sound-library` | opengameart.org/content/the-free-firearm-sound-library | pistol and AK shots (single shots cut from long takes); shooter v2 shots: ar15, ppsh, model12, nova, mossberg, walther_ppq, tikka, savage10, sks (see "Shooter v2 weapons") |
| `oga-combat-sound-effects-150` | opengameart.org/node/195932 (compilation, per-file sources in its `LICENSE.txt`) | knife, impacts, death, weapon switch, bazooka explosion (layered) |
| `fantozzi-footsteps` | opengameart.org/content/fantozzis-footsteps-grasssand-stone | footsteps (one surface) |
| `oga-chiptune-adventures` | opengameart.org/node/74001 (Juhani Junkala) | menu music loop |
| `suchohrad-generated-sounds` | generated by a script (not in the repo) | PLACEHOLDERS: vehicle engine loop, rocket flight loop, bazooka launch, three reloads |

Not downloaded: kenney.nl itself answers automated requests with 403 (the OpenGameArt copies above were used); the muted.io Performance Cars
pack (planned for engines) is behind a Ko-fi page that serves a Cloudflare JavaScript challenge, so the engine is a placeholder.
Sonniss is excluded (redistribution clause).

## Shooter v2 weapons (#114)

Every weapon id in `godot/shooter/data/weapons.json` has a held model (`WeaponFx.cs`, `Models`) and shot, reload and dry-fire
events (`godot/shooter/data/sounds.json`). All CC0 (OpenGameArt pages above, checked 2026-11-11).

- **Models**: the full Quaternius Ultimate Gun Pack zip (OBJ+MTL, flat colour, no textures) was downloaded and the needed OBJs converted
  one-to-one to GLB (geometry, normals, diffuse colour). The pack has **no machine guns**, so `rpk`, `pkm`, `m249` and `m60` reuse assault
  rifle models. Also shared: `aug` and `p90` are the two bullpups. The models were picked by their pack names and sizes and were not
  looked at (no screenshots); the viewmodel fit is the `Length` value in `WeaponFx.cs`.
  | Weapon id | Model file | Notes |
  |---|---|---|
  | `m4a1` | assault-rifle-2.glb | own model |
  | `rpk`, `pkm`, `m249`, `m60` | assault-rifle-3, -4, -5, assault-rifle2-3 .glb | machine guns: no model in the pack, rifle models stand in |
  | `g36` | assault-rifle2-1.glb | own model |
  | `aug`, `p90` | bullpup-1.glb, bullpup-2.glb | bullpups |
  | `mp5`, `ump45` | submachine-gun-1.glb, submachine-gun-2.glb | SMGs |
  | `remington870`, `spas12`, `benelli_m4`, `aa12` | shotgun-1 .. shotgun-4 .glb | own models |
  | `glock17`, `deagle` | pistol-2.glb, pistol-3.glb | own models (were `pistol-1.glb` placeholders) |
  | `m82`, `awm`, `svd` | sniper-rifle-2, -3, -4 .glb | own models (were `sniper-rifle.glb` placeholders) |
- **Sounds**: the existing `free-firearm-sound-library` takes are used where they fit. New single shots (trimmed and normalised the same way,
  from the same CC0 library, `LICENSE.txt` there) are `ar15.ogg` (AR-15), `ppsh.ogg` (PPSh SMG), `model12.ogg`, `nova.ogg`, `mossberg.ogg`
  (shotguns), `walther_ppq.ogg` (Glock 17), `tikka.ogg`, `savage10.ogg`, `sks.ogg` (snipers). Sharing: `ar15.ogg` for m4a1, aug, g36;
  `ak47.ogg` for rpk, m249, m60, pkm (pitched down); `ppsh.ogg` for the three SMGs; `model12` for remington870; `nova` for spas12 and aa12;
  `mossberg` for benelli_m4; `pistol_1911.ogg` for deagle (pitched); `tikka` for m82, `savage10` for awm, `sks` for svd (pitched).
  Reload and dry-fire events still use the generated placeholders and the Kenney click, as before.
- **Not sourced**: no reload or machine-gun sound exists in the CC0 libraries used so far; they are placeholders (see above).

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

## Shooter UI icons (#146)

Loadout and HUD icons live in `godot/shooter/ui/icons/` (not in this library, not placeable). `IconRegistry`
(`godot/ShooterCore/Icons`) maps each weapon, category, perk and killstreak id to its file; a missing id gets
`placeholder.png`. Regenerate with `make shooter-icons`. The weapon renders need a rendering device (a display, `DISPLAY` set); the
other icons only need ImageMagick and Pillow.

- `weapons/<id>.png` (512x256): side-on renders of the held models in the Quaternius gun pack (CC0), by
  `godot/shooter/tools/render_icons.gd`.
- `categories/<category>.png` (128x128): white silhouettes cut from one weapon render per category, by
  `godot/shooter/tools/make_icons.py`. Derived from the same CC0 pack.
- `perks/`, `streaks/`, `placeholder.png` (128x128): flat white glyphs drawn as SVG (sources in `ui/icons/src/`) by
  `godot/shooter/tools/make_icons.py`. Original work of this repository; no third-party pack was downloaded.

Icons are white on transparent so the theme can tint them. The licence record is the top-level `ui_icons` entry in
`manifest.json`.

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
