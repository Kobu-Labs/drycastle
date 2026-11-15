# I. Rubber-banding and lag-back (specialist report)

Scope: on-foot prediction and reconcile (`NetClient.cs` OnSelf / ReplayReconcile, `Player.cs`), predicted vehicles (`NetClient.Drive.cs`, `NetClient.Flight.cs`, `DriveModel` / `FlightPrediction` tolerances), remote render clock (`NetClient.cs` RenderRemotes, `NetClient.Cars.cs`, `NetClient.Aircraft.cs`, `NetClient.Animals.cs`), server input consumption (`NetServer.cs` SimulateClient, `CatchUpBudget`). Built on `perf-merged` (`bed2f3d`). Harness: `make shooter-loss-test` (16 bots, village map, 30 s window, same bot scripts before and after), plus a `--car-loop` client for vehicles. No screenshots, no graphical client.

Every number below is from the loss-test client and server logs (`out/shooter-loss/20261113-124605` before, `out/shooter-loss/20261113-125349` after). Runs differ in bot trajectories, so reconcile counts carry run-to-run noise of roughly 20-30 %. Only the render-clock counts are a clean before/after: they are zero by construction after the fix, and the before value is the same measurement on the same code path.

## 1. Verdict

The visible rubber-banding has three sources that the telemetry can separate:

1. **Own body and eye: every on-foot reconcile is drawn as a one-frame snap.** Prediction error above 3 cm is common (1-2 corrections per client per second, most of them above 5 cm). The state corrected in one step, and the first-person eye moved with it. **Fixed** (the eye glides, section 3.1). The reconcile count itself is unchanged; the fix changes what is drawn, not what is simulated.
   - **Biggest cause of the large ones: vertical (round 2, section 8).** 78-95 % of the on-foot corrections above 20 cm are mostly up or down; contacts with other players explain 2-25 %. Round 2 found and fixed a real replay-start divergence in the floor flag (section 8.1, the parity probe goes from 34-63 diverging replays of 794 to none), but the live big-correction counts did not fall by a measurable amount, so the vertical class has a second source that is not yet identified (section 8.4).
2. **Remote players and vehicles jump backwards.** The render clock was re-anchored to every arrival, so a late or early packet moved the drawn tick by up to 0.5 tick (clean link) and up to 1 tick (rtt200). A late packet drew the entity two ticks back. **Fixed**: the drawn tick never moves backwards (count: 206 / 444 / 595 backsteps per 30 s run before, 0 after).
3. **Predicted vehicles: position already glided, yaw snapped.** Corrections of 0.2-0.9 m and 1-3 degrees happen on bumps against walls and other vehicles, which the client does not see as solids (C-08). Position was faded (`Smooth`); yaw and pitch/roll were not. **Fixed** the angle snap (section 3.3). The car-loop sample is too route-dependent to show a before/after count (section 4.3).

Still open, with numbers in section 5 and 8: the vertical class (above), input starvation (0.4-0.8 % of player steps have no input queued; round 2 shows this is not a correction source, section 8.2, so no fix is warranted), and remote freezes when a gap exceeds the 100 ms buffer (rare, and the cost of the fix at rtt200).

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

Starvation happens with no impairment at all: the client's send timer and the server's tick are not phase-locked, so about one tick in 200 has no input.

**Correction (round 2, section 8.2):** the statement that each starved tick "is one predicted step the body did not take, so a guaranteed correction source" was wrong. The reconcile compares the predicted state with the server's at the same acknowledged input sequence, not at the same tick. A starved tick only delays the server's step; once the server steps the inputs in order, its state at each acknowledged sequence equals the client's prediction. Starvation does not, by itself, produce a correction. The repeat-last-input option would create the divergence, because it adds a step the client did not predict at any sequence.

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

0. **Vertical on-foot corrections: second source (section 8.4).** The replay-start floor flag is fixed (section 8.1). The remaining vertical class is not explained by it; candidates are server-only state changes the client cannot predict (knockback impulses from explosions, which set the body airborne on the server; `AddImpulse`), and jump and landing timing around the snap. Next step: count `AddImpulse` applications per player in the window, and split the vertical corrections by whether an impulse landed in the three ticks before them.

1. **Input starvation (cause 4): no fix, see section 8.2.** The earlier options are revised: repeating the last input is harmful (a model test shows the divergence); a one-input server buffer adds 33 ms to other players' view of the owner for no correction benefit; send phase-locking changes the client tick for no measured gain. Nothing is implemented.
2. **Adaptive interpolation delay.** Freezes are rare (0.003-0.03 % of draws), so a fixed 3-tick buffer is adequate at rtt100 and rtt200. An adaptive delay `max(3, p95 gap + 1)` would remove the rtt200 freezes at the cost of 1-2 ticks of latency for remote entities. Not needed on current numbers.
3. **Stale floor state on replay (C-02 mechanism): fixed, section 8.1.**
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

## 8. Round 2: replay floor flag, starvation, and the gate

