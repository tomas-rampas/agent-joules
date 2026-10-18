# Agent Joules - traceability

Generated 2026-10-18 from the eight story files in `docs/plan/stories/` (`Covers:` lines, scenario and task rows) and the Definition of Done in `docs/agent-joules.prompt.md`. Classes (MUST, SHOULD, COULD) come from `docs/plan/plan.md` section 3.2. The story files were not edited. Numbers below were computed with the commands listed under Counts.

Legend. Scenarios and Tasks in Tables A and B are summed over the covering stories; a story that covers two IDs counts once in each row, so the columns are not additive. Class: MUST when at least one covering story is MUST; `MUST*` when some other covering story is SHOULD or COULD (the part a cut removes, see plan.md 3.4); SHOULD or COULD when no covering story is MUST. M7 stories cover DoD boxes, not REQ or EDGE; the text `(REQ-032)` inside the AJ-150 Covers line is box wording and is not counted. REQ-016 appears in two milestones because AJ-144 (M6) lists it for the `report` flags and the content renderer.

## Table A: REQ-001 to REQ-032
| REQ | Milestone | Stories (Covers) | Scenarios | Tasks | Class |
|---|---|---|---|---|---|
| REQ-001 | M0 | AJ-002, AJ-003, AJ-004, AJ-006, AJ-007, AJ-008 | 38 | 29 | MUST |
| REQ-002 | M0 | AJ-001, AJ-005, AJ-009 | 17 | 9 | MUST |
| REQ-003 | M1 | AJ-020, AJ-021, AJ-022, AJ-023 | 24 | 19 | MUST* |
| REQ-004 | M1 | AJ-020, AJ-025, AJ-027, AJ-029, AJ-033 | 34 | 24 | MUST |
| REQ-005 | M1 | AJ-024, AJ-026, AJ-029, AJ-030, AJ-031, AJ-032 | 42 | 26 | MUST |
| REQ-006 | M1 | AJ-034, AJ-035, AJ-037 | 25 | 15 | MUST |
| REQ-007 | M2 | AJ-041 | 6 | 6 | MUST |
| REQ-008 | M2 | AJ-040, AJ-042 | 10 | 10 | MUST |
| REQ-009 | M3 | AJ-070, AJ-071, AJ-072 | 17 | 16 | MUST |
| REQ-010 | M3 | AJ-073, AJ-074, AJ-075, AJ-076 | 19 | 19 | MUST |
| REQ-011 | M2 | AJ-049, AJ-050 | 10 | 13 | MUST |
| REQ-012 | M2 | AJ-043, AJ-044, AJ-048 | 17 | 19 | MUST |
| REQ-013 | M2 | AJ-046, AJ-047, AJ-056 | 19 | 20 | MUST* |
| REQ-014 | M2 | AJ-051 | 5 | 6 | SHOULD |
| REQ-015 | M3 | AJ-078, AJ-079, AJ-080 | 21 | 18 | MUST |
| REQ-016 | M3/M6 | AJ-081, AJ-082, AJ-083, AJ-084, AJ-086, AJ-087, AJ-144 | 44 | 38 | MUST |
| REQ-017 | M2 | AJ-054, AJ-055, AJ-056, AJ-057, AJ-058 | 30 | 31 | MUST* |
| REQ-018 | M4 | AJ-100, AJ-101, AJ-107, AJ-108, AJ-114 | 32 | 35 | MUST |
| REQ-019 | M4 | AJ-102, AJ-103 | 13 | 12 | MUST* |
| REQ-020 | M4 | AJ-105, AJ-107, AJ-113, AJ-114, AJ-115 | 33 | 35 | MUST* |
| REQ-021 | M4 | AJ-106, AJ-109, AJ-110, AJ-111, AJ-113, AJ-114, AJ-115 | 50 | 46 | MUST* |
| REQ-022 | M4 | AJ-112 | 8 | 6 | MUST |
| REQ-023 | M3 | AJ-088 | 5 | 5 | SHOULD |
| REQ-024 | M1 | AJ-038 | 6 | 4 | MUST |
| REQ-025 | M6 | AJ-130, AJ-131, AJ-132, AJ-144, AJ-133, AJ-143 | 52 | 32 | MUST* |
| REQ-026 | M6 | AJ-144, AJ-134, AJ-143 | 29 | 18 | MUST* |
| REQ-027 | M6 | AJ-135, AJ-136, AJ-137, AJ-143 | 46 | 23 | MUST* |
| REQ-028 | M6 | AJ-135, AJ-138, AJ-139, AJ-140, AJ-141, AJ-142, AJ-143 | 70 | 39 | MUST* |
| REQ-029 | M0 | AJ-009, AJ-010, AJ-011, AJ-012 | 26 | 15 | MUST* |
| REQ-030 | M3 | AJ-085, AJ-086 | 11 | 10 | MUST |
| REQ-031 | M5 | AJ-120, AJ-121, AJ-125, AJ-126, AJ-127, AJ-128, AJ-129 | 51 | 50 | MUST* |
| REQ-032 | M3 | AJ-089, AJ-090, AJ-091, AJ-092, AJ-093 | 59 | 43 | MUST* |

