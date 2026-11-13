# Ground types tool: UI notes

Standard: `docs/standards/ui-ux.md`. Code: `godot/viewer/EditorMode.GroundTypes.cs` (page, edits, keys), `godot/viewer/GroundTypeEdits.cs`
(model: attribute catalog, merge, validation, the table of detail settings), `godot/viewer/EditorMode.Regions.cs` (the region's ground stack),
`godot/viewer/EditorMode.Layers.cs` (the attribute layer overlay), rail icon `ground` in `IconGlyph.cs`, theme variations `BannerPanel` and
`ReadOnlyBox` in `EditorTheme.cs`. Storage, merge rule and attribute catalog: `docs/pipeline.md` "Ground attributes and stacks", "Ground types of
your own"; decision: ADR 0015. MCP: `set_ground_type`, `reset_ground_type`, `delete_ground_type`, `set_region_ground`. Not run and not
smoke-tested: see "Not verified".

## States (the hint bar says each)
| State | How you get there | Esc does |
|---|---|---|
| Nothing selected | open the tool (4) | leaves the tool |
| Selected | click a row of the list, or Enter on a focused row, or `Ctrl+P` "<name> ground type" | deselects |
| Typing in a field (name, hex colour, OSM values, number) | click it, `N` for the name field | leaves the field, typed text is dropped |
| Delete confirm | `Del` or the Delete button | cancels (focus starts on Cancel) |

Empty and error states: a map whose `parcels.json` has no `ground_types` shows a red explanation to build it again (`make map`) and
the list says "No ground types yet" (you can still create types: they are saved in `edits.json` and used by that build); a map built
before types were editable lists name and colour only and says so; a map built before attributes (ADR 0015) has its rules entries read in the
old flat shape and shown under the attributes (the editor writes the new shape); a typed name that is invalid or taken says why under the field and
disables Create and Create layer; a deleted type is shown with a "deleted" tag, its form is disabled and Reset to rules brings it back; a refused save
shows "not saved: ..." in the status line (the file validator's reason); Delete is disabled for the default type and the dike, with
the reason as tooltip and note; a type that carries nothing says so under its title, a layer type (no appearance or water) says that stacked over
any ground it changes nothing about how the ground looks. A type that takes OSM land or is made by a rule cannot untick its last
appearance or water attribute: that checkbox is disabled with the reason as tooltip (and the dike's recipe is fixed).

The form: Appearance (Terrain colours checkbox, colour rows; Detail recipe checkbox, recipe dropdown, detail settings), Spawning, Water,
Events (one checkbox per event), Source (OSM). Each group says in one line how it stacks. A checkbox ticks an attribute the type carries; unticking
an attribute of `rules.yaml` is saved as `null` for it, ticking it again drops that override.

Every change is one `Mutate` = one save = one undo step: a colour change (committed when the picker closes or Enter in the hex
field), add or remove a colour, a checkbox, the recipe, an event, OSM values (Enter or leaving the field), a detail number, Create, Create layer,
Duplicate, Reset, Delete; on the Regions page: adding a type to the ground stack, Up, Down, Remove. A value set back to what `rules.yaml` says drops the
override. A banner on the page says the change shows after the next full map build (`make map`).

## Keyboard-only walkthrough (what the intended path is; not walked, see below)
1. `4` opens the tool. Tab (with nothing focused) focuses the list; Up/Down move between rows, Enter selects one.
2. Tab on: Terrain colours checkbox (Space toggles), colour swatch (Enter opens the picker), hex field (type `#rrggbb`, Enter applies), Remove,
   Add a colour, Detail recipe checkbox, Recipe dropdown, detail number fields, Players may spawn here, Water body, Bigfoot (Space toggles each),
   landuse and natural fields (Enter applies), Reset to rules, Delete.
3. `N` focuses the New type name; type a name, Enter creates it (or Tab to Create, Create layer or Duplicate selected).
4. `Del` asks "Delete ground type forest?", Esc cancels, Tab then Enter confirms. `Ctrl+Z` / `Ctrl+Y` undo and redo.
5. Esc steps out: field, selection, tool. `Ctrl+P` and the type name jumps to it.
6. `6` opens Regions, select a region, Tab to the ground stack: Add dropdown (Enter opens, arrows choose), each row's Up, Down and Remove buttons.
   `V` shows or hides the attribute layers from anywhere in the editor (not while a text field has the focus).

## Not verified (the editor was not run for this work)
- Keyboard-only walkthrough above, every state, focus order, hover/focus/pressed/disabled states of the new controls (the attribute
  checkboxes, the ground stack rows, the layers switch).
- Screenshots at 1280x720 and 3840x2160 (a list of ten types plus the form needs the sidebar to scroll at 720p; the form scrolls into view on select;
  the form is longer now with four attribute groups).
- That the colour picker's PopupClosed and a SpinBox's ValueChanged each give exactly one save (written to, but not exercised).
- That Tab with nothing focused reaches the list (handled in `HandleGroundKey` because Godot may not move focus from nothing).
- That a two-way round trip (editor writes, Python validates and the Python writer reproduces the bytes) holds; the C# canonical writer
  skips an empty `ground_types` like Python and writes `null` for an attribute removed from a type of the rules.
- The attribute layer overlay: that the mesh drapes on the terrain, shows through the aerial photo layer and the region paint in the right order,
  and is rebuilt after each edit (written to, built with `dotnet build`, never seen). Its look (a third opaque, a thin edge) is a guess.
- `make map` was run for the village map before and after the migration and the output compared (byte-identical terrain, detail and water,
  see ADR 0015 and the issue); the editor side was not.
- The editor's validator for regions (`ObjectEdits.CheckRegion`) did not know the `ground` key before this work, so a region with a ground type
  could not be saved from the editor; it now mirrors the Python rule (a non-empty list of unique names, not with `water_y`).

## Deviations
- No loading state: types come from the loaded map and the edits file in memory.
- Rename is a disabled button with a tooltip (names are IDs); to rename, Duplicate and Delete.
- Detail settings: only furrow probability, headland keep and tone patches per ha are fields (`GroundTypeEdits.DetailFields`); the
  rest of `detail` is shown read-only, and editable through `rules.yaml` or the `detail` attribute of the MCP tool `set_ground_type`.
- The editor lays `edits.json` over the rules entries of the last build (`parcels.json` `ground_types[].rules`), so a `rules.yaml` change
  appears only after a build; trees are no longer part of a ground type.
- The ground stack has Up / Down buttons, not drag and drop (keyboard first, like the rest of the editor).
- The overlay has one switch, not one per attribute group; every layer region is drawn in its own colour.
- UI scale 75-150 % and gamepad: not offered, like the rest of the editor.
