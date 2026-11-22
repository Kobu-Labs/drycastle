-- The Companion a player chose on the Equipment screen (#488): an id of companions.json, empty for none chosen (the first one they own is then out).
-- Which Companions a player owns are unlock rows of kind 'companion'; this is only the choice among them.

ALTER TABLE players ADD COLUMN companion TEXT NOT NULL DEFAULT '';
