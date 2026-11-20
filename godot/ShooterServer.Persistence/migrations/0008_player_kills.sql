-- The Hunter's Log (#430): the longest kill of a player by another player, one row per killer (upserted when a kill is longer; a tie keeps the
-- earlier one). Not a hunt: no species, weight or rarity. Both ids are stored profiles (no cascade: a player row cannot be deleted from under it).

CREATE TABLE player_kills (
    killer_id  INTEGER PRIMARY KEY REFERENCES players (id),
    victim_id  INTEGER NOT NULL REFERENCES players (id),
    distance_m REAL    NOT NULL,
    weapon     TEXT    NOT NULL,
    x          REAL    NOT NULL,
    y          REAL    NOT NULL,
    z          REAL    NOT NULL,
    killed_at  TEXT    NOT NULL
);
