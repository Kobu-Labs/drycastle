# Screen flow

Kept current with every UI change (see `ui-ux.md`, section 1 and 10).

## Target flow

```
Boot → Main menu ─┬─ Play → Mod / server list → Loading → In game ⇄ Pause ─┬─ Settings
                  ├─ Map editor → Loading → Editor ⇄ Pause ──────────────┤
                  ├─ Settings (Graphics / Audio / Controls / Gameplay / Accessibility)
                  └─ Quit (confirm if unsaved work)
```

Esc from any screen goes one level up; from In game or Editor it opens Pause.

## Current state (does not yet meet the standard)

| Screen | Status |
|---|---|
| Main menu | missing |
| Pause menu | missing |
| Settings | missing |
| Loading | missing |
| Viewer HUD | minimal debug overlay |
| Map editor | exists (`godot/viewer/EditorMode.cs`), predates this standard; needs a conformance pass |

Tracked by the UI foundation issue.
