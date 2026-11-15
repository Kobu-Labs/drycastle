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
- Aggro, kept per Raid: a player who hit any member (any source, ramming too), for `forget_seconds` after their last hit (each hit starts it again). Dropped then, or when the player dies or is over `give_up_m` away; the members go back to Idle/Seek or Guard. Chasers of one player take slots on a ring of the melee reach round them (golden angle by slot, from the way the Raid came) and members of every Raid are pushed apart within `separation_m` (`GangRoster.Update`). Enforcers and the boss chase; a Thief with free hands joins.
- Hits: `LiveTargets`, `RewoundTargets` (decide: they run, so rewind like the Bigfoot's `PoseRing`, or document why not), `MatchSim.ApplyDamage` / `Deal` routing: hit marker, kill = small XP, no kill feed, streak or assist. Run over by a vehicle = dead (see how a citizen or pet is road-killed).
- `NonEnemyTargets`: gang members are NOT in it (turrets and killstreak weapons shoot them). Bots do not see them.
- Dead: lies `corpse_seconds` (10), then ends.
- Client `NetClient.Thieves.cs` + a view: without the private pack a coloured capsule per kind (Thief, Enforcer with a stick, boss bigger). With it: `SyntyLib` peasant models as a stand-in until ticket 08. Clips by state.
- `ThiefGang` (pure): owns the members of one Raid, the aggro list, the route queue.

## Accept
- Tests (Strict FSM): hit count per kind incl. headshot and explosive weight; aggro on hit and on give-up; windup then damage only if still in reach; Flee ends the entity at the edge; route ration.
- A member is never invisible (capsule fallback).

## Done: notes
Code: pure `godot/ShooterCore/Thieves/` (`GangMember.cs` entity, enums, `GangRules`, interfaces; `ThiefGang.cs` gang, `GangRoster`, `GangHitIds`; `GangPlacement.cs`; data in `ThievesData.cs` + `thieves.json` section `gang`), server `NetServer.Thieves.Gang.cs`, client `NetClient.Thieves.cs` + `GangMemberView.cs`. Tests `GangMemberTests.cs`. Protocol 43.

### API for 03 and 04
- `GangRoster` (`NetServer.Gangs`): `NewGang(post)` per Raid, `Remove(gang)`, `Clear()`, `Killed` event, `SourceOf`. It is the match's `IGangHits` (`MatchSim.Gang`) and owns the one set of hit ids and the one route ration (`ServeRoute`, `AnyRouteWaiting`).
- `ThiefGang`: `Add(kind, at, yaw)` (03: one per member at the side door, staggered), `SpawnCrew(centre, snap)` (counts from `gang.raid`), `Post` (set it to the van's door: moves every member's `Home`), `Members`, `Count`, `AliveCount`, `Aggro`, `NoteHit(player)`, `FleeAll(edge)` (03: van destroyed), `Clear()`. `GangPlacement.NearestEdge(from, min, max)` for the edge point.
- `GangMember`: `Kind`, `State`, `Carried` (byte; 0 none, else Loot kind + 1: 04 maps `LootKind`), `Working` (04: Steal, Haul, climbs...), `Busy`, `CanFight` (a Thief with `Busy` stays out of fights), `Escort`, `Home`, `RouteRequest` / `SetRoute` (the route queue: new states ask the same way), `Flee(edge)`, `Kill(killer)`, `Remove(reason)` (03: a member that reaches the door ends as boarded; no death clip), `Died`.
- New states go in `GangMemberState` from value 8 (client treats an unknown value as standing), with edges in `GangMember.Spec`, then regenerate `docs/state-machines.md`; the client picks the clip in `GangMemberView.PlayFor`.
- Server: `IGangWorld` (`GangWorld` in `NetServer.Thieves.Gang.cs`): `Players` (refreshed each tick in `GangsPrepare`, before the entities tick), `Hurt`. `TakeNavTurn()` is the shared route ration (one a tick across bots and members; `ServeBotRoute` and `GangsTick` both ask it).

### Wire (protocol 43)
- World entity type 57 `gang member` (54 at first; the wildlife Flock has 54): owner field = look (opaque ushort, client picks the body), state byte = `GangMemberState`, hp byte = bit 0 alive, bits 1-2 kind (`GangMemberKind`), bits 3-5 carried Loot (0 none, 1-7), bits 6-7 free; position = feet, yaw = facing, pitch 0. `GangRules.PackHp/KindOf/AliveOf/CarriedOf`.
- Hit ids `0x7100..0x73DF` (`GangRules.FirstHitId`, 736) between the turret records (0x7000) and the Raid vans (0x73E0); moved at the merge to main, where wildlife owns 0x7400..0x77FF.
- `SpecialWeapons.Gang` = 240 (what a member's blow is credited to; the kill feed names it "Gang member"), `XpReason.GangKill`. No new event kind (232+ stay free): no fx event yet, the swing is the state.

### Decisions
- Rewound: members run (5.5 m/s), so each has a `PoseRing` recorded at the end of the tick (`CaptureGangPoses`) and `RewoundTargets` lists them where the shooter's screen showed them, as the Bigfoot (ADR 0013). Before a member's birth tick the pose clamps to the birth pose (`LagComp`).
- Hit routing: `MatchSim.ApplyDamage` routes the id range to `IGangHits` (like citizens and pets), so MOAB, turrets and every other path that calls it reach members; `Deal` sends the hit marker (head flag kept) and nothing else. Weight by `BigfootRules.SourceOf` (the server gives the weapon list through `GangRoster.SourceOf`) and `GangRules.Weight`; headshot 2, explosive 12 and vehicle weapon 3 (guesses, `gang.hits`). Cause Roadkill = dead outright (`RunOverGang` in `RunOverCheck`, same speed gate as a player). A MOAB counts as one explosive hit, like on the Bigfoot.
- Aggro is per `ThiefGang`, decided once a tick in `Update` (before the members tick): forget clock, give-up (player dead or > `give_up_m` from every living member), then each member that `CanFight` gets the nearest aggro player. A hit from a non-player or a dead player starts nothing. A vehicle-bound player is chased but never struck (no melee on riders).
- Members move along nav routes only; the last 3 m of a chase with no route is a straight step. Route requests: at most one per `repath_seconds`, only when the goal is `repath_m` from the end of the route. A queue per Raid, oldest first (first sight in the list within one tick); the roster takes turns between Raids.
- Thief stand-in until 04: idle 3 to 8 s, then walks to a random point within `wander_m` of the post. Enforcer escorts the Thief with the fewest escorts (reassigned when it dies), else stands at the post; boss stands at the post. All three chase when fighting, then go back.
- Melee: windup 0.45 s, blow lands at the start of Swing if the target is still within 2 m (flat) and 2.2 m of height, recovery to 1.0 s; the Gang boss has the fist (10), the Enforcer the pipe (25).
- `/event thieves` refuses without a running round, without the navigation mesh, or with no road on the mesh 25 to 60 m from the admin; otherwise the crew appears there and the placeholder timer still ends the event. End, cancel and round reset remove the crew (they do not flee; `FleeAll` is for 03).
- Kill = 5 XP (`gang.kill_xp`) as `XpReason.GangKill`; no streak, assist or kill feed. Not in `NonEnemyTargets` (turrets and streak weapons shoot them); the bots' contact list holds players only.
- Client: capsule fallback per kind (boss bigger and gold, Enforcer with a pipe, a nose shows facing, lies down when dead); with the private pack, Synty peasants (`gang.view.models`) with clip names in `gang.view.clips`. Walking is read from the interpolated movement (stand, guard and walk share clips). A blow's damage marker points at the nearest member (the hit event carries a hit id the client cannot map).

### Open
- Windup and swing clips are not named (`gang.view.clips.windup` / `swing` are empty): the body leans instead. Pick Synty melee clips (and the pipe's hand bone, `Hand_R` is a guess) when the pack is on disk (ticket 08).
- No fx or sound events: swing sound, hit thud and voice are ticket 09. No hit reaction (a flinch on a non-lethal hit): hp byte bits 6-7 or a toggle bit are free for it.
- Numbers are guesses by reading, not by playing: weights, run speeds, wander, the boss's fist damage.
- Not run in the game (brief): spawn point search, the route ration with bots, the views and the damage marker are build-verified only.

