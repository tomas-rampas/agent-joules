# M4: Local runtime and measurement

M4 decides the local runtime on evidence (a 90-minute NIM attempt, Ollama as the fallback), records and verifies every endpoint and model ID, makes the runtime, offload and warm-up visible in every local run file, calibrates the energy counter against power sampling, writes the methodology document, and runs the laptop measurement campaign that produces the committed results files for the `local`, `nvidia` and `nebius` tiers. Everything the README and the posts later quote about the laptop comes from the files this milestone commits.

## Dates and exit criteria

Dates: 2026-10-24 to 2026-10-26. The window overlaps the last day of M3; capacity is about six working hours per day. Planned split: 2026-10-24 AJ-100 to AJ-103 (the NIM attempt must finish first because its decision selects the local runtime); 2026-10-25 AJ-104 to AJ-109; 2026-10-26 AJ-110 to AJ-115 (M5 starts on the same day, once the local Definition of Done items are committed).

Exit criteria:
- [ ] `AJ validate --config` exits 0, and `dotnet test --filter "Spec=REQ-018" --logger "console;verbosity=normal"` shows at least one passed and no failed test.
- [ ] `grep -cE '^## (local|cloudgpu|nvidia|nebius)$' docs/endpoints.md` prints 4; the `## local`, `## nvidia` and `## nebius` sections each hold a line `Verified: <date>` with a date of 2026-10-17 or later (the `## cloudgpu` section carries `Status: pending` until AJ-126).
- [ ] `docs/nim-local-attempt.md` has the headings `Commands`, `Image`, `Outcome` and `Decision`; `grep -F "$NGC_API_KEY" docs/nim-local-attempt.md` prints nothing (key set in the shell).
- [ ] `jq '.environment | [.localRuntime, .gpuOffloadPercent, .modelLoadSeconds, .powerSource]'` on the latest local run file prints a runtime in {nim, ollama}, a number, a number and a value in {ac, battery, unknown}.
- [ ] `jq '[.run.repeat, .baseline.seconds]'` on the latest local run file prints values of at least 3 and at least 30.
- [ ] A committed `results/calibration-<yyyyMMddTHHmmssZ>.json` holds `counterJoules`, `samplingJoules` and `relativeDifference`, each a label object.
- [ ] `grep -cE '^#+ +(Measurement|Attribution|Baseline|Labels|Limitations)' docs/methodology.md` prints 5, and its Limitations section contains the seven required strings.
- [ ] One run file each for `local`, `nvidia` and `nebius` is committed under `results/`, plus the `results/probe-<yyyyMMddTHHmmssZ>.json` taken before the runs.
- [ ] `AJ validate results/` exits 0 and prints `Validated <n> files` with n equal to the number of committed run files.
- [ ] `git log --oneline` shows commits starting with `REQ-018`, `REQ-019`, `REQ-020`, `REQ-021`, `REQ-022`, `EDGE-009` and `EDGE-010`; `git grep -nE "nvapi-[A-Za-z0-9_-]{8,}"` prints nothing.

## Coverage

REQ-018, REQ-019, REQ-020, REQ-021, REQ-022; EDGE-009, EDGE-010.

## Dependencies and user actions

Needs: M0 (repository, secrets hygiene, `.gitignore`), M1 (NVML binding, `SampledEnergyMeter`, probe), M2 (`MeteredChatClient`, workflow, `PreflightService`, `run --dry-run`), M3 (results writer, schema, `validate`, report, cost, judge). M5 starts only after AJ-115 is green and committed.

User actions from prompt section 12 that this milestone consumes:

| Step | Action | Needed by |
|---|---|---|
| 4 | Credits requested on the Devpost Resources tab (Token Factory credits) | before AJ-101 (45-minute Nebius onboarding box) |
| 5 | `NEBIUS_API_KEY` created and stored | AJ-101, AJ-108, AJ-114 |
| 6 | `NVIDIA_API_KEY` created and stored | AJ-101, AJ-108, AJ-114 (also the judge key) |
| 7 | `NGC_API_KEY` created and stored | AJ-103 only |

Not a section 12 step but a user action: the laptop is on mains power with other GPU applications closed during AJ-110, AJ-111 and AJ-114.

## Stories

### AJ-100: Validate configured model IDs against docs/endpoints.md
Covers: REQ-018
User story: As a reviewer of the repository, I want `AJ validate --config` to compare every configured model ID with the recorded endpoint listing, so that no model ID enters a run without a verification record.
Acceptance criteria:
1. `AJ validate --config` exits 0 and prints `Validated <n> model IDs` when every `Tiers:<t>:ModelId`, every `Tiers:<t>:AgentModelIds` value and `Judge:ModelId` appears as a line `- model: <id>` in the matching section of `docs/endpoints.md`; the judge ID is looked up in the section of `Judge:Tier`.
2. `AJ validate --config` exits 4 when any ID is absent, prints each missing ID with its section name, and a fake HTTP handler records zero requests (the check is file-only).
3. IDs are compared exactly: `nemotron-3-nano:30b` does not match `nemotron-3-nano:30B` (exit 4).
4. An enabled tier whose section has no line `Verified: YYYY-MM-DD` makes the command exit 4 with the line `Verified date missing in section ## <tier>`; a tier with `Enabled=false` is skipped, but its heading must still exist.
5. `AJ validate --config <path>` validates the named configuration file instead of `config/appsettings.json`; `dotnet test --filter "Spec=REQ-018" --logger "console;verbosity=normal"` shows at least one passed and no failed test.
BDD scenarios:
```gherkin
@unit @AJ-S100 @REQ-018
Feature: Validate configured model IDs against docs/endpoints.md
  @positive
  Scenario: AJ-100 every configured ID is recorded
    Given a docs/endpoints.md with sections local, nvidia and nebius, each with a Verified date and its model lines
    And a configuration whose tier and agent model IDs all appear in those sections
    When I run "validate --config"
    Then the exit code is 0
    And the output contains "Validated" and "model IDs"
  @positive
  Scenario: AJ-100 the judge ID is looked up in the judge tier section
    Given Judge:Tier is "nvidia" and Judge:ModelId is listed only under "## nvidia"
    When I run "validate --config"
    Then the exit code is 0
  @positive
  Scenario: AJ-100 a disabled tier is skipped
    Given Tiers:cloudgpu:Enabled is false and "## cloudgpu" has no Verified line
    When I run "validate --config"
    Then the exit code is 0
  @negative
  Scenario: AJ-100 an absent ID exits 4 without network access
    Given the configuration names a model ID that is not listed in its section
    When I run "validate --config"
    Then the exit code is 4
    And the output names the missing ID and its section
    And the fake HTTP handler recorded 0 requests
  @negative
  Scenario: AJ-100 a tag differing only by case exits 4
    Given the section lists "nemotron-3-nano:30b" and the configuration names "nemotron-3-nano:30B"
    When I run "validate --config"
    Then the exit code is 4
  @negative
  Scenario: AJ-100 an enabled tier without a Verified date exits 4
    Given the "## nebius" section has model lines but no Verified line
    When I run "validate --config"
    Then the exit code is 4
    And the output contains "Verified date missing in section ## nebius"
  @negative
  Scenario: AJ-100 a judge ID listed only under another tier exits 4
    Given Judge:Tier is "nvidia" and the judge ID is listed only under "## nebius"
    When I run "validate --config"
    Then the exit code is 4
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-100-T1 | Write the failing feature and unit tests for endpoint validation | tests/AgentJoules.Specs/Features/Configuration/EndpointsValidation.feature, tests/AgentJoules.UnitTests/ConfigValidationTests.cs | 0.75 | - |
| AJ-100-T2 | Implement the endpoints document parser (sections, Verified line, model lines) | src/AgentJoules.Reporting/Endpoints/EndpointsDocParser.cs | 0.75 | AJ-100-T1 |
| AJ-100-T3 | Implement the configuration model-ID validator | src/AgentJoules.Reporting/ConfigModelValidator.cs | 0.75 | AJ-100-T2 |
| AJ-100-T4 | Wire the zero-or-one arity `--config` option into the validate command | src/AgentJoules.Cli/Commands/ValidateCommand.cs | 0.75 | AJ-100-T3 |
| AJ-100-T5 | Bind the feature steps to the CLI harness | tests/AgentJoules.Specs/Steps/ConfigurationSteps.cs | 0.5 | AJ-100-T4 |
| AJ-100-T6 | Create the endpoints document skeleton with the four headings and the line formats | docs/endpoints.md | 0.25 | AJ-100-T5 |
Test mapping: tests/AgentJoules.Specs/Features/Configuration/EndpointsValidation.feature; unit class ConfigValidationTests (technical-design 6.1), every test with `[Trait("Spec","REQ-018")]`.
Docs impact: docs/endpoints.md (skeleton with `## local`, `## cloudgpu`, `## nvidia`, `## nebius`, the `Verified: YYYY-MM-DD` line and the `- model: <id>` line format); README CLI reference row for `validate --config` (written in M6).
Definition of done:
- [ ] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [ ] `dotnet test --filter "Spec=REQ-018"` shows at least one passed and no failed test
Demo relevance: no (guard rail; the video does not show it).
Conflict: REQ-018 acceptance requires `Verified: 2026-10-17` or later in the `## cloudgpu` section, but the cloudgpu endpoint exists only on the VM in M5. Proposed resolution: M4 commits the `## cloudgpu` heading with the line `Status: pending (verified in M5, AJ-126)` and the validator requires `Verified:` only for enabled tiers (criterion 4); AJ-126 adds the `Verified:` line and the model line, which closes the REQ-018 acceptance for that section.

