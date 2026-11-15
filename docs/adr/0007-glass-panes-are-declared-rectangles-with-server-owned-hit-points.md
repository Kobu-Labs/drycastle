# Glass panes are declared rectangles with server-owned hit points

> **Partly superseded by ADR 0008** (`0008-generated-vehicle-glass-is-a-node-per-pane-with-an-outline.md`): the vehicle panes of our generated models (bus, flak truck, utility helicopter) are now a node per pane with an outline polygon, and the "darkened, not removed" consequence below no longer holds for them. A building's pane and the Kenney vehicles' panes are still rectangles as described here.

Decided while implementing shatterable glass (map #225, tickets #226 and #227). The tickets were never grilled with the user: these are the conservative defaults the implementation chose, written down so they can be challenged. The numbers live in `glass.json` and are cheap to change; the three decisions below are the ones that are hard to reverse.

**1. A pane is a rectangle in data, not a mesh.** A glass pane is a flat rectangle: centre, outward normal, width and height. A building's comes from `windows.json` (the pipeline already writes one record per window pane of a detailed building). A vehicle's is declared in the `glass` list of its type in `vehicles.json`, in the vehicle frame.

Why: the vehicle models have no separable panes. The generated ones (bus, flak truck, utility helicopter) merge all glass into the hull mesh under one material; the Kenney cars paint the glass into one colour-map texture. The server has no meshes at all, only an upright hit box per vehicle and the plain massing of a building as its wall. A rectangle is the one description both sides can use: the server tests the ray of a shot against it, the client draws on it.

Consequence: the crack and the shattered look are an overlay quad in front of the model's glass, which stays as it is underneath. A shattered pane of a see-through model is darkened, not removed. Removing it needs each pane as its own mesh in the model (a follow-up for the model generators).

**2. Pane state has its own reliable channel, keyed by owner and index.** A pane is `(owner, index)`: owner 0 is the map and the index is the pane's place in `windows.json` sorted by node name; any other owner is a vehicle's entity id and the index is the place in that type's `glass` list. The server keeps only panes that are not whole. A change goes out as event kind 170 (`owner, index, hp byte`), at most one per pane per tick; kind 171 says "the panes of this scope are whole except these" and serves the join catch-up, a new round and a vehicle that returned. Protocol 22.

Why not world entities (ADR 0002's generic channel, #104): that manager is capped at 256 live entities and sends position, rotation and owner for each. Panes never move on their own, there can be thousands of them, and almost all are whole almost all the time. State that changes a few times a match belongs on the reliable channel, as the camouflage and seat state already are.

Consequence: the index is only stable between a server and a client that read the same `windows.json` and `vehicles.json`. That already holds for the rest of the map and the data (the pole colliders, the vehicle types); a mismatch shows a crack on the wrong pane and nothing worse.

**3. A pane has no collider; shattering changes what a shot can reach only in vehicles.** A bullet that hit no player and stopped on a wall or a hull damages the pane it crossed there (a building's pane sits up to `building_reach` behind the wall, a vehicle's up to `vehicle_reach` inside the hit box). The wall and the hull take the shot as before, and the vehicle still takes its bullet damage. A blast damages every pane in its radius.

- Buildings have no interior. The wall behind a pane stays solid for bullets, players and cars whether the pane stands or not. (Bunkers have an interior since ADR 0009; they have no panes.)
- Vehicles: every rider used to be behind the hull (only the flak gunner is in the open). A shot whose ray crosses a **shattered** pane can now hit a rider behind it, through the same rule as the exposed gunner (`GunnerCover`). `shattered_exposes_riders` in `glass.json` turns this off.

Why: it is the smallest rule that makes breaking glass matter, and it reuses a mechanism that is already tested. An intact pane giving no extra protection keeps vehicle balance as it was.

**Defaults that are only numbers** (`glass.json`): 60 hit points for every pane; crack stages at 66 % and 33 % (three stages, the first as soon as a pane is damaged); a blast does 150 at its centre falling to nothing at its radius; panes are whole again at the start of a round and when their vehicle returns repaired; about 28 fragments, 12 shatter animations at once, none beyond 150 m.

See `docs/glass.md` for where the code is and what is left out.

**Note (ADR 0013, B-02, issue #257): the panes are tested at the rewound pose.** A shot at a vehicle's glass is tested in the frame of the vehicle where it stood at the shooter's view tick (the same pose as its hull and its exposed riders), not where it stands now. The pane keys (owner, index), the hit points and the wire are unchanged. The blast of a rocket still damages the panes at the vehicle's present pose, as the blast does everything else.
