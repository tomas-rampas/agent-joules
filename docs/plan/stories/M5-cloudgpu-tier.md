# M5: cloudgpu tier

M5 adds the second measured tier: Nemotron 3 Nano 30B-A3B served by a NIM container in FP8 on one on-demand NVIDIA L40S 48 GB inside a Nebius AI Cloud virtual machine, with the energy meter running on the same machine. The builder works over SSH only; the user creates the account, the VM and its deletion. The milestone ends with a schema-valid cloudgpu run file, a committed cloudgpu probe, `docs/cloudgpu-run.md` with cost at or below USD 30 and a confirmed teardown, and the consequences for the report, README and Devpost texts when the tier is only partial.

## Dates and exit criteria

Dates: 2026-10-26 to 2026-10-27. M5 starts only after AJ-115 (local Definition of Done items) is committed. The VM time box (4 hours wall-clock and USD 30 total spend, hard stop) runs from the moment the user hands over `CLOUDGPU_SSH_HOST`; AJ-125 to AJ-127 are the stories that consume it. Planned split: 2026-10-26 AJ-120 to AJ-124 (account, key pair, unit-tested guards, no cost yet); 2026-10-27 AJ-125 to AJ-129 (VM, NIM, runs, teardown, consequences). Capacity is about six working hours per day; the VM box is wall-clock and overlaps the start of M6.

Exit criteria:
- [ ] `jq '[.environment.provider, .environment.precision, .environment.instancePreset]'` on the latest `results/*-cloudgpu.json` (excluding `results/probe-*` and `results/calibration-*`) prints `nebius-ai-cloud`, `FP8` and a non-empty preset; `AJ validate results/` exits 0 and prints `Validated <n> files` with n equal to the number of committed run files.
- [ ] `results/probe-<yyyyMMddTHHmmssZ>-cloudgpu.json` exists with a boolean `energyCounterSupported` and a `migMode` of `disabled` or `not-supported`.
- [ ] `docs/cloudgpu-run.md` has the headings `Instance`, `Commands`, `Probe`, `Runs`, `Cost` and `Teardown`; its `Cost` section contains `USD` and a value of 30 or less; its `Teardown` section contains the word `deleted`.
- [ ] `grep -F "$NGC_API_KEY" docs/cloudgpu-run.md` prints nothing (key set in the shell); `git grep -nF "$CLOUDGPU_SSH_HOST"` prints nothing.
- [ ] The `## cloudgpu` section of `docs/endpoints.md` has `Verified: <date>` (2026-10-17 or later) and a `- model: <id>` line; `AJ validate --config` exits 0 with `AGENTJOULES__Tiers__cloudgpu__Enabled=true`.
- [ ] `dotnet test --filter "Spec=REQ-031"`, `--filter "Spec=EDGE-015"`, `--filter "Spec=EDGE-016"` and `--filter "Spec=EDGE-017"` each show at least one passed and no failed test.
- [ ] `AJ report --in results/ --out <scratch folder>` renders a `cloudgpu` row separate from every laptop row, with the word `partial` when the run status is `partial` and `sampling` when the counter was unsupported.
- [ ] The VM and its boot disk are deleted by the user (confirmation time and final cost recorded in `docs/cloudgpu-run.md`).

## Coverage

REQ-031; EDGE-015, EDGE-016, EDGE-017.

## Dependencies and user actions

Needs: M4 complete (AJ-115 committed); M1 (NVML binding with the Linux resolver, `SampledEnergyMeter`, probe); M2 (`MeteredChatClient`, `PreflightService`, `RunOrchestrator`, and AJ-056-T6a for the incomplete marking and checkpoint on host shutdown, with the AJ-034 signal path from M1); M3 (schema with the cloudgpu environment fields, `RunFileSelector`, report, judge, cost); the shared `CloudGpuBudgetGuard` options. The live README row check for EDGE-017 depends on the README block generator of M6 (see the Conflict line under AJ-129).

User actions from prompt section 12:

| Step | Action | Story |
|---|---|---|
| 10 | Create the Nebius AI Cloud account at the AI Cloud console, a project in `eu-north1`, add a card, pay the USD 25 minimum first payment | AJ-120 |
| 11 | Create the SSH key pair with `ssh-keygen -t ed25519`, private key outside the repository | AJ-120 |
| 12 | When the builder asks: create the VM (platform `gpu-l40s-a`, preset `1gpu-16vcpu-64gb`, fallbacks `1gpu-8vcpu-32gb` on the same platform then platform `gpu-l40s-d` with preset `1gpu-16vcpu-96gb`, region `eu-north1`, image `ubuntu24.04-cuda12`, boot disk of at least 150 GB, public IP on, preemptible off, your SSH public key), read the hourly price, set `CLOUDGPU_SSH_HOST` and `CLOUDGPU_SSH_USER` | AJ-125 |
| 13 | After the builder reports the runs copied: delete the VM and its boot disk, read the final cost on the billing page, give the deletion time and the cost to the builder; stop earlier if the billing page shows USD 25 | AJ-128 |

Also needed: `NGC_API_KEY` (step 7) and `NVIDIA_API_KEY` (step 6, the judge key) set on the laptop; they reach the VM only as environment of the SSH session.

## Stories

Note (2026-10-25, M5 session): feature files follow the repository convention from M1, tests/AgentJoules.Specs/Features/<Capability>/AJ-nnn-<slug>.feature; the file names in the story tables (for example Prerequisites.feature) map to that form.

