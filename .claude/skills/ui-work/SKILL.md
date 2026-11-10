---
name: ui-work
description: Required before adding or changing any UI in this project: menus, HUD, settings, loading screens, viewer overlay, map editor, dev tools, or any Godot Control/.tscn UI scene. Also when writing or reviewing a UI issue, PR, or subagent brief that touches UI.
---

# UI work

Every screen, including the map editor and dev tools, meets an AAA standard from the first version. Non-negotiable.

1. Read `docs/standards/ui-ux.md` in full before coding.
2. Use the shared Theme and tokens; create the Theme if it does not exist yet.
3. Before reporting done, complete its "Definition of done" and put the evidence in the PR: keyboard-only walkthrough, screenshots at 1280×720 and 3840×2160, deviations named. If a requirement can't be met, say so; never skip silently.
4. Label the issue and PR `ui`.

Briefing a subagent on UI: tell it to invoke this skill and paste the Definition of done into its brief.
