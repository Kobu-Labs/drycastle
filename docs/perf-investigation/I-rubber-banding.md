# I. Rubber-banding and lag-back (specialist report)

Scope: on-foot prediction and reconcile (`NetClient.cs` OnSelf / ReplayReconcile, `Player.cs`), predicted vehicles (`NetClient.Drive.cs`, `NetClient.Flight.cs`, `DriveModel` / `FlightPrediction` tolerances), remote render clock (`NetClient.cs` RenderRemotes, `NetClient.Cars.cs`, `NetClient.Aircraft.cs`, `NetClient.Animals.cs`), server input consumption (`NetServer.cs` SimulateClient, `CatchUpBudget`). Built on `perf-merged` (`bed2f3d`). Harness: `make shooter-loss-test` (16 bots, village map, 30 s window, same bot scripts before and after), plus a `--car-loop` client for vehicles. No screenshots, no graphical client.

Every number below is from the loss-test client and server logs (`out/shooter-loss/20261113-124605` before, `out/shooter-loss/20261113-125349` after). Runs differ in bot trajectories, so reconcile counts carry run-to-run noise of roughly 20-30 %. Only the render-clock counts are a clean before/after: they are zero by construction after the fix, and the before value is the same measurement on the same code path.

## 1. Verdict

The visible rubber-banding has three sources that the telemetry can separate:

1. **Own body and eye: every on-foot reconcile is drawn as a one-frame snap.** Prediction error above 3 cm is common (1-2 corrections per client per second, most of them above 5 cm). The state corrected in one step, and the first-person eye moved with it. **Fixed** (the eye glides, section 3.1). The reconcile count itself is unchanged; the fix changes what is drawn, not what is simulated.
   - **Biggest cause of the large ones (not fixed): vertical.** In the final gate run, 769 of the 810 on-foot corrections above 20 cm at rtt100 (95 %), and 104 of 133 on the clean link (78 %), have their error mostly up or down (section 4.4). Contacts with other players explain only 14-25 % of them. The likely mechanism is the stale floor state on replay (section 5.3), a hypothesis not yet tested.
2. **Remote players and vehicles jump backwards.** The render clock was re-anchored to every arrival, so a late or early packet moved the drawn tick by up to 0.5 tick (clean link) and up to 1 tick (rtt200). A late packet drew the entity two ticks back. **Fixed**: the drawn tick never moves backwards (count: 206 / 444 / 595 backsteps per 30 s run before, 0 after).
3. **Predicted vehicles: position already glided, yaw snapped.** Corrections of 0.2-0.9 m and 1-3 degrees happen on bumps against walls and other vehicles, which the client does not see as solids (C-08). Position was faded (`Smooth`); yaw and pitch/roll were not. **Fixed** the angle snap (section 3.3). The car-loop sample is too route-dependent to show a before/after count (section 4.3).

Still open, with numbers in section 5: the vertical corrections above (the largest visible class), input starvation on the server (0.5-0.9 % of player steps have no input queued, so the body stands still for a tick the client predicted), and remote freezes when a gap exceeds the 100 ms buffer (rare, but a jump-free freeze is the cost of the fix at rtt200).

## 2. Mechanism and cost (before the fix)

### 2.1 Own body reconciles: a snap per correction (cause 1)

`OnSelf` compares the predicted position for the acked input with the server's. Above `ReconcileEpsilon` (3 cm) it sets `_reconcile`, and `ReplayReconcile` (physics tick) calls `SetState` and replays the unacked inputs. The body moves at once and the camera, a child of the body, moves with it. There was no visual offset, so each correction was visible in one frame. The vehicles already faded their corrections (`Smooth`, 10/s); the on-foot path did not.

Measured, 16 clients, 30 s window, before the fix:

| scenario | on-foot reconciles (per client per s) | > 3 cm | > 5 cm | > 20 cm | > 50 cm | > 2 m (respawn / teleport, not a correction) |
|---|---|---|---|---|---|---|
| clean | 641 (1.34) | 641 | 591 | 233 | 63 | 7 |
| rtt100 (50 ms, 5 ms jitter, 3 % loss) | 1452 (3.02) | 1452 | 1389 | 806 | 238 | 6 |
| rtt200 (100 ms, 10 ms jitter, 3 % loss) | 853 (1.78) | 853 | 792 | 276 | 56 | 2 |