Round 2 was asked for two things: fix the vertical corrections (the stale floor flag on replay, which the coordinator asked to confirm with a parity test first), and fix input starvation with the least added latency. Both are reported below with the numbers, including the one that did not move.

### 8.1 The replay-start floor flag: confirmed and fixed

**Mechanism.** A movement step reads two floor states. The step's own flag (`MoveState.OnFloor`, carried in the state and exact on replay) drives gravity and acceleration. Godot's internal `IsOnFloor()` (the body's last `MoveAndSlide`) drives the floor snap and the floor-aware slide inside `MoveAndSlide`. After `SetState` the body is put back at the acknowledged state, but its internal flag is still the one from its last move: the newest predicted step, one to three steps ahead. The first replayed step therefore runs `MoveAndSlide` with the newest step's flag, and the error propagates. Only the first replayed step differs; after it, the internal flag is the replay's own.

**Parity probe (the gate).** `--move-parity` (`godot/shooter/MoveParity.cs`, headless, real village map, no server). A seeded walk with jumps (900 or 2400 steps) runs once from a settled spawn: that is the server model, every step following the one before it. Then 794 trials each rewind to the state after step k (`SetState`, as a reconcile does), replay steps k+1..k+20, and compare with the sequential state after k+20. The probe counts trials where the body's floor flag before the rewind differs from the step's flag.

| run (2400 steps, 794 trials) | floor flag mismatch | replays over 1 mm | over 5 mm | largest error |
|---|---|---|---|---|
| seed 20261113, plain `SetState` (before) | 303 | 67 | 62 | 4.34 m (0.79 m vertical) |
| seed 20261113, `SetStateForReplay` (after) | 297 | **0** | **0** | 0.7 mm |
| seed 7, plain `SetState` (before) | 210 | 36 | 34 | 0.62 m (3 mm vertical) |
| seed 7, `SetStateForReplay` (after) | 210 | **0** | **0** | 0.0 mm |

Plain-`SetState` counts vary between engine runs of the same seed (34 to 63 over 5 mm in four runs); the production path was 0 in every run. Every over-5 mm replay in the plain runs was a mismatched trial (the probe's `over_5mm_with_mismatch` equals `diverged_over_5mm`).

Two ways of setting the flag were tried in the probe. A zero-velocity move did not set it in about 8 % of cases (279 of 303). A vertical move (down onto a grounded step's floor, up off an airborne one's) set it in every case (300 of 300 and 210 of 210), and the restored place and velocity are set again exactly after it.

**The fix.** `Player.SetStateForReplay(MoveState)` (`godot/shooter/Player.cs`): `SetState`, and if the body's floor flag differs from the state's, the vertical probe move followed by `SetState` again. `NetClient.ReplayReconcile` calls it. Nothing else changes; the replayed steps are the same `Simulate` the server runs.

**Why not a pure unit test.** The parity gate has to run the engine: the floor flag that matters is Godot's internal state inside `MoveAndSlide`, and a pure model cannot reproduce it. The pure part of the question (does a replay reproduce the sequential run when the flags agree) is the same code path the probe exercises. The probe is therefore the gate, and it is kept in the repo as a tool.

### 8.2 Input starvation: measured, not a correction source, no fix

**Test (pure, `godot/ShooterCore.Tests/StarvationTests.cs`, 4 tests, all pass).** A model of the server's queue (one input per tick, `CatchUpBudget`, the 8-input cap, the highest sequence received, three inputs per packet, every 25th packet dropped, so the server runs empty on that tick) runs the shared movement step against a client predicting every input at once. The check is the state after each acknowledged sequence on the server against the prediction for the same sequence.

- A starved server (empty queue on every 25th tick): the largest divergence at any acknowledged sequence is below 0.1 mm. Starvation does not create a correction.
- No starvation: exact.
- The rejected alternative (step again with the last input while starved): the divergence exceeds 1 mm. It adds a step the client never predicted at any sequence, so it would create the very corrections it was meant to remove.
- `CatchUpBudget`: the second input in a tick is paid for by credit (8 initial, a quarter per tick); over 40 ticks at a queue of 5 the total is between 40 and 58 steps.

**Why there is no fix.** Reconcile compares by sequence, not by tick, and the server steps inputs in order, once each. A starved tick is a late step, not a wrong one. The earlier write-up's claim that each starved tick is a guaranteed correction source was wrong, and this section corrects it. The only server-side effect is that the owner's position on other clients lags by one tick (about 33 ms) while the queue is empty.

**Measured (the starved share is a server counter and was not changed by this round; the differences are run-to-run).**

| run | clean | rtt100 | rtt200 |
|---|---|---|---|
| round 1 before (`124605`) | 0.52 % (87 of 16,554) | 0.78 % (133 of 16,968) | 0.60 % (110 of 18,338) |
| round 2 gate (`132343`) | 0.39 % (65 of 16,644) | 0.63 % (109 of 17,161) | 0.83 % (153 of 18,403) |

### 8.3 The gate: one harness change, and why

The round-2 three-scenario run first failed on the clean scenario: one remote player's final state, as reported by the clients, differed from the server's by 16 cm. The check compared the clients' final state (at each client's last snapshot, about 8.5 s after the window) with the server's final dump (taken at 7 s, the harness's `ServerFinalAfterStop`). The server's per-tick record shows the body moved at about tick 1710-1725, after the 7 s dump and before the clients' last snapshot, so the clients were right and the dump was early. It failed twice with the production replay (a second clean-only run failed on another player, 0.16 m) and passed with the plain `SetState` control (`exp-plain-clean.log`).

