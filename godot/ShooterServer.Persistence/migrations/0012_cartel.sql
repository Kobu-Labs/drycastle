-- The Cartel (#437, #494, ADR 0046): a player's membership and Respect live on the Profile. cartel_membership is the CartelMembership byte: 0 outsider (never joined),
-- 1 member, 2 quit (rank and Respect frozen until a rejoin). The Cartel rank is not stored: it follows from the Respect (cartel.json ranks).

ALTER TABLE players ADD COLUMN cartel_membership INTEGER NOT NULL DEFAULT 0;
ALTER TABLE players ADD COLUMN cartel_respect INTEGER NOT NULL DEFAULT 0;
