-- The Hunter's Log (#336): one row per hunt, kept forever (no cascade: a player row cannot be deleted from under their hunts).
-- species is the index species or a mystery id; exact_species and variant are what was really hunted. kg is null for a mystery,
-- distance_m for a run over or a catch, hit_* are null when no hit diagram was taken (along and height are 0..255 for 0..1).

CREATE TABLE hunts (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    player_id     INTEGER NOT NULL REFERENCES players (id),
    species       TEXT    NOT NULL,
    exact_species TEXT    NOT NULL,
    variant       TEXT    NOT NULL DEFAULT '',
    kg            REAL,
    distance_m    REAL,
    weapon        TEXT    NOT NULL,
    hit_along     INTEGER,
    hit_height    INTEGER,
    hit_right     INTEGER,
    hit_head      INTEGER,
    hit_pose      INTEGER,
    hit_frame     INTEGER,
    x             REAL    NOT NULL,
    y             REAL    NOT NULL,
    z             REAL    NOT NULL,
    hunted_at     TEXT    NOT NULL
);

CREATE INDEX hunts_species_kg ON hunts (species, kg);
CREATE INDEX hunts_species_distance ON hunts (species, distance_m);
CREATE INDEX hunts_player ON hunts (player_id);
CREATE INDEX hunts_at ON hunts (hunted_at);
