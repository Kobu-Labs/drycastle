# Hunts are stored forever and mystery names are redacted server-side

The Hunter's Log (#336) is a permanent, server-wide book.

**1. Stored forever, one writer.** Every Hunt is a row of the progression database (`hunts`: Postgres migration 0002, SQLite 0007, an in-memory store), never updated or deleted, and no foreign key cascades (a player row cannot be removed from under their hunts). The server alone writes, through the store's write queue like every other progression write: the tick never waits for it. Rules (records, firsts, the Life List) are not in the store: `HuntBook` (ShooterCore) replays the rows at server start and takes each new hunt, so the tick answers records, the Life List and announcements from memory. Only the paged table reads the database, on the read lane.

**2. Redaction is the server's.** A client that has not caught a Mystery creature is never sent its name, its exact species, its shape or its hit diagram, in a table row, a card, a tile or an announcement: it is sent a hidden id (`mystery-N`, distinct per creature, saying nothing of which) and the client draws a blurred blob. One function (`HuntRedaction`) does it for every query, per asking player, so a first catch unlocks old rows and other players' tiles at once. The filter for a hidden creature is the one "Mystery hunts" option, so asking for it by name reveals nothing. A client-side hide would leak the name in the packet.

**Consequences.** An announcement of a mystery is encoded once per recipient. The book and its cache are lost with the process and rebuilt from the table, so the table is the truth. Guests, bots and sandbox players are not logged, and nothing logs for a game with no store.
