-- Shooter v2 loadout names (ticket #145): each loadout slot gets a display name, at most 24 characters. Existing slots are
-- named "Class 1" .. "Class 5" by their slot number, the same default a new profile gets.

ALTER TABLE loadouts ADD COLUMN name TEXT NOT NULL DEFAULT '';

UPDATE loadouts SET name = 'Class ' || (slot + 1) WHERE name = '';
