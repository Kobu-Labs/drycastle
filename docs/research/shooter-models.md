# Shooter v1: free models and animations (research for #69)

Research for issue #69, part of map #68 "Map: Shooter v1". Question: which free (CC0 or permissive) low-poly
packs cover the shooter's model and animation needs, against the bar set by the existing `assets/` packs
(flat-colour low-poly, CC0 or permissive; see `assets/README.md`).

Research date: 2026-11-10. Method: web search, then page fetches where the page loaded. Nothing has been
downloaded, opened in Godot, or licence-checked by reading a shipped `LICENSE` file.

## Legend

- **[V] verified**: the claim was read on the publisher's or licence-holder's own page in this session.
- **[S] snippet**: the claim came only from a search result summary, not a fetched page. Treat as a lead.
- **[U] unverified**: unconfirmed, or the page that would confirm it returned an error (403/404).

## Summary

- **Player character and animation: good coverage.** KayKit Adventurers (characters) plus KayKit Character
  Animations (shooting, aiming, reloading, idle, walk, run, death) is the strongest pairing: both CC0, glTF
  available, and a flat gradient-atlas style. This is the recommended base for remote players.
- **Guns: partial.** Quaternius Low Poly Guns (40 guns, CC0) and Quaternius Toon Shooter Game Kit (74 models, CC0)
  are the candidates. Neither is verified to contain a pistol, AK-47, bazooka and knife in glTF.
- **First-person arms: weak.** No verified CC0 arms pack with glTF. Leads exist but their pages were blocked.
- **Bazooka rocket: weak.** One CC0 rocket-launcher lead, format unknown. The rocket projectile itself is not
  covered by any pack found; a primitive is a likely fallback.
- **Motorcycle and bicycle: gap.** No CC0 low-poly motorcycle or bicycle with glTF was found. Most hits were
  royalty-free or paid marketplace listings, which do not meet the bar.

## Table

| Need | Candidate pack | Licence | Style fit | glTF / Godot notes | Gaps / status |
|---|---|---|---|---|---|
| Player character (remote players) | KayKit Character Pack: Adventurers (Knight, Barbarian, Rogue, Mage, Ranger; +3 extra) [V] | CC0 [V] | Good: "single gradient atlas texture (1024x1024)", stylised low-poly [V] | FBX and glTF [V]. Godot 4 asset-library listing exists [S] | Only basic movement animations are included; use KayKit Character Animations for the rest [V]. Weapons in the pack are melee/bow/staff, not firearms [V] |
| Player animations: idle / walk / run / shoot / death | KayKit Character Animations [V] | CC0 [V] | Matches KayKit characters | FBX and glTF [V]. Split into Rig_Medium (100+ anims) and Rig_Large (23 to 25+) [V] | Categories verified: idling, walking, running, shooting, aiming, reloading (one- and two-handed), death, getting hit [V]. Exact clip names not listed on page [V]; check in downloaded files. Rig must match the character model [V] |
| Player animations (alternative) | Quaternius Universal Animation Library (Standard) [V] | CC0 [V] | Animation only; characters are separate | GLB, glTF, FBX, OBJ, Blend (Standard: OBJ/FBX/glTF) [V]. 120+ anims in full pack; Standard free tier is 60-70% of it [V] | Death, locomotion (8 directions, jog, sprint), "combat and gun" emotes [V]. Idle and shoot clip names NOT confirmed [U] |
| Player animations (melee / armed combos) | Quaternius Universal Animation Library 2 [V] | CC0 [V] | Animation only | FBX, GLB, Blend; OBJ/glTF in Standard [V] | 130+ anims, melee and armed combos, zombie locomotion [V]. No gun or knife clips named [V] |
| Character set with animations (alternative) | Quaternius Toon Shooter Game Kit [S] | CC0 [S] | Toon/stylised [S]. Textured flag on page is ambiguous [V] | FBX, OBJ, Blend, glTF [V] | 74 models [V]; characters with 17 animations [V]. Guns, enemies mentioned, not itemised [V]. Animation names [U] |
| Player character (alt.) | Kenney Character Assets / Animated Characters 3 [S] | CC0 [S] | Kenney style [U] | Format [U] (kenney.nl page returned 404 on fetch) | Character Assets: idle, death, running, walking listed [S]. Animated Characters 3: idle, jump, running only [S]. No death in Animated Characters 3 [S] |
| Pistol / AK-47 / bazooka / knife | Quaternius Low Poly Guns Pack (OpenGameArt) [S] | CC0 [S] | Low-poly guns [S] | FBX, OBJ, Blend. No glTF listed on the OpenGameArt snippet [S]. Convert with Blender or verify in zip [U] | 40 guns [S]. Individual models not itemised. Itch page is titled "50+ LowPoly Guns" with a different formats list [S]: versions may differ [U] |
| Pistol / AK-47 / bazooka / knife (alt.) | Quaternius Toon Shooter Game Kit (guns) [S] | CC0 [S] | Toon [S] | glTF listed [V] | Presence of pistol/AK/bazooka/knife [U] |
| Pistol / rifle (alt.) | CC0 Flat Guns West / East (OpenGameArt) [S] | CC0 on OGA; CC-BY 4.0 on itch [S] | Flat-ish low-poly guns [S] | GLB listed for East [S] | Licence conflict between the two pages: check before use [S]. Rigged, meant for animation [S]. Not verified to include AK or bazooka [U] |
| Blaster / sci-fi guns | Kenney Blaster Kit [V] | CC0 [V] | Kenney flat style [U] | Formats not stated on page [V] | Sci-fi blasters, not firearms; 40 files [V]. Not a fit for pistol/AK-47 |
| First-person arms + hands | "low poly FPS rifle and hands" (OpenGameArt) [S] | Not seen [U] | Low-poly [S] | FBX, glTF, OBJ, Blend listed; a shooting animation [S] | Page blocked (403). Licence unknown: do not use until checked [U] |
| First-person arms (rigged) | "FPS arms rigged only" (OpenGameArt) [S] | CC0 [S] | [U] | Format [U] | Page blocked (403). Confirm glTF and contents [U] |
| First-person arms (alt.) | PSX First Person Arms (itch.io, free) [S] | Not stated [U] | PSX low-poly [S] | FBX, GLB, Blend [S] | Licence not stated; PSX look may not match flat style [U] |
| Bazooka | Low Poly Rocket Launcher (OpenGameArt, khairul169, 2016) [S] | CC0 [S] | Low-poly, ~600 faces [S] | Format not listed; single zip [S] | Needs zip inspection for glTF [U] |
| Bazooka rocket (projectile) | none found | — | — | — | Gap. Candidate: a primitive or a shape built in code (no file needed) [U] |
| Motorcycle | none CC0 found | — | — | Itch "Scooter" pack is CC0 with glTF [S] | Scooter is not a motorcycle. Gap [V] |
| Bicycle | none CC0 found | — | — | Quaternius LowPoly Public Transport (OGA, CC0) [S] may be the closest lead | Formats listed as Blend/OBJ/FBX, not glTF [S]. Does not list bicycles [S]. Gap [U] |
| Car | Kenney Car Kit (already in repo) [V] | CC0 [V] | Flat [V] | glTF/GLB, shared palette texture [V] per `assets/README.md` | Not needed |

