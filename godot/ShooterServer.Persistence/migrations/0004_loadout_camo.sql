-- Shooter v2 camouflage per loadout (ticket #141): each of the five loadout slots keeps its own CamoSpec (30 bytes, see
-- ShooterCore CamoSpec.Encode). A slot with no row uses the default look. Existing per-player looks (0003_camo) are copied
-- into every slot so nobody loses a saved look, then the per-player table is dropped.

CREATE TABLE loadout_camo (
    player_id INTEGER NOT NULL REFERENCES players (id) ON DELETE CASCADE,
    slot      INTEGER NOT NULL CHECK (slot BETWEEN 0 AND 4),
    spec      BLOB    NOT NULL CHECK (length(spec) = 30),
    PRIMARY KEY (player_id, slot)
);

INSERT INTO loadout_camo (player_id, slot, spec)
SELECT c.player_id, s.slot, c.spec
FROM camo c
CROSS JOIN (SELECT 0 AS slot UNION ALL SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT 3 UNION ALL SELECT 4) s;

DROP TABLE camo;
