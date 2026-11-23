# Ground patch and Zones tools: UI notes (issue #275, ADR 0022)

Standard: `docs/standards/ui-ux.md`. Code: `godot/viewer/Editor/EditorMode.Shapes.cs` (the polygon machinery both tools share: drawing, closing,
corners, delete, hints, the draw/state/list widgets), `EditorMode.Patches.cs` (patch type, water level, form, list), `EditorMode.Zones.cs`
(zone kinds, form, list, the zone overlay), `EditorMode.GroundMap.cs` (the ground map and the area meshes the zone overlay reuses),
`EditorMode.Dialogs.cs` (confirm dialog, Ctrl+P palette), rail icons in `IconGlyph.cs` ("ground", "zone"), theme variations `Danger` and
`DangerButton` in `EditorTheme.cs`. Smoke tests: `make viewer-shape-editor-smoke` (gestures), `make viewer-patches-smoke` (patch hot reload).
These replace the Regions tool, the ground stack and the attribute-layers overlay (`V`), which are gone.

## States and hints (hint bar text per state)
Both tools: the same states. A shape is a patch in the Ground tool (key 4) and a zone in the Zones tool (key 6).

| State | How you get there | Esc does |
|---|---|---|
| Idle | open the tool | steps back (see below) |
| Drawing | `N` or the Draw button (needs a selected type or kind) | cancels, nothing was saved |
| Level (patch of a water type only) | Enter with a valid shape | back to the corners |
| Selected | click a patch or zone, or its list row | deselects it |
| Dragging | press on a corner of the selected shape | cancels the move |

Every completed gesture is one save and one undo step: create (draw + Enter), retype or change kind, water level, move a corner, delete. A
patch reaches the map through the existing watcher (`patch-run`); a zone has no paint, so nothing is regenerated.

Esc goes one level up. Ground: drag, water level, drawing, the selected patch (when the patch was the last thing selected), the selected
type, the tool. Zones: drag, drawing, the selected zone, the selected kind, the tool. A text field is left first (typed text is dropped).

## Empty, loading and error states
- **Empty**: no patches ("No patches yet. Select a ground type, press N, ..."), no zones ("No zones to list. Pick a kind, ..."), no
  selection ("No patch selected", "Click a patch in the list or on the ground to select it").