Causes, from the contact tag (`onfoot_near_remote`: another living player within 1 m horizontally at the reconcile) and the axis tag (`onfoot_vertical`, error mostly up or down). Both tags were added after the before run and are measurement-only; the counts below come from the fixed build, which has the same reconcile logic, so they describe the prediction disagreement in both states. Axis numbers are in section 4.4.

- Contact with another player (fixed build, 30 s window of the 3-scenario run): 70 / 567 reconciles on clean (12 %), 100 / 1004 at rtt100 (10 %), 135 / 655 at rtt200 (21 %). Of the corrections above 20 cm, 0 / 182 (clean), 14 / 460 (rtt100), 34 / 290 (rtt200) had a player within 1 m. The final gate run gives 18-25 % of the big ones, so the share is small in both samples.
- Input starvation (section 2.3) gives one missing step per starved tick, about 0.1-0.2 m at walking and sprinting speed. It cannot account for the 182 corrections above 20 cm on the clean link alone (87 starved ticks in the whole round).
- Axis split (`onfoot_vertical`: the error is more than half vertical; section 4.4): of the corrections above 20 cm, 769 / 810 at rtt100 and 104 / 133 on the clean link are vertical. Steps, slopes, jumps and landings are the likely source. The stale floor state on replay (C-02 mechanism, confirmed in code: `Simulate` reads `IsOnFloor()` from the body, and `SetState` does not reset it) would produce exactly this, but it is not yet tested. A static collision mismatch (trees, fences, terrain) would be wall-side, not vertical, so it does not explain the bulk.

Cost: every correction is one visible frame of 3-50 cm. At rtt100 that is 2-3 per client per second.

### 2.2 Remote render clock: backward steps and holds (cause 2)

`RenderRemotes`, `Cars`, `Aircraft` and `Animals` each computed the drawn tick as `lastTick + (now - lastArrival) * 30 - delay`. That is equivalent to a clock re-anchored to each snapshot's arrival. The drawn tick therefore jumps by `1 - 30 * (arrival interval)` ticks at each arrival: a 10 ms jitter moves it 0.3 tick. A late packet moves it back by its lateness: the sample for the late tick is clamped to the newest buffered sample while the clock runs past it, and when the packet lands the clock falls back to `newest - 3`, two ticks behind the frozen picture.

Measured, rendered tick moving backwards between frames, all remote draws, 16 clients, 30 s:

| scenario | backsteps (frames) | largest backstep (ticks, metres at 6.7 m/s sprint) | remote draws at or past the newest sample (freeze) |
|---|---|---|---|
| clean | 206 | 0.53 (16 ms: 11 cm at sprint) | 0 of 431,925 |
| rtt100 | 444 | 0.50 | 15 of 431,820 (0.003 %) |
| rtt200 | 595 | **0.98** (33 ms: 22 cm at sprint, 0.6 m at car speed) | 75 of 431,940 (0.017 %) |

A clean-link backstep of 0.5 tick is 17 ms: 11 cm for a sprinting player and about 0.3 m for a car at 20 m/s. Most backsteps are small; the rtt200 ones are the visible ones.

Cost: a remote player or vehicle shuffles backward by up to a third of a second of motion, several times a second on an impaired link.

### 2.3 Server input starvation (cause 4, not fixed)

`SimulateClient` takes one input per tick; the queue is topped up by the client's 30 Hz packets (three inputs each, so one lost packet is covered). When the queue is empty the server does not step the body, while the client predicted the step. The reconcile then fixes the difference one tick later. Counted per living on-foot player (new counters, `StarvedTicks` / `StepTicks`, reported in `LOSSTEST_FINAL`):

| scenario | starved ticks | steps | starved share |
|---|---|---|---|
| clean | 87 | 16,554 | 0.52 % |
| rtt100 | 133 | 16,968 | 0.78 % |
| rtt200 | 110 | 18,338 | 0.60 % |

