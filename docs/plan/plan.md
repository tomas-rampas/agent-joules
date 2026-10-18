# Agent Joules - plan

Status: second refresh 2026-10-18 after the audit fix round; story files final; draft for user approval. Deadline: 2026-10-30 10:00 PDT = 17:00 UTC = 18:00 CET. Evidence labels: measured (a command was run on the files), derived (arithmetic on measured numbers), assumed (stated planning value).

## 1. Purpose and binding inputs

Each milestone is built in its own Claude Code session that starts from `docs/plan/milestone-session-prompt.md` (kickoff line, model rules, per-story loop, exit report); the orchestrator never pushes, the user pushes after each exit report.

This plan turns the build prompt into a dated, gated, cuttable schedule for an autonomous build session that the user supervises. It does not change the prompt or the design.

Precedence when two inputs disagree: (1) `docs/agent-joules.prompt.md`, the source of truth (32 REQ, 19 EDGE, 24 Definition of Done boxes, standing orders, sections 10 and 12); (2) `docs/design/technical-design.md`, which resolves prompt conflicts in its section 9.2 (standing order 8); (3) this file; (4) the eight story files in `docs/plan/stories/` (M0 to M7, 125 stories); (5) `docs/plan/traceability.md`, the generated mapping.

Rules the build session follows (prompt standing orders, restated because the gates below depend on them):
- Work milestone by milestone and story by story in plan order; write each story's failing Reqnroll scenarios and unit tests first; tick the story's definition-of-done boxes in the story file when done (standing order 8).
- Commit once per completed story. Commit convention: `AJ-NNN REQ-xxx: <summary>` or `AJ-NNN EDGE-xxx: <summary>`, for example `AJ-045 EDGE-006: retry HTTP 429 with backoff` (standing order 5). DoD box 13 and the story checks now follow this order (amendment P-1, applied to the prompt 2026-10-18, section 6).
- Never `git init`, never `git push`, never create, resize or delete a cloud resource. The user pushes, enables Pages, creates the release and submits.
- A requirement that cannot be met is written to `docs/build-log.md` and reported; no substitute is improvised (standing order 6). Stretch goals start only after all 24 boxes are checked (standing order 7).

## 2. Milestones

Hours are the sum of task estimates in the story files (measured by `awk` over the task tables, see traceability.md "Counts"). MUST hours use the classification of section 3.2. Totals include 2.60 h of USER ACTION tasks (M4 0.75 h, M5 1.60 h, M6 0.25 h); builder-only total 436.80 h. This table is also the assignment of REQ and EDGE IDs to milestones that the coverage checks use.

| Milestone | Dates (2026) | Goal | Exit criteria (one line) | REQ / EDGE | Stories | Hours | MUST h |
|---|---|---|---|---|---|---|---|
| M0 Prerequisites and scaffold | Oct 18-19 | Empty, warning-free .NET 10 solution, build log, secret baseline | `dotnet build -warnaserror -tl:off` 0 warnings; 22 pins; `Spec=REQ-029` green; REQ-001/002/029 commits | REQ-001, 002, 029 | AJ-001..012 (range 001..019) | 16.00 | 15.00 |
| M1 Energy meter core | Oct 19-21 | NVML binding, meter math, probe, no-GPU path, CI file | `results/probe-<ts>.json` committed; Spec filters for REQ-003..006, 024 and EDGE-001/002/003/005/013/014 green; container probe prints `gpuPresent: false` | REQ-003, 004, 005, 006, 024; EDGE-001, 002, 003, 005, 013, 014 | AJ-020..038 (020..039) | 51.00 | 44.25 |
| M2 Metered client and workflow | Oct 21-23 | Metered client, 4-agent workflow, tier config, `run` with dry-run | 12 Spec filters green; fan-out greps hit; `run --tier bogus` exits 2; live nvidia/nebius dry-run (or BLOCKED, carried) | REQ-007, 008, 011, 012, 013, 014, 017; EDGE-004, 006, 007, 008, 011 | AJ-040..058 (040..069) | 81.75 | 66.75 |
| M3 Cost, quality, results, report, live dashboard | Oct 23-25 | Cost, judge, schema, validate, report, static page, README block function, dashboard and replay | 9 Spec filters green; fixture `validate` prints `Validated 3 files`; fixture report greps pass; `dashboard --replay` exits 0 | REQ-009, 010, 015, 016, 023, 030, 032; EDGE-012, 018, 019 | AJ-070..093 (070..099) | 105.00 | 98.00 |
| M4 Local runtime and measurement | Oct 24-26 | Runtime decision (NIM 90 min box, Ollama), endpoints, calibration, methodology, laptop campaign | probe + local/nvidia/nebius run files + calibration committed; `validate results/` exits 0; nim/endpoints/methodology docs dated | REQ-018, 019, 020, 021, 022; EDGE-009, 010 | AJ-100..115 (100..119) | 55.45 | 48.45 |
| M5 cloudgpu tier | Oct 26-27 | L40S NIM run on a Nebius VM over SSH | cloudgpu run + probe schema-valid; `docs/cloudgpu-run.md` Cost <= USD 30 and Teardown `deleted` | REQ-031; EDGE-015, 016, 017 | AJ-120..129 (120..129) | 36.95 | 25.70 |
| M6 Docs, content, demo | Oct 27-29 | README, posts, demo script, article, Devpost files, `report` flags | `report --verify-readme` exits 0 and `report --help` lists the three new flags; posts resolved; shot list <= 170 s; zero `[[FILL:`; zero `owner: builder` | REQ-025, 026, 027, 028; REQ-016 (report flags, AJ-144) | AJ-130..144 (130..149) | 55.25 | 46.00 |
| M7 Hardening and submission | Oct 29-30 | Prove the 24 boxes, final reviews, release prep, submission plan | 24 boxes ticked or listed with reasons; `peer-review-critic` `VERDICT: APPROVED`; user: Pages 200, release, Devpost before 17:00 UTC | none (24 DoD boxes) | AJ-150..159 (150..159) | 38.00 | 26.50 |
| Total | | | | 32 REQ, 19 EDGE | 125 | 439.40 | 370.65 |

Measured totals: 125 stories, 903 scenarios (398 positive, 505 negative), 723 tasks, 439.40 h, 25 `Conflict:` lines (several others became `Resolved:` lines), 0 duplicate story IDs, 0 duplicate scenario names, 0 tasks above 2 h, every story carries `@AJ-S<nn>` and `@REQ`/`@EDGE` tags. Every REQ and EDGE appears in the milestone this table assigns (checked by script, no gap); the one addition is REQ-016, which AJ-144 (M6) also lists because the `report` flags and the content renderer live there.

## 3. Capacity and schedule

### 3.1 The problem in numbers