### AJ-101: Record the nvidia and nebius endpoint verification in docs/endpoints.md
Covers: REQ-018
User story: As a reader of the results, I want the hosted endpoints and model IDs verified by a live call and recorded with date and command, so that every model ID in the configuration is a verified fact and not a remembered one.
Acceptance criteria:
1. The `## nvidia` section of `docs/endpoints.md` holds the base URL from `Tiers:nvidia:BaseUrl`, `Verified: <UTC date>` (2026-10-17 or later), the exact command used with the key shown as `Bearer ****`, every Nemotron and Qwen ID returned by `GET {BaseUrl}/models` as `- model: <id>` lines, a line `Total models returned: <n>` stating the truncation of other entries, and `Chosen:` lines for the Planner, Researcher, Writer and Reviewer agents and for the judge.
2. The `## nebius` section holds the same fields; the chosen nebius model is the same model as on nvidia when `GET {BaseUrl}/models` lists it, otherwise the smallest listed Nemotron 3 model, and its ID contains `nemotron` (case-insensitive). When no Nemotron model is listed the section says `BLOCKED` with the missing item and the command output reports the build as blocked on this item; the tier is never silently skipped (prompt section 10).
3. The Nebius onboarding and verification box is 45 minutes; the section records the start and end time, and on expiry records what was verified and what failed; `grep -E "^Onboarding (started|ended):" docs/endpoints.md` prints both times for the nebius section, and the difference is at most 45 minutes or a `Box exceeded:` line is present.
4. `config/appsettings.json` names only IDs copied from these lists; `AJ validate --config` exits 0; `AJ run --tier nvidia --dry-run` and `AJ run --tier nebius --dry-run` each exit 0 (keys set), with the Nebius base URL confirmed by the call and noted in `docs/build-log.md`.
5. No key appears anywhere: `git grep -nF "$NVIDIA_API_KEY"` and `git grep -nF "$NEBIUS_API_KEY"` print nothing, and `git grep -nE "nvapi-[A-Za-z0-9_-]{8,}"` prints nothing. When the judge model ID equals an agent model ID the section says `Judge: self-judged` (REQ-010 disclosure).
BDD scenarios:
```gherkin
@integration @AJ-S101 @REQ-018
Feature: Record the nvidia and nebius endpoint verification
  @positive
  Scenario: AJ-101 the nvidia listing is recorded with date and redacted command
    Given NVIDIA_API_KEY is set in the environment
    When the builder calls GET {BaseUrl}/models for the nvidia tier and writes the "## nvidia" section
    Then the section has "Verified:" with a UTC date of 2026-10-17 or later
    And the section lists every returned Nemotron and Qwen model as "- model: <id>"
    And the recorded command shows "Bearer ****" and no key value
  @positive
  Scenario: AJ-101 the nebius tier uses the same model when it is listed
    Given NEBIUS_API_KEY is set and the nebius listing contains the model chosen for nvidia
    When the builder writes the "## nebius" section
    Then the chosen nebius model equals the chosen nvidia model
    And "AJ validate --config" exits 0
  @positive
  Scenario: AJ-101 the smallest listed Nemotron 3 model is chosen when the nvidia model is absent
    Given the nebius listing lacks the nvidia model but lists two other Nemotron 3 models
    When the builder chooses the nebius model
    Then the smaller of the two is chosen and the reason is recorded in the section
  @negative
  Scenario: AJ-101 no Nemotron model on nebius is reported as blocked
    Given the nebius listing contains no model whose ID contains "nemotron"
    When the builder writes the "## nebius" section
    Then the section says "BLOCKED" and names the missing item
    And the final report lists the nebius tier as blocked and not skipped
  @negative
  Scenario: AJ-101 a missing key stops the check before any call
    Given NEBIUS_API_KEY is not set
    When I run "run --tier nebius --dry-run"
    Then the exit code is 3
    And the output names "NEBIUS_API_KEY" and the user-secrets alternative
  @negative
  Scenario: AJ-101 the 45-minute box expires
    Given the nebius onboarding started more than 45 minutes ago without a listed model
    When the builder closes the box
    Then the section records what was verified and what failed
    And the build is reported blocked on the nebius item
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-101-T1 | Write the failing feature and the gated listing test | tests/AgentJoules.Specs/Features/Configuration/HostedEndpointRecord.feature, tests/AgentJoules.IntegrationTests/HostedEndpointListingTests.cs | 0.5 | AJ-100-T6 |
| AJ-101-T2 | USER ACTION: store NVIDIA_API_KEY and NEBIUS_API_KEY and confirm the credits request (section 12 steps 4 to 6) | none (user environment) | 0.3 | - |
| AJ-101-T3 | Query the nvidia model list and write the nvidia section | docs/endpoints.md | 0.75 | AJ-101-T1, AJ-101-T2 |
| AJ-101-T4 | Query the nebius model list and write the nebius section (45-minute box) | docs/endpoints.md | 0.75 | AJ-101-T2 |
| AJ-101-T5 | Copy the verified IDs into the tier and judge configuration | config/appsettings.json | 0.5 | AJ-101-T3, AJ-101-T4 |
| AJ-101-T6 | Run `validate --config` and both dry-runs and record the exit codes | docs/build-log.md | 0.5 | AJ-101-T5 |
Test mapping: tests/AgentJoules.Specs/Features/Configuration/HostedEndpointRecord.feature; AJ-100 `ConfigValidationTests` for the document format; gated test class HostedEndpointListingTests (design 6.3).
Docs impact: docs/endpoints.md (`## nvidia`, `## nebius`), docs/build-log.md (Nebius base-URL confirmation, box times), config/appsettings.json.
Definition of done:
- [ ] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [ ] both sections carry `Verified: <date>` and the `AJ validate --config` exit code 0 is recorded in docs/build-log.md
Demo relevance: no (the README quotes the model IDs recorded here).

### AJ-102: Enforce the NIM attempt document contract and the 90-minute box
Covers: REQ-019, EDGE-010
User story: As a reviewer, I want a test that rejects `docs/nim-local-attempt.md` when a heading, a time or a secret rule is violated, so that the time-boxed attempt is documented honestly whichever way it ends.
Acceptance criteria:
1. A parser test reads the document and requires the headings `Commands`, `Image`, `Outcome` and `Decision`, a line `Started: <ISO 8601 UTC>`, a line `Ended: <ISO 8601 UTC>` and, in `Decision`, a line `Local runtime: nim` or `Local runtime: ollama`.
2. The test rejects a document whose Ended minus Started exceeds 90 minutes unless it contains a line starting `Box exceeded:` with a reason.
3. The test rejects any `docker login` line that lacks `--password-stdin` or contains `--password` or ` -p `, and any text matching `nvapi-[A-Za-z0-9_-]{8,}` or a runtime-built fake NGC key.
4. `dotnet test --filter "Spec=REQ-019" --logger "console;verbosity=normal"` shows at least one passed and no failed test; a failure message names the broken rule and the heading or line.
BDD scenarios:
```gherkin
@unit @AJ-S102 @REQ-019 @EDGE-010
Feature: Enforce the NIM attempt document contract
  @positive
  Scenario: AJ-102 a complete document passes
    Given a NIM attempt document with the four headings, Started and Ended 62 minutes apart and "Local runtime: ollama"
    When the contract test runs
    Then the result is pass
  @positive
  Scenario: AJ-102 exactly 90 minutes passes
    Given Started and Ended are exactly 90 minutes apart
    When the contract test runs
    Then the result is pass
  @positive
  Scenario: AJ-102 an exceeded box with a stated reason passes
    Given Started and Ended are 104 minutes apart and a line "Box exceeded: image pull stalled"
    When the contract test runs
    Then the result is pass
  @negative
  Scenario: AJ-102 a missing heading fails
    Given the document has no "Outcome" heading
    When the contract test runs
    Then the result is fail and the message names "Outcome"
  @negative
  Scenario: AJ-102 91 minutes without a statement fails
    Given Started and Ended are 91 minutes apart and no "Box exceeded:" line
    When the contract test runs
    Then the result is fail and the message names the 90-minute box
  @negative
  Scenario: AJ-102 a docker login with a password argument fails
    Given the Commands section contains a docker login line with "--password" followed by a value
    When the contract test runs
    Then the result is fail and the message says the login must use --password-stdin
  @negative
  Scenario: AJ-102 a runtime-built key in the text fails
    Given the document contains a key built at runtime as "nvapi-" plus a random suffix
    When the contract test runs
    Then the result is fail and the message does not echo the suffix
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-102-T1 | Write the failing feature and parser unit tests | tests/AgentJoules.Specs/Features/Configuration/NimAttemptContract.feature, tests/AgentJoules.UnitTests/NimAttemptDocumentTests.cs | 0.5 | - |
| AJ-102-T2 | Implement the attempt-document parser (headings, times, decision line) | tests/AgentJoules.TestSupport/Docs/NimAttemptDocumentParser.cs | 0.75 | AJ-102-T1 |
| AJ-102-T3 | Implement the time-box and secret rules | tests/AgentJoules.TestSupport/Docs/NimAttemptRules.cs | 0.5 | AJ-102-T2 |
| AJ-102-T4 | Bind the feature steps | tests/AgentJoules.Specs/Steps/ConfigurationSteps.cs | 0.25 | AJ-102-T3 |
| AJ-102-T5 | Document the contract and the line formats | docs/build-log.md | 0.25 | AJ-102-T4 |
Test mapping: tests/AgentJoules.Specs/Features/Configuration/NimAttemptContract.feature; unit class NimAttemptDocumentTests with `[Trait("Spec","REQ-019")]` and `[Trait("Spec","EDGE-010")]`.
Docs impact: docs/build-log.md (contract: headings, `Started:`, `Ended:`, `Box exceeded:`, `Local runtime:` lines).
Definition of done:
- [ ] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [ ] the contract test is skipped with a clear message while `docs/nim-local-attempt.md` does not exist, and runs against the committed file after AJ-103
Demo relevance: no.

### AJ-103: Run the NIM-local attempt within 90 minutes and document the outcome
Covers: REQ-019, EDGE-010
User story: As the author who promises an honest README, I want a hard 90-minute attempt to serve a Nemotron model from a NIM container on the laptop, so that the local runtime is chosen on evidence and the README reports the attempt whichever way it ends.
Acceptance criteria:
1. The attempt starts at the first NGC catalog search and ends at the first successful `GET /v1/models` response on the container or at the 90-minute stop, whichever comes first; the document records `Started:` and `Ended:` in UTC.
2. The `Image` section names the catalog entry found (a 4B-class Nemotron NIM, otherwise the smallest Nemotron NIM whose BF16 weights plus KV cache fit in about 15 GB of the 16 GB GPU), the image path, the tag and the digest, or states that none was found; it records whether a 4B-class Nemotron NIM exists (unverified in the prompt).
3. The `Commands` section lists the exact commands with keys redacted; the login is `docker login nvcr.io --username '$oauthtoken' --password-stdin` fed from `NGC_API_KEY`; `DOCKER_CONFIG` points to a memory-backed or temporary directory that is deleted right after the pull, `docker logout nvcr.io` follows, and the container receives the key by variable name only; the document shows these steps with the key redacted; `grep -c "docker logout" docs/nim-local-attempt.md` prints 1 or more and `grep -F "$NGC_API_KEY" docs/nim-local-attempt.md` prints nothing.
4. The `Outcome` section holds the first successful model-list response (model ID) or the verbatim errors, plus `nvidia-smi --query-gpu=memory.used,memory.total --format=csv` readings written as `<measured>` values copied from the run.
5. At the stop (success or expiry) the container is stopped and removed when it is not the chosen runtime: `docker ps -a --filter name=<container-name> --format "{{.Names}}"` prints nothing after a failed or expired attempt.
6. The `Decision` section ends with `Local runtime: nim` or `Local runtime: ollama`; `Local:Runtime` in `config/appsettings.json` equals it; on a fallback, `README.md` links `docs/nim-local-attempt.md` and says that NIM-local was attempted and why it was not used (EDGE-010); the AJ-102 contract test passes on the committed file.
BDD scenarios:
```gherkin
@integration @AJ-S103 @REQ-019 @EDGE-010
Feature: Run the NIM-local attempt within 90 minutes
  @positive
  Scenario: AJ-103 the attempt succeeds and nim becomes the local runtime
    Given Docker Desktop with GPU support is running and NGC_API_KEY is set
    When the builder pulls and starts a Nemotron NIM and GET /v1/models returns a model ID within 90 minutes
    Then the Outcome section records the model ID and the memory readings
    And the Decision section says "Local runtime: nim"
  @positive
  Scenario: AJ-103 the attempt fails and the local tier falls back to Ollama with a README link
    Given the NIM container exits during start-up with an error
    When the builder records the decision
    Then the Decision section says "Local runtime: ollama"
    And README.md links "docs/nim-local-attempt.md" and states why NIM-local was not used
  @negative
  Scenario: AJ-103 the box expires at 90 minutes
    Given 90 minutes have passed since the first catalog search without a successful models call
    When the builder applies the hard stop
    Then the container is stopped and removed
    And the document records the status, the errors and the switch to Ollama
  @negative
  Scenario: AJ-103 the NGC login is rejected
    Given the NGC login to nvcr.io returns an authorization error
    When the builder writes the Outcome section
    Then the error text is recorded without any key value
    And the grep of the document for NGC_API_KEY prints nothing
  @negative
  Scenario: AJ-103 the container runs out of GPU memory
    Given the container starts and exits with an out-of-memory error on the 16 GB GPU
    When the builder writes the Outcome section
    Then the memory readings and the error are recorded
    And the Decision section says "Local runtime: ollama"
  @negative
  Scenario: AJ-103 no supported image exists for this GPU
    Given the catalog search finds no Nemotron NIM that supports compute capability 8.6
    When the builder writes the Image section
    Then the section states that none was found and the Decision falls back to Ollama
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-103-T1 | Write the failing feature and the gated NIM endpoint test; create `NimAttemptDocumentTests.cs` and the parser of AJ-102 here when AJ-102 was cut | tests/AgentJoules.Specs/Features/Configuration/NimLocalAttempt.feature, tests/AgentJoules.IntegrationTests/NimLocalEndpointTests.cs, tests/AgentJoules.UnitTests/NimAttemptDocumentTests.cs | 0.5 | AJ-102-T5 |
| AJ-103-T2 | USER ACTION: store NGC_API_KEY and confirm Docker Desktop runs with GPU support in WSL2 (section 12 step 7) | none (user environment) | 0.25 | - |
| AJ-103-T3 | Run the 90-minute attempt (catalog search, login, pull, run, models call) | docs/nim-local-attempt.md | 1.5 | AJ-103-T1, AJ-103-T2 |
| AJ-103-T4 | Stop and remove the container if not chosen and write the four sections | docs/nim-local-attempt.md | 0.5 | AJ-103-T3 |
| AJ-103-T5 | Set the local runtime in the configuration | config/appsettings.json | 0.25 | AJ-103-T4 |
| AJ-103-T6 | Add the NIM-local outcome and link to the README | README.md | 0.5 | AJ-103-T4 |
| AJ-103-T7 | Run the contract test and the secret grep on the committed document | docs/build-log.md | 0.25 | AJ-103-T6 |
Test mapping: tests/AgentJoules.Specs/Features/Configuration/NimLocalAttempt.feature; AJ-102 NimAttemptDocumentTests on the committed file; gated class NimLocalEndpointTests (design 6.3).
Docs impact: docs/nim-local-attempt.md (new), README.md (section "Local runtime: NIM attempt" with the link; the results block stays generated), docs/build-log.md.
Definition of done:
- [ ] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [ ] `docs/nim-local-attempt.md` committed with the four headings; Ended minus Started at most 90 minutes or `Box exceeded:` present
Demo relevance: yes: the video and README state which local runtime produced the numbers and link the attempt document.

