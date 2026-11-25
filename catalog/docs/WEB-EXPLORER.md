# Asset Catalogue: the web explorer

Written 2026-11-25. Companion to PROJECT.md, which explains the project, the record, the vocabulary and how search works. Read that first.

This document says **what the explorer must do**, not how to build it. The existing prototype page is discarded completely: do not reuse its code, layout or styling, and do not treat its behaviour as a specification. Where this document mentions the prototype, it is only to record a lesson.

Decided by the owner: a full modern web application (Next.js or Vite, Tailwind, shadcn or similar), production-grade, with a top-tier interface. Everything else about the stack is open.

## 1. Purpose

Let a person find the right 3D asset quickly, understand what it is and whether it fits, and take away what they need to use it. It is the human face of the same search that AI agents reach through MCP.

It is an **explorer**, not an editor: it reads the catalogue. Changing records is out of scope for the first version (section 9).

## 2. Who uses it

- **The level builder**: has a scene in mind ("a break room", "a checkpoint") and wants things that belong there. Thinks in places and needs, not asset names. Browses a lot, compares, collects several assets.
- **The developer or pipeline author**: wants one specific thing and its exact id, file path and measurements to put into code or config. Values speed, keyboard use and copyable facts.
- **The catalogue maintainer** (today the owner): checks whether search and the records are good. Wants to see why a result appeared, what each search stage contributed, what the describer was unsure about, and what a search cost.
- **A self-hoster's teammates**: arrive without context. The first screen must make sense with no explanation.

## 3. What the explorer can rely on

The service behind it offers these operations (PROJECT.md section 8):

- **Search**: query text plus optional filters. Returns ranked results, each with a record, a grade 0 to 3 when rerank ran, short notes on why it matched, and the ids of variants folded into it. Also returns how the query was understood (search text, required constraints, suggested facets), whether it abstained, how many assets passed the required constraints, notes about anything that was skipped, and time per stage. Progress can be reported as each stage starts.
- **Browse**: all assets passing a set of filters, one entry per variant group, in pages.
- **Facet counts**: for the current filters, how many assets each value of each facet would give.
- **Similar**: assets that look and read like a given one, never the asset's own variants.
- **Asset detail**: the full record and its variants.
- **Pictures**: four views per asset (three-quarter, front, top, back, 768 px) and a contact sheet (1536 px). Each view has a scale bar; the front view includes a 1.8 m human figure.
- **Vocabulary**: a definition for every facet term, category and size class.
- **Status**: how many assets, which describer, which search stages are available, whether an API key is present.

Filters available: kind, category (a branch or a leaf of the tree), material, setting, placement, function, style, condition, colour, pack, size class; minimum and maximum width, height, depth and longest side in metres; rigged yes/no. Values inside one filter are alternatives; different filters must all hold.

Things that are true of the data and shape the design:
- **Scale**: 160 assets today, about 4,200 after the full run, more if other packs are added. Design for tens of thousands.
- **Variants are common**: 4,195 models are about 2,340 distinct objects.
- **A search with both Claude steps takes 2 to 5 seconds** and costs the host about 1 to 2 cents. Without them it takes about half a second and costs nothing.
- **Search may deliberately return nothing.**
- **Records can be wrong.** The describer lists its own doubts per asset.
- **Low-poly art**: assets are small flat-shaded models; pictures are clean renders on a plain background.

## 4. Main flows

### 4.1 Find a known thing
The person types a name ("water cooler") and expects it first. They confirm from the picture, open it, copy its id or path.
- The top result must be visually obvious; a grade-3 result should read as "this is it".
- Id and file path are one action away from the result, without opening the detail.

### 4.2 Describe a need
The person types what they need, not what it is called: "something to hide behind at crouch height", "props for a messy office desk".
- Results are several different objects. The person scans pictures and reads one line per result to see *why* each was offered.
- The explorer shows how the query was understood, in plain words, so a wrong reading is visible at once.

### 4.3 Correct the reading of a query
Query understanding turns "wooden chair under 1 m" into required constraints (material wood, height under 1 m) and suggested facets. The person sees these, removes or changes one, and the search reruns with the corrected reading without asking the model again.
- Required constraints and suggestions must be clearly different things: one removes results, the other only reorders.
- A constraint the person removed must not silently come back on the next keystroke.