- Window: 2026-10-18 to 2026-10-29 18:00 CET (17:00 UTC, the content freeze of AJ-158) = 12 calendar days; at about 6 h per day of user time that is 72 h (assumed, from the user). The M7 gate (final reviews) then runs until the push at 20:00 UTC.
- Effort: 439.40 h of story estimates for all stories, 370.65 h for MUST only. Derived requirement, not a forecast: 36.6 story-hours per calendar day (6.1 per user hour) for everything, 30.9 per calendar day (5.1 per user hour) for MUST only.
- Story hours are effort estimates for a human developer. The build session's real velocity is unknown until M0 and M1 are done. No speed-up factor is assumed anywhere in this plan; section 3.5 measures it at each gate and decides from the measurement.
- Review gates (section 4) are not inside the story hours; they are paid in calendar time and therefore appear in the measured velocity automatically.
- Resolved gaps: the `report` flags and `ContentTemplateRenderer` (GAP-1) are built by AJ-144 (M6, MUST, 6.25 h); `RunAssembler`, the serialized totals and `ReportSentences` are created by AJ-056-T4a (M2, MUST, 1.75 h) so that AJ-047 can be cut; the marking of in-flight steps as `incomplete` on shutdown (exit 5, `run.status` `incomplete`) is owned by AJ-056-T6a (M2, MUST, 0.75 h), which AJ-034, AJ-123 and AJ-127 point at (audit Y-01). Both are inside every figure below.
- Fixed wall-clock inside the totals: NIM attempt box 1.5 h (AJ-103-T3), model download 1.0 h (AJ-107-T2), laptop campaign 2.0 h (AJ-114-T4), VM box up to 4 h (AJ-125 to AJ-127). They overlap other work.

### 3.2 Classification (MUST, SHOULD, COULD)

- MUST: needed for one of the 24 Definition of Done boxes (traceability.md Table C) or for a step of the demo program AJ-135 (section 8), or run by a MUST story as evidence.
- SHOULD: a prompt requirement not tied to a DoD box, or robustness around a MUST path.
- COULD: polish or an optional variant.
- Boxes 1 ("every REQ and EDGE acceptance met") and 15 (Spec traits) quantify over everything. A SHOULD or COULD cut leaves the named IDs UNMET and the final report lists them with the reason (prompt section 10); no other box is touched by a Level 1 or Level 2 cut.

| Milestone | MUST (stories, h) | SHOULD (stories, h) | COULD (stories, h) |
|---|---|---|---|
| M0 | 11, 15.00 | AJ-012 (1.00) | none |
| M1 | 16, 44.25 | AJ-023, 028, 036 (6.75) | none |
| M2 | 15, 66.75 | AJ-047, 051, 057, 058 (15.00) | none |
| M3 | 22, 98.00 | AJ-088, 093 (7.00) | none |
| M4 | 13, 48.45 | AJ-102, 104 (4.75) | AJ-113 (2.25) |
| M5 | 7, 25.70 | AJ-123, 124, 129 (11.25) | none |
| M6 | 13, 46.00 | AJ-143 (4.00) | AJ-137 (5.25) |
| M7 | 7, 26.50 | AJ-157, 158 (8.50) | AJ-159 (3.00) |
| Total | 104, 370.65 (84.3 %) | 18, 58.25 (13.3 %) | 3, 10.50 (2.4 %) |

All stories not named are MUST; traceability.md Table D carries the class of every story. Decisions encoded: AJ-100 is MUST (it alone builds `AJ validate --config`, which AJ-101, 107, 115, 126 and 132 run); AJ-038 is MUST (AJ-155 asserts `.github/workflows/ci.yml`); AJ-047 stays SHOULD and is cut-safe because AJ-056-T4a (MUST) creates `RunAssembler` and `ReportSentences` and AJ-047 depends on it. Promotions: AJ-124 becomes MUST if the VM probe shows `energyCounterSupported: false`; AJ-123 if a run is lost; AJ-129 as soon as the cloudgpu tier is partial or absent. AJ-045 (429 retry) and AJ-077 (judge never fails the run) are MUST because the hosted run files of box 7 cannot be produced reliably without them; AJ-052/053 are MUST because `--dry-run` runs them.

### 3.3 MUST critical path to a working Agent Joules and its demo

Single session, so the path equals the MUST total unless branches overlap (parallel sub-agents are an assumption; their throughput is not measured).

| Stage | MUST stories | h | Unblocks | Fixed wall-clock |
|---|---|---|---|---|
| S1 Scaffold | AJ-001..011 | 15.00 | green empty build, redaction, scan | none |
| S2 Meter, probe, CI file | AJ-020..022, 024..027, 029..035, 037, 038 | 44.25 | committed laptop probe | none |
| S3 Client, config, workflow, run | AJ-040..046, 048..050, 052..056 | 66.75 | fake-driven `run`, dry-run | none |
| S4a Results chain | AJ-070..087 | 65.00 | fixture report, README block function | none |
| S4b Dashboard | AJ-089..092 | 33.00 (replay minimum 30.75) | replay, `--dashboard --dry-run` | none; parallel to S4a after AJ-056 |
| S5 Local runtime, campaign | AJ-100, 101, 103, 105..112, 114, 115 | 48.45 | local, nvidia, nebius, calibration files | NIM 1.5 h, download 1.0 h, campaign 2.0 h |
| S6 cloudgpu | AJ-120..122, 125..128 | 25.70 | cloudgpu files | VM box <= 4 h; user tasks 1.6 h |
| S7 Content | AJ-130..136, 138..142, 144 | 46.00 | README block, posts, story, script | none; about 29 h front-loadable into S5/S6 waits (M6 Risks row 1) |
| S8 Verification | AJ-150..156 | 26.50 | 24 boxes, `VERDICT: APPROVED` | none |

Sum 370.65 h. If S4b and the front-loadable part of S7 overlap other work, the path shortens by at most about 33.0 + 29 = 62 h to about 309 h (assumed, needs parallel execution). AJ-133 and AJ-134 wait for AJ-144-T4..T6. The campaign (S5) needs S3 and S4a complete, because the run file needs judge, cost and schema; the cloudgpu runs need the whole local DoD committed (AJ-115, prompt section 2).

### 3.4 Cut order (single source of the drop order; AJ-159 copies it verbatim)

Admission is the reverse of the cut order: inside each milestone the MUST stories are built first; SHOULD and COULD stories last, and only while the gate rule of section 3.5 shows slack. Shortfall S = R_all - v* x T (hours of remaining work the session cannot deliver by the freeze, see 3.5). A step fires at a decision gate when S exceeds the cumulative hours of all earlier steps and its stories are not yet started. Decision gates (UTC): M1 2026-10-21 16:00, M2 2026-10-23 16:00, M3 2026-10-25 17:00, M4 2026-10-26 17:00, M5 2026-10-27 17:00. REGEN = `AJ report --in results/ --out docs/report/ --update-readme --render-content`, then `AJ report --verify-readme` (exit 0).

Mechanics. (1) The task tables chain each story to the previous one; when a story is cut, the next retained story's first task is re-pointed to the last retained story and the change is logged in `docs/build-log.md`. (2) "Create or extend in the first MUST consumer": files first created by a cut story (the AJ-104 probe artifacts are created by AJ-107, `OllamaRuntime.cs`, the `ILocalRuntime` extension, `WorkflowSteps.cs`, `LocalRuntimeTests.cs` from AJ-104; `NimAttemptDocumentTests` from AJ-102; `IncompleteRunTests` and the mid-stream drop mode from AJ-123) are created by AJ-105, AJ-106, AJ-103 and AJ-127 instead. No hours are added to the plan; when the cut happens the creating tasks' hours move to the consumer, so the realized saving of C4, C6 and C8 is the listed hours minus those tasks (not estimated here; AJ-159 records the actual value). (3) AJ-047 is cut-safe: its only dependency is on AJ-056-T4a, and no other story depends on it. (4) The dashboard methodology sentences formerly written by AJ-091-T8 and AJ-093-T6 are now owned by AJ-112, so cutting D2 or C7 removes only the README usage text and the feature, not a methodology paragraph.

Level 1: stories. "Boxes unticked" is `none` for every C step; the umbrella boxes 1 and 15 report the named IDs as UNMET (consequence column).

