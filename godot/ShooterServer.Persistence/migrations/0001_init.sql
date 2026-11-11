-- Shooter v2 initial schema (ADR 0003). Timestamps are UTC ISO-8601 text.

CREATE TABLE players (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    token       TEXT    NOT NULL UNIQUE,
    name        TEXT    NOT NULL DEFAULT '',
    xp          INTEGER NOT NULL DEFAULT 0 CHECK (xp >= 0),
    active_slot INTEGER NOT NULL DEFAULT 0 CHECK (active_slot BETWEEN 0 AND 4),
    created_at  TEXT    NOT NULL,
    last_seen   TEXT    NOT NULL
);

CREATE TABLE loadouts (
    player_id        INTEGER NOT NULL REFERENCES players (id) ON DELETE CASCADE,
    slot             INTEGER NOT NULL CHECK (slot BETWEEN 0 AND 4),
    primary_weapon   TEXT    NOT NULL DEFAULT '',
    secondary_weapon TEXT    NOT NULL DEFAULT '',
    perk             TEXT    NOT NULL DEFAULT '',
    streak1          TEXT    NOT NULL DEFAULT '',
    streak2          TEXT    NOT NULL DEFAULT '',
    streak3          TEXT    NOT NULL DEFAULT '',
    PRIMARY KEY (player_id, slot)
);

CREATE TABLE unlocks (
    player_id INTEGER NOT NULL REFERENCES players (id) ON DELETE CASCADE,
    kind      TEXT    NOT NULL,
    item_id   TEXT    NOT NULL,
    PRIMARY KEY (player_id, kind, item_id)
);

CREATE TABLE stats (
    player_id INTEGER PRIMARY KEY REFERENCES players (id) ON DELETE CASCADE,
    kills     INTEGER NOT NULL DEFAULT 0,
    deaths    INTEGER NOT NULL DEFAULT 0,
    matches   INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE weapon_kills (
    player_id INTEGER NOT NULL REFERENCES players (id) ON DELETE CASCADE,
    weapon_id TEXT    NOT NULL,
    kills     INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (player_id, weapon_id)
);
