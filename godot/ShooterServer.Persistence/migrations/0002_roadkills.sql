-- Shooter v2 roadkill counter (ticket #117): kills made by a vehicle run-over, counted in addition to kills.

ALTER TABLE stats ADD COLUMN roadkills INTEGER NOT NULL DEFAULT 0 CHECK (roadkills >= 0);