## Godot / glTF notes

- Godot 4 imports glTF 2.0 and GLB natively. Use GLB where available: one file, textures embedded.
- Kenney packs reference a shared palette texture (`Textures/colormap.png`). Keep the `Textures/` folder
  beside the models, as `assets/README.md` already requires.
- KayKit uses one gradient atlas texture per pack. The publisher says it can be downsampled to 128x128 [V].
- Animation retargeting is the main technical risk. Clips in Godot's AnimationPlayer bind to bone names
  on one skeleton. Characters and animations from different packs only share clips when they use the same
  rig. KayKit Character Animations is split into Rig_Medium and Rig_Large, so the animation pack must match
  the character rig. This is stated for KayKit [V]. Verify on a real import before committing to a pairing.
- Standard Quaternius packs ship FBX, OBJ and glTF, but not always GLB [V]. For Blend-only or FBX-only
  packs, a one-time conversion in Blender is needed. Record the conversion step in the asset manifest.
- The `assets/` README rule still applies: any new pack needs a `LICENSE.txt` and an entry in
  `manifest.json` and must pass `make assets-check`.

## Licence cautions

- Quaternius packs: CC0 [V] on each page checked.
- KayKit: CC0 [V]. Asks users not to resell unmodified copies or claim them as their own [V].
- CC0 Flat Guns West: itch says CC-BY 4.0, OGA says CC0 [S]. Do not use until resolved.
- Marketplace (CGTrader, Fab, Summer Engine) listings: royalty-free or paid licences, not CC0. Excluded.
- Summer Engine "Low Poly Rocket Launcher" was generated with text-to-3D [S]. Excluded: AI-generated
  content has an unclear licence chain.

## Gaps (for the map)

1. **First-person arms**: no verified CC0 glTF arms pack. Options: find and verify one of the OpenGameArt
   leads once reachable; otherwise model simple arms (primitives) or hide arms and show only the weapon.
2. **Gun set**: confirm the Quaternius Low Poly Guns (or Toon Shooter guns) includes pistol, AK-47 and knife
   in a convertible format. Bazooka may need the OGA rocket launcher or a primitive.
3. **Bazooka rocket**: no pack found. Likely a primitive.
4. **Motorcycle and bicycle**: no CC0 low-poly glTF found. Needs a decision: relax to CC-BY with attribution,
   use a primitive build, or use a closer sci-fi/public-transport pack after conversion.
5. **Animation clip names**: the Universal Animation Library and KayKit clip names need to be read from the
   downloaded files before the animation tickets are written.
6. **Rig match**: confirm the chosen character and animation packs share a rig before building remote players.

## Recommendation

- Remote players: KayKit Adventurers + KayKit Character Animations. Verify rig match first.
- Guns: verify Quaternius Low Poly Guns (and Toon Shooter guns) in a real download, then decide.
- Arms, rocket, motorcycle, bicycle: open decisions. Flag in map #68 "Not yet specified" or accept primitives.
- Next step: download the three verified candidates (KayKit Adventurers, KayKit Character Animations,
  Quaternius Low Poly Guns) into a scratch directory, not into `assets/`, and inspect them in Godot.