### AJ-104: Report Ollama failures with the exact fix command
Covers: EDGE-009
User story: As a developer running the local tier, I want a clear message when Ollama is not running or the model is not pulled, so that I can fix the problem with one command.
Acceptance criteria:
1. With the Ollama endpoint refusing connections, `AJ run --tier local --dry-run` exits 5 and the output contains the configured endpoint and the text `ollama serve`.
2. With the model list lacking the configured tag, the command exits 4 and the output contains the exact command `ollama pull nemotron-3-nano:30b` when that tag is configured (the tag in the message is the configured tag, not a constant).
3. In both cases the fake HTTP handler records no request to `/chat/completions`, and no key value is printed.
4. `dotnet test --filter "Spec=EDGE-009" --logger "console;verbosity=normal"` shows at least one passed and no failed test.
BDD scenarios:
```gherkin
@unit @AJ-S104 @EDGE-009
Feature: Report Ollama failures with the exact fix command
  @positive
  Scenario: AJ-104 Ollama is up and the tag is present
    Given the fake Ollama endpoint answers /api/version and lists "nemotron-3-nano:30b"
    When I run "run --tier local --dry-run"
    Then the exit code is 0
    And the output contains "OK"
  @positive
  Scenario: AJ-104 the configured tag is found among many listed tags
    Given the fake endpoint lists 25 tags including "nemotron-3-nano:30b"
    When I run "run --tier local --dry-run"
    Then the exit code is 0
  @negative
  Scenario: AJ-104 Ollama refuses connections
    Given the fake endpoint refuses connections
    When I run "run --tier local --dry-run"
    Then the exit code is 5
    And the output contains the configured endpoint and "ollama serve"
  @negative
  Scenario: AJ-104 the endpoint times out
    Given the fake endpoint never answers within the configured timeout
    When I run "run --tier local --dry-run"
    Then the exit code is 5
    And the output contains "ollama serve"
  @negative
  Scenario: AJ-104 the tag is not pulled
    Given the fake endpoint lists tags that do not include "nemotron-3-nano:30b"
    When I run "run --tier local --dry-run"
    Then the exit code is 4
    And the output contains "ollama pull nemotron-3-nano:30b"
    And no request ended in "/chat/completions"
  @negative
  Scenario: AJ-104 another configured tag is named in the pull command
    Given Tiers:local:ModelId is "qwen3:8b" and the endpoint does not list it
    When I run "run --tier local --dry-run"
    Then the exit code is 4
    And the output contains "ollama pull qwen3:8b"
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-104-T1 | Write the failing feature and unit tests | tests/AgentJoules.Specs/Features/Workflow/OllamaFailures.feature, tests/AgentJoules.UnitTests/LocalRuntimeTests.cs | 0.5 | - |
| AJ-104-T2 | Implement the Ollama availability check with the connection-failure mapping | src/AgentJoules.Workflow/Local/ILocalRuntime.cs, src/AgentJoules.Workflow/Local/OllamaRuntime.cs | 0.75 | AJ-104-T1 |
| AJ-104-T3 | Map a missing tag to a model-not-found error carrying the pull command | src/AgentJoules.Workflow/Local/OllamaRuntime.cs, src/AgentJoules.Workflow/Runs/PreflightService.cs | 0.5 | AJ-104-T2 |
| AJ-104-T4 | Bind the feature steps and the refused and timeout modes of the fake handler | tests/AgentJoules.Specs/Steps/WorkflowSteps.cs, tests/AgentJoules.TestSupport/FakeHttpMessageHandler.cs | 0.5 | AJ-104-T3 |
| AJ-104-T5 | Record the messages in the troubleshooting notes | docs/build-log.md | 0.25 | AJ-104-T4 |
Test mapping: tests/AgentJoules.Specs/Features/Workflow/OllamaFailures.feature; unit class LocalRuntimeTests (design 6.1) with `[Trait("Spec","EDGE-009")]`.
Docs impact: docs/build-log.md (messages); README troubleshooting and exit-code rows 4 and 5 (written in M6).
Definition of done:
- [ ] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [ ] both messages checked against the live check in AJ-107
Demo relevance: no.

### AJ-105: Disclose the local runtime, tag, offload and precision in every local run
Covers: REQ-020
User story: As a reader of the local numbers, I want each local run file to name the runtime, the exact tag, the GPU offload percentage and the precision, so that a partly CPU-bound run is never presented as a full-GPU run.
Acceptance criteria:
1. A local run file without `non-publishable` in `run.flags` has `environment.localRuntime` in {`nim`, `ollama`}, `environment.runtimeVersion`, `models[].modelId` equal to the exact tag, and a numeric `environment.gpuOffloadPercent`.
2. For Ollama, `gpuOffloadPercent` = `size_vram / size x 100` of the configured tag from `GET /api/ps` after warm-up; for NIM it is 100 by construction.
3. A value below 100 adds `partial-offload` to `run.flags` and the report header shows it; `environment.precision` comes from the quantization level of `POST /api/show` (Ollama) or `Tiers:local:Precision` (NIM).
4. When the model is not listed by `/api/ps` after warm-up, or `size` is 0, the run stops with exit 6 and a message naming the cause; no value is guessed.
5. `dotnet test --filter "Spec=REQ-020" --logger "console;verbosity=normal"` shows at least one passed and no failed test.
BDD scenarios:
```gherkin
@unit @AJ-S105 @REQ-020
Feature: Disclose the local runtime, tag, offload and precision
  @positive
  Scenario: AJ-105 full offload on Ollama
    Given the fake /api/ps reports size_vram equal to size for the configured tag
    When I run "run --tier local --repeat 1 --task incident-brief"
    Then environment.localRuntime is "ollama" and environment.gpuOffloadPercent is 100
    And run.flags does not contain "partial-offload"
  @positive
  Scenario: AJ-105 the NIM runtime reports 100 by construction
    Given Local:Runtime is "nim" and Tiers:local:Precision is "BF16"
    When I run "run --tier local --repeat 1 --task incident-brief"
    Then environment.localRuntime is "nim" and environment.gpuOffloadPercent is 100
    And environment.precision is "BF16"
  @positive
  Scenario: AJ-105 precision comes from the Ollama quantization level
    Given the fake /api/show reports quantization level "Q4_K_M"
    When I run "run --tier local --repeat 1 --task incident-brief"
    Then environment.precision is "Q4_K_M"
  @negative
  Scenario: AJ-105 partial offload sets the flag and the header
    Given the fake /api/ps reports size_vram at 62 percent of size
    When I run "run --tier local --repeat 1 --task incident-brief"
    Then environment.gpuOffloadPercent is 62
    And run.flags contains "partial-offload"
    And the rendered report header contains "partial-offload"
  @negative
  Scenario: AJ-105 the model is missing from the process list
    Given the fake /api/ps lists no model after warm-up
    When I run "run --tier local --repeat 1 --task incident-brief"
    Then the exit code is 6
    And the output says the offload could not be determined
  @negative
  Scenario: AJ-105 a zero size is refused
    Given the fake /api/ps reports size 0 for the configured tag
    When I run "run --tier local --repeat 1 --task incident-brief"
    Then the exit code is 6
    And the run file contains no gpuOffloadPercent value of 0
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-105-T1 | Create or extend the failing feature and unit tests (`LocalRuntimeTests.cs` is created here when AJ-104 was cut) | tests/AgentJoules.Specs/Features/Workflow/LocalRuntimeDisclosure.feature, tests/AgentJoules.UnitTests/LocalRuntimeTests.cs | 0.5 | AJ-104-T5 |
| AJ-105-T2 | Create or extend the Ollama description (version, offload from /api/ps, quantization from /api/show; `ILocalRuntime.cs` and `OllamaRuntime.cs` are created here when AJ-104 was cut) | src/AgentJoules.Workflow/Local/ILocalRuntime.cs, src/AgentJoules.Workflow/Local/OllamaRuntime.cs | 0.75 | AJ-105-T1 |
| AJ-105-T3 | Implement the NIM description (offload 100, precision from configuration) | src/AgentJoules.Workflow/Local/NimRuntime.cs | 0.5 | AJ-105-T1 |
| AJ-105-T4 | Fill the environment fields and the partial-offload run flag in the assembler | src/AgentJoules.Workflow/Runs/RunAssembler.cs | 0.75 | AJ-105-T2, AJ-105-T3 |
| AJ-105-T5 | Show the flag in the report header | src/AgentJoules.Reporting/Render/SummaryMarkdownRenderer.cs | 0.5 | AJ-105-T4 |
| AJ-105-T6 | Create or extend the feature step bindings (`WorkflowSteps.cs`) | tests/AgentJoules.Specs/Steps/WorkflowSteps.cs | 0.5 | AJ-105-T5 |
| AJ-105-T7 | Record the verified Ollama endpoints and fields | docs/build-log.md | 0.25 | AJ-105-T6 |
Test mapping: tests/AgentJoules.Specs/Features/Workflow/LocalRuntimeDisclosure.feature; unit class LocalRuntimeTests with `[Trait("Spec","REQ-020")]`.
Docs impact: docs/build-log.md (Ollama `/api/ps`, `/api/show` fields as verified); README sentence about the local runtime is written in AJ-115.
Definition of done:
- [ ] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [ ] a missing offload value is never written as 0 (`SchemaValidationTests` still green)
Demo relevance: yes: the report header shows runtime, tag and offload next to the local joules.