### AJ-120: Create the AI Cloud account, card and SSH key pair
Covers: REQ-031
Note (2026-10-25, AJ-120): feature file tests/AgentJoules.Specs/Features/CloudGpu/AJ-120-prerequisites.feature; steps 10 and 11 confirmed by the user at 18:11Z; the key hygiene check is KeyHygieneCheck in TestSupport (file names only, contents never read); .gitignore already held the three patterns; no VM requested.
User story: As the user, I want to create the Nebius AI Cloud account, fund it with the minimum payment and create an SSH key pair outside the repository, so that the builder can start the cloudgpu tier the moment the VM is requested.
Acceptance criteria:
1. The user confirms in the chat that the AI Cloud account exists (a separate product and sign-up from Token Factory), that a project exists in region `eu-north1`, that a card is added and that the USD 25 minimum first payment is made; the builder records the confirmation as a dated line without any account detail in `docs/build-log.md`.
2. The user is told once, in the request, that hackathon and Builders credits do not apply to AI Cloud, that the free trial is suspended, that the total cap is USD 30 and that runs stop when the billing page shows USD 25; the request text is recorded in `docs/build-log.md` and `grep -c "do not apply to AI Cloud" docs/build-log.md` prints 1 or more.
3. The key pair is created with `ssh-keygen -t ed25519`; the private key is outside the repository: `git ls-files | grep -E "id_ed25519|id_rsa|\.pem"` prints nothing, and `git check-ignore id_ed25519 id_rsa key.pem` prints all three names.
4. No VM is created in this story; the builder does not ask for the VM until AJ-121 to AJ-124 are committed; `test ! -e docs/cloudgpu-run.md` exits 0 when this story ends (the document is first written in AJ-125-T4).
BDD scenarios:
```gherkin
@integration @AJ-S120 @REQ-031
Feature: Create the AI Cloud account, card and SSH key pair
  @positive
  Scenario: AJ-120 the user confirms the funded account
    Given the user has created the AI Cloud account and project and paid the minimum first payment
    When the user confirms in the chat
    Then docs/build-log.md records a dated confirmation line without account details
  @positive
  Scenario: AJ-120 the key pair stays outside the repository
    Given the user ran "ssh-keygen -t ed25519" and stored the private key outside the repository
    When the builder lists the tracked files
    Then no tracked file name contains "id_ed25519", "id_rsa" or ".pem"
    And "git check-ignore" prints all three ignore patterns
  @negative
  Scenario: AJ-120 a private key inside the repository is rejected
    Given a file named "id_ed25519" exists in the repository working tree
    When the builder runs the key hygiene check
    Then the check fails and the file is reported without printing its content
  @negative
  Scenario: AJ-120 no card or payment blocks the tier
    Given the user has not added a card or made the first payment
    When the builder prepares the VM request
    Then the request is not sent
    And the final report lists the cloudgpu tier as blocked on step 10
  @negative
  Scenario: AJ-120 hackathon credits are assumed to cover AI Cloud
    Given the user expects Token Factory credits to pay for the VM
    When the builder states the cost rules
    Then the user is told that the credits do not apply and that the USD 25 payment is required
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-120-T1 | Write the failing feature and the key hygiene check | tests/AgentJoules.Specs/Features/CloudGpu/Prerequisites.feature, tests/AgentJoules.IntegrationTests/CloudGpuPrerequisitesTests.cs | 0.5 | AJ-115-T6 |
| AJ-120-T2 | USER ACTION: create the AI Cloud account and project in `eu-north1`, add a card, pay the USD 25 minimum (section 12 step 10) | none (user accounts) | 0.5 | - |
| AJ-120-T3 | USER ACTION: create the key pair with `ssh-keygen -t ed25519` outside the repository (section 12 step 11) | none (user machine) | 0.1 | - |
| AJ-120-T4 | Confirm the ignore patterns for private keys | .gitignore | 0.25 | AJ-120-T1 |
| AJ-120-T5 | Record the dated confirmations and the cost rules | docs/build-log.md | 0.25 | AJ-120-T2, AJ-120-T3, AJ-120-T4 |
Test mapping: tests/AgentJoules.Specs/Features/CloudGpu/Prerequisites.feature; gated class CloudGpuPrerequisitesTests (design 6.3).
Docs impact: .gitignore, docs/build-log.md.
Definition of done:
- [x] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [x] docs listed above updated
- [x] the user confirmation of steps 10 and 11 is recorded before AJ-125 starts
Demo relevance: no.

### AJ-121: Gate the cloudgpu tier on SSH variables, MIG and the tier flag
Covers: REQ-031
Note (2026-10-25, AJ-121): feature file tests/AgentJoules.Specs/Features/CloudGpu/AJ-121-gating.feature; no production change: the SSH-variable exit 3, the MIG exit 6, the rc 3 not-supported mapping and the enum tier order existed; 9 unit tests and 8 scenarios confirm T2 to T4.
User story: As the author, I want the CLI to refuse a cloudgpu run when the SSH variables are missing on the laptop or MIG is enabled on the GPU, and to leave the tier out of `--tier all` until it is enabled, so that the laptop dry-run stays green and a shared GPU never produces power numbers.
Acceptance criteria:
1. On the laptop with `Tiers:cloudgpu:Enabled=true` and `CloudGpu:RequireSshVariables=true`, `AJ run --tier cloudgpu --dry-run` exits 3 when `CLOUDGPU_SSH_HOST` or `CLOUDGPU_SSH_USER` is unset, names every missing variable, and prints no value; the exit code equals the code of a missing tier key.
2. With `CloudGpu:RequireSshVariables=false` (the VM) the SSH variables are not required.
3. A probe with `migMode` `enabled` makes a cloudgpu run exit 6 naming MIG; `disabled` and `not-supported` (NVML return code 3) pass.
4. With the committed configuration (`Tiers:cloudgpu:Enabled=false`) `AJ run --tier all --dry-run` exits 0 and lists no cloudgpu line; with `AGENTJOULES__Tiers__cloudgpu__Enabled=true` the order is local, cloudgpu, nvidia, nebius.
5. `dotnet test --filter "Spec=REQ-031" --logger "console;verbosity=normal"` shows at least one passed and no failed test.
BDD scenarios:
```gherkin
@unit @AJ-S121 @REQ-031
Feature: Gate the cloudgpu tier on SSH variables, MIG and the tier flag
  @positive
  Scenario: AJ-121 both SSH variables set passes the gate
    Given CloudGpu:RequireSshVariables is true and both SSH variables are set to runtime-built fake values
    When I run "run --tier cloudgpu --dry-run"
    Then the SSH variable check passes
  @positive
  Scenario: AJ-121 the VM does not require the SSH variables
    Given CloudGpu:RequireSshVariables is false and the SSH variables are unset
    When I run "run --tier cloudgpu --dry-run"
    Then the SSH variable check passes
  @positive
  Scenario: AJ-121 MIG not supported counts as MIG off
    Given the fake NVML returns return code 3 for the MIG mode
    When I run "run --tier cloudgpu --dry-run"
    Then the MIG check passes and the probe reports migMode "not-supported"
  @positive
  Scenario: AJ-121 an enabled tier is placed second in the all-tier order
    Given Tiers:cloudgpu:Enabled is true
    When I run "run --tier all --dry-run"
    Then the tiers are listed in the order local, cloudgpu, nvidia, nebius
  @negative
  Scenario: AJ-121 a missing host variable exits 3
    Given CloudGpu:RequireSshVariables is true and CLOUDGPU_SSH_HOST is unset
    When I run "run --tier cloudgpu --dry-run"
    Then the exit code is 3
    And the output names "CLOUDGPU_SSH_HOST"
  @negative
  Scenario: AJ-121 both SSH variables missing are reported in one message
    Given both SSH variables are unset and RequireSshVariables is true
    When I run "run --tier cloudgpu --dry-run"
    Then the exit code is 3
    And the output names "CLOUDGPU_SSH_HOST" and "CLOUDGPU_SSH_USER" and prints no value
  @negative
  Scenario: AJ-121 MIG enabled exits 6
    Given the fake NVML reports MIG mode enabled
    When I run "run --tier cloudgpu --dry-run"
    Then the exit code is 6
    And the output names MIG
  @negative
  Scenario: AJ-121 the committed configuration leaves cloudgpu out of the all-tier run
    Given the committed configuration with Tiers:cloudgpu:Enabled false
    When I run "run --tier all --dry-run"
    Then the exit code is 0
    And no line for "cloudgpu" is printed
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-121-T1 | Write the failing feature and unit tests | tests/AgentJoules.Specs/Features/CloudGpu/Gating.feature, tests/AgentJoules.UnitTests/CloudGpuTests.cs | 0.75 | AJ-120-T5 |
| AJ-121-T2 | Implement the SSH variable check in the preflight | src/AgentJoules.Workflow/Runs/PreflightService.cs, src/AgentJoules.Core/Configuration/CloudGpuOptions.cs | 0.75 | AJ-121-T1 |
| AJ-121-T3 | Implement the MIG precondition and the probe field | src/AgentJoules.Nvml/NvmlGpuTelemetrySource.cs, src/AgentJoules.Core/Gpu/ProbeResult.cs | 0.75 | AJ-121-T1 |
| AJ-121-T4 | Confirm the enabled-tier expansion order of `--tier all` | src/AgentJoules.Workflow/Runs/RunOrchestrator.cs | 0.5 | AJ-121-T2 |
| AJ-121-T5 | Bind the feature steps | tests/AgentJoules.Specs/Steps/CloudGpuSteps.cs | 0.5 | AJ-121-T4 |
| AJ-121-T6 | Note the gate rules and the environment variables | docs/build-log.md | 0.25 | AJ-121-T5 |
Test mapping: tests/AgentJoules.Specs/Features/CloudGpu/Gating.feature; unit class CloudGpuTests (design 6.1) with `[Trait("Spec","REQ-031")]`.
Docs impact: docs/build-log.md; README exit-code row 3 and 6 notes for cloudgpu (written in M6).
Definition of done:
- [x] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [x] docs listed above updated
- [x] no test prints or asserts a real SSH host or user value
Demo relevance: no.

