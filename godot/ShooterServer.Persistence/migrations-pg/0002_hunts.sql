-- The Hunter's Log (#336), the same table as SQLite migration 0007: one row per hunt, kept forever (no cascade: a player row cannot
-- be deleted from under their hunts). kg is null for a mystery, distance_m for a run over or a catch, hit_* are null when no hit
-- diagram was taken (along and height are 0..255 for 0..1).

CREATE TABLE hunts (
    id            BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    player_id     BIGINT      NOT NULL REFERENCES players (id),
    species       TEXT        NOT NULL,
    exact_species TEXT        NOT NULL,
    variant       TEXT        NOT NULL DEFAULT '',
    kg            REAL,
    distance_m    REAL,
    weapon        TEXT        NOT NULL,
    hit_along     SMALLINT,
    hit_height    SMALLINT,
    hit_right     BOOLEAN,
    hit_head      BOOLEAN,
    hit_pose      SMALLINT,
    hit_frame     SMALLINT,
    x             REAL        NOT NULL,
    y             REAL        NOT NULL,
    z             REAL        NOT NULL,
    hunted_at     TIMESTAMPTZ NOT NULL
);

CREATE INDEX hunts_species_kg ON hunts (species, kg DESC);
CREATE INDEX hunts_species_distance ON hunts (species, distance_m DESC);
CREATE INDEX hunts_player ON hunts (player_id);
CREATE INDEX hunts_at ON hunts (hunted_at DESC);
