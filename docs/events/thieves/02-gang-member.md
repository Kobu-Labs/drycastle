# 02 Gang member

The person of a Raid: entity, Hit count, three kinds, walking, melee. No stealing, no van yet: `/event thieves` spawns one crew at a road point near a player (dev fallback) and they idle, guard and fight.

## Reuse
- Pattern of `Citizen` / `CitizenCrowd` (`godot/ShooterCore/Citizens/`): `IWorldEntity`, world interface, hit-id maps, `AddTargets`, `Follow(route, dt, speed)`.
- Routes: `BotNav.Route` (`godot/shooter/BotNav.cs`), a few ms each, tick thread. Bots are rationed to one per tick (`ServeBotRoute`, `NetServer.Bots.cs`): put gang members on the same ration (a shared queue).
- Hit count rules: the Bigfoot's (`godot/ShooterCore/Events/Bigfoot*.cs`), here with headshot = 2.
- Player damage from an entity: as the Bigfoot does through `Deal` with a `SpecialWeapons` id.

## Do
- `GangMember : IWorldEntity`, new wire type (54), own hit-id range (see the list of ranges in `PetKeeper.cs`), own layout of the generic `WorldState`: owner = look, state byte = state, hp byte = kind + alive + carried Loot kind (leave room; ticket 04 uses it).
- Kinds Thief, Enforcer, Gang boss: hits (20 / 35 / 80), speeds, melee (fist 10, pipe 25, 1 swing/s, reach 2 m, windup) in `thieves.json`.
- State machine: Idle, Walk, Guard (Enforcer follows a Thief or stands by the van point; boss stands), Chase, Windup, Swing, Flee (to the map edge, then ends), Dead. Stealing states come in 04.
- Aggro, kept per Raid: a player who hit any member, or who is within `crowd_m` of a working Thief (hook for 04). Dropped when the player dies or is over `give_up_m` away. Enforcers and the boss chase; a Thief with free hands joins.
- Hits: `LiveTargets`, `RewoundTargets` (decide: they run, so rewind like the Bigfoot's `PoseRing`, or document why not), `MatchSim.ApplyDamage` / `Deal` routing: hit marker, kill = small XP, no kill feed, streak or assist. Run over by a vehicle = dead (see how a citizen or pet is road-killed).
- `NonEnemyTargets`: gang members are NOT in it (turrets and killstreak weapons shoot them). Bots do not see them.
- Dead: lies `corpse_seconds` (10), then ends.
- Client `NetClient.Thieves.cs` + a view: without the private pack a coloured capsule per kind (Thief, Enforcer with a stick, boss bigger). With it: `SyntyLib` peasant models as a stand-in until ticket 08. Clips by state.
- `ThiefGang` (pure): owns the members of one Raid, the aggro list, the route queue.

## Accept
- Tests (Strict FSM): hit count per kind incl. headshot and explosive weight; aggro on hit and on give-up; windup then damage only if still in reach; Flee ends the entity at the edge; route ration.
- A member is never invisible (capsule fallback).
