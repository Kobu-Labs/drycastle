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
