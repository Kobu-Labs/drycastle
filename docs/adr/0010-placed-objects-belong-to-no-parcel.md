# Placed objects belong to no parcel

Decided with the user while adding Street View poles (`docs/streetview.md`). On the polygon map only properties are parcels; the roads and the rest of the land are ground areas (map outline, `ground.py`). Poles stand beside roads, so they stood on no parcel. `edits.json` kept every placed object in the list of a parcel (`parcels.<ID>.objects`), the editor refused to place or move one onto ground that is no parcel, and `place_object` needed a parcel id.

**Decision.** A placed object is only a position. The list moves to the top level, `edits.json` `objects`, written sorted by id. Nothing about an object names a parcel: the editor and MCP place, move and duplicate it anywhere, and `objects.json` still carries a derived `parcel` (the one containing it, or null) so the game can group the nodes and a parcel-run can find the objects inside it. The old `owner` field is gone.

**Why a list of its own, not "under the nearest parcel".** An owner chosen for convenience is a lie that a mod can act on: replacing a parcel's contents would take a pole of the next road with it. Position is what the game needs, and the file already stored it absolutely.

**Compatibility.** `edits.parse` and the viewer's `EditsFile.Normalise` still read the old shape (objects under their parcel) and lift it, sorted by id, so a file from another branch loads; the next write uses the new shape. Other branches that add objects to `edits.json` will conflict on merge and are best rebased by loading and saving the file once with the new code. A parcel entry that held only objects is dropped by the lift.

Consequences:

- `place_object` takes `asset` and an exact `position`; `parcel` and an anchor are optional and only a way to compute a position in a parcel's front-edge frame. Without a parcel the rotation defaults to 0.
- The editor's placement ghost no longer turns red outside parcels, and a drag no longer snaps back. The inspector says "on no parcel" or "in P-NNNN".
- The instant path (`EditsWatcher`) diffs objects by id, not by parcel, so an object that crosses a parcel boundary, or leaves every parcel, is just replaced.
- Undo records each object by id at the top level; because the file is written sorted by id, an undone removal or an edit never reorders it.
