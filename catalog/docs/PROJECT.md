# Asset Catalogue: project description and handoff

Written 2026-11-25 for the session that continues this work. It describes what the project is, what exists today, what the standalone product must become, and what is still undecided. The web explorer has its own document: WEB-EXPLORER.md in this folder.

## 1. What this is

A searchable catalogue of 3D game assets. A vision model looks at pictures of every model and writes a structured record for it (what it is, what it is for, where it belongs, what it is made of). Code measures the exact facts (size, pivot, triangle count). People, AI agents and build pipelines then find assets by typing what they need: "water cooler", "something to hide behind at crouch height", "wooden furniture under 1 m for a kitchen".

**Priority, set by the owner and not negotiable: the quality of search results comes first.** Speed, cost and simplicity are traded away for it when they conflict. The owner also wants a production-level product, not a quick tool.

## 2. Where it is going

Today the catalogue is a Python package inside the game repository "drycastle". The decision is to move it into **its own repository and project** with these properties:

- **Self-hostable.** Someone else can run it on their own machine or server, most likely with Docker.
- **Own keys.** The person hosting supplies their own Anthropic API key. Without a key the product must still work, with reduced search (see section 8).
- **Ships with a catalogue already built.** As understood from the owner: the described records and the search index come included, so a self-hoster does not have to run the expensive describe step before they can search. See "To confirm" in section 14: whether pictures and vectors ship too, and the licence question.
- **Three ways in:** a web explorer (new, built from scratch), an MCP server for AI agents (not built), and the command line (exists).

## 3. Status today

| Part | State |
|---|---|
| Ingest, measure, render | Done for 4,506 Synty assets |
| Describe | Done for one pack only: "office", 160 assets, described by Claude Sonnet 5.5 |
| Index, text and image embeddings | Done for those 160 assets |
| Search engine | Working: query understanding, three retrievers, fusion, rerank, "no good match" |
| Evaluation | 60 test queries on the office pack, judged, nine search configurations scored |
| Web page | A prototype exists. The owner judged it very poor. **It is to be discarded entirely**, code and design |
| MCP server | Not built |
| Docker, auth, packaging | Not built |
| Full describe run (the other 4,035 assets) | Not started |
| Model comparison (Haiku vs Sonnet vs Opus records, scored through search) | Not started; the owner asked for it after the engine existed |

Spend on the Anthropic API so far is roughly $25 (estimated from token counts, not from the billing page).

## 4. Words used in this project

- **Asset**: one model file. Id is `<source>/<pack>/<name>`, for example `synty/office/water_cooler_01`.
- **Pack**: a purchased set of assets (office, military, farm ...).
- **Variant group**: assets that are the same object with a numeric suffix (`chair_01`, `chair_02`). Search shows one result per group and folds the others in.
- **Helper**: a LOD or collision mesh belonging to another asset. Helpers get no record of their own.
- **Record**: everything the catalogue knows about one asset (section 6).
- **Facet**: a field whose values come from a fixed list (kind, category, material, setting, placement, function, style, condition), plus the measured ones (colour, size class, pack).
- **Vocabulary**: those fixed lists. The describing model may only choose from them.
- **Describer**: the Claude model that wrote the records. Records are stored per describer so several can be compared.
- **Retriever**: one way of finding candidates. There are three: keyword, text embedding, image embedding.
- **Rerank**: a Claude call that grades the best candidates 0 to 3 against the query.
- **Abstain**: the search returns nothing because no candidate was graded good enough. This is intended behaviour.

## 5. The build pipeline

Six stages, each a command of the `catalog` CLI. Every stage caches its output per asset, keyed by the file's content hash and the stage version, so unchanged models are skipped on a rerun.

1. **Ingest**: scans the source folders, assigns ids, groups variants, marks LOD and collision helpers. Writes `assets.jsonl`.
2. **Measure**: reads the geometry. Size in metres, size class, pivot position, overall form, flat sides, triangle count, rig, animation names. No AI. 4,603 models took 7.6 seconds.
3. **Render**: draws four views (three-quarter, front, top, back) at 768 px and a 1536 px contact sheet, with scale bars and a 1.8 m human figure for scale, and extracts dominant colours. Needs OpenGL.
4. **Describe**: one Claude call per asset with the contact sheet, the measured facts and the vocabulary. Returns the record's descriptive half. This is the paid step.
5. **Index**: joins measure, render and describe into one record per asset and loads a SQLite database with full-text search. Also exports the records as JSONL.
6. **Embed**: turns record text and rendered views into vectors with two local models.