### AJ-122: Enforce the 4-hour and USD 25 stop with a fake clock
Covers: EDGE-017
Note (2026-10-25, AJ-122): feature file tests/AgentJoules.Specs/Features/CloudGpu/AJ-122-budget-guard.feature; CloudGpuBudgetGuard with BudgetDecision(Stop, Reason, Note); the budget lines go to stderr with a Warning prefix; a budget stop ends the whole plan and names the skipped tiers (fix round 1); TierStartUtc needs an offset, a future start exits 2, TimeBoxHours at most 4; the judge and the final write still run after a mid-run stop; the cloudgpu EnvironmentInfo fields are filled from CloudGpuOptions here for AJ-127.
User story: As the user paying for the VM, I want the run to stop starting new work once the billing page shows USD 25 or four hours have passed, so that the total spend stays below the USD 30 cap.
Acceptance criteria:
1. `CloudGpuBudgetGuard.Check()` returns `Stop` with reason `cost-cap` when `CloudGpu:ObservedCostUsd` is at least `CloudGpu:CostStopUsd` (25) and `time-box` when `now - CloudGpu:TierStartUtc` is at least `CloudGpu:TimeBoxHours` (4), otherwise `Continue`; when both hold at the same check the reason is `cost-cap`.
2. Before a cloudgpu run and before every (task, repeat) the orchestrator calls the guard: a stop before the run exits 6 with the reason; a stop between iterations starts no further iteration, writes the run file with `run.status` `partial` and `run.stopReason` set, exits 0 and prints a warning that tells the user to delete the VM.
3. The observed cost is an input the user reports from the billing page; the builder sets it through `AGENTJOULES__CloudGpu__ObservedCostUsd` before each invocation. A `CostStopUsd` above 30 fails configuration validation with exit 2.
4. The `Cost` section of `docs/cloudgpu-run.md` states the stop reason (AJ-128); the README row for `cloudgpu` contains `partial` (AJ-129).
5. `dotnet test --filter "Spec=EDGE-017" --logger "console;verbosity=normal"` shows at least one passed and no failed test; tests use `FakeTimeProvider`, not the wall clock.
BDD scenarios:
```gherkin
@unit @AJ-S122 @EDGE-017
Feature: Enforce the 4-hour and USD 25 stop
  @positive
  Scenario: AJ-122 a run under both limits continues
    Given the observed cost is 12.40 USD and 1 hour has passed since the tier start
    When the guard is checked
    Then the decision is Continue
  @positive
  Scenario: AJ-122 one second before four hours still continues
    Given the observed cost is 6.00 USD and 3 hours 59 minutes 59 seconds have passed
    When the guard is checked
    Then the decision is Continue
  @negative
  Scenario: AJ-122 an observed cost of 25 USD stops new work
    Given the observed cost is 25.00 USD
    When the guard is checked before the run
    Then the decision is Stop with reason "cost-cap"
    And "run --tier cloudgpu" exits 6 and starts no workflow call
  @negative
  Scenario: AJ-122 four hours stop new work
    Given 4 hours have passed since the tier start and the observed cost is 6.00 USD
    When the guard is checked before the next repeat
    Then the decision is Stop with reason "time-box"
  @negative
  Scenario: AJ-122 a mid-run stop keeps the finished repeats as a partial file
    Given repeats 1 and 2 of task one finished and the time box expires before repeat 3
    When the orchestrator checks the guard
    Then no further repeat starts
    And the run file has run.status "partial" and run.stopReason "time-box"
    And the exit code is 0 and the output tells the user to delete the VM
  @negative
  Scenario: AJ-122 cost cap wins when both limits hold
    Given the observed cost is 25.50 USD and 4 hours have passed
    When the guard is checked
    Then the stop reason is "cost-cap"
  @negative
  Scenario: AJ-122 a stop threshold above the cap is rejected
    Given CloudGpu:CostStopUsd is 31
    When I run "run --tier cloudgpu --dry-run"
    Then the exit code is 2
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-122-T1 | Write the failing feature and unit tests | tests/AgentJoules.Specs/Features/CloudGpu/BudgetGuard.feature, tests/AgentJoules.UnitTests/CloudGpuTests.cs | 0.75 | AJ-121-T6 |
| AJ-122-T2 | Implement the budget guard decision | src/AgentJoules.Core/Runs/CloudGpuBudgetGuard.cs | 0.75 | AJ-122-T1 |
| AJ-122-T3 | Call the guard before the run and before every iteration | src/AgentJoules.Workflow/Runs/RunOrchestrator.cs | 0.75 | AJ-122-T2 |
| AJ-122-T4 | Write the partial status, stop reason and warning text | src/AgentJoules.Workflow/Runs/RunAssembler.cs, src/AgentJoules.Cli/Output/ConsoleReporter.cs | 0.5 | AJ-122-T3 |
| AJ-122-T5 | Validate the stop threshold against the cap | src/AgentJoules.Core/Configuration/CrossFieldValidator.cs | 0.5 | AJ-122-T2 |
| AJ-122-T6 | Bind the feature steps | tests/AgentJoules.Specs/Steps/CloudGpuSteps.cs | 0.5 | AJ-122-T4, AJ-122-T5 |
| AJ-122-T7 | Document the stop procedure | docs/build-log.md | 0.25 | AJ-122-T6 |
Test mapping: tests/AgentJoules.Specs/Features/CloudGpu/BudgetGuard.feature; unit class CloudGpuTests (design 6.1) with `[Trait("Spec","EDGE-017")]`.
Docs impact: docs/build-log.md (stop procedure: stop runs, copy results, tell the user to delete the VM, record the reason); `docs/cloudgpu-run.md` Cost section in AJ-128.
Definition of done:
- [x] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [x] docs listed above updated
- [x] no test waits on the wall clock
Demo relevance: no.

### AJ-123: Keep incomplete cloudgpu run files out of reports
Covers: EDGE-016
Note (2026-10-25, AJ-123): feature file tests/AgentJoules.Specs/Features/CloudGpu/AJ-123-incomplete-runs.feature; criterion 2 read literally: the fan-out sibling is stopped at the meter gate after a cloudgpu drop (StopAfterDropMeterGate), so the run file holds the Planner and the dropped step only; the connection-lost line is a listener method; seven production files changed (the gate and its IStoppableMeterGate interface, MeteredChatClient, the orchestrator, the listener, the console reporter and listener) plus the workflow factory's failure choice from fix round 1.
User story: As a reader of the report, I want a run that was cut off by a lost VM or a broken connection to be kept as evidence but never shown as a result, so that no partial energy figure is read as complete.
Acceptance criteria:
1. A transport failure during a streaming call (an `IOException`, a `SocketException` or an `HttpRequestException` without a status code) yields a step with `outcome` `incomplete`; host shutdown (Ctrl+C, SIGTERM, SIGHUP) is not re-specified here: AJ-034 (M1) delivers the `ApplicationStopping` signal path and the exit-5 mapping, and AJ-056-T6a (M2) marks in-flight steps `incomplete`, sets `run.status` `incomplete` and writes the checkpoint; this story reuses both; the run file gets `run.status` `incomplete`, is kept on disk, the command exits 5 and asks the user to check the VM state in the console.
2. After the drop the code makes no further call and no retry (test `NoRetryAfterDrop`: the fake counts zero additional attempts even when the time box and the cost cap allow more).
3. `RunFileSelector.Latest(dir, cloudgpu)` skips files with status `incomplete`, `running` and `failed`; a report fixture holding such a file renders no row for it and, with no other file, renders `cloudgpu | not run`; the README results block fixture excludes it.
4. A hard VM loss that never wrote `incomplete` leaves a copied file with `run.status` `running`; it is treated the same way and recorded in `docs/cloudgpu-run.md`.
5. HTTP 429 exhaustion stays `outcome` `error` (exit 5), not `incomplete`; `dotnet test --filter "Spec=EDGE-016" --logger "console;verbosity=normal"` shows at least one passed and no failed test.
BDD scenarios:
```gherkin
@unit @AJ-S123 @EDGE-016
Feature: Keep incomplete cloudgpu run files out of reports
  @positive
  Scenario: AJ-123 a mid-stream connection drop yields one incomplete step
    Given a fake transport that drops the connection during the second agent call
    When I run "run --tier cloudgpu --repeat 1 --task incident-brief"
    Then the second step has outcome "incomplete" and run.status is "incomplete"
    And the exit code is 5
    And the output asks the user to check the VM state in the console
  @positive
  Scenario: AJ-123 a completed file is unaffected
    Given a completed cloudgpu run file and an older incomplete one
    When the latest file for tier "cloudgpu" is selected
    Then the completed file is returned
  @negative
  Scenario: AJ-123 no retry follows the drop
    Given a fake transport that drops the connection and ample time and budget remain
    When the run stops
    Then the fake recorded no attempt after the drop
  @negative
  Scenario: AJ-123 the report renders no row for an incomplete file
    Given a report input whose only cloudgpu file has status "incomplete"
    When the report is rendered
    Then the summary contains "cloudgpu | not run" and no cloudgpu step row
  @negative
  Scenario: AJ-123 a file left as running is skipped
    Given a copied cloudgpu file with run.status "running"
    When the latest file for tier "cloudgpu" is selected
    Then no file is returned
  @negative
  Scenario: AJ-123 rate-limit exhaustion is an error and not incomplete
    Given the fake endpoint answers HTTP 429 five times
    When I run "run --tier cloudgpu --repeat 1 --task incident-brief"
    Then the failed step has outcome "error"
    And run.status is "failed"
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-123-T1 | Write the failing feature and unit tests | tests/AgentJoules.Specs/Features/CloudGpu/IncompleteRuns.feature, tests/AgentJoules.UnitTests/IncompleteRunTests.cs | 0.75 | AJ-122-T7 |
| AJ-123-T2 | Add the mid-stream drop mode to the fake HTTP handler | tests/AgentJoules.TestSupport/FakeHttpMessageHandler.cs | 0.5 | AJ-123-T1 |
| AJ-123-T3 | Confirm the incomplete classification of transport failures | src/AgentJoules.Metering/OutcomeClassifier.cs | 0.5 | AJ-123-T2 |
| AJ-123-T4 | Stop without retry after a transport drop, reusing the AJ-056-T6a incomplete marking | src/AgentJoules.Workflow/Runs/RunOrchestrator.cs | 0.75 | AJ-123-T3, AJ-056-T6a |
| AJ-123-T5 | Skip incomplete, running and failed files in selection and rendering | src/AgentJoules.Reporting/Results/RunFileSelector.cs, src/AgentJoules.Reporting/Render/ReportAggregator.cs | 0.5 | AJ-123-T4 |
| AJ-123-T6 | Bind the feature steps | tests/AgentJoules.Specs/Steps/CloudGpuSteps.cs | 0.5 | AJ-123-T5 |
| AJ-123-T7 | Document the incident handling rule | docs/build-log.md | 0.25 | AJ-123-T6 |
Test mapping: tests/AgentJoules.Specs/Features/CloudGpu/IncompleteRuns.feature; unit classes IncompleteRunTests and ReportRenderingTests (REQ-023 list) with `[Trait("Spec","EDGE-016")]`.
Docs impact: docs/build-log.md; the incident entry in `docs/cloudgpu-run.md` in AJ-128.
Definition of done:
- [x] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [x] docs listed above updated
- [x] an incomplete file never appears in a README results fixture
Demo relevance: no.

