# C. Players: movement, prediction and reconciliation

> Transcribed by the lead from agent C's hand-back message. Agent C could not write
> report files itself. Content is C's; nothing here was re-verified yet (see the review pass).

## 1. Verdict

**Fit with changes.** On-foot prediction fits 16 players: one `MoveAndSlide` per player per tick,
reconciles capped at one per snapshot (30/s per client). But the shared step is not proven
identical on both sides: a *clean* link still gives 2205 reconciles, with errors of about 0.5 to 3
ticks of walking. Explain that before trusting it at 16.

Driver and pilot prediction fits the two predicted types (helicopter, amphibious transport), since
cost scales with drivers (one probe each), not vehicles. It does not generalise to all 29 vehicles:
probes cannot see other solids or on-foot players. Reconciliation is never a cost problem; it
becomes a correctness problem when player or vehicle contacts are frequent. The load threshold
needs measurement.

## 2. What is healthy

- Shared step inputs match: quantised `ToMove` (`godot/ShooterCore/Net/NetTypes.cs:85`), Quant
  constants (`NetTypes.cs:17-18`), `Simulate` (`godot/shooter/Player.cs:375-404`).
- Quantisation error (1/128 m = 3.9 mm/axis) is inside `ReconcileEpsilon` 3 cm
  (`godot/shooter/Net/NetClient.cs:21`).
- Server steps once per input in order, ack set on dequeue (`NetServer.cs:587-630`).
- Input cap 8 (`NetServer.cs:379`); 3-input redundancy (`NetClient.cs:525-527`).
- No client physics tick (`Player.cs:53`, `NetClient.cs:274`).
- Corpse ragdoll is server-positioned; client only turns it (`RagdollTumble.cs`, pure math).
- Both sides build the same street-lamp colliders (`Shooter.cs:339-349`).
- Driver probe ignores what it should (`DriveProbe.cs:9-12`). ADR 0010 holds in code.
- Server move cost bounded: loss-test p50 1.59 ms covers 16 player moves plus about 870 vehicle
  moves/s, so at most about 35 us per move (upper bound, not measured).

## 3. Findings

| id | title | mechanism / citation | cost | fix | effort | confidence | audit |
|---|---|---|---|---|---|---|---|
| C-01 | Player bodies solid to each other on the server only | `Player` never sets `CollisionLayer` (`Player.cs:11-78`), stays on layer 1; mask OnFoot = World\|VehicleSolid includes layer 1 (`CollisionLayers.cs:26`). Server holds 16 bodies in one space (`NetServer.cs:408`); client holds one. If the engine collides two kinematic bodies, bots block each other on the server only, drift grows per tick. | possible O(n²) per tick; drift | own layer (e.g. 16), mask World\|VehicleSolid; probe `Ignore` calls become unnecessary (`NetServer.cs:412, :163, :178`) | S | config CONFIRMED, engine behaviour UNVERIFIED | new (related T2-02, T3-014) |
| C-02 | Clean-link reconcile floor | Rate 4.6/client-s average, worst 26/s (about every snapshot). ~120 moves/s worst per client. Hypotheses: (a) C-01; (b) stale floor on replay: `Simulate` reads `IsOnFloor`/`GetFloorNormal` (`Player.cs:380, 396`) from newest move, `SetState` (`Player.cs:332-338`) does not reset it (mechanism CONFIRMED, small on flat ground); (c) client Perks neutral until profile (`NetClient.cs:718`, `NetClient.Loadout.cs:79`) while server applies at join (`NetServer.Progression.cs:21`) (mechanism CONFIRMED, only with run-speed perk). | not a CPU problem; correctness | test: spread bots 200 m apart, compare counts | S | cause UNVERIFIED | new |
| C-03 | Dead clients keep predicting | `NetClient.cs:510` sends zero input but `:520` still calls `Simulate`; server skips dead (`NetServer.cs:606`); `OnSelf` (`NetClient.cs:432-474`) has no dead check, so each snapshot snaps back. | low | skip `Simulate` when Dead | S | CONFIRMED | new |
| C-04 | Remotes have no jitter buffer; 100 ms delay shorter than observed gaps | `InterpDelayTicks=3` (`NetClient.cs:20`), render clock is last arrival (`NetClient.cs:536`), `Interpolator` clamps to newest (`Interpolator.cs:37`), so any gap over 100 ms freezes. Loss test: max gap 117 ms (rtt100, 3% loss), 90 ms mean worst; clean 56 ms. | freeze on gaps >100 ms | adaptive delay `max(3 ticks, p95 gap + 1 tick)` | S-M (costs latency) | mechanism CONFIRMED, frequency UNVERIFIED | new |
| C-05 | Recoil and breath on different clocks | Client wall time (`NetClient.Predict.cs:39, :48-49`); server tick time (`NetServer.cs:631, :633, :636`). Shot offset differs by up to one tick of decay (~33 ms). | up to ~33 ms offset | client tick counter | S | mechanism CONFIRMED, magnitude UNVERIFIED | new |
| C-06 | Vehicle solid boxes lag on client | Client places on snapshot arrival (`NetClient.Cars.cs:81`, from `NetClient.cs:205-210`); server from current position (`ServerCar.cs:126-133`). About 0.8 m behind for a walker at 10 m/s, 100 ms RTT (derived). | ~0.8 m | see vehicle doc | – | CONFIRMED | known (vehicle doc) |
| C-07 | Server growth linear in players | One `MoveAndSlide`, two water scans (`Player.cs:378, 396`), head `TestMove` when crouched/prone (`Player.cs:377`). Only risk is C-01. | linear | – | – | CONFIRMED | – |
| C-08 | Driver prediction scales with drivers | Max 16 probes: one sweep, four rays each per tick (`DriveProbe.cs:9-12`). Client drives through unseen vehicles and players; server corrects bumps (`NetServer.Vehicles.cs:397`); each bump is one reconcile. Tolerances `DriveModel.cs:419` (5 cm, 0.3 m/s, 0.01 rad). | 16 probes/tick max | – | – | CONFIRMED | – |
| C-09 | Remote animation | Speed from rendered positions (`NetClient.cs:599-604`), smoothing Lerp 0.3 per frame (frame-rate dependent). Draws dominate (T2-24); arm IK open (T3-029, cost UNVERIFIED). | low | – | – | CONFIRMED | T2-24, T3-029 |