| Step | Stories | h | Cum h | Trigger | Consequence | Boxes unticked | Regeneration |
|---|---|---|---|---|---|---|---|
| C1 | AJ-159, AJ-113, AJ-012 | 6.25 | 6.25 | S > 0 h at the M1 gate or later | no second drop-order copy (this section is the source); optional Qwen3 baseline marking (REQ-020 "if made") absent; user-action record absent | none | none |
| C2 | AJ-157 | 3.75 | 10.00 | S > 6.25 h | release assets and notes lost; Demo URL second satisfier lost (Pages first, video URL third) | none | none |
| C3 | AJ-137 | 5.25 | 15.25 | S > 10.00 h | REQ-027 second half (article) UNMET | none | none |
| C4 | AJ-088, AJ-102, AJ-023 | 7.25 | 22.50 | S > 15.25 h | automated proof only: REQ-023 six-class gate, REQ-019 document-contract test, REQ-003 integration gate; checked by hand in the build log | none | none |
| C5 | AJ-051, AJ-036, AJ-028 | 9.00 | 31.50 | S > 22.50 h | REQ-014 instruments (keep the `UseOpenTelemetry` grep if 1 h is spare), EDGE-014 and the EDGE-001 container check, EDGE-013 throttling (methodology says "not measured") | none | none |
| C6 | AJ-104, AJ-047, AJ-058, AJ-057 | 13.25 | 44.75 | S > 31.50 h | EDGE-009 fix-command messages, REQ-013 concurrent half and EDGE-004 (`--mode concurrent` documented as not built), REQ-017 per-code exit mapping, EDGE-006 half (partial results on a failed step) | none | none |
| C7 | AJ-093 | 4.00 | 48.75 | S > 44.75 h | EDGE-019 failure isolation; no published run and no video take uses `--dashboard` live without it, replay instead | none | none |
| C8 | AJ-123, AJ-124 | 7.25 | 56.00 | S > 48.75 h and the VM probe and runs did not trigger them | EDGE-016, EDGE-015 depth | none | none |
| C9 | AJ-129, AJ-158, AJ-143 | 12.75 | 68.75 | S > 56.00 h; AJ-129 never when the tier is partial or absent | tier-consequence rules, the 12-step submission order, the honesty audit (M7 AJ-152/153 still check the same files) | none | REGEN |

Level 2: tasks inside MUST stories (cumulative hours restart; trigger counts on top of Level 1).

| Step | Tasks | h | Cum h | Trigger | Consequence | Boxes unticked | Regeneration |
|---|---|---|---|---|---|---|---|
| D1 | AJ-114-T9 (Qwen run) | 0.75 | 0.75 | S > 68.75 h | baseline run absent | none | REGEN if a `-baseline` file existed |
| D2 | AJ-091-T6 and T8 | 2.00 | 2.75 | S > 69.50 h | REQ-032 live events: the live page shows no power or progress during a run; the video uses `dashboard --replay` | none | none |
| D3 | AJ-090-T5 | 1.50 | 4.25 | S > 71.50 h | power chart: cards and tables only | none | none |

Dashboard order inside M3: AJ-089 (T1..T6c), AJ-090 (including the R1 to R6 tasks T3a, T4a, T4b), AJ-091 (T1..T5 including T4a and T4b), AJ-092 first = 30.75 h, which is everything box 24 and the replay need; then AJ-091-T6 and T8 (D2), then AJ-093 (C7). If only one view can be built, it is replay (AJ-092 and its technical prerequisites).

Level 3: the cloudgpu tier. Needs the user's explicit approval (prompt section 10); decided at the M4 gate, before the VM time box starts.

| Step | Stories | h | Cum h | Trigger | Consequence | Boxes unticked | Regeneration |
|---|---|---|---|---|---|---|---|
| L3 | AJ-120..122, AJ-125..128 and AJ-151 not built; AJ-129 kept | 24.95 net (25.70 + 3.25 - 4.00) | 24.95 | S > 73.00 h at the M4 gate 2026-10-26 17:00 UTC and the user approves | cloudgpu absent: README row reads "cloudgpu: not run", candidate A-local, no cloudgpu sentence anywhere; platform rule met through the `nebius` run file; a second cloudgpu repeat set is never a drop step (box 20) | 21, 22, 23 (substance) | REGEN |

Floor after all levels: 370.65 - 4.25 - 24.95 = 341.45 h (28.5 per calendar day), still a high bar; the cuts cannot close the gap alone, velocity decides. Level 1 saves 68.75 h, Level 2 4.25 h, Level 3 24.95 h net; together 97.95 h.

Submission shapes (REQ-028; AJ-129, AJ-141, AJ-158):
- cloudgpu complete: pitch candidate A, README row `cloudgpu` with the VM preset, region, GPU-hours and observed cost, boxes 21 to 23 checked.
- cloudgpu partial (4 h wall-clock from the `CLOUDGPU_SSH_HOST` handover, or USD 25 observed, whichever first; hard cap USD 30): run file status `partial`, README row says `partial`, `Cost` names the stop reason, candidate A stays (a `partial` file is a committed run file under REQ-028 as amended), VM deleted by the user, boxes 21 to 23 still checkable.
- cloudgpu absent: row `cloudgpu | not run`, candidate A-local (singular tagline), Built-with without Nebius AI Cloud, README sentence `None besides Token Factory.`, no cloudgpu sentence in story, posts, script or article, boxes 21, 22 and the substance of 23 reported unchecked with the reason. The platform rule is then met through the `nebius` Token Factory run file, which becomes the hard requirement (a missing `nebius` file means BLOCKED, never a silent skip).

### 3.5 Schedule gates and the re-planning rule

Gate times are 18:00 local on the date shown (CEST until Oct 24, CET from Oct 25); the final freeze is 17:00 UTC on Oct 29. "Required on remaining" divides the remaining hours by the days left to the freeze, assuming earlier gates were met exactly (derived).

| Gate | Date | Days elapsed | Cum MUST h | Cum all h | Remaining MUST h | Days left | Required on remaining, MUST / all (h per day) |
|---|---|---|---|---|---|---|---|
| M0 exit | Oct 19 | 2 | 15.00 | 16.00 | 355.65 | 10 | 35.6 / 42.3 |
| M1 exit (first decision) | Oct 21 | 4 | 59.25 | 67.00 | 311.40 | 8 | 38.9 / 46.5 |
| M2 exit | Oct 23 | 6 | 126.00 | 148.75 | 244.65 | 6 | 40.8 / 48.4 |
| M3 exit | Oct 25 | 8 | 224.00 | 253.75 | 146.65 | 4 | 36.7 / 46.4 |
| M4 exit | Oct 26 | 9 | 272.45 | 309.20 | 98.20 | 3 | 32.7 / 43.4 |
| M5 exit | Oct 27 | 10 | 298.15 | 346.15 | 72.50 | 2 | 36.2 / 46.6 |
| M6 and M7 exit (freeze) | Oct 29 | 12 | 370.65 | 439.40 | 0 | 0 | M6 and M7 together hold 72.50 MUST h for the last 2 days |

Reading: the dates demand 33 to 41 MUST hours per remaining day at every gate, while the first gates look cheap (7.5 and 14.8 per elapsed day). Use the right-hand column, not the cumulative average, to decide.

