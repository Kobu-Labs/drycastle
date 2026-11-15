# 08 Assets

Real models, clips and dressing. **Blocked:** the Gang Warfare pack was not found on disk on 2026-11-14 (searched `~/downloads`, `~/Downloads`, `~`); ask where it is.

## Facts
- Wanted (repo-root note `gang`): `SM_Chr_GangBoss_01`, `SM_Chr_GangMember_Male_03`, `SM_Chr_StreetGirl_01`, `SM_Chr_GangMember_Male_02_FixedScale`; from POLYGON Military `SM_Veh_Van_Destroyed_01` (door must open and close).
- Military: extracted at `assets-private/synty/military/polygon-military-01/`, not converted (no GLB, no atlas, no import cache).
- Conversion: `assets-private/synty/README.md`; `tools/export_glb.gd` (kinds char, fbx, prop, attach), `build_pets.sh` as a one-command example, `check_glb.gd`.
- Clips: rows in `tools/clips.json`, then `anim_extract.py`, `anim_convert.gd`, `anim_bake.gd` (retarget baked offline on one character's rest pose; `anim_retarget_probe.gd` to check a new rig). 31 baked now; no run, jump, kneel, swing, knockdown.
- Runtime: `SyntyLib.Instance(model, atlas)`, `SyntyLib.Clip(name)`; bone attachments as `DemonView.cs` (`Hand_R`, `Head`); procedural pose as `SitPose` in `CitizenView.cs`.

## Do
- Convert the four characters and their atlas set; check the rig is the 50-bone one (probe), re-bake if the rest pose differs. Boss = GangBoss, Enforcer = Male_03, Thief = Male_02 or StreetGirl; a random texture look of the pack as shipped.
- Convert the van: body and the side door as separate nodes with a hinge/slide pivot; wreck look for Destroyed.
- Bake clips (masculine + feminine where split): run, sprint, jump running, land, crouch idle and transitions, kneel enter/loop/exit, light combo swings (pipe), pommel strike or counter shove (the unarmed "punch"), hit react, knockdown begin/loop/end, stun loop, death, a few taunt and talk idles, drink sip (smoking).
- Climb clips, made by us (no pack has one): `climb_up`, `climb_down`, `climb_work`, keyed on the Synty rig by a script (hands and feet alternating on a vertical line), baked like the rest.
- Carry overlay: one `SkeletonModifier3D` with poses TwoHands (tire, dish), UnderArm (pet), OneHand (connector, pipe at rest).
- Dressing by bone attachment: rusty pipe (Enforcer), cigarette with a lit tip, gold chain (boss, some others), speaker on a strap (09). Tattoos and the gold tooth only if the pack's textures have them.
- Member view switches from the stand-in peasants to these; capsule fallback stays.
- A `make` target or one script that redoes all of the above from the pack.

## Accept
- With the private folder: members, van, pipe and clips load (`check_glb.gd`). Without: fallback untouched.
- Nothing of the pack is tracked by git.

## 08a done: converted assets (2026-11-15)
All under `/home/jjakubec/coding/drycastle/assets-private/synty` (= `S`; private, main checkout only). Rebuild: `S/tools/build_gang.sh ~/downloads`. Details: `S/README.md` (new section), `S/glb/vehicles/raid_van.json`, `S/anim/manifest.json` (rows with `"rig":"gang"`).

**Characters** `S/glb/<name>.glb`: `gang_boss_01` (1.80 m), `gang_enforcer_01` (1.83), `gang_thief_01` (1.81, Male_02; no FixedScale variant exists), `gang_thief_02` (1.80, StreetGirl). Skeleton `Skeleton3D` child of the root; face +Z (rotate 180° as `CitizenView` does); material `synty_atlas`.
- Atlases: `gang-warfare/textures/PolygonGangWarfare_0{1..4}_{A,B,C}.png` (01-04 clothing palettes, A/B/C the pack's skin tones). Pick at random per member.
- Bones: `Hand_R`, `Hand_L`, `Head`, `Neck`, `Spine_03`, `Hips`, `Jaw`. Same 50-bone rig as the priest, NOT the peasants' finger naming: the gang has its own baked clips, do not play peasant clips on them.

**Clips** `S/anim/baked/<name>.glb` (seconds; L = loop). `_masc`/`_femn` pairs (femn for `gang_thief_02`): `run_f` 0.733 L, `sprint_f` 0.633 L, `jump_running` 2.4, `land_soft` 0.767, `crouch_idle` 1.8 L, `stand_to_crouch` 0.667, `crouch_to_stand` 0.667, `kneel_enter` 3.2, `kneel_loop` 5.0 L, `kneel_exit` 7.5, `drink_sip` 4.0 (smoking), `posture_aggressive` 2.767 L, `taunt_laugh_point` 3.567, `taunt_shake_fist` 2.7, `taunt_beckon` 3.0, `taunt_menacing_fists` 4.167. Gang versions of shared ones: `walk_f_masc_g`, `walk_f_femn_g` 1.067 L, `idle_standing_masc_g`, `idle_standing_femn_g` 1.8 L.
- Combat (no split): `swing_a` 0.833 (hand moves 0.07, peak 0.40), `swing_b` 1.5 (starts held: seek to ~0.85, peak 1.1), `swing_c` 2.233 (seek ~1.45, peak 1.93), `pommel_strike` 1.667 (0.53, peak 0.87), `counter_shove` 1.333 (0.53, peak 0.70), `hit_react_f`, `hit_react_b` 0.867, `knockdown_begin` 0.733, `knockdown_loop` 1.967 L, `knockdown_end` 4.0 (holds to ~2.3 then gets up), `stun_loop` 2.267 L, `death_front_sword` 1.467, `death_back_sword_g` 1.733, `death_left_sword_g` 1.167.
- Climb (ours, `S/tools/climb_make.gd`): `climb_up` 0.9 L, `climb_down` 0.9 L, `climb_work` 1.6 L. Wall plane at z = +0.27 in model space: put the model origin 0.27 m from the surface, facing it. One loop = 0.44 m of travel: playback speed = climb speed / 0.489.

**Pipe grip**: child of a `BoneAttachment3D` on `Hand_R` with origin (−0.0879, 0.0391, 0.0163), quaternion xyzw (−0.4785, −0.3531, 0.3529, 0.7224); Synty weapons have the blade along +Y, grip at the origin. `pipe_01` spans y −0.566..0.335: rotate 180° about Z and shift +0.215 along its Y.

**Van** `S/glb/vehicles/raid_van.glb`, atlas `glb/atlas/military_land_vehicle_01_burnt.png`: nodes `body`, `door`. 2.417 wide (x) × 1.860 high × 4.753 long (z); forward +Z; origin centre, y 0 at the ground. Door on x < 0 (the right side facing +Z): closed at (−0.722, 0.763, 0.068), open at (−0.842, 0.763, −0.932). No glass, hollow shell. Also `car_small_destroyed.glb`, `car_medium_destroyed.glb` (not used in v1).

**Props** `S/glb/props/` (Gang atlas `gang-warfare/textures/PolygonGangWarfare_01_A.png`): `pipe_01`, `crowbar_01`, `bat_01` (long axis Y), `cigarette_01`, `cigarette_lit_01` (along +Z, 0.147 long), `lighter_01`, `jewelry_pile_01`. Military atlas `glb/atlas/military_01_A.png`: `tire_01` (0.725 across, lies flat, axis Y), `boombox_01` (0.94 wide: too big to carry), `speaker_01..04`, `stereo_speaker_01` (0.3-0.5 m: scale down for the hip speaker), `dish_01`, `downpipe_connector_01`.
- Not in any pack: a necklace / chain (the generated `gold_chain.glb` stays), tattoos and a gold tooth are whatever the textures show.

**Unseen**: the look of the climb poses, the pipe grip, the van's doorway (back faces may show through).