## Table B: EDGE-001 to EDGE-019
| EDGE | Milestone | Stories (Covers) | Scenarios | Tasks | Class |
|---|---|---|---|---|---|
| EDGE-001 | M1 | AJ-035, AJ-036 | 14 | 9 | MUST* |
| EDGE-002 | M1 | AJ-027 | 6 | 4 | MUST |
| EDGE-003 | M1 | AJ-026 | 8 | 4 | MUST |
| EDGE-004 | M2 | AJ-047 | 6 | 5 | SHOULD |
| EDGE-005 | M1 | AJ-030 | 7 | 5 | MUST |
| EDGE-006 | M2 | AJ-045, AJ-057 | 11 | 13 | MUST* |
| EDGE-007 | M2 | AJ-042 | 6 | 5 | MUST |
| EDGE-008 | M2 | AJ-053 | 5 | 5 | MUST |
| EDGE-009 | M4 | AJ-104, AJ-107 | 12 | 12 | MUST* |
| EDGE-010 | M4 | AJ-102, AJ-103, AJ-115 | 21 | 18 | MUST* |
| EDGE-011 | M2 | AJ-052 | 8 | 6 | MUST |
| EDGE-012 | M3 | AJ-077 | 6 | 5 | MUST |
| EDGE-013 | M1 | AJ-028 | 6 | 5 | SHOULD |
| EDGE-014 | M1 | AJ-036 | 6 | 4 | SHOULD |
| EDGE-015 | M5 | AJ-124, AJ-127 | 16 | 15 | MUST* |
| EDGE-016 | M5 | AJ-123, AJ-127, AJ-128 | 21 | 21 | MUST* |
| EDGE-017 | M5 | AJ-122, AJ-127, AJ-128, AJ-129 | 30 | 29 | MUST* |
| EDGE-018 | M3 | AJ-091 | 10 | 10 | MUST |
| EDGE-019 | M3 | AJ-089, AJ-093 | 16 | 15 | MUST* |

## Table C: the 24 Definition of Done boxes (prompt order)
M7 story and task are the `DoD:` mapping of the M7 file (checked: 24 rows, 24 distinct boxes). Delivered by names the earlier stories that produce the artifact the box checks.

