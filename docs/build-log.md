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

### AJ-002 toolchain and machine checks (2026-10-18T16:14Z to 16:16Z, measured)

| Check | Command | Exit code | Output | Label |
|---|---|---|---|---|
| SDK | `dotnet --version` | 0 | `10.0.401` | measured |
| SDK list | `dotnet --list-sdks` | 0 | seven SDKs under C:\Program Files\dotnet\sdk, including `10.0.401` and `10.0.112`; global.json with `latestPatch` on 10.0.401 resolves to 10.0.401 | measured |
| Docker CLI | `docker --version` | 0 | `Docker version 29.6.1, build 8900f1d`; the user-reported 29.6.1 is confirmed | measured |
| Docker daemon | `docker info --format '{{.ServerVersion}}'` | 0 | `29.6.1`, daemon running | measured |
| WSL2 | `wsl -l -v` with NUL bytes stripped by `tr -d '\0'` | 0 | distros Ubuntu-22.04 (default), Ubuntu, kali-linux, docker-desktop; every VERSION is 2; all Stopped | measured |
| Ollama | `ollama --version` | 127 | command not found in Git Bash; `where.exe ollama` exits 1; no `%LOCALAPPDATA%\Programs\Ollama` folder; port 11434 closed | measured |
| Ollama tags | `ollama list` | 127 | not available; no tag recorded, nothing pulled. Prompt section 7 (2026-10-17) records Qwen3 8B as pulled locally and the `nemotron-3-nano:30b` tag as confirmed; on 2026-10-18 Ollama itself is absent from this laptop, so both facts are superseded by this measurement, not corrected silently | measured |
| GPU | `nvidia-smi --query-gpu=name,driver_version,power.limit --format=csv,noheader` | 0 | `NVIDIA GeForce RTX 3080 Ti Laptop GPU, 616.92, [N/A]`; the name contains 3080; driver 616.92 equals the prompt value 616.92, no difference; power.limit prints `[N/A]` through nvidia-smi on this laptop, the M1 NVML probe measures the enforced limit (design C-11) | measured |
| NVML library | `test -f /c/Windows/System32/nvml.dll` | 0 | present | measured |
| git | `git --version` | 0 | `git version 2.54.0.windows.1` | measured |
| repository | `git rev-parse --is-inside-work-tree` | 0 | `true`; no `git init` was run (design C-10) | measured |
| gitleaks | `gitleaks version` | 127 | not installed; AJ-011 uses the `git grep fallback` | measured |
| keys present | PowerShell `[Environment]::GetEnvironmentVariable('<NAME>','User').Length -gt 0` and the same expression with `'Process'`, for NEBIUS_API_KEY, NVIDIA_API_KEY and NGC_API_KEY | 0 | `True` for all three in user scope and in process scope; no value and no length printed | measured |
| dotnet format | `dotnet format --version` | 0 | `10.0.401-servicing.26423.113+e34a38d2ae1fc26406a317517196e55c68ff83ab` | measured |
| ASP.NET Core reference pack | `ls "C:/Program Files/dotnet/packs/Microsoft.AspNetCore.App.Ref"` | 0 | contains `10.0.12` (design 2.3) | measured |

Blocked items, each naming the milestone it blocks:
- Ollama absent: the M4 local runtime (Ollama fallback, REQ-020) is blocked until the user installs Ollama and pulls the Nemotron tag that REQ-020 names (`nemotron-3-nano:30b` per prompt section 7, to be re-checked in M4); affected stories AJ-105 and AJ-107, and AJ-106 if the NIM attempt also fails; AJ-104 uses fake HTTP handlers and is not blocked; Qwen3 8B is only the optional baseline (AJ-114-T9). The M4 NIM attempt (REQ-019) is unaffected. Recorded as a user action under `## User actions` and as an unmet criterion under `## Requirements that cannot be met`; the M0 exit report carries it.
- Docker daemon running: the M1 container check (EDGE-001, EDGE-014) is not blocked.
- nvidia-smi and nvml.dll present: the M1 gated NVML integration test is not blocked.

