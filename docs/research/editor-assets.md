# Research: low-poly asset packs for parcel props

Ticket: Kobu-Labs/drycastle #30. Map: #29 (World map editor v1). Brief: `mods/world-map/v1-editor.md`.

Method: web search and listing-page reads only (no downloads, no test imports). Items marked **unverified** were not confirmed on the listing. Nothing here was imported into Godot 4; the Godot column is based on format support, not tests.

## Summary

- Strong CC0 coverage exists for **trees, bushes, flowers, grass, suburban fences, driveways and walls**, all free.
- **Cars** have a CC0 option with glTF (Kenney Car Kit), with a fallback in FBX/OBJ.
- **Pavement slabs** and **flower pots** have no confirmed CC0 glTF pack. These are coverage gaps; primitives or a pipeline-generated mesh should fill them.
- **Paid packs under 50 EUR**: none had price, licence and glTF all confirmed. Nothing paid is recommended yet.

## Candidates

| Pack | Creator | Price | Licence | Formats | Covers | Style / notes |
|---|---|---|---|---|---|---|
| Stylized Nature MegaKit | Quaternius | Free (name your own price; $0 allowed). Pro $9.99, Source $14.99 add-on tiers | CC0 ("free to use in personal, educational and commercial projects") | FBX, OBJ, glTF (Source tier adds Godot, Unity, Unreal projects) | Trees (40), plants and flowers (35), rocks (27), grass, bushes | Flat stylised low-poly. Model count stated inconsistently (81 free, 110+, 116, 118). Best single pack for trees, flowers, bushes, grass. |
| City Kit (Suburban) | Kenney | Free (donation optional) | CC0 1.0, no attribution required | glTF, FBX, OBJ, DAE, STL | Suburban buildings, fences, trees, driveways (35+ models) | Flat-colour, matches Kenney style. Bushes, walls, pavement not listed. Fences and trees are the relevant part. |
| Car Kit | Kenney | Free (donation optional) | CC0 | glTF, FBX, OBJ per itch.io listing (40+ models) | Cars, kart racers, debris | Flat-colour. A PlayCanvas forum thread reports GLB files failing the glTF validator / load in that engine. Validate in Godot before committing. |
| Lowpoly Cars | Quaternius | Free (name your own price) | CC0 | FBX, OBJ, Blend only (no glTF) | Cars (count not stated; Cop SUV is Patreon-only) | Needs an FBX/OBJ to glTF conversion step. Fallback for cars. |
| Modular Fences (PSX style) | valsekamerplant | Free (name your own price) | CC0, commercial use allowed | GLB, FBX | 79 pieces: metal, brick, picket, plaster and drystone walls, wooden fences | Textured PSX look, not flat colour. Good wall and fence coverage. Redistribution not stated explicitly. |
| Modular Hedges (PSX style) | valsekamerplant | Free (now free per devlog; source $1.99+) | CC0, commercial use allowed | GLB, FBX | 160+ parts: low and tall hedges, corners, overgrowth | Textured PSX look. Bush and hedge coverage. Redistribution not stated explicitly. |
| Fantasy Town Kit | Kenney | Free (donation optional) | CC0 | Not stated on page (Kenney packs usually ship glTF) | Walls (tag), town buildings, 160 files | Flat-colour. Walls only partly confirmed by item list. |
| Shapespark Low-Poly Plants Kit | Shapespark | Free | CC0 1.0 (Sketchfab listing); forum says free to use | glTF among TurboSquid formats (unverified on source repo) | 30 objects, 12 trees, ~7,600 tris | Flat-colour. Only trees confirmed; bushes and flowers unconfirmed. Check the GitHub repo. |
| Garden Pot Pack 1 | CGTrader free | Free | Claimed CC0 textures | **unverified** | 20 garden pots | Possible flower pot source. Licence and format not confirmed. |
| Low-Poly Fence Asset Pack | Andrew Walsh (Fab) | Free | **Unknown** (licence field is "-") | FBX with converted glTF/GLB/USDZ | Fences only (plus a lamp post). Single shared material, flat look. | Style fit is good, but the licence is not stated, so it is not usable until confirmed. |
| Low-Poly + Realistic Garden | CGTrader / Fab | Paid, price **unverified** | **unverified** | **unverified** | Hedges, flower beds, stone paths (per listing) | Candidate for the paid under-50 EUR slot. Listing page did not load in the read; needs a manual check. |

## Per-category view

- **Cars**: Kenney Car Kit (CC0, glTF listed). Fallback: Quaternius Lowpoly Cars (convert FBX/OBJ).
- **Pavement slabs/blocks**: **gap**. Kenney City Kit (Roads) lists no sidewalk or paving pieces. Generate from primitives or find a paid pack.
- **Flower pots**: **gap**. Only Garden Pot Pack 1 (licence and format unverified). Primitive cylinder is the fallback.
- **Flowers**: Quaternius MegaKit (35 plants and flowers, CC0, glTF).
- **Bushes**: Quaternius MegaKit (bushes), PSX Modular Hedges (CC0, GLB).
- **Garden patches / grass**: Quaternius MegaKit (grass). Patches are not listed as a distinct item; build from grass plus flower models.
- **Walls**: PSX Modular Fences (plaster, drystone, brick walls, CC0); Kenney Fantasy Town Kit (walls, CC0, format unverified).
- **Fences**: Kenney City Kit (Suburban) (CC0, glTF); PSX Modular Fences (CC0, GLB).
- **Trees**: Quaternius MegaKit (40 trees, CC0, glTF); Kenney City Kit (Suburban) trees; Shapespark plants kit (12 trees).

## Godot 4 import notes

- glTF/GLB imports natively in Godot 4 and is the preferred format. Packs that ship glTF (Kenney, Quaternius MegaKit, PSX fences/hedges) are the easiest path.
- FBX-only packs (Quaternius cars) need an FBX-to-glTF conversion step, for example with Blender, or a Godot FBX import. Neither was tested.
- The Kenney Car Kit GLB validation issue should be checked with the glTF validator before depending on it.

## Style fit

- Flat colour: Kenney packs, Quaternius MegaKit, Shapespark kit, Fab fence pack. These match the brief.
- Textured PSX: valsekamerplant fences and hedges. Usable, but not strictly flat-colour, so they may need texture stripping or a material override in the editor.

## Recommended shortlist

1. Quaternius Stylized Nature MegaKit: trees, flowers, bushes, grass (CC0, glTF, free).
2. Kenney City Kit (Suburban): fences, trees, driveways (CC0, glTF, free).
3. Kenney Car Kit: cars (CC0, glTF listed; validate). Fallback: Quaternius Lowpoly Cars.
4. PSX Modular Fences and Hedges (valsekamerplant): walls, fences, hedges (CC0, GLB, free). Accept the textured look or override.
5. Kenney Fantasy Town Kit: walls, if the PSX walls are not enough (confirm format).

Still open: pavement slabs (no pack found), flower pots (Garden Pot Pack 1 needs verification), and a paid pack under 50 EUR (none fully verified). Next step is to verify the Garden Pot Pack licence and format, and to check the Low-Poly + Realistic Garden price and licence on its own page.

## Licence notes for the repo

- CC0 means no attribution is required, so redistribution inside a git repo is permitted. Listing pages for the PSX packs do not state redistribution explicitly; the CC0 dedication covers it, but a quick confirmation is advisable.
- Keep a `LICENSE` or `CREDITS` note per imported pack anyway, since the sources are community-hosted.