### AJ-106: Order warm-up, idle baseline and repeats in local runs
Covers: REQ-021
User story: As a reader of the local numbers, I want every local run to warm the model up, measure the idle baseline with the model resident and repeat each task, so that load time is excluded and run-to-run spread is visible.
Acceptance criteria:
1. In a local run the recorded call order is: probe, meter start, one unmetered warm-up call (Ollama `POST /api/generate` with `keep_alive` of `30m`, NIM one short chat completion), baseline window, then task calls; the warm-up produces no step reading and no judge reading.
2. `environment.modelLoadSeconds` is numeric and equals the warm-up wall time; `baseline.seconds` equals `--baseline-seconds` (default 30); `environment.powerSource` is `ac`, `battery` or `unknown`, read from `IPowerSourceDetector` (Windows `GetSystemPowerStatus`, Linux `/sys/class/power_supply`; no supply files gives `unknown`).
3. `--repeat <n>` (default 3) executes every selected task n times after warm-up; every step carries `repeat` 1 to n and `run.repeat` equals n; `--repeat 0` exits 2.
4. A `--baseline-seconds` value below 30 prints `Warning: baseline shorter than 30 s is not publishable`, still runs, and adds `non-publishable` to `run.flags` (the schema lists it among the run flags); `RunFileSelector.Latest` never returns such a file (resolved, see the note under the story).
5. Hosted tiers perform no warm-up and no baseline window and ignore `--baseline-seconds`; a failing warm-up (Ollama refusing) exits 5 before the baseline window opens.
BDD scenarios:
```gherkin
@unit @AJ-S106 @REQ-021
Feature: Order warm-up, idle baseline and repeats in local runs
  @positive
  Scenario: AJ-106 the call order is warm-up, baseline, tasks
    Given a local run with fakes recording every operation
    When I run "run --tier local --task incident-brief --repeat 1 --baseline-seconds 30"
    Then the recorded order is probe, meter start, warm-up, baseline window, task calls
    And no step reading exists for the warm-up call
  @positive
  Scenario: AJ-106 the warm-up keeps the model loaded for 30 minutes
    Given the Ollama runtime is used
    When the warm-up call is made
    Then the request body contains keep_alive "30m" and an empty prompt
    And environment.modelLoadSeconds equals the fake warm-up duration
  @positive
  Scenario: AJ-106 three repeats produce twelve steps for one task
    When I run "run --tier local --task incident-brief --repeat 3"
    Then the run file has 12 steps with repeat values 1, 2 and 3 four times each
    And run.repeat is 3
  @positive
  Scenario: AJ-106 the power source is recorded
    Given the fake power source detector returns "battery"
    When I run "run --tier local --task incident-brief --repeat 1"
    Then environment.powerSource is "battery"
  @negative
  Scenario: AJ-106 a zero repeat is refused
    When I run "run --tier local --repeat 0"
    Then the exit code is 2
  @negative
  Scenario: AJ-106 a short baseline is flagged as not publishable
    When I run "run --tier local --task incident-brief --repeat 1 --baseline-seconds 10"
    Then the output contains "baseline shorter than 30 s is not publishable"
    And baseline.seconds is 10
    And run.flags contains "non-publishable"
  @positive
  Scenario: AJ-106 a 30 s baseline run carries no non-publishable flag
    When I run "run --tier local --task incident-brief --repeat 1 --baseline-seconds 30"
    Then run.flags does not contain "non-publishable"
  @negative
  Scenario: AJ-106 a short-baseline file is not selected as the latest local file
    Given a valid local run file and a newer local run file whose run.flags contain "non-publishable"
    When the latest file for tier "local" is selected
    Then the valid local run file is returned
  @negative
  Scenario: AJ-106 a failing warm-up stops before the baseline
    Given the fake Ollama endpoint refuses the warm-up request
    When I run "run --tier local --repeat 1"
    Then the exit code is 5
    And no baseline window was opened
  @negative
  Scenario: AJ-106 a hosted tier has no warm-up and no baseline window
    When I run "run --tier nvidia --task incident-brief --repeat 1 --baseline-seconds 30"
    Then no warm-up call and no baseline window were recorded
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-106-T1 | Create or extend the failing feature and unit tests | tests/AgentJoules.Specs/Features/Workflow/WarmupBaselineRepeat.feature, tests/AgentJoules.UnitTests/LocalRuntimeTests.cs | 0.75 | AJ-105-T7 |
| AJ-106-T2a | Implement `PowerSourceDetector` behind `IPowerSourceDetector` (Windows `GetSystemPowerStatus` through `LibraryImport("kernel32.dll")`, ACLineStatus 1 = ac, 0 = battery; Linux `/sys/class/power_supply/*/type` = Mains and `online`; no supply files = unknown) with a unit test through a fake | src/AgentJoules.Nvml/Platform/PowerSourceDetector.cs, tests/AgentJoules.UnitTests/PowerSourceDetectorTests.cs | 1.0 | AJ-106-T1 |
| AJ-106-T2 | Create or extend the Ollama warm-up with keep-alive and load-time capture | src/AgentJoules.Workflow/Local/OllamaRuntime.cs | 0.5 | AJ-106-T1 |
| AJ-106-T3 | Implement the NIM warm-up through an unmetered client | src/AgentJoules.Workflow/Local/NimRuntime.cs | 0.5 | AJ-106-T1 |
| AJ-106-T4 | Order warm-up, baseline and repeats in the orchestrator | src/AgentJoules.Workflow/Runs/RunOrchestrator.cs | 1.0 | AJ-106-T2, AJ-106-T2a, AJ-106-T3 |
| AJ-106-T5 | Add the short-baseline warning, the `non-publishable` run flag and the repeat validation | src/AgentJoules.Cli/Commands/RunCommand.cs, src/AgentJoules.Cli/Output/ConsoleReporter.cs | 0.5 | AJ-106-T4 |
| AJ-106-T6 | Bind the feature steps and the operation recorder | tests/AgentJoules.Specs/Steps/WorkflowSteps.cs, tests/AgentJoules.TestSupport/OperationRecorder.cs | 0.5 | AJ-106-T5 |
| AJ-106-T7 | Note the keep-alive setting for users | docs/build-log.md | 0.25 | AJ-106-T6 |
Test mapping: tests/AgentJoules.Specs/Features/Workflow/WarmupBaselineRepeat.feature; unit classes LocalRuntimeTests and AttributionBaselineTests (REQ-023 list) with `[Trait("Spec","REQ-021")]`.
Docs impact: docs/build-log.md (call order, `OLLAMA_KEEP_ALIVE=30m` advice for the README quick start, risk R-06).
Definition of done:
- [ ] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [ ] warm-up traffic is absent from `steps`, `judge` and `totals` in a fixture run file
Demo relevance: no.
Resolved: the prompt defines the run flag `non-publishable` for a baseline below 30 s; the option stays free for tests, the warning and the flag come from criterion 4, the run file is never "latest", and publishability is double-checked where evidence is committed, by AJ-114 (`baseline.seconds >= 30` checked with jq) and AJ-115 (`CommittedEvidenceTests`).

### AJ-107: Select and record the local model in docs/endpoints.md
Covers: REQ-018, REQ-020, EDGE-009
User story: As a reader of the local numbers, I want the local model verified on this machine and recorded with its tag, size and processor split, so that the published local tier names a model that was actually served.
Acceptance criteria:
1. When the AJ-103 decision is `ollama`: `ollama pull nemotron-3-nano:30b` succeeds (or the failure is recorded), `ollama list`, `ollama show nemotron-3-nano:30b` and `ollama ps` after a warm-up are recorded in the `## local` section with the quantization, the size and the processor split written as `<measured>` copies; the Ollama library page is checked for smaller Nemotron tags and the result is recorded.
2. Model choice rule: keep `nemotron-3-nano:30b` unless its processor split is below 100 percent GPU and a smaller verified Nemotron tag exists that runs fully on the GPU; in that case the smaller tag may be used, the reason is recorded in `## local` and in `docs/build-log.md`, and the models-differ disclosure of REQ-012 applies. Otherwise the 30b tag is kept and the run carries `partial-offload`.
3. The `## local` section also holds the Qwen3 8B tag exactly as `ollama list` prints it, marked `baseline (non-Nemotron)`, with `Verified: <UTC date>`, the command used and the list returned by `GET {BaseUrl}/models` (Nemotron and Qwen entries, total count). When the decision is `nim`, the section holds the NIM `/v1/models` result of AJ-103 instead.
4. Live EDGE-009 checks: with Ollama stopped, `AJ run --tier local --dry-run` exits 5 and prints `ollama serve`; with the tag overridden through `AGENTJOULES__Tiers__local__ModelId` to a tag that is not pulled, it exits 4 and prints the matching `ollama pull` command.
5. `AJ validate --config` exits 0 and `AJ run --tier local --dry-run` exits 0 with Ollama running.
6. The live EDGE-009 checks need these AJ-104 artifacts: the Ollama availability probe (`GET /api/version`, connection failure mapped to exit 5 with `ollama serve`), the missing-tag mapping (exit 4 with `ollama pull <tag>`), and the warm-up keep-alive and `/api/ps` reading that AJ-105 and AJ-106 extend. If AJ-104 was cut, AJ-107-T1 creates them in `OllamaRuntime.cs`, `ILocalRuntime.cs` and `PreflightService.cs`; the hours of AJ-104-T2 and AJ-104-T3 (1.25 h) move into AJ-107-T1 in the plan with no change to the milestone total; `AJ run --tier local --dry-run` with Ollama stopped prints `ollama serve` and exits 5.
BDD scenarios:
```gherkin
@integration @AJ-S107 @REQ-018 @REQ-020 @EDGE-009
Feature: Select and record the local model
  @positive
  Scenario: AJ-107 the Nemotron tag is verified and recorded
    Given Ollama is running and the Local:Runtime decision is "ollama"
    When the builder pulls and shows "nemotron-3-nano:30b" and lists the models endpoint
    Then the "## local" section holds the tag, the quantization, the size and a Verified date
  @positive
  Scenario: AJ-107 the Qwen3 8B tag is recorded as baseline
    Given "ollama list" shows a Qwen3 8B tag
    When the builder writes the "## local" section
    Then the Qwen tag is recorded exactly and marked "baseline (non-Nemotron)"
  @positive
  Scenario: AJ-107 partial offload is recorded with the processor split
    Given "ollama ps" after warm-up shows less than 100 percent GPU for the 30b tag
    When the builder records the model choice
    Then the processor split and the reason for keeping or replacing the tag are recorded
  @negative
  Scenario: AJ-107 Ollama stopped gives exit 5 with the start command
    Given the Ollama service is stopped
    When I run "run --tier local --dry-run"
    Then the exit code is 5
    And the output contains "ollama serve"
  @negative
  Scenario: AJ-107 an unpulled tag gives exit 4 with the pull command
    Given the local model ID is overridden to a tag that is not pulled
    When I run "run --tier local --dry-run"
    Then the exit code is 4
    And the output contains "ollama pull" followed by that tag
  @negative
  Scenario: AJ-107 the library lists no smaller Nemotron tag
    Given the Ollama library page lists no smaller verified Nemotron tag
    When the builder records the model choice
    Then the 30b tag is kept and the result of the library check is recorded
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-107-T1 | Write the failing feature and the gated local listing test; create the AJ-104 Ollama probe and missing-tag mapping here when AJ-104 was cut (criterion 6) | tests/AgentJoules.Specs/Features/Configuration/LocalModelRecord.feature, tests/AgentJoules.IntegrationTests/LocalModelListingTests.cs, src/AgentJoules.Workflow/Local/ILocalRuntime.cs, src/AgentJoules.Workflow/Local/OllamaRuntime.cs, src/AgentJoules.Workflow/Runs/PreflightService.cs | 0.5 | AJ-106-T7 |
| AJ-107-T2 | Pull the Nemotron tag and record show and list output | docs/endpoints.md | 1.0 | AJ-107-T1 |
| AJ-107-T3 | Check the Ollama library page for smaller Nemotron tags | docs/endpoints.md | 0.5 | AJ-107-T1 |
| AJ-107-T4 | Measure the processor split after warm-up and record the model choice | docs/endpoints.md, docs/build-log.md | 0.5 | AJ-107-T2 |
| AJ-107-T5 | Write the local section with the Qwen baseline entry and the models list | docs/endpoints.md | 0.5 | AJ-107-T4 |
| AJ-107-T6 | Set the local tier model and agent IDs and run the validation | config/appsettings.json | 0.5 | AJ-107-T5 |
| AJ-107-T7 | Run the two live failure checks and record the output | docs/build-log.md | 0.5 | AJ-107-T6 |
Test mapping: tests/AgentJoules.Specs/Features/Configuration/LocalModelRecord.feature; AJ-104 LocalRuntimeTests for the messages (created in AJ-107-T1 when AJ-104 was cut); gated class LocalModelListingTests (design 6.3).
Docs impact: docs/endpoints.md (`## local`), docs/build-log.md (model choice and reason, live EDGE-009 output), config/appsettings.json.
Definition of done:
- [ ] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [ ] `AJ validate --config` exit 0 recorded in docs/build-log.md
Demo relevance: no (the report header quotes the tag).

### AJ-108: Verify streaming usage and the reasoning toggle per tier
Covers: REQ-018
User story: As a reader of the results, I want the streaming usage behaviour and the reasoning mode verified and recorded for every tier, so that tokens and the thinking setting in the tables are facts and not assumptions.
Acceptance criteria:
1. For each of `local`, `nvidia` and `nebius`, `docs/endpoints.md` records one streaming call made with `stream_options.include_usage`: the line `Streaming usage: reported` or `Streaming usage: not-reported`, and the input and output token counts as `<measured>` copies when reported.
2. For each tier the section records `Reasoning toggle: <exact JSON merged into the request body>` or `Reasoning toggle: unsupported`, and `Reasoning result: off` or `on` or `unsupported`, with the evidence: the same short prompt is sent without and with the toggle and the responses are compared for reasoning content or a think block (open point O-02; the assumed JSON is verified, not trusted).
3. The configuration carries the verified `Tiers:<t>:ExtraBodyJson` and `Tiers:<t>:ReasoningMode` (`off`, `on` or `unsupported`) for each tier; a tier that cannot disable reasoning is stated in `docs/endpoints.md`, in `docs/build-log.md` (risk R-05) and is carried to the report header by M6.
4. When Ollama omits streaming usage (open point O-04), the section says so, `docs/build-log.md` records the ADR-09 decision (tokens `not-reported` for local streaming, or buffered local calls with TTFT `N/A`), and the configuration or code follows that decision.
5. One metered call per tier through the real stack, written to a scratch folder with `--out`, shows tokens labelled `measured` where usage is reported and `not-reported` where it is not; `AJ run --tier all --dry-run` exits 0.
BDD scenarios:
```gherkin
@integration @AJ-S108 @REQ-018
Feature: Verify streaming usage and the reasoning toggle per tier
  @positive
  Scenario: AJ-108 the nvidia tier reports streaming usage and honours the toggle
    Given NVIDIA_API_KEY is set
    When one streaming call is made with include_usage and the reasoning toggle JSON
    Then "Streaming usage: reported" is recorded with the token counts
    And "Reasoning result: off" is recorded with the evidence
  @positive
  Scenario: AJ-108 the nebius tier is verified the same way
    Given NEBIUS_API_KEY is set
    When the same two calls are made against the nebius tier
    Then its section records the usage behaviour, the toggle JSON and the result
  @negative
  Scenario: AJ-108 Ollama omits streaming usage
    Given the local Ollama endpoint returns no usage in the final stream chunk
    When the builder records the local section
    Then "Streaming usage: not-reported" is recorded
    And docs/build-log.md records the ADR-09 decision
    And a metered local call shows tokens labelled "not-reported" and never 0
  @negative
  Scenario: AJ-108 the toggle is ignored by the endpoint
    Given the response with the toggle still contains reasoning content
    When the builder records the tier section
    Then "Reasoning result: unsupported" is recorded and Tiers:<tier>:ReasoningMode is "unsupported"
  @negative
  Scenario: AJ-108 the endpoint rejects the unknown body field
    Given the endpoint answers HTTP 400 to the request containing the toggle JSON
    When the builder records the tier section
    Then the section records "Reasoning toggle: unsupported" with the error excerpt and no key value
  @negative
  Scenario: AJ-108 a missing key stops the check
    Given NVIDIA_API_KEY is not set
    When I run "run --tier all --dry-run"
    Then the exit code is 3
    And the output names "NVIDIA_API_KEY"
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-108-T1 | Write the failing feature and the gated streaming usage test | tests/AgentJoules.Specs/Features/Configuration/StreamingUsageAndReasoning.feature, tests/AgentJoules.IntegrationTests/StreamingUsageTests.cs | 0.75 | AJ-107-T7 |
| AJ-108-T2 | Verify streaming usage and the reasoning toggle on the local tier | docs/endpoints.md | 0.75 | AJ-108-T1 |
| AJ-108-T3 | Verify streaming usage and the reasoning toggle on the nvidia tier | docs/endpoints.md | 0.75 | AJ-108-T1 |
| AJ-108-T4 | Verify streaming usage and the reasoning toggle on the nebius tier | docs/endpoints.md | 0.75 | AJ-108-T1 |
| AJ-108-T5 | Set ExtraBodyJson and ReasoningMode per tier | config/appsettings.json | 0.5 | AJ-108-T2, AJ-108-T3, AJ-108-T4 |
| AJ-108-T6 | Run one metered call per tier into a scratch folder and inspect the labels | docs/build-log.md | 0.75 | AJ-108-T5 |
| AJ-108-T7 | Record the ADR-09 and R-05 decisions | docs/build-log.md | 0.25 | AJ-108-T6 |
Test mapping: tests/AgentJoules.Specs/Features/Configuration/StreamingUsageAndReasoning.feature; MeteredChatClientTests (REQ-023 list) for the no-usage label path; gated class StreamingUsageTests (design 6.3).
Docs impact: docs/endpoints.md (per-tier lines `Streaming usage:`, `Reasoning toggle:`, `Reasoning result:`), docs/build-log.md, config/appsettings.json.
Definition of done:
- [ ] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [ ] `AJ run --tier all --dry-run` exit code 0 recorded in docs/build-log.md
Demo relevance: no (the report header shows the reasoning mode per tier).

### AJ-109: Compute the counter versus sampling calibration
Covers: REQ-021, REQ-014
User story: As a reader who doubts the energy figures, I want `AJ calibrate` to measure one workload window with both methods and publish both totals and their relative difference, so that the agreement or disagreement of the two methods is a published fact.
Acceptance criteria:
1. `AJ calibrate [--task <name>] [--seconds <n>]` (defaults: the first task in file order, 60) runs a warm-up, then one window during which the task workflow repeats until `--seconds` have elapsed, and writes `results/calibration-<yyyyMMddTHHmmssZ>.json`.
2. The file holds `counterJoules`, `samplingJoules` and `relativeDifference` as label objects (`measured` numbers), plus task, seconds, `sampleCount` and `sampleRateHz` as plain fields; `relativeDifference` = (counter - sampling) / counter.
3. When the counter is unsupported, `counterJoules` is `{value:null,label:"N/A",reason:"counter-not-supported: <probe reason>"}` and `relativeDifference` is N/A with a reason; the command exits 0 and nothing is estimated. A counter of 0 gives `relativeDifference` N/A with reason `counter-zero`.
4. Without a GPU the command exits 6 and writes no file; `--seconds 0` or an unknown `--task` exits 2.
5. The file validates against `$defs/calibration` of `docs/results-schema.json`; `dotnet test --filter "Spec=REQ-021" --logger "console;verbosity=normal"` shows at least one passed and no failed test.
6. Added 2026-10-20 (AJ-051 spec review): `calibrate` offers the option from `TelemetryOptions.CreateNoConsoleTelemetry()` and its host calls `TelemetrySetup.Add(services, configuration, consoleEnabled)` with the console metrics exporter on unless the flag is given (test `Calibrate_no_console_telemetry_omits_console_exporter`, trait `Spec=REQ-014`).
BDD scenarios:
```gherkin
@unit @AJ-S109 @REQ-021
Feature: Compute the counter versus sampling calibration
  @positive
  Scenario: AJ-109 both methods are written with the relative difference
    Given a fake meter whose counter reads 100 J and whose sampling integral reads 98 J for the window
    When I run "calibrate --seconds 60"
    Then the file has counterJoules 100, samplingJoules 98 and relativeDifference 0.02, each labelled "measured"
  @positive
  Scenario: AJ-109 the file name and schema are valid
    When I run "calibrate --seconds 60"
    Then a file matching "calibration-<yyyyMMddTHHmmssZ>.json" exists in the output folder
    And it validates against the calibration schema definition
  @positive
  Scenario: AJ-109 the default task is the first task in file order
    When I run "calibrate"
    Then the file names the first task file and seconds 60
  @negative
  Scenario: AJ-109 an unsupported counter is written as N/A with the reason
    Given the fake NVML returns NOT_SUPPORTED for the energy counter
    When I run "calibrate --seconds 60"
    Then counterJoules is null with label "N/A" and a non-empty reason
    And relativeDifference is null with label "N/A"
    And the exit code is 0
  @negative
  Scenario: AJ-109 a counter of zero gives an N/A difference
    Given the fake counter reads 0 J for the window
    When I run "calibrate --seconds 60"
    Then relativeDifference is null with reason "counter-zero"
  @negative
  Scenario: AJ-109 no GPU exits 6 and writes nothing
    Given the NVML loader is faked as missing
    When I run "calibrate"
    Then the exit code is 6
    And no calibration file exists
  @negative
  Scenario: AJ-109 a zero duration is refused
    When I run "calibrate --seconds 0"
    Then the exit code is 2
  @negative
  Scenario: AJ-109 an unknown task is refused
    When I run "calibrate --task no-such-task"
    Then the exit code is 2
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-109-T1 | Write the failing feature and unit tests | tests/AgentJoules.Specs/Features/Cli/Calibration.feature, tests/AgentJoules.UnitTests/CalibrationTests.cs | 0.75 | AJ-108-T7 |
| AJ-109-T2 | Add or confirm the calibration document model and its schema definition | src/AgentJoules.Core/Results/CalibrationDocument.cs, src/AgentJoules.Reporting/Json/AgentJoulesJsonContext.cs, docs/results-schema.json | 0.75 | AJ-109-T1 |
| AJ-109-T3 | Implement the calibration service (one window, two methods, difference) | src/AgentJoules.Workflow/Runs/CalibrationService.cs | 1.0 | AJ-109-T2 |
| AJ-109-T4 | Implement the calibrate command and its argument checks, incl. `--no-console-telemetry` and the `TelemetrySetup.Add` call | src/AgentJoules.Cli/Commands/CalibrateCommand.cs | 0.75 | AJ-109-T3 |
| AJ-109-T5 | Bind the feature steps | tests/AgentJoules.Specs/Steps/CliSteps.cs | 0.5 | AJ-109-T4 |
| AJ-109-T6 | Regenerate the schema and run the committed-schema test | docs/results-schema.json | 0.25 | AJ-109-T5 |
| AJ-109-T7 | Document the command and the file fields | docs/build-log.md | 0.25 | AJ-109-T6 |
Test mapping: tests/AgentJoules.Specs/Features/Cli/Calibration.feature; unit classes CalibrationTests and SchemaValidationTests (REQ-023 list) with `[Trait("Spec","REQ-021")]`.
Docs impact: docs/results-schema.json (`$defs/calibration`), docs/build-log.md; README CLI reference row for `calibrate` (written in M6).
Definition of done:
- [ ] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [ ] `CommittedSchemaMatchesGenerated` still green after the schema change
Demo relevance: yes: the calibration result backs the claim that the counter and sampling agree (or by how much they differ).

### AJ-110: Run the preflight for the laptop measurement campaign
Covers: REQ-021
User story: As the author, I want a fixed preflight (probe, dry-run, power and load conditions) committed before any measurement run, so that every published number comes from a known machine state.
Acceptance criteria:
1. `AJ probe --json --save` exits 0 and writes `results/probe-<yyyyMMddTHHmmssZ>.json` with `gpuPresent` true, a `driverVersion` equal to the output of `nvidia-smi --query-gpu=driver_version --format=csv,noheader`, and a boolean `energyCounterSupported`; the file is committed (standing order 4).
2. `AJ run --tier all --dry-run` exits 0 and prints, per enabled tier, the endpoint, the matched model IDs and `OK`.
3. The laptop is on mains power (`AJ probe` text output or the first run's `environment.powerSource` is `ac`); the campaign is not started on `battery`; `unknown` is accepted only with a note in `docs/build-log.md`.
4. Conditions are recorded in `docs/build-log.md`: Ollama running with `OLLAMA_KEEP_ALIVE=30m` (when the runtime is Ollama), other GPU applications closed, and the output of `nvidia-smi --query-compute-apps=pid,process_name --format=csv`.
5. When `energyCounterSupported` is false the campaign still proceeds with sampling (EDGE-002 disclosure), and `docs/build-log.md` states that the README results block must contain the word `sampling`.
BDD scenarios:
```gherkin
@integration @AJ-S110 @REQ-021
Feature: Run the preflight for the laptop measurement campaign
  @positive
  Scenario: AJ-110 the probe is saved and matches nvidia-smi
    Given the laptop GPU and driver are present
    When I run "probe --json --save"
    Then the exit code is 0
    And the saved probe has gpuPresent true and the driver version printed by nvidia-smi
  @positive
  Scenario: AJ-110 the dry-run passes for every enabled tier
    Given NVIDIA_API_KEY and NEBIUS_API_KEY are set and Ollama is running
    When I run "run --tier all --dry-run"
    Then the exit code is 0
    And each of "local", "nvidia" and "nebius" is followed by "OK"
  @negative
  Scenario: AJ-110 battery power blocks the campaign
    Given the probe reports power source "battery"
    When the builder evaluates the preflight
    Then the campaign is not started
    And the user is asked to connect mains power
  @negative
  Scenario: AJ-110 an unsupported counter proceeds with sampling
    Given the saved probe has energyCounterSupported false
    When the builder evaluates the preflight
    Then the campaign proceeds with method "sampling"
    And docs/build-log.md notes that the README results block must contain "sampling"
  @negative
  Scenario: AJ-110 a missing key fails the dry-run before any call
    Given NEBIUS_API_KEY is not set
    When I run "run --tier all --dry-run"
    Then the exit code is 3
    And the output names "NEBIUS_API_KEY"
  @negative
  Scenario: AJ-110 another GPU application is running
    Given nvidia-smi lists a compute process other than Ollama
    When the builder evaluates the preflight
    Then the process is recorded and the user is asked to close it before the campaign
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-110-T1 | Write the failing feature and the gated probe test | tests/AgentJoules.Specs/Features/Cli/CampaignPreflight.feature, tests/AgentJoules.IntegrationTests/LaptopProbeTests.cs | 0.5 | AJ-109-T7 |
| AJ-110-T2 | USER ACTION: connect mains power, close other GPU applications, confirm the keys are still set | none (user machine) | 0.1 | - |
| AJ-110-T3 | Run the probe and save the file | results/probe-<yyyyMMddTHHmmssZ>.json | 0.25 | AJ-110-T1, AJ-110-T2 |
| AJ-110-T4 | Run the all-tier dry-run and record the exit code | docs/build-log.md | 0.25 | AJ-110-T3 |
| AJ-110-T5 | Record the machine conditions and compute-app listing | docs/build-log.md | 0.25 | AJ-110-T3 |
| AJ-110-T6 | Commit the probe file with the `AJ-110 REQ-021:` message prefix | results/probe-<yyyyMMddTHHmmssZ>.json | 0.1 | AJ-110-T4, AJ-110-T5 |
Test mapping: tests/AgentJoules.Specs/Features/Cli/CampaignPreflight.feature; ProbeCommandTests (design 6.1) for the file shape; gated class LaptopProbeTests (design 6.3).
Docs impact: docs/build-log.md (preflight conditions), results/probe-<yyyyMMddTHHmmssZ>.json.
Definition of done:
- [ ] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [ ] the committed probe file validates against `$defs/probe` (`SchemaValidationTests`)
Demo relevance: yes: `AJ probe` on the laptop GPU is the first shot of the video.

### AJ-111: Run the calibration on the laptop and commit the file
Covers: REQ-021
User story: As the author, I want one calibration run on the laptop committed with the results, so that the methodology document can quote the measured agreement of the two methods.
Acceptance criteria:
1. After AJ-110, `AJ calibrate --seconds 60` exits 0 on the laptop with the local runtime up and writes `results/calibration-<yyyyMMddTHHmmssZ>.json`.
2. `jq '[.counterJoules, .samplingJoules, .relativeDifference]'` on the file prints three label objects: numbers labelled `measured`, or `null` with label `N/A` and a non-empty `reason` when the probe shows the counter unsupported.
3. The `relativeDifference` value recomputed from the two joule values with `jq` equals the stored value within 1e-9.
4. The file is committed with a message starting `AJ-111 REQ-021:` (story id first, then the ids delivered; check regex `^AJ-[0-9]{3}( (REQ|EDGE)-[0-9]{3})*:`); whatever value results is published, and a large difference is explained in the methodology and never hidden (risk R-02).
BDD scenarios:
```gherkin
@integration @AJ-S111 @REQ-021
Feature: Run the calibration on the laptop
  @positive
  Scenario: AJ-111 the calibration file is written with three label objects
    Given the preflight of AJ-110 passed and Ollama serves the local model
    When I run "calibrate --seconds 60"
    Then the exit code is 0
    And the new calibration file has counterJoules, samplingJoules and relativeDifference as label objects
  @positive
  Scenario: AJ-111 the stored difference matches the recomputed value
    Given the committed calibration file with counterJoules and samplingJoules both measured
    When the difference is recomputed as (counter - sampling) / counter
    Then it equals the stored relativeDifference within 1e-9
  @negative
  Scenario: AJ-111 Ollama is not running
    Given the Ollama service is stopped
    When I run "calibrate --seconds 60"
    Then the exit code is 5
    And no calibration file is written
  @negative
  Scenario: AJ-111 the counter is unsupported on this GPU
    Given the committed probe has energyCounterSupported false
    When I run "calibrate --seconds 60"
    Then counterJoules has label "N/A" with a reason and the exit code is 0
  @negative
  Scenario: AJ-111 the laptop runs on battery
    Given the probe reports power source "battery"
    When the builder evaluates the calibration precondition
    Then the calibration is not run until mains power is connected
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-111-T1 | Write the failing feature and the gated calibration check | tests/AgentJoules.Specs/Features/Cli/CalibrationLive.feature, tests/AgentJoules.IntegrationTests/CalibrationLiveTests.cs | 0.5 | AJ-110-T6 |
| AJ-111-T2 | Run the calibration on the laptop | results/calibration-<yyyyMMddTHHmmssZ>.json | 0.5 | AJ-111-T1 |
| AJ-111-T3 | Validate the file against the schema and recompute the difference | docs/build-log.md | 0.25 | AJ-111-T2 |
| AJ-111-T4 | Commit the calibration file with the `AJ-111 REQ-021:` prefix | results/calibration-<yyyyMMddTHHmmssZ>.json | 0.1 | AJ-111-T3 |
Test mapping: tests/AgentJoules.Specs/Features/Cli/CalibrationLive.feature; SchemaValidationTests (REQ-023 list) for the file shape; gated class CalibrationLiveTests (design 6.3).
Docs impact: docs/build-log.md (command, exit code, file name); the methodology quote is written in AJ-112.
Definition of done:
- [ ] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [ ] the file name is recorded for AJ-112
Demo relevance: yes: the README states the relative difference and the file name.

### AJ-112: Write the methodology document with a contract test
Covers: REQ-022
User story: As a skeptical reader, I want a methodology document that states what is measured, how and with which limits, so that no energy figure is read as more than it is.
Acceptance criteria:
1. Merge rule: M3 stories may already have written sentences into `docs/methodology.md`; this story creates the headings by merging, keeps every existing sentence verbatim (placed under the heading it belongs to, or under `Limitations` when none fits), and never overwrites the file; the contract test compares the pre-existing sentences with the result.
2. `docs/methodology.md` has the headings `Measurement`, `Attribution`, `Baseline`, `Labels` and `Limitations` (`grep -cE '^#+ +(Measurement|Attribution|Baseline|Labels|Limitations)' docs/methodology.md` prints 5) and a line `Verified: <UTC date>`.
3. Measurement explains what NVML measures, both methods and their units (millijoules and milliwatts), the sample rate, and where windows start and stop relative to the HTTP call (`grep -ciE "millijoule|milliwatt|sample rate|HTTP call" docs/methodology.md` prints 4 or more); Attribution explains one metered client per agent and why no ambient context is used; Baseline explains gross and net energy and the concurrent-mode union totals; Labels lists the five labels and the flags.
4. The Limitations section contains each of the strings `GPU-only`, `display`, `power limit`, `sampling`, `offload`, `hosted`, `cloud`, covering: GPU-board scope, GPU shared with the display, the laptop power limit, sampling resolution and possible driver averaging, partial CPU offload, hosted energy `N/A`, and the cloudgpu tier (dedicated VM GPU with the provider's driver and host power management, cost observed from the billing page, no comparison of laptop and datacenter joules as efficiency claims).
5. The document names the committed calibration file and quotes its `relativeDifference` exactly as stored; it quotes the power limit from the committed probe and notes that the NVML enforced limit and the `nvidia-smi` limit may differ (conflict C-11); it states whether the clock-throttle reasons API worked on the laptop (EDGE-013).
6. A contract test fails when a heading or a required string is missing, when the quoted difference differs from the file, or when no calibration file is named; `dotnet test --filter "Spec=REQ-022" --logger "console;verbosity=normal"` shows at least one passed and no failed test.
7. The three REQ-032 sentences are written in `Limitations` (this story is the MUST path; the dashboard stories that first wrote them are cut candidates): "The dashboard server adds no GPU work." and "A browser tab adds compositing load, so published runs are recorded without one." and "Estimates shown on the live dashboard never enter results files."; `grep -cF "adds no GPU work" docs/methodology.md` and `grep -cF "adds compositing load" docs/methodology.md` and `grep -cF "never enter results files" docs/methodology.md` each print 1.
BDD scenarios:
```gherkin
@unit @AJ-S112 @REQ-022
Feature: Write the methodology document with a contract test
  @positive
  Scenario: AJ-112 a complete document passes
    Given a methodology document with the five headings and the seven Limitations strings
    And it names a calibration file and quotes its stored relativeDifference
    When the contract test runs
    Then the result is pass
  @positive
  Scenario: AJ-112 the Limitations section may use the strings in any order
    Given the seven strings appear in a different order
    When the contract test runs
    Then the result is pass
  @negative
  Scenario: AJ-112 a missing heading fails
    Given the document has no "Baseline" heading
    When the contract test runs
    Then the result is fail and the message names "Baseline"
  @negative
  Scenario: AJ-112 a missing Limitations string fails
    Given the Limitations section lacks the string "cloud"
    When the contract test runs
    Then the result is fail and the message names "cloud"
  @negative
  Scenario: AJ-112 a quoted difference that differs from the file fails
    Given the document quotes 0.5 while the named calibration file stores a different value
    When the contract test runs
    Then the result is fail and the message names both values
  @negative
  Scenario: AJ-112 no calibration file is named
    Given the document names no results/calibration file
    When the contract test runs
    Then the result is fail and the message says the calibration file must be named
  @negative
  Scenario: AJ-112 a missing dashboard sentence fails
    Given the Limitations section lacks the sentence "The dashboard server adds no GPU work." or the sentence "Estimates shown on the live dashboard never enter results files."
    When the contract test runs
    Then the result is fail and the message names the sentence
  @negative
  Scenario: AJ-112 an existing sentence from an earlier milestone survives the merge
    Given docs/methodology.md already holds a sentence written by an M3 story under no required heading
    When the headings are created by merging into the existing document
    Then the sentence is still present verbatim in the document
    And the five required headings exist```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-112-T1 | Write the failing feature and the contract tests | tests/AgentJoules.Specs/Features/Reporting/MethodologyContract.feature, tests/AgentJoules.UnitTests/MethodologyDocumentTests.cs | 0.5 | AJ-111-T4 |
| AJ-112-T2 | Implement the methodology parser and rules | tests/AgentJoules.TestSupport/Docs/MethodologyDocumentParser.cs | 0.75 | AJ-112-T1 |
| AJ-112-T3 | Bind the feature steps | tests/AgentJoules.Specs/Steps/ReportingSteps.cs | 0.25 | AJ-112-T2 |
| AJ-112-T4 | Create the five headings and write the Measurement, Attribution, Baseline and Labels sections, merging into any existing text (never overwrite) | docs/methodology.md | 1.5 | AJ-112-T3 |
| AJ-112-T5 | Write the Limitations section, the two dashboard sentences and the calibration and probe quotes | docs/methodology.md | 1.0 | AJ-112-T4 |
| AJ-112-T6 | Run the contract test on the committed document and record the result | docs/build-log.md | 0.25 | AJ-112-T5 |
Test mapping: tests/AgentJoules.Specs/Features/Reporting/MethodologyContract.feature; unit class MethodologyDocumentTests with `[Trait("Spec","REQ-022")]`.
Docs impact: docs/methodology.md (new), docs/build-log.md.
Definition of done:
- [ ] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [ ] every number in the document is copied from a named committed file; none is typed from memory
Demo relevance: no (the README and article link it).

### AJ-113: Mark non-Nemotron local runs as baseline
Covers: REQ-020, REQ-021
User story: As a reader of the tables, I want any local run on a non-Nemotron model such as Qwen3 8B to be labelled `baseline` in its file name, its model list and the report, so that it is never read as a Nemotron result.
Acceptance criteria:
1. A run whose agent model IDs include one without `nemotron` (case-insensitive) is written as `results/<yyyyMMddTHHmmssZ>-local-baseline.json`, and each such `environment.models[]` entry has `baseline` true.
2. `RunFileSelector.Latest(dir, local)` ignores `-baseline` files even when they are newer; `AJ validate results/` accepts them and counts them in `Validated <n> files`.
3. The report labels baseline rows `baseline (non-Nemotron)` and never merges them into the Nemotron row of the same tier.
4. `dotnet test --filter "Spec=REQ-020" --logger "console;verbosity=normal"` shows at least one passed and no failed test.
BDD scenarios:
```gherkin
@unit @AJ-S113 @REQ-020 @REQ-021
Feature: Mark non-Nemotron local runs as baseline
  @positive
  Scenario: AJ-113 a Qwen run gets the baseline suffix
    Given Tiers:local:AgentModelIds name a Qwen3 8B tag for all agents
    When I run "run --tier local --task incident-brief --repeat 1"
    Then the run file name ends with "-local-baseline.json"
    And every environment.models entry has baseline true
  @positive
  Scenario: AJ-113 a Nemotron run has no suffix
    Given all agent model IDs contain "nemotron"
    When I run "run --tier local --task incident-brief --repeat 1"
    Then the run file name ends with "-local.json" and no model has baseline true
  @negative
  Scenario: AJ-113 the latest-file selection skips a newer baseline file
    Given a Nemotron local file and a newer baseline local file
    When the latest file for tier "local" is selected
    Then the Nemotron file is returned
  @positive
  Scenario: AJ-113 validate counts baseline files
    Given three run files of which one has the baseline suffix
    When I run "validate results/"
    Then the exit code is 0 and the output contains "Validated 3 files"
  @negative
  Scenario: AJ-113 one non-Nemotron agent makes the whole file baseline
    Given only the Reviewer agent uses a Qwen tag
    When I run "run --tier local --task incident-brief --repeat 1"
    Then the run file name ends with "-local-baseline.json"
  @negative
  Scenario: AJ-113 the report never merges a baseline row into the Nemotron row
    Given a report input with a Nemotron local file and a baseline local file
    When the report is rendered
    Then the summary has separate rows and the baseline row is labelled "baseline (non-Nemotron)"
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-113-T1 | Write the failing feature and unit tests | tests/AgentJoules.Specs/Features/Reporting/BaselineLabelling.feature, tests/AgentJoules.UnitTests/ReportRenderingTests.cs | 0.5 | AJ-112-T6 |
| AJ-113-T2 | Add or confirm the baseline suffix and model marker rule | src/AgentJoules.Reporting/Results/RunFileName.cs, src/AgentJoules.Workflow/Runs/RunAssembler.cs | 0.5 | AJ-113-T1 |
| AJ-113-T3 | Add or confirm the latest-file exclusion of baseline files | src/AgentJoules.Reporting/Results/RunFileSelector.cs | 0.25 | AJ-113-T2 |
| AJ-113-T4 | Label baseline rows in the summary | src/AgentJoules.Reporting/Render/SummaryMarkdownRenderer.cs | 0.5 | AJ-113-T3 |
| AJ-113-T5 | Bind the feature steps | tests/AgentJoules.Specs/Steps/ReportingSteps.cs | 0.25 | AJ-113-T4 |
| AJ-113-T6 | Note the baseline convention | docs/build-log.md | 0.25 | AJ-113-T5 |
Test mapping: tests/AgentJoules.Specs/Features/Reporting/BaselineLabelling.feature; unit classes ReportRenderingTests and SchemaValidationTests (REQ-023 list) with `[Trait("Spec","REQ-020")]`.
Docs impact: docs/build-log.md; README results note on baseline rows (written in M6).
Definition of done:
- [ ] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [ ] a committed baseline file, if any, is excluded from the README results block
Demo relevance: no.

### AJ-114: Run the laptop measurement campaign for local, nvidia and nebius
Covers: REQ-018, REQ-020, REQ-021
User story: As the author, I want one committed run file each for `local`, `nvidia` and `nebius` produced by a fixed command sequence, so that every published laptop and hosted number traces to a file that validates.
Acceptance criteria:
1. Start state: `git status --porcelain` prints nothing, `dotnet build -warnaserror -tl:off` exits 0, the AJ-110 preflight conditions hold, and the AJ-111 calibration file is committed.
2. Sequence, from the repository root (`AJ` = `dotnet run --project src/AgentJoules.Cli --`): (a) `AJ probe --json --save` (new probe file); (b) `AJ run --tier all --dry-run` exits 0; (c) `AJ run --tier all --repeat 3`, mode `serialized`, all three tasks, baseline 30 s, started detached with its log and exit-code marker outside the repository; (d) `AJ validate results/`.
3. The run in (c) writes `results/<yyyyMMddTHHmmssZ>-local.json`, `-nvidia.json` and `-nebius.json` with `run.status` `completed`, exactly 3 tasks, `run.repeat` equal to 3, 36 steps per file (3 tasks x 3 repeats x 4 agents), a non-empty `judge` array, and `outcome` `ok` on every step.
4. The local file has `baseline.seconds` of at least 30, a numeric `environment.modelLoadSeconds`, `environment.powerSource` in {ac, battery, unknown}, a numeric `environment.gpuOffloadPercent` and `environment.localRuntime` per AJ-103; every hosted-tier step has `grossJoules` and `netJoules` as `{value:null,label:"N/A"}` with a reason.
5. `AJ validate results/` exits 0 and prints `Validated <n> files` where n equals `ls results/ | grep -cE '^[0-9]{8}T[0-9]{6}Z-(local|cloudgpu|nvidia|nebius)(-baseline)?\.json$'`.
6. A failed tier file (status `failed`) is not committed: the failing tier is re-run alone (`AJ run --tier <tier> --repeat 3`) after the cause is fixed, and the incident (file name, status, reason) is logged in `docs/build-log.md`; `git grep` for keys prints nothing; the files are committed with a message starting `AJ-114 REQ-021:` (story id first; check regex `^AJ-[0-9]{3}( (REQ|EDGE)-[0-9]{3})*:`).
7. If time remains, one optional Qwen3 8B run (`AGENTJOULES__Tiers__local__AgentModelIds` overrides, one task, `--repeat 1`) produces a `-local-baseline.json` file; it is not an exit criterion.
BDD scenarios:
```gherkin
@integration @AJ-S114 @REQ-018 @REQ-020 @REQ-021
Feature: Run the laptop measurement campaign
  @positive
  Scenario: AJ-114 the full campaign writes three completed run files
    Given the preflight passed, the keys are set and Ollama serves the local model
    When I run "run --tier all --repeat 3"
    Then the exit code is 0
    And one completed run file exists for each of "local", "nvidia" and "nebius"
    And each file has 3 tasks, run.repeat 3 and 36 steps
  @positive
  Scenario: AJ-114 the local file carries warm-up, baseline and runtime evidence
    Given the campaign completed
    When I read the latest local run file
    Then baseline.seconds is at least 30
    And environment.modelLoadSeconds and environment.gpuOffloadPercent are numbers
    And environment.powerSource is one of "ac", "battery" and "unknown"
  @positive
  Scenario: AJ-114 hosted steps carry no energy values
    Given the campaign completed
    When I read the latest nvidia and nebius run files
    Then every step has grossJoules and netJoules with label "N/A" and a null value
  @positive
  Scenario: AJ-114 the committed files validate
    Given the three run files are in results/
    When I run "validate results/"
    Then the exit code is 0
    And the output contains "Validated" followed by the number of run files
  @negative
  Scenario: AJ-114 a missing nebius key stops the campaign before any call
    Given NEBIUS_API_KEY is not set
    When I run "run --tier all --repeat 3"
    Then the exit code is 3
    And no run file was written for any tier
  @negative
  Scenario: AJ-114 rate-limit exhaustion on a hosted tier leaves a failed file that is not committed
    Given the nvidia tier answers HTTP 429 until the five attempts are exhausted
    When the run stops
    Then the exit code is 5 and the nvidia file has run.status "failed"
    And the failed file is excluded from the commit and logged in docs/build-log.md
  @negative
  Scenario: AJ-114 a failed tier is re-run alone
    Given the local and nebius files are completed and the nvidia file failed
    When I run "run --tier nvidia --repeat 3"
    Then a completed nvidia run file exists and the local file is unchanged
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-114-T1 | Write the failing feature and the gated campaign checks | tests/AgentJoules.Specs/Features/Cli/LaptopCampaign.feature, tests/AgentJoules.IntegrationTests/LaptopCampaignTests.cs | 0.75 | AJ-113-T6 |
| AJ-114-T2 | USER ACTION: leave the laptop on mains power and idle (no other GPU use) while the run executes | none (user machine) | 0.1 | - |
| AJ-114-T3 | Run the probe and the all-tier dry-run and check the start state | results/probe-<yyyyMMddTHHmmssZ>.json, docs/build-log.md | 0.5 | AJ-114-T1, AJ-114-T2 |
| AJ-114-T4 | Launch `AJ run --tier all --repeat 3` detached and confirm the log is growing | results/<yyyyMMddTHHmmssZ>-local.json, results/<yyyyMMddTHHmmssZ>-nvidia.json, results/<yyyyMMddTHHmmssZ>-nebius.json | 2.0 | AJ-114-T3 |
| AJ-114-T5 | Check exit marker, status and step counts of the three files with jq | docs/build-log.md | 0.5 | AJ-114-T4 |
| AJ-114-T6 | Re-run a failed tier alone and log the incident (contingency) | docs/build-log.md | 1.0 | AJ-114-T5 |
| AJ-114-T7 | Run `validate results/` and compare n with the file count | docs/build-log.md | 0.25 | AJ-114-T5 |
| AJ-114-T8 | Run the secret scan and commit the results with the `AJ-114 REQ-021:` prefix | results/ | 0.25 | AJ-114-T7 |
| AJ-114-T9 | Run the optional Qwen3 8B baseline run | results/<yyyyMMddTHHmmssZ>-local-baseline.json | 0.75 | AJ-114-T8 |
Test mapping: tests/AgentJoules.Specs/Features/Cli/LaptopCampaign.feature; SchemaValidationTests (REQ-023 list) for the file shapes; gated class LaptopCampaignTests (design 6.3).
Docs impact: docs/build-log.md (commands, exit codes, `Validated <n> files`, incidents), results/ (three run files and the probe).
Definition of done:
- [ ] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [ ] `AJ validate results/` exit code 0 and the `Validated <n> files` line are recorded in docs/build-log.md
Demo relevance: yes: the report, README block and posts quote exactly these files.

### AJ-115: Gate the committed local evidence and the README runtime statements
Covers: REQ-020, REQ-021, EDGE-010
User story: As a reviewer, I want a test suite that checks the committed laptop evidence and the README statements against each other, so that a missing runtime disclosure, a short baseline or a missing NIM link fails the build.
Acceptance criteria:
1. Tests in `CommittedEvidenceTests` read the latest `local` run file (via `RunFileSelector`) and require: `environment.localRuntime` in {nim, ollama}; the model tag present in the `## local` section of `docs/endpoints.md`; a numeric `gpuOffloadPercent`, with `partial-offload` in `run.flags` when it is below 100; `baseline.seconds` of at least 30; `run.repeat` of at least 3; a numeric `environment.modelLoadSeconds`; `environment.powerSource` in {ac, battery, unknown}.
2. They require a committed `results/calibration-*.json` whose three fields are label objects.
3. `README.md` contains the sentence `Local numbers were produced with <runtime> running <tag>.` with the runtime and tag equal to the run file values (the sentence sits outside the generated results block).
4. EDGE-010: when the `Decision` section of `docs/nim-local-attempt.md` records `Local runtime: ollama`, `README.md` links `docs/nim-local-attempt.md` and the latest local run file has `environment.localRuntime` equal to `ollama`.
5. `dotnet test --filter "Spec=REQ-020"`, `--filter "Spec=REQ-021"` and `--filter "Spec=EDGE-010"` each show at least one passed and no failed test; the per-ID results are recorded in `docs/build-log.md`.
BDD scenarios:
```gherkin
@unit @AJ-S115 @REQ-020 @REQ-021 @EDGE-010
Feature: Gate the committed local evidence and the README runtime statements
  @positive
  Scenario: AJ-115 consistent evidence passes
    Given a workspace with a local run file, endpoints, a calibration file and a README that agree
    When the evidence tests run
    Then all checks pass
  @positive
  Scenario: AJ-115 the README sentence matches the run file
    Given the run file has localRuntime "ollama" and the tag "nemotron-3-nano:30b"
    And README.md contains "Local numbers were produced with ollama running nemotron-3-nano:30b."
    When the evidence tests run
    Then the README check passes
  @negative
  Scenario: AJ-115 partial offload without the flag fails
    Given gpuOffloadPercent is 62 and run.flags is empty
    When the evidence tests run
    Then the check fails and names "partial-offload"
  @negative
  Scenario: AJ-115 a baseline shorter than 30 seconds fails
    Given baseline.seconds is 20
    When the evidence tests run
    Then the check fails and names baseline.seconds
  @negative
  Scenario: AJ-115 fewer than three repeats fails
    Given run.repeat is 2
    When the evidence tests run
    Then the check fails and names run.repeat
  @negative
  Scenario: AJ-115 the README names a different tag than the run file
    Given the README sentence names a tag that differs from the run file
    When the evidence tests run
    Then the README check fails and names both tags
  @negative
  Scenario: AJ-115 a fallback decision without the README link fails
    Given the Decision says "Local runtime: ollama" and README.md has no link to the attempt document
    When the evidence tests run
    Then the EDGE-010 check fails
  @negative
  Scenario: AJ-115 a fallback decision with a run file that says nim fails
    Given the Decision says "Local runtime: ollama" and the latest local run file says "nim"
    When the evidence tests run
    Then the EDGE-010 check fails
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-115-T1 | Write the failing feature and the evidence tests | tests/AgentJoules.Specs/Features/Reporting/CommittedEvidence.feature, tests/AgentJoules.UnitTests/CommittedEvidenceTests.cs | 0.75 | AJ-114-T8 |
| AJ-115-T2 | Implement the evidence reader | tests/AgentJoules.TestSupport/Evidence/CommittedEvidenceReader.cs | 0.75 | AJ-115-T1 |
| AJ-115-T3 | Implement the field, README and decision rules | tests/AgentJoules.UnitTests/CommittedEvidenceTests.cs | 0.75 | AJ-115-T2 |
| AJ-115-T4 | Bind the feature steps | tests/AgentJoules.Specs/Steps/ReportingSteps.cs | 0.25 | AJ-115-T3 |
| AJ-115-T5 | Add the runtime sentence to the README Local runtime section | README.md | 0.25 | AJ-115-T4 |
| AJ-115-T6 | Run the three per-ID filters and record passed, failed and skipped counts | docs/build-log.md | 0.25 | AJ-115-T5 |
Test mapping: tests/AgentJoules.Specs/Features/Reporting/CommittedEvidence.feature; unit class CommittedEvidenceTests with `[Trait("Spec","REQ-020")]`, `[Trait("Spec","REQ-021")]` and `[Trait("Spec","EDGE-010")]`.
Docs impact: README.md (sentence `Local numbers were produced with <runtime> running <tag>.`), docs/build-log.md.
Definition of done:
- [ ] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [ ] M5 may start only after this story is committed (local Definition of Done items)
Demo relevance: no.

## Risks

| Risk | Trigger | Mitigation | Owner |
|---|---|---|---|
| NIM attempt overruns | 90 minutes pass without a models response | hard stop, remove the container, record, switch to Ollama (AJ-103) | builder |
| 30b model does not fit the 16 GB GPU | `ollama ps` shows less than 100 percent GPU | disclose `partial-offload`, optionally use a smaller verified Nemotron tag (AJ-107), models-differ disclosure | builder |
| Ollama omits streaming usage | final chunk has no usage (O-04) | tokens `not-reported` or buffered local calls with TTFT `N/A`, decision logged (AJ-108) | builder |
| Reasoning cannot be switched off on a tier | toggle ignored or rejected (O-02, R-05) | `ReasoningMode: unsupported`, shown in header and README | builder |
| 429 on build.nvidia.com with 36 judge calls | retries exhausted (R-04) | retry client; judge failures never change the exit code; re-run the failed tier alone (AJ-114) | builder |
| Counter and sampling disagree | large `relativeDifference` (R-02) | publish the value with the explanation in the methodology | builder |
| Nebius lists no Nemotron model or the 45-minute box expires | AJ-101 scenarios | report the build as blocked on that item with the exact missing key, URL or model; never skip silently | builder, user |
| Estimate exceeds the window capacity | task sum of 55.45 hours against about 18 hours of capacity | user-action tasks run in parallel; wall-clock tasks overlap other work; AJ-113 and the optional Qwen run (AJ-114-T9) can move to M7 without affecting any exit criterion | builder |
| Laptop state drifts during the run | battery power, background GPU load | preflight (AJ-110) and `environment.powerSource` recorded; battery runs are not committed | user |

## Estimate

Note (2026-10-18): any deferral or descope candidates named in this section are informational; the drop order in docs/plan/plan.md section 3.4 is the single source and classifies stories as MUST, SHOULD or COULD.

Sum of task estimates, computed from the task tables: 55.45 h including the four USER ACTION tasks (54.70 h builder-only; user actions total 0.75 h).

| Story | Tasks | Hours |
|---|---|---|
| AJ-100 | 6 | 3.75 |
| AJ-101 | 6 | 3.30 |
| AJ-102 | 5 | 2.25 |
| AJ-103 | 7 | 3.75 |
| AJ-104 | 5 | 2.50 |
| AJ-105 | 7 | 3.75 |
| AJ-106 | 8 | 5.00 |
| AJ-107 | 7 | 4.00 |
| AJ-108 | 7 | 4.50 |
| AJ-109 | 7 | 4.25 |
| AJ-110 | 6 | 1.45 |
| AJ-111 | 4 | 1.35 |
| AJ-112 | 6 | 4.25 |
| AJ-113 | 6 | 2.25 |
| AJ-114 | 9 | 6.10 |
| AJ-115 | 6 | 3.00 |
| Total | 102 | 55.45 |

Capacity check: three days at about six hours is 18 hours, so the plan exceeds the window by roughly 37 hours of task effort. Part of the sum is wall-clock rather than hands-on work (AJ-103-T3 box 1.5 h, AJ-107-T2 model download 1.0 h, AJ-114-T4 detached campaign run 2.0 h) and the unit-test stories are small, but the gap is real and is carried as a risk. If M4 runs late, the deferral order is: AJ-114-T9 (optional Qwen run), AJ-113, then the local-tier extras of AJ-108; no exit criterion depends on the first two.
