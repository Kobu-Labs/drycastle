-- The Mineral Log (#401): one row per find, kept forever (no cascade: a player row cannot be deleted from under their finds). A player finds each
-- mineral once; the place among a mineral's finders is the order of these rows (found_at, id), replayed at server start.

CREATE TABLE mineral_finds (
    id        INTEGER PRIMARY KEY AUTOINCREMENT,
    player_id INTEGER NOT NULL REFERENCES players (id),
    mineral   TEXT    NOT NULL,
    found_at  TEXT    NOT NULL,
    UNIQUE (player_id, mineral)
);
