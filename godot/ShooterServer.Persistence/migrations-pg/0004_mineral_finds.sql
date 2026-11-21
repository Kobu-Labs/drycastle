-- The Mineral Log (#401), the same table as SQLite migration 0009: one row per find, kept forever. A player finds each mineral once; the
-- place among a mineral's finders is the order of these rows (found_at, id).

CREATE TABLE mineral_finds (
    id        BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    player_id BIGINT      NOT NULL REFERENCES players (id),
    mineral   TEXT        NOT NULL,
    found_at  TIMESTAMPTZ NOT NULL,
    UNIQUE (player_id, mineral)
);
