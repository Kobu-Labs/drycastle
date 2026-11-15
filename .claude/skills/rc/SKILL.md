---
name: rc
description: Release candidate (RC) workflow. Use for "put it in the latest RC", "add #N to the release candidate", "RC checklist", "what is in the RC", "mark #N passed/failed in the RC", "start a new RC". RC = branch release-<ver>v (e.g. release-0.10v) + issue label rc-<ver>v.
---

# Release candidate flow

An RC is a branch `release-<ver>v` cut from `main` plus a GitHub label `rc-<ver>v` on every ticket in it. The branch is not `release`: pushing it deploys nothing. The label is the test list.

**Latest RC** (resolve first, say which you got):
```bash
BR=$(git branch --list 'release-*v' --sort=-version:refname | tr -d ' *+' | head -1)   # release-0.10v
LB=rc-${BR#release-}                                                                    # rc-0.10v
```

## Actions

**Add to latest RC** ("put it in the RC", "add #N"):
1. Work must be committed on its own issue branch (not `main`; branch first if needed). Stage only the ticket's files.
2. `git checkout $BR && git merge --no-ff <issue-branch> -m "Merge <issue-branch> into $BR"`. Never push.
3. `gh issue edit N --add-label $LB`
4. `gh issue comment N --body "In release candidate ${BR#release-} (branch $BR, merge <sha>). <one line what changed>. To test: <concrete steps>"`. The checklist reads the text after "To test: ".
5. Report branch, merge sha, label.

**Checklist** ("RC checklist", "what is in the RC"): print as markdown, ticked when the ticket has an "RC pass" comment:
```bash
gh issue list --label $LB --state all --limit 200 --json number,title,state,comments --jq '.[] |
  (if any(.comments[]; .body|startswith("RC pass")) then "- [x] " else "- [ ] " end)
  + "#\(.number) \(.title) (\(.state))"
  + ([.comments[]|select(.body|startswith("In release candidate"))|.body|split("To test: ")[1]?]|first // "" | if .=="" then "" else "\n  - test: " + . end)'
```
End with counts: passed / untested.

**Result** ("#N passed" / "#N failed: why"): `gh issue comment N --body "RC pass: <what was checked>"`, or `"RC fail: <what>"` and `gh issue reopen N` if closed. A fix goes on the issue branch and is merged into `$BR` again (re-run the Add steps 2 and 4, comment "RC fix merged <sha>").

**New RC** ("start RC 0.11v"): `git checkout main && git checkout -b release-0.11v`, `gh label create rc-0.11v --color fbca04 --description "In release candidate 0.11v (branch release-0.11v): to test"`. From then on that is the latest.

**Ship**: never without an explicit go. Then `make release-check`, snapshot the map (`docs/release.md` checklist), PR `main` -> `release`. The label stays after shipping as the record of what shipped.

Keep replies terse: the checklist, the sha, nothing else.
