# Lets introduce a concept of events
- short, time limited in game events that usually pose a challange in some kind
- user should get a reward after participating in an event and defeating it
- events need some kind of annoucment, either trough military-like intercom, or just a hud on the screen
    - another way would be an NPC
        - the intercom would say suspicious activity was detected and we need to go to base to see more info
        - there would be an "intel" room, with a big screen - military styled
        - this screen would have all the details for the event
            - when it begins
            - what it is
            - risks
            - where (if applies)
            - some more graphics related to the event
- each event has a specific spawn chance
- events can happen somewhat periodically, but randomized, so its not within the same time of the server
- events can be triggered by admin via /event command
- additional event themed-music can be implemented
- events will despawn after X time of anyone not participating
- "/event current end" should be udes in testing, it will conclude the event as if the player won
- most dropable items from events can be picked up when a player dies with it, this cycle of picking up can be done at most 3 times, then when you try to pick it up again it says "THe item crumbles in your hands"



# Bigfoot
## Mechanics
- big foot will rarely spawn in dense forest
- bigfoot should take around 1 minute to kill, the killing of it should be action packed
### Attack
- bigfoot will haul tree trunks on the players attacking it
- additionally it can stomp, causing a shockwave to travel across the ground, knicking players away
- bigfoor can throw vehicles when it comes in contact with them
    - these will be just thrown somewhere, not get dmg
    - including when players are inside
- bigfoot can pickup a player and launch him, preferrably on a different player
## Visual
- a 3D animated asset, around twice the size of a player
- the asset should be somewhat fear inducing
## Reward
- after its killed, footsteps will appear, these will lead to a corpse the bigfoot has killed
- player can follow these footsteps
- this corpse will have a special weapon - TBD, which the player can pickup, they will loose it upon death
- additionally bigfoot corpse can be looted to get the bigfoot head - player can equip it instead of their helmetc
    - this will prevent players from getting headshot dmg

# Lochness
## Mechanics
- two bumps in the Morava river
- when players gets really close or they hit it with a boat (shooting it does nothing), the locchness will awake
- lochness wil spawn either at the top of the morava river or at the bottom, and swim to the opposite end
- lochness will attack by smashing players with its had, so we need "tentacle-like" animation system
    - this will also create a shockwave similar to players
- it can also pikcup boats into its mouth and throw them
## Visual
- not-spawned - just two bumps in the water
- spawned - a very big, possibly more than trees, lochness monster
    - a typical lochness styled monster/hydra/scylla
## Reward
- TBD

# Swat Squad in a bus
- the red bus model filled on every seat with SWAT members
- these will be very strong bots with lots of armour and strong weapons
## Mechanic
- the bus go from the main road and will stop in front of the churk
    - the bus will spawn in at road at https://www.google.com/maps/@48.3899045,16.8648597,146a,60y,257.46h,90t/data=!3m7!1e1!3m5!1s4icwdJNPUoY2N5MHL7O9Jg!2e0!6shttps:%2F%2Fstreetviewpixels-pa.clients6.google.com%2Fv1%2Fthumbnail%3Fcb_client%3Dmaps_sv.tactile%26w%3D900%26h%3D600%26pitch%3D0%26panoid%3D4icwdJNPUoY2N5MHL7O9Jg%26yaw%3D257.4552!7i16384!8i8192!5m1!1e1?hl=sk&entry=ttu&g_ep=EgoyMDI2MTExMC4wIKXMDSoASAFQAw%3D%3D and continue from there alongside the road
- the swat team will hop out of the bus, this needs to be animated
    - the bus stops
    - doors animate open
    - awat members jump down from their seat and walk to the door to exit the bus
    - once they are out they can take damage and shoot back
- armed with RPG as well
- they can place turrest as well
- they will try to stay at the main square - in front of the churk
- they should try to take cover then peak and shoot
- they cannot take dmg before they get off from the bus
- after defeat the bus can be driven (this will be the only instance how to get the bus)
    - otherwise if the event runs out they will all hop on their seat the same way as they exited and leave the village
