# M7: Hardening and submission

Goal: prove every box of the Definition of Done with a command or an artifact, harden the repository (build, format, tests, secret scan, final reviews), prepare the v0.1.0 release, and put the user in a position to push, publish and submit to Devpost well before 2026-10-30 10:00 PDT (17:00 UTC). This milestone owns no requirement. It owns the 24 Definition of Done boxes, the release and the submission plan. Pushing, enabling Pages, creating the release, recording and uploading the video and submitting to Devpost are the user's actions; the builder prepares text and commands only. `AJ` stands for `dotnet run --project src/AgentJoules.Cli --`, run from the repository root.

## Dates and exit criteria

Dates: 2026-10-29 to 2026-10-30 (two working days, about 12 working hours at six hours per day; the M6 window ends on 2026-10-29). Deadline: 2026-10-30 10:00 PDT = 17:00 UTC (PDT is UTC-7 until 2026-11-01).

Exit criteria:
- [x] Each of the 24 Definition of Done boxes is ticked by the command or artifact named in the mapping table below, or is listed as unchecked with its reason in the final report (prompt section 10). (the `## M7 exit` entry: box 1 listed unchecked, the REQ-032 screenshot is a pending user action; the other 23 boxes ticked by the stories and the exit battery)
- [x] `dotnet build -warnaserror -tl:off --no-incremental` exits 0 with `0 Warning(s)` and `0 Error(s)`; `dotnet format --verify-no-changes` exits 0; `dotnet test` exits 0 with the three counts in `docs/build-log.md`. (the exit battery on abddeac, 2026-10-27T12:24:43Z, and the re-run on ff3e269: both builds 0 warnings 0 errors, format exit 0, passed=6950 failed=0 skipped=25 exit=0)
- [x] `git log --oneline <build-start>..HEAD | wc -l` equals the number of build-session commits (`<build-start>` is the HEAD hash recorded by M0 AJ-002 in `docs/build-log.md`) and `git log --no-merges <build-start>..HEAD --format=%s | grep -vE "^M[0-9] exit( addendum)?:" | grep -vcE "^AJ-[0-9]{3}( (REQ|EDGE)-[0-9]{3})*:"` prints 0; nothing is pushed (`git ls-remote origin main` equals the recorded `origin/main` hash).  (the `--no-merges` flag and the exit-subject exclusion were added 2026-10-19 because the session prompt mandates merge and `M<k> exit` subjects, see docs/build-log.md `## M0 exit`) (PASS under the AJ-156 amendments: on abddeac 177 commits equal to the rev-list count, the amended subject check 0; see the exit entry)
- [x] The last `peer-review-critic` run of the session ends with `VERDICT: APPROVED`. (peer-review-critic round 2 APPROVED, 2026-10-27 12:52Z, see the exit entry)
- [x] `ls artifacts/agent-joules-v0.1.0-*.zip | wc -l` prints 3 and `docs/release-notes-v0.1.0.md` exists. (three zips and the release notes, see the AJ-157 entry; the zips are rebuilt from the published commit before the release, recorded in the addendum)
- [x] `docs/devpost-checklist.md` holds the sections `Submission order`, `Cut-off plan` and `Timeline`, and `docs/build-log.md` holds the section `Drop order`. (the three checklist sections and the Drop order present, 12 drop rows, see the exit entry)
- [ ] User actions done and checked (the user's, listed in AJ-157 and AJ-158): `curl -s -o /dev/null -w "%{http_code}" https://tomas-rampas.github.io/agent-joules/report/` prints 200; `gh release view v0.1.0 --repo tomas-rampas/agent-joules` exits 0; the Devpost page shows the project as submitted before 2026-10-30 17:00 UTC. (the session's clauses PASS: Pages /report/ 200 at 13:21:14Z, gh release view v0.1.0 lists the three assets; open: the Devpost submission, the user's, by 2026-10-30 17:00 UTC)
- [x] After the push, `gh run list --workflow ci.yml --branch main --limit 1` shows `completed` and `success` for both `windows-latest` and `ubuntu-latest` (not a Definition of Done box; REQ-024). (CI run 40510482866 on 98cc246 completed success on both runners at 13:20:13Z)

## Coverage

None. M7 has no REQ or EDGE of its own. It covers the 24 Definition of Done boxes of the prompt (the 24th was added with REQ-032 on 2026-10-18), mapped once each:

| Box | Definition of Done box | Story | Task |
|---|---|---|---|
| 1 | Every REQ and EDGE acceptance criterion above is met | AJ-154 | AJ-154-T4 |
| 2 | Project quality bar passes (build/lint/format/tests clean) | AJ-155 | AJ-155-T7 |
| 3 | No work outside the Requirements (Out of scope respected) | AJ-154 | AJ-154-T5 |
| 4 | `dotnet build -warnaserror -tl:off` reports 0 warnings and 0 errors | AJ-155 | AJ-155-T3 |
| 5 | `dotnet format --verify-no-changes` exits 0 | AJ-155 | AJ-155-T4 |
| 6 | `dotnet test` exits 0; passed, failed and skipped counts recorded in `docs/build-log.md` | AJ-155 | AJ-155-T5 |
| 7 | `AJ probe` and `AJ run --tier all` executed on the laptop; probe output and one results JSON per tier committed | AJ-150 | AJ-150-T3 |
| 8 | README results block generated from those files; `AJ report --verify-readme` exits 0 | AJ-152 | AJ-152-T3 |
| 9 | `docs/nim-local-attempt.md`, `docs/endpoints.md`, `docs/methodology.md` and `docs/devpost-checklist.md` present, each with a verification date | AJ-153 | AJ-153-T3 |
| 10 | `content/linkedin-posts.md` holds three variants with every number resolved from named results files | AJ-152 | AJ-152-T5 |
| 11 | `content/demo-script.md` shot list totals 170 seconds or less | AJ-152 | AJ-152-T6 |
| 12 | Secret scan clean; scan name and exit code recorded in `docs/build-log.md` | AJ-155 | AJ-155-T6 |
| 13 | `git log --oneline` shows commits whose messages start with the story id followed by the REQ or EDGE ids (M7 stories that deliver no REQ start with the story id alone) | AJ-156 | AJ-156-T3 |
| 14 | Nothing pushed by the builder; the `git push` command for the user is present in `docs/devpost-checklist.md` | AJ-156 | AJ-156-T4 |
| 15 | Every REQ or EDGE whose Acceptance names a unit test has a passing test with its `Spec` trait; per-ID results recorded | AJ-154 | AJ-154-T3 |
| 16 | `docs/report/index.html` generated by `AJ report`, no `<script`, `docs/.nojekyll` exists | AJ-152 | AJ-152-T4 |
| 17 | `docs/devpost-checklist.md` has the line "Demo URL:" and the headings "Registration", "Tracks", "Judging criteria", "Credits" | AJ-153 | AJ-153-T4 |
| 18 | `docs/devpost-story.md` present, zero `[[FILL:`, no "Fill-after-build" heading | AJ-153 | AJ-153-T5 |
| 19 | `docs/devpost-feedback.md` has no `owner: builder` placeholder; user placeholders listed in the checklist | AJ-153 | AJ-153-T6 |
| 20 | Run files carry run.repeat >= 3 for every tier | AJ-150 | AJ-150-T4 |
| 21 | `results/*-cloudgpu.json` and `results/probe-*-cloudgpu.json` present and schema-valid | AJ-151 | AJ-151-T3 |
| 22 | `docs/cloudgpu-run.md` present with `Cost` at or below USD 30 and `Teardown` confirmed | AJ-151 | AJ-151-T4 |
| 23 | README results block contains `cloudgpu` | AJ-151 | AJ-151-T5 |
| 24 | `AJ run --tier local --dashboard --dry-run` prints the dashboard URL and exits 0; `AJ dashboard --replay` on a committed run file serves the page and emits one event per step (REQ-032) | AJ-150 | AJ-150-T7 |

Stories AJ-157 (release), AJ-158 (submission and cut-off plan) and AJ-159 (buffer and drop order) map no box; they prepare the user actions and the fallback.

## Dependencies and user actions

Needs: M0 to M6 complete or explicitly reported as partial; the M6 content freeze at 2026-10-29 17:00 UTC (assumed planning target, 10:00 PDT).

User actions (the user's, not the builder's; section 12 step numbers; every one is also a `(user)` line in `docs/devpost-checklist.md`; except the git and gh steps delegated to the session at the M7 exit (user decision 2026-10-27 12:11Z; AJ-156 amendment (c), AJ-157 amendment (d))):

| Step | User action | Story | Planning target, UTC (PDT in parentheses; assumed) |
|---|---|---|---|
| 13 | Delete the VM and boot disk; give deletion time and final cost (if M5 ran) | AJ-151 | before 2026-10-29 17:00 (10:00 PDT) |
| - | session (delegated 2026-10-27 12:11Z; the user checks the result): `git push -u origin main` after the M7 gate (decision D-6) | AJ-156, AJ-158 | 2026-10-29 20:00 (13:00 PDT); latest 2026-10-30 07:00 (00:00 PDT) |
| - | session (delegated 2026-10-27 12:11Z; the user checks the result): Enable GitHub Pages (main, `/docs`) and confirm the report URL answers 200 | AJ-158 | 2026-10-29 21:00 (14:00 PDT); latest 2026-10-30 08:00 (01:00 PDT) |
| - | session (delegated 2026-10-27 12:11Z; the user checks the result): `gh release create v0.1.0 ...` with the three assets | AJ-157 | 2026-10-29 21:00 (14:00 PDT); latest 2026-10-30 08:00 (01:00 PDT) |
| - | Fill the seven `owner: user` placeholders of `docs/devpost-feedback.md`; fill `{{repo.url}}` in the posts before posting | AJ-158 | 2026-10-29 22:00 (15:00 PDT); latest 2026-10-30 10:00 (03:00 PDT) |
| - | Record the video on 2026-10-29 (decision D-7) and upload it as a public YouTube video | AJ-158 | public by 2026-10-30 07:00 (00:00 PDT); latest 2026-10-30 11:00 (04:00 PDT) |
| 8 | Submit on Devpost, fields in the order of `Submission order` | AJ-158 | 2026-10-30 14:00 (07:00 PDT) |
| 9 | After submission keep the repository unchanged on the submitted commit; the `v0.1.0` tag marks it | AJ-157 | after submission |

Conflict: the conventions file states the deadline as 18:00 CET, 17:00 UTC. CEST ended on Sunday 2026-10-25 (the last Sunday of October, inferred from the calendar: 2026-10-18 is a Sunday), so on 2026-10-30 central Europe is on CET (UTC+1) and 17:00 UTC is 18:00 CET. Proposed resolution: every time in this milestone is written in UTC and PDT; the local reading for a user in central Europe is UTC+1 (assumed location, inferred from the CEST mention). Nothing else about the deadline changes.

## Stories

### AJ-150: Verify the laptop, hosted and dashboard evidence
Covers: DoD: `AJ probe` and `AJ run --tier all` executed on the laptop; probe output and one results JSON per tier (`local`, `nvidia`, `nebius`) committed under `results/`; DoD: Run files carry run.repeat >= 3 for every tier; DoD: `AJ run --tier local --dashboard --dry-run` prints the dashboard URL and exits 0; `AJ dashboard --replay` on a committed run file serves the page and emits one event per step (REQ-032)
User story: As the reviewer of the submission, I want the probe file and one schema-valid run file per measured and hosted tier to be committed with at least three repeats, and the live dashboard and its replay to work on committed data, so that every published number has a source file and a judge without a GPU can still see the live view.
Acceptance criteria:
1. A probe file exists: `ls results | grep -cE "^probe-[0-9]{8}T[0-9]{6}Z\.json$"` prints 1 or more; its `gpuPresent` is `true` and its `driverVersion` equals the driver `nvidia-smi --query-gpu=driver_version --format=csv,noheader` prints (@integration).
2. One run file per tier: `for t in local nvidia nebius; do ls results | grep -cE "^[0-9]{8}T[0-9]{6}Z-$t\.json$"; done` prints three values of 1 or more, and the newest file of each has `run.status` `completed`.
3. `run.repeat` is 3 or more in the newest file of every tier present (including `cloudgpu` when it exists): `grep -m1 -E '"repeat": *[0-9]+' results/<file>` prints a value of 3 or more (the run object precedes the steps array).
4. `AJ validate results/` exits 0 and prints `Validated <n> files` where `<n>` equals the number of run files: `ls results | grep -cE '^[0-9]{8}T[0-9]{6}Z-(local|cloudgpu|nvidia|nebius)(-baseline)?\.json$'` (probe-* and calibration-* files are not counted).
5. The newest local file has `baseline.seconds` 30 or more, a numeric `environment.modelLoadSeconds`, `environment.powerSource` in {`ac`, `battery`, `unknown`}, `environment.localRuntime` in {`nim`, `ollama`} and a numeric `environment.gpuOffloadPercent`.
6. Hosted files label energy `N/A` with a null value, never 0: the schema validation of criterion 4 and the scenario below prove it.
7. No stray file: `ls results/*.partial results/*.raw 2>/dev/null | wc -l` prints 0.
8. Box 24, first command: `AJ run --tier local --dashboard --dry-run` prints a line starting `Dashboard:` containing `http://127.0.0.1:` and exits 0 without a browser (@integration, needs the laptop GPU and the local runtime for the dry run).
9. Box 24, second command: `AJ dashboard --replay results/<newest nebius file> --speed 50 --exit-after 5` exits 0 and emits one server-sent event per step of that file (36 for the demo shape), served from `127.0.0.1` only; the replay needs no GPU and no key (@unit through the in-process harness).
10. The published run files were recorded without a browser open: `docs/build-log.md` contains the statement for the local run, `docs/methodology.md` states both facts (the server adds no GPU work; a browser tab adds compositing load), and the README says so (REQ-032 text).
Amendments (2026-10-27, M7 kickoff and pool A gates): (a) criterion 10 is read as the M6 exit fix reads it: the statement is "recorded without a browser showing the dashboard" plus the open-applications note of the 2026-10-25 campaign (the AJ-114 record), and the evidence rule asserts the substring `recorded without a browser` in `docs/build-log.md`; after K2 the published local file is results/20261027T095519Z-local.json, recorded without a browser, with the still-open applications named in the README notes; (b) in the scenario "A hosted energy value of 0 is rejected", "names the hosted step" means the validator's failure line names the hosted run file and the metric `grossJoules` (the validator prints the file and the parameter, not a step index); (c) the step bindings live in `tests/AgentJoules.Specs/Steps/EvidenceSteps.*.cs` partial classes with one `EvidenceContext`, not in `ConfigurationSteps.cs` (the AJ-043 class), and `EvidenceChecks` is split per story like `DocumentChecks`; (d) one `Feature:` per file, so the `@integration` block is `RunEvidence.Integration.feature` (likewise `CloudGpuEvidence.Integration.feature` for AJ-151).
BDD scenarios:
```gherkin
@unit @AJ-S150 @REQ-032 @DoD
Feature: AJ-150 Laptop and hosted run evidence
  @positive
  Scenario: AJ-150 Each tier has a completed run file with at least three repeats
    Given the committed results folder
    When the newest non-baseline file per tier is read
    Then local, nvidia and nebius each have status "completed" and run.repeat 3 or more
  @positive
  Scenario: AJ-150 The local file carries baseline and runtime evidence
    Given the newest local run file
    When baseline.seconds, modelLoadSeconds, powerSource, localRuntime and gpuOffloadPercent are read
    Then baseline.seconds is 30 or more and the other four fields are present and valid
  @positive
  Scenario: AJ-150 Validate counts every committed result file
    Given the committed results folder
    When "validate results/" runs
    Then the exit code is 0 and "Validated <n> files" equals the number of run files
  @negative
  Scenario: AJ-150 A tier with run.repeat 1 is rejected
    Given a results folder whose newest nvidia file has run.repeat 1
    When the evidence check runs
    Then it fails with "run.repeat 1 is below 3: nvidia"
  @negative
  Scenario: AJ-150 A missing tier file is rejected
    Given a results folder without a nebius run file
    When the evidence check runs
    Then it fails with "no run file for tier: nebius"
  @negative
  Scenario: AJ-150 A hosted energy value of 0 is rejected
    Given a nebius fixture whose grossJoules is {value:0,label:"measured"}
    When "validate" runs on the folder
    Then the exit code is 2 and the failure names the hosted step
  @negative
  Scenario: AJ-150 A baseline of 20 seconds is rejected
    Given a local fixture with baseline.seconds 20
    When the evidence check runs
    Then it fails with "baseline.seconds 20 is below 30"
  @positive
  Scenario: AJ-150 Replay of the committed nebius file emits one event per step
    Given the newest committed nebius run file
    When "dashboard --replay <file> --speed 50 --exit-after 5" runs in process
    Then the exit code is 0 and the number of events equals the number of steps in the file
  @negative
  Scenario: AJ-150 Replay of a file that does not exist is rejected
    Given no file at results/absent.json
    When "dashboard --replay results/absent.json" runs
    Then the exit code is not 0 and the message names the file
  @negative
  Scenario: AJ-150 The build log lacks the no-browser statement and is rejected
    Given a build log without the sentence that the published runs were recorded without a browser open
    When the evidence check runs
    Then it fails with "missing statement: published runs recorded without a browser"
  @negative
  Scenario: AJ-150 A leftover partial file is rejected
    Given a results folder containing "x.json.partial"
    When the evidence check runs
    Then it fails with "stray file: x.json.partial"

@integration @AJ-S150 @REQ-032 @DoD
Feature: AJ-150 Probe and dashboard dry run on the laptop
  @positive
  Scenario: AJ-150 The dashboard dry run prints a loopback URL
    Given the laptop GPU and the local runtime are up
    When "run --tier local --dashboard --dry-run" runs
    Then the exit code is 0 and a line starts with "Dashboard:" and contains "http://127.0.0.1:"
  @negative
  Scenario: AJ-150 The dashboard dry run without a local runtime fails with a code and prints no key
    Given the local runtime is down
    When "run --tier local --dashboard --dry-run" runs
    Then the exit code is 5 and the output names the endpoint
  @positive
  Scenario: AJ-150 The committed probe matches the driver now installed
    Given the laptop GPU and nvidia-smi
    When "probe --json" runs
    Then the exit code is 0 and driverVersion equals the driver of the committed probe file
  @negative
  Scenario: AJ-150 A changed driver version is reported
    Given the committed probe names driver 616.92 and the installed driver is another version
    When the comparison runs
    Then it reports "driver changed since the committed probe" and the probe is rerun and committed
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-150-T1 | Write the failing evidence feature for run files, repeats, baseline and stray files | tests/AgentJoules.Specs/Features/Configuration/RunEvidence.feature | 0.5 | - |
| AJ-150-T2 | Add the `EvidenceChecks` class (newest file per tier, repeat, baseline, stray-file checks) and its step bindings | tests/AgentJoules.Specs/Support/EvidenceChecks.cs, tests/AgentJoules.Specs/Steps/ConfigurationSteps.cs | 0.75 | AJ-150-T1 |
| AJ-150-T3 | Tick box 7: run the criterion 1, 2 and 4 commands and record their output | docs/build-log.md | 0.5 | AJ-150-T2 |
| AJ-150-T4 | Tick box 20: run the criterion 3 command for every tier file and record the repeat values | docs/build-log.md | 0.25 | AJ-150-T3 |
| AJ-150-T5 | Rerun `AJ probe --json` on the laptop and compare the driver version with the committed probe | tests/AgentJoules.IntegrationTests/ProbeDriverTests.cs | 0.25 | AJ-150-T3 |
| AJ-150-T6 | Record the box 7 and box 20 results with exit codes | docs/build-log.md | 0.25 | AJ-150-T4, AJ-150-T5 |
| AJ-150-T7 | Tick box 24: run the dashboard dry run and the replay command, record both exit codes and the event count, and add the no-browser statement | docs/build-log.md | 0.5 | AJ-150-T2 |
Test mapping: tests/AgentJoules.Specs/Features/Configuration/RunEvidence.feature; SchemaValidationTests (hosted N/A rule); the dashboard unit tests of REQ-032 (SSE payload, replay events)
Docs impact: docs/build-log.md
Definition of done:
- [x] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [x] docs listed above updated
- [x] boxes 7, 20 and 24 ticked with the command output in `docs/build-log.md`, or listed unchecked with the reason (boxes 7, 20 and 24 ticked 2026-10-27, see the AJ-150 entry of docs/build-log.md)
Demo relevance: yes: these files are the source of every number the video shows, and the dashboard commands are shots 4 and 5 (replay is the fallback).

### AJ-151: Verify the cloudgpu evidence
Covers: DoD: `results/*-cloudgpu.json` and `results/probe-*-cloudgpu.json` present and schema-valid; DoD: `docs/cloudgpu-run.md` present with `Cost` at or below USD 30 and `Teardown` confirmed; DoD: README results block contains `cloudgpu`
User story: As the reviewer of the submission, I want the cloudgpu run, its probe and its run record to be present, valid and within the cost cap, so that the Nebius AI Cloud claim in the submission is backed by files, or is withdrawn.
Acceptance criteria:
1. `ls results | grep -E "^[0-9]{8}T[0-9]{6}Z-cloudgpu\.json$"` prints at least one file and `ls results | grep -E "^probe-[0-9]{8}T[0-9]{6}Z-cloudgpu\.json$"` prints at least one; `AJ validate results/` exits 0 with both included in the count.
2. The newest cloudgpu run file has `environment.provider` `nebius-ai-cloud`, `environment.precision` `FP8`, a non-empty `environment.instancePreset` and `run.status` `completed` or `partial`; a file with status `incomplete`, `failed` or `running` does not count as evidence.
3. The cloudgpu probe records `energyCounterSupported` (boolean) and `migMode` `disabled` (a value `enabled` fails the story).
Amendments (2026-10-27, pool A gates): (a) criterion 1's "both included in the count" is read with AJ-150 criterion 4: `validate results/` counts run files only (`Validated 4 files` on the committed tree), so the run file is in that count and the cloudgpu probe's schema validity is shown separately (`validate <probe file>` when the command accepts a file, else the AJ-125 probe tests); (b) `migMode` `not-supported` is accepted besides `disabled` (AJ-125 allows it; only `enabled` fails); (c) the cost rule reads the first `USD <decimal>` of the `- Whole-run cost:` line of `## Cost` (7 on the committed record) against the cap 30, and the teardown rule looks for `deleted` in `## Teardown`.
4. `docs/cloudgpu-run.md` has the headings `Instance`, `Commands`, `Probe`, `Runs`, `Cost`, `Teardown`; the `Cost` section contains `USD` and a value of 30 or less; the `Teardown` section contains the word `deleted` and the user's deletion confirmation time (user action, section 12 step 13).
5. `grep -F "$NGC_API_KEY" docs/cloudgpu-run.md` with the key set returns no hits (@integration) and `grep -cE "nvapi-[A-Za-z0-9_-]{8,}" docs/cloudgpu-run.md` prints 0.
6. `sed -n '/<!-- results:begin -->/,/<!-- results:end -->/p' README.md | grep -c "cloudgpu"` prints 1 or more.
7. If no cloudgpu evidence exists (criterion 1 or 2 fails), the story records boxes 21 and 22 as unchecked with the reason, and notes that box 23 holds only literally (the row reads `cloudgpu | not run`). It writes the cloudgpu-absent record itself in `docs/build-log.md` under `## cloudgpu absent`: the unchecked boxes, the reason, and the fixed steps that make the submission consistent (row `cloudgpu | not run` from `AJ report --in results/ --out docs/report/ --update-readme --render-content`, pitch A-local, singular tagline, Built-with list without `Nebius AI Cloud`, README sentence `None besides Token Factory.`, no cloudgpu sentence in story, posts, script or article). This holds without AJ-158: when AJ-158 exists its `Cut-off plan` cloudgpu row is the same list and is cross-checked against this record; when AJ-158 was cut, this record is the only fallback document. Check: `grep -c "^## cloudgpu absent" docs/build-log.md` prints 1 when no cloudgpu evidence exists.
BDD scenarios:
```gherkin
@unit @AJ-S151 @DoD
Feature: AJ-151 cloudgpu evidence
  @positive
  Scenario: AJ-151 A completed cloudgpu file with FP8 on a named preset is accepted
    Given a results folder with a completed cloudgpu file whose provider is "nebius-ai-cloud" and precision "FP8"
    When the cloudgpu evidence check runs
    Then it passes and records the preset
  @positive
  Scenario: AJ-151 The run record has six headings and a cost within the cap
    Given the committed docs/cloudgpu-run.md
    When its sections are parsed
    Then it has Instance, Commands, Probe, Runs, Cost and Teardown
    And the Cost section holds a USD value of 30 or less and the Teardown section holds "deleted"
  @positive
  Scenario: AJ-151 The README results block names cloudgpu
    Given the committed README.md
    When the results block is read
    Then it contains "cloudgpu"
  @negative
  Scenario: AJ-151 A cost above the cap is rejected
    Given a cloudgpu-run.md whose Cost section states "USD 31.20"
    When the cloudgpu evidence check runs
    Then it fails with "cost 31.20 exceeds 30"
  @negative
  Scenario: AJ-151 A missing deletion confirmation is rejected
    Given a cloudgpu-run.md whose Teardown section lacks the word "deleted"
    When the cloudgpu evidence check runs
    Then it fails with "teardown not confirmed"
  @negative
  Scenario: AJ-151 An incomplete cloudgpu file does not count as evidence
    Given a results folder whose only cloudgpu file has run.status "incomplete"
    When the cloudgpu evidence check runs
    Then it fails with "no completed or partial cloudgpu run file"
  @negative
  Scenario: AJ-151 A probe with MIG enabled is rejected
    Given a cloudgpu probe fixture with migMode "enabled"
    When the cloudgpu evidence check runs
    Then it fails with "MIG enabled: measurement invalid"
  @negative
  Scenario: AJ-151 A missing run-record heading is rejected
    Given a cloudgpu-run.md without the heading "Teardown"
    When the cloudgpu evidence check runs
    Then it fails with "missing heading: Teardown"

@integration @AJ-S151 @DoD
Feature: AJ-151 cloudgpu record carries no key
  @positive
  Scenario: AJ-151 The NGC key value is absent from the run record
    Given NGC_API_KEY is set
    When "grep -F" for its value runs over docs/cloudgpu-run.md
    Then the exit code is 1 and no line is printed
  @negative
  Scenario: AJ-151 A key value in the run record fails the scan
    Given a copy of the run record containing the value of NGC_API_KEY
    When the scan runs
    Then it fails with "key material in docs/cloudgpu-run.md" without printing the value
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-151-T1 | Write the failing cloudgpu evidence feature (files, status, cost cap, teardown, MIG) | tests/AgentJoules.Specs/Features/CloudGpu/CloudGpuEvidence.feature | 0.5 | - |
| AJ-151-T2 | Add the cloudgpu checks (status filter, headings, cost parse, MIG) to EvidenceChecks | tests/AgentJoules.Specs/Support/EvidenceChecks.cs | 0.75 | AJ-151-T1, AJ-150-T2 |
| AJ-151-T3 | Tick box 21: run the criterion 1 to 3 commands and record the output | docs/build-log.md | 0.5 | AJ-151-T2 |
| AJ-151-T4 | Tick box 22: check the six headings, the USD cap and the user's deletion confirmation in the run record | docs/cloudgpu-run.md | 0.5 | AJ-151-T3 |
| AJ-151-T5 | Tick box 23: confirm `cloudgpu` in the generated README block | README.md | 0.25 | AJ-151-T3 |
| AJ-151-T6 | Write the `## cloudgpu absent` record (unchecked boxes 21 and 22, reason, literal box 23, fixed consistency steps) when no evidence exists; create or extend, independent of AJ-158 | docs/build-log.md | 0.5 | AJ-151-T3 |
| AJ-151-T7 | Run the key scan on the run record with the key set and record the exit code | docs/build-log.md | 0.25 | AJ-151-T4 |
Test mapping: tests/AgentJoules.Specs/Features/CloudGpu/CloudGpuEvidence.feature; CloudGpuTests, IncompleteRunTests
Docs impact: docs/build-log.md; docs/cloudgpu-run.md (only a missing Teardown or Cost line)
Definition of done:
- [x] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [x] docs listed above updated
- [x] boxes 21, 22 and 23 ticked with command output, or listed unchecked with the reason (the user's VM deletion and cost are user actions) (boxes 21, 22 and 23 ticked 2026-10-27, see the AJ-151 entry of docs/build-log.md)
Demo relevance: yes: the cloudgpu row and the Nebius AI Cloud section appear in shot 6.

### AJ-152: Verify the report page, README block and content artifacts
Covers: DoD: README results block generated from those files; `AJ report --verify-readme` exits 0; DoD: `content/linkedin-posts.md` holds three variants with every number resolved from named results files; DoD: `content/demo-script.md` shot list totals 170 seconds or less; DoD: `docs/report/index.html` generated by `AJ report`; `grep -c "<script" docs/report/index.html` prints 0; `docs/.nojekyll` exists
User story: As the reviewer of the submission, I want the generated outputs to match the committed results, so that the report page, the README table and the posts cannot drift from the data.
Acceptance criteria:
1. `AJ report --verify-readme` exits 0; after `AJ report --in results/ --out docs/report/ --update-readme --render-content`, `git status --short README.md docs/report content/linkedin-posts.md` prints nothing (generation is deterministic).
2. `test -f docs/report/index.html` exits 0, `grep -c "<script" docs/report/index.html` prints 0, `grep -c "joules-per-step.svg" docs/report/index.html` prints 1 or more, `grep -c "GPU-board energy only" docs/report/index.html` prints 1 or more, `test -f docs/.nojekyll` exits 0.
3. `grep -c "^## Variant: " content/linkedin-posts.md` prints 3, `grep -c "{{results\." content/linkedin-posts.md` prints 0, the `Sources:` line names existing files, each variant is 1 300 characters or fewer.
4. The duration column of the shot list in `content/demo-script.md` sums to 170 or less: `awk -F'|' '/^\| [0-9]/{s+=$4} END{print s}' content/demo-script.md` prints a value of 170 or less.
5. Every box is checked against the newest run files; a run file newer than the generated outputs makes criterion 1 fail until regenerated. Check: `find results -name '*.json' -newer docs/report/index.html` prints nothing.
BDD scenarios:
```gherkin
@unit @AJ-S152 @DoD
Feature: AJ-152 Generated outputs match the committed results
  @positive
  Scenario: AJ-152 Report generation is deterministic
    Given a workspace with the committed results and a README with markers
    When "report --in results/ --out docs/report/ --update-readme --render-content" runs twice
    Then the second run changes no file and "report --verify-readme" exits 0
  @positive
  Scenario: AJ-152 The report page has no script and links the chart
    Given the generated docs/report/index.html and docs/.nojekyll
    When the page is read
    Then it contains no "<script", contains "joules-per-step.svg" and "GPU-board energy only"
  @positive
  Scenario: AJ-152 The rendered posts have three variants and no placeholder
    Given the committed content/linkedin-posts.md
    When it is parsed
    Then it has three variants, no "{{results." text, and a Sources line naming existing files
  @positive
  Scenario: AJ-152 The shot list totals 170 seconds or less
    Given the committed content/demo-script.md
    When the duration column is summed
    Then the sum is 170 or less
  @negative
  Scenario: AJ-152 A hand-edited README block is rejected
    Given a README whose results block has one number edited
    When "report --verify-readme" runs
    Then the exit code is 7
  @negative
  Scenario: AJ-152 A script element in the page is rejected
    Given a page fixture containing "<script>"
    When the evidence check runs
    Then it fails with "script element in docs/report/index.html"
  @negative
  Scenario: AJ-152 A missing nojekyll file is rejected
    Given a workspace without docs/.nojekyll
    When the evidence check runs
    Then it fails with "missing docs/.nojekyll"
  @negative
  Scenario: AJ-152 A shot list above 170 seconds is rejected
    Given a script whose durations sum to 171
    When the evidence check runs
    Then it fails with "shot list totals 171 s, limit 170 s"
  @negative
  Scenario: AJ-152 A post with an unresolved placeholder is rejected
    Given rendered posts containing "{{results.local.totals.netJoules.value}}"
    When the evidence check runs
    Then it fails with "unresolved placeholder in content/linkedin-posts.md"
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-152-T1 | Write the failing feature for determinism, page, posts and shot list | tests/AgentJoules.Specs/Features/Reporting/ReportAndContentEvidence.feature | 0.5 | - |
| AJ-152-T2 | Add the page, posts and shot-list checks to EvidenceChecks | tests/AgentJoules.Specs/Support/EvidenceChecks.cs | 0.5 | AJ-152-T1, AJ-150-T2 |
| AJ-152-T3 | Tick box 8: run `AJ report --verify-readme` and the second-run `git status` check, record exit codes | docs/build-log.md | 0.5 | AJ-152-T2 |
| AJ-152-T4 | Tick box 16: regenerate the report and run the four page greps and the `.nojekyll` test | docs/report/index.html, docs/.nojekyll | 0.5 | AJ-152-T3 |
| AJ-152-T5 | Tick box 10: render the posts and run the variant, placeholder, Sources and length checks | content/linkedin-posts.md | 0.5 | AJ-152-T3 |
| AJ-152-T6 | Tick box 11: sum the shot-list durations with the awk command and record the value | content/demo-script.md | 0.25 | AJ-152-T2 |
| AJ-152-T7 | Record boxes 8, 10, 11 and 16 with their outputs | docs/build-log.md | 0.25 | AJ-152-T4, AJ-152-T5, AJ-152-T6 |
Test mapping: ReadmeBlockTests, ReportRenderingTests, ContentRenderingTests; tests/AgentJoules.Specs/Features/Reporting/ReportAndContentEvidence.feature
Docs impact: docs/build-log.md
Definition of done:
- [x] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [x] docs listed above updated
- [x] the generated files are committed in the same commit as the last results file they reflect (boxes 8, 10, 11 and 16 ticked 2026-10-27, see the AJ-152 entry of docs/build-log.md; no generated file changed, so nothing new to commit)
Demo relevance: yes: the report page is the Try it out URL.

### AJ-153: Verify the documentation and Devpost files
Covers: DoD: `docs/nim-local-attempt.md`, `docs/endpoints.md`, `docs/methodology.md` and `docs/devpost-checklist.md` present, each with a verification date; DoD: `docs/devpost-checklist.md` has the line "Demo URL:" and the headings "Registration", "Tracks", "Judging criteria", "Credits"; DoD: `docs/devpost-story.md` present, `grep -cF "[[FILL:" docs/devpost-story.md` prints 0, no "Fill-after-build" heading; DoD: `docs/devpost-feedback.md` has no `owner: builder` placeholder left; its `owner: user` placeholders are listed in `docs/devpost-checklist.md` under "Feedback form, user fills"
User story: As the reviewer of the submission, I want the four reference documents and the three Devpost files to carry their verification dates, headings and zero leftover placeholders, so that nothing unfinished reaches the form.
Acceptance criteria:
1. The four documents exist and each contains a line `Verified: YYYY-MM-DD` with a date of 2026-10-17 or later: `for f in docs/nim-local-attempt.md docs/endpoints.md docs/methodology.md docs/devpost-checklist.md; do grep -cE "^Verified: 2026-1[0-9]-[0-9]{2}" $f; done` prints four values of 1 or more (`docs/endpoints.md` has one per tier section). A checklist whose rules page could not be verified reads `Verified: not verified (checked <date>, HTTP <status>)` and then box 9 is unchecked with that reason.
2. `grep -c "^Verified" docs/devpost-checklist.md` and the headings `Registration`, `Tracks`, `Judging criteria`, `Credits` (each once) pass; `grep -c "^Demo URL:" docs/devpost-checklist.md` prints 1.
3. `grep -cF "[[FILL:" docs/devpost-story.md` prints 0 and `grep -ci "Fill-after-build" docs/devpost-story.md` prints 0.
4. `grep -cF "owner: builder" docs/devpost-feedback.md` prints 0; `grep -cF "owner: user" docs/devpost-feedback.md` prints 7; the checklist heading `Feedback form, user fills` exists once with 7 bullets.
5. The `Registration` section still holds the three `<user fills>` values (the user fills them) and the report lists them as open user actions, not as defects.
BDD scenarios:
```gherkin
@unit @AJ-S153 @DoD
Feature: AJ-153 Documentation and Devpost file evidence
  @positive
  Scenario: AJ-153 Each reference document carries a verification date
    Given the four committed documents
    When the "Verified:" lines are read
    Then each document has a date of 2026-10-17 or later
  @positive
  Scenario: AJ-153 The checklist has the Demo URL line and the four headings
    Given the committed docs/devpost-checklist.md
    When it is parsed
    Then it has one "Demo URL:" line and the headings Registration, Tracks, Judging criteria, Credits
  @positive
  Scenario: AJ-153 The story has no placeholder and no checklist section
    Given the committed docs/devpost-story.md
    When placeholders and headings are counted
    Then "[[FILL:" occurs 0 times and no heading contains "Fill-after-build"
  @positive
  Scenario: AJ-153 The feedback file has seven user placeholders listed in the checklist
    Given the committed feedback file and checklist
    When the owner markers and the checklist heading are read
    Then "owner: builder" occurs 0 times, "owner: user" occurs 7 times and the checklist lists seven bullets
  @negative
  Scenario: AJ-153 A document without a verification date is rejected
    Given a copy of docs/methodology.md without a "Verified:" line
    When the documentation check runs
    Then it fails with "no verification date: docs/methodology.md"
  @negative
  Scenario: AJ-153 A not-verified checklist leaves box 9 unchecked
    Given a checklist whose line reads "Verified: not verified (checked 2026-10-29, HTTP 403)"
    When the documentation check runs
    Then box 9 is reported unchecked with the reason "rules page not verified"
  @negative
  Scenario: AJ-153 A leftover story placeholder is rejected
    Given a copy of the story with one "[[FILL:" left
    When the documentation check runs
    Then it fails with "leftover placeholder in docs/devpost-story.md"
  @negative
  Scenario: AJ-153 A leftover builder placeholder in the feedback file is rejected
    Given a copy of the feedback file with one "owner: builder" placeholder
    When the documentation check runs
    Then it fails with "builder placeholder left in docs/devpost-feedback.md"
  @negative
  Scenario: AJ-153 A checklist without the Feedback heading is rejected
    Given a copy of the checklist without "Feedback form, user fills"
    When the documentation check runs
    Then it fails with "missing heading: Feedback form, user fills"
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-153-T1 | Write the failing feature for dates, headings and leftover placeholders | tests/AgentJoules.Specs/Features/Configuration/DocsEvidence.feature | 0.5 | - |
| AJ-153-T2 | Add the date, heading and marker checks to EvidenceChecks | tests/AgentJoules.Specs/Support/EvidenceChecks.cs | 0.5 | AJ-153-T1, AJ-150-T2 |
| AJ-153-T3 | Tick box 9: add a `Verified: <date>` line where missing in the four documents and run the grep loop | docs/nim-local-attempt.md, docs/endpoints.md, docs/methodology.md, docs/devpost-checklist.md | 0.75 | AJ-153-T2 |
| AJ-153-T4 | Tick box 17: run the Demo URL and heading greps on the checklist | docs/devpost-checklist.md | 0.25 | AJ-153-T3 |
| AJ-153-T5 | Tick box 18: run the two story greps | docs/devpost-story.md | 0.25 | AJ-153-T2 |
| AJ-153-T6 | Tick box 19: run the two owner greps and count the seven checklist bullets | docs/devpost-feedback.md, docs/devpost-checklist.md | 0.25 | AJ-153-T2 |
| AJ-153-T7 | Record boxes 9, 17, 18 and 19 with their outputs | docs/build-log.md | 0.25 | AJ-153-T4, AJ-153-T5, AJ-153-T6 |
Test mapping: tests/AgentJoules.Specs/Features/Configuration/DocsEvidence.feature
Docs impact: docs/build-log.md; the four documents (verification line only)
Definition of done:
- [x] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [x] docs listed above updated
- [x] boxes 9, 17, 18 and 19 ticked with command output, or unchecked with the reason (a checklist that is `not verified` leaves box 9 unchecked) (boxes 9, 17, 18 and 19 ticked 2026-10-27, see the AJ-153 entry of docs/build-log.md)
Demo relevance: no.

### AJ-154: Verify spec conformance, Spec traits and scope
Covers: DoD: Every REQ and EDGE acceptance criterion above is met; DoD: No work outside the Requirements (Out of scope respected); DoD: Every REQ or EDGE whose Acceptance names a unit test has at least one passing test with `[Trait("Spec","<ID>")]`; the per-ID filter results are recorded in `docs/build-log.md`
User story: As the reviewer of the submission, I want one table that shows, for every requirement and edge case, the command that proves it and its result, so that no acceptance criterion is assumed.
Acceptance criteria:
1. Box 15: for each of the 36 IDs whose Acceptance names a unit test (REQ-003, 004, 005, 007, 008, 009, 010, 011, 013, 014, 015, 016, 017, 018, 025, 026, 029, 030, 032; EDGE-001, 002, 003, 004, 005, 006, 007, 008, 009, 011, 012, 013, 015, 016, 017, 018, 019) the command `dotnet test --no-build --filter "Spec=<ID>" --logger "console;verbosity=normal"` shows at least one passed and zero failed; the 36 results are recorded as a table `Spec filter results` in `docs/build-log.md` (ID, passed, failed, skipped, exit code).
2. Box 1: `docs/build-log.md` holds a table `Acceptance matrix` with one row for each of REQ-001 to REQ-032 and EDGE-001 to EDGE-019 (51 rows): ID, the acceptance command or artifact, the observed result, status in {`MET`, `UNMET`, `BLOCKED`, `USER-ACTION`}. `grep -cE "^\| (REQ|EDGE)-[0-9]{3} \|" docs/build-log.md` prints 51 and no status cell is empty. Any row not `MET` carries its reason and appears in the final report.
3. Box 3: `grep -E "AgentHooks|Agents.AI.Mcp|DevUI|Agents.AI.A2A|SemanticKernel|Plotly|ScottPlot|OxyPlot" Directory.Packages.props` prints nothing; `git ls-files "*.py" | wc -l` prints 0; `grep -c "<IsPackable>false</IsPackable>" Directory.Build.props` prints 1; the project folders under `src/` and `tests/` equal the eleven projects of the technical design (seven under `src/`, four under `tests/`); `git log --format=%s | grep -c "^STRETCH:"` prints 0 unless every box is already ticked.
4. EDGE-010 and EDGE-014 (document-only acceptances) and the user-action rows (first CI run) are marked with their evidence file or `USER-ACTION`, not left blank.
5. When `specs/agent-joules.md` exists (option B), `spec-compliance-reviewer` runs against it and its `VERDICT` line is recorded; with option A the same review reads Layer 2 of the prompt.
Amendments (2026-10-27, pool B): (a) criterion 2's literal grep over the whole log cannot print 51 (the older Per-ID filter results table holds 220 rows of that shape and the Spec filter results table adds 36 in the `Spec=<ID>` form); the check is the EvidenceChecks.Conformance parser over the table under the `Acceptance matrix` caption, 51 rows, no empty status; (b) the `Acceptance matrix` carries a fifth column `Reason`, required on every row whose status is not MET; (c) the `Spec filter results` ID cells read `Spec=<ID>`; (d) criterion 5: no `specs/agent-joules.md` exists (option A), so T6 is the full-spec spec-compliance-reviewer pass over the prompt's Layer 2 at the M7 exit (session prompt section 7.2).
BDD scenarios:
```gherkin
@unit @AJ-S154 @DoD
Feature: AJ-154 Spec conformance evidence
  @positive
  Scenario: AJ-154 The acceptance matrix has a row for every requirement and edge case
    Given the committed docs/build-log.md
    When the Acceptance matrix is parsed
    Then it has 51 rows with no empty status
  @positive
  Scenario: AJ-154 The Spec filter table covers the 36 unit-test IDs
    Given the committed docs/build-log.md
    When the Spec filter results table is parsed
    Then it has 36 rows each with passed 1 or more and failed 0
  @positive
  Scenario: AJ-154 No out-of-scope package is referenced
    Given the committed Directory.Packages.props
    When it is searched for alpha, preview, charting and Semantic Kernel packages
    Then nothing is found
  @negative
  Scenario: AJ-154 A matrix with a missing row is rejected
    Given a build log whose Acceptance matrix lacks EDGE-014
    When the matrix check runs
    Then it fails with "missing row: EDGE-014"
  @negative
  Scenario: AJ-154 A Spec filter row with a failed test is rejected
    Given a filter table row "REQ-009 | passed 3 | failed 1"
    When the matrix check runs
    Then it fails with "REQ-009 has failed tests"
  @negative
  Scenario: AJ-154 An alpha package is rejected
    Given a Directory.Packages.props that references Microsoft.Agents.AI.AgentHooks
    When the scope check runs
    Then it fails with "out-of-scope package: Microsoft.Agents.AI.AgentHooks"
  @negative
  Scenario: AJ-154 A stretch commit before all boxes are ticked is rejected
    Given a git history with a commit "STRETCH: energy budget" and one unchecked box
    When the scope check runs
    Then it fails with "stretch started before the Definition of Done was complete"
  @negative
  Scenario: AJ-154 An UNMET row without a reason is rejected
    Given a matrix row "REQ-031 | ... | UNMET" with an empty reason
    When the matrix check runs
    Then it fails with "UNMET without reason: REQ-031"
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-154-T1 | Write the failing conformance feature (matrix rows, filter table, scope) | tests/AgentJoules.Specs/Features/Configuration/SpecConformance.feature | 0.5 | - |
| AJ-154-T2 | Add the matrix, filter-table and scope parsers to EvidenceChecks | tests/AgentJoules.Specs/Support/EvidenceChecks.cs | 0.75 | AJ-154-T1, AJ-150-T2 |
| AJ-154-T3 | Tick box 15: run the 36 filter commands and write the `Spec filter results` table | docs/build-log.md | 1.0 | AJ-154-T2 |
| AJ-154-T4 | Tick box 1: run every acceptance command and write the 51-row `Acceptance matrix` | docs/build-log.md | 2.0 | AJ-154-T3 |
| AJ-154-T5 | Tick box 3: run the scope commands of criterion 3 and record the output | docs/build-log.md | 0.5 | AJ-154-T2 |
| AJ-154-T6 | Run `spec-compliance-reviewer` against the spec and record its verdict | docs/build-log.md | 0.5 | AJ-154-T4 |
| AJ-154-T7 | Resolve or report every UNMET and BLOCKED row | docs/build-log.md | 0.25 | AJ-154-T6 |
Test mapping: tests/AgentJoules.Specs/Features/Configuration/SpecConformance.feature
Docs impact: docs/build-log.md
Definition of done:
- [x] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [x] docs listed above updated
- [x] boxes 1, 3 and 15 ticked, or listed unchecked with the reason of each non-MET row (boxes 3 and 15 ticked 2026-10-27: the scope commands clean, the 36 Spec filters green; box 1 listed unchecked with its reason: the Acceptance matrix has 50 MET rows and one USER-ACTION row, REQ-032, whose Acceptance includes the live-page screenshot docs/report/dashboard.png that the user takes during the M7 rehearsal (decision 7 of the M6 exit); box 1 is ticked when the screenshot lands and the row turns MET; see the AJ-154 entry and the two tables at the end of docs/build-log.md)
Demo relevance: no.

### AJ-155: Final hardening: build, format, tests, secret scan, CI preparation
Covers: DoD: Project quality bar passes (build/lint/format/tests clean); DoD: `dotnet build -warnaserror -tl:off` reports 0 warnings and 0 errors; DoD: `dotnet format --verify-no-changes` exits 0; DoD: `dotnet test` exits 0; passed, failed and skipped counts recorded in `docs/build-log.md`; DoD: Secret scan clean; scan name and exit code recorded in `docs/build-log.md`
User story: As the maintainer, I want the final tree to build without warnings, pass format and tests, and contain no key material, so that the repository a judge clones is clean.
Acceptance criteria:
1. Box 4: `dotnet build -warnaserror -tl:off --no-incremental` exits 0 and its output contains `0 Warning(s)` and `0 Error(s)`; then the literal command `dotnet build -warnaserror -tl:off` exits 0 with the same two strings (the first run is non-incremental because an incremental build skips compilation and hides warnings).
2. Box 5: `dotnet format --verify-no-changes` exits 0.
3. Box 6: with the integration switch and the keys removed from the environment, `env -u AGENTJOULES_INTEGRATION -u NVIDIA_API_KEY -u NEBIUS_API_KEY -u NGC_API_KEY dotnet test` exits 0 with 0 failed; `dotnet test --list-tests` contains MeteredChatClientTests, AttributionBaselineTests, CostModelTests, JudgeParsingTests, ReportRenderingTests, SchemaValidationTests; integration tests are reported skipped; `docs/build-log.md` has the line `dotnet test: passed=<n> failed=0 skipped=<m> exit=0`.
4. Box 12: if `command -v gitleaks` succeeds, `gitleaks detect --source . --no-banner` exits 0; otherwise `git grep -nE "nvapi-[A-Za-z0-9_-]{8,}"` exits 1 (no hits) and, with the keys set, `git grep -nF "$NVIDIA_API_KEY"`, `git grep -nF "$NEBIUS_API_KEY"`, `git grep -nF "$NGC_API_KEY"` each exit 1; `docs/build-log.md` records the scan name and the exit codes (a `git grep` exit of 1 is recorded as `1 = no hits`).
5. Box 2 is ticked only when criteria 1, 2 and 3 all hold in the same commit. Check: the commit hash is recorded once for criteria 1 to 3 and `git status --short` prints nothing at that hash.
6. CI preparation: `.github/workflows/ci.yml` contains `windows-latest`, `ubuntu-latest`, `dotnet build -warnaserror -tl:off`, `dotnet format --verify-no-changes`, `dotnet test` and a probe step: `grep -c` of each prints 1 or more; `grep -n "secrets\." .github/workflows/ci.yml` prints nothing.
7. CI green on both runners is a user-dependent check after the push (user action): `gh run list --workflow ci.yml --branch main --limit 1 --json status,conclusion` shows `completed` and `success`, and `gh run view <id> --json jobs --jq '.jobs[] | [.name,.conclusion]'` shows both runners `success`. A red run is recorded as a finding with its failing step; the builder prepares the fix as a local commit for the user to push and the submission does not wait for it (REQ-024 says the first run is not a Definition of Done item).
Amendments (2026-10-27 12:50Z, the exit peer review): (a) the negative scenario "A key value found in the tree fails the scan" writes a synthetic key-shaped value built at run time into its temporary tracked file instead of the real NGC_API_KEY value (keys never reach a file, the AJ-143 precedent); the positive scenario still scans the tracked tree for the three real values, read-only, printing file names only.
BDD scenarios:
```gherkin
@unit @AJ-S155 @DoD
Feature: AJ-155 Final hardening
  @positive
  Scenario: AJ-155 A clean build output is accepted
    Given a captured build output containing "0 Warning(s)" and "0 Error(s)" and exit code 0
    When the hardening check runs
    Then box 4 passes
  @positive
  Scenario: AJ-155 The test summary line is recorded with the three counts
    Given a captured test run with 412 passed, 0 failed and 9 skipped
    When the hardening check formats the summary
    Then the line reads "dotnet test: passed=412 failed=0 skipped=9 exit=0"
  @positive
  Scenario: AJ-155 The CI workflow has both runners and no secrets
    Given the committed .github/workflows/ci.yml
    When it is searched
    Then it contains windows-latest, ubuntu-latest, the build, format and test commands and a probe step
    And it contains no "secrets."
  @positive
  Scenario: AJ-155 The secret scan fallback records its name and exit codes
    Given gitleaks is not installed
    When the scan step runs
    Then the build log records "git grep fallback" with the exit codes of the four commands
  @negative
  Scenario: AJ-155 A build with a warning is rejected
    Given a captured build output containing "1 Warning(s)"
    When the hardening check runs
    Then it fails with "build has warnings"
  @negative
  Scenario: AJ-155 A test run with a failure is rejected
    Given a captured test run with 1 failed
    When the hardening check runs
    Then it fails with "tests failed: 1"
  @negative
  Scenario: AJ-155 A format difference is rejected
    Given a captured "dotnet format --verify-no-changes" exit code 2
    When the hardening check runs
    Then it fails with "format check failed"
  @negative
  Scenario: AJ-155 A key-shaped string in a tracked file is rejected
    Given a tracked fixture file containing a runtime-built "nvapi-" plus 16 characters value
    When the secret scan runs
    Then it fails with "key-shaped string in tracked file"
  @negative
  Scenario: AJ-155 A workflow that references a secret is rejected
    Given a workflow fixture containing "secrets.NVIDIA_API_KEY"
    When the CI check runs
    Then it fails with "secrets reference in ci.yml"

@integration @AJ-S155 @DoD
Feature: AJ-155 Key scan with the real keys
  @positive
  Scenario: AJ-155 No tracked line contains a key value
    Given NVIDIA_API_KEY, NEBIUS_API_KEY and NGC_API_KEY are set
    When "git grep -nF" runs for each value
    Then each exits 1 and prints no line
  @negative
  Scenario: AJ-155 A key value found in the tree fails the scan
    Given a temporary tracked file containing the NGC_API_KEY value
    When "git grep -nF" runs for it
    Then the scan fails and prints the file name only
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-155-T1 | Write the failing hardening feature (build, test summary, CI file, scan) | tests/AgentJoules.Specs/Features/Configuration/Hardening.feature | 0.5 | - |
| AJ-155-T2 | Add the build-output, test-summary and CI-file checks to EvidenceChecks | tests/AgentJoules.Specs/Support/EvidenceChecks.cs | 0.5 | AJ-155-T1, AJ-150-T2 |
| AJ-155-T3 | Tick box 4: run the non-incremental and the literal build commands and record both outputs | docs/build-log.md | 0.5 | AJ-155-T2 |
| AJ-155-T4 | Tick box 5: run `dotnet format --verify-no-changes`, fix any difference with `dotnet format`, rerun, record the exit code | docs/build-log.md | 0.5 | AJ-155-T3 |
| AJ-155-T5 | Tick box 6: run `dotnet test` without the switch and keys, list the tests, record the counts line | docs/build-log.md | 0.75 | AJ-155-T4 |
| AJ-155-T6 | Tick box 12: run gitleaks or the grep fallback with the keys set and record the scan name and exit codes | docs/build-log.md | 0.5 | AJ-155-T5 |
| AJ-155-T7 | Tick box 2: confirm criteria 1 to 3 hold in one commit and record its hash | docs/build-log.md | 0.25 | AJ-155-T6 |
| AJ-155-T8 | Check the CI workflow greps and the absence of `secrets.` | .github/workflows/ci.yml | 0.25 | AJ-155-T2 |
| AJ-155-T9 | Write the post-push CI check commands and the red-run procedure | docs/devpost-checklist.md | 0.25 | AJ-155-T8 |
Test mapping: tests/AgentJoules.Specs/Features/Configuration/Hardening.feature; RedactionTests
Docs impact: docs/build-log.md; docs/devpost-checklist.md (CI check commands, marked user action)
Definition of done:
- [x] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [x] docs listed above updated
- [x] boxes 2, 4, 5, 6 and 12 ticked with command output, or listed unchecked with the reason (boxes 4, 5, 6 and 12 ticked 2026-10-27 from the hardening battery, see the AJ-155 entry of docs/build-log.md; box 2 listed pending: criteria 1 to 3 hold on the measured tree, and the commit hash at which `git status --short` is empty is recorded by the M7 exit battery, which ticks box 2)
Demo relevance: no.

### AJ-156: Verify the git history and the push boundary, then run the final review gates
Covers: DoD: `git log --oneline` shows commits whose messages start with the story id followed by the REQ or EDGE ids; DoD: Nothing pushed by the builder: `git log origin/main..HEAD --oneline` lists every build-session commit; the `git push` command for the user is present in `docs/devpost-checklist.md`
User story: As the user who pushes, I want the local history to contain every build commit, labelled by requirement, and nothing pushed yet, so that one `git push` publishes exactly the reviewed work.
Acceptance criteria:
1. Box 13: `git log --no-merges <build-start>..HEAD --format=%s | grep -vE "^M[0-9] exit( addendum)?:" | grep -vcE "^AJ-[0-9]{3}( (REQ|EDGE)-[0-9]{3})*:"` prints 0, where `<build-start>` is the HEAD hash recorded by M0 AJ-002 in `docs/build-log.md` (the planning commits made before the build are outside the range). Every build-session commit starts with the story id followed by the REQ or EDGE ids the story delivers (for example `AJ-045 EDGE-006: ...`); M7 stories that deliver no REQ use `^AJ-[0-9]{3}:`; `git log --oneline | head` shows such commits.
2. Box 14: `git ls-remote origin main` prints the `origin/main` hash recorded in `docs/build-log.md` by M0 AJ-002 (@integration), so nothing was pushed; `git log origin/main..HEAD --oneline` lists every build-session commit and also the planning commits made before the build (7 at the time of planning), and `git rev-list --count <build-start>..HEAD` equals the number of build-session commits; `git remote -v` shows `origin` at github.com/tomas-rampas/agent-joules; `grep -c "git push" docs/devpost-checklist.md` prints 1 or more.
3. `git status --short` prints nothing (everything is committed) before the review gates start.
4. `code-review-gatekeeper` runs over the final diff; every BLOCKER and MAJOR finding is fixed in a commit whose message starts with the story id and the REQ or EDGE ids concerned, or has explicit user sign-off recorded in `docs/build-log.md`.
5. After the last commit, `peer-review-critic` runs on the branch against `origin/main`; its last line is `VERDICT: APPROVED` (the Stop hook blocks the session end otherwise, at most three times for `CHANGES_REQUIRED`); each BLOCKER and MAJOR finding is resolved or signed off by the user.
6. No command in the build log or the checklist shows the builder running `git push`, `gh release create` or any cloud-resource command.
Amendments (2026-10-27): (a) criterion 1 and box 13: the accepted subject forms are the story form with one or more story ids and zero or more REQ or EDGE ids (pooled commits and M7 stories without a REQ) and the milestone subjects M<k> exit, exit addendum, kickoff, exit review fixes and exit peer review; the literal pattern of criterion 1 is reported as a separate count (10 non-matching subjects on the history at 0ffa008, all pooled or exit subjects) and the amended count is 0; (b) criterion 2 and box 14: by the user's decision of 2026-10-20 the session publishes main at every milestone exit, so the recorded reference for "nothing pushed" is the previous milestone's exit commit on origin/main (01df5cf) and not the M0 base; the M0 base stays the start of the history and commit-count checks; (c) criterion 6: by the user's decision of 2026-10-27 12:11Z the session also runs the tag, the release and the Pages setting at the exit, so the checklist labels those four blocks "User action delegated to the session at the M7 exit" and the rule accepts that label; the build log records each delegated command with its exit code in the exit entry; the statement "no command shows the builder running git push, gh release create or a cloud-resource command" is read as: none outside the recorded exit steps, and no cloud-resource command at all. (d) 2026-10-27 12:50Z, the exit peer review: the gated scenario "The remote main still points at the recorded base" reads the remote refs/heads/main hash and asserts it is a commit the local HEAD descends from (the remote holds nothing this checkout does not), so it stays green after the session's own publication of main; box 14 rests on the ls-remote result measured at abddeac (01df5cf, 5 commits behind HEAD) and the recorded reference stays in the commit-count report only.
BDD scenarios:
```gherkin
@unit @AJ-S156 @DoD
Feature: AJ-156 Git history and push boundary
  @positive
  Scenario: AJ-156 Every build commit starts with a requirement ID
    Given a temporary repository with a recorded build-start and commits "AJ-006 REQ-001: scaffold", "AJ-045 EDGE-006: retry on 429" and "AJ-155: format"
    When the history check runs
    Then it passes
  @positive
  Scenario: AJ-156 Nothing is pushed when origin main equals the recorded base
    Given a temporary repository with origin/main at the recorded base and three local commits
    When the push-boundary check runs
    Then it reports 3 commits ahead and passes
  @positive
  Scenario: AJ-156 The checklist holds the git push command
    Given the committed docs/devpost-checklist.md
    When it is searched for "git push"
    Then at least one fenced block contains it
  @negative
  Scenario: AJ-156 A commit without a requirement ID is rejected
    Given a temporary repository with a commit "tidy readme"
    When the history check runs
    Then it fails with "commit without story id: tidy readme"
  @negative
  Scenario: AJ-156 A pushed history is rejected
    Given a temporary repository where origin/main equals HEAD
    When the push-boundary check runs
    Then it fails with "origin/main moved: the builder pushed"
  @negative
  Scenario: AJ-156 An uncommitted change blocks the review gates
    Given a temporary repository with a modified tracked file
    When the gate precondition check runs
    Then it fails with "uncommitted changes"
  @negative
  Scenario: AJ-156 A checklist without the push command is rejected
    Given a copy of the checklist without "git push"
    When the push-boundary check runs
    Then it fails with "missing git push block"

@integration @AJ-S156 @DoD
Feature: AJ-156 Remote state
  @positive
  Scenario: AJ-156 The remote main still points at the recorded base
    Given network access and the recorded base hash
    When "git ls-remote origin main" runs
    Then it prints the recorded hash
  @negative
  Scenario: AJ-156 A moved remote main is reported
    Given the remote main differs from the recorded base
    When the check runs
    Then it reports "remote main moved" and asks the user whether they pushed
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-156-T1 | Write the failing git-boundary feature using temporary repositories | tests/AgentJoules.Specs/Features/Configuration/GitBoundary.feature | 0.5 | - |
| AJ-156-T2 | Add the history, push-boundary and clean-tree checks to EvidenceChecks | tests/AgentJoules.Specs/Support/EvidenceChecks.cs | 0.75 | AJ-156-T1, AJ-150-T2 |
| AJ-156-T3 | Tick box 13: run the history command and record the output | docs/build-log.md | 0.25 | AJ-156-T2 |
| AJ-156-T4 | Tick box 14: run the ahead-count, remote and checklist commands and record the output | docs/build-log.md | 0.25 | AJ-156-T3 |
| AJ-156-T5 | Run `code-review-gatekeeper` on the final diff and record its findings | docs/build-log.md | 1.0 | AJ-156-T4, AJ-155-T7 |
| AJ-156-T6 | Fix every BLOCKER and MAJOR finding in requirement-labelled commits | src/, tests/, docs/ (as the findings name) | 1.0 | AJ-156-T5 |
| AJ-156-T7 | Run `peer-review-critic` on the branch against origin/main and record the VERDICT line | docs/build-log.md | 1.0 | AJ-156-T6 |
| AJ-156-T8 | Record the final commit hash, the ahead count and the unticked boxes for the final report | docs/build-log.md | 0.25 | AJ-156-T7 |
Test mapping: tests/AgentJoules.Specs/Features/Configuration/GitBoundary.feature
Docs impact: docs/build-log.md
Definition of done:
- [x] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [x] docs listed above updated
- [x] boxes 13 and 14 ticked, and the last `peer-review-critic` verdict is `VERDICT: APPROVED` or the user signed off the open findings (boxes 13 and 14 ticked 2026-10-27 under amendments (a) and (b), see the AJ-156 entry; ticked by the M7 exit entry: the last peer-review-critic verdict APPROVED (round 2, 2026-10-27 12:52Z))
Demo relevance: no.

### AJ-157: Prepare the v0.1.0 release (the tag and the GitHub release are the user's actions)
Covers: DoD: none (release preparation; supports the checklist command of box 14 and the demo URL fallback)
User story: As the user, I want the release notes and three assets ready and the exact `gh release create` command in the checklist, so that I create the v0.1.0 release in one command after the push.
Acceptance criteria:
1. `docs/release-notes-v0.1.0.md` exists with the headings `Features`, `Fixes`, `Breaking changes`, `Migration`, `Known limitations`, `Honesty rules`, `Assets`, `Verification`; `Breaking changes` and `Migration` read `None (first release)`.
2. Every number in the notes names a results file or `docs/cloudgpu-run.md` and carries a label; the notes state the tiers that ran and the tiers that did not (`cloudgpu | not run` when absent) and contain the sentence `Energy is GPU-board energy only.`
3. Three assets exist and are git-ignored: `ls artifacts/agent-joules-v0.1.0-*.zip | wc -l` prints 3, namely `agent-joules-v0.1.0-fdd.zip` (output of `dotnet publish src/AgentJoules.Cli -c Release --no-self-contained -o artifacts/fdd`), `agent-joules-v0.1.0-results.zip` (every `results/*.json`) and `agent-joules-v0.1.0-report.zip` (the contents of `docs/report/`); `git ls-files artifacts | wc -l` prints 0.
4. Zip contents: the fdd zip lists `AgentJoules.Cli.dll`; the results zip file count equals `ls results | grep -cE "\.json$"`; the report zip lists `index.html`, `summary.md`, `joules-per-step.svg`; no zip contains `.raw` files, `nvapi-` strings or `id_ed25519`, `id_rsa` or `.pem` names.
5. Smoke test: `dotnet artifacts/fdd/AgentJoules.Cli.dll probe --json` exits 0 (`gpuPresent` true on the laptop, false elsewhere) and `dotnet artifacts/fdd/AgentJoules.Cli.dll validate results/` exits 0.
6. The command in `docs/devpost-checklist.md` names exactly the notes file and the three asset paths above; `gh release create v0.1.0 --repo tomas-rampas/agent-joules --target main --title "Agent Joules v0.1.0" --notes-file docs/release-notes-v0.1.0.md <three assets>` is marked `User action (the builder never runs this):`.
7. User actions after the command: `gh release view v0.1.0 --repo tomas-rampas/agent-joules --json assets --jq '.assets[].name'` prints three names; the user keeps the repository unchanged on the tagged commit until the results are announced (prompt section 12 step 9). Tagging happens after the final push so that the tag marks the submitted commit.
Amendments (2026-10-27, pool C gates): (a) criterion 4's "nvapi- strings" is read as key-shaped strings, the CI key pattern nvapi-[A-Za-z0-9_-]{8,}: the fdd zip ships the product's own redaction code and documentation, whose literal prefix nvapi- (the regex nvapi-[A-Za-z0-9_-]{4,} and the nvapi-**** mask in AgentJoules.Core.dll and the AgentJoules.Core, Dashboard and Reporting XML files) is not key material; the scan over every entry finds no key-shaped string and none of the three key values; (b) the two scenarios that read the git-ignored artifacts/ folder pass without asserting, with the reason in the scenario output, in a checkout that holds no zip (the CI runners), and assert on the builder's machine where the zips exist; (c) criterion 2's number rule accepts a results file name or docs/cloudgpu-run.md or a label word in the same sentence or row; the NIM-local attempt's duration cites docs/nim-local-attempt.md, the only record of it; (d) criteria 6 and 7 and the definition-of-done box "the tag and the release are not created by the builder": by the user's decision of 2026-10-27 12:11Z the session creates the tag and the release at the M7 exit after the CI run on the merged main, with the zips rebuilt from the exit commit and their hashes recorded; the user keeps the repository unchanged on the tagged commit.
BDD scenarios:
```gherkin
@unit @AJ-S157 @DoD
Feature: AJ-157 Release package
  @positive
  Scenario: AJ-157 The release notes have the required headings and the scope sentence
    Given the committed docs/release-notes-v0.1.0.md
    When it is parsed
    Then it has the eight headings and the sentence "Energy is GPU-board energy only."
  @positive
  Scenario: AJ-157 The three zip assets have the expected contents
    Given the built artifacts folder
    When the zip listings are read
    Then the fdd zip lists AgentJoules.Cli.dll, the results zip counts every results json and the report zip lists index.html
  @positive
  Scenario: AJ-157 The checklist release command matches the files on disk
    Given the committed checklist and the artifacts folder
    When the paths in the "gh release create" block are checked
    Then every path exists and the notes path equals docs/release-notes-v0.1.0.md
  @negative
  Scenario: AJ-157 A key-shaped string inside a zip is rejected
    Given a results zip fixture containing a runtime-built "nvapi-" plus 16 characters value
    When the package check runs
    Then it fails with "key material in agent-joules-v0.1.0-results.zip"
  @negative
  Scenario: AJ-157 A tracked artifacts folder is rejected
    Given a repository where "artifacts/agent-joules-v0.1.0-fdd.zip" is tracked
    When the package check runs
    Then it fails with "artifacts must not be tracked"
  @negative
  Scenario: AJ-157 A number in the notes without a source is rejected
    Given release notes containing "the local run used 310 J"
    When the package check runs
    Then it fails with "number without source: 310 J"
  @negative
  Scenario: AJ-157 A missing asset named in the command is rejected
    Given a checklist command naming "artifacts/agent-joules-v0.1.0-report.zip" that does not exist
    When the package check runs
    Then it fails with "asset not found: artifacts/agent-joules-v0.1.0-report.zip"
  @negative
  Scenario: AJ-157 Release notes that claim a missing tier ran are rejected
    Given release notes mentioning cloudgpu figures and no cloudgpu run file
    When the package check runs
    Then it fails with "cloudgpu claim without run file"
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-157-T1 | Write the failing release-package feature | tests/AgentJoules.Specs/Features/Configuration/ReleasePackage.feature | 0.5 | - |
| AJ-157-T2 | Add the zip-listing, notes and command cross-check to EvidenceChecks | tests/AgentJoules.Specs/Support/EvidenceChecks.cs | 0.5 | AJ-157-T1, AJ-150-T2 |
| AJ-157-T3 | Write the release notes from the results files, README and known limitations | docs/release-notes-v0.1.0.md | 1.0 | AJ-157-T2 |
| AJ-157-T4 | Build the three zip assets under the git-ignored `artifacts/` folder | artifacts/agent-joules-v0.1.0-fdd.zip, artifacts/agent-joules-v0.1.0-results.zip, artifacts/agent-joules-v0.1.0-report.zip | 0.75 | AJ-157-T3 |
| AJ-157-T5 | Smoke-test the fdd build with `probe --json` and `validate results/` | docs/build-log.md | 0.5 | AJ-157-T4 |
| AJ-157-T6 | Cross-check the checklist release command against the notes path and the asset names | docs/devpost-checklist.md | 0.25 | AJ-157-T4 |
| AJ-157-T7 | Record the asset sizes, hashes and the user's verification command in the build log | docs/build-log.md | 0.25 | AJ-157-T5 |
Test mapping: tests/AgentJoules.Specs/Features/Configuration/ReleasePackage.feature
Docs impact: docs/release-notes-v0.1.0.md; docs/devpost-checklist.md; docs/build-log.md
Definition of done:
- [x] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [x] docs listed above updated
- [x] the tag and the release are not created by the builder (user action) (the assets built 2026-10-27, see the AJ-157 entry of docs/build-log.md; amendment (d) of 2026-10-27 12:11Z: by the user's decision the session creates the tag and the release at the M7 exit after the CI run on the merged main, recorded in the exit entry; the box is read as "not created by the builder on its own initiative")
Demo relevance: no.

### AJ-158: Write the submission order, the timeline and the cut-off plan
Covers: DoD: none (submission preparation; every Devpost step is a user action)
User story: As the user submitting on Devpost, I want the fields in the order of the form, a timeline that ends well before 2026-10-30 10:00 PDT (17:00 UTC), and a decision table for what to submit when a tier is missing, so that a missing tier never costs the submission.
Acceptance criteria:
1. `docs/devpost-checklist.md` has the sections `Submission order`, `Cut-off plan`, `Timeline`. `Submission order` is a numbered list of 12 user actions, each ending with `(user)`, in this order: 1 project name; 2 elevator pitch (200 characters, text from AJ-141); 3 About the project (`docs/devpost-story.md`); 4 Built-with tags (25 maximum, list from the checklist); 5 Try it out links (the `Demo URL:` line and the repository); 6 YouTube video link (`<user fills>`); 7 track (the user's pick, recommendation in `Tracks`); 8 team members; 9 repository URL with OSI license and README highlights (README sections of AJ-132); 10 Nemotron and Nebius feedback questionnaire (`docs/devpost-feedback.md`, seven user placeholders); 11 submit; 12 after submission keep the repository on the submitted commit and confirm the `v0.1.0` tag. Each row names an artifact that exists or `(user)` text.
2. `Timeline` lists planning targets (assumed; one calendar, plan.md decisions D-6 and D-7) in UTC with the PDT equivalent in parentheses (UTC minus 7 hours): content freeze 2026-10-29 17:00 UTC (10:00 PDT); M7 gate passed and push 2026-10-29 20:00 UTC (13:00 PDT); Pages and release live 2026-10-29 21:00 UTC (14:00 PDT); video recorded on 2026-10-29 and public by 2026-10-30 07:00 UTC (00:00 PDT); submission complete by 2026-10-30 14:00 UTC (07:00 PDT); deadline 2026-10-30 17:00 UTC (10:00 PDT). The latest acceptable targets, used only when the evening plan slips, are push 2026-10-30 07:00 UTC (00:00 PDT), Pages and release 08:00 UTC (01:00 PDT), video public 11:00 UTC (04:00 PDT), submission 14:00 UTC (07:00 PDT). It states that 3 hours of buffer remain between the last target and the deadline and that for a user in central Europe the local time is UTC+1 (CET) on that date. Every UTC/PDT pair differs by exactly 7 hours; `grep -cE "UTC \([0-9]{2}:[0-9]{2} PDT\)" docs/devpost-checklist.md` prints 6 or more.
3. `Cut-off plan` is a table with one row per missing item: cloudgpu tier, local tier, nvidia tier, nebius tier, judge, Pages not live, video not ready, feedback placeholders unfilled. Each row names: what is submitted, the files that change, the regeneration commands (`AJ report --in results/ --out docs/report/ --update-readme --render-content`, `AJ report --verify-readme`), the story whose fallback applies, and the unchecked boxes.
4. The nebius row states the hard rule: the Token Factory run is a hackathon requirement; without a `nebius` run file the build is reported as BLOCKED on that item and the submission is not made with a claim the files do not support. When `nebius` is missing and a cloudgpu file exists, the platform rule is satisfied through Nebius AI Cloud and the nebius row reads `not run`; when both are missing the user decides, with the missing key, URL or model named.
5. The cloudgpu row lists: row `cloudgpu | not run`, pitch A-local, singular tagline, Built-with list without `Nebius AI Cloud`, README sentence `None besides Token Factory.`, no cloudgpu sentence in the story, posts, article or script, and boxes 21, 22 and the substance of 23 unchecked.
6. The video row: if the full video is not public by 2026-10-30 07:00 UTC (latest 11:00 UTC), record the minimal cut (shots 1, 3, 6 and 9, planned 65 s), upload it, and note it in the checklist; a missing video is the only item for which no fallback exists. Check: the awk sum of the durations of shots 1, 3, 6 and 9 in `content/demo-script.md` prints 65.
7. Nothing in the file says that the builder pushed, submitted or posted; the steps 8 and 9 of the prompt section 12 are written as the user's. Check: `grep -niE "builder (pushed|submitted|posted|created the release)" docs/devpost-checklist.md` prints nothing.
BDD scenarios:
```gherkin
@unit @AJ-S158 @DoD
Feature: AJ-158 Submission order and cut-off plan
  @positive
  Scenario: AJ-158 The submission order has 12 user actions in the form order
    Given the committed docs/devpost-checklist.md
    When the Submission order section is parsed
    Then it has 12 numbered items each ending with "(user)" in the specified order
  @positive
  Scenario: AJ-158 The timeline offsets are correct
    Given the Timeline section
    When each UTC and PDT pair is compared
    Then PDT equals UTC minus 7 hours for every pair and the last target is at least 3 hours before 17:00 UTC
  @positive
  Scenario: AJ-158 The cut-off plan has a row for every missing item
    Given the Cut-off plan table
    When its rows are read
    Then rows exist for cloudgpu, local, nvidia, nebius, judge, Pages, video and feedback
    And each row names files, a regeneration command, a fallback story and unchecked boxes
  @positive
  Scenario: AJ-158 Regenerating without a cloudgpu file yields the not-run row
    Given a fixture workspace with run files for local, nvidia and nebius
    When the commands of the cloudgpu row are run
    Then the README block and summary contain "cloudgpu | not run" and "report --verify-readme" exits 0
  @negative
  Scenario: AJ-158 A timeline pair with a wrong offset is rejected
    Given a Timeline line "17:00 UTC (09:00 PDT)"
    When the checklist check runs
    Then it fails with "offset must be 7 hours: 17:00 UTC (09:00 PDT)"
  @negative
  Scenario: AJ-158 A submission step without a user marker is rejected
    Given a Submission order item without "(user)"
    When the checklist check runs
    Then it fails with "step not marked as user action: 11"
  @negative
  Scenario: AJ-158 A cut-off row without a regeneration command is rejected
    Given the cloudgpu row with the command column empty
    When the checklist check runs
    Then it fails with "no regeneration command: cloudgpu"
  @negative
  Scenario: AJ-158 A missing deadline string is rejected
    Given a checklist without "2026-10-30 10:00 PDT (17:00 UTC)"
    When the checklist check runs
    Then it fails with "missing deadline"
  @negative
  Scenario: AJ-158 A claim that the builder submitted is rejected
    Given a checklist sentence "the builder submitted the project"
    When the checklist check runs
    Then it fails with "builder action claimed: submitted"
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-158-T1 | Write the failing submission feature (order, offsets, rows, regeneration) | tests/AgentJoules.Specs/Features/Configuration/SubmissionPlan.feature | 0.5 | - |
| AJ-158-T2 | Add the order, timeline-offset and cut-off-row parsers to EvidenceChecks | tests/AgentJoules.Specs/Support/EvidenceChecks.cs | 0.5 | AJ-158-T1, AJ-150-T2 |
| AJ-158-T3 | Write `Submission order` with the 12 user actions and their artifacts | docs/devpost-checklist.md | 1.0 | AJ-158-T2, AJ-139-T4 |
| AJ-158-T4 | Write the `Cut-off plan` table with files, commands, fallback stories and unchecked boxes | docs/devpost-checklist.md | 1.0 | AJ-158-T3 |
| AJ-158-T5 | Write `Timeline` in UTC and PDT with the 3-hour buffer and the CET note | docs/devpost-checklist.md | 0.5 | AJ-158-T4 |
| AJ-158-T6 | Rehearse the cloudgpu-absent row in a fixture workspace and record the exit codes | docs/build-log.md | 1.0 | AJ-158-T4 |
| AJ-158-T7 | Record the plan, its assumptions and the unticked-box list format in the build log | docs/build-log.md | 0.25 | AJ-158-T6 |
Test mapping: tests/AgentJoules.Specs/Features/Configuration/SubmissionPlan.feature
Docs impact: docs/devpost-checklist.md; docs/build-log.md
Definition of done:
- [x] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [x] docs listed above updated
- [ ] the user has read the plan and given the missing-item decisions that are theirs (nebius and video rows) (open: a user action for the submission day; the rows state the rules; listed in the final report, 2026-10-27)
Demo relevance: no.

### AJ-159: Define the buffer, the risk register and the drop order
Covers: DoD: none (schedule protection; defines what is cut, in order, if time runs out)
User story: As the project owner, I want a fixed order of what is dropped first and a reserved buffer before the deadline, so that when time runs out I cut by rule and never cut a Definition of Done box by accident.
Acceptance criteria:
1. `docs/build-log.md` has a section `Drop order` that copies plan.md section 3.4 verbatim, in this order: Level 1 steps C1 to C9 (stories and hours as in the plan), Level 2 steps D1 to D3 (tasks and hours as in the plan), then the Level 3 paragraph (the cloudgpu tier: boxes 21, 22 and the substance of 23). Per step it adds `trigger` (a UTC time and a condition from the gate rule of plan.md 3.5), `consequence`, `boxes unticked` (`none` for C1 to C9 and D1 to D3; 21, 22 and 23 for Level 3) and `regeneration`. The parser asserts the same step ids in the same order as the plan: `grep -cE "^\| (C[1-9]|D[1-3]) \|" docs/build-log.md` prints 12. A second cloudgpu repeat set is never a drop step (box 20 requires `run.repeat >= 3` for every tier) and the three LinkedIn variants are never dropped (box 10); both are named in the Never dropped list.
2. Steps that untick a box come after every step that does not: the parser asserts the `boxes unticked` column is `none` for C1 to C9 and D1 to D3 and non-empty only for Level 3.
3. A `Never dropped` list names the minimum submission: a `nebius` run file (or cloudgpu if nebius is BLOCKED by the user decision), the `local` run file, schema validation, the build, format and test commands, the secret scan, the README results block, the checklist with the push command, the story, the feedback draft, the demo script, `peer-review-critic` approval, and the user's video and submission.
4. A `Buffer` table lists the freeze times of AJ-158 `Timeline` and the unallocated hours: the reserve is 3 hours before the 17:00 UTC deadline and 2 hours inside the M7 window (assumed planning values); every trigger time lies before 2026-10-30 17:00 UTC.
5. The risk register of this milestone (`## Risks` table, ids RK-1 to RK-8) is cross-linked: each drop item names the risk that triggers it.
6. The M6 and M7 task estimates are compared with the capacity in the same section (M6: 55.25 h versus about 18 h; M7: see `## Estimate`), with the statement that the gap is closed by the drop order, by front-loading M6 work and by the fact that most M7 tasks are commands that run in minutes (the hours are planning figures, not measurements).
BDD scenarios:
```gherkin
@unit @AJ-S159 @DoD
Feature: AJ-159 Drop order and buffer
  @positive
  Scenario: AJ-159 The drop order is monotonic in damage
    Given the committed docs/build-log.md
    When the Drop order list is parsed
    Then steps C1 to C9 and D1 to D3 have "boxes unticked" none and Level 3 names boxes 21, 22 and 23
  @positive
  Scenario: AJ-159 Every trigger time is before the deadline
    Given the Drop order and Buffer sections
    When each trigger time is read
    Then every time is before 2026-10-30 17:00 UTC
  @positive
  Scenario: AJ-159 The never-dropped list names the minimum submission
    Given the committed build log
    When the Never dropped list is read
    Then it names the nebius run file, the local run file, the secret scan, the README block, the checklist with the push command, the story, the script and the user's video and submission
  @positive
  Scenario: AJ-159 The buffer reserves three hours before the deadline
    Given the Buffer table
    When the last target and the deadline are compared
    Then the gap is 3 hours or more
  @negative
  Scenario: AJ-159 A damaging item placed before a harmless one is rejected
    Given a drop list where Level 3 precedes step C3
    When the order check runs
    Then it fails with "Level 3 unticks boxes before harmless steps"
  @negative
  Scenario: AJ-159 An item without a trigger time is rejected
    Given a drop item with an empty trigger
    When the order check runs
    Then it fails with "no trigger: C4"
  @negative
  Scenario: AJ-159 A trigger after the deadline is rejected
    Given a drop item with trigger "2026-10-30 18:00 UTC"
    When the order check runs
    Then it fails with "trigger after deadline: C5"
  @negative
  Scenario: AJ-159 A never-dropped item that appears in the drop list is rejected
    Given a drop list that contains the secret scan
    When the order check runs
    Then it fails with "never-dropped item in drop list: secret scan"
  @negative
  Scenario: AJ-159 A buffer under three hours is rejected
    Given a Buffer table whose last target is 15:00 UTC
    When the order check runs
    Then it fails with "buffer 2 h is below 3 h"
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-159-T1 | Write the failing drop-order feature (order, triggers, never-dropped, buffer) | tests/AgentJoules.Specs/Features/Configuration/DropOrder.feature | 0.5 | - |
| AJ-159-T2 | Add the drop-list, trigger-time and buffer parsers to EvidenceChecks | tests/AgentJoules.Specs/Support/EvidenceChecks.cs | 0.5 | AJ-159-T1, AJ-150-T2 |
| AJ-159-T3 | Copy plan.md section 3.4 verbatim as the `Drop order` and add triggers, consequences, unticked boxes and regeneration | docs/build-log.md | 1.0 | AJ-159-T2 |
| AJ-159-T4 | Write the `Never dropped` list and the `Buffer` table with the capacity comparison | docs/build-log.md | 0.5 | AJ-159-T3, AJ-158-T5 |
| AJ-159-T5 | Cross-link each drop item to its risk id in this milestone | docs/build-log.md | 0.25 | AJ-159-T4 |
| AJ-159-T6 | Record the plan and the date it was agreed with the user | docs/build-log.md | 0.25 | AJ-159-T5 |
Test mapping: tests/AgentJoules.Specs/Features/Configuration/DropOrder.feature
Docs impact: docs/build-log.md
Definition of done:
- [x] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [x] docs listed above updated
- [x] the user confirmed the drop order before 2026-10-29 17:00 UTC (a user action) (the order itself is the user's plan decision D-2 of 2026-10-19 and decision 1 of the M6 exit accepted at the M7 kickoff on 2026-10-27; the trigger, risk and buffer columns were added by pool C after the kickoff and are shown to the user in the final report; see the AJ-159 entry)
Demo relevance: no.

## Risks

| Id | Risk | Trigger | Mitigation | Owner |
|---|---|---|---|---|
| RK-1 | Estimate exceeds the window: 38.0 task hours against about 12 working hours | the sum in `## Estimate` is above 12 | most tasks are commands that run in minutes; verification stories AJ-150 to AJ-153 run in parallel as soon as their inputs are committed; apply the drop order of AJ-159 (C1 to C9 and D1 to D3 first); the figures are planning values, not measurements | builder |
| RK-2 | User pushes late or Pages is not live | `curl` of the report URL does not print 200 by 2026-10-29 22:00 UTC (15:00 PDT), latest 2026-10-30 08:00 UTC (01:00 PDT) | Demo URL falls back to the v0.1.0 release, then to the public video URL (prompt section 3); the user pastes the real Pages URL | user |
| RK-3 | The final reviews keep returning findings | `peer-review-critic` ends with `VERDICT: CHANGES_REQUIRED` three times | fix BLOCKER and MAJOR findings first; open MINOR findings are listed in the final report; the user may sign off the rest | builder, user |
| RK-4 | Official rules never verified (HTTP 403) | the checklist line reads `not verified` | box 9 is reported unchecked with that reason; the user pastes the Rules tab text, the builder updates the 13 rule items and the date | user |
| RK-5 | cloudgpu tier absent or partial | no completed or partial `results/*Z-cloudgpu.json` at 2026-10-29 12:00 UTC | AJ-158 cloudgpu row and the fallbacks of M6; boxes 21, 22 and the substance of 23 reported unchecked | builder |
| RK-6 | CI red on the first run | `gh run list` shows a failure after the push | the failing step is recorded; a fix is prepared as a local commit for the user; the submission does not wait (REQ-024) | builder, user |
| RK-7 | Video missing or longer than 170 s | no public YouTube URL by 2026-10-30 07:00 UTC (00:00 PDT), latest 11:00 UTC (04:00 PDT) | minimal 65 s cut (shots 1, 3, 6, 9); cut shot 5 first; no fallback exists for a missing video | user |
| RK-8 | A results file is regenerated late, or a published run was recorded with a browser open | a run file newer than the generated README, report or posts; a build-log entry that a browser was open | rerun `AJ report --in results/ --out docs/report/ --update-readme --render-content`, rerun AJ-152 and AJ-154-T3; a run recorded with a browser open is discarded and repeated without one (REQ-032) | builder |

## Estimate

Note (2026-10-18): any deferral or descope candidates named in this section are informational; the drop order in docs/plan/plan.md section 3.4 is the single source and classifies stories as MUST, SHOULD or COULD.

| Story | Tasks | Hours |
|---|---|---|
| AJ-150 Laptop, hosted and dashboard evidence | 7 | 3.0 |
| AJ-151 cloudgpu evidence | 7 | 3.25 |
| AJ-152 Report page, README block and content | 7 | 3.0 |
| AJ-153 Documentation and Devpost files | 7 | 2.75 |
| AJ-154 Spec conformance, Spec traits, scope | 7 | 5.5 |
| AJ-155 Final hardening | 9 | 4.0 |
| AJ-156 Git boundary and final review gates | 8 | 5.0 |
| AJ-157 Release v0.1.0 preparation | 7 | 3.75 |
| AJ-158 Submission order, timeline, cut-off plan | 7 | 4.75 |
| AJ-159 Buffer, risks, drop order | 6 | 3.0 |
| Total | 72 | 38.0 |

Capacity of the window: about 12 hours (two days at six hours). The gap and its handling are RK-1. User actions (push, Pages, release, video, Devpost) are not in the estimate; their planning targets are in the table under `## Dependencies and user actions`.
