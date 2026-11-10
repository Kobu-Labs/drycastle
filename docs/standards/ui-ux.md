# UI/UX standard

Source: `mods/methodology.md`. The game follows AAA standards and its UI and menus ship polished from the first version. This applies to everything with a screen: the game menus, the HUD, the viewer, the **map editor** and developer tools. There is no "it's only a dev tool" exception.

This document defines what "AAA standard" means here, so it can be checked. Every issue that adds or changes UI is accepted against section 10.

## 1. Flow: predictable and never a dead end

1. Every screen is reached from a known place and leaves to a known place. The screen map (menu → mod/server pick → loading → game → pause → settings → quit) is kept in `docs/standards/screen-flow.md` and updated with any new screen.
2. **Esc / Back always goes one level up.** In game it opens the pause menu; in a menu it returns to the previous screen; in a text field or dialog it cancels. It never quits without confirmation.
3. No dead ends: every screen has a visible way out. Every modal has Cancel.
4. The same action has the same name and the same place everywhere (e.g. "Settings" is always in the main menu and pause menu, in the same position).
5. Destructive or irreversible actions (delete, discard unsaved edits, quit with unsaved work) ask for confirmation, naming what will be lost. Reversible ones use undo instead of a confirmation.
6. State is restored: the last used server, settings, window size and editor selection come back after a restart.

## 2. Input and controls

1. Full **keyboard + mouse** and **gamepad** support on every menu. Navigation is by focus: arrows/stick move, Enter/A accepts, Esc/B backs out, Tab cycles groups.
2. The focused element is always visibly highlighted (never rely on hover alone). First focus is set sensibly when a screen opens.
3. Controls are **rebindable**, stored in one input map, with conflict detection and "reset to default".
4. Default bindings follow genre convention (WASD, Space jump, Shift sprint, E interact, Tab scoreboard/hotbar, F1 help).
5. Context-sensitive **control hints** are shown on screen (bottom bar or tooltip), switching icons between keyboard and gamepad.
6. Mouse capture is explicit: gameplay captures, any menu/editor panel releases, and the state is always visible. Alt-Tab never leaves the cursor stuck.
7. Camera and look sensitivity, invert-Y and FOV are settings, not constants.

## 3. Feedback and responsiveness

1. Every interactive element has distinct **normal, hover, focus, pressed, disabled** states.
2. Any action that takes longer than ~100 ms shows progress; anything longer than ~1 s shows a progress indicator with a label (loading the map, connecting, saving). The UI thread never blocks: no frozen window.
3. Transitions are short (100–250 ms), consistent in easing, and skippable. Nothing animates for decoration only.
4. Success, warning and error feedback uses a consistent toast/banner pattern. Error messages say what happened and what to do next, never raw exceptions or codes alone.
5. Sound: UI click, hover, accept, back and error sounds, all on the UI audio bus with its own volume.

## 4. Visual system

1. One Godot **Theme** resource with design tokens (colours, font sizes, spacing, corner radius). Screens never hard-code colours or sizes.
2. 8 px spacing grid, one type scale (at most 5 sizes), one icon set, consistent alignment and margins.
3. Fits **1280×720 to 3840×2160**, 16:9 to 21:9, via anchors/containers and canvas scaling. No clipped or overlapping elements at any supported size. A **UI scale** setting (75–150 %) exists.
4. Readable: body text at least 16 px at 1080p; contrast at least 4.5:1 for text, 3:1 for icons and borders.
5. Not colour-only: state and team are also shown with shape, icon or text. A colour-blind-safe palette option exists for HUD and team colours.
6. The HUD is minimal, anchored to safe margins, with configurable visibility (health, ammo, minimap, hints).

## 5. Menus and settings

1. Main menu, pause menu and settings exist from the first playable build, not as a later pass.
2. Settings are grouped **Graphics / Audio / Controls / Gameplay / Accessibility**, all persisted to one config file, changes preview live, with **Apply / Revert** and an auto-revert timer for risky video changes.
3. Graphics: window mode, resolution, VSync, frame cap, render scale, quality preset, view distance. Audio: master, music, effects, UI, voice (as they appear).
4. Server/mod selection lists show name, mod, players, ping and status, sortable, with a clear empty and error state.
5. Every list, table and form has loading, empty and error states designed, not left blank.

## 6. HUD and in-game UI

1. Crosshair, health, ammo, weapon hotbar (selected slot obvious), kill feed, scoreboard (hold Tab), match timer and score, hit and damage indicators, respawn screen.
2. Text sizes and positions do not shift while values change (use fixed-width digits and reserved space).
3. Nothing in the HUD allocates per frame or rebuilds the layout every frame.

## 7. Map editor and developer tools

The editor meets the same bar as the game.

1. A clear **mode indicator** (fly / select / place / property edit) and a persistent **control hint bar**.
2. A layout with consistent panels: tool palette, parcel/property inspector, asset browser, status bar (selected ID, unsaved edits, coordinates).
3. **Undo/redo** for every edit (Ctrl+Z / Ctrl+Shift+Z), unsaved-change indicator, explicit save, confirmation on discard.
4. Selecting a parcel or property group highlights it in the world and in the inspector; hover previews it. Edits apply live ("pop-in") with no map rebuild.
5. Placement shows a ghost preview with snapping, rotate and grid options; invalid placement is shown before it is attempted.
6. Tooltips on every control, with its shortcut. F1 opens a shortcut cheat sheet. A command palette (Ctrl+P) searches actions and parcels by ID or address.
7. Property inspector fields use the right widget (dropdown for fence type, slider with number for ranges, colour picker), with validation and inline errors.
8. The prompt/MCP window shows the pending change, its result, and an undo for it.

## 8. Accessibility

Remappable inputs, UI scale, colour-blind palette, no reliance on colour alone, no required fast timing in menus, text never below the minimum size, a reduce-motion option that disables screen shake, camera bob and animated transitions.

## 9. Performance

The UI budget is under 1 ms per frame on the target machine. No per-frame allocations in UI code; no layout rebuilds each frame; large lists are virtualised. Opening a menu never drops the frame rate.

## 10. Definition of done for any UI issue

An issue touching UI is not done until its PR shows all of the following:

- [ ] Reached through the screen flow; `docs/standards/screen-flow.md` updated if a screen was added.
- [ ] Esc/Back behaviour verified, no dead ends, destructive actions confirmed or undoable.
- [ ] Walked through with **keyboard only**, and (once supported) with a gamepad.
- [ ] All interactive states present; focus visible; hints shown.
- [ ] Uses the shared Theme and tokens, no hard-coded colours or sizes.
- [ ] Screenshots attached at **1280×720** and **3840×2160** (and 21:9 where relevant), via `--screenshot`.
- [ ] Loading, empty and error states shown.
- [ ] Settings persisted where applicable.
- [ ] Review pass against sections 1–9 noted in the PR description, with any deviation named and justified.

## Open decisions (not yet made)

- Gamepad support from day one or after the keyboard/mouse flow is solid.
- Localisation: English only for now; strings go through Godot translation keys from the start so this stays cheap.
- Final art direction for UI (flat low-poly look to match the map is assumed).