Re-planning rule, applied at every milestone exit (and written to `docs/build-log.md` under `## Gate <Mk>`):
1. Record delivered hours (sum of estimates of stories whose DoD boxes are ticked and committed), split MUST and other; d = calendar days since 2026-10-18; v = delivered MUST hours per day; v_w = the same over the last milestone window. Use v* = min(v, v_w).
2. Compute T = days to 2026-10-29 17:00 UTC, R_must = remaining MUST h, R_all = remaining h of all stories not cut.
3. If R_all / v* <= T: no cut; admit SHOULD and COULD stories of the remaining milestones, after the MUST stories of their milestone.
4. If R_all / v* > T >= R_must / v*: apply Level 1 steps C1, C2, ... in order until R_all' / v* <= T (the trigger column of 3.4 states the same rule as a shortfall S = R_all - v* x T).
5. If R_must / v* > T: apply all of Level 1, then Level 2 in order D1 to D3. If it still misses, propose Level 3 to the user with these numbers before M5 starts, and use the missing-item rows of AJ-158.
6. Level 1 is pre-approved if decision D-2 is accepted; Level 2 and Level 3 need an explicit user reply each time. Every cut records gate, numbers, items, REQ and EDGE affected and boxes affected (none for Level 1 and 2).
7. A later gate that shows slack re-admits deferred items in reverse cut order. The M0 gate is a sanity reading only (setup-dominated); the M1 gate (Oct 21) is the first real decision. If no gate is ever met the final report lists the unchecked boxes with reasons (prompt section 10).

### 3.6 User actions on the calendar (prompt section 12)

Keys (steps 5, 6, 7) are already set on the laptop (user-reported 2026-10-18). One calendar for the end game, as AJ-158 states it: content freeze 2026-10-29 17:00 UTC (10:00 PDT); M7 gate passed and `git push` 20:00 UTC (13:00 PDT); Pages and release 21:00 UTC (14:00 PDT); video recorded on Oct 29 and public by 2026-10-30 07:00 UTC (00:00 PDT); submission by 14:00 UTC (07:00 PDT); deadline 17:00 UTC (10:00 PDT, 18:00 CET). Latest acceptable targets, used only when the evening plan slips: push Oct 30 07:00 UTC, Pages and release 08:00 UTC, video public 11:00 UTC, submission 14:00 UTC. Other times are planning targets (assumed); CEST until Oct 24.

| When | Action | Needed by | If late |
|---|---|---|---|
| Oct 18 | Decide D-1 (pins) and D-2 (cut order) | AJ-005, M0 | M0 starts on the prompt defaults |
| Oct 18-21 | Steps 1 to 3: Devpost account, join, read the Rules tab; paste its text if the fetch returns 403 | AJ-138 (Oct 27) | rules recorded `not verified`; box 9 reported with that reason |
| Oct 18-23 | Step 4: request the USD 25 Token Factory credits and join the Builders Program | AJ-101 45-minute box (Oct 24) | nebius onboarding box starts without credits |
| Oct 18-23 | Steps 10 and 11: AI Cloud account at console.nebius.com, card, USD 25 minimum, `ssh-keygen -t ed25519` outside the repo | AJ-120 (Oct 26) | M5 cannot start; Level 3 |
| Oct 23 | Steps 5 and 6 live check (keys present) | M2 exit | item BLOCKED, carried to M4 |
| Oct 24-26 | Laptop on mains power, other GPU applications and browsers closed during AJ-110, AJ-111, AJ-114 (and step 7, Docker Desktop with GPU support, for AJ-103) | M4 | battery runs are not committed |
| Oct 27, when the builder asks | Step 12: create the VM (platform `gpu-l40s-a`, preset `1gpu-16vcpu-64gb`, fallbacks as in the prompt, `eu-north1`, image `ubuntu24.04-cuda12`, disk >= 150 GB, public IP, preemptible off), read the hourly price, set `CLOUDGPU_SSH_HOST` and `CLOUDGPU_SSH_USER` | AJ-125; starts the 4 h / USD 30 box | box shortened |
| Oct 27, during runs | Report the billing-page cost before each invocation (AJ-127-T5) | stop at USD 25 | guard stops on the 4 h mark only |
| Oct 27, after "results copied" | Step 13: delete the VM and boot disk, give deletion time and final cost | AJ-128 | `Teardown` unconfirmed, box 22 open, money keeps running |
| Oct 27-28 | Take the screenshot of the live dashboard during the AJ-135 rehearsal and save it as `docs/report/dashboard.png` (AJ-135-T6, 0.25 h; the README links it only if the file exists) | AJ-131, AJ-135 | no link, no screenshot; nothing else changes |
| Oct 28 | Fill Registration placeholders (handle, join date, credits date) | AJ-139 | placeholders stay, listed in the final report |
| Oct 29 before 17:00 UTC | Confirm the drop order (this plan, 3.4); fill the 7 `owner: user` feedback placeholders; fill `{{repo.url}}` in posts | AJ-158 | listed in the final report |
| Oct 29, 20:00 UTC after the M7 gate | `git push -u origin main` (D-6) | AJ-156 | latest Oct 30 07:00 UTC |
| Oct 29, 21:00 UTC | Enable Pages (main, `/docs`); `gh release create v0.1.0`; check `curl` of `/report/` prints 200 | AJ-157, AJ-158 | Demo URL falls back to the release, then the video |
| Oct 29 (recording), public by Oct 30 07:00 UTC | Record the video (planned 165 s), upload as a public YouTube video (D-7) | AJ-158 | latest 11:00 UTC; no fallback exists for a missing video |
| Oct 30 by 14:00 UTC (15:00 CET, 07:00 PDT) | Step 8: submit on Devpost in the order of the checklist | deadline 17:00 UTC | 3 h buffer |
| After submission | Step 9: keep the repository on the submitted commit; check the first CI run | judging Dec 1-15 | none |

## 4. Review gates per milestone

Per story, before its commit (framework rule for the build session): `code-review-gatekeeper` and `security-specialist` on the story's diff; `spec-compliance-reviewer` scoped to the REQ and EDGE IDs in the story's `Covers:`; findings of severity BLOCKER and MAJOR are fixed before the commit `AJ-NNN REQ-xxx: ...`. Per milestone, after its last commit: `peer-review-critic` on `<previous milestone exit commit>..HEAD` (M0: from the `origin/main` hash recorded in `docs/build-log.md`); the milestone is declared done only with `VERDICT: APPROVED` or user sign-off of the remaining findings. A `CHANGES_REQUIRED` verdict is fixed and re-run, at most three rounds (the Stop hook allows three), then the user decides. Reviewers keep their default `opus` tier; implementers run at their default tier; long noisy runs (test batteries, the 2 h campaign, CI logs) go to `bash-expert` or `powershell-expert`.

