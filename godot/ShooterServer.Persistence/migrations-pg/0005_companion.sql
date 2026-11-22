-- The Companion a player chose on the Equipment screen (#488), the same column as SQLite migration 0010: an id of companions.json, empty for none chosen.

ALTER TABLE players ADD COLUMN companion TEXT NOT NULL DEFAULT '';