Starvation happens with no impairment at all: the client's send timer and the server's tick are not phase-locked, so about one tick in 200 has no input. Under jitter it rises a little. Each starved tick is one predicted step the body did not take (0.1 m walking, 0.22 m sprinting), so it is a small but guaranteed correction source.

### 2.4 Predicted vehicles (cause 3)

Position was already faded (`Smooth`, `CorrectionFade` = 10/s). Yaw, pitch and roll were not: a correction above the 0.01 rad tolerance snapped the whole car or helicopter by the turn. Car-loop sample, one car per run, before the fix:

| run | distance driven | vehicle corrections | > 5 cm | > 20 cm | largest position | largest yaw |
|---|---|---|---|---|---|---|
| clean | 466 m in 40 s | 0 | 0 | 0 | 0 | 0 |
| rtt100 | 112 m in 24 s | 14 | 10 | 7 | 0.44 m | 2.9 deg (3 corrections above 1 deg) |

Position corrections came from bumps: the car bot drives into walls and other cars, which the client's drive probe does not see as solids (ADR 0010; C-08). Each correction moves the vehicle by the bump the server resolved.

## 3. Fixes

### 3.1 Own eye glides over an on-foot correction (`Player.cs`, `NetClient.cs`)

`ReplayReconcile` records the moved distance `before - after` (the predicted present minus the corrected one) into `_viewFix` when it is within `SnapDistance` (2 m; respawns and teleports are not glided). `FadeOwnCorrection` decays `_viewFix` each frame at `CorrectionFade` (10/s) and sets `Player.ViewOffset`; `Player._Process` draws the eye at `eye + ViewOffset` when not in a chase view. The state, the simulation and the server's position are unchanged; the eye is drawn at the old place and reaches the new one with a 100 ms time constant. The fade is cleared on spawn, car entry and exit.

Effect on the picture (arithmetic, not a pixel measurement): at 60 fps a correction of `e` metres moves the eye by at most `e * (1 - exp(-10/60))`, about 0.15 e per frame, instead of `e` in one frame. The fade has a time constant of 100 ms: a 20 cm correction is 3 cm in the first frame and within 1 cm of its target after about 17 frames (0.3 s). Reconcile counts do not change (table in section 4.1), by design.

### 3.2 A render clock that never runs backwards (`ShooterCore/Net/RenderClock.cs`)

The server tick estimate is a filtered offset: each snapshot gives a sample `tick / 30 - arrival`, and the offset moves by 1/8 of the difference (`RenderClock.Gain`). The drawn tick is `max(previous, (now + offset) * 30 - delay)`: when the estimate moves back (a late packet), the drawn tick holds and does not snap back. All four render sites (players, cars, aircraft, animals) call `RenderTickNow()`.

Consequence: the entity holds still for the lateness beyond the buffer instead of moving back. Measured: backsteps 0 in every scenario; freeze draws 0 / 0 / 120 (clean / rtt100 / rtt200) in the 3-scenario run and 0 / 15 (clean / rtt100) in the final gate run, so the freeze count rises at rtt200 (75 before) while the backward jump disappears. That is the trade: a short freeze instead of a 2-tick snap. Unit tests in `godot/ShooterCore.Tests/RenderClockTests.cs` (4 tests: steady jittered arrivals advance at 30/s and never backwards; a 200 ms late packet holds and does not snap; the drawn tick re-joins the schedule; the first sample syncs).

### 3.3 Predicted vehicles fade yaw, pitch and roll (`NetClient.Drive.cs`, `NetClient.Flight.cs`)