| Milestone | Spec reviewer scope | Security focus | Milestone gate extra |
|---|---|---|---|
| M0 | REQ-001, 002, 029 | redaction helper, `.gitignore` secret patterns (`*.pem`, `id_ed25519*`, `id_rsa*`), secret scan, no key in build log | build, format, test commands exit 0 |
| M1 | REQ-003..006, 024; EDGE-001, 002, 003, 005, 013, 014 | P/Invoke surface (System32-only load path, handles, no exception escapes), probe output free of secrets, CI file without `secrets.` | committed probe equals `nvidia-smi` driver |
| M2 | REQ-007, 008, 011..014, 017; EDGE-004, 006, 007, 008, 011 | key redaction on every error path, base URLs from configuration only, retry never echoes headers, test harness isolation from real keys | greps for fan-out and `UseOpenTelemetry`; no `AsyncLocal` |
| M3 | REQ-009, 010, 015, 016, 023, 030, 032; EDGE-012, 018, 019 | dashboard binds 127.0.0.1 only, no external resource, HTML-encoded values, no key in SSE payloads, safe path handling in `validate` and `replay` | `Validated 3 files`; replay exits 0 |
| M4 | REQ-018..022; EDGE-009, 010 | `docker login --password-stdin`, no key in `docs/nim-local-attempt.md`, `Bearer ****` in `docs/endpoints.md`, results free of key material | `git grep` of the three keys prints nothing |
| M5 | REQ-031; EDGE-015, 016, 017 | SSH only, no cloud-resource command anywhere, memory-backed `DOCKER_CONFIG`, keys only in the SSH session environment, no host or IP in the repository, cost guard | `grep -F "$NGC_API_KEY" docs/cloudgpu-run.md` prints nothing |
| M6 | REQ-025..028; REQ-016 for the report flags (AJ-144) | generated content free of secrets, report page without `<script>`, honesty audit AJ-143 | `report --verify-readme` exits 0; `report --help` lists the new flags |
| M7 | whole spec: all REQ and EDGE plus the 24 boxes (AJ-154) | repository-wide secret scan, release assets free of `results/*.raw` and keys | branch against `origin/main`, `VERDICT: APPROVED` last line |

## 5. Dependencies and seams between milestones

Seams recorded by the story writers, plus the ones found while consolidating.

| # | Seam | Producer | Consumer | Rule |
|---|---|---|---|---|
| 1 | `tasks/.gitkeep` | AJ-003 (M0) | AJ-048 (M2) | deleted in the commit that adds the three task files |
| 2 | Build log headings, `SecretRedactor` | AJ-001, AJ-010 (M0) | all stories | later stories append under existing headings; AJ-034 registers present key values |
| 3 | `Metric`, `IEnergyMeter`, fake meter, `ReadingCalculator`, `ScriptedNvmlApi`, `CliApplication` with `probe` | M1 | M2 | a story that finds one missing creates the minimum and logs it |
| 4 | Meter gate types `IMeterGate`, `SerializedMeterGate`, `NoOpMeterGate` | AJ-032 (M1) | AJ-046, AJ-047 (M2) | M1 delivers the types, M2 the `--mode` wiring, `shared-window` and union totals (REQ-013) |
| 5 | Exit 6 mapping | AJ-034 (M1) | AJ-054, AJ-058 (M2) | `run --tier local` with the missing-NVML fake carries the EDGE-001 trait |
| 6 | `low-resolution`, `sampling` disclosures | AJ-026, AJ-027 (M1) | AJ-083, AJ-087 (M3), AJ-133 (M6) | report row, header and README assertions reference EDGE-003 and EDGE-002 |
| 7 | Throttle result on the laptop | AJ-028 (M1) | AJ-112 (M4) | methodology says "unsupported" if the API is |
| 8 | `ICostCalculator` placeholder, `IJudgeService` no-op | M2 | AJ-070..072, AJ-076 (M3) | replaced by registration, no M2 test changes |
| 9 | Minimal `RunDocument` and writer | AJ-055 (M2) | AJ-078, AJ-079 (M3) | M3 completes the records and generates the schema; M2 fixtures are re-validated |
| 10 | `ILocalRuntime` warm-up no-op | M2 | AJ-105, AJ-106 (M4) | M4 fills it |
| 11 | `--config` arity | AJ-043, 054, 086 | AJ-080 (M3), AJ-100 (M4) | plain path option per command; zero-or-one arity only on `validate`; flag form implemented in M4 |
| 12 | Live-event hook points in `MeteredChatClient` and `RunOrchestrator` | M2 | AJ-091-T6 (M3) | AJ-089 depends on AJ-056-T1 and AJ-040-T3 |
| 13 | Exit code 7 | mapper in AJ-058 (M2) | AJ-144 (M6) | the real `--verify-readme` path and its exit-7 test are built in AJ-144-T5/T6 |
| 14 | README block | AJ-087 (M3, pure function) | AJ-133 (M6) | M3 delivers `ReadmeResultsBlock.Render`; AJ-144 (M6) adds `Update`, `Verify` and the flags; AJ-133 runs them and adds the partial and not-run row cases |
| 15 | `--update-readme`, `--verify-readme`, `--render-content`, `ContentTemplateRenderer` (GAP-1, resolved) | AJ-144 (M6, 6.25 h, MUST) | AJ-133, AJ-134, AJ-135 | AJ-133-T1 depends on AJ-144-T6, AJ-134-T1/T3/T6 on AJ-144-T4/T5; null values render as label text, implemented in AJ-144 |
| 16 | `docs/methodology.md` write order | AJ-112 (M4) now owns the dashboard sentences too | AJ-091-T8, AJ-093-T6 (README text only) | AJ-112 carries a merge rule and never overwrites; same for `README.md`, extended in place by M1, M3, M6 |
| 17 | First live judge calls | AJ-076 (M3) | AJ-114 (M4) | judge failure never changes the exit code |
| 18 | `validate results/` count and `report` on real runs | M3 | M4, M6 | carried forward until real runs exist |
| 19 | Live dry-run of nvidia and nebius | M2 exit | AJ-101 (M4) | recorded BLOCKED and carried if keys were absent |
| 20 | `## cloudgpu` endpoint heading | AJ-100 (M4, `Status: pending`) | AJ-126 (M5) | AJ-126 adds `Verified:` and the model line |
| 21 | Local DoD gate | AJ-115 (M4) | AJ-120 (M5) | M5 starts only after AJ-115 is committed (prompt section 2) |
| 22 | Schema cloudgpu fields, `RunFileSelector` statuses | AJ-079, AJ-081 (M3) | AJ-123, AJ-127 (M5) | statuses `completed`, `partial`, `incomplete`, `failed`; only the first two are selectable |
| 23 | Rental cost formula | AJ-072 (M3) | AJ-125, AJ-127 (M5) | hourly price comes from the console via the user, configuration key `HourlyPriceUsd` |
| 24 | Tier state and the `partial` README word | AJ-129 (M5 fixtures) | AJ-133, AJ-141 (M6), M7 | live check when the block is generated; M7 re-runs it (conflict row 23) |
| 25 | Illustrative video run | AJ-135 (M6) | AJ-150 (M7) | command adds `--out artifacts/demo-run`; `run.repeat` of latest files re-checked |
| 26 | Replay of a committed run file | AJ-092 (M3) | AJ-135, AJ-136 (M6) | video fallback when the local tier is unavailable |
| 27 | Drop order | this plan, section 3.4 | AJ-158, AJ-159 (M7) | one source: AJ-159 copies steps C1 to C9, D1 to D3 and Level 3 verbatim; the parser asserts the same step ids |
| 28 | `RunAssembler`, serialized totals, `ReportSentences` | AJ-056-T4a (M2, MUST) | AJ-047, AJ-072, 074, 076, 105, 122 | AJ-047 depends on AJ-056-T4a and nothing depends on AJ-047, so it can be cut |
| 29 | `AJ validate --config` and the `docs/endpoints.md` skeleton | AJ-100 (M4, MUST) | AJ-101, 107, 115, 126, 132 | those stories run the command as evidence |
| 30 | Files first created by a cut story | AJ-104, AJ-102, AJ-123 | AJ-105, 106, 103, 127 | the first MUST consumer creates or extends the file (3.4 mechanics) |
| 31 | Marking in-flight steps `incomplete` on shutdown (Y-01, resolved) | AJ-056-T6a (M2, MUST) | AJ-034 (shutdown path), AJ-123, AJ-127 | AJ-056-T6a sets `run.status` `incomplete`, checkpoints and exits 5; the other three stories point at it instead of re-specifying it |
| 32 | Cut-safety of MUST stories (Y-06 to Y-09, resolved) | AJ-021-T3 holds the Linux NVML candidates; AJ-050-T5a (0.25 h) creates `StepFailedException`; AJ-107 creates the AJ-104 probe artifacts; AJ-151 criterion 7 writes the cloudgpu-absent record | AJ-036, AJ-057, AJ-104, AJ-158 | cutting C5 (AJ-036), C6 (AJ-057, AJ-104) or C9 (AJ-158) removes no file a MUST story needs; AJ-058 extends the `ExitCode` enum and AJ-131 owns the README exit-code table |

