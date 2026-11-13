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
- in nomine patris et spritus sancti playing in the churh
    - i will provode the asset, it will be with non trivial echo, as if it was really played in the church
## Mechanics
- the church opens its interior - models needs to be updated
- citizens NPCs will start exiting their houses and go to sit on the benches in the churh
- citizens can be killed
- citizens will continue to enter the church and take seats unless its capacity was filled in terms of seats
    - this should be tweakable, but limited by the geamoetry of the churck
    - we can place seats along the seconds floor as well
- the pastor and some more monks will be in front of the altar
- when pastor is killed the song will stop playing, the monks will look at the pastor shocked
- a closed door will be next to the altar, when pastor is killed after some while a new one appears and takes their place, song will start playing shortly after
- some basic hand gestures should be added to the pastor
- player can take a seat and sit trough the whole Holy Mass
    - when its completed everybody the church will just close and the player will be teleported in front of it, looking ats its door
    - a very angelic like text will appear in the middle of the screen, something like "You feel a divine blessing"
- if the pastor is killed three times it will turn into a demonic figure, crawling on all fours on the walls, throwing fireballs on fireballs at the players
- killing the devils will not close the churck immediately, only after every player leaves,
- after killing the devil a hellish screen of "You feel cursed" will appear
## Reward
- underwent the Mass - divine blessing
    - when player dies they can be brought back from death (only once)
- killed - devils curse
    - adds fire damage to bullets, these will visually set players ablaze, dealing around 0.5hp/s for 10s

# Carousel/Carnival/Fair
- techno music
# Electio
# Nazis in bunker
# Gypsies storming the village
- rytmus songs playing from their cars
