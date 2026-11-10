# Shooter v1: free sound effects and music (research)

Research for issue #70, part of map #68 ("Map: Shooter v1"). Scope: which free packs can cover the shooter's sound needs under the bar set on #70: CC0 or permissive licence, usable commercially, downloadable without login where possible.

Research date: 2026-11-10.

## How to read the status column

- **Verified**: the licence and file facts were read from the primary page (fetched) in this session.
- **Unverified**: the claim comes only from search-result snippets, a third-party mirror, or a page the fetch tool could not open (OpenGameArt returned HTTP 403 to every fetch). Check the page before use.
- **Not found**: no candidate located in this pass.

## Summary

- **Best free base for the shooter: Kenney.** Interface Sounds (CC0, verified) covers UI and menu clicks. Music Jingles (CC0, verified) covers short menu stingers. Kenney's Impact Sounds pack is the likely hit/impact source but its official page was not found (only third-party copies, unverified).
- **Gunshots and reloads are the weakest area for free CC0 packs.** The best CC0 candidate is The Free Firearm Sound Library on OpenGameArt (unverified). A commercial-licence fallback is Sonniss GDC bundles (licence verified, contents not verified).
- **Sonniss GDC bundles are the largest free library, but the licence is not CC0.** It allows commercial use with no attribution, but forbids redistributing the sounds "as sound effects", and forbids AI training. A mod distributed as raw audio files may fall foul of the redistribution clause. This needs a decision before adoption (see Open questions).
- **Footsteps, explosions and engines all have CC0 candidates** on OpenGameArt and muted.io, but none has been checked against the page.
- **Knife, bazooka launch/rocket flight, and per-weapon reloads for AK-47 and bazooka** have no candidate found yet. These are the main gaps.

## Coverage table