## 6. Conflict register

All 25 `Conflict:` lines of the story files (measured with `grep -h '^Conflict:' M*.md | wc -l`; four more lines became `Resolved:` lines and are in the second table). Owner: "prompt amendment, applied 2026-10-18" = the prompt sentence has been changed by the coordinator (wording quoted in the second table); "design" = the design section holds or should hold the sentence (most are already in design 9.2); "accepted as written" = no text changes, sequencing or content already present. Design 9.2 items C-01 to C-03 and C-09 to C-12 are not repeated; C-01 is decision D-1.

| # | Story | ID | Conflict | Proposed resolution | Owner |
|---|---|---|---|---|---|
| 1 | AJ-003 | REQ-001 | `tasks/` must exist, git cannot track an empty folder, design 3.4 says exactly three JSON files | `tasks/.gitkeep` until AJ-048 deletes it in the commit that adds the task files | accepted as written |
| 2 | AJ-026 | EDGE-003 | rendered row must contain `low-resolution`, needs the M3 renderer | M1 delivers the flag; AJ-083 asserts the row with the EDGE-003 trait | accepted as written |
| 3 | AJ-027 | EDGE-002 | header and README disclosure need M3 and M6 | M1 delivers method, flag, console line; AJ-083 and AJ-133 carry the header and README assertions | accepted as written |
| 4 | AJ-028 | EDGE-013 | "methodology says so" needs the M4 file | M1 logs the laptop result; AJ-112 copies the sentence | accepted as written |
| 5 | AJ-032 | REQ-005, REQ-013 | the gate belongs to REQ-013 (M2) but the M1 brief asks for it | M1 delivers `IMeterGate` and implementations under REQ-005; M2 keeps REQ-013 wiring | accepted as written |
| 6 | AJ-035 | EDGE-001 | `run --tier local` returns 6 needs the M2 `run` command | M1 proves the mapping at its source; AJ-054 adds the CLI-level test with the EDGE-001 trait | accepted as written |
| 7 | AJ-043 | REQ-012 | `--config` has two meanings (design C-05) | `--config <path>` per command; zero-or-one arity only on `validate` | design |
| 8 | AJ-054 | REQ-017 | `--tier bogus` must exit 2, System.CommandLine returns 1 (C-04) | `CliApplication` inspects `ParseResult.Errors` and returns 2 | design |
| 9 | AJ-054 | REQ-017 | `--config` meaning on `run` (C-05) | plain path option on `run` | design |
| 10 | AJ-056 | REQ-017 | `--tier all` vs cloudgpu only on the VM (C-06) | `all` expands to enabled tiers in order; cloudgpu disabled in the committed file, enabled on the VM by `AGENTJOULES__Tiers__cloudgpu__Enabled=true` (REQ-017 already says so) | accepted as written |
| 11 | AJ-072 | REQ-009 | no cloudgpu cost defined (C-08) | step cost = hourly price x wall time, `estimated`, USD; billed total in `environment.cloudCostUsd` (REQ-009 already says so) | accepted as written |
| 12 | AJ-080 | REQ-015 | invalid arguments exit 2 vs 1 (C-04) | shared `ParseResult.Errors` check applies to `validate` | design |
| 13 | AJ-080 | REQ-015 | `--config` meaning on `validate` (C-05) | arity zero-or-one; flag form checks model IDs, implemented in M4 (AJ-100) | design |
| 14 | AJ-082 | REQ-016 | "table per step" vs median over repeats (C-07) | REQ-016 now reads as quoted in the second table (amendment 1) | prompt amendment, applied 2026-10-18 |
| 15 | AJ-085 | REQ-030 | "one `<table>` row per step" vs median over repeats (C-07) | REQ-030 now reads as quoted in the second table (amendment 2) | prompt amendment, applied 2026-10-18 |
| 16 | AJ-086 | REQ-016 | `--config` meaning on `report` (C-05) | plain path option on `report` | design |
| 17 | AJ-089 | REQ-032 | live power and step status are not sink readings (C-13) | readings via `FanOutMetricsSink`; power, progress, step-started as `LiveEvent`s from values the run already computed | design |
| 18 | AJ-090 | REQ-032, REQ-030 | report page has no `<script>`, live page needs JavaScript (C-15) | different pages; the live page is never written to `docs/report/` | design |
| 19 | AJ-091 | REQ-032, REQ-012 | no `"http` literal in C#, dashboard prints a URL (C-14) | URI built with `UriBuilder(Uri.UriSchemeHttp, ...)`; page uses relative URLs | design |
| 20 | AJ-100 | REQ-018 | `## cloudgpu` needs `Verified:` before the VM exists | M4 commits `Status: pending (verified in M5, AJ-126)`; validator requires `Verified:` for enabled tiers; AJ-126 closes it | accepted as written |
| 21 | AJ-129 | EDGE-017 | README row must contain `partial`, README block is built in M6 | M5 proves the rule with fixtures; AJ-133 checks live; M7 re-runs | accepted as written |
| 22 | AJ-129 | REQ-028 | A-local applies when "no committed run file", but an excluded `incomplete` file may exist | REQ-028 now defines "no committed run file" as quoted in the second table (amendment 5) | prompt amendment, applied 2026-10-18 |
| 23 | AJ-135 | REQ-027 | the video command writes a newest repeat-1 local file that breaks "latest" and box 20 | already in design section 10 step 9 and prompt REQ-027 (`--out artifacts/demo-run`); no design change | design |
| 24 | AJ-140 | REQ-028 | story asks for median joules per run, no per-run sum exists | add to design 3.5: a run is the sum of the step `netJoules` of one (task, repeat) with outcome `ok`; the `jq` expression is recorded in the build log; label `measured` | design |
| 25 | M7 preamble | deadline | an earlier note said 19:00 CEST; CEST ended Oct 25 | all M7 times in UTC and PDT; CET = UTC+1 on Oct 30; nothing else changes | accepted as written |

Owners of the 25 rows: 3 prompt amendment (applied), 11 design, 11 accepted as written.

The seven prompt amendments (all applied by the coordinator on 2026-10-18; no approval requested; wording as it stands in `docs/agent-joules.prompt.md`) and the two plan-found items:

