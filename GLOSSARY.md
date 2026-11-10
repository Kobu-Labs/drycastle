# Glossary

**Base map**:
The versioned, shared data artifact of the real village of Suchohrad (terrain, roads, parcels, default buildings, landmarks, navigation data). Every mod consumes a specific version of it; it is not a game itself.
_Avoid_: world, level, scene

**Mod**:
A separate game built on the base map, with its own gameplay, database, and multiplayer server (e.g. a PvP shooter, a DayZ-style survival game, an MMO RPG).
_Avoid_: game mode, plugin

**Parcel**:
A land unit of the base map with a stable ID of our own (not the cadastral number), covering every cadastral parcel including roads and fields. Its boundary is fixed; its contents (building, props, events) can be replaced by a mod.
_Avoid_: plot, lot, tile

**Map pipeline**:
The scripted, re-runnable process that generates the base map from source data with no manual map building.

**Parcel kind**:
The category of a parcel (building plot, garden, road, field, forest, water, other), assigned by rules.

**Replacement scene**:
What a mod supplies to replace a parcel's base contents.

**Property group**:
Several parcels that belong to one owner and are treated as one property, sharing a fence around their combined outer edge. Parcel IDs and boundaries do not change; a parcel is in at most one group.
_Avoid_: merged parcel, estate

**Hand edit**:
A deliberate, developer-made change to a parcel's or property group's contents (fence, trees, placed objects) that survives every pipeline re-run.

**UI standard**:
The checkable definition of AAA-level UI/UX in `docs/standards/ui-ux.md`. It applies to every screen, including the map editor and developer tools, and gates acceptance of any UI issue.
_Avoid_: style guide, polish pass