| Need | Candidate pack | Licence | Format | Coverage | Gaps | Status |
|---|---|---|---|---|---|---|
| UI / menu clicks, hover, back, HUD ticks | [Kenney Interface Sounds](https://kenney.nl/assets/interface-sounds) | CC0 | Not stated on page (Kenney packs are usually OGG/WAV) | 100 files, direct download with a donation prompt that can be skipped, no login | None for basic UI; it is not a hit-marker pack | Verified (licence, count, download). Format unverified. |
| Menu stingers / short jingles | [Kenney Music Jingles](https://kenney.nl/assets/music-jingles) | CC0 | Not stated on page; OpenGameArt mirror is OGG ([node 22000](https://opengameart.org/node/22000), unverified) | 85 short jingles, standalone download offered | Jingles, not loopable menu music | Verified (licence, count, standalone download). Format unverified. |
| Hit / impact sounds (body hit, wall, prop) | Kenney Impact Sounds | CC0 per third-party copies ([Godot port](https://godotengine.org/asset-library/asset/edit/7694), [Gumroad copy](https://downloadforfree.gumroad.com/l/free-impact-sounds-pack)) | Unknown | About 130 files per the Gumroad listing | Official kenney.nl page not found, so the licence rests on mirrors | Unverified |
| General sci-fi / laser / sweeps (may cover sci-fi weapon or UI accents) | [Kenney Digital Audio](https://kenney.nl/assets/digital-audio) | CC0 | Not stated | 60 files; page only tags "space" and "laser" | No gunshot/impact categories shown | Verified (licence, count). Contents unverified. |
| Pistol and AK-47 gunshots | [The Free Firearm Sound Library](https://opengameart.org/content/the-free-firearm-sound-library) (OpenGameArt) | CC0 (uploader statement; 194 MB archive) | Archive; formats not confirmed | Tagged for automatic rifles, semi-auto, pistols | Per-file list not confirmed; check whether AK-style fire is in there | Unverified (page returned 403). Best CC0 candidate for guns. |
| Pistol gunshots (backup) | [gunshots by kurt](https://opengameart.org/content/gunshots) (OpenGameArt) | CC0 | WAV presumed | Pistol files (.22 pistol and .22 magnum), reported echo | Echo; needs trimming. No rifle or bazooka | Unverified |
| Reloads | [gun reload sounds by SpringySpringo](https://opengameart.org/content/gun-reload-sounds) (OpenGameArt) | CC0 | Not stated | Tagged assault rifle, pistol, shotgun; list shows mostly a shotgun cock | Mostly missing; no AK or pistol reload confirmed | Unverified, weak |
| Gunshots, commercial fallback | [Sonniss GDC game audio bundles](https://sonniss.com/gameaudiogdc/) | Sonniss EULA: commercial use, no attribution, no redistribution "as sound effects", no AI training (see Licence notes) | WAV (per [bedroomproducersblog](https://bedroomproducersblog.com/2026/03/16/sonniss-gdc-2026-bundle/) and [rekkerd](https://rekkerd.org/sonniss-releases-gdc-2026-game-audio-bundle/)) | Large; the 2026 bundle is reported as more than 347 files (7.47 GB); earlier bundles are 23.5 GB (GDC 2024) and larger | Per-category file lists not confirmed for weapons, reloads or footsteps | Licence verified; content unverified; size figures conflict between sources |
| Paid gunshot pack (not free, not CC0) | Nightshade Game Studios, Gunshot Sounds Pack 1 (itch.io, $3) | Not CC0; licence unchecked | Unknown | Assault rifle, LMG, pistol, shotgun, SMG, sniper | Not free; licence unchecked | Unverified. Listed only as a fallback. |
| Knife | Not found | n/a | n/a | n/a | Knife swing, hit and stab sounds | Not found |
| Bazooka launch and rocket flight | Not found (the Combat pack below has explosions only) | n/a | n/a | n/a | Launch whoosh, rocket trail loop | Not found |
| Explosion | [Combat Sound Effects Pack](https://opengameart.org/node/195932) (OpenGameArt) | CC0 per listing; copyright notice unread (403) | WAV, 48 kHz, 16-bit stereo | 23 explosion and destruction sounds, 78 magic sounds | Read the copyright notice before use. Also [single synthesized explosion](https://opengameart.org/content/synthesized-explosion) (CC0, FLAC) and [bang/firework pack](https://opengameart.org/comment/106597) (CC0, 25 sounds) | Unverified |
| Footsteps, grass and stone | [Fantozzi's Footsteps (Grass/Sand & Stone)](https://opengameart.org/content/fantozzis-footsteps-grasssand-stone) (OpenGameArt) | CC0 (sourced from Freesound CC0 samples) | Not stated | 12 single steps | Only 12 steps; no per-surface variants beyond grass/sand/stone | Unverified |
| Footsteps, snow and gravel | "42 Snow and Gravel Footsteps" (OpenGameArt; page not located as a direct link in this pass) | CC0 (uploader permission for Freesound extracts) | Not stated | 42 steps | Snow/gravel only | Unverified |
| Footsteps, multi-surface | [Different steps on wood, stone, leaves, gravel and mud](https://opengameart.org/content/different-steps-on-wood-stone-leaves-gravel-and-mud) (OpenGameArt, TinyWorlds) | CC0 | Not stated | Wood, stone, leaves, gravel, mud | Grass not listed; check counts | Unverified |
| Footsteps, multi-surface alt. | [Footsteps on different surfaces](https://opengameart.org/content/footsteps-on-different-surfaces) (OpenGameArt) | Not shown in snippet | Not stated | Concrete, grass/dirt, gravel, metal, polished floor | Licence not visible in search result | Licence not found. Do not use until checked. |
| Vehicle engines (loopable) | [Racing car engine sound loops](https://opengameart.org/content/racing-car-engine-sound-loops) (OpenGameArt) | CC0 | Not stated | Loops made for a racing game; differ only in pitch | Motorcycle and bike not covered | Unverified |
| Vehicle engines (synth variants) | [Engine sound effects with synthesized sounds](https://opengameart.org/node/166538) (OpenGameArt) | CC0 | Not stated | Mix of recorded and synthesized engines | Needs pitch and crossfade work | Unverified |
| Vehicle engines (field recordings) | [Performance Cars, muted.io](https://muted.io/performance-cars) | CC0 | WAV | 68 field recordings of sports-car engines and asphalt | Not loops; needs editing to make loops. Download is via a Ko-fi page | Verified (licence, count, WAV). Loop suitability unverified. |
| Menu music (chiptune loops) | [Juhani Junkala, Chiptune Adventures](https://opengameart.org/node/74001) (OpenGameArt, "CC0 - Retro Music") | CC0 per listings | OGG (8.1 MB zip) and WAV (32.1 MB zip) | Multiple loops; user comments say "Stage 1" fits a menu | Official track names not confirmed; 400 Indie Game Music Loops pack (itch.io) is the larger related source | Unverified |
| Menu music (other CC0 loops) | [Flowerbed Fields loop](https://opengameart.org/content/flowerbed-fields-loop), "Main Title screen loop" by Joth (OpenGameArt; page not located as a direct link in this pass) | CC0 per listings | WAV, OGG | Cheerful/intro loops | Single tracks; style is chiptune | Unverified |
| Menu music (not chiptune) | [Menu Music 1](https://opengameart.org/content/menu-music-1) (OpenGameArt) | Listed CC0, but copyright notice says "Public Domain" | Unknown | Upbeat electronic | Conflicting licence wording; check | Unverified, avoid until checked |

## Verified vs unverified

**Verified from primary pages:**
- Kenney Interface Sounds: CC0, 100 files, direct download (donation prompt skippable).
- Kenney Music Jingles: CC0, 85 files, standalone download available.
- Kenney Digital Audio: CC0, 60 files; no weapon/impact categories listed.
- muted.io Performance Cars: CC0, 68 WAV field recordings.
- Sonniss licence terms (from [sonniss.com/gdc-bundle-license](https://sonniss.com/gdc-bundle-license/) and [sonniss.com/gameaudiogdc](https://sonniss.com/gameaudiogdc/)): commercial use, no attribution, no redistribution as sound effects, AI training prohibited, no account or login stated for download.
- Freesound FAQ ([freesound.org/help/faq](https://freesound.org/help/faq/)): CC0 permits commercial use; downloading requires a logged-in account. This fails the no-login bar, but CC0 content can still be sourced from it by hand.

**Unverified (search snippets or third-party sources only):**
- Everything on OpenGameArt. The site returned HTTP 403 to the fetch tool, so every OpenGameArt licence, file count and format in this doc is from search snippets. Each must be checked on its own page before use. OpenGameArt licences are set per upload and can differ by file.
- Kenney Impact Sounds (only third-party copies found).
- Sonniss bundle contents and sizes (two sources disagree on the 2026 bundle size and on which bundle is current).
- Nightshade pack licence.

## Licence notes

- **CC0 (Kenney, most OpenGameArt uploads, Freesound CC0 samples):** no attribution, commercial use, modification and redistribution allowed. Attribution is not required, though some uploaders ask for credit anyway.
- **Sonniss GDC EULA:** commercial use and no attribution are allowed. The restrictions that matter here:
  - No redistribution "as sound effects", including modified versions and sample or asset packs. Sounds may be used inside a finished game, film or app.
  - No AI training.
  - Sonniss's own torrents may be seeded, but re-uploads by others are not covered.
- **CC-BY and CC-BY-SA items** were seen in the search results (for example a footstep pack on node 133264 is CC-BY 3.0, and the Q009 weapon set is CC-BY-SA 3.0). These are excluded by the bar on #70 unless the user accepts attribution.

## Open questions for the user

1. **Sonniss redistribution clause:** does shipping raw WAV files as part of a mod download count as "redistribution as sound effects"? If yes, Sonniss sounds can only sit inside finished game builds, not in a mod or asset release. Decision needed before adopting Sonniss.
2. **Accept attribution-licence content** (CC-BY) for gaps like knife and bazooka? Not found yet at CC0.
3. **Accept a paid pack** (for example the Nightshade gunshot pack at $3) for guns? It would cover the gap that free packs leave.

## Suggested next steps

1. Open each OpenGameArt page in the table, confirm licence and file list, and mark its status.
2. Confirm Kenney Impact Sounds on kenney.nl (search for it directly, or check the Kenney pack list).
3. Search for a CC0 or permissive knife/melee pack and a rocket launch/whoosh pack, which are both not found in this pass.
4. Decide on the Sonniss question before downloading any bundle.

## Sources

- Kenney Interface Sounds: https://kenney.nl/assets/interface-sounds
- Kenney Music Jingles: https://kenney.nl/assets/music-jingles
- Kenney Digital Audio: https://kenney.nl/assets/digital-audio
- Sonniss GDC bundle page: https://sonniss.com/gameaudiogdc/
- Sonniss GDC licence: https://sonniss.com/gdc-bundle-license/
- Sonniss 2026 bundle (secondary): https://bedroomproducersblog.com/2026/03/16/sonniss-gdc-2026-bundle/ and https://rekkerd.org/sonniss-releases-gdc-2026-game-audio-bundle/
- Freesound FAQ: https://freesound.org/help/faq/
- muted.io Performance Cars: https://muted.io/performance-cars
- OpenGameArt pages (search snippets only, 403 to fetch): linked inline in the table
- Kenney Impact Sounds (third-party): https://godotengine.org/asset-library/asset/edit/7694 and https://downloadforfree.gumroad.com/l/free-impact-sounds-pack
