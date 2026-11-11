# Shooter v2: SQLite persistence behind repository interfaces, token identity, JSON data files

Decided for the Shooter v2 mod (progression, profiles, loadouts). Builds on ADR 0001 (Godot 4 C#, authoritative headless server) and does not change the networking layer of ADR 0002.

**Database.** SQLite through `Microsoft.Data.Sqlite`, one database file owned by the dedicated server. Persistence is reached only through repository interfaces, so the store can later be swapped for Postgres without touching game code. Schema changes are numbered `.sql` migrations applied in order at server start. No ORM: SQL is written by hand and lives with the migrations.

**Identity.** No accounts in v2. On first run the client generates a GUID player token and stores it in its client config file. The server maps token to a player row. The display name is stored on the profile, not on the token. There are no passwords. This is a deliberate deviation from a normal account system: anyone holding the token is that player, and a stolen token is a stolen profile. Revisit if the project ever needs real account recovery or public hosting (Accounts are out of scope in the map).

**Code layout.** Persistence lives in a new server-only project `godot/ShooterServer.Persistence`. It has no Godot dependency and is tested by `ShooterCore.Tests` or a sibling test project. `ShooterCore` stays scene-free and database-free: it holds only plain data types (Profile, Loadout, Unlock) that the simulation and the server both use.

**Data files.** Weapons, perks, killstreaks, and levels/unlocks are JSON files in `godot/shooter/data/`, each with a top-level `version` field. They load through the existing `DataFile`/`GameData` path, not a second loader. The server validates every submitted loadout against these files; a client cannot choose an item that the data files do not list.

**Consequences.** Server and client must agree on the data-file versions, as they already must agree on the map build (`docs/shooter-server.md`). Every later v2 ticket that stores or reads progression goes through the repository interfaces and the token-keyed profile.

**Addendum: PostgreSQL.** The swap this ADR anticipated is done for deployment: `PostgresProgressionStore` implements the same repository interfaces with Npgsql, with one baseline schema in `migrations-pg/` (the SQLite history is folded in; there are no older Postgres databases). `--db` / `SUCHOHRAD_DB` selects the backend (a `postgres://` URL or Npgsql connection string, else a SQLite path). The same contract tests run on both when `SHOOTER_TEST_PG` points at a throwaway database. SQLite remains the default for development and single-binary runs; a schema change must be added to both migration sets.
