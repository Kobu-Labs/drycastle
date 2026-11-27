# Cartel rank lives on the Profile, and a Job is not an Event

Decided with the owner on 2026-11-27 for the drug cartel career (#437, design in `docs/cartel.md`). A player works for the Cartel, runs Jobs and climbs a Cartel rank.

**1. A layer, not a match mode.** Jobs run inside ordinary ffa and tdm rounds, in the shared village, beside Events. The Match mode stays what it is (how a round is scored). A `cartel` match mode was rejected: it would split a small lobby between two games.

**2. Rank persists.** Cartel membership, Cartel rank and Respect are Profile state, written through the repositories of ADR 0003 (a migration in both the SQLite and the Postgres sets), unlike Equipment (ADR 0016), which is match state. A rank that lasts a session would make "career" mean nothing. Respect is not XP: a Job pays no XP, and XP never counts toward a rank. A player who quits keeps rank and Respect frozen until they rejoin. A guest cannot join; a sandbox player earns nothing; bots never join.

**3. A Job is not an Event.** An Event is one happening at a time with a lifecycle of its own (`EventRun`, the Intel board). Jobs are many at once, each owned by one player or one Squad, and run beside whatever Event is on. The Job is a generic core (giver, objective, reward, failure) of which the Cartel is the first user; bus (#398) and ambulance (#436) jobs are to reuse it.

**4. A personal ladder.** Every player climbs their own ranks; nobody holds a seat another wants. Limited, contested positions were deferred: in a small friends lobby one player would be the boss and the rest would wait.

**5. Squads carry co-op, in team deathmatch only.** In ffa a Job is solo. In tdm the Squad runs it, and a Squad gets a leader (its creator, then its longest-standing member) who alone starts Jobs; every member at the moment of success gets the full reward, wherever they are. Squads stay team-deathmatch only. In a Squad only the leader's death fails a Job.
