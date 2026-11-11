-- Camo codec 2 (character: body, skin tone, headgear) grows a CamoSpec from 30 to 33 bytes. Rows saved before stay 30-byte
-- (codec 1) blobs and still decode, with the default character; a row is rewritten as 33 bytes the next time it is saved.
-- SQLite cannot change a CHECK in place, so the table is rebuilt.

CREATE TABLE loadout_camo_new (
    player_id INTEGER NOT NULL REFERENCES players (id) ON DELETE CASCADE,
    slot      INTEGER NOT NULL CHECK (slot BETWEEN 0 AND 4),
    spec      BLOB    NOT NULL CHECK (length(spec) IN (30, 33)),
    PRIMARY KEY (player_id, slot)
);

INSERT INTO loadout_camo_new (player_id, slot, spec) SELECT player_id, slot, spec FROM loadout_camo;

DROP TABLE loadout_camo;

ALTER TABLE loadout_camo_new RENAME TO loadout_camo;
