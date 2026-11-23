# A pack tree is solid at its wood and its leaves stop nothing

Decided with the user during #375 (Inspect tree hitboxes). **Changes** what #158 gave trees (a solid trunk and a shot-only crown, both from the code-built tree) for every tree that is drawn as a pack model.

**Why.** The solids were the code-built tree (a thin trunk, stacked crown boxes or pyramids), while within 220 m the player sees a pack model matched to it by height only (#287). `scripts/tools/tree_collider_audit.py` drew every pair the game can make: 36 of 52 had more than 30% of the crown solid's silhouette standing in empty air (up to 71%: a pine drawn on a broadleaf's boxes, crowns starting metres under the first leaf), and the round broadleaf models stuck out of their crown solid by as much. Shots stopped where nothing was drawn. Fitting a crown solid per model would still be a box round a ragged shape.

**Decision.**
- A tree drawn as a pack model is solid at its **limbs** only (GLOSSARY): the model's wood is cut level every 5% of its height, each limb a cut crosses is joined to the one under it by a leaning solid, a fork gets a solid per arm, a straight run is one solid. A limb is followed until it has run inside foliage for 30% of the crown's height; a twig thinner than 30% of the trunk's foot has none.
- Its leaves stop nothing: no shot, rocket, sight line, aircraft. A model without wood (the bushes that stand in for thicket trees) is not solid at all.
- Limbs are solid for everyone (players, cars, shots), like a trunk: one solid set, no second set for shots.
- The limbs are data: `godot/shooter/data/tree_limbs.json`, written by `scripts/tools/tree_collider_audit.py --write` from the private pack (numbers only, no mesh). The server and every client choose each tree's model from that table by the same position hash the client draws by (`PackTreeLimbs.Dress`, `PackTreeMath.ModelOf`), whether or not the machine has the pack or has pack trees switched off.
- Everything else keeps the code-built solids: a map without `model.base_uv2`, the viewer (no data folder), hand-placed trees and bushes (`ObjectColliders`).

**Consequences.**
- Foliage is no cover and no concealment for the server: bots and turrets see through it, a helicopter flies through it and stops on the limbs.
- Trunks are as wide as they are drawn (0.6 to 2.1 m at the median forest scale against 0.53 m before), for walking and driving too.
- A client without the pack sees the baked trees with the pack models' solids.
- Village map: 200,832 solids became 253,524, distinct shapes 15,839 became 7,920 (a tree's limbs are built at the nearest 1% step of its scale, `TreeLimbs.ScaleStep`, so trees of nearly one size share shapes).
- A new tree model in `pack_trees.json` needs the table written again; `TreeLimbsTests` fails until it is.

**Considered.** A crown solid fitted per model (still phantom at the edges, and the user did not want leaves to stop shots). One straight trunk solid per model (off to the side on a leaning trunk, 39% phantom on a fork). A separate, thinner trunk for movement (two solid sets for no asked-for gain).
