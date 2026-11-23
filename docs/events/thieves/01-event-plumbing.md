# 01 Event plumbing

Make a third event kind possible without special cases, and add the empty `thieves` kind.

## Today
- `EventBook` (`godot/ShooterCore/Events/EventRun.cs`): kind constants, hard-coded `Startable`, `WhyNotStart` text names the Bigfoot.
- `/event current end|cancel`: `ChatCurrentEvent` (`NetServer.Bigfoot.cs`) knows the Bigfoot, falls through to `ChatCurrentEnd` (`NetServer.Mass.cs`) which knows the Mass. Any other kind: "No event is running."
- No real event calls `IntelServer.Announce` / `Withdraw` (`Net/Server/Intel/IntelServer.cs`).

## Do
- A per-kind handler table on the server: start, end (as a win), cancel. Bigfoot and Mass move onto it, behaviour unchanged. `/event current end|cancel` dispatches by `EventBook.Current`.
- `WhyNotStart` text: generic, names the running kind.
- Kind `thieves`: constant, `Startable`, chat choice (`ChatCommands.cs`), `case` in `ChatEvent`.
- `NetServer.Thieves.cs` skeleton: Init (called from `_Ready`), Start, Tick (from `_PhysicsProcess` next to `MassTick`), End, Cancel, round reset, join catch-up hook. Start = `_events.Start`, chat line, `AnnounceIntel` + the Event announcement; end = `WithdrawIntel`, `_events.End(kind, Won)`. For now the event ends itself after a placeholder timer.
- `thieves.json` + `ThievesFile` with `version` only, registered and loaded.
- `ThievesEvent` (pure) with a phase machine: Idle, Raiding, Over. Spec + docs regenerated.

## Accept
- `/event thieves` starts; `/event current end` and `cancel` end it; a second start is refused with the generic text; Bigfoot and Mass commands behave as before (existing tests pass).
- Tests: handler dispatch per kind, `ThievesEvent` phases.

## Done: notes
- Handler table: `NetServer.Events.cs` `InitEvents()`, `_eventHandlers.Register(kind, name, start, end, cancel?)`; pure part `EventHandlers<TWho>` (`ShooterCore/Events/EventHandlers.cs`).
- Hooks marked HOOK in `NetServer.Thieves.cs`: `InitThieves`, `ThievesTick`, `ThievesFinish(won, by)`, `SendThievesOnJoin`, `ThievesRoundReset`.
- Placeholder: the event wins itself after `PlaceholderSeconds` (60, in code): remove with the first Raid (03).
- No wire change, protocol still 42. Intel text is a placeholder.
