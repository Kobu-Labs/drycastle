## Agent skills

### Issue tracker

Issues are tracked in GitHub Issues for Kobu-Labs/drycastle (via the `gh` CLI). See `docs/agents/issue-tracker.md`.

### Domain docs

Single-context: one `GLOSSARY.md` + `docs/adr/` at the repo root. See `docs/agents/domain.md`.

### UI/UX standard

All UI, including the map editor and developer tools, follows `docs/standards/ui-ux.md` (AAA standard, from `mods/methodology.md`). Non-negotiable: polished menus and predictable flow with sensible controls from the first version. Before writing or changing UI, read it; a UI issue is done only when its section 10 checklist is met and shown in the PR (keyboard-only walkthrough, screenshots at 1280x720 and 3840x2160, theme tokens only). Keep `docs/standards/screen-flow.md` current. Briefs for UI subagents must include the standard.