Inputs are GLB files only. **Converting other formats to GLB is outside this product** (owner's decision).

## 6. The record

One per asset. Four layers:

**Identity and files**: id, source, pack, name, path of the model file, variant group, paths of the four views and the contact sheet, which describer wrote it.

**Chosen from the vocabulary by the describer**: kind; category (one leaf of a tree) and secondary categories; materials; settings (places it belongs); placements (how it sits in a scene); functions (what it does for a level designer); styles; condition; real front (which rendered side is the true front).

**Free text by the describer**: plain name; description (what it looks like); usage (where and how to use it); aliases; distinguishing (what sets it apart from its siblings); snaps_with; likely queries (what someone would type to find it); doubts (what the describer was unsure about).

**Measured by code**: width, height, depth in metres; longest side; size class; form; flat faces; pivot; triangle count; rig yes/no; animation names; colour names, largest area first.

There is deliberately **no "state" facet** (owner's decision).

Example (shortened):

```json
{
  "id": "synty/office/water_cooler_01",
  "kind": "prop",
  "category": "prop/appliance/vending-cooler",
  "materials": ["plastic", "metal"],
  "settings": ["break-room", "office", "reception"],
  "placements": ["floor-standing", "against-wall"],
  "styles": ["office"],
  "condition": ["intact"],
  "plain_name": "office water cooler",
  "description": "Tall boxy floor-standing water dispenser with a grey cabinet body and a recessed front alcove holding a small tap. A light blue inverted water bottle ...",
  "usage": "Place in office break rooms, reception areas, corridors or waiting rooms as set dressing ...",
  "aliases": ["water dispenser", "water fountain", "bottled water dispenser"],
  "likely_queries": ["water cooler", "something for a break room", "drinking water station"],
  "doubts": ["... it is unclear whether hot and cold taps are modelled."],
  "size_m": [0.33, 1.47, 0.40],
  "size_class": "furniture",
  "pivot": "base-centre",
  "tris": 160,
  "has_rig": false,
  "colours": ["grey", "light blue"]
}
```

## 7. The vocabulary

Kept in one YAML file, with a definition for every term (the explorer shows these as explanations).

| Facet | Terms | Examples |
|---|---|---|
| kind | 16 | building, building-piece, structure, vehicle, furniture, prop, plant, rock |
| category | 218 leaves in a tree | `furniture/seating/chair`, `prop/appliance/vending-cooler` |
| material | 24 | metal, wood, plastic, concrete, glass |
| setting | 61 | kitchen, office, break-room, military base |
| placement | 18 | floor-standing, against-wall, wall-mounted, on-surface |
| function | 26 | full-cover, low-cover, seating, storage, obstacle |
| style | 17 | military, post-apocalyptic, industrial, rural, medieval |
| condition | 7 | intact, worn, rusty, damaged, destroyed, overgrown, dead |
| size class (measured) | 7 | hand, tabletop, furniture, vehicle-scale, room, building, terrain |

Why closed lists: in a pilot on 295 assets, the same model run twice agreed with itself on only 31% of free-form function tags. With open tags, filters cannot be trusted.

**The vocabulary is marked "working version, not yet approved".** The owner has not signed it off. It was derived from the pilot's free-form output and has only been exercised on the office pack.

## 8. How a search is answered

1. **Understand the query** (Claude Haiku, optional). Splits the text into search text, *required* constraints (only what the person stated outright, such as "under 1 m" or "wooden") and *suggested* facets (inferred; they only nudge the ranking and never remove results).
2. **Retrieve** with three methods in parallel, 40 candidates each, all local:
   - keyword: full-text search over the record, with spelling repair;
   - text embedding: meaning of the query against the record text (several vectors per asset, including one per likely query);
   - image embedding: the query against the rendered pictures.
3. **Fuse** the lists into one ranking.
4. **Rerank** (Claude Sonnet, optional): grades the top 30 from 0 to 3 by reading their records as text. Grade 0 is dropped.
5. **Fold variants** into one result each. If nothing is left, **abstain**.

Other operations: **browse** (all assets passing a set of filters, no query), **facet counts** (how many assets each filter value would give), **similar** (assets that look and read like a given one), **asset detail** (record plus its variants).

Cost per search with both Claude steps on: roughly 1 to 2 US cents, almost all of it rerank (estimate from prompt sizes). Everything else is free. With no API key the two Claude steps are skipped and search still works.

## 9. Evaluation

A set of 60 queries on the office pack in five styles (keyword, descriptive, need-based, with constraints, and "negative" queries that have no right answer). 1,618 query-asset pairs were graded 0 to 3 by Claude Opus looking at the pictures without seeing the records. A human overrides file is supported; none has been written yet.

| Setup | nDCG@10 | Recall@10 | MRR | Abstains on no-answer queries |
|---|---|---|---|---|
| keyword only | 0.725 | 0.750 | 0.788 | 58% |
| text embedding only | 0.899 | 0.955 | 0.936 | 0% |
| image embedding only | 0.778 | 0.844 | 0.794 | 0% |
| keyword + text | 0.898 | 0.955 | 0.934 | 0% |
| all three | 0.875 | 0.938 | 0.916 | 0% |
| all three + rerank | 0.901 | 0.917 | 1.000 | 33% |
| full (all three + understanding + rerank) | 0.881 | 0.906 | 0.979 | 67% |

Reading: text embeddings carry most of the quality. Rerank puts the right answer first and makes "no good match" possible, at a small cost in recall. Image embeddings did not help ranking on this set. The set is small (160 assets, one pack), so differences of a few hundredths are noise.

Pilot comparison of describers (50 assets judged blind by Opus): factual errors Haiku 86, Sonnet 39, Opus 12. Projected cost for the whole catalogue with the batch API: about $4, $40, $79.

## 10. Data

`~/coding/catalog` on the owner's machine is a copy, taken 2026-11-25, of the catalogue data. 1.5 GB, 32,657 files.

| Path | Holds | Size |
|---|---|---|
| `assets.jsonl` | the asset list | 1.8 MB |
| `measure/` | geometry facts, one JSON per asset | 19 MB |
| `render/` | four PNG views + contact sheet + JSON per asset | 1.4 GB |
| `describe/<describer>/` | records as written by the describer | under 1 MB (office only) |
| `records/<describer>/` | joined records as JSONL, one file per pack | under 1 MB |
| `index/<describer>/catalog.db` | SQLite search database | 1.3 MB |
| `embed/` | two vector sets (NumPy) with their keys | 8 MB |
| `evalset/` | judgments, run results, cache of model answers | 2.3 MB |
| `describe-pilot/`, `pilot-report/` | the early open-vocabulary pilot | 4 MB |

Notes:
- `render/` and `measure/` also contain leftovers from non-Synty packs (`repo/`, `animal-kit/`) from before the owner limited the catalogue to Synty. They are not in the index.
- Paths inside records point at `assets-private/synty/glb/...` in the game repository. A standalone product needs its own rule for where model files live.
- The pictures and records are derived from **paid Synty assets**. See section 13.

## 11. Code

Location: `catalog/` in the worktree `/home/jjakubec/coding/drycastle-rc-0.14`, branch `photo-houses-0.14`, commit `e09f88d4` ("Asset catalog: searchable index of the asset packs"). Not pushed. Python 3.13, managed with uv; about 15,000 lines including the vocabulary; 101+ tests passing.

**Carry over** to the new repository:
- the pipeline stages, the glTF reader, the renderer, the store and runner;
- the record, the vocabulary file and loader;
- the index, the three retrievers, fusion, query understanding, rerank, the engine;
- the service layer: one class offering search, similar, browse, facets, asset detail, picture lookup, vocabulary and status, with no HTTP in it. Built so the web API and the MCP server share it;
- the evaluation package and the office query set;
- the tests.

**Do not carry over:** the `web/` folder (the discarded page), and the pilot code unless the pilot report is still wanted.

**Tied to the game repository and must be made standalone:** the config assumes paths relative to that repo and finds the API key in its `.env`; the renderer has a hard-coded table of Synty texture atlases; the source list in `catalog.yaml` names that repo's folders.

Local models: text `Qwen/Qwen3-Embedding-0.6B` (1024 numbers per vector), image `google/siglip2-so400m-patch14-384`. Claude models: Haiku 5.5 for query understanding, Sonnet 5.5 for describing and rerank, Opus 5.5 for judging.

An architecture diagram with two step-by-step walk-throughs (build, one search) exists in the owner's excalibruh tool under the id `asset-catalog`.

## 12. What has to be built for the standalone product

Stated as outcomes, not designs.

1. **A standalone repository** that builds, tests and runs without the game repository.
2. **A clear split between building a catalogue and serving one.** Serving must not need the renderer, a GPU or the source model files to answer a search.
3. **A catalogue that can be shipped and mounted**: one folder that a server can read, with a statement of what produced it (describer, vocabulary version, embedding models), so a server can refuse a folder it cannot read correctly.
4. **A stable, documented HTTP API** for the explorer and for other programs. The prototype API (status, vocabulary, facets, browse, search, streamed search, similar, asset, image) shows the needed operations; the explorer document lists what is missing from it.
5. **An MCP server** over the same service: search, get one asset, similar, list facets. Usable both by local agent tools and over the network.
6. **Access control.** The prototype only answers on localhost with no login. A hosted instance needs authentication and a limit on paid model calls, because every search spends the host's key.
7. **Configuration by environment**: API key (optional), port, where the catalogue is, which Claude models to use, spending limits.
8. **Docker packaging** for serving, and a documented way to run the build for people who catalogue their own assets.
9. **The web explorer** (WEB-EXPLORER.md).
10. **Support for sources other than Synty**, if the product is meant for other people's assets (open question).

## 13. Constraints

- **Quality of results first.**
- **English only**: records, vocabulary, queries, interface.
- **Synty packs only** for now; other packs are ignored by the owner's instruction.
- **Paid assets.** The model files are licensed. The game repository is public on GitHub, so nothing derived from the assets was ever committed there. Until the licence question in section 14 is answered, treat pictures, records and vectors as private: no public repository, no public image registry, no public demo.
- **Only the Anthropic API receives asset pictures**, and only during describe. The explorer must load nothing from third-party servers.
- **Low API concurrency.** The owner's organisation allows only a few simultaneous requests; four to five threads worked, more produced rate-limit errors.
- **No GLB conversion** in this product.

## 14. Known problems and open decisions

**Known problems**
- The query "binders" finds nothing in the full setup although text search alone finds it. Not diagnosed (a required filter from query understanding, or a strict rerank).
- Two wrong grades in the evaluation set: a hand dryer accepted as "coffee machine", a cellphone accepted as "calculator". They need human overrides.
- The reranker is strict on need-based queries and sometimes drops one right answer.
- Describers pad the settings list (for example "study" and "reception" on many office assets).
- "Real front" is unreliable. For Synty vehicles the rendered "front" view shows the rear.
- 165 animation clips and 8 part files have nothing to render and no record yet. The plan was name-only records.
- Likely queries are not checked. The plan was to keep only those that actually retrieve their own asset.
- The batch API (half price) is not implemented for describe.
- Image embedding of the whole catalogue takes 5 to 6.5 hours on the owner's 4 GB laptop GPU.
- Large parts of the owner's library are not converted to GLB and so are absent: among others both "Simple Interiors" packs (house and apocalypse), which the owner named as an early target, and most of Dark Fantasy, Fantasy Kingdom, Horror Mansion, Kids and Shops.

**Decisions waiting for the owner**
1. Approve or change the vocabulary.
2. Which describer for the full run (Sonnet is the current default; Opus made a third of Sonnet's factual errors in the pilot).
3. Keep image embeddings (four views, one view, a smaller model) or drop them.
4. Who the product is for: only the owner's group searching the owner's catalogue, or anyone cataloguing their own assets. This decides how much of the build side must be generalised.

**To confirm**
- "Index and describe already included" was understood as: the product ships with records and index prebuilt. If it instead meant that the pipeline stages are already part of the code, section 2 changes.
- Whether the shipped catalogue includes pictures (the explorer is useless without them) and vectors (search quality depends on them).
- **Licence**: whether renders and descriptions of Synty assets may be given to other people at all. Not checked. If not, every self-hoster must build from assets they own, and "ships with a catalogue" only applies to the owner's private deployment.