### AJ-124: Use power sampling when the energy counter is unsupported on the VM
Covers: EDGE-015
Note (2026-10-25, AJ-124): feature file tests/AgentJoules.Specs/Features/CloudGpu/AJ-124-sampling-fallback.feature; the calibration suffix comes from a new calibrate --suffix option (the command has no tier option); the probe suffix, the sampling fallback, the header line and the README row word existed.
User story: As a reader of the cloudgpu row, I want the report to say when the VM GPU gave no energy counter and sampling was used instead, so that the method behind the number is never hidden and nothing is estimated to fill a gap.
Acceptance criteria:
1. With the fake NVML returning NOT_SUPPORTED for the counter and the tier configured as `cloudgpu`, every reading has `method` `sampling` and the flag `sampling-fallback`, and the console prints the fallback line; with the counter supported the method is `counter` and the flag is absent.
2. The calibration result has `counterJoules` as `{value:null,label:"N/A"}` with a non-empty `reason` taken from the probe, `relativeDifference` N/A, and `samplingJoules` measured; nothing is estimated.
3. `AJ probe --json --save --suffix cloudgpu` writes `results/probe-<yyyyMMddTHHmmssZ>-cloudgpu.json` and `AJ calibrate` on the cloudgpu tier writes `results/calibration-<yyyyMMddTHHmmssZ>-cloudgpu.json`.
4. The report header contains `Energy method: sampling (counter unsupported)` for the cloudgpu tier and the README block fixture row for `cloudgpu` contains the word `sampling`.
5. A forced `Meter:Method=counter` with an unsupported counter exits 6; `dotnet test --filter "Spec=EDGE-015" --logger "console;verbosity=normal"` shows at least one passed and no failed test.
BDD scenarios:
```gherkin
@unit @AJ-S124 @EDGE-015
Feature: Use power sampling when the energy counter is unsupported on the VM
  @positive
  Scenario: AJ-124 a supported counter keeps the counter method
    Given the fake NVML supports the energy counter and the tier is "cloudgpu"
    When I run "run --tier cloudgpu --repeat 1 --task incident-brief"
    Then every reading has method "counter" and no "sampling-fallback" flag
  @positive
  Scenario: AJ-124 the suffix names the probe and calibration files
    Given the tier is "cloudgpu"
    When I run "probe --json --save --suffix cloudgpu" and "calibrate --seconds 60"
    Then the files match "probe-<yyyyMMddTHHmmssZ>-cloudgpu.json" and "calibration-<yyyyMMddTHHmmssZ>-cloudgpu.json"
  @positive
  Scenario: AJ-124 an unsupported counter falls back to sampling with the flag
    Given the fake NVML returns NOT_SUPPORTED for the counter and the tier is "cloudgpu"
    When I run "run --tier cloudgpu --repeat 1 --task incident-brief"
    Then every reading has method "sampling" and the flag "sampling-fallback"
    And the console shows the fallback line
  @positive
  Scenario: AJ-124 the calibration states the counter as N/A with the probe reason
    Given the fake NVML returns NOT_SUPPORTED for the counter
    When I run "calibrate --seconds 60"
    Then counterJoules is null with label "N/A" and a non-empty reason
    And relativeDifference is null with label "N/A"
  @positive
  Scenario: AJ-124 the report and README fixture disclose sampling
    Given a cloudgpu run file with method "sampling"
    When the report and the README block are rendered
    Then the header contains "Energy method: sampling (counter unsupported)"
    And the cloudgpu README row contains "sampling"
  @negative
  Scenario: AJ-124 a forced counter method with no counter exits 6
    Given Meter:Method is "counter" and the counter is unsupported
    When I run "run --tier cloudgpu --dry-run"
    Then the exit code is 6
  @negative
  Scenario: AJ-124 an unsupported counter never yields a zero counter value
    Given the fake NVML returns NOT_SUPPORTED for the counter
    When I run "calibrate --seconds 60"
    Then counterJoules has a null value and the file contains no counterJoules value of 0
  @negative
  Scenario: AJ-124 a forced sampling method is not labelled as a fallback
    Given Meter:Method is "sampling" and the counter is supported
    When I run "run --tier cloudgpu --repeat 1 --task incident-brief"
    Then every reading has method "sampling" and no "sampling-fallback" flag
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-124-T1 | Write the failing feature and unit tests | tests/AgentJoules.Specs/Features/CloudGpu/SamplingFallback.feature, tests/AgentJoules.UnitTests/CalibrationTests.cs | 0.75 | AJ-123-T7 |
| AJ-124-T2 | Add the `--suffix` option to the probe command | src/AgentJoules.Cli/Commands/ProbeCommand.cs | 0.5 | AJ-124-T1 |
| AJ-124-T3 | Add the cloudgpu suffix to the calibration file name and the N/A reason path | src/AgentJoules.Workflow/Runs/CalibrationService.cs | 0.5 | AJ-124-T1 |
| AJ-124-T4 | Confirm the fallback method and flag for the cloudgpu tier | src/AgentJoules.Core/Energy/SampledEnergyMeter.cs | 0.5 | AJ-124-T1 |
| AJ-124-T5 | Render the sampling line in the header and the README row word | src/AgentJoules.Reporting/Render/SummaryMarkdownRenderer.cs, src/AgentJoules.Reporting/Readme/ReadmeResultsBlock.cs | 0.5 | AJ-124-T4 |
| AJ-124-T6 | Bind the feature steps | tests/AgentJoules.Specs/Steps/CloudGpuSteps.cs | 0.5 | AJ-124-T2, AJ-124-T3, AJ-124-T5 |
| AJ-124-T7 | Document the suffix and the fallback disclosure | docs/build-log.md | 0.25 | AJ-124-T6 |
Test mapping: tests/AgentJoules.Specs/Features/CloudGpu/SamplingFallback.feature; unit classes CalibrationTests and PowerIntegrationTests (design 6.1) with `[Trait("Spec","EDGE-015")]`.
Docs impact: docs/build-log.md; README sampling disclosure through the generated block (M6).
Definition of done:
- [x] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [x] docs listed above updated
- [x] no fallback value is estimated or defaulted to 0
Demo relevance: no.

### AJ-125: Create the VM when asked, deliver the CLI and probe the GPU over SSH
Covers: REQ-031
Note (2026-10-25, AJ-125): feature file tests/AgentJoules.Specs/Features/CloudGpu/AJ-125-vm-delivery-and-probe.feature; the code part (T1) shipped before the VM handover; the VM-bound and docs tasks follow.
Note (2026-10-26, AJ-125): the VM is one NVIDIA H100 (platform gpu-h100-sxm, preset 1gpu-16vcpu-200gb, region eu-north1, Regular, USD 4.63/hour incl. disk), created under the user decision of 2026-10-26 after two L40S starts in eu-north1 ended "Resource exhausted"; the probe file results/probe-20261026T122330Z-cloudgpu.json names NVIDIA H100 80GB HBM3 with migMode disabled and energyCounterSupported true. The box starts at the handover stamp 2026-10-26T12:19:28Z and ends 16:19:28Z with the USD 25 stop line; the probe ran on the VM over SSH (see the AJ-125 entry in docs/build-log.md for the deviations).
User story: As the user, I want to create exactly the VM the builder asks for and hand over only the SSH host and user as environment variables, so that the builder can deliver the CLI and prove the GPU is a dedicated L40S (2026-10-26: an H100 under the user decision in criterion 2) before any money is spent on a run.
Acceptance criteria:
1. Before the VM is requested: `dotnet test --filter "Spec=REQ-020"`, `--filter "Spec=REQ-021"` and `--filter "Spec=EDGE-010"` pass and the AJ-115 commit exists (the local Definition of Done items are committed); `dotnet publish src/AgentJoules.Cli -c Release -r linux-x64 --self-contained true -o artifacts/linux-x64` exits 0 on the laptop and `git check-ignore artifacts/linux-x64` prints the path (ADR-14); the delivery choice is recorded in `docs/cloudgpu-run.md`.
2. The builder asks the user, in one message, to create the VM with platform `gpu-l40s-a`, preset `1gpu-16vcpu-64gb` (fallbacks: `1gpu-8vcpu-32gb` on the same platform if unavailable or materially more expensive, then platform `gpu-l40s-d` with preset `1gpu-16vcpu-96gb`; no other GPU class without a new user decision; 2026-10-26 user decision after both L40S starts in eu-north1 ended "Resource exhausted": the candidates were an NVIDIA H100 in eu-north1 and an NVIDIA RTX PRO 6000 in uk-south2; the H100 in eu-north1 was created and is the only accepted GPU besides the L40S, the uk-south2 candidate is not accepted; the one created is recorded in the Instance section), region `eu-north1`, image `ubuntu24.04-cuda12`, boot disk of at least 150 GB, public IP on, preemptible off and the user's SSH public key, and to read the hourly price shown for the preset. The user sets `CLOUDGPU_SSH_HOST` and `CLOUDGPU_SSH_USER`, gives the price (open point O-07 default: the console value at creation wins over the user-observed value in the prompt) and confirms `preemptible: off`; the handover time (UTC) is recorded as `CloudGpu:TierStartUtc` and starts the 4-hour box.
3. Over SSH only, the builder records: `nvidia-smi --query-gpu=name,uuid,driver_version --format=csv` (an L40S name, otherwise stop and ask the user, no other GPU class; 2026-10-26: the stop happened and the user decided, see criterion 2), the boot disk size from `df -h /` as a `<measured>` copy (at least 150 GB, otherwise stop), `docker --version` (the image's Docker presence is not stated; when absent the install commands are recorded and counted in the box), and the NVIDIA Container Toolkit presence.
4. The builder copies `artifacts/linux-x64`, `config/`, `tasks/` and `docs/endpoints.md` with `scp` to `$CLOUDGPU_SSH_USER@$CLOUDGPU_SSH_HOST`; on the VM `./AgentJoules.Cli probe --json --save --suffix cloudgpu` exits 0 and the copied `results/probe-<yyyyMMddTHHmmssZ>-cloudgpu.json` validates against `$defs/probe` with `name` (L40S, or the GPU of the 2026-10-26 user decision in criterion 2) (2026-10-19: AJ-035 pins the probe key `name`), `gpuUuid`, `driverVersion`, `powerLimitW`, `migMode` in {`disabled`, `not-supported`}, a boolean `energyCounterSupported`, `method` and `sampleRateHz`.
5. Dedicated GPU: `nvidia-smi --query-compute-apps=pid,process_name --format=csv` lists no process before NIM starts; MIG enabled stops the story (the builder cannot change it; the user is asked).
6. No host, IP or user value appears in the repository: `git grep -nF "$CLOUDGPU_SSH_HOST"` and `git grep -nF "$CLOUDGPU_SSH_USER"` print nothing; commands in documents show the variable names only. (2026-10-26, spec defect found by the AJ-125 spec review: the user value is a substring of the repository owner's public GitHub handle in URLs committed long before M5, so the bare-value grep can never print nothing; the check is restated as the form the evidence oracle CloudGpuEvidence.RepositoryHoldsSshValue uses: `git grep -nF "$CLOUDGPU_SSH_USER@"` prints nothing and `git grep -nwF "$CLOUDGPU_SSH_HOST"` prints nothing; measured on the pool: both print nothing.)
BDD scenarios:
```gherkin
@integration @AJ-S125 @REQ-031
Feature: Create the VM when asked, deliver the CLI and probe the GPU over SSH
  @positive
  Scenario: AJ-125 the self-contained publish is ready before the VM exists
    Given the local Definition of Done commit exists
    When I run "dotnet publish src/AgentJoules.Cli -c Release -r linux-x64 --self-contained true -o artifacts/linux-x64"
    Then the exit code is 0
    And "git check-ignore artifacts/linux-x64" prints the path
  @positive
  Scenario: AJ-125 the VM matches the request and the probe is committed
    Given the user created the VM as requested and set CLOUDGPU_SSH_HOST and CLOUDGPU_SSH_USER
    When the builder copies the CLI and runs "./AgentJoules.Cli probe --json --save --suffix cloudgpu" on the VM
    Then the probe file has an accepted GPU name, a gpuUuid, migMode "disabled" or "not-supported" and a boolean energyCounterSupported
    And the file validates against the probe schema definition
  @positive
  Scenario: AJ-125 a fallback preset is accepted and recorded
    Given the preferred preset is unavailable and the user created preset "1gpu-8vcpu-32gb" on the same platform
    When the builder records the instance
    Then the Instance section names the preset used and why the fallback was needed
  @negative
  Scenario: AJ-125 a GPU class other than L40S stops the story
    Given nvidia-smi on the VM reports a GPU that is not an L40S
    When the builder checks the instance
    Then the builder stops and asks the user for a new decision
  @negative
  Scenario: AJ-125 a boot disk below 150 GB stops the story
    Given df on the VM reports a boot disk below 150 GB
    When the builder checks the instance
    Then the builder stops and asks the user to recreate the VM with a larger disk
  @negative
  Scenario: AJ-125 MIG enabled stops the story
    Given the VM probe reports migMode "enabled"
    When I run "run --tier cloudgpu --dry-run" on the VM
    Then the exit code is 6
    And the builder asks the user to disable MIG or recreate the VM
  @negative
  Scenario: AJ-125 the SSH variables are missing on the laptop
    Given CLOUDGPU_SSH_HOST is unset on the laptop
    When the builder tries to copy the CLI
    Then no scp command runs and the user is asked to set the variable
  @negative
  Scenario: AJ-125 Docker is absent from the image
    Given "docker --version" fails on the VM
    When the builder prepares the container runtime
    Then the install commands are recorded in the Commands section and counted in the box
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-125-T1 | Write the failing feature and the gated probe test | tests/AgentJoules.Specs/Features/CloudGpu/VmDeliveryAndProbe.feature, tests/AgentJoules.IntegrationTests/CloudGpuProbeTests.cs | 0.75 | AJ-124-T7 |
| AJ-125-T2 | Publish the self-contained linux-x64 CLI and confirm the ignore rule | .gitignore, artifacts/linux-x64 | 0.5 | AJ-125-T1 |
| AJ-125-T3 | USER ACTION: create the VM as requested, read the hourly price, set `CLOUDGPU_SSH_HOST` and `CLOUDGPU_SSH_USER` (section 12 step 12) | none (user cloud console and environment) | 0.5 | AJ-125-T2, AJ-120-T5 |
| AJ-125-T4 | Check GPU class, boot disk, Docker and container toolkit over SSH | docs/cloudgpu-run.md | 0.5 | AJ-125-T3 |
| AJ-125-T5 | Copy the CLI, config, tasks and endpoints file with scp | docs/cloudgpu-run.md | 0.5 | AJ-125-T4 |
| AJ-125-T6 | Run the probe on the VM and copy the file back | results/probe-<yyyyMMddTHHmmssZ>-cloudgpu.json | 0.5 | AJ-125-T5 |
| AJ-125-T7 | Commit the probe file with the `AJ-125 REQ-031:` prefix | results/probe-<yyyyMMddTHHmmssZ>-cloudgpu.json | 0.1 | AJ-125-T6 |
| AJ-125-T8 | Write the Instance and Probe sections of the run document | docs/cloudgpu-run.md | 0.5 | AJ-125-T6 |
Test mapping: tests/AgentJoules.Specs/Features/CloudGpu/VmDeliveryAndProbe.feature; SchemaValidationTests (REQ-023 list) and ProbeCommandTests (design 6.1) for the probe file; gated class CloudGpuProbeTests (design 6.3).
Docs impact: docs/cloudgpu-run.md (`Instance`, `Probe`, delivery choice), .gitignore, results/probe-<yyyyMMddTHHmmssZ>-cloudgpu.json.
Definition of done:
- [ ] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [ ] the handover time and the hourly price are recorded in the Instance section
Demo relevance: yes: the cloudgpu probe shows the L40S (2026-10-26: the H100 of the user decision), the MIG state and the counter support next to the laptop probe.

