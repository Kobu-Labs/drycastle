# Flood wall

The concrete wall between the river and the village is a kit of placeable pieces, generated in
`pipeline/src/suchohrad_pipeline/floodwall.py` (pictures and the manifest sync in `floodwall_preview.py`).
It is modelled after photos of the real wall: a stepped concrete profile with a ledge on the village side,
a thinner upper wall behind it, a pale blue pipe railing on top, piers at the openings, a ladder, and the
plaque of the flood of 4 April 2006.

## Profile and frame

Local metres, z up, `x` along the wall. **`-y` is the village side (the ledge), `+y` the river.** The origin is
on the ground under the centre line of the upper wall, in the middle of the piece's length, so a run is placed
along the line the wall stands on. `rotation_deg` 0 = the wall runs east-west with the ledge to the south.

| Part | Where (y across, z up) |
|---|---|
| base | y -1.33 to 0.15, from 1 m in the ground (so a piece also sits on uneven ground) |
| toe step | 0.18 m wide, top at 0.62 m |
| ledge (the walkway) | 1 m wide (y -1.15 to -0.15), top at 0.90 m |
| upper wall | 0.3 m thick, top at 1.80 m |
| rail | one pipe at 2.25 m, posts 2 m apart |
| pier | stands 0.25 m onto the ledge (0.75 m left to walk past), top at 1.95 m |

Two things differ from the real wall so that it can be walked: the ledge is 1 m wide (about 0.7 m in the
photos; the player is 0.7 m wide), and its top is 0.90 m above the ground so that a jump (about 1.1 m) reaches
it. The railing panels that stand across the ledge at the real access point are on the river-side landing
instead. The river side of the wall is not in the photos: its plain face and the landing are guesses.

## Pieces

`make floodwall-preview` draws them in `out/floodwall/` (`GLB=1` also writes the models).

| Asset | Length | What |
|---|---|---|
| `prim:floodwall_straight_4`, `prim:floodwall_straight_2` | 4 m, 2 m | a run of wall with the walkway and the railing |
| `prim:floodwall_joint` | 0 | a pier for a joint between two pieces; at a bend it also fills the wedge in the ledge |
| `prim:floodwall_access` | 4 m | a pedestrian opening between two piers (stop-log slots in their inner faces), a ladder from the ground, the plaque, and on the river side a landing with railings and a second ladder |
| `prim:floodwall_gate` | 6 m | the road gate: two thick piers and a sill with the stop-log groove, 4 m clear |
| `prim:floodwall_gate_pier` | 1 m | one gate pier: ends a wall, or two of them make a gate of another width |

Pieces butt end to end: the next piece's origin is half of each length further along the wall. Their category is
`wall`, so they are in the editor's Objects tool with the other walls.

**Bends.** Pieces turn about the point where their centre lines meet. Put a `floodwall_joint` on that point,
turned to the bisector (the mean of the two rotations): its pier hides the meeting upper walls, and where the
ledge is on the outside of the bend its quarter drum fills the wedge that opens there. One joint covers a bend
of up to 25 degrees (`JOINT_MAX_BEND_DEG`); a sharper turn takes several pieces. With the ledge on the inside
the pieces overlap, which does not show (horizontal faces have one tone for that reason).

`floodwall.lay_run(names, start, heading_deg, bends)` gives the placements (asset, x, y, `rotation_deg`) of a
list of pieces laid end to end, with joints turned to the bisector.

## Collision

A placed object normally collides as one box of its category and size (`ObjectColliders.ForCategory`), which
would bury the walkway. So a piece lists what is solid: `solids` in its `assets/manifest.json` entry, boxes
`{"min": [x, y, z], "max": [x, y, z], "surface": "wall" | "metal"}` in the piece's frame (z up). An asset
with `solids` collides with those boxes and nothing else (`ObjectColliders.ParseSolids` in ShooterCore,
`MapLoader.AddObjectColliders`), on the server and the client alike. Any asset may use this.

Solid: the base and ledge, the toe, the upper wall, the piers, the landing, the gate sill (5 cm, cars drive
over it) and the rails (a thin box each: a shot passes between the wall top and the rail, a player does not
get over). The ladders are not solid and cannot be climbed (the game has no ladders): the ledge and the
landing are reached with a jump, the wall is crossed at an access point or a gate.

The entry also has `length_m`, the step along the wall to the next piece.

## Changing the kit

After any change run `make floodwall-manifest` (a test compares the manifest with the generator) and
`make floodwall-preview` to look at the result. The map export writes the models as `prims/floodwall_*.glb`.
Faces are flat colours; the long vertical faces are cut into 2 m formwork panels with a slightly different
tone each and a few dark runs under the edges, from a hash of the piece name and the panel, so the output is
the same on every run.