`ReplayDriveReconcile` and `UpdatePiloting` record the attitude difference of each correction (`AngleDelta`, yaw wrapped; a turn over 1 rad is not glided) into `AngleFix`. The predicted branch of `PoseDrive` and `PoseFlight` adds the decaying `AngleFix` to the drawn attitude (the predictor's own state is unchanged), so the body and the chase camera turn with the same 100 ms time constant. Position already faded; this removes the matching angle snap.

### 3.4 Telemetry (no behaviour change)

`NetClient.Rubber.cs` (`RubberStats`): on-foot correction bands (> 3, 5, 20, 50 cm, 2 m), contact and vertical tags, vehicle corrections (position > 5 / 20 cm, yaw > 1 / 3 deg, maxima), remote draws, clamped draws, backsteps and the largest backstep. Reported per window in the loss-test client JSON (`LOSSTEST_CLIENT`) and at the end of a car loop. Server: `stepTicks` and `starvedTicks` per player in `LOSSTEST_FINAL`.

## 4. Before / after

### 4.1 Loss test (3 scenarios of the same bot scripts, 16 bots, 30 s window)

Before: `out/shooter-loss/20261113-124605` (perf-merged behaviour plus telemetry). After: `out/shooter-loss/20261113-125349` (this branch). Both pass.

| scenario | on-foot reconciles total (per client per s) | > 5 cm | > 20 cm | > 50 cm | render backsteps (largest, ticks) | remote draws clamped |
|---|---|---|---|---|---|---|
| clean, before | 641 (1.34) | 591 | 233 | 63 | 206 (0.53) | 0 |
| clean, after | 567 (1.18) | 494 | 182 | 85 | **0** (0) | 0 |
| rtt100, before | 1452 (3.02) | 1389 | 806 | 238 | 444 (0.50) | 15 |
| rtt100, after | 1004 (2.09) | 950 | 460 | 155 | **0** (0) | 0 |
| rtt200, before | 853 (1.78) | 792 | 276 | 56 | 595 (0.98) | 75 |
| rtt200, after | 655 (1.36) | 626 | 290 | 79 | **0** (0) | 120 |

Reading: the reconcile counts moved by 10-30 %, and that is run-to-run variation (the fix does not change the simulation); they are not a result. The result is the render backsteps (every one removed) and the visibility of corrections, which the counts cannot show. The rtt200 freeze count rose (75 to 120 draws, 0.017 to 0.028 %): the new clock holds instead of snapping, so a gap longer than the buffer now shows as a short freeze.

Starved ticks, same runs: 0.52 / 0.78 / 0.60 % before, 0.55 / 0.59 / 0.65 % after (unchanged, not fixed).

Prediction error p95 (loss test "pred error", worst client): 0.111 / 0.467 / 0.578 m before; 0.218 / 0.570 / 0.378 m after. These include respawn and contact corrections, so they do not isolate the fixes.

### 4.2 Car loop, rtt100 (vehicle corrections)

Before (one car in one run): 14 corrections in 112 m, 10 above 5 cm, 7 above 20 cm, largest 0.44 m and 2.9 deg.
After (three cars in one run, different routes): 26 corrections in 669 m (all above 5 cm), 25 in 373 m (23 above 5 cm, 19 above 20 cm), 77 in 174 m (75 above 5 cm, 19 above 20 cm, largest 0.94 m and 2.2 deg).

These samples do not support a before/after comparison: the car bot's route, the cars it finds free, and the bumps it hits all change between runs, and only one before run got a driver. The vehicle fix changes what is drawn, not the correction count. Clean-link car run after the fix was not completed (one car timed out).

### 4.3 What was not improved

- Corrections are still made every time the prediction disagrees: the fix hides nothing from the server's authority, but a car that bumps a wall still moves 0.2-0.9 m in the server's view, and the client still shows that with the same fade.
- Remote freezes at rtt200 rose slightly (section 4.1).

## 5. Left as a write-up (not implemented)

0. **Vertical on-foot corrections (the largest visible class, section 4.4).** Hypothesis: the body's floor flag (`IsOnFloor()`, physics-driven) is not reset when `SetState` rewinds the body, so a replayed step near a landing, a step or a slope uses the floor of the newest step. Test: a headless two-step replay around a landing with the floor flag set from the old and the new step; if the first replayed step differs from the server's by more than 1 mm, the flag must come from the state (`MoveState.OnFloor`) and not from the body. This is a movement-model change, so it needs a parity test against the server before it ships.

1. **Input starvation (cause 4).** 0.5-0.8 % of player steps have no input. Options, in order of cost: (a) a one-input server buffer (run the input one tick late once the queue has one entry; +33 ms on the server's view of every player, invisible to the owner, costs rewind precision; design question); (b) repeat the last movement input when the queue is empty (server guesses; the ack bookkeeping must not advance; needs a test of the replay); (c) client send phase-locked to the server tick (no protocol change, but a client tick change). Recommendation: (c) first, then measure; (a) only if starvation stays above 0.2 % under jitter.
2. **Adaptive interpolation delay.** Freezes are rare (0.003-0.03 % of draws), so a fixed 3-tick buffer is adequate at rtt100 and rtt200. An adaptive delay `max(3, p95 gap + 1)` would remove the rtt200 freezes at the cost of 1-2 ticks of latency for remote entities. Not needed on current numbers.
3. **Stale floor state on replay (C-02 mechanism).** `SetState` does not reset the body's floor flag, so the first replayed step can use the floor of the newest step. Likely small (landings, slopes), and not measured. A unit test with a jump and a landing would settle it.
4. **Client does not see other players or vehicles as solids (C-01, C-08).** Each contact is a server correction (vehicles 0.2-0.9 m, bodies 20-50 cm). Making remote bodies solid on the client needs the server's collision layers settled first (is the player-on-player collision real on the server? C-01 is still UNVERIFIED).
5. **Predicted vehicle yaw on the car loop.** The new fade is in place; a same-route before/after (a fixed road, the same driver script) would be needed to measure its visible effect.

## 6. Confidence

- Render backsteps: high. Mechanism in code, counted before and after, zero after by construction.
- On-foot correction glide: high that the snap is gone from the state (the state moves in one step, the eye does not); medium on the visible result (arithmetic, no frame capture, per the rules).
- Vehicle yaw fade: high on the mechanism, low on magnitude (small sample).
- Cause split of the on-foot corrections: medium. Contact and starvation are measured; the rest is unassigned until the axis split (section 4.4) and the stale-floor test are run.
- Starvation share: high (counted on the server).

## 7. Tests and gates

- `dotnet build godot/shooter/shooter.csproj`: `    0 Error(s)`
- `dotnet test godot/ShooterCore.Tests/ShooterCore.Tests.csproj`: `Passed!  - Failed:     0, Passed:  1742, Skipped:     0, Total:  1742, Duration: 7 s - ShooterCore.Tests.dll (net8.0)` (includes the 4 new `RenderClockTests`).
- `make shooter-loss-test BOTS=16 MAP=/home/jjakubec/coding/drycastle/out/village/map`: `SHOOTER_LOSS_TEST_OK`, `| result | PASS | PASS |`, `0.00 % / 0` wrong decoded states (section 4.5).

### 4.4 Axis and contact split (final gate run, `out/shooter-loss/20261113-125918`)

| scenario | on-foot corrections | above 20 cm | of those, vertical-dominant | contact (player within 1 m) above 20 cm |
|---|---|---|---|---|
| clean | 445 | 133 | 104 (78 %) | 33 (25 %) |
| rtt100 | 1502 | 810 | 769 (95 %) | 142 (18 %) |

Contact and vertical tags are per correction and overlap. The vertical share is high in both runs; the contact share is 18-25 % of the big ones, so bodies bumping each other (C-01) is a real but secondary cause.

### 4.5 Final gate

`make shooter-loss-test BOTS=16 MAP=/home/jjakubec/coding/drycastle/out/village/map` (default scenarios clean and rtt100; log `scratchpad/final-loss.log`):

```
SHOOTER_LOSS_TEST_OK
| result | PASS | PASS |
| decoded vs server samples wrong (share) / longest wrong run ticks | 0.00 % / 0 | 0.00 % / 0 |
| final state entity mismatches | 0 | 0 |
| victim hit-event / death-event / scoreboard mismatches | 0 / 0 / 0 | 0 / 0 / 0 |
```

Final-gate counts: render backsteps 0 in both scenarios; remote draws clamped 0 (clean) and 15 of 431,985 (0.003 %, rtt100); on-foot reconciles 445 (0.93 per client per s) and 1502 (3.13). Starved ticks 0.52 % (clean) and 0.85 % (rtt100).