- when they go to the village the bus does have collisons, but its immovable object, every vehicle/player standing in its way will be flinged back
## Visual
- reuse the existing bus model
- SWAT  assets will be provided
## Reward
- balistic vest can be looted from special swat soldier that actually carried it
- this will lower damage taken when hit in chest
- the bus

# Holy Mass
Settled design (2026-11-13). Numbers live in `data/holy_mass.json`.
- chant: in nomine patris et spiritus sancti, positional in the church, non trivial echo
    - asset provided later, ~1 min, loops; placeholder https://www.youtube.com/watch?v=XTMS5LgXCGQ
- announcement: church bells, audible village-wide (no intel room)
- started with `/event mass`, ended with `/event current end`; does not wait for the Bigfoot lifecycle (#264), brings the minimum it needs
## Church interior
- permanent in the model (`church_solids`), real shell collision like bunkers; new ADR extending 0009
- doors are solid outside the event, the event opens them; client hides the interior while doors are shut
- nave, aisle, bench rows, altar, sacristy door next to the altar, gallery (second floor) with seats and stairs
- the two side aisles are hollow too (as wide as the plan), open to the nave by arcades; side altars at the aisle ends, standing spots in the aisles; real windows (dark frame, breakable glass pane) in the aisle walls, the clerestory, the chancel and the front facade above the door
- door at least 2 m wide (NPC navigation, 0.5 m radius)
- blast shelter like a bunker room: a blast outside does not hurt players inside and the other way round
- pipeline exports `church.json`: seat poses, aisle path, altar and monk spots, demon wall anchors, doors
## Mechanics
- timeline: gather (~2 min) -> Mass (2 min default, chant looped) -> end
- citizens: new world entity, not bots; count = seat count (ground floor + gallery, tweakable, limited by geometry)
    - spawn staggered at house doors near the church, navmesh to the church door, scripted path to their seat
    - can be killed (one hit), not replaced, no other consequence; bots ignore them
- pastor and monks in front of the altar; pastor has basic hand gestures
- pastor killed: chant stops, monks look at the pastor shocked, Mass progress pauses (no reset)
    - ~10 s later a new pastor comes out of the sacristy door, chant resumes ~5 s after
- monks can be killed, no effect, no respawn
- player takes a free seat with interact; seated players cannot fire and can be killed
- blessing needs: seated at Mass end, seated for at least 80 % of the Mass, not a sinner
    - sinner = killed a citizen, monk or pastor during this event
- Mass completed: church closes, seated players are teleported in front of it facing the door, angelic text "You feel a divine blessing"
## Demon
- pastor killed three times: it turns into a demonic figure, the doors slam shut and lock in whoever is inside
- the Mass is void (no blessing this event), remaining citizens flee or die
- crawls on all fours on walls and ceiling: scripted movement between baked anchors, not physics
- throws fireballs: arcing projectiles, small splash, burn
- hit-count damage model like Bigfoot, scaled by players inside
- wipe: heals, doors reopen, waits
- killed: hellish text "You feel cursed" for every player with at least one hit; doors open, the church closes only after every player leaves
### Pounce
- cut-scene attack (protocol v40, `holy_mass_demon.json` `pounce`; `pin_s` 0 turns it off): at a living player in the church, in sight, on the ground (not seated at the Mass), within `range_m` (16); `chance` at each dwell end, `cooldown_s` 30 after a pounce, first no sooner than `first_after_s` 12 of fighting; never the same victim twice in a row while another qualifies; one at a time
- wind (`wind_s` 0.9): demon crouches facing the victim, victim marked (event 223 `DemonPounce`, stage 1; demon state 10); victim's camera turns to the demon and the mouse look is locked, they can still move
- flight (state 11): leap arc (leap speed) that homes on the victim's feet
- pin (`pin_s` 5, state 12): victim knocked flat on their back where they stand, head away from the demon's approach, body held by the server like a Mass seat (no movement, no weapon, no use), takes no damage from anything (`MatchSim.SetHeld`), is not a hit target; others can shoot the demon. Demon stands over them on the floor, head lowered, biting (jaw snaps ~2.5/s). Victim's camera drops to 0.25 m looking up at the demon's face (muzzle `face_distance_m` above their eyes), shake on landing, red pulse and shake on each bite, weapon and crosshair hidden; `bite.ogg` plays flat and loud for the victim, positional for the rest
- release: health set to exactly `leave_hp` (10), above or below what it was; victim gets up with `immunity_s` of immunity to the demon's fire and flame; demon leaps off to a wall or the ceiling (forced leap)
- aborts (stage 0): victim leaves, dies or leaves the church before the landing (no pin, cooldown still applies); victim leaves during the pin, or the demon dies (victim let go, health unchanged). `NetServer.PounceSweep` lets go of anyone nothing holds every tick; the event ending does the same
- reduce motion: no shake or jolt, softer flash
## Reward
Both are a status, not an Item: no slot, no drop. They survive a round change and end when the player dies.
- underwent the Mass - divine blessing
    - lethal damage is negated once: full HP in place, ~1 s invulnerable, golden flash; streak kept, attacker gets no kill
- killed the demon - devils curse (pure buff)
    - adds fire damage to bullets, these visually set players ablaze, dealing 0.5hp/s for 10s
## Assets
- Synty packs where they fit, instead of modelling: citizens, pastor, monks, demon, benches, altar and other interior props
- picks (pack in brackets):
    - pastor `SM_Chr_Priest_01`, monks `SM_Chr_Monk_01`, lectern `SM_Prop_Church_Lectern_01` (POLYGON Fantasy Kingdom, Godot)
    - pew `SM_Prop_Pew_01` / `SM_Prop_Pew_Large_01`, holy water `SM_Prop_Holy_Water_01`, altar `SM_Prop_Altar_Table_01` (POLYGON Dark Fantasy, Godot)
    - citizens `SM_Chr_Male_Peasant_01`, `SM_Chr_Female_Peasant_01`, `SM_Chr_Female_Peasant_02` (POLYGON Fantasy Characters, Unity package: meshes are in `Models/Characters.fbx`; 15 texture variants 01-05 x A-C for variety)
    - demon `SM_Chr_Demon_01` (POLYGON Horror Mansion, Unity package: mesh is in `Models/Characters.fbx`, import the FBX and rebuild the material by hand)
- animations: Synty animation packs (Emotes And Taunts, Idles, Base Locomotion, Sword Combat), the Polygon-rig variant of each clip (`A_POLY_*`; the `A_MOD_*` twin is for the Sidekick rig), converted into `assets-private/synty/anim/`
    - citizens: death `SWD_Death_B`, `SWD_Death_L` (sword not shown); idle picked at random from the idle clips; walk from Base Locomotion; sit down and sitting are code-built (no clip exists)
    - some citizens take a standing spot instead of a seat and pray kneeling: `IDL_Pray_Kneeling_Enter/Loop/Exit_Masc`
    - Mass completed: some citizens clap (`EMOT_Celebrate_Clapping_Polite_Femn`), on standing up or at their standing spot
    - pastor gestures: `EMOT_Angry_TantrumStomp_Femn`, `EMOT_Angry_StranglingMotion_Femn`, `EMOT_Reproach_CalmDown_Femn`, `IDL_PointHand_Index_F_Femn`, `IDL_Pray_Standing_Enter/Loop/Exit_Femn`, `IDL_Thoughtful_R_Enter_Femn`, `IDL_Thoughtful_L_Loop/Exit_Femn`, `IDL_Plead_F_Masc`; death and walk as citizens; Mass survived: `EMOT_Sporty_Flex_Combo_Femn`
    - monks shocked: `EMOT_Reproach_CalmDown_Femn` or `EMOT_Sad_Crying_Masc`, very rarely `EMOT_Celebrate_Dab_Femn`; death and idle as citizens
    - demon: scream `EMOT_Aggressive_Roar_Low_Masc`; crawl on all fours, creepy head movements and emotes, and the fireball throw are our own procedural system (no clips)
    - transformation: the pastor turns into the demon in an animation with lots of fire, then the demon drops on all fours
- the church shell stays the code model (it fits the real footprint); Synty props are placed inside it at `church.json` poses
- Synty is paid: only in gitignored `assets-private/`, never in this public repo (#265); no fallback models, the event needs the pack
## Implemented (ticket 3: Mass event, pastor, sitting)
- `/event mass` starts, `/event current end` concludes as if completed (blessing computed), a second `end` removes what is left; `Completed` lasts `completed_seconds`, then everything is removed. Core: `HolyMassEvent` (`ShooterCore/Citizens`), server: `NetServer.Mass.cs`, numbers in `holy_mass.json` `mass`
- phases `MassPhase`: Idle, Gather, Mass, Completed, Void. Hooks on `NetServer`: `MassPhaseChanged` (doors), `PastorTurns` (demon), `GrantBlessing(playerId)` (status system, stub)
- pastor and monks are citizen entities with a role (hit-point byte = `role * 2 + alive`); states Standing and Clapping added
- seats: citizens fill every seat but `player_seats` kept ones; a player takes a kept seat or one no citizen has (a killed citizen's seat after its body is gone); never a citizen's seat, never a standing spot
- seated player: body and weapon not simulated, look still taken; interact or jump stands up; replicated as a crouch at the seat feet (no sit pose for the player character); shot as a crouching figure
- Mass time = progress (paused while the pastor is gone); seated time counts only while it progresses; ended early the share is measured against progress so far (none: every seated non-sinner)
- audio: `chant_sound` loops positionally with a reverb bus (no asset yet; a missing file is skipped and logged once); `bells_sound` plays once everywhere (`assets/drycastle-generated-sounds/church_bells.ogg`, made by `scripts/make_church_bells.py`)
- bells: ring when the gather starts; the first citizen leaves `bells_lead_seconds` later (the gather clock includes the lead)
- a killed monk or pastor (kills 1 and 2) lies until the event is cleared (citizens' corpses still expire); dead monks do not react to later pastor kills
- the Mass begins `start_delay_seconds` (4) after the last citizen still coming has reached its place (dead ones do not count, nor the bells' lead); `gather_seconds` is the upper limit
- when the pastor turns (phase Void) every living citizen (walking, seated, kneeling, standing) sprints home at `flee_speed` (6 m/s): scripted route out, then the walk route backwards to its spawn point, removed there (citizen state 7 Fleeing, protocol v39; client clip `sprint_up25f_femn`, the walk sped up when it is missing). Killable while fleeing; the doors do not stop them. Monks stay and burn; the demon's fire spares citizens
- a monk who sees the pastor killed picks one of calm down, crying, `emote_taunt_shhh_femn` (a third each; 3% dab)
- third pastor kill: the pastor entity is removed in the hit (no death clip, no corpse) and the demon transforms at once (`transform_delay_s` is 0)
## Implemented (ticket 4: demon and curse)
- `/event demon` turns the pastor now (`HolyMassEvent.ForceTurn`); the third pastor kill does the same. Numbers: `holy_mass_demon.json` (`fight` section: delays, turning, climb, door grace, corpse time). Server: `NetServer.Demon.cs`, client: `NetClient.Demon.cs`, `DemonView.cs` (body, head, emotes, death), `DemonPose.cs` (crawl modifier), `DemonFx.cs`
- stages: delay (`transform_delay_s`, 0: none) -> transforming (the dead pastor is removed, everyone else in the church dies where they stand, the demon burns into being; not hittable) -> climbing (scripted leap to the first anchor) -> fighting (`DemonBrain`) -> dying (corpse time, then the event is `End`ed)
- doors: shut while a living player is inside; empty church (a wipe, or nobody was in when it turned): they open and the demon waits (it heals on a wipe); a player inside shuts them after `door_grace_s`. Killed: doors open, `_closeWhenEmpty`
- wire: world entity 52 (owner field = octahedral surface normal, yaw/pitch = facing, state `DemonState`, hp byte = share of hits left); events 216 fireball (clients fly the arc), 217 impact, 218 cursed toast. Hit id 39999
- fireball damage and burn are the victim's own (weapon "Burning", no kill credit); hits on the demon count by weapon kind (splash weapons explosive, special weapons and vehicles vehicle, the rest bullet)
- gait: diagonal, driven by distance, two-bone IK per limb to footholds on the surface plane (`DemonPose`)
- roar / leap / flame (protocol v37, `holy_mass_demon.json` `roar`, `leap`, `flame`; each off when its key number is 0): `DemonBrain` phases Roaring (on wake, every `interval_s`, at `health_shares`), LeapWind + Leaping (scripted arc to an anchor on another surface: opposite wall, wall to ceiling and back; server-side position; about every 12 s: `leap.chance` at each dwell end after `cooldown_s`, certain after `due_s`), Breathing (windup, then a cone that burns every `tick_s`); replicated as demon states 6-9, event 222 `DemonFlame` names the target so clients aim the stream
- church decor (client only, from `church.json`, `ChurchDecor.cs`): wooden cross hung in front of the retable above the altar (placed from the altar's pose and the chancel outline: `ChurchDecor` constants `WallInsetM`, `RetableM`), burns and turns upside down while a demon exists, upright again after; warm interior lights (no shadows; chandeliers `ChandelierDropM` under the lowest ceiling anchor) go out when the demon appears
- church paintings (client only, `ChurchPaintings.cs`): 12 `painting` props in `church.json` (`pos` canvas centre on the wall face, `yaw`, `w`/`h` metres; `PAINTINGS` in `church_interior.py`) are a gilt frame plus a canvas; calm pictures are painted in code, and while a demon exists each burns through (shader dissolve with ember edge, staggered over `SpreadS`) to a `paintings/hell_01..09.png` of the private pack (best aspect match, cropped by UV; painted red/black fallback) and pulses faint red
- demon size: `scale` (1.6) in `holy_mass_demon.json`; model, gait stride, hit capsule (`HitTarget.Scale`), body height, muzzle and mouth offsets follow it; the other lengths in the file are those of a scale 1 demon
- movement: the crawl picks goals 2-6 links away, preferring another surface and the ceiling (`surface_change_bonus`, `ceiling_bonus` plus hunger); dwells are short (`dwell_s`)

## Tickets
1. Enterable church (pipeline, ADR, `church.json`)
2. Citizen NPC entity (walk to seat, sit, die)
3. Mass event and blessing (timeline, pastor, sitting, status concept, minimal `/event mass`)
4. Demon and curse

# Carousel/Carnival/Fair
- techno music
# Election
# Nazis in bunker

# Thieves storming the village
- rytmus songs playing from their cars


- waves 
    - walk
    - motorbikes
    - cars - rusty damanged
## Visual
- rats will ocasionally run from the van and attack the player (jumping on them)
- lit cigarettes in mouth
- golden chains
- neck tattoos
- missing teeth
- golden tooth
- latino colored people
## Mechanic
- first wave arrive in a van (more vans can come later) and they haul their loot in the van, then loop the stealing again
### Audio snippets
- when player is walking near the thieves they have a chance to say something to the player
- these recordings will be provided by us as assets
    - this are in downloads - around 15 couple seconds recordings
- audio volume should be proximity based
- note to ourselves:
    - dig more
    - jaj sak co je
    - more naker
    - co je more
    - sokeres mores
    - mandze tuke
    - nemas cigaretu?
    - dig on
    - jaj dig raklo
- the soudns will be ran trough a pipeline to adjust the voice
    - we aim for a raspy voice, as someone who has smoked a lot of cigaretes
- music playing
    - some NPC models will have  asmall "repracik" - a JBL-like speaker
    - songs from later provided assets will play from these
### Stealing
- stealing tires from cars and carrying them (the car will have an actual missing tire)
- climb electricity poles and stealing the connectors (no actual missing piece on the pole)
    - the electricity pole has a 5% chance to electrocute the thief (visual electircy beams and whizzing sounds) 
    - they drop on the ground (ragdoll), unconcious for couple seconds, they try again
- add satelites to buildings
    - they can steal the satellite as well
- add pets to gardens
    - pets can be shot or road killed
    - some gardens have a chance to spawn a pet (assets will be later provided, for now just dogs and catsi, hens, etc just small)
    - the thief can sometimes chose to jump the fence, grab the pet under their arm, and run it to the haul van
    - player needs to be careful not to shoot the pet, or it will die
    - when player kills the thief carrying the pet, the pet will pathfind to that garden it came from

## Design (grilled 2026-11-14)

Terms: **Raid** = one van and its crew. **Gang member** = Thief, Enforcer or Gang boss. **Loot** = a stolen tire, pet, dish or pole connector. Tickets: `docs/events/thieves/` (throwaway, deleted when done). Van decision: ADR 0021.

### Event
- Start: `/event thieves` only (no scheduler). Intel board entry + the standard Event announcement.
- 3 Raids. A Raid: 1 Gang boss, 2 Enforcers, 5 Thieves. Next van comes when the previous leaves or on a timer; at most 2 vans at once. Counts grow with player count. All numbers in `thieves.json`.
- Cannot be lost. Ends (won) when the last van is gone, destroyed or driven off. Runs to its end with no player near.
- No event HUD.
- State is kept after the event: Loot that left in a van is gone until the round resets; dropped Loot stays lying and can still be sent back (count capped in data).

### Gang members
- Hit count, not hit points: bullet 1, headshot 2, explosive and vehicle weapons a data weight, run over by a vehicle = dead.
- **Thief**: steals; ~20 hits; shoves with free hands; keeps hauling (faster) when carrying.
- **Enforcer**: rusty pipe; does not steal; escorts Thieves and guards the van; ~35 hits.
- **Gang boss**: stays by the van; ~80 hits.
- They attack only when interfered with: a member of the same Raid was hit by the player, or the player stands within a few metres of a Thief who is stealing or hauling. They give up when the player dies or is ~30 m away.
- Melee: pipe 25, fist 10, one swing a second, reach 2 m, a windup a player can step out of.
- Dead: lies ~10 s, small XP to the killer, no kill feed, streak or assist. Turrets and killstreak weapons treat them as enemies. Bots ignore them and they ignore bots.
- Looks: Gang Warfare models (boss `GangBoss_01`, enforcer `GangMember_Male_03`, thief `GangMember_Male_02` or `StreetGirl_01`), a random look of the pack as shipped; cigarette, gold chain, tattoos, gold tooth. Without the private assets: coloured capsules, a box van, silent.

### Van
- Its own scripted entity, not a vehicle: nobody enters it, nothing pushes it, it pushes through whatever is in its way (rams like a car). Honks at random and near players.
- Enters on a random road at the map edge, drives the road graph to a stop the server picks: the road point with the most stealable targets within ~80 m, not used by another van this event.
- 300 hits. Can be destroyed on the way in. Destroyed: that Raid is done, everything loaded goes back by itself, the crew flees on foot to the map edge.
- Leaves when full (8 Loot), when its stay timer runs out (~4 min), or when its crew is dead. Surviving crew board first.
- Crew appear at the side door when it opens and vanish into it when boarding; the door is animated, there is no sit or exit animation.

### Loot
- A Thief picks the nearest free target of a random kind (weights in data); one Thief per target.
- **Tire**: off a spawned car; the Thief kneels by the wheel; the wheel is visibly gone; the car drives as before.
- **Pet**: the Thief jumps the fence where it is nearest, grabs the pet, comes back out the same way with it under one arm. A carried pet can be shot and dies. Carrier killed: the pet walks home by itself.
- **Dish**: the pipeline places satellite dishes on houses (seeded chance, a wall under the eave facing the road, in `objects.json`, always in the village); a generated small model. The Thief climbs to it.
- **Pole connector**: the Thief climbs the pole; nothing goes missing on the pole. 5 % chance of electrocution: beams and whizzing at the top, the Thief falls, lies knocked down a few seconds, gets up, tries again; it costs no hits.
- Carrier killed: the Loot lies where they fell. Another Thief may pick it up. A player holds use ~1 s on it to send it back to its place (interrupted by moving off or taking a hit).
- Animations: the climb clip is made by us (no pack has one); carrying is the run clip with a procedural arm pose (as `SitPose`); kneel, jump and land, pipe swings, shove, knockdown and stun come from the Synty packs.

### Audio
- Voice lines: the 15 recordings (`~/downloads/New Recording*.mov`) baked through one raspy chain (`make thief-voices`, ffmpeg, the chain as a JSON file, to be tuned by ear) into `assets-private/audio/thieves/voice/`.
- The server decides a line: a member with a player within ~8 m rolls a chance every few seconds, a cooldown per member, one line at a time per Raid; every client near hears the same line from that member, to ~25 m.
- Each van loops one song (positional, ~60 m, stops when destroyed). A member has a 5 % chance to carry a speaker playing another song (~30 m); it stops when they die and drops as a plain Item a player can put in the Bag (silent, no slot).
- Songs: any `.ogg` in `assets-private/audio/thieves/songs/`, dropped there by us; an empty folder is silence. Picked: youtube `vUXzn-xTeA0`, `odp6sdJNUtg`, `D5F6tjfx-nY`.

### Reward
- Event XP to players who hit a member or sent Loot back.
- A Gold chain Item (new Neck slot, pickup limit 3) on the corpse of each Gang boss. A boss who drives off alive keeps his.

### Not in v1
- Rats; arrivals on foot, on motorbikes and in the destroyed cars; a scheduler; bots taking part.


# Pets Detour
- this will be a separate ticket, which needs good design
- pets will be on one parcel and "live" only there
- we can add assets from the Simple DOg Pack - later downloaded
- poop asset can spawn on that parcel as well
- reminred to ourselves, ignore in v1
    - saved dog does a backflip


# Rozhlas detour
- when rozhlas event is active, the doors to the Obecny Urad building will open and players can enter the interior
- i will provide better instructions on how to design the interior of the building later
- there will be couple rooms with around 3 workers in total
    - fat mayor in the chair
    - two desks with computers, one will have a female worker
    - one dictation table with the microphone where the female sits - she "is" the one that speaks to the mic when rozhlas annoucment is made
- all non-trivial sounds within the building - ei only shoots, kills, explosion etc, no footsteps and jumps, will be broadcast trough the megaphones
- additionally players can kill the annoucer, take the set, and V to speak, their mic input will be broadcast trough the megaphone network
- players input will have around 2 second delay and must be ran trough the same sound altering pipeline as the annoucment itself was


# Zumpar
# Kozkar

## Design (grilled 2026-11-14)

Terms: **Detour** = the Urad being open during a broadcast. **Hijack** = the 3 min after the announcer is killed. **Set holder** = the player on the mic.

### Rozhlas becomes server-owned
- Today: client-only, each client's wall clock, server has no "rozhlas active". Changes to: server schedules with its own clock, sends start + variant + offset; late joiners get the phase.
- `RozhlasPhase` + `StateMachineSpec`: Idle → OnAir → Hijacked → Idle.
- Not an EventBook Event. Other events run alongside; their announcements play over a hijack and don't end it.
- Detour opens on every scheduled broadcast (10 min slot) and on `/event rozhlas`. Intel/event announcements over the megaphones don't open it.

### Building
- Built (ADR 0020, `pipeline/src/drycastle_pipeline/office_interior.py`): hollow south-west bar (clerks' office, mayor's room) and entry in the middle bar; middle bar's road wall set back 1.4 m behind a covered walkway; entrance in the set-back wall; furniture as `props` of `urad.json` (Synty SIMPLE Office models placed by the game, boxes drawn without the pack); `urad.json` exported with the map.
- CCTV station in the mayor's room: reserved, issue #270.
- Outside a detour: sealed, interior not rendered, workers don't exist.
- Doors never trap: interior always exitable (see the door, below: a shut door shows everyone out), no kill. Bots never enter.
- Entrance door (`doorway` of `urad.json`, next to `church.json`): the church door's box and look (`DoorNode`), open exactly while a detour is open (OnAir/Hijacked), shut at Idle; server-owned, wire event 188 `UradDoor`, a joiner is told. When it shuts, every living player in an interior room or the doorway is moved ~1.5 m outside it (`Urad.EvictionPoint`), so nobody is locked in for the 10 min. No `urad.json`: no volume, workers, table or door. Worker `pos` is the seat (chairs are baked into the map).
- No megaphone horn on the Urad: the feed is faint inside (nearest pole 66 m).

### Workers
- Citizen entity, new roles: Mayor (fat, in the chair), Clerk x2 (one female), Announcer (female, at the dictation table).
- Spawn seated at spots from `urad.json` (`SitPose`), stay seated when shot at. No lip sync or talking animation.
- Passive, one-hit kill, no score, no kill feed, no penalty.
- Respawn fresh every broadcast.

### Hijack
- Announcer killable any time the doors are open, including before/during her announcement. Kill is the only way to free the mic.
- Kill: announcement cuts instantly (scream, mic thump, feedback over the megaphones).
- Clock: exactly 3:00 from the kill, replacing whatever remained. One per broadcast; not refreshed by taking the set, holder deaths or other kills. Runs even if nobody takes the set.
- During it: doors open, mic live, combat rebroadcast on, surviving workers stay. All stop together.
- No warning before the end: hard cut with a click. No HUD timer, no takeover notice.
- No kill: broadcast ends on its normal schedule, no player mic.

### The set
- Fixed at the dictation table, not carried, not an item.
- Hold E for 1 s at the table once the announcer is dead (prompt + ring, like a care package): the player sits down in her chair (church sitting: held body, no weapon; E or jump stands up) and only then holds the set. Standing up for any reason releases it.
- One holder at a time. Released on death, disconnect, moving more than ~2 m away, or hijack end. Next player can take it at once. No talk cap.
- New rebindable action `rozhlas_talk`, default V, hold to talk, active only for the holder (`vehicle_view` keeps V in vehicles).
- HUD: "ON AIR" indicator while held, nothing else. No local monitoring.
- Mic: OS default input, `enable_input` on, no device picker or settings UI in v1.

### Live feed
- One feed carries player voice and in-building combat sounds; both get the announcement's exact pipeline.
- Upstream: clean Opus (Concentus, 16 kHz mono, 20 ms frames) on a new unreliable ENet channel; server accepts frames only from the set holder. Protocol bump.
- Combat mix, server-side: every Shot / Explosion / Death (players and workers) positioned inside the Urad volume while OnAir or Hijacked, anywhere in the building, no distance falloff. One dry sample per sound id, no random pitch or layering. Kills use Death + victim position. Footsteps, jumps, reloads, swaps, pickups: never.
- Pipeline: server pipes the mix through a live ffmpeg subprocess running the same filter graph as `scripts/make_rozhlas.py`. Filter parameters live in one file read by both. One process per mic-live phase; echo taps randomised once at its start.
- Only frames carrying sound are sent: a live mic nobody uses costs no bandwidth.
- Delay: fixed 2 s on the server between input and broadcast. Buffer drains on holder death/release: last words, then the shot, then silence.
- Downstream: degraded stream as Opus at ~32-48 kbps to every client, running whenever the mic is live. Clients add ~100 ms jitter buffer, decode and play with the announcement's nearest-pole gain and low-pass. No client DSP.
- Heard by everyone, all teams.
- ffmpeg missing or crashed: logged once, feed silent for that phase; doors, workers and the mic interaction still work. Never a dry fallback.
- ffmpeg becomes a server runtime dependency (`deploy/Dockerfile`, dev machines).

### Slices
1. Server-owned rozhlas + `RozhlasPhase` machine.
2. Urad shell, doors, workers. Postponed (interior instructions).
3. Server feed: ffmpeg subprocess, shared params, Opus downstream, client playback.
4. Server-side combat mix.
5. Mic capture, upstream, hold-V, set holder.

1 and 3 don't depend on the building. 4 and 5 need the Urad volume and the dictation-table spot from 2.