| Ref | Where | Wording in force | Status |
|---|---|---|---|
| Amendment 1 (row 14) | REQ-016 | "a Markdown table with one row per (tier, task, agent) aggregate over the REQ-021 repeats (design conflict C-07; rows are not one per step)" | applied |
| Amendment 2 (row 15) | REQ-030 | "one `<table>` row per (tier, task, agent) aggregate over its repeats (design conflict C-07: rows aggregate the REQ-021 repeats, they are not one per step)" | applied |
| Amendment 3 (AJ-106, now a `Resolved:` line) | REQ-021 | "(`--baseline-seconds` stays a free option for tests; a value below 30 prints a warning and the run file carries the `non-publishable` flag of design conflict C-16, so it is never selected as the latest file; plan conflict AJ-106)"; REQ-017 keeps "`--baseline-seconds <n>` (default 30, local only)" | applied |
| Amendment 4 (AJ-126, `Resolved:`) | section 8 | "Because `docker login` writes the credential to the Docker config file, the login uses a memory-backed `DOCKER_CONFIG` directory (tmpfs) that is removed right after the pull, followed by `docker logout nvcr.io`; the NIM container receives the key by variable name only when the image requires it, and `docs/cloudgpu-run.md` shows these steps with the value redacted (plan conflict AJ-126)." | applied (REQ-031 itself unchanged) |
| Amendment 5 (row 22) | REQ-028 | "\"no committed run file\" means no `results/*-cloudgpu.json` whose `run.status` is `completed` or `partial` (an excluded `incomplete` or `failed` file counts as none; plan conflict AJ-129)" | applied |
| Amendment 6 (AJ-134, two `Resolved:` lines) | REQ-026 | "\"GPU-board energy measured on one laptop\" for variants that cite laptop numbers only, or \"GPU-board energy measured on one laptop and on one Nebius AI Cloud GPU, reported separately\" for variants that also cite cloudgpu numbers (plan conflict AJ-134)"; a placeholder whose value is null renders its label text, never 0 | applied |
| Amendment 7 (P-1) | DoD box 13 | "`git log --oneline` shows commits whose messages start with the story ID followed by the REQ or EDGE IDs delivered, as standing order 5 defines (for example `AJ-045 REQ-007: meter streaming calls`); M7 stories that deliver no REQ start with the story ID alone" | applied |
| Exit code 5 (design 3.6 and the prompt exit-code list aligned 2026-10-18; AJ-034, AJ-123, AJ-127) | exit codes | code 5 now also covers "a run interrupted before completion by host shutdown or a lost connection (`run.status` `incomplete`, EDGE-016)" and "no free loopback port within 20 attempts for `dashboard --replay`" | applied; AJ-034 builds the `ApplicationStopping` path, AJ-123 and AJ-127 produce and test the `incomplete` file |
| P-2 | M4, M5 Estimate text | "builder hours exclude USER ACTION tasks" while the sums included them | resolved in the story files: totals include user actions, builder-only figures in parentheses (M4 54.70 h, M5 35.35 h) |
| Y-01 (re-audit) | AJ-034, AJ-123, AJ-127, AJ-056 | exit code 5 for a run interrupted before completion: who marks the in-flight steps `incomplete` | owned by AJ-056-T6a (0.75 h, MUST): marks in-flight steps `incomplete`, sets `run.status` `incomplete`, checkpoints on shutdown, exits 5; the other stories point at it | resolved in the story files |
| REQ-032 R1 to R6 (user decision 2026-10-18) | design 3.10; AJ-089 (+0.75 h), AJ-090 (+2.25 h), AJ-091 (+0.50 h), AJ-135 (+0.25 h user action), AJ-131 | REQ-032 R1 to R6 added 2026-10-18 (real-time bound, power interval, running estimates, in-place rendering, estimates never in results, visual design) | design 3.10 and stories updated; prompt REQ-032 and its Acceptance line carry the rules; +3.50 h MUST in M3 and +0.25 h in M6; no R1 to R6 task sits in a cut step (Z-12): D2 is AJ-091-T6 and T8 only, 2.00 h (the power forwarding task T7 moved into AJ-089-T6c, AJ-089 7.75 h, AJ-091 9.50 h); D3 = AJ-090-T5 1.50 h, C7 = AJ-093 4.00 h; AJ-135-T7 (0.25 h) records the width check; AJ-112 writes the R5 sentence | applied |
| Y-06 to Y-09 (closing pass) | AJ-021, AJ-036, AJ-050, AJ-058, AJ-107, AJ-151 | MUST stories depended on files or tasks owned by cut candidates | resolved in the story files: Linux NVML candidates moved into AJ-021-T3 (AJ-021 4.50 h, AJ-036-T2 deleted, AJ-036 2.00 h), AJ-050-T5a creates `StepFailedException` (AJ-050 no longer depends on AJ-057), AJ-107 creates the AJ-104 probe artifacts if AJ-104 is cut, AJ-151 criterion 7 writes the cloudgpu-absent record itself, AJ-058 extends `ExitCode` and leaves the README table to AJ-131 | resolved |

## 7. Risk register

Design R-01..R-13 first, then story-level risks, deduplicated. Owners: B builder session, U user, C coordinator.