- **Error / refusal, with the way out**: Draw disabled with the reason as tooltip and on `N` ("pick a ground type in the list first", "this
  map lists no zone kinds: build it again (make map), then reopen the editor", "finish or cancel the shape you are drawing first (Esc)", a
  deleted type or a rule's type); a map built before `zone_kinds` shows a red note in the Zones page; a map without `origin_epsg5514` shows a
  red note and disables Draw; invalid shapes are refused inline before anything is saved (needs 3 corners, crossing, no area) and a drag into
  a crossing shows a red outline and is refused; refused saves show "not saved: ..." with the validator's reason; a type that a patch uses
  cannot be deleted or have Water toggled (the reason names the patches).
- **Loading**: the ground map and the zone overlay are built one type or kind per frame and say "Drawing zones 3/12..." / "Drawing ground
  areas 3/12..." under their switch; a map built before `ground_areas` says to build it again. Saving is instant (status line), so there is
  nothing else to wait for.

## Keyboard-only walkthrough (the intended path; the API and key paths are in the smoke test, a human has not walked it with the mouse unplugged)
1. `4` opens Ground (`6` Zones); `Ctrl+P` and a type, kind, `GP-0001`, `Z-0001`, "draw a patch" or "draw a zone" jump there or start it.
2. Tab (with nothing focused) focuses the list; Up/Down/Tab move between rows, Enter selects a type or kind.
3. `N` starts a shape (refused with the reason when no type or kind is selected). Fly the camera (WASD, `T` top-down). `C` places a corner at the
   point under the cursor, or at the screen centre when the cursor is over a panel. Backspace or Ctrl+Z removes the last corner.
4. Enter closes and creates a zone or a land patch (refused with the reason in the panel when invalid). A water patch asks for its water level
   first: type it (the number field is in the tab order), Enter creates, Esc goes back to the corners.
5. `[` and `]` choose a corner of the selected shape, arrows move it by 0.5 m (each move is one undo step). Tab to a list row and Enter selects a
   shape. Tab to the Type or Kind dropdown (Enter opens, arrows choose) to change it.
6. `Del` asks "Delete patch GP-0002?" (focus starts on Cancel, Esc cancels, Tab then Enter confirms). Ctrl+Z / Ctrl+Y undo and redo.
7. `M` shows or hides the tool's map overlay. Esc steps out level by level.

## Controls and their states
Every control has normal, hover, focus, pressed and disabled looks from the shared theme (`EditorTheme`): list rows and kind rows are toggle
buttons (selected = pressed); the Draw, Finish, Create, Back, Cancel and Delete buttons are `FocusBtn`s (focusable, release focus after a press so
Enter and Space go back to the viewport); disabled controls carry the reason as tooltip. Tooltips name the shortcut (Draw patch of forest (N),
Delete GP-0002 (Del), Show areas on the map (M)). Delete is `DangerButton` and always confirms, naming the shape, its corners and area, and
that Ctrl+Z brings it back.

## Settings persisted (`user://editor_ui.cfg`)
Open tool, selected ground type, selected patch, selected zone, selected zone kind, ground map on/off (`ground_map_on`), zone overlay on/off
(`zone_overlay_on`), fly speed, aerial photo, floor plan. The old `layers_on`, `region` and `region_colour` keys are ignored.

## Screenshots
Not taken. The owner forbids running the game or taking screenshots unless asked, so the 1280x720 and 3840x2160 shots of the definition of
done are a named deviation. The shot hooks exist for whoever runs them: `--editor-shot FILE --shot-patch GP-0001`, `--shot-zone Z-0001`, or
`--shot-draft "x,y;..." [--shot-zones --shot-kind bigfoot | --shot-type forest]`.

## Deviations from the standard
- **Screenshots** at 1280x720 and 3840x2160: not taken (above). The sidebar of the Ground tool (map switch, types, patches, New type, the form of a
  type) is long and scrolls; the form of a selected type or patch scrolls into view on select.
- **Keyboard-only walkthrough and every state/focus/hover look**: written from the code and covered by the smoke test's API and key events, not walked
  by a human; nothing here was run.
- **Smoke tests were not run** (they run the game); they were only kept consistent and compiled with `dotnet build`.
- **UI scale 75-150 %** is not offered; the editor chrome uses the fixed theme sizes like the other tools (a gap of the whole editor).
- **No live colour preview**: a patch's paint comes from `patch-run` after the save; the ground map shows the type's colour at once.
- **Retyping a patch to a water type** gives it a starting water level (the terrain height under its middle) instead of asking in a dialog: the
  level row is right below, and the change is one undo step. Drawing a new water patch does ask (the Level step).
- **A rule's type (the dike) cannot be drawn as a patch**; the editor refuses it with the reason (the contract allows it, there is no use for it).
- **No corner insert/delete**: corners can be dragged and nudged and the whole shape deleted (the MCP tools do not have it either).
- **Unfinished shapes are not kept** when you switch tools: they are discarded with a status message naming the loss (never saved, nothing to undo).
- **Del and Esc follow the last thing selected** in the Ground tool (the patch or the type), because both can be selected at once (selecting a patch
  selects its type). The hint bar says which.
- **Zone overlay key**: `M`, the same key and name as the ground map, since only one of the two tools is open at a time (documented here and in the
  F1 sheet and the Zones page).
- **Sounds, reduce-motion, colour-blind option**: not present anywhere in the editor yet; the tools add no animation and never rely on colour alone
  (swatches come with the label or name, invalid shapes come with text, rule zones are named "rule" in the hover and the list).
- Unverified: the overlay on the real map (12 m triangles float 1 m over the terrain, draw order of the ground map, rule zones, drawn zones and the
  aerial photo), hover labels and click-to-select, the focus order of the new controls, that each SpinBox step is one save, the watcher's `patch-run`
  round trip for water patches.
