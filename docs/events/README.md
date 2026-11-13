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
- no pack ships animations: walk, sit, gestures, shocked look, death and the demon crawl need another source
- the church shell stays the code model (it fits the real footprint); Synty props are placed inside it at `church.json` poses
- Synty is paid: only in gitignored `assets-private/`, never in this public repo (#265); every Synty asset needs a CC0 or code fallback so the game runs without the pack
## Tickets
1. Enterable church (pipeline, ADR, `church.json`)
2. Citizen NPC entity (walk to seat, sit, die)
3. Mass event and blessing (timeline, pastor, sitting, status concept, minimal `/event mass`)
4. Demon and curse

# Carousel/Carnival/Fair
- techno music
# Electio
# Nazis in bunker
# Gypsies storming the village
- rytmus songs playing from their cars
