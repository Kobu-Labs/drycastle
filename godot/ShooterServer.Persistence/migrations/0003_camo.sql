-- Shooter v2 camouflage (ticket #140): one encoded CamoSpec per player (30 bytes, see ShooterCore CamoSpec.Encode).
-- A player with no row uses the default look.

CREATE TABLE camo (
    player_id INTEGER PRIMARY KEY REFERENCES players (id) ON DELETE CASCADE,
    spec      BLOB    NOT NULL CHECK (length(spec) = 30)
);
