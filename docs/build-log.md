# Agent Joules build log

Purpose: the single record of toolchain facts, version deviations, scan results, test counts, per-ID filter results, probe and WSL2 notes, user actions, requirements that cannot be met and base commits (REQ-002, REQ-029, builder standing order 6). ASCII only, LF only; never a key value, a hostname or an IP. Every number carries one label: measured, user-reported, inferred or assumed.

## Toolchain check

Commands, exit codes, UTC dates and labels of the SDK, Docker, WSL2, Ollama, GPU driver and git checks (AJ-002), plus the hygiene, property and grep checks that later M0 stories run.

### AJ-001 checks (2026-10-18T16:17:53Z, measured)

| Check | Command | Exit code | Result |
|---|---|---|---|
| nine headings once each | for each of the nine headings: `grep -cxF "<heading>" docs/build-log.md`; a count other than 1 prints `missing heading: <heading>` and the loop exits 1 | 0 | every count is 1 |
| ASCII only | `grep -nP "[^\x00-\x7F]" docs/build-log.md` | 1 | no match |
| LF only, story command | `grep -c $'\r' docs/build-log.md` | prints 0 | Git for Windows grep 3.0 strips CR before matching, so this command cannot see a CR; the byte count on the next row is the binding check |
| LF only, byte count | `tr -cd '\r' < docs/build-log.md` followed by `wc -c` | prints 0 | 0 CR bytes |
| no key pattern | `grep -nE "nvapi-[A-Za-z0-9_-]{8,}" docs/build-log.md` | 1 | no match |
| negative, scratch copy: deleted heading | the heading loop on a copy without `## Test counts` | 1 | printed `missing heading: ## Test counts` |
| negative, scratch copy: non-ASCII dash | `grep -nP "[^\x00-\x7F]"` on a copy with an em-dash appended | 0 | reported the offending line number (44) |
| scratch copy: a row appended under an existing heading | one row appended under `## Secret scan`, then `grep -cxF "## Secret scan"` | prints 1 | heading count unchanged, no new heading |

Working-tree line endings (2026-10-18T16:22Z, measured with `tr -cd '\r' < <file>` and `wc -c`): `.editorconfig` has 389 CR bytes in 389 lines and `.gitignore` 482 CR bytes in 499 lines, so both are CRLF in the working tree (`git ls-files --eol` prints `i/lf w/crlf`; `core.autocrlf=true`). README.md, .gitattributes and the docs/ files are LF. The index is LF for every file. Design section 2.3 states the worktree was LF on 2026-10-18; that is not true for these two files. AJ-009 adds `* text=auto eol=lf` and re-checks the two files out as LF.

## Version deviations and additions

Every package whose used version differs from the pins of design section 2.4 (package, pinned version, used version, reason) and every package added beyond the design list (REQ-002).

## Secret scan

Scan name, exact command (variable names only, never values), exit codes, UTC date and verdict of every secret scan run (REQ-029, AJ-011); the latest row is the one that counts.

## Test counts

Passed, failed and skipped counts of `dotnet test` per recorded run (AJ-008 and later).

## Per-ID filter results

One row per `dotnet test --filter "Spec=<ID>"` run: ID, passed, failed, skipped, UTC date.

## Probe and WSL2 notes

Results of `probe` runs and of the Linux and WSL2 execution checks (M1 and later).

## User actions

Prompt section 12 steps 1 to 13 with owner, status, gating milestone and evidence (AJ-012).

## Requirements that cannot be met

Requirement ID, reason and UTC date for every item stopped under standing order 6, and every story blocked under session prompt section 4 step 7.

## Base commits

`git rev-parse origin/main` and `git rev-parse HEAD` at build start with their UTC date (AJ-002); the HEAD hash is `<build-start>` for every later commit-message check.

## Story log

One entry per story commit: what shipped, decisions, deviations from design or story with the reason, measured numbers with labels, estimated hours, wall-clock minutes and sub-agent tokens.

### AJ-001 Create the build log with fixed sections (REQ-002)

- Shipped: docs/build-log.md with the nine fixed headings, a tenth heading `## Story log`, a one-line purpose under each, and the AJ-001 check rows under `## Toolchain check`.
- Decisions: the tenth heading `## Story log` is the home of the per-story entries that docs/plan/milestone-session-prompt.md section 4 step 6 requires; none of the nine fits them. This is a deviation from the story's heading list and is declared here. Sub-agent tokens in every story entry count the implementation and gate calls; the docs call that writes the entry is excluded there and added to the milestone total at the exit.
- Deviations: the story's LF check `grep -c $'\r'` is blind on this machine (Git for Windows grep 3.0, see the Toolchain check rows), so a byte count is logged beside it. Design section 2.3 claims an LF working tree; `.editorconfig` and `.gitignore` are CRLF in the working tree (measured), to be fixed by AJ-009.
- Gates: code-review-gatekeeper round 1 CHANGES_REQUIRED (MAJOR: T2 rows missing; MINOR: estimated hours in the Story log purpose, blocked-story home, tenth-heading declaration), all applied before this commit; security-specialist APPROVED; spec-compliance-reviewer APPROVED (AJ-001 AC1 to AC3 PASS).
- Velocity: estimate 0.50 h; wall-clock 16:16Z (branch created) to 16:25Z (entry written), 9 min, commit follows; sub-agent tokens 258,829 (docs writer 71,132; gates 75,056 + 79,612 + 33,029).
