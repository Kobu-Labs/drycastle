# Shooter v1: real-world weapon reference numbers

Research for issue #71 (map #68, "Shooter v1"). Goal: a grounded starting point for the weapon data file. Target context: 100 HP player, 16-player FFA, village map.

Status key:
- **Verified**: read on the source page in this research pass (WebFetch of the page itself).
- **Snippet only**: seen in a search-result summary, page not opened. Treat as a lead, not a fact.
- **Unverified**: no usable source found. Needs a second look before a number is chosen.

Source URLs are listed in the Sources section at the end.

## 1. Real-world values

| Weapon | Parameter | Real-world value | Source | Status |
|---|---|---|---|---|
| AK-47 | Cyclic rate of fire | 600 rounds/min (10 rounds/s) | [S1] Wikipedia | Verified |
| AK-47 | Practical rate, semi / burst | 40 rpm semi, 100 rpm burst | [S1] | Verified |
| AK-47 | Magazine | 30 rounds (standard). Other box and drum sizes exist (20, 40, 50, 75 round) | [S1] | Verified (standard 30). Sources disagree on which box sizes exist |
| AK-47 | Reload time | Not found for the real rifle | none | Unverified |
| AK-47 | Effective range | 350 m | [S1] infobox | Verified (infobox). Snippet gives 300 m for full-auto, 400 m for semi |
| AK-47 | Muzzle velocity | 715 m/s (2,350 ft/s) | [S1] | Verified. A Sofrep snippet gives 730 m/s |
| AK-47 | Cartridge | 7.62x39 mm | [S1] | Verified |
| Beretta M9 (pistol) | Magazine | 15 rounds (standard). 17 for A3, 8/10 restricted | [S2] Wikipedia | Verified |
| Beretta M9 (pistol) | Effective range | 50 m (55 yd) | [S2] | Verified |
| Beretta M9 (pistol) | Maximum range | 100 m (110 yd) | [S2] | Verified. Conflicts with a 1,800 m figure in a snippet |
| Beretta M9 (pistol) | Muzzle velocity | 381 m/s (1,250 ft/s) | [S2] | Verified. Snippets give 365-375 m/s |
| Beretta M9 (pistol) | Cyclic rate | Not applicable (semi-auto) | [S2] | Verified (not stated on page) |
| Beretta M9 (pistol) | Reload time | Not found | none | Unverified |
| M1 bazooka | Rocket velocity | 265 ft/s (81 m/s) | [S3] Wikipedia | Verified. Snippets give 80-89 m/s |
| M1 bazooka | Effective range | 150 yd (140 m) | [S3] (page's stated figure) | Verified as stated on page. Snippets give 90-275 m, so the figure is contested |
| M1 bazooka | Maximum range | 400 yd (370 m) | [S3] | Verified as stated on page |
| M1 bazooka | Rocket weight (M6A3) | 1.53 kg (3.38 lb), warhead 1.59 kg | [S3] | Verified (the page quotes the warhead weight, not the filler) |
| M1 bazooka | Reload time | Not found | none | Unverified |
| M1 bazooka | Blast / lethal radius | Not found. The M6 round is a shaped-charge (HEAT) warhead; the page gives no casualty radius | [S3] | Unverified |
| Combat knife | Reach / swing time | Not found for a real knife | none | Unverified |

Notes on the real-world column:
- The M1 bazooka figures are for the 2.36 in. launcher and rocket. Its real rocket is a slow, short-range HEAT round, not a game-style fast projectile.
- The 60 mm "bazooka" family (M20, etc.) has very different numbers. The M1 page is used here because it matches the "bazooka" in the brief.

## 2. Popular arcade shooters

| Game | Weapon | Parameter | Value | Source | Status |
|---|---|---|---|---|---|
| CS2 | AK-47 | Base damage | 36 per body hit | [S4] csdb.gg | Verified |
| CS2 | AK-47 | Headshot multiplier | 4x (144 per headshot) | [S4] | Verified |
| CS2 | AK-47 | Fire rate | 600 RPM | [S4] | Verified |
| CS2 | AK-47 | Magazine / reserve | 30 / 90 | [S4] | Verified |
| CS2 | AK-47 | Reload time | 2.5 s (other sites say 2.43 s) | [S4] | Verified (page). A snippet says Valve reworked reload in March 2026, not confirmed |
| CS2 | AK-47 | Armor penetration | 77.5% | [S4] | Verified |
| CS2 | AK-47 | Range modifier | 0.98 per 500 units | [S4] | Verified |
| CS2 | AK-47 | Movement speed | 215 units/s | [S4] | Verified |
| CS2 | Knife | Movement speed | 250 units/s (130 when walking) | snippet from search summary, page not opened | Snippet only |
| CS2 | Knife | Primary / secondary damage | 40 / 65 unarmored (34 / 55 armored) | snippet, wiki table | Snippet only |
| CS2 | Knife | Melee range | Not found. A datamined "effective range 4096" appears on a stats page, but it is a generic field, not reach | none | Unverified |
| CS2 | Knife | Swing time | Not found | none | Unverified |
| CS2 | Pistol | Not researched | - | - | Unverified |
| CoD (Mobile) | AK-47 | Fire rate / magazine | 545 RPM / 30 | snippet, guide | Snippet only |
| CoD (Cold War) | AK-47 | Fire rate / bullet velocity | 600 RPM / 490 m/s | snippet, review | Snippet only |
| CoD (CoD4) | AK-47 | Reload | 1.5 s (add ammo), 3.25 s (empty), 30 mag | snippet, guide | Snippet only. Conflicts with another CoD4 source giving 450 or 700 RPM |
| CoD (MW 2019 and later) | AK-47 | Damage per hit, 100 HP base | Not found | none | Unverified |
| Battlefield 2042 | RPG-7 / recoilless | Projectile speed | Reduced 17% for Engineers in patch 5.3.0. No absolute speed | EA update notes, snippet | Snippet only |
| Battlefield 2042 | RPG-7 | Splash radius, damage per hit | Not found | none | Unverified |

The CoD and Battlefield rows are weak. No page in this pass gave a Modern Warfare or BF2042 number that could be opened and checked. They are listed so a reader knows what was searched and where the gaps are.

## 3. What the 100 HP target implies (arithmetic from verified values)

Using CS2 as the only fully verified arcade reference:

- AK-47 body hit: 36 damage, so 3 body hits kill a 100 HP player (108). A headshot at 4x (144) kills in one shot.
- AK-47 time to empty a magazine: 30 rounds at 600 RPM is about 3.0 s of fire.
- AK-47 time to kill, body only, from fire start: about 0.2 s between shots at 600 RPM, so 3 hits is about 0.4 s of fire plus aim time.

Real-world bazooka and knife figures cannot be turned into 100 HP hit counts yet, because damage per hit for both is unverified.

## 4. Open gaps (next research steps)

1. Bazooka blast/lethal radius and damage per hit. The search for a casualty radius was blocked by the search tool's content filter, so this gap is not resolved. Set it from game design, not from a real-world casualty figure.
2. Knife melee reach and swing interval for CS2 and for real knives.
3. Modern Warfare / BF2042 damage and base HP. Use the in-game data, or a stats wiki that can be opened.
4. Real AK-47 and M9 magazine change times, as opposed to CS2 game reload times.

## Sources

- [S1] https://en.wikipedia.org/wiki/AK-47 (opened)
- [S2] https://en.wikipedia.org/wiki/Beretta_M9 (opened)
- [S3] https://en.wikipedia.org/wiki/M1_bazooka (opened)
- [S4] https://csdb.gg/weapon/ak-47 (opened)
- Search summaries (not opened): loadoutroom.com, sofrep.com, profilerr.net, bo3.gg, metabot.gg, exitlag.com, EA Battlefield 2042 update notes 5.3.0 (ea.com), mein-mmo.de, codmunity.gg.
- Failed fetches: csdb.gg/weapon/knife (404), breezewiki.discard.no Counter-Strike knife page (bot-protection block).
