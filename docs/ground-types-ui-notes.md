# Ground types (Ground tool): UI notes

Standard: `docs/standards/ui-ux.md`. Code: `godot/viewer/Editor/Tools/GroundTool.cs` (selection, keys, hover), `GroundTypesPanel.cs` (page), `GroundTypeOps.cs` (edits), `GroundTypeRows.cs` (row words), `godot/viewer/Edits/GroundTypeEdits.cs`
(model: attribute catalog, merge, validation, the table of detail settings), `godot/viewer/Editor/Tools/GroundMap.cs` (the ground map),
rail icon `ground` in `IconGlyph.cs`, theme variations `BannerPanel` and `ReadOnlyBox` in `EditorTheme.cs`. The Ground tool's patches are
in `docs/patches-zones-ui-notes.md`. Storage, merge rule and attribute catalog: `docs/pipeline.md` "Ground types"; decisions: ADR 0015,
ADR 0022 (a ground type is terrain only; spawning, events and wildlife are zone kinds, the ground stack and the attribute layers are gone).
MCP: `set_ground_type`, `reset_ground_type`, `delete_ground_type`. Not run and not smoke-tested: see "Not verified".

## States (the hint bar says each)
| State | How you get there | Esc does |
|---|---|---|
| Nothing selected | open the tool (4) | leaves the tool |
| Selected | click a row of the list, or Enter on a focused row, or click the ground, or `Ctrl+P` "<name> ground type" | deselects |
| Typing in a field (name, hex colour, OSM values, number) | click it, `Shift+N` for the name field | leaves the field, typed text is dropped |
| Delete confirm | `Del` or the Delete button | cancels (focus starts on Cancel) |

Empty and error states: a map whose `parcels.json` has no `ground_types` shows a red explanation to build it again (`make map`) and
the list says "No ground types yet" (you can still create types: they are saved in `edits.json` and used by that build); a map built
before types were editable lists name and colour only and says so; a typed name that is invalid or taken says why under the field and
disables Create and Duplicate; a deleted type is shown with a "deleted" tag, its form is disabled and Reset to rules brings it back; a refused save
shows "not saved: ..." in the status line (the file validator's reason); Delete is disabled for the default type, the dike and a type a patch uses,
with the reason as tooltip and note; Water cannot be ticked or unticked on a type a patch uses (the reason names the patches); a type that carries
nothing says so under its title (a type with only colours is plain paint). A type that takes OSM land or is made by a rule cannot untick its last
appearance or water attribute: that checkbox is disabled with the reason as tooltip (and the dike's recipe is fixed).

The form: Appearance (Terrain colours checkbox, colour rows; Detail recipe checkbox, recipe dropdown, detail settings), Water, Source (OSM). Each
group says in one line what it is. A checkbox ticks an attribute the type carries; unticking an attribute of `rules.yaml` is saved as `null` for
it, ticking it again drops that override.

Every change is one `Mutate` = one save = one undo step: a colour change (committed when the picker closes or Enter in the hex
field), add or remove a colour, a checkbox, the recipe, OSM values (Enter or leaving the field), a detail number, Create, Duplicate, Reset, Delete. A value
set back to what `rules.yaml` says drops the override. A banner on the page says the change shows after the next full map build (`make map`).

## Keyboard-only walkthrough (what the intended path is; not walked, see below)
1. `4` opens the tool. Tab (with nothing focused) focuses the list; Up/Down move between rows, Enter selects one.
2. Tab on: Terrain colours checkbox (Space toggles), colour swatch (Enter opens the picker), hex field (type `#rrggbb`, Enter applies), Remove,
   Add a colour, Detail recipe checkbox, Recipe dropdown, detail number fields, Water body (Space toggles), landuse and natural fields (Enter applies),
   Reset to rules, Delete.
3. `Shift+N` focuses the New type name; type a name, Enter creates it (or Tab to Create or Duplicate selected). `N` is for drawing a patch.
4. `Del` asks "Delete ground type forest?", Esc cancels, Tab then Enter confirms. `Ctrl+Z` / `Ctrl+Y` undo and redo.
5. Esc steps out: field, patch, selection, tool. `Ctrl+P` and the type name jumps to it. `M` shows or hides the ground map.

## Not verified (the editor was not run for this work)
- Keyboard-only walkthrough above, every state, focus order, hover/focus/pressed/disabled states of the controls.
- Screenshots at 1280x720 and 3840x2160 (a list of ten types, the patches and the form need the sidebar to scroll at 720p; the form scrolls into
  view on select). Not taken: the owner forbids running the game or taking screenshots unless asked.
- That the colour picker's PopupClosed and a SpinBox's ValueChanged each give exactly one save (written to, but not exercised).
- That Tab with nothing focused reaches the list (handled in `GroundTool.HandleKey` because Godot may not move focus from nothing).
- That a two-way round trip (editor writes, Python validates and the Python writer reproduces the bytes) holds; the C# canonical writer
  skips an empty `ground_types` like Python and writes `null` for an attribute removed from a type of the rules.
- The editor's validator (`ObjectEdits.CheckPatch`, `GroundTypeEdits.Validate`) mirrors the Python rules of ADR 0022 (terrain-only attributes,
  `ground_patches`, `zones`, `regions` unknown); it was written against the contract, not compared with the finished `edits/schema.py`.
- Ground map (`Tools/GroundMap.cs`): not run, no screenshots. Unverified: the overlay on the real map (12 m triangles float 1 m over the
  terrain), the slit cut of polygons with holes (the dike), the one-type-per-frame build, hover label and click-to-select, `M`, focus order of the
  checkbox. Needs `make map` first: the exported `parcels.json` of an older build has no `ground_areas` (the page then says to build it again).

## Deviations
- No loading state for the types: they come from the loaded map and the edits file in memory (the ground map says "Drawing ground areas 3/12...").
- Rename is a disabled button with a tooltip (names are IDs); to rename, Duplicate and Delete.
- Detail settings: only furrow probability, headland keep and tone patches per ha are fields (`GroundTypeEdits.DetailFields`); the
  rest of `detail` is shown read-only, and editable through `rules.yaml` or the `detail` attribute of the MCP tool `set_ground_type`.
- The editor lays `edits.json` over the rules entries of the last build (`parcels.json` `ground_types[].rules`), so a `rules.yaml` change
  appears only after a build; trees are no longer part of a ground type.
- UI scale 75-150 % and gamepad: not offered, like the rest of the editor.
