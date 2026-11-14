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
