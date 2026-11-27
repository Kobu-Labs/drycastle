-- The Cartel (#437, #494, ADR 0045), the same columns as SQLite migration 0012: cartel_membership is the CartelMembership byte (0 outsider, 1 member, 2 quit),
-- cartel_respect the Respect earned. The rank follows from the Respect (cartel.json ranks).

ALTER TABLE players ADD COLUMN cartel_membership INTEGER NOT NULL DEFAULT 0;
ALTER TABLE players ADD COLUMN cartel_respect INTEGER NOT NULL DEFAULT 0;