| Box | Definition of Done box | M7 story | M7 task | Delivered by (milestone: stories) |
|---|---|---|---|---|
| 1 | Every REQ and EDGE acceptance criterion is met | AJ-154 | AJ-154-T4 | M0-M6: every REQ and EDGE story (Tables A, B) |
| 2 | Project quality bar passes | AJ-155 | AJ-155-T7 | M0: AJ-008; every story's definition of done |
| 3 | No work outside the Requirements | AJ-154 | AJ-154-T5 | M0: AJ-005 (no preview packages), AJ-006 (net10.0 only) |
| 4 | `dotnet build -warnaserror -tl:off` 0 warnings, 0 errors | AJ-155 | AJ-155-T3 | M0: AJ-004, AJ-008 |
| 5 | `dotnet format --verify-no-changes` exits 0 | AJ-155 | AJ-155-T4 | M0: AJ-004 (editorconfig, analyzers), AJ-008 |
| 6 | `dotnet test` exits 0, counts in build log | AJ-155 | AJ-155-T5 | M0: AJ-007, AJ-008; M3: AJ-088 |
| 7 | `AJ probe` and `AJ run --tier all` on the laptop; probe and one run file per tier committed | AJ-150 | AJ-150-T3 | M1: AJ-037; M2: AJ-054, 055, 056; M4: AJ-114 |
| 8 | README results block generated; `report --verify-readme` exits 0 | AJ-152 | AJ-152-T3 | M3: AJ-087; M6: AJ-144 (flags), AJ-133 |
| 9 | nim-local-attempt, endpoints, methodology, devpost-checklist present with dates | AJ-153 | AJ-153-T3 | M4: AJ-100, 101, 103, 107, 112; M6: AJ-138 |
| 10 | `content/linkedin-posts.md` three variants, numbers resolved | AJ-152 | AJ-152-T5 | M6: AJ-134, AJ-144 (renderer) |
| 11 | `content/demo-script.md` shot list <= 170 s | AJ-152 | AJ-152-T6 | M6: AJ-136 |
| 12 | Secret scan clean, name and exit code recorded | AJ-155 | AJ-155-T6 | M0: AJ-010, AJ-011 |
| 13 | `git log` shows commits starting with the story ID followed by the REQ or EDGE IDs (standing order 5) | AJ-156 | AJ-156-T3 | every story's commit (standing order 5) |
| 14 | Nothing pushed by the builder; `git push` command in the checklist | AJ-156 | AJ-156-T4 | M6: AJ-139 |
| 15 | Every REQ/EDGE with a named unit test has a passing `Spec` trait test; per-ID results recorded | AJ-154 | AJ-154-T3 | every story's Test mapping; M3: AJ-088 |
| 16 | `docs/report/index.html` generated, no `<script`, `docs/.nojekyll` | AJ-152 | AJ-152-T4 | M3: AJ-085, AJ-086 |
| 17 | Checklist has `Demo URL:` and headings Registration, Tracks, Judging criteria, Credits | AJ-153 | AJ-153-T4 | M6: AJ-138, AJ-139 |
| 18 | `docs/devpost-story.md` present, zero `[[FILL:`, no Fill-after-build heading | AJ-153 | AJ-153-T5 | M6: AJ-140 |
| 19 | `docs/devpost-feedback.md` no `owner: builder`; user placeholders listed in checklist | AJ-153 | AJ-153-T6 | M6: AJ-142 |
| 20 | Run files carry `run.repeat` >= 3 for every tier | AJ-150 | AJ-150-T4 | M2: AJ-056; M4: AJ-106, AJ-114; M5: AJ-127 |
| 21 | `results/*-cloudgpu.json` and `results/probe-*-cloudgpu.json` present, schema-valid | AJ-151 | AJ-151-T3 | M5: AJ-125, AJ-127 |
| 22 | `docs/cloudgpu-run.md` present, Cost <= USD 30, Teardown confirmed | AJ-151 | AJ-151-T4 | M5: AJ-128 |
| 23 | README results block contains `cloudgpu` | AJ-151 | AJ-151-T5 | M5: AJ-129; M6: AJ-133 |
| 24 | `run --tier local --dashboard --dry-run` prints the URL; `dashboard --replay` emits one event per step | AJ-150 | AJ-150-T7 | M3: AJ-089, 090, 091 (T1..T5), 092 |