### 4.4 Browse without a query
The person has no words yet. They pick a pack, a kind or a place ("kitchen") and look around, narrowing step by step.
- Every filter value shows how many assets it would give before it is chosen, and values that would give nothing are recognisable.
- The category tree can be walked branch by branch.
- Size can be narrowed by class and by exact metres.
- Any vocabulary term can be explained in place (what exactly does "low-cover" mean?).

### 4.5 Combine query and filters
A query with filters applied by hand. Filters set by hand are the person's and stay until they remove them; constraints found in the text belong to the query and change with it. The person can always tell which is which.

### 4.6 Inspect one asset
From any result, the person opens the asset and sees:
- all four views, large, with a way to see fine detail;
- what it is and how to use it (plain name, description, usage, aliases);
- every facet value, each usable as a starting point for a new search ("show everything with this setting");
- measurements with units, size class, pivot, flat sides, triangle count, rig and animations;
- colours;
- identity: id, pack, file path, describer;
- the describer's doubts, clearly marked as uncertainty and not hidden;
- its variants, side by side, with what differs.
Opening and closing an asset keeps the person's place in the results. Moving to the next or previous result works without going back to the list.

### 4.7 Variants
A result that stands for several variants says so and lets the person see all of them, pick the one they want, and compare them. The count shown must be the true number of variants. (Lesson: the prototype counted only variants that matched the query, which misled.)

### 4.8 More like this
From any asset: "similar". The result is a new set the person can filter and keep exploring from, and they can get back to where they were.

### 4.9 Collect and take away
The level builder gathers several assets for one scene.
- Mark assets into a selection that survives navigation and reloads.
- See the selection, remove items, and export it in a form useful to a person and to a program (at least ids and paths; the full records as an option).
- Copy a single id, path or record at any time.
Whether selections are saved on the server and shared between people is undecided (section 10).

### 4.10 Share and return
Any state worth showing someone (a query with its filters, an open asset, a similar-to view) has a link that restores it. Back and forward in the browser do what the person expects. Recent searches are available again without retyping.

### 4.11 Nothing found
Three different situations that must look different and each say what to do next:
- **The filters exclude everything**: no asset passes the required constraints. Offer to loosen the constraint that causes it.
- **Search abstained**: candidates existed but none was judged good enough. Say so plainly, and offer to show the rejected candidates anyway.
- **The catalogue does not have it**: the same as abstaining from the person's view; the offer to look at near misses covers it.

### 4.12 Working without an API key
With no key, or when the model calls fail or a spending limit is reached, search still works on the local retrievers. The explorer says once, clearly and without alarm, that results are not graded and queries are not interpreted, and what the host can do about it. It does not repeat this on every search.

### 4.13 See why (maintainer)
For any search the maintainer can see:
- time spent in each stage and in total;
- which retrievers found each result and the short reason;
- the grade each result received;
- the candidates that were dropped;
- the effect of switching a stage off and on (each retriever, query understanding, rerank), without leaving the page.
This is a diagnostic layer. It must not clutter the view of the other three kinds of user, and it must be easy to reach for the maintainer.

### 4.14 First visit
An empty search box alone is a poor start. The first screen shows what the catalogue contains (how many assets, which packs, what kinds of thing), invites both typing and browsing, and offers a few example queries of the different kinds (a name, a need, a constraint).

## 5. States every view must handle

- **Loading**: a search has distinct stages; show real progress through them, and show retrieved candidates as soon as they are meaningful if the service makes that possible, not a blank wait for 5 seconds.
- **Superseded**: a new query while one is running replaces it cleanly; a late answer to the old query never overwrites the new one.
- **Partial**: a stage was skipped; results are shown with a note.
- **Empty**: the three cases of 4.11.
- **Error**: the service is unreachable, the request was refused, the session expired. Each says what happened and what to do, in words for a person.
- **Missing picture**: a record without a readable image still shows as a usable result.
- **Stale**: the catalogue was rebuilt while the page was open; an asset that no longer exists is explained, not shown as a broken page.
- **Long content**: 30 aliases, a 200-character name, a category six levels deep, 40 variants.

## 6. Quality bar