### AJ-126: Serve Nemotron 3 Nano in FP8 with NIM on the VM
Covers: REQ-031
Note (2026-10-25, AJ-126): feature file tests/AgentJoules.Specs/Features/CloudGpu/AJ-126-nim-serving.feature; the code part (T1) shipped before the VM handover; the VM-bound and docs tasks follow.
Note (2026-10-26, AJ-126): NIM 2.1.4 serves nvidia/nemotron-3-nano on the VM (profile vllm-fp8-tp1-pp1-34.0, precision FP8, max_model_len 262144, bound to the loopback interface only); the model answers with thinking on by default, returned inline in content, so Tiers:cloudgpu:ExtraBodyJson sets chat_template_kwargs enable_thinking false (the only off switch measured). The calibration results/calibration-20261026T124311Z-cloudgpu.json reads counterJoules 20796.42 and samplingJoules 20679.15 (relativeDifference 0.0056 over 624 samples at 10 Hz).
User story: As a reader of the cloudgpu row, I want the NIM image, its digest and the FP8 profile recorded and verified by a live models call, so that the row names exactly what was served.
Acceptance criteria:
1. `docker login nvcr.io --username '$oauthtoken' --password-stdin` receives `NGC_API_KEY` through the SSH session only (delivered by the session environment or its standard input, never a command-line argument); `DOCKER_CONFIG` points to a memory-backed directory (for example under `/dev/shm`) deleted right after the pull, `docker logout nvcr.io` follows, and the container receives the key by variable name only; `grep -c "docker logout" docs/cloudgpu-run.md` prints 1 or more with the commands shown redacted (prompt section 8 as amended); `docker pull nvcr.io/nim/nvidia/nemotron-3-nano` (a tag recorded, `latest` or a tagged version) succeeds, which confirms the inferred pull string; the digest from `docker image inspect --format '{{index .RepoDigests 0}}'` is recorded as `ContainerDigest`.
2. When that pull path fails, the fallback image `nvcr.io/nim/nvidia/model-free-nim` is used and the substitution is recorded with the error; the builder reports it in the final report.
3. The FP8 profile is selected: the NIM profile listing is recorded and `vllm-fp8-tp1-pp1` (the expected generic profile, inferred) or the listed single-GPU FP8 profile is set through `NIM_MODEL_PROFILE`; BF16 is never used on the 48 GB GPU. When no FP8 profile is listed the builder stops and reports.
4. If the container fails for KV-cache memory, `NIM_MAX_MODEL_LEN` is lowered and the value used is recorded; `GET http://localhost:<port>/v1/models` from the VM returns the model ID, which is recorded in the `## cloudgpu` section of `docs/endpoints.md` with `Verified: <UTC date>`, the command, and the streaming-usage and reasoning-toggle results measured as in AJ-108 (`Reasoning result:` must match the other tiers, or `unsupported` is stated).
5. `AJ validate --config` exits 0 on the VM with `AGENTJOULES__Tiers__cloudgpu__Enabled=true`; during measurement only the NIM process holds the GPU (`nvidia-smi --query-compute-apps=pid,process_name --format=csv`).
6. `grep -F "$NGC_API_KEY" docs/cloudgpu-run.md` and `git grep -nF "$NGC_API_KEY"` print nothing; the document shows `$NGC_API_KEY` as a variable name only.
BDD scenarios:
```gherkin
@integration @AJ-S126 @REQ-031
Feature: Serve Nemotron 3 Nano in FP8 with NIM on the VM
  @positive
  Scenario: AJ-126 the pull succeeds and the digest is recorded
    Given the NGC login on the VM succeeded through standard input
    When the builder pulls "nvcr.io/nim/nvidia/nemotron-3-nano"
    Then the pull string is confirmed and the digest is recorded as ContainerDigest
  @positive
  Scenario: AJ-126 the FP8 profile serves and the models call returns the model ID
    Given the NIM profile listing contains a single-GPU FP8 profile
    When the container starts with NIM_MODEL_PROFILE set to it
    Then GET /v1/models from the VM returns a model ID
    And the "## cloudgpu" section of docs/endpoints.md records the ID, the date and the command
  @negative
  Scenario: AJ-126 the pull path is wrong and the fallback image is used
    Given the pull of "nvcr.io/nim/nvidia/nemotron-3-nano" returns a not-found error
    When the builder uses "nvcr.io/nim/nvidia/model-free-nim"
    Then the Commands section records the error and the substitution
  @negative
  Scenario: AJ-126 no FP8 profile is listed
    Given the NIM profile listing contains only BF16 profiles
    When the builder reads the listing
    Then the builder stops and reports instead of using BF16
  @negative
  Scenario: AJ-126 the container runs out of KV-cache memory
    Given the container exits with a memory error at the default context length
    When the builder sets NIM_MAX_MODEL_LEN to a lower value
    Then the value used is recorded and the models call succeeds
  @negative
  Scenario: AJ-126 the NGC login is rejected
    Given the NGC login returns an authorization error
    When the builder records the result
    Then the error is recorded without any key value
  @negative
  Scenario: AJ-126 another process holds the GPU during measurement
    Given nvidia-smi lists a compute process other than the NIM process
    When the builder checks exclusivity
    Then measurement does not start and the process is recorded
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-126-T1 | Write the failing feature and the gated NIM models test | tests/AgentJoules.Specs/Features/CloudGpu/NimServing.feature, tests/AgentJoules.IntegrationTests/CloudGpuNimTests.cs | 0.5 | AJ-125-T7 |
| AJ-126-T2 | Log in to nvcr.io through the session and pull the image | docs/cloudgpu-run.md | 1.0 | AJ-126-T1 |
| AJ-126-T3 | Record the digest and list the model profiles | docs/cloudgpu-run.md | 0.25 | AJ-126-T2 |
| AJ-126-T4 | Start the container with the FP8 profile and call the models endpoint | docs/cloudgpu-run.md | 1.0 | AJ-126-T3 |
| AJ-126-T5 | Lower the context length cap if the start fails (contingency) | docs/cloudgpu-run.md | 0.5 | AJ-126-T4 |
| AJ-126-T6 | Verify streaming usage and the reasoning toggle on the NIM endpoint | docs/endpoints.md | 0.5 | AJ-126-T4 |
| AJ-126-T7 | Write the cloudgpu endpoints section and the tier model IDs | docs/endpoints.md, config/appsettings.json | 0.5 | AJ-126-T6 |
| AJ-126-T8 | Check GPU exclusivity and run the config validation on the VM | docs/cloudgpu-run.md | 0.25 | AJ-126-T7 |
| AJ-126-T9 | Write the NIM entries of the Instance and Commands sections | docs/cloudgpu-run.md | 0.25 | AJ-126-T8 |
Test mapping: tests/AgentJoules.Specs/Features/CloudGpu/NimServing.feature; ConfigValidationTests (design 6.1) for the endpoints format; gated class CloudGpuNimTests (design 6.3).
Docs impact: docs/endpoints.md (`## cloudgpu`: Verified date, model line, usage and reasoning lines), docs/cloudgpu-run.md (image, digest, profile, context cap), config/appsettings.json (verified ID; `Enabled` stays false in the committed file).
Definition of done:
- [ ] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [x] the `## cloudgpu` section carries a `Verified:` line, which closes the REQ-018 acceptance left open by AJ-100
Demo relevance: no.
Resolved: prompt section 8 (as amended) fixes the login procedure in criterion 1 (memory-backed `DOCKER_CONFIG`, `docker logout nvcr.io`, key by variable name only); the VM and its disk are deleted at teardown (AJ-128).

