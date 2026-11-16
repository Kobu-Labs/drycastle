---
name: check
description: Start work on a ticket. Use for "/check #110", "check 110", "pick up #110", "start #110". Fetches latest main, makes an issue branch in its own worktree, reads the ticket, and reports a brief. Does not implement.
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
git fetch -q origin main
git worktree list | grep -E "issue-N-|/issue-N$"      # exists? reuse, do NOT recreate
git branch --list "issue-N-*"; git branch -r --list "origin/issue-N-*"   # branch w/o worktree? check it out instead of -b
git worktree add -b issue-N-<slug> .claude/worktrees/issue-N-<slug> origin/main
```
- Existing worktree: `git -C <wt> status -sb` + `git -C <wt> log --oneline origin/main..HEAD`; report ahead/behind main and dirty state. Don't reset/rebase unasked.
- Use `--no-track`-style base: branch off `origin/main`, not local `main` (may be stale).
- Work only via absolute paths / `git -C <wt>` from here on.

## 3. Get familiar

- Read `GLOSSARY.md` terms the ticket uses; grep `docs/adr/` for related ADRs.
- Locate code the ticket names (grep/Explore in the worktree, not main). Read the files, not just names.
- Touches phase/state logic: read `docs/state-machines.md` first (see CLAUDE.md).
- Touches UI: note `ui-work` skill + `docs/standards/ui-ux.md` apply.
- Skim related/linked issues, recent `git log` on the touched files.

## 4. Report (terse, bullets)

- `#N title` (state, labels, assignee), worktree path + branch, base sha
- Ask: what ticket wants, in 2-4 bullets
- Where: files/modules involved
- Constraints: ADRs, state machines, UI standard, blockers
- Suggested approach: short
- Unresolved questions last, if any

Do not implement, commit, push, claim/assign, comment or label the issue unless asked. Don't run the game, take screenshots, or build extensively.