Negative scenarios of AJ-002 (older SDK, stopped Docker daemon, missing nvidia-smi): none triggered, so no item goes under `## Requirements that cannot be met` and global.json is created by AJ-004 with 10.0.401 and is not lowered; the missing-tool rule was exercised by the Ollama row above (reason by name, milestone named, nothing invented).

### AJ-003 folder checks (2026-10-18T16:28Z, measured)

| Check | Command | Exit code | Result |
|---|---|---|---|
| seven folders exist | `for d in src tests docs content results tasks config; do test -d "$d" || echo "missing $d"; done` | 0 | printed nothing |
| empty folders trackable | `find content results tasks config -maxdepth 1 -name .gitkeep` piped to `wc -l` | 0 | 4 |
| root allow-list | `ls -d */` | 0 | config, content, docs, results, src, tasks, tests; hidden `.git`, `.code-review-graph` and `.serena` also exist, the last two are git-ignored tool state (accepted; `.serena` is not in the story's list) |
| no literal drive folder | `ls -A` filtered for `^[CD]:` | 1 | none |

### AJ-004 build-property checks (2026-10-18T16:28Z to 16:36Z, measured)

| Check | Command | Exit code | Result |
|---|---|---|---|
| global.json content | `cat global.json`; `grep -c '"test"' global.json` | 0; prints 0 | equals design 2.3; no `test` section (ADR-03) |
| REQ-001 literal strings | `grep -F "<TreatWarningsAsErrors>true</TreatWarningsAsErrors>" Directory.Build.props`; `grep -F "<Nullable>enable</Nullable>" Directory.Build.props` | 0; 0 | one line each |
| eleven properties | one grep per property name in Directory.Build.props | 0 | TargetFramework net10.0, ImplicitUsings enable, LangVersion latest, AnalysisLevel 10.0-recommended, EnforceCodeStyleInBuild true, GenerateDocumentationFile true, NoWarn `$(NoWarn);CS1591`, EnableConfigurationBindingGenerator true, IsPackable false, Deterministic true, ContinuousIntegrationBuild true only under `'$(GITHUB_ACTIONS)' == 'true'`; each found once |
| InvariantGlobalization in src only | `dotnet msbuild src/AgentJoules.Core -getProperty:InvariantGlobalization`; `grep -c InvariantGlobalization Directory.Build.props`; the same grep on tests/Directory.Build.props | 0; prints 0; prints 0 | `true`; the property appears only in src/Directory.Build.props |
| test projects not invariant | `dotnet msbuild tests/AgentJoules.Specs -getProperty:InvariantGlobalization` | 0 | empty value, not `true` (AJ-007 project, 16:36Z) |
| root properties reach src | `dotnet msbuild src/AgentJoules.Core -getProperty:TreatWarningsAsErrors` | 0 | `true` |
| root properties reach tests | `dotnet msbuild tests/AgentJoules.UnitTests -getProperty:TreatWarningsAsErrors` | 0 | `true` (AJ-007 project, 16:36Z) |
| tests props content | read tests/Directory.Build.props | n/a | import via GetPathOfFileAbove, IsTestProject true, NoWarn `$(NoWarn);CA1707;CA2007;CA1515`, xunit, xunit.runner.visualstudio and Microsoft.NET.Test.Sdk without Version; the ItemGroup is conditioned on `'$(MSBuildProjectName)' != 'AgentJoules.TestSupport'` |
| .editorconfig diff | `git diff --numstat .editorconfig`; `grep -c "file_scoped:suggestion" .editorconfig` | 0; prints 0 | `14 1`: one removed line (line 129, suggestion to warning) and 14 added (comment, `[*.cs]`, eight `dotnet_diagnostic` rows, blank line, `[*.{cs,json,md,svg,html}]`, `end_of_line = lf`) |
| SDK still resolves | `dotnet --version` | 0 | `10.0.401` |

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

Pre-M4 tool action, not one of the 13 prompt steps (added by AJ-002 on 2026-10-18, measured): Ollama is not installed on this laptop (`ollama --version` exit 127). Owner: user. Status: open. Gates: M4 local runtime (REQ-020; AJ-105, AJ-107; AJ-106 if NIM fails). Action: install Ollama and pull the Nemotron tag that REQ-020 names before M4 starts on 2026-10-24. AJ-012 adds the 13-step table after this note.

## Requirements that cannot be met

Requirement ID, reason and UTC date for every item stopped under standing order 6, and every story blocked under session prompt section 4 step 7.

| Item | Reason | UTC date | Label | Gates |
|---|---|---|---|---|
| AJ-002 acceptance criterion 4 (`ollama --version` exits 0) | Ollama is not installed on this laptop (exit 127; `where.exe ollama` exit 1; no install folder; port 11434 closed). Installing software is a user action, so the builder records it under standing order 6 and does not improvise a substitute | 2026-10-18T16:15Z | measured | M4 local runtime (REQ-020); REQ-001 and the M0 exit criteria are unaffected |

## Base commits

`git rev-parse origin/main` and `git rev-parse HEAD` at build start with their UTC date (AJ-002); the HEAD hash is `<build-start>` for every later commit-message check.

| Ref | Hash | UTC date | Label |
|---|---|---|---|
| `git rev-parse origin/main` | aafaba934b50db74947090086bc32fb2441c2380 | 2026-10-18T16:14Z | measured |
| `git rev-parse HEAD` at build start, the `<build-start>` of every commit-message check | aafaba934b50db74947090086bc32fb2441c2380 | 2026-10-18T16:14Z | measured |

## Story log

One entry per story commit: what shipped, decisions, deviations from design or story with the reason, measured numbers with labels, estimated hours, wall-clock minutes and sub-agent tokens.

### AJ-001 Create the build log with fixed sections (REQ-002)

- Shipped: docs/build-log.md with the nine fixed headings, a tenth heading `## Story log`, a one-line purpose under each, and the AJ-001 check rows under `## Toolchain check`.
- Decisions: the tenth heading `## Story log` is the home of the per-story entries that docs/plan/milestone-session-prompt.md section 4 step 6 requires; none of the nine fits them. This is a deviation from the story's heading list and is declared here. Sub-agent tokens in every story entry count the implementation and gate calls; the docs call that writes the entry is excluded there and added to the milestone total at the exit.
- Deviations: the story's LF check `grep -c $'\r'` is blind on this machine (Git for Windows grep 3.0, see the Toolchain check rows), so a byte count is logged beside it. Design section 2.3 claims an LF working tree; `.editorconfig` and `.gitignore` are CRLF in the working tree (measured), to be fixed by AJ-009.
- Gates: code-review-gatekeeper round 1 CHANGES_REQUIRED (MAJOR: T2 rows missing; MINOR: estimated hours in the Story log purpose, blocked-story home, tenth-heading declaration), all applied before this commit; security-specialist APPROVED; spec-compliance-reviewer APPROVED (AJ-001 AC1 to AC3 PASS).
- Velocity: estimate 0.50 h; wall-clock 16:16Z (branch created) to 16:25Z (entry written), 9 min, commit follows; sub-agent tokens 258,829 (docs writer 71,132; gates 75,056 + 79,612 + 33,029).

### AJ-002 Verify toolchain and machine prerequisites (REQ-001)

- Shipped: the AJ-002 toolchain table (15 rows), the blocked-items list and the negative-scenario note under `## Toolchain check`; the two base-commit rows under `## Base commits`; the Ollama user action under `## User actions`; the AJ-002 criterion 4 row under `## Requirements that cannot be met`.
- Decisions: no `git init` (design C-10); global.json is created by AJ-004 at 10.0.401; the three negative scenarios did not trigger, so nothing is marked blocked for Docker or the GPU.
- Deviations: acceptance criterion 4 is unmet (Ollama absent, user action, see `## Requirements that cannot be met`); the prompt section 7 facts about Qwen3 8B and the Nemotron Ollama tag (2026-10-17) are superseded by today's measurement; the UTC date is given once per table heading rather than per row.
- Gates: code-review-gatekeeper round 1 CHANGES_REQUIRED (MAJOR: Ollama unblock condition and story list, AJ-012 does not own the Ollama row; MINOR: superseded prompt fact, global.json wording, process-scope command, full format version), all applied before this commit; security-specialist APPROVED (INFO on the process-scope command, applied); spec-compliance-reviewer CHANGES_REQUIRED on criterion 4 only (criteria 1 to 3 and 5 to 8 PASS), resolved by recording criterion 4 as a user action under standing order 6 because the builder cannot install Ollama.
- Velocity: estimate 1.25 h; wall-clock 16:14Z (first check) to 16:32Z (entry written), 18 min, commit follows; sub-agent tokens 282,036 (docs writer 77,028; gates 95,259 + 77,260 + 32,489).

### AJ-003 Create the repository folders (REQ-001)

- Shipped: `content/.gitkeep`, `results/.gitkeep`, `tasks/.gitkeep`, `config/.gitkeep`; `src/` and `tests/` are created by the files of AJ-004 to AJ-007.
- Decisions: `tasks/.gitkeep` resolves the REQ-001 versus design 3.4 conflict as the story proposes; the M2 story that adds the three task files (AJ-048, plan seam 1) deletes it in the same commit. The git-ignored `.serena/` folder at the root is tool state like `.code-review-graph/` and is accepted outside the story's allow-list.
- Deviations: none.
- Gates (pooled over AJ-003 to AJ-006, one review call per gate): code-review-gatekeeper APPROVED (no finding for AJ-003); security-specialist APPROVED; spec-compliance-reviewer APPROVED (criteria 1 to 3 PASS).
- Velocity: estimate 0.50 h; AJ-003 to AJ-006 were implemented in one csharp-expert call from 16:25Z to 16:28Z and gated together from 16:29Z to 16:34Z; pooled wall-clock for the four stories 16:25Z to 16:40Z (about 15 min plus the four entry commits); pooled sub-agent tokens 377,849 (implementation 100,084; gates 123,465 + 84,892 + 69,408), recorded once here and referenced by the AJ-004 to AJ-006 entries.

### AJ-004 Pin the SDK and share the build properties (REQ-001)

- Shipped: `global.json`, `Directory.Build.props`, `src/Directory.Build.props` (GetPathOfFileAbove import, InvariantGlobalization), `tests/Directory.Build.props` (import, IsTestProject, NoWarn, shared test packages), `.editorconfig` with the one in-place change and the appended analyzer and LF sections.
- Decisions: the shared test PackageReference ItemGroup is conditioned on `'$(MSBuildProjectName)' != 'AgentJoules.TestSupport'` so the design's "except TestSupport" holds for packages as well as for IsTestProject; the appended `.editorconfig` block starts with a comment and an explicit `[*.cs]` header so the diagnostics are not scoped by the file's last section; `.editorconfig` keeps its CRLF working-tree endings (git normalises on commit) and keeps `insert_final_newline = false` for `[*.cs]`, so `dotnet format` strips the trailing newline of every .cs file.
- Deviations: none from the story. Three MINOR code-review notes accepted without change: (1) `$(NoWarn);CS1591` drops the SDK default `1701;1702` (binding-redirect warnings that .NET 10 does not emit; the reviewer's scratch build with the pinned packages gave 0 warnings); (2) `end_of_line = lf` is only safe after AJ-009 adds `* text=auto eol=lf`, so no checkout, stash or restore happens between this commit and AJ-009; (3) NU1901 to NU1904 audit warnings become errors under TreatWarningsAsErrors, accepted on purpose because the AJ-008 rule resolves them by version and never by NoWarn.
- Gates: pooled with AJ-003 (see that entry); code-review-gatekeeper APPROVED with the three MINOR notes above; security-specialist APPROVED; spec-compliance-reviewer APPROVED (criteria 1 to 6 PASS; the two test-project checks it deferred are measured in the table above).
- Velocity: estimate 2.00 h; pooled wall-clock and tokens in the AJ-003 entry.
