# Generated vehicle glass is a node per pane with an outline polygon

Decided for issue #243 (follow-up to shatterable glass, PR #240). **Supersedes the vehicle part of decision 1 of ADR 0007** ("a pane is a rectangle in data, not a mesh"); decisions 2 and 3 of ADR 0007 stand.

**What changes.** For the vehicle models we generate ourselves (bus, flak truck, utility helicopter):

- Each breakable glass pane is its own glTF node `glass_N`, N being the pane's place in the `glass` list of the vehicle in `vehicles.json` (the same index as on the wire). Several slabs of one pane (a bus door leaf's two panes above and below the handrail bar) sit under one node. A door leaf's pane is a child of the leaf.
- The generator scripts (`scripts/make_bus_model.py`, `make_flak_model.py`, `make_utility_helicopter_model.py`) write that `glass` list into `vehicles.json` when they run without an output path, so the data is not measured by hand. A pane there is still `centre`, `normal` and `size` (its bounding rectangle) and now has an `outline`: a convex, counter-clockwise polygon of 3 to 12 points `[u, v]` in metres from the centre, `u` along the pane's width, `v` up it. A pane without an outline is its whole rectangle (the Kenney car and jeep, which have no pane nodes, stay as they are).
- The client hides the `glass_N` node while the pane is shattered and shows it again when the pane is whole (a new round, a repaired vehicle). The crack overlay is clipped to the outline and the fragments are cut from it (every fragment triangle of the rectangle's grid is clipped to the polygon).
- A shot is tested against the outline (`GlassPaneRect.Contains`), not only the rectangle: it is a handful of cross products on the few panes of one vehicle. A bullet that lands on the black frame of a window no longer hurts the glass.

**Why.** The original glass stayed in the model under the opening (darkened on the see-through bus and helicopter), and a triangular quarter light or a wrapped windscreen corner cracked and shattered as a rectangle. The generators already build every slab, so naming it and recording its outline costs nothing; the Kenney models cannot get this because their glass is a colour-map texture on the body mesh.

**Consequences.**
- The index is now also tied to the `.glb`: after a script run the model and `vehicles.json` must ship together (they do, in one commit). A node missing for an index only means the original glass stays visible.
- The bus windscreen is three panes (front and the two wrapped corners), so the bus has 18 panes. The limit per vehicle is 24 instead of 16.
- Only convex outlines are supported (the cut and the shader clip against half-planes). A concave pane is declared as several panes.
- Not changed: the crack overlay and the fragments are still flat quads in the pane's plane, and a door leaf's overlay stays at the closed position if the leaf is open.

**Note (ADR 0013, B-02, issue #257): the outline is tested at the rewound pose.** A shot is tested against a pane's outline in the frame of the vehicle where it stood at the shooter's view tick (see ADR 0007's note), so a pane is where the shooter saw it. The `glass_N` node and the outline are unchanged.
