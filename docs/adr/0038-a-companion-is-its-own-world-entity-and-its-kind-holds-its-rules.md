# A Companion is its own world entity and its kind holds its rules

A Companion (#488, #429) is an animal that belongs to one player and stays with them. More kinds are wanted than the first (a flyer that circles its owner, a dog that fetches), each with rules of its own.

**1. Not an Animal.** A Companion is a world entity type of its own (58), not a state of `Animal`. `Animal.Behaviour` has one free state left, an Animal is a hit target, a body others stop for and a thing a vehicle throws, and none of that is true of a Companion: nothing hurts it and it stops nobody. It shares the look only: the species, variants and clips of `animals.json`, and the client's model builder.

**2. The kind holds the rules.** `Companion` (ShooterCore) is only what all share: the entity, the owner, place, pose and a few ways to move. What one does is a class derived from it (`HeelCompanion`, `WingCompanion`) with its own state enum, its own `StateMachineSpec` and its own numbers, read from the `rules` object of its `companions.json` entry by that class alone. A new kind is a class and one line in `CompanionKinds`. There is no shared behaviour table and no flags on the base for one kind's sake.

**3. The wire carries a pose, not a state.** The state byte is a `CompanionPose` (idle, walk, run, sit, fly, hover): which clip to play. The kind's own state never leaves the server, so the client, the wire and the keeper do not change with a new kind; only a clip no pose names adds a pose.

**4. The keeper owns presence.** `CompanionKeeper` knows who has which Companion out and makes or removes the entity: it is in the world while the server says its owner is present (alive, on foot). Kinds never decide whether they exist.

**5. Owned on the Profile, out in the session.** Owning one is an unlock row of kind `companion` (as a reward weapon or item, ADR 0016's split): a collection tier's `reward_tier` grants it for good. Which one is out is the player's choice among those they own: the Companion picker on the Equipment screen (F) sends a wish (event 114 `CompanionPick`), the server checks it against the roster (`CompanionRoster`: unlock rows, plus the one `/companion` gave for the session, or all in dev mode), answers with the roster (event 115 `Companions`) and keeps the choice in `players.companion` (SQLite migration 0010, Postgres 0005). Until a player picks, the first one owned is out. There is no "none" in the picker (a Companion is always on).

**Consequences.** A Companion walks on the terrain like an animal and is put beside its owner when it cannot follow (no pathfinding). Shots, vehicles, bots, wildlife and Thieves ignore it by not knowing the type. Protocol 82.