## Table D: stories per milestone and their Covers list
| Story | M | Class | Hours | Scenarios | Tasks | Covers |
|---|---|---|---|---|---|---|
| AJ-001 | M0 | MUST | 0.50 | 4 | 2 | REQ-002 |
| AJ-002 | M0 | MUST | 1.25 | 6 | 5 | REQ-001 |
| AJ-003 | M0 | MUST | 0.50 | 4 | 2 | REQ-001 |
| AJ-004 | M0 | MUST | 2.00 | 8 | 6 | REQ-001 |
| AJ-005 | M0 | MUST | 1.00 | 6 | 3 | REQ-002 |
| AJ-006 | M0 | MUST | 2.00 | 8 | 6 | REQ-001 |
| AJ-007 | M0 | MUST | 2.00 | 6 | 6 | REQ-001 |
| AJ-008 | M0 | MUST | 1.50 | 6 | 4 | REQ-001 |
| AJ-009 | M0 | MUST | 1.25 | 7 | 4 | REQ-002, REQ-029 |
| AJ-010 | M0 | MUST | 1.75 | 7 | 4 | REQ-029 |
| AJ-011 | M0 | MUST | 1.25 | 6 | 4 | REQ-029 |
| AJ-012 | M0 | SHOULD | 1.00 | 6 | 3 | REQ-029 |
| AJ-020 | M1 | MUST | 2.50 | 5 | 5 | REQ-003, REQ-004 |
| AJ-021 | M1 | MUST | 4.50 | 6 | 5 | REQ-003 |
| AJ-022 | M1 | MUST | 3.00 | 7 | 5 | REQ-003 |
| AJ-023 | M1 | SHOULD | 2.00 | 6 | 4 | REQ-003 |
| AJ-024 | M1 | MUST | 2.00 | 8 | 4 | REQ-005 |
| AJ-025 | M1 | MUST | 1.50 | 7 | 3 | REQ-004 |
| AJ-026 | M1 | MUST | 2.50 | 8 | 4 | REQ-005, EDGE-003 |
| AJ-027 | M1 | MUST | 2.00 | 6 | 4 | REQ-004, EDGE-002 |
| AJ-028 | M1 | SHOULD | 2.75 | 6 | 5 | EDGE-013 |
| AJ-029 | M1 | MUST | 5.25 | 8 | 6 | REQ-004, REQ-005 |
| AJ-030 | M1 | MUST | 2.50 | 7 | 5 | REQ-005, EDGE-005 |
| AJ-031 | M1 | MUST | 1.75 | 6 | 3 | REQ-005 |
| AJ-032 | M1 | MUST | 2.00 | 5 | 4 | REQ-005 |
| AJ-033 | M1 | MUST | 2.75 | 8 | 6 | REQ-004 |
| AJ-034 | M1 | MUST | 4.50 | 9 | 6 | REQ-006 |
| AJ-035 | M1 | MUST | 3.50 | 8 | 5 | REQ-006, EDGE-001 |
| AJ-036 | M1 | SHOULD | 2.00 | 6 | 4 | EDGE-001, EDGE-014 |
| AJ-037 | M1 | MUST | 2.00 | 8 | 4 | REQ-006 |
| AJ-038 | M1 | MUST | 2.00 | 6 | 4 | REQ-024 |
| AJ-040 | M2 | MUST | 2.75 | 4 | 5 | REQ-008 |
| AJ-041 | M2 | MUST | 4.75 | 6 | 6 | REQ-007 |
| AJ-042 | M2 | MUST | 3.00 | 6 | 5 | REQ-008, EDGE-007 |
| AJ-043 | M2 | MUST | 4.00 | 5 | 6 | REQ-012 |
| AJ-044 | M2 | MUST | 5.75 | 7 | 7 | REQ-012 |
| AJ-045 | M2 | MUST | 5.00 | 6 | 7 | EDGE-006 |
| AJ-046 | M2 | MUST | 2.75 | 5 | 5 | REQ-013 |
| AJ-047 | M2 | SHOULD | 4.00 | 6 | 5 | REQ-013, EDGE-004 |
| AJ-048 | M2 | MUST | 3.25 | 5 | 6 | REQ-012 |
| AJ-049 | M2 | MUST | 3.00 | 5 | 5 | REQ-011 |
| AJ-050 | M2 | MUST | 5.75 | 5 | 8 | REQ-011 |
| AJ-051 | M2 | SHOULD | 4.25 | 5 | 6 | REQ-014 |
| AJ-052 | M2 | MUST | 5.00 | 8 | 6 | EDGE-011 |
| AJ-053 | M2 | MUST | 3.00 | 5 | 5 | EDGE-008 |
| AJ-054 | M2 | MUST | 4.00 | 6 | 5 | REQ-017 |
| AJ-055 | M2 | MUST | 4.75 | 6 | 5 | REQ-017 |
| AJ-056 | M2 | MUST | 10.00 | 8 | 10 | REQ-013, REQ-017 |
| AJ-057 | M2 | SHOULD | 3.50 | 5 | 6 | REQ-017, EDGE-006 |
| AJ-058 | M2 | SHOULD | 3.25 | 5 | 5 | REQ-017 |
| AJ-070 | M3 | MUST | 3.50 | 5 | 5 | REQ-009 |
| AJ-071 | M3 | MUST | 2.75 | 5 | 5 | REQ-009 |
| AJ-072 | M3 | MUST | 3.50 | 7 | 6 | REQ-009 |
| AJ-073 | M3 | MUST | 2.00 | 4 | 4 | REQ-010 |
| AJ-074 | M3 | MUST | 2.00 | 5 | 4 | REQ-010 |
| AJ-075 | M3 | MUST | 2.50 | 4 | 4 | REQ-010 |
| AJ-076 | M3 | MUST | 5.00 | 6 | 7 | REQ-010 |
| AJ-077 | M3 | MUST | 3.00 | 6 | 5 | EDGE-012 |
| AJ-078 | M3 | MUST | 5.50 | 9 | 6 | REQ-015 |
| AJ-079 | M3 | MUST | 5.75 | 7 | 7 | REQ-015 |
| AJ-080 | M3 | MUST | 2.75 | 5 | 5 | REQ-015 |
| AJ-081 | M3 | MUST | 2.50 | 6 | 4 | REQ-016 |
| AJ-082 | M3 | MUST | 3.75 | 5 | 5 | REQ-016 |
| AJ-083 | M3 | MUST | 5.25 | 7 | 6 | REQ-016 |
| AJ-084 | M3 | MUST | 4.50 | 6 | 6 | REQ-016 |
| AJ-085 | M3 | MUST | 3.50 | 6 | 5 | REQ-030 |
| AJ-086 | M3 | MUST | 3.75 | 5 | 5 | REQ-016, REQ-030 |
| AJ-087 | M3 | MUST | 3.50 | 5 | 5 | REQ-016 |
| AJ-088 | M3 | SHOULD | 3.00 | 5 | 5 | REQ-023 |
| AJ-089 | M3 | MUST | 6.75 | 11 | 9 | REQ-032, EDGE-019 |
| AJ-090 | M3 | MUST | 9.25 | 22 | 10 | REQ-032 |
| AJ-091 | M3 | MUST | 10.50 | 10 | 10 | REQ-032, EDGE-018 |
| AJ-092 | M3 | MUST | 6.50 | 11 | 8 | REQ-032 |
| AJ-093 | M3 | SHOULD | 4.00 | 5 | 6 | REQ-032, EDGE-019 |
| AJ-100 | M4 | MUST | 3.75 | 7 | 6 | REQ-018 |
| AJ-101 | M4 | MUST | 3.30 | 6 | 6 | REQ-018 |
| AJ-102 | M4 | SHOULD | 2.25 | 7 | 5 | REQ-019, EDGE-010 |
| AJ-103 | M4 | MUST | 3.75 | 6 | 7 | REQ-019, EDGE-010 |
| AJ-104 | M4 | SHOULD | 2.50 | 6 | 5 | EDGE-009 |
| AJ-105 | M4 | MUST | 3.75 | 6 | 7 | REQ-020 |
| AJ-106 | M4 | MUST | 5.00 | 10 | 8 | REQ-021 |
| AJ-107 | M4 | MUST | 4.00 | 6 | 7 | REQ-018, REQ-020, EDGE-009 |
| AJ-108 | M4 | MUST | 4.50 | 6 | 7 | REQ-018 |
| AJ-109 | M4 | MUST | 4.25 | 8 | 7 | REQ-021 |
| AJ-110 | M4 | MUST | 1.45 | 6 | 6 | REQ-021 |
| AJ-111 | M4 | MUST | 1.35 | 5 | 4 | REQ-021 |
| AJ-112 | M4 | MUST | 4.25 | 8 | 6 | REQ-022 |
| AJ-113 | M4 | COULD | 2.25 | 6 | 6 | REQ-020, REQ-021 |
| AJ-114 | M4 | MUST | 6.10 | 7 | 9 | REQ-018, REQ-020, REQ-021 |
| AJ-115 | M4 | MUST | 3.00 | 8 | 6 | REQ-020, REQ-021, EDGE-010 |
| AJ-120 | M5 | MUST | 1.60 | 5 | 5 | REQ-031 |
| AJ-121 | M5 | MUST | 3.50 | 8 | 6 | REQ-031 |
| AJ-122 | M5 | MUST | 4.00 | 7 | 7 | EDGE-017 |
| AJ-123 | M5 | SHOULD | 3.75 | 6 | 7 | EDGE-016 |
| AJ-124 | M5 | SHOULD | 3.50 | 8 | 7 | EDGE-015 |
| AJ-125 | M5 | MUST | 3.85 | 8 | 8 | REQ-031 |
| AJ-126 | M5 | MUST | 4.75 | 7 | 9 | REQ-031 |
| AJ-127 | M5 | MUST | 5.20 | 8 | 8 | REQ-031, EDGE-015, EDGE-016, EDGE-017 |
| AJ-128 | M5 | MUST | 2.80 | 7 | 6 | REQ-031, EDGE-016, EDGE-017 |
| AJ-129 | M5 | SHOULD | 4.00 | 8 | 8 | REQ-031, EDGE-017 |
| AJ-130 | M6 | MUST | 2.25 | 7 | 5 | REQ-025 |
| AJ-131 | M6 | MUST | 3.75 | 11 | 6 | REQ-025 |
| AJ-132 | M6 | MUST | 3.50 | 7 | 6 | REQ-025 |
| AJ-144 | M6 | MUST | 6.25 | 10 | 7 | REQ-016, REQ-025, REQ-026 |
| AJ-133 | M6 | MUST | 1.75 | 6 | 4 | REQ-025 |
| AJ-134 | M6 | MUST | 3.50 | 8 | 7 | REQ-026 |
| AJ-135 | M6 | MUST | 3.50 | 15 | 6 | REQ-027, REQ-028 |
| AJ-136 | M6 | MUST | 3.50 | 11 | 6 | REQ-027 |
| AJ-137 | M6 | COULD | 5.25 | 9 | 7 | REQ-027 |
| AJ-138 | M6 | MUST | 4.00 | 11 | 6 | REQ-028 |
| AJ-139 | M6 | MUST | 2.75 | 8 | 5 | REQ-028 |
| AJ-140 | M6 | MUST | 4.75 | 9 | 7 | REQ-028 |
| AJ-141 | M6 | MUST | 1.75 | 7 | 4 | REQ-028 |
| AJ-142 | M6 | MUST | 4.50 | 9 | 7 | REQ-028 |
| AJ-143 | M6 | SHOULD | 4.00 | 11 | 4 | REQ-025, REQ-026, REQ-027, REQ-028 |
| AJ-150 | M7 | MUST | 3.00 | 15 | 7 | DoD boxes 7, 20, 24 |
| AJ-151 | M7 | MUST | 3.25 | 10 | 7 | DoD boxes 21, 22, 23 |
| AJ-152 | M7 | MUST | 3.00 | 9 | 7 | DoD boxes 8, 10, 11, 16 |
| AJ-153 | M7 | MUST | 2.75 | 9 | 7 | DoD boxes 9, 17, 18, 19 |
| AJ-154 | M7 | MUST | 5.50 | 8 | 7 | DoD boxes 1, 3, 15 |
| AJ-155 | M7 | MUST | 4.00 | 11 | 9 | DoD boxes 2, 4, 5, 6, 12 |
| AJ-156 | M7 | MUST | 5.00 | 9 | 8 | DoD boxes 13, 14 |
| AJ-157 | M7 | SHOULD | 3.75 | 8 | 7 | DoD: none (release, submission, schedule) |
| AJ-158 | M7 | SHOULD | 4.75 | 9 | 7 | DoD: none (release, submission, schedule) |
| AJ-159 | M7 | COULD | 3.00 | 9 | 6 | DoD: none (release, submission, schedule) |

