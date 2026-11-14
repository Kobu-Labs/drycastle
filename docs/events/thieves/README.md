# Thieves event: tickets

Throwaway. Delete this folder when the event is done. Design: `docs/events/README.md` ("Thieves storming the village", Design). Terms: `GLOSSARY.md` (Thieves event, Raid, Raid van, Gang member, Loot). Van: ADR 0021.

| # | Ticket | Needs | State |
|---|---|---|---|
| 01 | Event plumbing | | done |
| 02 | Gang member | 01 | todo |
| 03 | Raid van | 01, 02 | todo |
| 04 | Loot + tires | 02, 03 | todo |
| 05 | Pets | 04 | todo |
| 06 | Dishes | 04 | todo |
| 07 | Poles | 04, climb of 06 | todo |
| 08 | Assets | 02 (pack on disk) | blocked: Gang Warfare pack not found on disk |
| 09 | Audio | 02, 03 | todo |
| 10 | Reward | 02 | todo |

## Rules for every ticket

- Read first: `CLAUDE.md`, `GLOSSARY.md`, `docs/state-machines.md`, the design section, ADR 0021.
- Pure logic in `godot/ShooterCore/Thieves/` (Godot-free, tested in `godot/ShooterCore.Tests`); server in `godot/shooter/Net/NetServer.Thieves*.cs`; client in `NetClient.Thieves*.cs`.
- Every state machine: enum + `StateMachineSpec<T>` + `StateMachine<T>`, then regenerate `docs/state-machines.md`. No bool/timer flag clusters.
- Every number in `godot/shooter/data/thieves.json` (`ThievesFile : IVersioned`, registered in `GameData.cs`, handed over in `Shooter.cs`).
- Wire change: bump `Wire.ProtocolVersion` (`godot/ShooterCore/Net/Wire.cs`) and add the comment line. Free: world entity wire types 54+, `EventKind` 232+.
- Private assets missing: everything still works (capsules, box, silence). Private audio never goes in `sounds.json` (startup throws on a missing file): load it as `ChurchChant` does.
- Done = builds (`shooter.csproj`), `ShooterCore.Tests` pass. Do not run the game or take screenshots.
- Update the State column above and note what was left open at the bottom of the ticket.