### AJ-127: Run the cloudgpu workload on the VM and copy the results back
Covers: REQ-031, EDGE-015, EDGE-016, EDGE-017
Note (2026-10-25, AJ-127): feature file tests/AgentJoules.Specs/Features/CloudGpu/AJ-127-cloudgpu-run.feature; the code part (T1) shipped before the VM handover; the VM-bound and docs tasks follow.
Note (2026-10-26, AJ-127): feature file tests/AgentJoules.Specs/Features/CloudGpu/AJ-127-cloudgpu-run.feature; the VM was the H100 of the AJ-125 user decision in eu-north1 (the story's L40S was not offered), and the run completed at 12:49:48Z with 36 steps ok, no budget stop, no partial file and no re-run; three judge quality values are N/A (HTTP 429, reason judge-failed), and cloudCostUsd is not-reported with reason billed-after-run, so the whole-run bill is recorded in docs/cloudgpu-run.md (AJ-128).
User story: As the author, I want the same tasks, warm-up, baseline and repeats as the laptop run executed on the VM inside the box, with the results copied back and validated, so that the cloudgpu row is measured under the same protocol and kept separate from every laptop figure.
Acceptance criteria:
1. On the VM, inside `tmux`, with the session environment `AGENTJOULES__Tiers__cloudgpu__Enabled=true`, `AGENTJOULES__CloudGpu__RequireSshVariables=false`, `AGENTJOULES__CloudGpu__TierStartUtc`, `...__InstancePreset`, `...__Region`, `...__ContainerImage`, `...__ContainerDigest`, `...__HourlyPriceUsd` and the judge key (`NVIDIA_API_KEY`), `./AgentJoules.Cli run --tier cloudgpu --dry-run` exits 0 and its output is recorded in `docs/cloudgpu-run.md`.
2. `./AgentJoules.Cli calibrate` runs when the probe shows `energyCounterSupported` true (otherwise the sampling-only file of AJ-124 is written) and writes `results/calibration-<yyyyMMddTHHmmssZ>-cloudgpu.json`; `./AgentJoules.Cli run --tier cloudgpu --repeat 3` writes `results/<yyyyMMddTHHmmssZ>-cloudgpu.json` with `run.tasks` of 3, `run.repeat` 3, 36 steps, `baseline.seconds` of at least 30, `environment.provider` `nebius-ai-cloud`, `environment.precision` `FP8`, a non-empty `instancePreset`, `region` `eu-north1` (2026-10-26: the uk-south2 candidate of the AJ-125 user decision was not chosen and is not accepted), `gpuUuid`, `containerImage` and a `containerDigest` starting `sha256:`; temperature 0 and the reasoning mode equal to the other tiers.
3. The guard of AJ-122 is checked before each invocation with the user-reported `ObservedCostUsd`; at USD 25 or four hours no new invocation starts and the file is `partial` (EDGE-017); a lost VM or broken connection leaves an `incomplete` file that is excluded and not retried (EDGE-016); a dropped SSH session does not stop the run because it lives in `tmux`; a host shutdown on the VM uses the AJ-034 `ApplicationStopping` signal path and exit-5 mapping with the AJ-056-T6a marking (steps `incomplete`, `run.status` `incomplete`, checkpoint written) and is not re-specified here.
4. The results are copied back with `scp` and `AJ validate results/` exits 0 on the laptop, printing `Validated <n> files` with n equal to the number of committed run files; the report keeps the cloudgpu row separate from the laptop rows (no merged or averaged figure).
5. Keys appear only in the session environment: `git grep -nF "$NVIDIA_API_KEY"` and `git grep -nF "$NGC_API_KEY"` print nothing; no command in the documents carries a key value; the files are committed with a message starting `AJ-127 REQ-031:` (story id first; check regex `^AJ-[0-9]{3}( (REQ|EDGE)-[0-9]{3})*:`).
BDD scenarios:
```gherkin
@integration @AJ-S127 @REQ-031 @EDGE-015 @EDGE-016 @EDGE-017
Feature: Run the cloudgpu workload on the VM and copy the results back
  @positive
  Scenario: AJ-127 the dry-run passes on the VM
    Given the NIM container serves the model and the session environment is set
    When I run "./AgentJoules.Cli run --tier cloudgpu --dry-run" on the VM
    Then the exit code is 0
    And the output is recorded in docs/cloudgpu-run.md
  @positive
  Scenario: AJ-127 the full run completes with FP8 metadata
    Given the dry-run passed and the guard allows a run
    When I run "./AgentJoules.Cli run --tier cloudgpu --repeat 3" on the VM
    Then the run file has 36 steps, run.repeat 3 and run.status "completed"
    And environment.provider is "nebius-ai-cloud" and environment.precision is "FP8"
  @positive
  Scenario: AJ-127 the copied files validate on the laptop
    Given the run file was copied back with scp
    When I run "validate results/"
    Then the exit code is 0
    And the output contains "Validated" followed by the number of run files
  @positive
  Scenario: AJ-127 an SSH drop does not stop the run
    Given the run executes inside a tmux session on the VM
    When the SSH connection breaks and is re-established
    Then the run continues and finishes with status "completed"
  @negative
  Scenario: AJ-127 a lost VM leaves an excluded incomplete file
    Given the VM becomes unreachable during the run
    When the builder copies what exists and asks the user to check the console
    Then no retry is made past the box or the cap
    And the incident is recorded and the file is not used in any report
  @negative
  Scenario: AJ-127 the reported cost reaches 25 USD
    Given the user reports 25.00 USD observed on the billing page
    When the builder is about to start the next invocation
    Then no new invocation starts
    And the builder copies the existing results and tells the user to delete the VM
  @negative
  Scenario: AJ-127 the four-hour mark is reached
    Given 4 hours have passed since the handover
    When the builder is about to start the next invocation
    Then no new invocation starts and the stop reason "time-box" is recorded
  @negative
  Scenario: AJ-127 the judge key is missing on the VM
    Given the judge key is not set in the VM session
    When I run "./AgentJoules.Cli run --tier cloudgpu --dry-run" on the VM
    Then the exit code is 3
    And the output names "NVIDIA_API_KEY"
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-127-T1 | Write the failing feature and the gated cloudgpu run-file checks; create `IncompleteRunTests.cs` and the mid-stream drop mode of the fake handler here when AJ-123 was cut | tests/AgentJoules.Specs/Features/CloudGpu/CloudGpuRun.feature, tests/AgentJoules.IntegrationTests/CloudGpuRunTests.cs, tests/AgentJoules.UnitTests/IncompleteRunTests.cs, tests/AgentJoules.TestSupport/FakeHttpMessageHandler.cs | 0.75 | AJ-126-T9 |
| AJ-127-T2 | Start `tmux` on the VM and set the session environment | docs/cloudgpu-run.md | 0.25 | AJ-127-T1 |
| AJ-127-T3 | Run the VM dry-run and the calibration and record the output | docs/cloudgpu-run.md, results/calibration-<yyyyMMddTHHmmssZ>-cloudgpu.json | 0.75 | AJ-127-T2 |
| AJ-127-T4 | Run `run --tier cloudgpu --repeat 3` inside the box | results/<yyyyMMddTHHmmssZ>-cloudgpu.json | 2.0 | AJ-127-T3 |
| AJ-127-T5 | USER ACTION: read the billing page and report the observed cost before each invocation and at the end of the run | none (user cloud console) | 0.2 | AJ-127-T3 |
| AJ-127-T6 | Apply the stop procedure or the incident handling when a limit or a loss occurs (contingency) | docs/cloudgpu-run.md | 0.5 | AJ-127-T4, AJ-127-T5 |
| AJ-127-T7 | Copy the results back with scp and validate them on the laptop | results/ | 0.5 | AJ-127-T4 |
| AJ-127-T8 | Run the secret scan and commit the results with the `AJ-127 REQ-031:` prefix | results/ | 0.25 | AJ-127-T7 |
Test mapping: tests/AgentJoules.Specs/Features/CloudGpu/CloudGpuRun.feature; SchemaValidationTests and ReportRenderingTests (REQ-023 list) for the file and the row; CloudGpuTests and IncompleteRunTests (design 6.1); gated class CloudGpuRunTests (design 6.3).
Docs impact: docs/cloudgpu-run.md (`Commands`, `Runs`), results/ (cloudgpu run file, calibration file), docs/build-log.md.
Definition of done:
- [ ] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [ ] the elapsed time since the handover and the last observed cost are recorded when the story ends
Demo relevance: yes: the cloudgpu run file is the source of the cloudgpu row in the report page, the README and the posts.

### AJ-128: Document the cloudgpu run, the cost and the user's teardown
Covers: REQ-031, EDGE-016, EDGE-017
Note (2026-10-25, AJ-128): feature file tests/AgentJoules.Specs/Features/CloudGpu/AJ-128-run-document-contract.feature; the code part (T1 to T3) shipped before the VM handover; the VM-bound and docs tasks follow.
User story: As a reviewer and as the person paying, I want `docs/cloudgpu-run.md` to record the instance, commands, probe, runs, cost and the user's deletion of the VM, so that the cost cap, the incidents and the teardown are documented facts.
Acceptance criteria:
1. A contract test requires the headings `Instance`, `Commands`, `Probe`, `Runs`, `Cost` and `Teardown` in `docs/cloudgpu-run.md`.
2. `Instance` holds platform, preset, region, image, boot disk size, `preemptible: off (user-confirmed)`, the hourly price from the console, GPU name, UUID, driver and the delivery choice; `Commands` shows keys redacted and the SSH target only as `$CLOUDGPU_SSH_USER@$CLOUDGPU_SSH_HOST`; `Runs` holds start and end times, file names, exit codes and any incident (lost VM, broken SSH) with the statement that no retry was made past the box or the cap.
3. `Cost` contains `USD`, the observed cost with the billing-page timestamp, and the stop reason or `no stop triggered`; the test fails when the value exceeds 30.
4. `Teardown` records the user's deletion time and final cost and contains the word `deleted`; while the user has not confirmed, the document says `pending user confirmation`, the test fails and the Definition of Done box stays unchecked.
5. `grep -F "$NGC_API_KEY" docs/cloudgpu-run.md` prints nothing; the contract test rejects any IPv4 literal and any runtime-built fake key; `dotnet test --filter "Spec=REQ-031"` shows at least one passed and no failed test.
BDD scenarios:
```gherkin
@unit @AJ-S128 @REQ-031 @EDGE-016 @EDGE-017
Feature: Document the cloudgpu run, the cost and the user's teardown
  @positive
  Scenario: AJ-128 a complete document passes
    Given a run document with the six headings, a Cost section of "USD 6.12" and a Teardown section containing "deleted"
    When the contract test runs
    Then the result is pass
  @positive
  Scenario: AJ-128 a cost of exactly 30 USD passes
    Given the Cost section states "USD 30.00"
    When the contract test runs
    Then the result is pass
  @negative
  Scenario: AJ-128 a missing Teardown heading fails
    Given the document has no "Teardown" heading
    When the contract test runs
    Then the result is fail and the message names "Teardown"
  @negative
  Scenario: AJ-128 a cost above the cap fails
    Given the Cost section states "USD 31.20"
    When the contract test runs
    Then the result is fail and the message names the 30 USD cap
  @negative
  Scenario: AJ-128 a pending teardown fails
    Given the Teardown section says "pending user confirmation" and does not contain "deleted"
    When the contract test runs
    Then the result is fail and the message says the teardown is not confirmed
  @negative
  Scenario: AJ-128 an IP address literal fails
    Given the Commands section contains a dotted IPv4 address
    When the contract test runs
    Then the result is fail without echoing the address
  @negative
  Scenario: AJ-128 a runtime-built key fails
    Given the document contains a key built at runtime as "nvapi-" plus a random suffix
    When the contract test runs
    Then the result is fail and the message does not echo the suffix
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-128-T1 | Write the failing feature and the contract tests | tests/AgentJoules.Specs/Features/CloudGpu/RunDocumentContract.feature, tests/AgentJoules.UnitTests/CloudGpuRunDocumentTests.cs | 0.5 | AJ-127-T8 |
| AJ-128-T2 | Implement the run-document parser and the cost and secret rules | tests/AgentJoules.TestSupport/Docs/CloudGpuRunDocumentParser.cs | 0.75 | AJ-128-T1 |
| AJ-128-T3 | Bind the feature steps | tests/AgentJoules.Specs/Steps/CloudGpuSteps.cs | 0.25 | AJ-128-T2 |
| AJ-128-T4 | USER ACTION: delete the VM and its boot disk, read the final cost on the billing page, give the deletion time and cost to the builder (section 12 step 13) | none (user cloud console) | 0.3 | AJ-127-T8 |
| AJ-128-T5 | Write the Runs, Cost and Teardown sections | docs/cloudgpu-run.md | 0.75 | AJ-128-T4 |
| AJ-128-T6 | Run the contract test, the key grep and the host grep | docs/build-log.md | 0.25 | AJ-128-T3, AJ-128-T5 |
Test mapping: tests/AgentJoules.Specs/Features/CloudGpu/RunDocumentContract.feature; unit class CloudGpuRunDocumentTests with `[Trait("Spec","REQ-031")]`, `[Trait("Spec","EDGE-016")]` and `[Trait("Spec","EDGE-017")]`.
Docs impact: docs/cloudgpu-run.md (final), docs/build-log.md.
Definition of done:
- [ ] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [ ] the user's deletion confirmation time and the final billing figure are in the `Teardown` and `Cost` sections
Demo relevance: yes: the README names the VM preset, region, GPU-hours and observed cost taken from this document.

### AJ-129: Apply the partial-tier consequences to the report, README and Devpost texts
Covers: REQ-031, EDGE-017
Note (2026-10-25, AJ-129): feature file tests/AgentJoules.Specs/Features/CloudGpu/AJ-129-partial-tier-consequences.feature; the code part (T1, T2 and T5) shipped before the VM handover; the VM-bound and docs tasks follow.
User story: As the author, I want the report, the README row and the Devpost pitch and story to follow the real state of the cloudgpu tier, so that no text claims a measured cloud GPU run that did not complete.
Acceptance criteria:
1. The tier state is derived from committed files only: `complete` (a selectable file with status `completed`), `partial` (a selectable file with status `partial`) or `absent` (no `completed` or `partial` file for the tier; `incomplete`, `running` and `failed` files count as none).
2. Report and README block fixtures: a `partial` file renders a `cloudgpu` row containing `partial`; an `absent` tier renders `cloudgpu | not run`; the cloudgpu row is separate from every laptop row and no sentence compares laptop and cloud joules; when model IDs or precision differ across tiers the header states `Models differ across tiers: <tier>=<id> (<precision>), ...`.
3. Tagline rule from the Devpost checklist requirements: `docs/devpost-pitch.md` keeps candidate A as the tagline when the tier state is `complete` or `partial`, and switches to candidate A-local only when the state is `absent`; when the committed cloudgpu file names a GPU other than the L40S, the GPU name in candidate A is replaced by the file's `gpuName` and the change is logged in `docs/build-log.md`.
4. `docs/devpost-story.md` sentences that a measured cloudgpu fact contradicts (for example a GPU class, a stopped tier, a sampling method) are changed in place; each change is recorded in `docs/build-log.md` with the old sentence, the new sentence and the file that proves it; unaffected sentences stay unchanged.
5. The facts the README section "Other Nebius tools or services used" must name (preset, region, GPU-hours, observed cost) are listed in `docs/build-log.md` from `docs/cloudgpu-run.md` for the M6 README story; `dotnet test --filter "Spec=EDGE-017"` and `--filter "Spec=REQ-031"` show at least one passed and no failed test.
BDD scenarios:
```gherkin
@unit @AJ-S129 @REQ-031 @EDGE-017
Feature: Apply the partial-tier consequences to the report, README and Devpost texts
  @positive
  Scenario: AJ-129 a completed cloudgpu file keeps candidate A
    Given a committed cloudgpu run file with status "completed" and gpuName containing "L40S"
    When the tagline rule is evaluated
    Then the tagline is candidate A and no change is required
  @positive
  Scenario: AJ-129 a partial file keeps candidate A and the row says partial
    Given a committed cloudgpu run file with status "partial"
    When the README block is rendered
    Then the cloudgpu row contains "partial"
    And the tagline rule keeps candidate A
  @positive
  Scenario: AJ-129 a different GPU name replaces the name in candidate A
    Given the committed cloudgpu file names a GPU other than the L40S
    When the tagline rule is evaluated
    Then candidate A uses the file's gpuName
    And docs/build-log.md contains an entry for the change
  @negative
  Scenario: AJ-129 no usable cloudgpu file selects candidate A-local
    Given no cloudgpu file with status "completed" or "partial" is committed
    When the tagline rule is evaluated
    Then the tagline is candidate A-local
    And the README block renders "cloudgpu | not run"
  @negative
  Scenario: AJ-129 an incomplete file counts as absent
    Given the only cloudgpu file has status "incomplete"
    When the tagline rule is evaluated
    Then the tier state is absent and candidate A-local is selected
  @negative
  Scenario: AJ-129 a story sentence changed without a log entry fails
    Given docs/devpost-story.md differs from its pre-drafted sentence and docs/build-log.md has no matching entry
    When the consequence check runs
    Then the check fails and names the sentence
  @negative
  Scenario: AJ-129 laptop and cloud rows must stay separate
    Given a report fixture with a laptop local file and a cloudgpu file
    When the report is rendered
    Then the summary has separate rows and contains no merged or averaged figure
  @positive
  Scenario: AJ-129 a sampling-method cloudgpu row names the method
    Given a completed cloudgpu file with method "sampling"
    When the README block is rendered
    Then the cloudgpu row contains "sampling"
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-129-T1 | Write the failing feature and unit tests | tests/AgentJoules.Specs/Features/CloudGpu/PartialTierConsequences.feature, tests/AgentJoules.UnitTests/ReadmeBlockTests.cs | 0.75 | AJ-128-T6 |
| AJ-129-T2 | Implement the tier-state and tagline rule helper | tests/AgentJoules.TestSupport/Docs/CloudGpuTaglineRule.cs | 0.5 | AJ-129-T1 |
| AJ-129-T3 | Add or confirm the partial, sampling and not-run words in the README block rows | src/AgentJoules.Reporting/Readme/ReadmeResultsBlock.cs | 0.5 | AJ-129-T1 |
| AJ-129-T4 | Add or confirm the partial marker and the separate-row rule in the summary | src/AgentJoules.Reporting/Render/SummaryMarkdownRenderer.cs | 0.5 | AJ-129-T3 |
| AJ-129-T5 | Bind the feature steps | tests/AgentJoules.Specs/Steps/CloudGpuSteps.cs | 0.5 | AJ-129-T2, AJ-129-T4 |
| AJ-129-T6 | Apply the tagline rule and log any change | docs/devpost-pitch.md, docs/build-log.md | 0.5 | AJ-129-T5 |
| AJ-129-T7 | Change contradicted story sentences and log each change | docs/devpost-story.md, docs/build-log.md | 0.5 | AJ-129-T6 |
| AJ-129-T8 | List the cloudgpu facts for the README section "Other Nebius tools or services used" | docs/build-log.md | 0.25 | AJ-129-T7 |
Test mapping: tests/AgentJoules.Specs/Features/CloudGpu/PartialTierConsequences.feature; unit classes ReadmeBlockTests and ReportRenderingTests (design 6.1, REQ-023 list) with `[Trait("Spec","EDGE-017")]` and `[Trait("Spec","REQ-031")]`.
Docs impact: docs/devpost-pitch.md, docs/devpost-story.md, docs/build-log.md; the README sections themselves are written in M6 from the facts listed here.
Definition of done:
- [ ] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [ ] every change to a pre-drafted Devpost sentence has a matching entry in docs/build-log.md
Demo relevance: yes: the pitch and the report row must match the real state of the cloud tier.
Conflict: EDGE-017 acceptance requires the README row for `cloudgpu` to contain the word `partial`, but the README results block is generated by the REQ-025 story in M6. Proposed resolution: M5 proves the rule with the README block fixtures above; the live README check runs when the M6 story generates the block, and M7 re-runs it for the Definition of Done box "README results block contains `cloudgpu`".
Conflict: REQ-028 switches the tagline to A-local when the cloudgpu tier "produced no committed run file", yet an `incomplete` or `failed` file may exist on disk while being excluded from every report (EDGE-016). Proposed resolution: a tier with no selectable file (status `completed` or `partial`) counts as having produced no committed run file; an excluded file is documented in `docs/cloudgpu-run.md` but does not keep candidate A.

## Risks

| Risk | Trigger | Mitigation | Owner |
|---|---|---|---|
| Preferred preset unavailable or materially more expensive | the user's console shows no `1gpu-16vcpu-64gb` or a clearly higher hourly price | fallbacks in order: `1gpu-8vcpu-32gb`, then `gpu-l40s-d` with `1gpu-16vcpu-96gb`; no other GPU class without a new decision (AJ-125) | user, builder |
| Inferred NIM pull path is wrong | `docker pull` returns not found | fallback image `nvcr.io/nim/nvidia/model-free-nim`, substitution recorded (AJ-126) | builder |
| No FP8 profile is listed for the L40S | profile listing shows BF16 only | stop and report; BF16 needs more than 48 GB (AJ-126) | builder |
| KV cache does not fit next to the 30.46 GB FP8 weights | container exits with a memory error | lower `NIM_MAX_MODEL_LEN`, record the value (AJ-126) | builder |
| Energy counter unsupported on the VM GPU | probe shows `energyCounterSupported` false | sampling with `sampling-fallback`, disclosure everywhere, no estimate (AJ-124) | builder |
| VM lost or SSH broken | connection drops mid-run | `tmux` keeps the run alive; hard loss gives an excluded `incomplete` file, no retry past the box (AJ-123, AJ-127) | builder, user |
| Spend passes the cap | billing page shows USD 25 or four hours pass | the guard stops new work, the user deletes the VM at once (AJ-122, AJ-128) | builder, user |
| VM left running after the runs | the user does not delete it promptly | the builder asks for deletion in the same message that reports the results copied; `Teardown` stays unconfirmed and the Definition of Done box stays open (AJ-128) | user |
| Key left on the VM | `docker login` writes its credential file | memory-backed `DOCKER_CONFIG`, logout, VM deletion (AJ-126 criterion 1) | builder |
| Judge rate-limit from the VM | 429 on the judge tier | judge failures never change the exit code; steps keep `quality` N/A with `not-scored` | builder |
| Estimate exceeds two working days | task sum of 36.95 hours against about 12 hours of capacity | unit stories AJ-121 to AJ-124 and AJ-129 are independent of the VM and can be done in parallel; the VM box is wall-clock; the cloudgpu tier may end as `partial` by design | builder |

Note (2026-10-20): Carry from the M1 exit (D-16): trim the sample buffer or cap the effective rate near 100 Hz before the VM runs.

## Estimate

Note (2026-10-18): any deferral or descope candidates named in this section are informational; the drop order in docs/plan/plan.md section 3.4 is the single source and classifies stories as MUST, SHOULD or COULD.

Sum of task estimates, computed from the task tables: 36.95 h including the five USER ACTION tasks (35.35 h builder-only; user actions total 1.60 h).

| Story | Tasks | Hours |
|---|---|---|
| AJ-120 | 5 | 1.60 |
| AJ-121 | 6 | 3.50 |
| AJ-122 | 7 | 4.00 |
| AJ-123 | 7 | 3.75 |
| AJ-124 | 7 | 3.50 |
| AJ-125 | 8 | 3.85 |
| AJ-126 | 9 | 4.75 |
| AJ-127 | 8 | 5.20 |
| AJ-128 | 6 | 2.80 |
| AJ-129 | 8 | 4.00 |
| Total | 71 | 36.95 |

Capacity check: two days at about six hours is 12 hours, so the plan exceeds the window by roughly 25 hours of task effort. The VM-bound work (AJ-125 to AJ-127, 13.8 hours of tasks) is bounded by the 4-hour wall-clock box and the USD 30 cap, not by this sum: tasks that do not fit the box are not started and the tier ends as `partial` by design (EDGE-017). The unit-test stories (AJ-121 to AJ-124 and AJ-129, 18.75 hours) are independent of the VM and are the work to compress or parallelise.