- **Looks and feels like a finished product** of the quality of the best current developer and design tools: considered typography and spacing, a coherent visual system, purposeful motion, no default-component look.
- **Pictures first.** People choose assets by looking. The pictures are the content; the interface frames them.
- **Fast to the eye**: typing, filtering, opening and closing an asset respond instantly; the wait for model-backed stages is the only wait, and it is explained.
- **Smooth at scale**: scrolling and filtering stay smooth with tens of thousands of assets.
- **Keyboard complete**: every flow in section 4 can be done without a mouse, with visible focus and discoverable shortcuts.
- **Accessible**: WCAG 2.2 AA; works with a screen reader; grades and states are never carried by colour alone; respects reduced motion.
- **Light and dark** themes, both designed, neither an afterthought.
- **Responsive** from a laptop to a large monitor. A phone layout is not required for the first version but nothing should break on a tablet.
- **English only.**
- **No requests to third parties**: fonts, icons, scripts and analytics all come from the host. A self-hosted instance may hold private assets.
- **Honest about cost**: the person can tell when an action will spend the host's API budget, and the host can see what was spent.
- **Tested**: the flows in section 4 are covered by automated tests, and the interface is reviewed visually by a person before it is called done. (Lesson: the prototype was never looked at by anyone before delivery.)

## 7. What the service still lacks for this explorer

The explorer cannot reach the bar above on the prototype API. These are needs, to be designed by the next session:

1. **Small pictures.** Only 768 px PNG views exist. A grid of hundreds of results needs light thumbnails in modern formats and at several sizes.
2. **Login and sessions** for any instance not on localhost, and a per-user or per-instance spending limit.
3. **Cancelling** a search that was superseded, so abandoned searches stop spending money. (The prototype let them run to the end.)
4. **Remembering answers** for repeated identical searches, so going back does not pay or wait again.
5. **Early results**: candidates before rerank has finished.
6. **Dropped candidates** in the response, for 4.11 and 4.13.
7. **Sorting** in browse (by name, size, pack, triangle count). Today it is id order only.
8. **Paging of search results** beyond the first set.
9. **Facet counts for a search**, not only for browse.
10. **Access to the model file**, at least a download, for hosts allowed to offer it (section 10).
11. **Reloading a rebuilt catalogue** without restarting the server.
12. **Usage figures**: calls and estimated spend per period.
13. **A versioned, documented contract**, since the explorer and the service will now be developed separately.

## 8. Candidate features, not yet decided

Worth considering because they would change how good the product feels; none is promised.

- **Interactive 3D preview** of the model in the asset view (rotate, zoom, wireframe, scale reference). The strongest single addition for judging an asset. Needs the model files to be served, which touches the licence question.
- **Compare view**: two to four assets side by side with their measurements aligned.
- **Search by picture**: drop an image, get similar assets. The image vectors already exist.
- **Saved searches and named collections**, shared between users of one instance.
- **Flag a wrong record**: the person marks a record or a search result as wrong; the maintainer gets a review queue. This is the first step towards editing and would feed the evaluation set.
- **Evaluation dashboard** for the maintainer: the scores from PROJECT.md section 9 per configuration and per query, with the failing queries one click from the live search.
- **Size in context**: show the asset's footprint against a room or a person at true scale.

## 9. Out of scope for the first version

- Editing records or the vocabulary.
- Running the build pipeline from the browser.
- Uploading assets.
- Languages other than English.
- A phone-first layout.
- Public, anonymous access.

## 10. Open questions for the owner

1. **Who logs in?** One shared password, named users, or an external identity provider. This decides whether selections and history are personal.
2. **May the explorer serve the model files** (download, 3D preview), or only pictures and records?
3. **Are selections saved on the server and shared**, or kept in the person's browser?
4. **Should the maintainer's diagnostic layer be available to every user** or only to an admin?
5. **Is the default search the full one** (2 to 5 seconds, paid) with a fast mode on request, or fast first with "grade these" on request? This is a product decision about cost and feel, and the evaluation numbers in PROJECT.md section 9 are the evidence to decide it with.
6. **Which of section 8 belongs in the first version?**

## 11. Pointers

- Project description: `PROJECT.md` in this folder
- Catalogue data to develop against: `~/coding/catalog/` (160 fully described office assets; pictures and measurements for all 4,506)
- Service code showing the available operations: `catalog/src/asset_catalog/service.py` in `/home/jjakubec/coding/drycastle-rc-0.14`, commit `e09f88d4`
- To run the current service for reference: `uv run catalog serve` in that `catalog/` folder (answers on `http://127.0.0.1:8765`, API description at `/api/docs`). It reads the game repository's data folder, not this copy.
- Architecture diagram: the owner's excalibruh tool, diagram id `asset-catalog`