## Counts
| Milestone | Stories | Scenarios | Positive / negative (tagged) | Tasks | Hours | MUST h | Conflict lines | Resolved lines |
|---|---|---|---|---|---|---|---|---|
| M0 | 12 | 74 | 34 / 40 | 49 | 16.00 | 15.00 | 1 | 0 |
| M1 | 19 | 130 | 60 / 70 | 86 | 51.00 | 44.25 | 5 | 0 |
| M2 | 19 | 108 | 51 / 57 | 113 | 81.75 | 66.75 | 4 | 0 |
| M3 | 24 | 167 | 75 / 92 | 142 | 105.00 | 98.00 | 9 | 0 |
| M4 | 16 | 108 | 44 / 64 | 102 | 55.45 | 48.45 | 1 | 1 |
| M5 | 10 | 72 | 30 / 42 | 71 | 36.95 | 25.70 | 2 | 1 |
| M6 | 15 | 139 | 59 / 80 | 87 | 55.00 | 45.75 | 2 | 2 |
| M7 | 10 | 97 | 41 / 56 | 72 | 38.00 | 26.50 | 1 | 0 |
| Total | 125 | 895 | 394 / 501 | 722 | 439.15 | 370.40 | 25 | 4 |

Classes: MUST 104 stories 370.40 h; SHOULD 18 stories 58.25 h; COULD 3 stories 10.50 h. The totals include 2.60 h of USER ACTION tasks (M4 0.75 h, M5 1.60 h, M6 0.25 h); builder-only total 436.55 h (M4 54.70 h, M5 35.35 h, M6 54.75 h without user actions). Conflict lines: 24 sit inside a story, 1 (M7) sits in the milestone preamble; 4 further lines are `Resolved:` lines. Every story carries `@AJ-S<nn>` and `@REQ`/`@EDGE` tags.