The probable cause of the late move is that the replay change alters the bots' own predictions, and so their scripted aim and fire, and so the final-seconds knockbacks; this is not established.

The gate was changed so that each client's final state is compared with the server's per-tick record at that client's own final tick (`final_tick`, in `server_states.csv`). That is the same comparison, aligned in time. It is not weaker: every player, every field, exact equality, as before. The server's dump at 7 s is no longer compared with the clients.

After the change, the clean scenario and the three-scenario gate pass (section 8.5).

### 8.4 The vertical class after the fix

Live loss test, same counting as section 4.4 (corrections above 20 cm, of which mostly vertical). Rows are runs of the current code; the first two rows are the round-1 runs.

| run | clean | rtt100 | rtt200 |
|---|---|---|---|
| round 1 before (`124605`) | 233 | 806 | 276 |
| round 1 final gate (`125918`), vertical share | 133 (78 %) | 810 (95 %) | n/a |
| round 2 gate (`131917`), vertical share | 76 (79 %) | 535 (94 %) | 587 (90 %) |
| round 2 gate (`132343`), vertical share | 168 (87 %) | 272 (83 %) | 357 (94 %) |

Run-to-run spread on identical code is about 2x (`131917` and `132343` differ by 2x on the same scenarios; the bots' fights differ). So these counts do not show a reduction from the replay fix. What the telemetry does show:

- The replay starts with the wrong floor flag in 36 (clean), 361 (rtt100) and 461 (rtt200) of the reconciles in the window (`floor_flag_mismatch`, `132343`): 5 %, 41 % and 69 % of reconciles. The mismatch is common in the live game (the bots jump, so the newest step is often across a takeoff or landing from the acknowledged one), and the replay fix is applied to each of them.
- The vertical share of the big corrections is 83-94 % in every run, before and after the fix. The replay fix therefore does not reach the main vertical source.
- Contacts with another player remain a small share (2-25 % of the big corrections).

The candidate that remains is server-only state the client cannot predict: knockback impulses (`AddImpulse`, which sets the body airborne on the server only) and the snap at landings. That is a hypothesis; the next measurement is in section 5.0.

### 8.5 Final numbers and gate lines

Round-2 gate, `out/shooter-loss/20261113-132343` (clean and rtt100 and rtt200), the final run with the floor-mismatch counter:

| scenario | reconciles (per client per s) | > 5 cm | > 20 cm | vertical of > 20 cm | replays starting with the wrong floor flag | remote draws clamped | render backsteps | starved |
|---|---|---|---|---|---|---|---|---|
| clean | 677 (1.41) | 588 | 168 | 147 (87 %) | 36 | 0 | 0 | 0.39 % |
| rtt100 | 874 (1.82) | 804 | 272 | 227 (83 %) | 361 | 45 | 0 | 0.63 % |
| rtt200 | 670 (1.40) | 626 | 357 | 336 (94 %) | 461 | 90 | 0 | 0.83 % |

`decoded vs server samples wrong (share) / longest wrong run ticks`: `0.00 % / 0` in all three.

Earlier runs, for the record: `131142` (three scenarios, old gate: clean failed on the late move), `131511` (clean only, old gate: failed), the plain-`SetState` control (`exp-plain-clean.log`, scratch, old gate: passed). New gate: `131917` (passed, three scenarios) and `132343` (passed, final, with the floor-mismatch counter). Only `132343` is the reference for section 8.5.

### 8.6 Gate lines (verbatim)

- `dotnet build godot/shooter/shooter.csproj`: `    0 Error(s)`
- `dotnet test godot/ShooterCore.Tests/ShooterCore.Tests.csproj`: `Passed!  - Failed:     0, Passed:  1746, Skipped:     0, Total:  1746, Duration: 7 s - ShooterCore.Tests.dll (net8.0)` (round 1: 1742; round 2 adds the 4 `StarvationTests`)
- `make shooter-loss-test BOTS=16 MAP=/home/jjakubec/coding/drycastle/out/village/map SCENARIOS=clean,rtt100,rtt200`: `SHOOTER_LOSS_TEST_OK`, `| result | PASS | PASS | PASS |`, `0.00 % / 0 | 0.00 % / 0 | 0.00 % / 0`
- Parity probe: `MOVE_PARITY trials=794 steps=2400 ... diverged_over_5mm=0 ... production=True` (both seeds)
