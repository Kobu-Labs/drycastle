## Agent skills

### Issue tracker

Issues are tracked in GitHub Issues for Kobu-Labs/drycastle (via the `gh` CLI). See `docs/agents/issue-tracker.md`.

### Domain docs

Single-context: one `GLOSSARY.md` + `docs/adr/` at the repo root. See `docs/agents/domain.md`.

### State machines

Every state machine has a `StateMachineSpec<T>` (godot/ShooterCore/Fsm/) and is listed in generated `docs/state-machines.md`: read it before touching phase/state logic, never grow a new `bool`/`_xLeft` flag cluster. New or changed machine: enum + spec next to the owner, `StateMachine<T>` for the clock, then `UPDATE_STATE_DOCS=1 dotnet test godot/ShooterCore.Tests --filter StateMachineCatalogTests`. Timers/edges: `Countdown`, `Edge`, `Change<T>`, `Ramp`, `PendingAnswer`, `Lifetime`. Tests run Strict (undeclared transition throws); production logs and takes it.

### Vehicles

Anything that draws, attaches to or measures a vehicle (glass, lamps, wheels, cockpit, seats, smoke, holes) follows `docs/standards/car-design.md`: body parts hang under the `hull`, not the root. Read it first.

### UI/UX standard

All UI, including the map editor, meets `docs/standards/ui-ux.md` (non-negotiable). Invoke the `ui-work` skill before writing or changing UI.

## Communication & Brevity

- In all interactions, plans, and commit messages, be extremely concise. Sacrifice grammar for the sake of concision.
- Eliminate conversational filler, pleasantries, and introductory/concluding fluff.
- Use dense, scannable bullet points and code snippets instead of prose paragraphs.
- In plan mode or when requirements are ambiguous, list any unresolved questions at the very end, if any.