Commands (run in `docs/plan/stories`; the result is the number after `->`):

```
grep -c '^### AJ-' M*.md                                   # per file 12 19 19 24 16 10 15 10; sum 125
grep -h '^### AJ-' M*.md | sed 's/:.*//' | sort | uniq -d | wc -l                       # -> 0 duplicate story IDs
grep -hE '^[[:space:]]*Scenario( Outline)?:' M*.md | wc -l                              # -> 895 scenarios
grep -hE '^[[:space:]]*Scenario( Outline)?:' M*.md | sed -E 's/^[[:space:]]*Scenario( Outline)?:[[:space:]]*//' | sort | uniq -d | wc -l   # -> 0 duplicate names
grep -h '@positive' M*.md | wc -l ; grep -h '@negative' M*.md | wc -l                  # -> 394 ; 501
grep -h '^| AJ-[0-9]*-T' M*.md | wc -l                                                   # -> 722 tasks
grep -h '^| AJ-[0-9]*-T' M*.md | awk -F'|' '{s+=$(NF-2)} END{printf "%.2f\n", s}'      # -> 439.15 h
grep -h '^| AJ-[0-9]*-T' M*.md | awk -F'|' '$(NF-2)+0>2{n++} END{print n+0}'            # -> 0 tasks above 2 h
grep -h '^Conflict:' M*.md | wc -l                                                       # -> 25 (and grep -c '^Resolved:' adds 4)
grep -h '^Covers:' M[0-6]-*.md | grep -oE 'REQ-[0-9]{3}' | sort -u | wc -l              # -> 32
grep -h '^Covers:' M[0-6]-*.md | grep -oE 'EDGE-[0-9]{3}' | sort -u | wc -l             # -> 19
comm -23 <(seq -f 'REQ-%03g' 1 32) <(grep -h '^Covers:' M[0-6]-*.md | grep -oE 'REQ-[0-9]{3}' | sort -u) | wc -l    # -> 0 missing (EDGE analogous, seq 1 19 -> 0)
grep -cE '^\| [0-9]+ \| .* \| AJ-15[0-9] \| AJ-15[0-9]-T[0-9]+ \|$' M7-*.md              # -> 24 DoD mapping rows (24 distinct boxes)
grep -c '^- \[ \]' ../../agent-joules.prompt.md                                         # -> 24 boxes in the prompt
grep -lP '[^\x00-\x7F]' M*.md | wc -l ; grep -lc $'\r' M*.md | wc -l                    # -> 0 ; 0
```

Per-milestone coverage was also compared with the milestone table in `docs/plan/plan.md` section 2 by script: 0 missing and 0 extra REQ or EDGE IDs in every milestone. REQ-016 is covered in two milestones: M3 (AJ-081 to AJ-087) and M6 (AJ-144, the `report` flags and the content renderer); no other ID is. Hours per story use the Estimate column of the task rows; the awk command above takes every task row, including ones such as `AJ-006-T3b`, `AJ-050-T5a`, `AJ-056-T4a`, `AJ-089-T6b` and `AJ-091-T4b`.

Check results: duplicate story IDs 0; duplicate scenario names 0; tasks above 2 hours 0; REQ without a story 0 (no GAP row in Table A); EDGE without a story 0 (no GAP row in Table B); DoD boxes without an M7 story and task 0 (Table C, 24 of 24); non-ASCII characters or CR in the story files 0.

Resolved since the first version: GAP-1 (AJ-144), P-1, P-2, E-01, X-10, X-11, Y-01, Y-06 to Y-09. Added 2026-10-18: REQ-032 rules R1 to R6 (AJ-089, AJ-090, AJ-091, AJ-135, AJ-131; design 3.10). Open: none from this traceability pass.