| ID | Risk | Trigger | Mitigation | Owner |
|---|---|---|---|---|
| K-01 | Capacity gap: 370.65 MUST h in 12 days | gate table in 3.5 | cut order 3.4, re-planning rule 3.5 | C, U |
| R-01 | Energy counter unsupported on the laptop GPU | AJ-037 probe `energyCounterSupported: false` | sampling with `sampling-fallback`, disclosed everywhere (AJ-027); stack facts measured a working counter | B |
| R-02 | `nvmlDeviceGetPowerUsage` is a 1 s average; counter and sampling disagree | large `relativeDifference` in AJ-111 | publish with explanation in methodology (AJ-112) | B |
| R-03 | Endpoints ignore `include_usage` | tokens read `not-reported` | label stays, recorded per tier in `docs/endpoints.md` (AJ-108) | B |
| R-04 | 429 on build.nvidia.com (about 40 rpm, 36 judge calls per tier) | retries exhausted | retry client (AJ-045); judge failure never changes exit (AJ-077); re-run the failed tier alone (AJ-114-T6) | B |
| R-05 | Reasoning cannot be disabled on a tier | toggle ignored or rejected | `reasoningMode: unsupported` in file, header, README | B |
| R-06 | Ollama unloads the model between calls | latency jumps | warm-up with `keep_alive` 30 m, README tip `OLLAMA_KEEP_ALIVE=30m` | B |
| R-07 | JsonSchema.Net API differs from notes | AJ-078/079 do not compile | names from package XML docs; never move to 9.x (not OSI) | B |
| R-08 | NVML export names differ | `EntryPointNotFoundException` | deprecated throttle export as fallback, log it | B |
| R-09 | Timing-based tests flaky | overlap assertions fail intermittently | `FakeTimeProvider`, 1 s branches, overlap-only asserts | B |
| R-10 | Container check rewrites `obj/`, Git Bash mangles the mount path | AJ-036 build fails afterwards | `MSYS_NO_PATHCONV=1`, `dotnet restore` on host | B |
| R-11 | REQ-032 costs more than the design estimate (design 6 to 8 h, plan 37.00 h for AJ-089 to AJ-093 after the R1 to R6 additions) | M3 gate misses | replay first, Level 2 steps D2, D3, Level 1 step C7 | C |
| R-12 | `TypedResults.ServerSentEvents` quotes string data | AJ-091 wire scenario fails | hand-written `text/event-stream` writer (ADR-22) | B |
| R-13 | A browser tab adds GPU load during measurement | published run recorded with a browser open | published runs without a browser, recorded in the build log; video run is illustrative (RK-8) | B, U |
| K-02 | Agent Framework workflow API differs from notes | AJ-050-T5/T6 do not compile against 1.23.0 | cap discovery at 1 h, read the fork source, never upgrade | B |
| K-03 | Pinned package not on NuGet, NuGet audit NU1901..1904 under warnings-as-errors | restore or build fails | deviation rule of REQ-002, logged; Options packages missing from design 2.4 are added in AJ-033 and logged | B |
| K-04 | Price not found (nebius, nvidia) | AJ-070-T4 | no row, cost `N/A` with reason, build-log entry | B |
| K-05 | Docker Desktop not running or WSL2 has no SDK | AJ-002 or AJ-036 | recorded blocked or "not run and why" (EDGE-014 allows it) | U |
| K-06 | Test harness leaks real keys into missing-key tests | test passes only on a clean machine | `CliHostOverrides` omits environment and user secrets by default | B |
| K-07 | Keys late or Nebius lists no Nemotron model; 45-minute box expires | AJ-101 | BLOCKED report with the exact missing key, URL or model; never silent | B, U |
| K-08 | 30B local model does not fit 16 GB | `ollama ps` below 100 % | `partial-offload` flag, optional smaller verified tag, models-differ disclosure | B |
| K-09 | NIM attempt overruns | 90 min without a models response | hard stop, remove container, document, Ollama (AJ-103) | B |
| K-10 | Laptop state drifts (battery, other GPU load) | `powerSource` not `ac` | preflight AJ-110; battery runs not committed | U |
| K-11 | cloudgpu setup: preset unavailable, NIM path wrong, no FP8 profile, KV cache does not fit | AJ-125/126 errors | fallback presets, `model-free-nim` image, lower `NIM_MAX_MODEL_LEN`, record all | B, U |
| K-12 | cloudgpu counter unsupported, VM lost, spend passes the cap, VM left running, key left on VM | AJ-124, 123, 122, 128, 126 | sampling fallback; excluded `incomplete` file; guard at 4 h / USD 25; deletion request in the same message as "results copied"; memory-backed `DOCKER_CONFIG` | B, U |
| K-13 | AJ-144 (report flags, renderer, 6.25 h) is on the M6 critical path of AJ-133, AJ-134, AJ-135 | AJ-144-T4..T6 not committed by Oct 28 | front-load AJ-144 with the other result-independent M6 stories (M6 Risks row 1) | B |
| K-14 | A run file lands after the text was filled; video run overwrites the latest file | file newer than the README | rerun `AJ report --update-readme --render-content`, AJ-143, `--out artifacts/demo-run` | B |
| K-15 | Official rules page unreadable (403) | checklist says `not verified` | user pastes Rules text; box 9 reported with the reason | U |
| K-16 | Video longer than 170 s or missing; Pages URL differs; late push | no YouTube URL by 2026-10-30 07:00 UTC (latest 11:00 UTC); `curl` not 200 by Oct 29 21:00 UTC (latest Oct 30 08:00) | cut shot 5 first; minimal 65 s cut; Demo URL fallbacks; push Oct 29 20:00 UTC | U |
| K-17 | Final reviews keep returning findings; CI red on first run | three `CHANGES_REQUIRED` | fix BLOCKER and MAJOR, user sign-off for the rest; CI fix prepared as a local commit | B, U |
| K-18 | Assumption rules unconfirmed (below-baseline cost N/A, non-ok step quality, mixed-label median, counter reset `not-reported`, probe with forced counter) | architect disagrees | defaults logged in the build log; confirm before box 1 (decision D-8) | C |

## 8. The demo program

The demo is the CLI itself on the three committed tasks, from the repository root: `AJ probe --json --save`, `AJ run --tier all --dry-run`, `AJ run --tier all` (local, nvidia, nebius; 3 tasks x 3 repeats x 4 agents), `AJ calibrate`, on the VM the cloudgpu probe, run and calibrate, `AJ validate results/`, `AJ report --update-readme --render-content`, `AJ report --verify-readme`, then the illustrative live run `AJ run --tier local --task energy-explainer --repeat 1 --out artifacts/demo-run --dashboard` with `AJ dashboard --replay` on a committed file as the fallback. The dashboard shot has a deliberate visual design (REQ-032 R6: design tokens, light and dark themes, fixed header, agents, chart and footer regions, in-place push-driven rendering with running estimates labelled `estimated`), and a rehearsal screenshot `docs/report/dashboard.png` taken by the user is linked from the README when it exists. Every number shown comes from a committed results file; the live run is illustrative. The video is at most 170 s (planned 165 s), uploaded as a public YouTube video; the demo URL is the GitHub Pages path `/report/`. Defined end to end by AJ-135 (steps and acceptance), AJ-136 (script) and design section 10.

## 9. Open decisions for the user

| # | Decision | Default and recommendation | Needed by |
|---|---|---|---|
| D-1 | Package pins (design O-01, C-01): stay on Agent Framework 1.23.0, Microsoft.Extensions.AI 10.10.x, OpenTelemetry 1.18.0, or move to 1.25.0, 10.11.0, 1.20.0 | Stay. The prompt binds the pins; design verified every API at 1.23.0 and compiled it against 1.25.0 (C-12). Moving needs amendments to REQ-002 and section 4 plus an API re-check whose hours are not estimated | Oct 18, before AJ-005-T1 |
| D-2 | Approve the cut order (3.4) and the re-planning rule (3.5); Level 1 pre-approved, Level 2 and 3 per event | Approve | Oct 19 |
| D-3 | Prompt amendments (seven, section 6, including DoD box 13 / P-1 and the REQ-016 wording) | Applied by the coordinator on 2026-10-18; no approval requested | done |
| D-4 | GAP-1 (report flags and renderer without a task) | Resolved by AJ-144 (6.25 h, MUST), included in all totals | done |
| D-5 | AJ-159 copies section 3.4 verbatim (done in AJ-159 criterion 1); AJ-159 stays COULD | done | done |
| D-6 | Push on 2026-10-29 at 20:00 UTC (13:00 PDT) after the M7 gate; latest Oct 30 07:00 UTC | Evening; leaves deadline day for the form | Oct 29 |
| D-7 | Devpost track (REQ-028: user's pick; recommended "Coding and agentic engineering") and the video recording slot | Recommended track; record on Oct 29, public by Oct 30 07:00 UTC | Oct 29 |
| D-8 | Confirm story-level assumptions: below-baseline cost and ratio `N/A` (AJ-072, 074), quality of non-ok steps (AJ-076), mixed-label median (AJ-082), counter reset gives `not-reported` (AJ-026), throttle support (AJ-028) | Accept as written | Oct 25 |
| D-9 | May the session run independent stories in parallel sub-agents? Throughput unmeasured | No parallelism until the M1 gate shows the single-session velocity | Oct 21 |
| D-10 | Steps 1 to 4 and 10 and 11 of prompt section 12 on the calendar of 3.6 | Do by the dates given | see 3.6 |

Resolved audit findings (plan audit and re-audit, 2026-10-18; Y-01 to Y-09 incl. AJ-092 Ctrl+C scenario and AJ-131-T4 exit-code table): GAP-1 and D-4 (AJ-144), E-01 (AJ-131 now says ten demo steps), X-08 (`artifacts/demo-run`), X-10 (AJ-056-T4a), X-11 (AJ-100 MUST), X-12 (AJ-159 copies 3.4), S-02 (mechanics in 3.4), S-04 (AJ-038 MUST), S-16 (wording quoted in section 6), S-20 (traceability), S-21 (one calendar in 3.6).