Audit status in this area: T3-010 partly fixed (capsule gated, `Player.cs:418`; `TestMove` still per
tick, `Player.cs:377`). T3-017 boxed enumerator fixed (`Water.cs:103`). T3-024 open but negligible
(`Interpolator.cs:21`, about 0.37 MB/s memmove). T3-025 negligible. T3-037 open, negligible
(`Player.cs:344-353`). T3-046 open, negligible (`NetClient.Predict.cs:31`).

## 4. Design alternatives considered

- **Keep on-foot prediction, fix C-01 by own layer.** S; files `Player.cs`, `NetServer.cs`, probe
  `Ignore` calls; no ADR or protocol change.
- **Server-only players.** S code, but input-to-motion latency becomes RTT + 100 ms. Rejected for
  on-foot (OPINION).
- **Predict all vehicles with pure models.** L; one model per type, re-tune 29 vehicles, parity
  tests; entity type 6 exists; removes 100 ms steering lag for all drivers; cost one sweep + 4 rays
  per driver per tick. Test with one more type first.
- **Server-authoritative vehicles with dead reckoning** (send velocity, extrapolate about 100 ms).
  M; about 6 B per moving vehicle per tick; less lag for riders and others, no prediction of other
  drivers.
- **Adaptive interpolation delay (C-04).** S-M; costs latency; no freeze under jitter.

## 5. Needs measurement

1. Two `Player` bodies 1 m apart on the server: does `MoveAndSlide` stop? Then loss test with bots
   spread 200 m apart, counting reconciles. Cheapest: a headless two-body scene.
2. Count `Interpolator` clamps per remote per second at rtt100. Cheapest: one counter in
   `RenderRemotes`.
3. Time `Player.Simulate` in the server tick at 16 players, with and without parked vehicles
   (T2-04). Cheapest: a stopwatch in `SimulateClient`.
4. Log client vs server recoil per shot (no screenshots).

## Needed from other agents

- A: delta baseline and gap freezes (C-04); input drops at cap 8 (`NetServer.cs:379`).
- B: whether shot resolution uses tick-clock recoil (C-05).
- D: DriveModel vs CarModel split; vehicle contact rates (C-06, C-08).
- E: whether Godot/Jolt collide two kinematic bodies (decides C-01).
- F: arm IK cost (T3-029), draw counts (T2-24).
- G: dead gating (C-03).
- H: player contact count at 64 players if C-01 holds.
