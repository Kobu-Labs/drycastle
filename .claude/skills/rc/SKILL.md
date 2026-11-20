---
name: rc
description: Release candidate (RC) workflow. Use for "put it in the latest RC", "add #N to the release candidate", "RC checklist", "what is in the RC", "mark #N passed/failed in the RC", "start a new RC". RC = branch release-<ver>v (e.g. release-0.10v) + project field RC = rc-<ver>v.
---

# Release candidate flow

An RC is a branch `release-<ver>v` cut from `main` plus the project field `RC` = `rc-<ver>v` on every ticket in it. The branch is not `release`: pushing it deploys nothing. The RC field is the test list. No labels are used for RC.

**The RC lives on `origin`, not locally.** `origin/$BR` is the source of truth. Never check it out or merge in the main checkout (it may hold that branch, and the editor/MCP write to it live). Merge in a throwaway worktree and push.

**Latest RC** (resolve first, say which you got):
```bash
git fetch -q origin
BR=$(git branch -r --list 'origin/release-*v' --sort=-version:refname | tr -d ' ' | sed 's#^origin/##' | head -1)   # release-0.10v
RCV=rc-${BR#release-}                                                                   # rc-0.10v
```

**Project board** (https://github.com/orgs/Kobu-Labs/projects/7). Fields: `RC` = rc-0.10v / rc-0.11v / rc-0.12v (which RC the ticket is in); `Tested` = No/Yes/Failed. Helpers, run for an issue N; each adds the issue to the project if missing (idempotent), then sets the field:
```bash
PJ=PVT_kwDOBbn3Qs4BsAfd; FD=PVTSSF_lADOBbn3Qs4BsAfdzhqQmVg; FRC=PVTSSF_lADOBbn3Qs4BsAfdzhqseas
declare -A OPT=([No]=693d3fcb [Yes]=aa5fbb44 [Failed]=229d2260)
declare -A RCOPT=([rc-0.10v]=cca7b354 [rc-0.11v]=691eea66 [rc-0.12v]=4ecbcfe4)
rc_set() { # rc_set N RCV  (sets the RC field)
  local item=$(gh project item-add 7 --owner Kobu-Labs --url "$(gh issue view $1 --json url -q .url)" --format json -q .id)
  gh project item-edit --project-id $PJ --id "$item" --field-id $FRC --single-select-option-id ${RCOPT[$2]}
}
tested() { # tested N V
  local item=$(gh project item-add 7 --owner Kobu-Labs --url "$(gh issue view $1 --json url -q .url)" --format json -q .id)
  gh project item-edit --project-id $PJ --id "$item" --field-id $FD --single-select-option-id ${OPT[$2]}
  # entering the RC (No) = work started: Status -> In Progress (not Todo)
  [ "$2" = No ] && gh project item-edit --project-id $PJ --id "$item" --field-id PVTSSF_lADOBbn3Qs4BsAfdzhqQjLM --single-select-option-id 47fc9ee4
}
```
The board view per RC: filter `RC:rc-<ver>v`. `Tested` is per issue, not per RC: carried-over issues keep their value; reset by hand if retest needed.

## Actions

**Add to latest RC** ("put it in the RC", "add #N"):
1. Work must be committed on its own issue branch (not `main`; branch first if needed). Stage only the ticket's files.
2. Merge in a throwaway worktree and push (the issue branch must be pushed first, or merge its sha):
   ```bash
   WT=$(mktemp -d) && git worktree add -q --detach $WT origin/$BR
   git -C $WT merge --no-ff <issue-branch> -m "Merge <issue-branch> into $BR" && git -C $WT push origin HEAD:$BR
   SHA=$(git -C $WT rev-parse --short HEAD); git worktree remove --force $WT
   ```
   Conflict: resolve in `$WT`, never in the main checkout. Local `$BR`, if any, is stale; do not touch it.
3. `rc_set N $RCV`; `tested N No` (project helpers above; `tested` also sets Status=In Progress)
4. `gh issue comment N --body "In release candidate ${BR#release-} (branch $BR, merge <sha>). <one line what changed>. To test: <concrete steps>"`. The checklist reads the text after "To test: ".
5. Report branch, merge sha, RC value.

**Checklist** ("RC checklist", "what is in the RC"): print as markdown, ticked when the ticket has an "RC pass" comment:
```bash
gh project item-list 7 --owner Kobu-Labs --limit 1000 --format json \
  | jq -r --arg rc "$RCV" '.items[] | select(.rC==$rc) | .content.number' \
  | while read n; do gh issue view $n --json number,title,state,comments --jq '
  (if any(.comments[]; .body|startswith("RC pass")) then "- [x] " else "- [ ] " end)
  + "#\(.number) \(.title) (\(.state))"
  + ([.comments[]|select(.body|startswith("In release candidate"))|.body|split("To test: ")[1]?]|first // "" | if .=="" then "" else "\n  - test: " + . end)'; done
```
End with counts: passed / untested.

**Result** ("#N passed" / "#N failed: why"): `gh issue comment N --body "RC pass: <what was checked>"`, or `"RC fail: <what>"` and `gh issue reopen N` if closed. Also `tested N Yes` (pass) / `tested N Failed` (fail). A fix merged again: `tested N No`. A fix goes on the issue branch and is merged into `$BR` again and pushed (re-run the Add steps 2 and 4, comment "RC fix merged <sha>").

**New RC** ("start RC 0.11v"): `git fetch -q origin && git push origin origin/main:refs/heads/release-0.11v`. Add option `rc-0.11v` to the project `RC` field (board settings UI; the gh CLI cannot add single-select options), then copy its option id into `RCOPT` above. From then on that is the latest.

**Ship**: never without an explicit go. Then `make release-check`, snapshot the map (`docs/release.md` checklist), PR `main` -> `release`. The RC field stays after shipping as the record of what shipped.

Keep replies terse: the checklist, the sha, nothing else.
