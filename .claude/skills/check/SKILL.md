---
name: check
description: Start work on a ticket. Use for "/check #110", "check 110", "pick up #110", "start #110". Fetches latest RC branch, makes an issue branch in its own worktree, reads the ticket, and reports a brief (goal, recommendations, open questions). Does not implement.
---

# /check #N

Arg: issue number (`#110` or `110`). No arg: ask.

## 1. Read ticket (before touching git)

```bash
gh issue view N --json number,title,state,body,labels,assignees,comments,blockedBy,parent
gh pr view N 2>/dev/null   # bare #N may be a PR; see docs/agents/issue-tracker.md
```
- Closed issue / PR / open blockers: say so in the brief, still proceed unless user says stop.
- Parent/sub-issues: view parent too, list siblings.

## 2. Worktree

Never touch the main checkout (editor/MCP write it live). Convention: `.claude/worktrees/issue-N-<slug>`, branch `issue-N-<slug>`; slug = title, lowercase, kebab, max ~5 words.

```bash
cd /home/jjakubec/coding/drycastle
git fetch -q origin
BR=$(git branch -r --list 'origin/release-*v' --sort=-version:refname | tr -d ' ' | sed 's#^origin/##' | head -1)   # latest RC, e.g. release-0.10v (see rc skill)
git worktree list | grep -E "issue-N-|/issue-N$"      # exists? reuse, do NOT recreate
git branch --list "issue-N-*"; git branch -r --list "origin/issue-N-*"   # branch w/o worktree? check it out instead of -b
git worktree add -b issue-N-<slug> .claude/worktrees/issue-N-<slug> origin/$BR
```
- Base = latest RC branch `origin/$BR`, not main. Say which RC in the report.
- Existing worktree: `git -C <wt> status -sb` + `git -C <wt> log --oneline origin/$BR..HEAD`; report ahead/behind `origin/$BR` and dirty state. Don't reset/rebase unasked.
- Use `--no-track`-style base: branch off `origin/$BR`, not local branches (may be stale).
- Work only via absolute paths / `git -C <wt>` from here on.

## 3. Get familiar

- Read `GLOSSARY.md` terms the ticket uses; grep `docs/adr/` for related ADRs.
- Locate code the ticket names (grep/Explore in the worktree, not main). Read the files, not just names.
- Touches phase/state logic: read `docs/state-machines.md` first (see CLAUDE.md).
- Touches UI: note `ui-work` skill + `docs/standards/ui-ux.md` apply.
- Skim related/linked issues, recent `git log` on the touched files.

## 4. Report (terse, bullets)

- `#N title` (state, labels, assignee, blockers if any), worktree path + branch, base sha
- **Goal**: summary of what the ticket is for, 2-4 bullets. Outcome, not implementation.
- **Recommendations**: what to do + why, incl. ADR/state-machine/UI-standard implications when they shape the call. Opinionated, short.
- **Open questions**: numbered (`1.`, `2.`), last. Each answerable in a line; state your default. Omit section if none.

No file lists, no "Where" section, no code locations. Files are for your own familiarisation (step 3), not the report.

Do not implement, commit, push, claim/assign, comment or label the issue unless asked. Don't run the game, take screenshots, or build extensively.
