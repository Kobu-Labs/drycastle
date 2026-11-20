# UI/UX standard

Owner's rule: *"This game follows AAA standards; UI and menus come polished from the get-go. Menus must be polished, even for the map editor, this is non-negotiable: predictable, good UX flow with sensible controls."* It covers every screen: game menus, HUD, viewer, map editor, dev tools. No "it's only a dev tool" exception.

## 1. Flow
- Every screen has a known entry and exit; no dead ends; every modal has Cancel.
- **Esc/Back goes one level up.** In game or editor it opens Pause. It never quits without confirmation.
- Target flow: Boot → Main menu → (Play → server list → Loading → Game ⇄ Pause) | (Map editor → Loading → Editor ⇄ Pause) | Settings | Quit. Main and pause menus and settings exist from the first playable build.
- Same action, same name, same place everywhere.
- Destructive actions confirm and name what is lost; reversible ones offer undo.
- Last server, settings, window size and editor selection persist across restarts.

## 2. Input
- Keyboard + mouse and gamepad navigation on every menu, with a visible focus and a sensible first focus.
- Rebindable controls in one input map, with conflict detection and reset. Genre-conventional defaults (WASD, Space, Shift, E, Tab, F1).
- On-screen control hints that follow the active device.
- Mouse capture is explicit and visible; menus and editor panels release it.
- Sensitivity, invert-Y, FOV are settings.

## 3. Feedback
- Every control has normal, hover, focus, pressed, disabled states.
- Over ~100 ms show progress; over ~1 s a labelled progress indicator. The UI thread never blocks.
- Transitions 100-250 ms, consistent, skippable.
- One toast/banner pattern. Errors say what happened and what to do next.
- UI sounds (click, hover, accept, back, error) on their own audio bus.

## 4. Visual
- One Godot Theme with tokens (colour, size, spacing, radius); no hard-coded values in screens. 8 px grid, ≤5 text sizes, one icon set.
- Works from 1280×720 to 3840×2160, 16:9 to 21:9; UI scale setting 75-150 %.
- Body text ≥16 px at 1080p; contrast ≥4.5:1 text, ≥3:1 icons. Never colour-only; colour-blind-safe option; reduce-motion option.

### Collection logs
- A collection log (Hunter's Log, Mineral Log) is the one place the UI is dressed in a material instead of the flat HUD surfaces: an opaque board with a skin of its own (lodge: planks, brass, parchment, antlers; stone: masonry, iron, slate, crystals). This is the named exception to "UI art matches the flat low-poly map"; the art stays flat-shaded (a few tones, no photo textures).
- Every log is built on `LogFrame` with a `LogSkin`: the skin's tokens and its Theme live in `LogSkin.cs`, the screen's own code has no colours. A new log adds a skin, not a frame.
- Text sits only on a plate, a paper or the plain board colour, with the contrast of section 4. Rarity, "found", "yours" and "selected" are always a shape or words as well as a colour (a medal and the tier's name, a tick, "(you)", a thicker frame).
- The states of section 3 hold on a skin too: a plaque lightens on hover, lightens more with a thicker rim when pressed, and gets a ring when focused.

### Owner's UI rules (from the Hunter's Log review, #387)
Checklist for any new UI. Each line is something the owner had to ask for.

**Layout**
- One fact per line, never `a | b | c` on one wrapping line. Every card of a kind has the same lines in the same order; a missing value shows a placeholder, never drops the line (stats then align across cards).
- Centre what is reasonable: card/tile grids use `FlowContainer.Alignment = Center`; legends and summaries centre under what they describe.
- Nothing is ever cropped (headers, labels, cells) from 1280×720 up and at 150 % scale. Columns get minimum-width tokens (`LogColMin`) shared by header and cells; when too narrow, drop the least important column or stack the detail panel, never cut text. Test the narrow case first.
- A headline number (a record's kg or m) is a badge floating over its picture's corner, out of the layout, with a strip reserved under the picture so it covers little; never a text line whose length can shift the rows below.
- Swapping a child of a row (e.g. a link for a label): remove the old one now (`RemoveChild`), or build the row with the final control; `QueueFree` leaves it in layout until the frame ends and the new one gets no room.
- Detail panels carry their own labels as icons plus value; check that no row can be empty or clipped (a button with no text is a bug).

**Icons**
- Icon + text for every attribute (person = hunter, rifle = weapon, crosshair = hit area, tape = distance, calendar = when). Same attribute, same icon, on every screen.
- One icon set, flat single-tone, tinted by the skin's ink. Never mix detailed or coloured art next to flat glyphs.
- Draw a new icon as a preview PNG and show it before wiring it in; expect 1-3 rounds ("looks nothing like a sniper rifle"). Keep the silhouette recognisable at 20 px: long barrel, scope, no bipod/clutter; for a ruler just the strip.

**Links and clickables**
- A link looks like one: icon, always underlined, filled+bordered hover, hand cursor, tooltip with the destination ("X's log").
- The click target is the text itself, never its column or row width. A click beside it must not navigate.

**Naming and content**
- One word per concept everywhere: column, filter, filter option, card all say "Hunter", not "Player" in one place. Fix the name in every string when one changes ("Game" became "Regular").
- Remove a control that duplicates another path (weapon filter; "All my hunts" button when the Hunter column/links reach it). Remove section titles that only repeat their sections.
- A removed UI element leaves no dead code, strings or doc lines.

**Process**
- The user judges in the running game: seed realistic data across every state (all rarity tiers, empty, hidden) so the screen can be seen, and say that a server restart may be needed.
- Record findings here and in the feature's doc as you go; do not wait for the end.

## 5. Settings
- Groups: Graphics / Audio / Controls / Gameplay / Accessibility, one config file, live preview, Apply/Revert, auto-revert for risky video changes.
- Every list, table and form has designed loading, empty and error states.

## 6. HUD
- Crosshair, health, ammo, hotbar (selected slot obvious), kill feed, scoreboard (hold Tab), timer and score, damage indicators, respawn screen.
- No layout shift when values change; nothing allocates or rebuilds per frame; UI under 1 ms per frame.

## 7. Map editor
- Mode indicator (fly/select/place/edit) and a control-hint bar; panels: tools, inspector, asset browser, status bar (selection ID, unsaved edits).
- Undo/redo for every edit, unsaved indicator, explicit save, confirm on discard.
- Selected/hovered parcel or property group highlighted in world and inspector; edits apply live.
- Placement shows a ghost with snapping; invalid placement shown before it happens.
- Tooltips with shortcuts, F1 cheat sheet, Ctrl+P palette (actions, parcel IDs, addresses).
- Right widget per field (dropdown, slider+number, colour picker), inline validation.
- Prompt/MCP window shows the pending change, result and undo.

## Definition of done (every UI issue)
- [ ] Esc/Back verified; no dead ends; destructive actions confirmed or undoable
- [ ] Keyboard-only walkthrough (gamepad once supported)
- [ ] All states, focus and hints present; loading/empty/error shown
- [ ] Theme tokens only
- [ ] Screenshots at 1280×720 and 3840×2160 (`--screenshot`)
- [ ] Settings persisted where applicable
- [ ] Deviations from this document named and justified in the PR

## Open
Gamepad from day one or later; English only, but strings use translation keys; UI art direction assumed to match the flat low-poly map.
