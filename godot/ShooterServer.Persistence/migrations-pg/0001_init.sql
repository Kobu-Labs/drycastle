-- Shooter v2 schema for PostgreSQL (ADR 0003). The SQLite migrations 0001-0006 are folded into this baseline: there are no older
-- Postgres databases to upgrade. Timestamps are timestamptz (UTC); camouflage blobs are 30 (codec 1) or 33 (codec 2) bytes.

CREATE TABLE players (
    id          BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    token       TEXT        NOT NULL UNIQUE,
    name        TEXT        NOT NULL DEFAULT '',
    xp          INTEGER     NOT NULL DEFAULT 0 CHECK (xp >= 0),
    active_slot INTEGER     NOT NULL DEFAULT 0 CHECK (active_slot BETWEEN 0 AND 4),
    created_at  TIMESTAMPTZ NOT NULL,
    last_seen   TIMESTAMPTZ NOT NULL
);

CREATE TABLE loadouts (
    player_id        BIGINT  NOT NULL REFERENCES players (id) ON DELETE CASCADE,
    slot             INTEGER NOT NULL CHECK (slot BETWEEN 0 AND 4),
    primary_weapon   TEXT    NOT NULL DEFAULT '',
    secondary_weapon TEXT    NOT NULL DEFAULT '',
    perk             TEXT    NOT NULL DEFAULT '',
    streak1          TEXT    NOT NULL DEFAULT '',
    streak2          TEXT    NOT NULL DEFAULT '',
    streak3          TEXT    NOT NULL DEFAULT '',
    name             TEXT    NOT NULL DEFAULT '',
    PRIMARY KEY (player_id, slot)
);

CREATE TABLE unlocks (
    player_id BIGINT NOT NULL REFERENCES players (id) ON DELETE CASCADE,
    kind      TEXT   NOT NULL,
    item_id   TEXT   NOT NULL,
    PRIMARY KEY (player_id, kind, item_id)
);

CREATE TABLE stats (
    player_id BIGINT PRIMARY KEY REFERENCES players (id) ON DELETE CASCADE,
    kills     BIGINT NOT NULL DEFAULT 0,
    deaths    BIGINT NOT NULL DEFAULT 0,
    matches   BIGINT NOT NULL DEFAULT 0,
    roadkills BIGINT NOT NULL DEFAULT 0 CHECK (roadkills >= 0)
);

CREATE TABLE weapon_kills (
    player_id BIGINT NOT NULL REFERENCES players (id) ON DELETE CASCADE,
    weapon_id TEXT   NOT NULL,
    kills     BIGINT NOT NULL DEFAULT 0,
    PRIMARY KEY (player_id, weapon_id)
);

CREATE TABLE loadout_camo (
    player_id BIGINT  NOT NULL REFERENCES players (id) ON DELETE CASCADE,
    slot      INTEGER NOT NULL CHECK (slot BETWEEN 0 AND 4),
    spec      BYTEA   NOT NULL CHECK (length(spec) IN (30, 33)),
    PRIMARY KEY (player_id, slot)
);
