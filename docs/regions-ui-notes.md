# Region tool: UI notes (issue #62)

Standard: `docs/standards/ui-ux.md`. Code: `godot/viewer/EditorMode.Regions.cs` (tool, sidebar page, confirm dialog, Ctrl+P palette),
rail icon in `IconGlyph.cs` ("region"), theme variations `Danger` and `DangerButton` in `EditorTheme.cs`. Smoke test:
`make viewer-region-editor-smoke`.

## States and hints (hint bar text per state)
| State | How you get there | Esc does |
|---|---|---|
| Idle | open the tool (6) | leaves the tool |
| Drawing | N, or click empty ground with nothing selected | cancels, nothing was saved |
| Colouring | Enter (or click the first corner) with a valid shape | back to the corners |
| Selected | click a region or its list row | deselects |
| Dragging | press on a corner of the selected region | cancels the move |

Every completed gesture is one save and one undo step: create (draw + colour), recolour, move a corner, delete. The saved file reaches
the map through the existing watcher (`region-run`, Regions 2).

## Keyboard-only walkthrough (verified through key events in the smoke test unless marked)
1. `6` opens the tool; `Ctrl+P`, type "region", Enter runs "Region: new" (same as `N`).
2. Fly the camera (WASD, `T` top-down). `C` places a corner at the point under the cursor, or at the screen centre when the cursor is over a panel. Backspace or Ctrl+Z removes the last corner.
3. Enter closes the shape (refused with the reason in the panel when invalid). Tab to the colour button (Enter opens the picker) or type `#rrggbb` into the hex field (Enter applies). Enter creates the region.
4. `[` and `]` choose a corner, arrows move it by 0.5 m (each move is one undo step). Tab to a list row and Enter selects a region.
5. `Del` asks "Delete region R-0002?" (focus starts on Cancel, Esc cancels, Tab then Enter confirms). Ctrl+Z / Ctrl+Y undo and redo.
6. Esc steps out level by level. Walked: the API and key paths in the smoke test. Not walked by a human with the mouse unplugged.

## Screenshots (docs/screenshots/regions/)
`regions-selected-1258x1412.png`, `regions-drawing-1258x1412.png`, `regions-colouring-1258x1412.png`, taken with the real viewer
(`--editor-shot ... --shot-tool Region --shot-region R-0002` or `--shot-draft "x,y;..." [--shot-closed]`).

## Deviations from the standard
- **Screenshot sizes.** 1280x720 and 3840x2160 could not be produced: the window manager on this machine pins the window to 1258x1412
  (it ignored `--resolution` and `Window.Size`) and there is no Xvfb. The shots above are the real viewer at the size it got. Run
  `--shot-size 1280x720` / `3840x2160` (new option) on a machine whose window manager honours it.
- **UI scale 75-150 %** is not offered; the editor chrome uses the fixed theme sizes like the other tools (a gap of the whole editor, not of this tool).
- **Gamepad** is not supported anywhere in the editor yet.
- **Loading state**: none. Region data is read from the in-memory edits file, so there is nothing to wait for; saving shows the status line ("saved (...)"). Error states shown: map without `origin_epsg5514`, refused shapes (inline), refused saves ("not saved: ...").
- **Live colour preview** while the picker is open is not shown on the ground (the draped mesh comes from `region-run` after the save). The swatch, hex field and list show the colour.
- **No corner insert/delete.** Corners can be dragged, nudged, and the whole region deleted; adding or removing a corner of a saved region is not in this issue's scope (the MCP tools do not have it either).
- **Ctrl+P palette** did not exist; a small one is added here (tools, undo/redo, "Region: new", region IDs, parcel IDs once two characters are typed). Parcel addresses are not included.
- **Unfinished shapes are not kept** when you switch tools: they are discarded with a status message naming the loss (never saved, so nothing to undo).
- **Colour picker popup** is Godot's stock `ColorPickerButton` popup, styled by the shared theme only as far as the theme reaches it. Its change is saved when the popup closes (one undo step), not per drag.
- **Sounds, reduce-motion, colour-blind option**: not present anywhere in the editor yet; the tool adds no animation and never relies on colour alone (swatches come with hex text, invalid shapes come with text).
