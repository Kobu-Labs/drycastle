# Bunkers are the only buildings with an interior

Decided by grilling #224 (map #220) with the user. The Shooter v2 map (#104) ruled interiors out and ADR 0007 relies on it ("Buildings have no interior"). The three bunkers of the village are now the exception: a player walks in through an open doorway and shoots out of the firing slits. Every other building stays a solid block.

**1. Only bunkers.** A general interior needs a floor plan for some 380 buildings that the source data does not have, and it changes every fight in the village. A bunker is one small concrete room, there are three of them, and they are worth going into. Glass panes of other buildings keep ADR 0007's rule: the wall behind a pane stays solid.

**2. The bunker stays a code model, not a hand-made asset.** Map #220 is about hand-modelling real buildings in an external tool (#221). The bunker does not follow it: one model in `pipeline/src/drycastle_pipeline/bunker.py` already fits itself to each real footprint (8, 10 and 18 corners), so one change makes all three enterable and none of them waits for #221.

**3. All three bunkers are buildings; the placeable bunker stays closed.** A placed object collides as one solid box (`PropColliders`), so nobody can enter one. The third bunker was a placed `prim:bunker` standing on top of the plain building OSM draws there; that building is now the bunker (an override, like the other two) and the placed object is gone. `prim:bunker` remains in the catalogue as the closed form.

**4. The collision mesh is the real shell.** Other landmarks collide as a plain massing. A bunker's collision mesh has the walls with their openings, the ceiling and the dome, so the loader needs no change (colliders are already triangle meshes). Its ground floor has no collision: the ground under a building is flattened, and a player inside stands on the terrain like everywhere else. (The terrain collision of the chunks round a bunker is therefore built at the full 4 m grid, because the decimated mesh sat up to 0.3 m under the flattened floor and put a step in the doorway.) The floors under it are real collision.

Consequences:

- Bots do not go in. Their navigation mesh is baked for a 0.5 m body radius on 0.5 m cells, and the doorway is 1.0 m wide. They still see and shoot through the openings.
- Scale is for play, not true to life: 0.5 m walls (the real ones are about a metre), a doorway a standing player fits through.

**5. A bunker room is a shelter.** Splash damage ignores walls everywhere else (the v1 rule in `ProjectileMath`). For a bunker the pipeline exports each room as a volume (`bunkers.json`), and a blast hurts a player only when both are in the open or both are in the same room. So a rocket on the roof does nothing to the players inside, and one fired in through the doorway does nothing to those outside. The MOAB and a direct hit do not ask. Making every wall block blasts was rejected: it changes every fight in the village and needs a ray per victim against geometry the server only has as triangle soup.

**6. Three floors go down, joined by ladders the player gets on.** Each bunker has the ground floor and three rooms under it, one per floor, on the same footprint (2.4 m floor to floor, so the lowest is about 7 m down). A square hatch in each floor, in a different place on each, leads to the next; the ground floor's hatch is a hole cut out of the terrain (visual and collision), the only place a bunker changes it. The ladder is a movement state, not a collision shape: the interact key, near a ladder and looking at it, gets the player on; forward climbs and back descends; interact or jump lets go; the top and the bottom step off by themselves. It is part of the shared movement step, so the client predicts it exactly as the server runs it, and it needs no new replicated state (the ladder state rides in the predicted movement state; other players are seen sliding up and down in their normal pose, there is no climbing animation). A player on a ladder cannot fire. Moving the body by the step instead of by the physics was chosen over a ladder collider with an area trigger: the hatch is 1 m wide, and physics against a ladder volume in it would snag.

**7. A bunker interior is dark with lamps.** Client only. In a room the day's ambient light is turned down to a fifth, one warm unshadowed lamp hangs in every room (made only for the bunker near the camera), and the rooms are furnished for a purpose (ground: ammunition under the slits; first floor down: crew quarters; second: ammunition store; third: command post).

Not done: bots do not avoid the interior floors when they pick a target; a bot after a player underground walks to the nearest point it can reach, which is at the door.
