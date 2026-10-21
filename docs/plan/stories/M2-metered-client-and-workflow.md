# M2: Metered client and workflow

Goal: wrap every agent's chat client in a metering decorator that produces exactly one attributed reading per call (buffered and streaming), survives HTTP 429 without distorting latency, labels missing usage as not-reported, and publishes readings to a sink. On top of that, build the four-agent Planner -> [Researcher, Writer] -> Reviewer workflow with fan-out and fan-in, bind the four tiers by configuration only, support serialized and concurrent execution, expose OpenTelemetry instruments, and deliver the `run` command with dry-run, documented exit codes and checkpointed run files. Everything in this milestone is proven with `FakeChatClient`, `ScriptedNvmlApi`, `FakeHttpMessageHandler` and the in-process `CliHarness`: no GPU, network or key is needed.

## Dates and exit criteria

Dates: 2026-10-21 to 2026-10-23 (three working days, about 18 hours at the assumed six hours per day; see Estimate for the gap).

Exit criteria:
- [ ] `dotnet build -warnaserror -tl:off --no-incremental` exits 0 with 0 warnings and 0 errors.
- [ ] For each ID in REQ-007, REQ-008, REQ-011, REQ-012, REQ-013, REQ-014, REQ-017, EDGE-004, EDGE-006, EDGE-007, EDGE-008, EDGE-011, the command `dotnet test --filter "Spec=<ID>" --logger "console;verbosity=normal"` shows at least one passed test and no failed test; the twelve results are recorded in `docs/build-log.md`.
- [ ] `grep -rnE "AddFanOutEdge|BuildConcurrent" src`, `grep -rnE "AddFanInBarrierEdge|BuildConcurrent" src` and `grep -rn "RunStreamingAsync" src` each return at least one hit.
- [ ] `grep -rn "UseOpenTelemetry" src` returns at least one hit; `grep -rnE "AsyncLocal" src` returns no hit.
- [ ] `grep -rnE "\"https?://" src --include=*.cs | grep -vE "www\.w3\.org/2000/svg|json-schema\.org"` returns no hit.
- [ ] `ls tasks/*.json | wc -l` prints 3 and each file parses with `name`, `prompt` and `maxOutputTokens`.
- [ ] `dotnet run --project src/AgentJoules.Cli -- run --help` lists `--tier`, `--task`, `--mode`, `--out`, `--baseline-seconds`, `--repeat` and `--dry-run`; `dotnet run --project src/AgentJoules.Cli -- run --tier bogus; echo $?` prints 2.
- [ ] Live check, needs user steps 5 and 6 (keys): `AJ run --tier nvidia --dry-run` and `AJ run --tier nebius --dry-run` exit 0 and the nebius model ID contains "nemotron" (case-insensitive). If the keys are not yet present at the end of 2026-10-23 the item is recorded BLOCKED in `docs/build-log.md` and carried to M4; it does not block the offline criteria above. The `local` dry-run waits for the local runtime delivered in M4.

## Coverage

REQ: REQ-007, REQ-008, REQ-011, REQ-012, REQ-013, REQ-014, REQ-017.
EDGE: EDGE-004, EDGE-006, EDGE-007, EDGE-008, EDGE-011.

## Dependencies and user actions

Needs from earlier milestones (assumed delivered; a story that finds one missing creates the minimum and says so in `docs/build-log.md`):
- M0: solution, `Directory.Build.props`, `Directory.Packages.props` pins, test projects, `AgentJoules.TestSupport`, secret redaction (`SecretRedactor`).
- M1: `Metric` label object, `IEnergyMeter`, `DeterministicEnergyMeter`, `ReadingCalculator`, `IGpuTelemetrySourceFactory`, `ScriptedNvmlApi`, `CliApplication` with `probe`.

Cut-order note: AJ-047 (concurrent half of the metering modes) depends on AJ-056-T4a, which creates `RunAssembler`; no other story depends on AJ-047.

Hands over to later milestones: `ICostCalculator` seam with a placeholder implementation (M3 replaces it), an `IJudgeService` seam with a no-op implementation (M3 fills it), `ILocalRuntime` warm-up seam with a no-op implementation (M4 fills it), the minimal `RunDocument` records and writer (M3 hardens them and adds the schema).

User actions from prompt section 12: steps 5 (`NEBIUS_API_KEY`) and 6 (`NVIDIA_API_KEY`) are needed only for the live dry-run criterion above. Step 4 (credits) should be done before step 5. Step 7 (`NGC_API_KEY`) is not needed in this milestone. No test in this milestone needs a key; tests build fake keys at run time.

Note (2026-10-20, M2 session): feature files follow the repository convention from M1, tests/AgentJoules.Specs/Features/<Capability>/AJ-0nn-<slug>.feature; the file names in the story tables (for example ReadingAndSink.feature) map to that form.

## Stories

### AJ-040: Define the per-call reading and publish it to a metrics sink
Covers: REQ-008
User story: As a workflow developer, I want every metered call to publish one structured reading to an `IMetricsSink`, so that the results writer and the exporters consume the same record.
Acceptance criteria:
1. `StepReading` holds agent name, model ID, tier, start and end UTC, `latencyMs`, `ttftMs`, `inputTokens`, `outputTokens`, the energy fields, `cost`, `retries`, `waitMs`, `outcome` and `flags`; the test `Reading_carries_every_required_field` in `tests/AgentJoules.UnitTests/MeteredChatClientTests.cs` (trait `Spec=REQ-008`) passes.
2. A reading built for tier `nvidia` or `nebius` with `UnmeteredEnergyMeter` has `grossJoules`, `netJoules`, `avgW` and `peakW` equal to `{value:null,label:"N/A",reason:"hosted-tier"}`; the test `Hosted_tier_reading_has_energy_NA` passes.
3. `InMemoryMetricsSink.Snapshot()` returns readings in publish order; 100 parallel `Publish` calls yield exactly 100 readings (test `Sink_is_thread_safe`).
4. The workflow sink and the judge sink are two instances; a reading published to the judge sink is absent from the workflow sink snapshot (test `Judge_sink_is_separate`).
5. A metric labelled `N/A` or `not-reported` cannot carry a numeric value, so a missing value can never be written as 0 (test `Missing_value_is_never_zero`).
BDD scenarios:
```gherkin
@unit @AJ-S040 @REQ-008
Feature: AJ-040 Define the per-call reading and publish it to a metrics sink
  @positive
  Scenario: AJ-040 sink returns readings in publish order
    Given an in-memory metrics sink
    When readings for agents "Planner", "Researcher" and "Writer" are published in that order
    Then the snapshot lists "Planner", "Researcher" and "Writer" in that order
  @positive
  Scenario: AJ-040 hosted tier reading labels energy as N/A
    Given a reading for tier "nvidia" built with the unmetered energy meter
    When the reading is published
    Then grossJoules, netJoules, avgW and peakW each have label "N/A", reason "hosted-tier" and a null value
  @negative
  Scenario: AJ-040 numeric value under an N/A label is rejected
    Given a metric factory
    When a metric with label "N/A" and value 0 is requested
    Then construction fails and no metric is created
  @negative
  Scenario: AJ-040 judge reading never reaches the workflow sink
    Given a workflow sink and a separate judge sink
    When a reading for agent "Judge" is published to the judge sink
    Then the workflow sink snapshot is empty
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-040-T1 | Write the failing BDD feature and unit tests for reading content and sink | tests/AgentJoules.Specs/Features/Metering/ReadingAndSink.feature, tests/AgentJoules.UnitTests/MeteredChatClientTests.cs | 0.5 | - |
| AJ-040-T2 | Create the `StepIdentity` and `StepReading` records | src/AgentJoules.Core/Runs/StepIdentity.cs, src/AgentJoules.Core/Results/StepReading.cs | 1.0 | AJ-040-T1 |
| AJ-040-T3 | Create `IMetricsSink` and `InMemoryMetricsSink` | src/AgentJoules.Metering/IMetricsSink.cs, src/AgentJoules.Metering/InMemoryMetricsSink.cs | 0.5 | AJ-040-T2 |
| AJ-040-T4 | Create `UnmeteredEnergyMeter` for hosted tiers | src/AgentJoules.Core/Energy/UnmeteredEnergyMeter.cs | 0.5 | AJ-040-T2 |
| AJ-040-T5 | Record the `Spec=REQ-008` test result in the build log | docs/build-log.md | 0.25 | AJ-040-T3, AJ-040-T4 |
Test mapping: `MeteredChatClientTests` (required test class); feature file `tests/AgentJoules.Specs/Features/Metering/ReadingAndSink.feature`.
Docs impact: `docs/build-log.md` (test result line); README telemetry and results sections are written in M6.
Definition of done:
- [x] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings (2026-10-20: build 0 warnings exit 0 and 1,769/0/5)
- [x] docs listed above updated (2026-10-20: docs updated in this entry)
- [x] `dotnet test --filter "Spec=REQ-008"` shows at least one passed and no failed test (2026-10-20: `dotnet test --filter "Spec=REQ-008"` 122 passed, 0 failed)
Demo relevance: no (internal contract; every console step line and results row in the demo comes from this record).

### AJ-041: Meter buffered and streaming calls with exactly one reading per call
Covers: REQ-007
User story: As a workflow developer, I want a decorator that wraps any `IChatClient` and meters both `GetResponseAsync` and `GetStreamingResponseAsync` for one named agent, so that every agent step has its own attributed latency, time to first token and energy window.
Acceptance criteria:
1. A buffered call through `MeteredChatClient` publishes exactly one reading with `ttftMs = {value:null,label:"N/A",reason:"buffered-call"}` (test `Buffered_call_yields_one_reading`).
2. A streaming call publishes exactly one reading whose `ttftMs` is within 20 ms of the fake's first-update delay (test `Streaming_ttft_matches_first_update_delay`, fake time, exact by construction).
3. A cancelled stream yields exactly one reading with `outcome: "cancelled"`; a stream whose enumerator throws yields exactly one reading with `outcome: "error"` and the exception is rethrown; abandoning the enumerator early yields one `cancelled` reading.
4. A test builds the decorator with `new ChatClientBuilder(inner).Use(...)` and passes (test `Builds_via_ChatClientBuilder_Use`).
5. The agent name is a constructor argument and is copied to the reading; `grep -rnE "AsyncLocal" src` returns no hit (attribution by construction).
6. A second call started while one is in flight on the same instance throws `InvalidOperationException`.
BDD scenarios:
```gherkin
@unit @AJ-S041 @REQ-007
Feature: AJ-041 Meter buffered and streaming calls with exactly one reading per call
  @positive
  Scenario: AJ-041 buffered call yields one reading with ttft N/A
    Given a metered client for agent "Planner" over a fake client that answers in one response
    When the client is called with GetResponseAsync
    Then the sink holds exactly 1 reading for agent "Planner"
    And the reading has ttftMs label "N/A"
  @positive
  Scenario: AJ-041 streaming call records time to first content update
    Given a metered client for agent "Writer" over a fake stream whose first update arrives after 300 ms
    When the stream is consumed to completion
    Then the sink holds exactly 1 reading with outcome "ok"
    And ttftMs is within 20 ms of 300
  @positive
  Scenario: AJ-041 decorator composes through ChatClientBuilder
    Given a fake inner client
    When the decorator is added with ChatClientBuilder.Use and built
    Then calling the built client publishes 1 reading
  @negative
  Scenario: AJ-041 cancelled stream yields one cancelled reading
    Given a metered client over a fake stream with 5 updates
    When the caller cancels after the second update
    Then the sink holds exactly 1 reading with outcome "cancelled"
  @negative
  Scenario: AJ-041 failing stream yields one error reading and rethrows
    Given a metered client over a fake stream that throws after the first update
    When the stream is consumed
    Then the exception reaches the caller
    And the sink holds exactly 1 reading with outcome "error"
  @negative
  Scenario: AJ-041 overlapping call on one instance is rejected
    Given a metered client with a call in flight
    When a second call starts on the same instance
    Then InvalidOperationException is thrown
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-041-T1 | Write the failing BDD feature and unit tests for buffered and streaming metering | tests/AgentJoules.Specs/Features/Metering/MeteredCalls.feature, tests/AgentJoules.UnitTests/MeteredChatClientTests.cs | 0.5 | - |
| AJ-041-T2 | Create `FakeChatClient` (scripted chunks, delays on `FakeTimeProvider`, scripted exceptions) and `FakeTimeDriver` | tests/AgentJoules.TestSupport/FakeChatClient.cs, tests/AgentJoules.TestSupport/FakeTimeDriver.cs | 1.0 | AJ-041-T1 |
| AJ-041-T3 | Implement the buffered path of `MeteredChatClient` with window, reading and sink publish | src/AgentJoules.Metering/MeteredChatClient.cs | 1.0 | AJ-041-T2, AJ-040-T3 |
| AJ-041-T4 | Implement the streaming path with TTFT and the `ok`, `cancelled`, `error` outcome in `finally` | src/AgentJoules.Metering/MeteredChatClient.cs, src/AgentJoules.Metering/OutcomeClassifier.cs | 1.5 | AJ-041-T3 |
| AJ-041-T5 | Add the one-in-flight guard and the `ChatClientBuilder` extension | src/AgentJoules.Metering/MeteredChatClient.cs, src/AgentJoules.Metering/ChatClientBuilderMeteringExtensions.cs | 0.5 | AJ-041-T4 |
| AJ-041-T6 | Record the `Spec=REQ-007` test result in the build log | docs/build-log.md | 0.25 | AJ-041-T5 |
Test mapping: `MeteredChatClientTests` (required test class); feature file `tests/AgentJoules.Specs/Features/Metering/MeteredCalls.feature`.
Docs impact: `docs/build-log.md` (test result line); `docs/methodology.md` input note on attribution by construction (written in M4).
Definition of done:
- [x] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings (2026-10-20: build 0 warnings exit 0 and 1,821/0/5)
- [x] docs listed above updated (2026-10-20: docs updated in this entry)
- [x] `dotnet test --filter "Spec=REQ-007"` shows at least one passed and no failed test (2026-10-20: Spec=REQ-007 46 passed, 0 failed)
- [x] `grep -rnE "AsyncLocal" src` returns no hit (2026-10-20: `grep -rnIE "AsyncLocal" src --include=*.cs` 0 hits; the literal command matches third-party DLLs under bin/)
Demo relevance: yes: each streamed step line in the demo (agent, joules, watts, tokens, latency) is one reading from this decorator.

### AJ-042: Report absent usage as not-reported, never 0
Covers: REQ-008, EDGE-007
User story: As a reader of the results, I want tokens and cost shown as not-reported or N/A when a provider returns no usage, so that no missing number is ever presented as 0.
Acceptance criteria:
1. A fake client returning `UsageDetails` with 120 input and 45 output tokens yields `inputTokens {value:120,label:"measured"}` and `outputTokens {value:45,label:"measured"}` (test `Usage_is_recorded_as_measured`).
2. A fake client without usage yields both token metrics as `{value:null,label:"not-reported",reason:"provider-omitted-usage"}` on the buffered path and on the streaming path (tests `No_usage_buffered_is_not_reported`, `No_usage_streaming_is_not_reported`).
3. When tokens are not reported the reading's `cost` has label `N/A` and a null value (never 0).
4. A provider-reported 0 tokens is kept as `measured` 0 (the label, not the number, decides).
5. `ICostCalculator` is the only way the decorator obtains cost; M2 ships `NotConfiguredCostCalculator` (always `N/A`, reason `cost-not-configured`), which M3 replaces.
6. A step whose `outputTokens` exceeds the task's `maxOutputTokens` carries the flag `max-tokens-exceeded` (test `Max_tokens_exceeded_flag`); when output tokens are not reported the flag is absent.
BDD scenarios:
```gherkin
@unit @AJ-S042 @REQ-008 @EDGE-007
Feature: AJ-042 Report absent usage as not-reported, never 0
  @positive
  Scenario: AJ-042 usage with 120 input and 45 output tokens is measured
    Given a fake client that returns usage with 120 input and 45 output tokens
    When a buffered call is metered
    Then inputTokens is 120 with label "measured"
    And outputTokens is 45 with label "measured"
  @positive
  Scenario: AJ-042 provider-reported zero tokens stays measured
    Given a fake client that returns usage with 0 input and 0 output tokens
    When a buffered call is metered
    Then inputTokens is 0 with label "measured"
  @negative
  Scenario: AJ-042 buffered call without usage reports not-reported
    Given a fake client that returns no usage
    When a buffered call is metered
    Then inputTokens and outputTokens have label "not-reported" and a null value
    And cost has label "N/A" and a null value
  @negative
  Scenario: AJ-042 streaming call without usage reports not-reported
    Given a fake stream whose updates carry no usage content
    When the stream is consumed
    Then inputTokens and outputTokens have label "not-reported" and a null value
  @positive
  Scenario: AJ-042 output above the task limit gets the max-tokens-exceeded flag
    Given a task with maxOutputTokens 512 and a response reporting 600 output tokens
    When a buffered call is metered
    Then the reading has flag "max-tokens-exceeded"
  @negative
  Scenario: AJ-042 unreported output tokens carry no max-tokens flag
    Given a task with maxOutputTokens 512 and a response without usage
    When a buffered call is metered
    Then the reading has no flag "max-tokens-exceeded"
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-042-T1 | Write the failing BDD feature and unit tests for usage mapping | tests/AgentJoules.Specs/Features/Metering/UsageReporting.feature, tests/AgentJoules.UnitTests/MeteredChatClientTests.cs | 0.5 | AJ-041-T2 |
| AJ-042-T2 | Map `UsageDetails` to measured or not-reported token metrics on the buffered path and set `max-tokens-exceeded` | src/AgentJoules.Metering/MeteredChatClient.cs, src/AgentJoules.Core/Cost/TokenCounts.cs | 1.0 | AJ-042-T1, AJ-041-T3 |
| AJ-042-T3 | Collect `UsageContent` from streaming updates | src/AgentJoules.Metering/MeteredChatClient.cs | 0.75 | AJ-042-T2, AJ-041-T4 |
| AJ-042-T4 | Create `ICostCalculator` and `NotConfiguredCostCalculator` and call them from the decorator | src/AgentJoules.Core/Cost/ICostCalculator.cs, src/AgentJoules.Core/Cost/NotConfiguredCostCalculator.cs | 0.5 | AJ-042-T2 |
| AJ-042-T5 | Record the `Spec=REQ-008` and `Spec=EDGE-007` results and the open include_usage check in the build log | docs/build-log.md | 0.25 | AJ-042-T3, AJ-042-T4 |
Test mapping: `MeteredChatClientTests` (required test class); feature file `tests/AgentJoules.Specs/Features/Metering/UsageReporting.feature`.
Docs impact: `docs/build-log.md`. Whether Ollama honours `stream_options.include_usage` is unverified (design O-04); M4 verifies it and records it in `docs/endpoints.md`.
Definition of done:
- [x] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings (2026-10-20: build 0 warnings exit 0 and 1,865/0/5)
- [x] docs listed above updated (2026-10-20: docs updated in this entry)
- [x] `dotnet test --filter "Spec=EDGE-007"` shows at least one passed and no failed test (2026-10-20: Spec=EDGE-007 6 passed, 0 failed)
- [x] the report-cell half of the no-usage acceptance (token cells show `not-reported`, cost cell shows `N/A`) is carried by the Markdown rendering story in M3 with the no-usage fixture this story creates in `RunDocumentFixtures` (2026-10-20: the fixture is `StepReadingFixtures.NoUsage()`; RunDocument does not exist before AJ-055; `RunDocumentFixtures` is created by AJ-078-T5 (M3) and AJ-083-T2 adds the no-usage row; the report-cell assertion stays with M3)
Demo relevance: yes: the honesty rule "a value that does not exist is N/A or not-reported, never 0" is visible in the console step line (`in=not-reported`).

### AJ-043: Bind and validate tier configuration
Covers: REQ-012
User story: As a developer, I want one `config/appsettings.json` section per tier with base URL, model ID, optional per-agent model IDs and the key variable name, so that the same workflow runs on `local`, `cloudgpu`, `nvidia` and `nebius` by configuration only.
Acceptance criteria:
1. `config/appsettings.json` has root `AgentJoules` and sections `Tiers:local`, `Tiers:cloudgpu`, `Tiers:nvidia`, `Tiers:nebius`, each binding `BaseUrl`, `ModelId`, `AgentModelIds`, `ApiKeyVariable`, `Enabled`, `ReasoningMode`, `Precision`; `Tiers:cloudgpu:Enabled` is false and the other three are true in the committed file (test `Committed_config_binds`).
2. Every key is overridable by `AGENTJOULES__<Section>__<Key>`; a test sets `AGENTJOULES__Tiers__nvidia__ModelId` through the in-memory configuration override and the bound value changes.
3. Invalid configuration exits 2 with a message naming the key: an enabled tier without `BaseUrl`, `ReasoningMode` outside `off|on|unsupported`, `Judge:Tier` naming an unknown tier, an unknown tier section name.
4. `grep -rnE "\"https?://" src --include=*.cs | grep -vE "www\.w3\.org/2000/svg|json-schema\.org"` returns no hit (endpoint URLs live in configuration only).
5. The committed configuration holds only variable names (for example `NVIDIA_API_KEY`), never a key value.
BDD scenarios:
```gherkin
@unit @AJ-S043 @REQ-012
Feature: AJ-043 Bind and validate tier configuration
  @positive
  Scenario: AJ-043 four tier sections bind from the committed file
    Given the committed config/appsettings.json
    When the options are bound and validated
    Then tiers "local", "cloudgpu", "nvidia" and "nebius" each have a BaseUrl and a ModelId
    And tier "cloudgpu" is disabled
  @positive
  Scenario: AJ-043 environment variable overrides the file
    Given the committed config with nvidia ModelId "model-a"
    When AGENTJOULES__Tiers__nvidia__ModelId is set to "model-b" in the configuration override
    Then the bound nvidia ModelId is "model-b"
  @negative
  Scenario: AJ-043 invalid reasoning mode exits 2
    Given the config with Tiers:nvidia:ReasoningMode set to "maybe"
    When any command starts
    Then the exit code is 2
    And the output names "Tiers:nvidia:ReasoningMode"
  @negative
  Scenario: AJ-043 enabled tier without base URL exits 2
    Given the config with Tiers:nebius:BaseUrl removed
    When any command starts
    Then the exit code is 2
    And the output names "Tiers:nebius:BaseUrl"
  @negative
  Scenario: AJ-043 judge tier naming an unknown tier exits 2
    Given the config with Judge:Tier set to "bogus"
    When any command starts
    Then the exit code is 2
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-043-T1 | Write the failing BDD feature and unit tests for tier binding and validation | tests/AgentJoules.Specs/Features/Configuration/TierConfiguration.feature, tests/AgentJoules.UnitTests/TierConfigurationTests.cs | 0.5 | - |
| AJ-043-T2 | Create the `TierOptions`, `JudgeOptions` and `WorkflowOptions` records and extend `AgentJoulesOptions` | src/AgentJoules.Core/Configuration/TierOptions.cs, src/AgentJoules.Core/Configuration/AgentJoulesOptions.cs | 1.0 | AJ-043-T1 |
| AJ-043-T3 | Create `FlatSectionsValidator : IValidateOptions<AgentJoulesOptions>` and the cross-field rules | src/AgentJoules.Core/Configuration/FlatSectionsValidator.cs, src/AgentJoules.Core/Configuration/CrossFieldValidator.cs | 1.0 | AJ-043-T2 |
| AJ-043-T4 | Write the tier sections of the committed configuration (nebius base URL from prompt section 12 step 5; other values from the builder lookup, recorded as unverified in the build log until M4 verifies them) | config/appsettings.json | 0.75 | AJ-043-T2 |
| AJ-043-T5 | Map `OptionsValidationException` to exit 2 in the host start path | src/AgentJoules.Cli/Hosting/AgentJoulesHostFactory.cs | 0.5 | AJ-043-T3 |
| AJ-043-T6 | Document the configuration keys and the override rule | docs/build-log.md | 0.25 | AJ-043-T4 |
Test mapping: feature file `tests/AgentJoules.Specs/Features/Configuration/TierConfiguration.feature`; class `TierConfigurationTests` (trait `Spec=REQ-012`).
Docs impact: `config/appsettings.json`; the README configuration table is written in M6 from these keys.
Definition of done:
- [x] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings (2026-10-20: build exit 0 with 0 warnings; battery 2,000 passed, 0 failed, 5 skipped)
- [x] docs listed above updated (2026-10-20: `config/appsettings.json` and the configuration keys paragraph of the AJ-043 entry in docs/build-log.md)
- [x] the URL grep in acceptance criterion 4 returns no hit (2026-10-20: the grep printed nothing, exit 1)
Demo relevance: no (the demo shows the effect: the same command runs on every tier).
Conflict: C-05 (design 9.2) - the global config-path option and `validate --config` share a name; resolution: `--config <path>` is defined per command (here on `run`, `probe`, `report`, `calibrate`), and its zero-or-one arity form belongs to `validate` (delivered in M3 and M4).

### AJ-044: Create provider chat clients per tier from configuration only
Covers: REQ-012
User story: As a developer, I want a provider factory that builds an OpenAI-compatible chat client from the tier configuration with temperature 0, the task's token limit and the SDK retry disabled, so that every tier is called identically and the only differences are configuration values.
Acceptance criteria:
1. `ProviderClientFactory.Create(tier, modelId)` targets the tier's configured `BaseUrl`; a test with four tiers and four fake base URLs asserts each request host matches its own tier (test `Each_tier_uses_its_own_base_url`).
2. The OpenAI SDK retry is disabled: a fake handler that always answers 500 records exactly 1 HTTP request (test `Sdk_retry_is_disabled`).
3. Every chat request body has `temperature` 0 and the task's `maxOutputTokens`; the test reads the recorded request body (the builder verifies the exact JSON property name of the OpenAI SDK and records it in the build log).
4. A seed is sent only when `Tiers:<t>:SupportsSeed` is true; otherwise the body has no seed.
5. `Tiers:<t>:ExtraBodyJson` is deep-merged into the body of `POST .../chat/completions` only; `GET .../models` is unchanged; unparseable JSON exits 2 at start.
6. A per-agent model ID in `AgentModelIds` overrides the tier `ModelId`; an agent without an entry uses `ModelId`.
7. `environment.reasoningMode` is taken from `Tiers:<t>:ReasoningMode` (`off`, `on` or `unsupported`).
8. The token-limit JSON property name comes from `Tiers:<t>:MaxTokensField` (`max_completion_tokens` or `max_tokens`); the test asserts the configured name appears in the recorded request body, and the name each endpoint honours is verified and recorded per tier in `docs/endpoints.md` in M4.
9. The named HTTP client has `Timeout` equal to `Timeout.InfiniteTimeSpan` while the SDK `NetworkTimeout` equals `Tiers:<t>:TimeoutSeconds` (test `Http_timeout_infinite_network_timeout_set`).
BDD scenarios:
```gherkin
@unit @AJ-S044 @REQ-012
Feature: AJ-044 Create provider chat clients per tier from configuration only
  @positive
  Scenario: AJ-044 request carries temperature 0 and the task token limit
    Given tier "nvidia" configured with a fake base URL and task maxOutputTokens 512
    When the chat client sends one request
    Then the recorded body has temperature 0 and the limit 512
  @positive
  Scenario: AJ-044 per-agent model override wins over the tier default
    Given tier "nebius" with ModelId "model-a" and AgentModelIds Writer "model-b"
    When clients for "Planner" and "Writer" are created
    Then "Planner" uses "model-a" and "Writer" uses "model-b"
  @positive
  Scenario: AJ-044 extra body JSON is merged into the completion request only
    Given tier "nvidia" with ExtraBodyJson that sets one extra field
    When a completion request and a models request are sent
    Then the completion body contains the extra field
    And the models request has no body change
  @negative
  Scenario: AJ-044 SDK retry is disabled so a 500 is attempted once
    Given a fake endpoint that always answers 500
    When one chat request is sent
    Then the handler recorded exactly 1 request
  @negative
  Scenario: AJ-044 seed is omitted when the tier does not support it
    Given tier "nebius" with SupportsSeed false
    When the chat client sends one request
    Then the recorded body has no seed field
  @negative
  Scenario: AJ-044 unparseable extra body JSON exits 2
    Given tier "nvidia" with ExtraBodyJson "{not json"
    When any command starts
    Then the exit code is 2
  @positive
  Scenario: AJ-044 configured max tokens field name is used in the body
    Given tier "nebius" with MaxTokensField "max_completion_tokens" and task maxOutputTokens 384
    When the chat client sends one request
    Then the recorded body has the property "max_completion_tokens" with value 384
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-044-T1 | Write the failing BDD feature and unit tests for provider clients | tests/AgentJoules.Specs/Features/Workflow/ProviderClients.feature, tests/AgentJoules.UnitTests/ProviderClientTests.cs | 0.5 | AJ-043-T2 |
| AJ-044-T2 | Create `FakeHttpMessageHandler` (routes, status codes, request recording) and `SseBuilder` | tests/AgentJoules.TestSupport/FakeHttpMessageHandler.cs, tests/AgentJoules.TestSupport/SseBuilder.cs | 1.5 | AJ-044-T1 |
| AJ-044-T3 | Implement `ProviderClientFactory` with endpoint, bearer key, `ClientRetryPolicy(0)` and timeout | src/AgentJoules.Workflow/Providers/ProviderClientFactory.cs | 1.0 | AJ-044-T2 |
| AJ-044-T4 | Implement `RequestBodyPatchHandler` for `ExtraBodyJson` | src/AgentJoules.Workflow/Providers/RequestBodyPatchHandler.cs | 1.0 | AJ-044-T3 |
| AJ-044-T5 | Add per-agent model resolution and chat options (temperature 0, max tokens field name, optional seed) and the infinite `HttpClient` timeout | src/AgentJoules.Core/Configuration/TierOptions.cs, src/AgentJoules.Workflow/Agents/ChatOptionsFactory.cs | 1.0 | AJ-044-T3 |
| AJ-044-T6 | Register the named HTTP clients and the factory in the service collection | src/AgentJoules.Workflow/ServiceCollectionExtensions.cs | 0.5 | AJ-044-T4 |
| AJ-044-T7 | Record the verified request-body property names in the build log | docs/build-log.md | 0.25 | AJ-044-T5 |
Test mapping: feature file `tests/AgentJoules.Specs/Features/Workflow/ProviderClients.feature`; class `ProviderClientTests` (trait `Spec=REQ-012`).
Docs impact: `docs/build-log.md`; the reasoning-off JSON per endpoint is verified and written to `docs/endpoints.md` in M4 (design default: `{"chat_template_kwargs":{"enable_thinking":false}}`, assumed).
Definition of done:
- [x] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings (2026-10-20: build 0 warnings exit 0 and 2,090/0/5)
- [x] docs listed above updated (2026-10-20: docs updated in this entry, verified member and body names recorded)
- [x] the URL grep from AJ-043 still returns no hit (2026-10-20: the grep printed nothing)
Demo relevance: no.

### AJ-045: Retry HTTP 429 with backoff and keep the wait out of latency
Covers: EDGE-006
User story: As a developer running against a rate-limited hosted endpoint, I want 429 responses retried with exponential backoff, jitter and `Retry-After`, with the waiting time recorded separately, so that a throttled call still produces one honest reading and latency does not include the wait.
Acceptance criteria:
1. A handler answering 429, 429, 200 yields a reading with `retries: 2` and `outcome: "ok"` (test `Two_429s_then_success`).
2. `waitMs` holds the summed delays and `latencyMs` equals elapsed time minus `waitMs`; the test uses `FakeTimeProvider`, a fixed `Random` and fixture delays, and finishes without waiting on the wall clock.
3. A `Retry-After` header (seconds or HTTP date) overrides the computed backoff; the computed backoff is base 1 s, factor 2, full jitter, cap 30 s.
4. A handler answering 429 on every call records exactly 5 HTTP requests (SDK retry disabled in AJ-044), the reading has `outcome: "error"` and `retries: 4`, and `RetriesExhaustedException` is thrown (test `Always_429_exhausts_after_5_attempts`).
5. Only 429 is retried: a 500, a timeout or a connection error is attempted once and ends with `outcome: "error"`.
6. One reading spans all attempts (sink count is 1). For streaming, a 429 raised before the first update is retried; a failure after an update was yielded is not retried (tests `One_reading_spans_attempts` and `Stream_429_before_first_update_retried`: sink count 1; recorded request counts 2 and 1).
7. The exit-5 and partial-file consequence of exhaustion is delivered in AJ-057.
BDD scenarios:
```gherkin
@unit @AJ-S045 @EDGE-006
Feature: AJ-045 Retry HTTP 429 with backoff and keep the wait out of latency
  @positive
  Scenario: AJ-045 two 429 responses then success are retried
    Given an endpoint that answers 429, 429 and then 200
    When a metered call is made with a fake clock
    Then the reading has retries 2 and outcome "ok"
    And the sink holds exactly 1 reading
  @positive
  Scenario: AJ-045 wait time is excluded from latency
    Given an endpoint that answers 429 once with Retry-After 2 and then 200 after a 100 ms call
    When a metered call is made with a fake clock
    Then waitMs is 2000
    And latencyMs is 100
  @positive
  Scenario: AJ-045 a 429 before the first stream update is retried
    Given a streaming endpoint that answers 429 and then streams normally
    When the stream is consumed
    Then the reading has retries 1 and outcome "ok"
  @negative
  Scenario: AJ-045 always-429 stops after 5 attempts
    Given an endpoint that answers 429 on every call
    When a metered call is made with a fake clock
    Then exactly 5 HTTP requests were recorded
    And the reading has outcome "error" and retries 4
  @negative
  Scenario: AJ-045 a 500 response is not retried
    Given an endpoint that answers 500
    When a metered call is made
    Then exactly 1 HTTP request was recorded
    And the reading has outcome "error" and retries 0
  @negative
  Scenario: AJ-045 a failure after a stream update is not retried
    Given a streaming endpoint that yields one update and then fails with 429
    When the stream is consumed
    Then exactly 1 HTTP request was recorded
    And the reading has outcome "error"
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-045-T1 | Write the failing BDD feature and unit tests for 429 handling | tests/AgentJoules.Specs/Features/Metering/RateLimitRetry.feature, tests/AgentJoules.UnitTests/RetryPolicyTests.cs | 0.5 | AJ-044-T2 |
| AJ-045-T2 | Add 429 and `Retry-After` modes to the fake HTTP handler | tests/AgentJoules.TestSupport/FakeHttpMessageHandler.cs | 0.5 | AJ-045-T1 |
| AJ-045-T3 | Create `RetryOptions` and `RetryLedger` | src/AgentJoules.Metering/RetryOptions.cs, src/AgentJoules.Metering/RetryLedger.cs | 0.5 | AJ-045-T1 |
| AJ-045-T4 | Implement the buffered retry path of `RateLimitRetryChatClient` with backoff, jitter, cap and `Retry-After` | src/AgentJoules.Metering/RateLimitRetryChatClient.cs | 1.5 | AJ-045-T3 |
| AJ-045-T5 | Implement the streaming retry rule (first `MoveNextAsync` only) | src/AgentJoules.Metering/RateLimitRetryChatClient.cs | 1.0 | AJ-045-T4 |
| AJ-045-T6 | Subtract `waitMs` from latency and copy `retries` and `waitMs` into the reading | src/AgentJoules.Metering/MeteredChatClient.cs | 0.75 | AJ-045-T4, AJ-041-T4 |
| AJ-045-T7 | Record the `Spec=EDGE-006` test result in the build log | docs/build-log.md | 0.25 | AJ-045-T6 |
Test mapping: `RetryPolicyTests` (design class); feature file `tests/AgentJoules.Specs/Features/Metering/RateLimitRetry.feature`.
Docs impact: `docs/build-log.md`; the retry rule (5 attempts, base 1 s, factor 2, cap 30 s) is quoted in the README rate-limit note in M6.
Definition of done:
- [x] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings (2026-10-20: build exit 0 with 0 warnings; full run 2,173 passed, 0 failed, 5 skipped)
- [x] docs listed above updated (2026-10-20: docs updated in this entry)
- [x] `dotnet test --filter "Spec=EDGE-006"` shows at least one passed and no failed test (2026-10-20: Spec=EDGE-006 77 passed, 0 failed)
- [x] the retry tests complete without sleeping on the wall clock (injected `TimeProvider`) (2026-10-20: every wait goes through the injected TimeProvider and the exact-time driver, no `Task.Delay` without a provider and no `TimeProvider.System` in the retry tests)
Demo relevance: no (a 429 during the recorded run would show as `retries` in the step line).

### AJ-046: Serialize metered windows with the process-wide meter gate
Covers: REQ-013
User story: As a measurement owner, I want serialized execution to be enforced by one process-wide gate shared by every metered client, so that no two metered energy windows overlap and every step energy value is exclusive.
Acceptance criteria:
1. `IMeterGate` has two implementations: `SerializedMeterGate` (one `SemaphoreSlim(1,1)`) and `NoOpMeterGate`; serialized mode registers one `SerializedMeterGate` instance shared by every `MeteredChatClient`, including judge clients.
2. In serialized mode a client acquires the gate before opening its window and releases it after the window closes; two parallel branches produce non-overlapping windows and no `shared-window` flag (test `Serialized_windows_do_not_overlap`).
3. Gate waiting time is outside the energy window and outside `latencyMs`.
4. A call that throws or is cancelled still releases the gate, so the next call proceeds (test `Gate_is_released_on_failure`).
5. `--mode concurrent` registers the no-op gate; the workflow topology is identical in both modes.
BDD scenarios:
```gherkin
@unit @AJ-S046 @REQ-013
Feature: AJ-046 Serialize metered windows with the process-wide meter gate
  @positive
  Scenario: AJ-046 two parallel branches run one after the other in serialized mode
    Given serialized mode and two metered clients that each take 1 second on a fake clock
    When both clients are called at the same time
    Then the two metering windows do not overlap
    And neither reading has flag "shared-window"
  @positive
  Scenario: AJ-046 gate wait is not counted as latency
    Given serialized mode and a first call that holds the gate for 1 second
    When a second call waits for the gate and then takes 200 ms
    Then the second reading has latencyMs 200
  @positive
  Scenario: AJ-046 concurrent mode uses the no-op gate
    Given concurrent mode
    When the gate is resolved from the service collection
    Then it is the no-op gate
  @negative
  Scenario: AJ-046 failing call releases the gate
    Given serialized mode and a first call that throws
    When a second call is started after the first failed
    Then the second call completes with outcome "ok"
  @negative
  Scenario: AJ-046 cancelled call releases the gate
    Given serialized mode and a first call that is cancelled mid-flight
    When a second call is started
    Then the second call completes with outcome "ok"
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-046-T1 | Write the failing BDD feature and unit tests for the gate | tests/AgentJoules.Specs/Features/Metering/SerializedGate.feature, tests/AgentJoules.UnitTests/ExecutionModeTests.cs | 0.5 | AJ-041-T2 |
| AJ-046-T2 | Create `IMeterGate`, `SerializedMeterGate` and `NoOpMeterGate` | src/AgentJoules.Metering/IMeterGate.cs, src/AgentJoules.Metering/SerializedMeterGate.cs, src/AgentJoules.Metering/NoOpMeterGate.cs | 0.75 | AJ-046-T1 |
| AJ-046-T3 | Acquire before the window and release in `finally` inside `MeteredChatClient` | src/AgentJoules.Metering/MeteredChatClient.cs | 0.75 | AJ-046-T2, AJ-041-T4 |
| AJ-046-T4 | Register the gate by run mode in the service collection | src/AgentJoules.Metering/MeteringServices.cs, src/AgentJoules.Workflow/ServiceCollectionExtensions.cs | 0.5 | AJ-046-T2 |
| AJ-046-T5 | Record the `Spec=REQ-013` gate results in the build log | docs/build-log.md | 0.25 | AJ-046-T3 |
Test mapping: `ExecutionModeTests` (design class; the 1 s branches use wide margins and assert overlap only, never exact durations); feature file `tests/AgentJoules.Specs/Features/Metering/SerializedGate.feature`.
Docs impact: `docs/build-log.md`; the README "serialized by default" note is written in M6.
Carried from AJ-032 (2026-10-19): `SerializedMeterGate` is not re-entrant, so no judge call and no second metered call may start while a flow holds the gate; a streaming `MeteredChatClient` holds the gate until its enumerator is disposed, so an abandoned enumerator hangs the run until the run token fires; the try/finally that disposes the releaser must begin immediately after `AcquireAsync` returns, before `ledger.Reset()` and `OpenWindow()`, or one failed call keeps the only slot for the process; the composition root disposes the gate only after every metered call has finished (a waiter queued at Dispose never completes); the shared-window overlap rule is strict (a.Start < b.Stop and b.Start < a.Stop).
Definition of done:
- [x] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings (2026-10-20: build 0 warnings exit 0 and 1,893 passed, 0 failed, 5 skipped)
- [x] docs listed above updated (2026-10-20: in the AJ-046 entry of `docs/build-log.md`)
- [x] `dotnet test --filter "Spec=REQ-013"` shows at least one passed and no failed test for the serialized tests (2026-10-20: Spec=REQ-013 23 passed, 0 failed)
Demo relevance: yes: serialized default is why each demo step has an exclusive energy value.

### AJ-047: Flag shared windows and total concurrent runs over the union window
Covers: REQ-013, EDGE-004
User story: As a reader of concurrent-mode results, I want overlapping readings flagged `shared-window` and the run energy computed over the union of windows, so that per-step values are never summed into a wrong total.
Acceptance criteria:
1. In concurrent mode two branches of 1 s that start together produce two readings flagged `shared-window`, and the run `totals.grossJoules` equals the union-window energy, not the sum of the two readings (test `Concurrent_total_is_union_not_sum`, fixture values from `DeterministicEnergyMeter`, not measurements).
2. In serialized mode the run total is the sum of the step readings and no reading has `shared-window`.
3. Windows that only touch at a boundary (end equals next start) are not overlapping and are not flagged (test `Touching_windows_not_shared`: neither reading has flag `shared-window`).
4. Tokens and cost are summed (cost per currency, no conversion); `wallClockMs` is the workflow wall time; judge readings are never included. These sums, the serialized totals and the hosted-tier N/A rule are created in AJ-056-T4a (needed by every run file); this story asserts them again for concurrent mode (test `Concurrent_sums_and_judge_exclusion`).
5. In a hosted-tier concurrent run, energy totals are `N/A` with reason `hosted-tier`, never 0.
6. The fixed sentence `Per-step energy overlaps in concurrent mode and does not sum to the run total.` is defined once in `ReportSentences` (created in AJ-056-T4a) and the run records `run.mode: "concurrent"` (test `Concurrent_run_records_mode`); the rendered-summary assertion for the sentence is part of the Markdown story in M3.
BDD scenarios:
```gherkin
@unit @AJ-S047 @REQ-013 @EDGE-004
Feature: AJ-047 Flag shared windows and total concurrent runs over the union window
  @positive
  Scenario: AJ-047 overlapping branches are flagged and totalled over the union
    Given concurrent mode and two branches of 1 second at a constant 60 W on a deterministic meter
    When the run is assembled
    Then both readings have flag "shared-window"
    And the run gross energy equals the union-window energy and not the sum of the two readings
  @positive
  Scenario: AJ-047 serialized run totals are the sum of the steps
    Given serialized mode and two consecutive 1 second steps at a constant 60 W
    When the run is assembled
    Then the run gross energy equals the sum of the two readings
    And no reading has flag "shared-window"
  @positive
  Scenario: AJ-047 cost is totalled per currency without conversion
    Given step costs of 0.10 EUR, 0.20 EUR and 0.05 USD
    When the run is assembled
    Then costByCurrency has EUR 0.30 and USD 0.05
  @negative
  Scenario: AJ-047 windows that only touch are not shared
    Given concurrent mode and two windows where the first ends exactly when the second starts
    When the run is assembled
    Then neither reading has flag "shared-window"
  @negative
  Scenario: AJ-047 hosted-tier concurrent totals are N/A not zero
    Given concurrent mode on tier "nvidia" with unmetered readings
    When the run is assembled
    Then totals grossJoules has label "N/A" and a null value
  @negative
  Scenario: AJ-047 judge readings never enter the totals
    Given 4 workflow readings and 4 judge readings
    When the run is assembled
    Then totals inputTokens equals the sum over the 4 workflow readings only
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-047-T1 | Write the failing BDD feature and unit tests for overlap and union totals | tests/AgentJoules.Specs/Features/Metering/ConcurrentUnion.feature, tests/AgentJoules.UnitTests/ExecutionModeTests.cs | 0.5 | AJ-046-T1 |
| AJ-047-T2 | Implement `UnionWindowCalculator` (merge overlapping spans, sum counter deltas or integrate samples) and wire `ComputeUnion` into both meters | src/AgentJoules.Core/Energy/UnionWindowCalculator.cs, src/AgentJoules.Core/Energy/DeterministicEnergyMeter.cs | 1.5 | AJ-047-T1 |
| AJ-047-T3 | Add overlap detection and the `shared-window` flag to `RunAssembler` | src/AgentJoules.Workflow/Runs/RunAssembler.cs | 1.0 | AJ-047-T2, AJ-056-T4a |
| AJ-047-T4 | Add the union-window totals branch for concurrent mode to `RunAssembler` | src/AgentJoules.Workflow/Runs/RunAssembler.cs | 0.75 | AJ-047-T3 |
| AJ-047-T5 | Record the `Spec=REQ-013` and `Spec=EDGE-004` results in the build log | docs/build-log.md | 0.25 | AJ-047-T4 |
Test mapping: `ExecutionModeTests` (design class); feature file `tests/AgentJoules.Specs/Features/Metering/ConcurrentUnion.feature`.
Docs impact: `docs/build-log.md`; `docs/methodology.md` states the union-window rule (written in M4).
Definition of done:
- [ ] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [ ] `dotnet test --filter "Spec=EDGE-004"` shows at least one passed and no failed test
Demo relevance: no (the demo runs serialized; concurrent mode is a documented option).

### AJ-048: Load the fixed task set from tasks/
Covers: REQ-012
User story: As a measurement owner, I want a small fixed set of committed tasks with a name, a prompt and a token limit, so that every tier answers the same inputs and runs are comparable.
Acceptance criteria:
1. `ls tasks/*.json | wc -l` prints 3; `tasks/` contains exactly `01-incident-brief.json` (`name` "incident-brief", `maxOutputTokens` 512), `02-api-migration.json` (`name` "api-migration", 768) and `03-energy-explainer.json` (`name` "energy-explainer", 384); each parses with `name`, `prompt`, `maxOutputTokens` (test `Committed_tasks_parse`).
2. `TaskSetLoader` returns the tasks in file-name order; `--task <name>` selects one.
3. A task file with a missing field, `maxOutputTokens` of 0 or less, malformed JSON or a duplicate `name` exits 2 and names the file.
4. An unknown `--task` name exits 2 and lists the available names.
5. Each prompt is self-contained (no placeholder tokens such as `{{`).
BDD scenarios:
```gherkin
@unit @AJ-S048 @REQ-012
Feature: AJ-048 Load the fixed task set from tasks/
  @positive
  Scenario: AJ-048 committed tasks load in file-name order
    Given the committed tasks folder
    When the task set is loaded
    Then the names are "incident-brief", "api-migration" and "energy-explainer" in that order
  @positive
  Scenario: AJ-048 a single task is selected by name
    Given the committed tasks folder
    When the task "energy-explainer" is requested
    Then exactly 1 task with maxOutputTokens 384 is returned
  @negative
  Scenario: AJ-048 missing maxOutputTokens exits 2 naming the file
    Given a tasks folder where "02-api-migration.json" has no maxOutputTokens
    When the task set is loaded
    Then the exit code is 2
    And the output names "02-api-migration.json"
  @negative
  Scenario: AJ-048 unknown task name exits 2 and lists the available names
    Given the committed tasks folder
    When the task "bogus" is requested
    Then the exit code is 2
    And the output lists "incident-brief", "api-migration" and "energy-explainer"
  @negative
  Scenario: AJ-048 duplicate task names exit 2
    Given a tasks folder with two files whose name is "same"
    When the task set is loaded
    Then the exit code is 2
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-048-T1 | Write the failing BDD feature and unit tests for the task set | tests/AgentJoules.Specs/Features/Configuration/TaskSet.feature, tests/AgentJoules.UnitTests/TaskSetTests.cs | 0.5 | - |
| AJ-048-T2 | Implement `TaskDefinition` and `TaskSetLoader` with validation (source-generated JSON) | src/AgentJoules.Workflow/Tasks/TaskDefinition.cs, src/AgentJoules.Workflow/Tasks/TaskSetLoader.cs, src/AgentJoules.Core/CoreJsonContext.cs | 1.0 | AJ-048-T1 |
| AJ-048-T3 | Author the incident-brief task | tasks/01-incident-brief.json | 0.5 | AJ-048-T2 |
| AJ-048-T4 | Author the api-migration task | tasks/02-api-migration.json | 0.5 | AJ-048-T2 |
| AJ-048-T5 | Author the energy-explainer task | tasks/03-energy-explainer.json | 0.5 | AJ-048-T2 |
| AJ-048-T6 | Document the task file shape in the build log | docs/build-log.md | 0.25 | AJ-048-T5 |
Test mapping: `TaskSetTests` (design class); feature file `tests/AgentJoules.Specs/Features/Configuration/TaskSet.feature`.
Docs impact: `tasks/*.json`; the README task table is written in M6.
Definition of done:
- [x] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings (2026-10-20: build exit 0, 0 warnings; full run 2,271/0/5 at 19:08Z)
- [x] docs listed above updated (2026-10-20: task file shape in the build-log AJ-048 entry)
- [x] `ls tasks/*.json | wc -l` prints 3 and the folder holds nothing else (2026-10-20: `ls tasks/*.json | wc -l` prints 3 and `ls -A tasks | wc -l` prints 3)
Demo relevance: yes: the demo runs `--task energy-explainer`.

### AJ-049: Create one metered agent per name from a tier and model
Covers: REQ-011
User story: As a workflow developer, I want an agent factory that builds each of the four agents from its own metered, retry-protected chat client, so that every reading is attributed to the agent that owns the client.
Acceptance criteria:
1. `AgentCatalog` defines exactly four agents: Planner, Researcher, Writer, Reviewer, with the instructions of the technical design (section 3.4).
2. `AgentFactory.Create(identity, chatOptions)` builds the pipeline MeteredChatClient (outermost) -> RateLimitRetryChatClient -> provider client, wraps it in a `ChatClientAgent` with `UseProvidedChatClientAsIs = true`, and the agent name equals `identity.Agent` (test `Pipeline_order_and_name`).
3. Four agents created for one (tier, task, repeat) hold four distinct `MeteredChatClient` instances, each constructed with its own agent name; the test calls each agent through fake clients and asserts each reading's agent name matches the agent that owns the client (test `Reading_agent_matches_owner`).
4. Each agent's `ChatOptions` has temperature 0 and the task's `maxOutputTokens`.
5. A different `(tier, task, repeat)` gets new client instances (no shared retry ledger).
BDD scenarios:
```gherkin
@unit @AJ-S049 @REQ-011
Feature: AJ-049 Create one metered agent per name from a tier and model
  @positive
  Scenario: AJ-049 four agents have four distinct metered clients
    Given the agent factory with fake provider clients
    When agents "Planner", "Researcher", "Writer" and "Reviewer" are created for one task and repeat
    Then there are 4 distinct metered client instances
  @positive
  Scenario: AJ-049 each reading carries the name of the owning agent
    Given the four agents created over fake chat clients
    When each agent answers one prompt
    Then the sink holds 4 readings and each reading agent name equals the agent that produced it
  @positive
  Scenario: AJ-049 chat options use temperature 0 and the task limit
    Given the task "incident-brief" with maxOutputTokens 512
    When an agent is created
    Then its chat options have temperature 0 and max output tokens 512
  @negative
  Scenario: AJ-049 an agent name outside the catalog is rejected
    Given the agent factory
    When an agent named "Judgey" is requested from the catalog
    Then creation fails with a configuration error
  @negative
  Scenario: AJ-049 a new repeat does not reuse the previous retry ledger
    Given agents created for repeat 1 whose Writer recorded 2 retries
    When agents are created for repeat 2
    Then the new Writer client starts with 0 retries
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-049-T1 | Write the failing BDD feature and unit tests for the agent factory | tests/AgentJoules.Specs/Features/Workflow/AgentFactory.feature, tests/AgentJoules.UnitTests/AttributionBaselineTests.cs | 0.5 | AJ-044-T5 |
| AJ-049-T2 | Create `AgentCatalog` with the four agent definitions | src/AgentJoules.Workflow/Agents/AgentCatalog.cs | 0.5 | AJ-049-T1 |
| AJ-049-T3 | Implement `AgentFactory.Create` with the metering and retry pipeline and `UseProvidedChatClientAsIs` | src/AgentJoules.Workflow/Agents/AgentFactory.cs | 1.25 | AJ-049-T2, AJ-045-T4, AJ-041-T5 |
| AJ-049-T4 | Register the factory and a per-run scope for ledgers | src/AgentJoules.Workflow/ServiceCollectionExtensions.cs | 0.5 | AJ-049-T3 |
| AJ-049-T5 | Record the `Spec=REQ-011` agent-factory results in the build log | docs/build-log.md | 0.25 | AJ-049-T4 |
Test mapping: `AttributionBaselineTests` (required test class); feature file `tests/AgentJoules.Specs/Features/Workflow/AgentFactory.feature`.
Docs impact: `docs/build-log.md`; the agent role table is written in the README in M6.
Definition of done:
- [x] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings (2026-10-20: build 0 warnings exit 0 and 2,309/0/5)
- [x] docs listed above updated (2026-10-20: docs updated in this entry)
- [x] `grep -rnE "AsyncLocal" src` still returns no hit (2026-10-20: `grep -rnIE "AsyncLocal" src --include=*.cs` 0 hits; the literal command matches third-party DLLs under bin/, as recorded for AJ-041)
Demo relevance: yes: the four named agents are the rows of the demo table and chart.

### AJ-050: Run Planner, fan-out Researcher and Writer, fan-in Reviewer as a workflow
Covers: REQ-011
User story: As a workflow developer, I want the four agents wired as a Microsoft Agent Framework workflow with one fan-out and one fan-in, executed through `InProcessExecution.RunStreamingAsync`, so that the demo is a real multi-agent graph with per-step energy.
Acceptance criteria:
1. `grep -rnE "AddFanOutEdge|BuildConcurrent" src`, `grep -rnE "AddFanInBarrierEdge|BuildConcurrent" src` and `grep -rn "RunStreamingAsync" src` each return at least one hit.
2. A unit test runs the workflow with fake chat clients and asserts a reading exists for each of Planner, Researcher, Writer and Reviewer, each with the matching agent name (test `Workflow_has_one_reading_per_agent`).
3. The Reviewer starts only after both branches finish: the Reviewer window start is not before the later of the Researcher and Writer window ends (test `Reviewer_starts_after_both_branches`, asserted on fake-clock window timestamps).
4. All four executors stream (`agent.RunStreamingAsync`), so every reading has a measured `ttftMs` with the streaming fake.
5. When one agent fails (fake endpoint answers 500), the Reviewer is not called, the failed agent has an `error` reading and the workflow raises `StepFailedException` (exit 5 mapping is AJ-057).
6. Executors declare their message types (`SendsMessage`, `YieldsOutput`); an undeclared type would fail at run time, so a test runs the full graph once.
BDD scenarios:
```gherkin
@unit @AJ-S050 @REQ-011
Feature: AJ-050 Run Planner, fan-out Researcher and Writer, fan-in Reviewer as a workflow
  @positive
  Scenario: AJ-050 workflow produces one reading per named agent
    Given the workflow factory over four fake chat clients
    When the workflow runs the task "energy-explainer"
    Then the sink holds readings for "Planner", "Researcher", "Writer" and "Reviewer"
    And each reading agent name matches the agent owning that client
  @positive
  Scenario: AJ-050 reviewer waits for both branches
    Given serialized mode and fake clients whose Researcher takes 1 second and Writer takes 2 seconds
    When the workflow runs
    Then the Reviewer window starts after the Writer window ends
  @positive
  Scenario: AJ-050 every step streams and records time to first token
    Given fake streaming clients
    When the workflow runs
    Then every reading has a measured ttftMs
  @negative
  Scenario: AJ-050 a failing researcher stops the workflow before the reviewer
    Given a Researcher client whose endpoint answers 500
    When the workflow runs
    Then StepFailedException is raised
    And the sink holds an "error" reading for "Researcher" and no reading for "Reviewer"
  @negative
  Scenario: AJ-050 a failing planner stops both branches
    Given a Planner client whose endpoint answers 500
    When the workflow runs
    Then the sink holds one reading for "Planner" with outcome "error"
    And no reading for "Researcher" or "Writer"
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-050-T1 | Write the failing BDD feature and unit tests for the workflow graph | tests/AgentJoules.Specs/Features/Workflow/FanOutFanIn.feature, tests/AgentJoules.UnitTests/AttributionBaselineTests.cs | 0.5 | AJ-049-T1 |
| AJ-050-T2 | Create the message records (`TaskInput`, `PlanMessage`, `BranchOutput`, `FinalOutput`) | src/AgentJoules.Workflow/Graph/Messages.cs | 0.5 | AJ-050-T1 |
| AJ-050-T3 | Implement `PlannerExecutor` | src/AgentJoules.Workflow/Graph/PlannerExecutor.cs | 0.75 | AJ-050-T2, AJ-049-T3 |
| AJ-050-T4 | Implement `ResearcherExecutor` and `WriterExecutor` | src/AgentJoules.Workflow/Graph/ResearcherExecutor.cs, src/AgentJoules.Workflow/Graph/WriterExecutor.cs | 1.0 | AJ-050-T3 |
| AJ-050-T5 | Implement `ReviewerExecutor` collecting both branches and yielding the final output | src/AgentJoules.Workflow/Graph/ReviewerExecutor.cs | 1.0 | AJ-050-T4 |
| AJ-050-T5a | Create or extend `StepFailedException` (exit 5) in the exception hierarchy | src/AgentJoules.Core/Cli/AgentJoulesException.cs | 0.25 | AJ-050-T1 |
| AJ-050-T6 | Implement `AgentJoulesWorkflowFactory` and the `RunStreamingAsync` event loop | src/AgentJoules.Workflow/Graph/AgentJoulesWorkflowFactory.cs | 1.5 | AJ-050-T5, AJ-050-T5a |
| AJ-050-T7 | Run the three REQ-011 greps and record the hits in the build log | docs/build-log.md | 0.25 | AJ-050-T6 |
Test mapping: `AttributionBaselineTests` (required test class); feature file `tests/AgentJoules.Specs/Features/Workflow/FanOutFanIn.feature`.
Docs impact: `docs/build-log.md`; the workflow diagram for the README is produced in M6.
Definition of done:
- [x] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings (2026-10-20: build exit 0 with 0 warnings; battery 2,339 passed, 0 failed, 5 skipped)
- [x] docs listed above updated (2026-10-20: build log entry, test count, per-ID and secret scan rows; grep hits recorded)
- [x] `dotnet test --filter "Spec=REQ-011"` shows at least one passed and no failed test (2026-10-20: 58 passed, 0 failed; full run 2,339 passed, 0 failed, 5 skipped)
- [x] the three greps in criterion 1 each return at least one hit (2026-10-20: hits 1, 1 and 2 over src/*.cs)
Demo relevance: yes: the demo shows Planner, then Researcher and Writer, then Reviewer, each with its own joules.

### AJ-051: Record OpenTelemetry instruments and the activity source
Covers: REQ-014
User story: As an observability user, I want the agents built with `UseOpenTelemetry()` and a `Meter` named `AgentJoules` recording joules, watts, tokens, latency and cost, so that I can feed the same readings into a console or OTLP backend.
Acceptance criteria:
1. `grep -rn "UseOpenTelemetry" src` returns at least one hit (in `AgentFactory`).
2. A unit test with an in-memory metric exporter runs one metered fake local call and asserts that the five instruments `agentjoules.step.energy`, `agentjoules.step.power`, `agentjoules.step.tokens`, `agentjoules.step.latency` and `agentjoules.step.cost` each recorded a measurement carrying the attributes `gen_ai.agent.name`, `aj.tier` and `gen_ai.request.model` (test `Five_instruments_with_three_attributes`).
3. A metric whose value is null (hosted-tier energy, not-reported tokens, N/A cost) is not recorded, so no zero measurement appears (test `Null_metrics_not_recorded`: the in-memory exporter holds no measurement for them).
4. The console metrics exporter is on by default for `run` and `calibrate` and absent with `--no-console-telemetry`; the OTLP exporter is added only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set.
5. Traces use sources `AgentJoules` and `Microsoft.Agents.AI.Workflows*`, go to OTLP only, and `EnableSensitiveData` stays off (no prompt text in spans).
6. The README telemetry section lists the instrument names and the OTLP variable (check: `grep -c "agentjoules.step.energy" README.md` and `grep -c "OTEL_EXPORTER_OTLP_ENDPOINT" README.md` each print at least 1).
BDD scenarios:
```gherkin
@unit @AJ-S051 @REQ-014
Feature: AJ-051 Record OpenTelemetry instruments and the activity source
  @positive
  Scenario: AJ-051 one local call records all five instruments with three attributes
    Given an in-memory metric exporter and a metered local fake call with measured energy and usage
    When the call completes and the meter provider is flushed
    Then the instruments energy, power, tokens, latency and cost each have a measurement
    And every measurement has attributes gen_ai.agent.name, aj.tier and gen_ai.request.model
  @positive
  Scenario: AJ-051 OTLP exporter is added when the endpoint variable is set
    Given the environment variable OTEL_EXPORTER_OTLP_ENDPOINT is set in the configuration override
    When the telemetry setup is built
    Then an OTLP exporter is registered
  @negative
  Scenario: AJ-051 hosted-tier call records no energy measurement
    Given an in-memory metric exporter and a metered fake call on tier "nvidia"
    When the call completes and the meter provider is flushed
    Then the energy and power instruments have no measurement
  @negative
  Scenario: AJ-051 call without usage records no token measurement
    Given an in-memory metric exporter and a metered fake call whose response has no usage
    When the call completes and the meter provider is flushed
    Then the tokens instrument has no measurement and no zero value
  @negative
  Scenario: AJ-051 console exporter is off with the opt-out flag
    Given the run command with --no-console-telemetry
    When the telemetry setup is built
    Then no console exporter is registered
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-051-T1 | Write the failing BDD feature and unit tests for the instruments | tests/AgentJoules.Specs/Features/Metering/Telemetry.feature, tests/AgentJoules.UnitTests/TelemetryTests.cs | 0.5 | AJ-041-T3 |
| AJ-051-T2 | Create `AgentJoulesInstruments` with the five instruments | src/AgentJoules.Metering/AgentJoulesInstruments.cs | 1.0 | AJ-051-T1 |
| AJ-051-T3 | Record measurements from `MeteredChatClient`, skipping null values | src/AgentJoules.Metering/MeteredChatClient.cs | 0.75 | AJ-051-T2 |
| AJ-051-T4 | Add `UseOpenTelemetry(sourceName: "AgentJoules")` to the agent pipeline | src/AgentJoules.Workflow/Agents/AgentFactory.cs | 0.5 | AJ-051-T2, AJ-049-T3 |
| AJ-051-T5 | Implement `TelemetrySetup` with console and OTLP switches and the trace sources | src/AgentJoules.Cli/Hosting/TelemetrySetup.cs | 1.0 | AJ-051-T2 |
| AJ-051-T6 | Write the README telemetry section with instrument names and the OTLP variable | README.md | 0.5 | AJ-051-T5 |
Test mapping: `TelemetryTests` (design class, trait `Spec=REQ-014`); feature file `tests/AgentJoules.Specs/Features/Metering/Telemetry.feature`.
Docs impact: README section "Telemetry" (instrument names, `--no-console-telemetry`, `OTEL_EXPORTER_OTLP_ENDPOINT`).
Definition of done:
- [x] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings (2026-10-20: build 0 warnings exit 0 and 2,412/0/5)
- [x] docs listed above updated (2026-10-20: README `## Telemetry` written and this entry)
- [x] `dotnet test --filter "Spec=REQ-014"` shows at least one passed and no failed test (2026-10-20: Spec=REQ-014 68 passed, 0 failed)
- [x] `grep -rn "UseOpenTelemetry" src` returns at least one hit (2026-10-20: 1 hit, `src/AgentJoules.Workflow/Agents/AgentFactory.cs` line 135)
Demo relevance: no (optional: the console exporter output can be shown for a few seconds).

### AJ-052: Exit 3 before any call when an API key is missing
Covers: EDGE-011
User story: As a user running a hosted tier, I want the command to name every missing key variable and the user-secrets alternative, and never print a key value, so that I can fix the setup in one pass without leaking a secret.
Acceptance criteria:
1. With an empty environment, `run --tier all --dry-run` exits 3 and the one message names both `NVIDIA_API_KEY` and `NEBIUS_API_KEY` (test `Missing_keys_listed_in_one_message`).
2. The message includes the alternative `dotnet user-secrets set "<NAME>" "<value>" --project src/AgentJoules.Cli` with the literal text `<value>` and no real value.
3. A key set to a runtime-built `nvapi-` value together with a failing endpoint never appears in stdout or stderr: no character of the key beyond the public `nvapi-` prefix (test `Key_suffix_is_never_printed`; design 3.2 and design-review N-09, updated 2026-10-18 at the M0 exit) (handled paths; an exception that escapes a thread is covered by the AJ-034 UnhandledException handler).
4. A missing workflow-tier key exits 3 before any HTTP request (the fake handler records 0 requests).
5. Outside `--dry-run`, a missing judge key only warns and the command continues; with `--dry-run` the judge key joins the same message.
6. For `cloudgpu` on the laptop (`CloudGpu:RequireSshVariables` true) a missing `CLOUDGPU_SSH_HOST` or `CLOUDGPU_SSH_USER` exits 3; on the VM (`RequireSshVariables` false) it does not.
7. The harness is deny-by-default: when `CliHostOverrides` is non-null the GPU factory is `UnavailableGpuTelemetrySourceFactory`, every primary HTTP handler is a `DenyAllHttpMessageHandler` that throws naming the URL, `IPowerSourceDetector` returns `unknown`, and the OTLP endpoint is read only from the isolated `IConfiguration`; a harness run with no override performs 0 real requests (test `Harness_makes_no_real_requests`). (Note 2026-10-20, AJ-052 spec review: the `IPowerSourceDetector` clause moves to AJ-106, which creates the type in M4; the deny-all handler wiring lives in the test harness `CliHarnessRun`, the only caller passing non-null overrides, see design 3.6 line 489; the other clauses are delivered by AJ-052.)
BDD scenarios:
```gherkin
@unit @AJ-S052 @EDGE-011
Feature: AJ-052 Exit 3 before any call when an API key is missing
  @positive
  Scenario: AJ-052 all required keys present passes the key check
    Given fake keys for NVIDIA_API_KEY and NEBIUS_API_KEY in the configuration override
    When "run --tier all --dry-run" is executed
    Then the key check passes and the exit code is not 3
  @positive
  Scenario: AJ-052 missing judge key outside dry-run only warns
    Given NEBIUS_API_KEY is present and the judge key is absent
    When "run --tier nebius" is executed with fake endpoints
    Then a warning names the judge key and the exit code is not 3
  @negative
  Scenario: AJ-052 dry-run lists every missing key in one message
    Given an empty environment
    When "run --tier all --dry-run" is executed
    Then the exit code is 3
    And one message names "NVIDIA_API_KEY" and "NEBIUS_API_KEY"
    And the message shows the user-secrets alternative with the placeholder "<value>"
  @negative
  Scenario: AJ-052 key value is never printed when the endpoint fails
    Given a runtime-built key starting with "nvapi-" and an endpoint that refuses the connection
    When "run --tier nvidia" is executed
    Then the output does not contain any character of the key after the nvapi- prefix
  @negative
  Scenario: AJ-052 no request is sent when a key is missing
    Given tier "nebius" selected and NEBIUS_API_KEY absent
    When "run --tier nebius" is executed
    Then the exit code is 3
    And the fake handler recorded 0 requests
  @negative
  Scenario: AJ-052 missing SSH variable for cloudgpu on the laptop exits 3
    Given tier "cloudgpu" enabled with RequireSshVariables true and CLOUDGPU_SSH_HOST absent
    When "run --tier cloudgpu --dry-run" is executed
    Then the exit code is 3
    And the message names "CLOUDGPU_SSH_HOST"
  @positive
  Scenario: AJ-052 harness run with only fakes performs no real request
    Given a harness run with fake keys and a fake models handler
    When "run --tier nvidia --dry-run" is executed
    Then the deny-all handler recorded 0 real requests
  @negative
  Scenario: AJ-052 unconfigured URL is denied naming the URL
    Given a harness run where a tier base URL has no fake route
    When a request to that URL is attempted
    Then the deny-all handler throws and the message names the URL
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-052-T1 | Write the failing BDD feature and unit tests for missing keys | tests/AgentJoules.Specs/Features/Cli/MissingKeys.feature, tests/AgentJoules.UnitTests/DryRunTests.cs | 0.5 | AJ-044-T2 |
| AJ-052-T2 | Create `CliHarness` with `CliHostOverrides` (isolated environment and user-secrets, unavailable GPU factory, `DenyAllHttpMessageHandler`, power source `unknown`) or extend the M1 one | tests/AgentJoules.TestSupport/CliHarness.cs, tests/AgentJoules.TestSupport/DenyAllHttpMessageHandler.cs, src/AgentJoules.Cli/CliApplication.cs | 2.0 | AJ-052-T1 |
| AJ-052-T3 | Implement the key check in `PreflightService` for workflow tiers, judge key and SSH variables | src/AgentJoules.Workflow/Runs/PreflightService.cs | 1.0 | AJ-052-T2, AJ-043-T3 |
| AJ-052-T4 | Create `MissingKeyException` with the single-message formatter and the user-secrets text | src/AgentJoules.Core/Cli/AgentJoulesException.cs, src/AgentJoules.Workflow/Runs/MissingKeyMessage.cs | 0.5 | AJ-052-T3 |
| AJ-052-T5 | Verify redaction of the output writers with the runtime-built key | src/AgentJoules.Cli/Output/RedactingTextWriter.cs, tests/AgentJoules.UnitTests/RedactionTests.cs | 0.5 | AJ-052-T2 |
| AJ-052-T6 | Document the key variables and the user-secrets commands | README.md | 0.5 | AJ-052-T4 |
Test mapping: `DryRunTests` and `RedactionTests` (design classes, trait `Spec=EDGE-011`); feature file `tests/AgentJoules.Specs/Features/Cli/MissingKeys.feature`.
Docs impact: README section "Keys" (variable names, `setx` and `dotnet user-secrets` forms; no values).
Definition of done:
- [x] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings (2026-10-20: build 0 warnings exit 0 and 2,526/0/5)
- [x] docs listed above updated (2026-10-20: README `## Keys` written and this entry)
- [x] `dotnet test --filter "Spec=EDGE-011"` shows at least one passed and no failed test (2026-10-20: Spec=EDGE-011 66 passed, 0 failed)
- [x] `CliHarness` never reads the process environment unless a test asks for it (2026-10-20: `Harness_reads_neither_the_process_environment_nor_user_secrets_unless_asked` green)
Demo relevance: no.

### AJ-053: Exit 4 before any completion call when a model ID is not listed
Covers: EDGE-008
User story: As a user, I want the command to fail with the missing model ID and a short list of available IDs before any completion call, so that a typo or a retired model costs no quota and no time.
Acceptance criteria:
1. When the fake `GET /v1/models` response lacks a configured model ID, the command exits 4, the output contains the missing ID and at least one listed ID, and the fake handler recorded 0 requests to the completions route (test `Missing_model_exits_4`).
2. At most 20 available IDs are printed, Nemotron and Qwen entries first (test `Model_ids_capped_at_20_nemotron_first`: the output has 20 ID lines and the first lines contain `nemotron` or `qwen`).
3. Every configured ID of a selected tier is checked, including each `AgentModelIds` value, and `Judge:ModelId` is checked against the judge tier.
4. A models endpoint that refuses the connection exits 5; an empty model list exits 4 with the line `no models listed`.
5. The local runtime tag-missing hint text (`ollama pull <tag>`) belongs to M4; here the generic ID list applies to every tier.
BDD scenarios:
```gherkin
@unit @AJ-S053 @EDGE-008
Feature: AJ-053 Exit 4 before any completion call when a model ID is not listed
  @positive
  Scenario: AJ-053 all configured IDs listed passes
    Given a fake models response listing every configured model ID for tier "nvidia"
    When the model check runs
    Then the check passes
  @positive
  Scenario: AJ-053 available IDs are capped at 20 with Nemotron and Qwen first
    Given a fake models response with 30 IDs including 2 Nemotron and 1 Qwen entries
    And the configured ID is absent
    When the command runs
    Then the output lists 20 IDs
    And the Nemotron and Qwen IDs appear before the others
  @negative
  Scenario: AJ-053 missing model ID exits 4 with no completion call
    Given a fake models response that lacks the configured ID "missing-model"
    When "run --tier nvidia" is executed
    Then the exit code is 4
    And the output contains "missing-model" and at least one listed ID
    And the fake handler recorded 0 requests to the completions route
  @negative
  Scenario: AJ-053 per-agent model ID that is not listed exits 4
    Given AgentModelIds maps Writer to an ID absent from the fake models response
    When "run --tier nebius --dry-run" is executed
    Then the exit code is 4
    And the output contains that ID
  @negative
  Scenario: AJ-053 unreachable models endpoint exits 5
    Given a models endpoint that refuses the connection
    When "run --tier nvidia --dry-run" is executed
    Then the exit code is 5
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-053-T1 | Write the failing BDD feature and unit tests for the model check | tests/AgentJoules.Specs/Features/Cli/ModelNotFound.feature, tests/AgentJoules.UnitTests/DryRunTests.cs | 0.5 | AJ-052-T2 |
| AJ-053-T2 | Implement `ModelCatalogClient.ListAsync` (`GET {BaseUrl}/models` with bearer key) | src/AgentJoules.Workflow/Providers/ModelCatalogClient.cs | 1.0 | AJ-053-T1, AJ-044-T3 |
| AJ-053-T3 | Implement the ID ordering and the 20-entry cap formatter | src/AgentJoules.Workflow/Providers/ModelIdListFormatter.cs | 0.5 | AJ-053-T2 |
| AJ-053-T4 | Add the model check and the exit 4 and exit 5 mapping to `PreflightService`; create `ModelNotFoundException` (exit 4) and `EndpointUnreachableException` (exit 5) in `AgentJoulesException.cs` (closing-pass row Y-10; AJ-057-T3 reuses them) | src/AgentJoules.Workflow/Runs/PreflightService.cs, src/AgentJoules.Core/Cli/AgentJoulesException.cs | 0.75 | AJ-053-T3 |
| AJ-053-T5 | Record the `Spec=EDGE-008` result in the build log | docs/build-log.md | 0.25 | AJ-053-T4 |
Test mapping: `DryRunTests` (design class, trait `Spec=EDGE-008`); feature file `tests/AgentJoules.Specs/Features/Cli/ModelNotFound.feature`.
Docs impact: `docs/build-log.md`; the exit-code table in the README is written in M6.
Definition of done:
- [x] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings (2026-10-21: build 0 warnings exit 0 and 2,567/0/5)
- [x] docs listed above updated (2026-10-21: this build-log entry)
- [x] `dotnet test --filter "Spec=EDGE-008"` shows at least one passed and no failed test (2026-10-21: Spec=EDGE-008 36 passed, 0 failed)
Demo relevance: no.

### AJ-054: Add `run --dry-run` that validates everything and makes no completion call
Covers: REQ-017, REQ-014, EDGE-001
Note (2026-10-20, M1 exit): EDGE-001 added per plan section 5 seam row 5 (`run --tier local` with the missing-NVML fake carries the EDGE-001 trait) and the M1 spec full pass, so the trait is not lost.
User story: As a user, I want `AJ run --dry-run` to validate configuration, keys and model IDs and to show the endpoint and matched models per tier without spending a token, so that I find setup problems before a long measured run.
Acceptance criteria:
1. `AJ run --help` lists `--tier`, `--task`, `--mode`, `--out`, `--baseline-seconds`, `--repeat`, `--no-console-telemetry` and `--dry-run` (test `Help_lists_eight_options`).
2. `AJ run --tier all --dry-run` with fake endpoints, fake keys and models listed exits 0 and prints, per tier, the endpoint, the matched model IDs and `OK`.
3. A dry-run never calls a chat completions route (test `Dry_run_never_calls_completions`: no recorded request path ends in `/chat/completions`).
4. `AJ run --tier bogus` exits 2; `--mode bogus`, `--repeat 0` and `--baseline-seconds -1` exit 2.
5. For `local` the dry-run runs the probe through the GPU port; with no GPU it exits 6.
6. Live check on the laptop (keys required): `AJ run --tier nvidia --dry-run` and `AJ run --tier nebius --dry-run` exit 0; the `local` dry-run is completed in M4.
7. Added 2026-10-20 (AJ-051 spec review): the run command offers the option from `TelemetryOptions.CreateNoConsoleTelemetry()` and its host calls `TelemetrySetup.Add(services, configuration, consoleEnabled)` with the console metrics exporter on unless the flag is given; `probe` never enables it (tests `Run_registers_console_exporter_by_default` and `No_console_telemetry_omits_console_exporter`, trait `Spec=REQ-014`, using the AJ-051 registration-count helpers). `AgentJoulesInstruments` is registered by AJ-056 with the composition root. (Note 2026-10-21, AJ-054: in a dry run the console metrics exporter is not registered, because the OpenTelemetry console exporter prints a resource block on stdout at host disposal even when no metric was recorded, which would break the per-tier output of criterion 2; outside a dry run the exporter is on unless the flag is given; the OTLP checks run in both cases.)
BDD scenarios:
```gherkin
@unit @AJ-S054 @REQ-017
Feature: AJ-054 Add run --dry-run that validates everything and makes no completion call
  @positive
  Scenario: AJ-054 dry-run prints endpoint, matched IDs and OK per tier
    Given fake keys, fake models responses and a scripted GPU for tiers "local", "nvidia" and "nebius"
    When "run --tier all --dry-run" is executed
    Then the exit code is 0
    And each tier line shows the endpoint, the matched model IDs and "OK"
  @positive
  Scenario: AJ-054 help lists all seven options
    Given the run command
    When "run --help" is executed
    Then the output lists --tier, --task, --mode, --out, --baseline-seconds, --repeat and --dry-run
  @negative
  Scenario: AJ-054 unknown tier exits 2
    Given the run command
    When "run --tier bogus" is executed
    Then the exit code is 2
  @negative
  Scenario: AJ-054 invalid repeat count exits 2
    Given the run command
    When "run --tier nvidia --repeat 0 --dry-run" is executed
    Then the exit code is 2
  @negative
  Scenario: AJ-054 dry-run on local without a GPU exits 6
    Given the GPU port reports no device
    When "run --tier local --dry-run" is executed
    Then the exit code is 6
  @negative
  Scenario: AJ-054 dry-run makes no completion request
    Given fake keys and models responses for tier "nvidia"
    When "run --tier nvidia --dry-run" is executed
    Then no recorded request path ends with "/chat/completions"
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-054-T1 | Write the failing BDD feature and unit tests for run options and dry-run | tests/AgentJoules.Specs/Features/Cli/DryRun.feature, tests/AgentJoules.UnitTests/DryRunTests.cs | 0.5 | AJ-053-T1 |
| AJ-054-T2 | Define the `run` command and its options with allowed values and defaults, incl. `--no-console-telemetry`, and call `TelemetrySetup.Add` from the run host | src/AgentJoules.Cli/Commands/RunCommand.cs, src/AgentJoules.Cli/Hosting/AgentJoulesHostFactory.cs | 1.0 | AJ-054-T1 |
| AJ-054-T3 | Return exit 2 when `ParseResult.Errors` is non-empty before invoking any action | src/AgentJoules.Cli/CliApplication.cs | 0.75 | AJ-054-T2 |
| AJ-054-T4 | Implement `DryRunService` with the per-tier output lines and the local probe | src/AgentJoules.Workflow/Runs/DryRunService.cs | 1.25 | AJ-054-T2, AJ-053-T4 |
| AJ-054-T5 | Add the run options table and the dry-run example to the README | README.md | 0.5 | AJ-054-T4 |
Test mapping: `DryRunTests` (design class, trait `Spec=REQ-017`, with `Spec=EDGE-001` on the `run --tier local` missing-NVML case (exit 6) and `Spec=REQ-014` on the two console-exporter tests); feature file `tests/AgentJoules.Specs/Features/Cli/DryRun.feature`.
Docs impact: README section "Run" (options table and dry-run example).
Definition of done:
- [x] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings (2026-10-21: build 0 warnings exit 0 and 2,647/0/5)
- [x] docs listed above updated (2026-10-21: README `### run` subsection and this build-log entry)
- [x] `dotnet run --project src/AgentJoules.Cli -- run --tier bogus; echo $?` prints 2 (2026-10-21: dotnet run --project src/AgentJoules.Cli --no-build -- run --tier bogus exit 2, measured by the orchestrator)
Demo relevance: yes: step 2 of the demo is `AJ run --tier all --dry-run`.
Conflict: C-04 (design 9.2) - REQ-017 requires `AJ run --tier bogus` to exit 2 but System.CommandLine returns 1 on parse errors; resolution: `CliApplication` inspects `ParseResult.Errors`, prints them and returns 2 before any action runs.
Conflict: C-05 (design 9.2) - `--config` is defined per command (zero-or-one arity only on `validate`), so `run --config <path>` is a plain path option.

### AJ-055: Write run files atomically after every task and repeat
Covers: REQ-017
User story: As a user of a long or fragile run, I want the results file written after every (task, repeat) through a temporary file and an atomic move, with a run status, so that a crash or a rate-limit failure never leaves a truncated file and never loses finished steps.
Acceptance criteria:
1. The run file name is `results/<yyyyMMddTHHmmssZ>-<tier>[-baseline].json`, matching `^\d{8}T\d{6}Z-(local|cloudgpu|nvidia|nebius)(-baseline)?\.json$`; the `-baseline` suffix is used when any agent model ID does not contain `nemotron` (case-insensitive) (test `Run_file_name_rules`).
2. `ResultsFileWriter` writes `<name>.json.partial` and moves it onto `<name>.json`; a simulated failure during the write leaves the previous complete `<name>.json` intact (test `Failed_write_keeps_previous_file`).
3. `run.status` takes `running`, `completed`, `failed`, `partial` or `incomplete`; the first write has `running`.
4. Files are UTF-8 without BOM with LF newlines and invariant number formatting (test `File_is_utf8_no_bom_lf`: the first bytes are not EF BB BF and the file contains no CR).
5. The records `RunDocument`, `RunInfo`, `EnvironmentInfo`, `StepReading` and `Totals` exist in `AgentJoules.Core` with the camelCase names of the prompt skeleton, and `AgentJoulesJsonContext` registers `RunDocument`; M3 hardens these records and generates the schema.
6. A document with an `N/A` metric serialises `"value": null` (never 0).
7. An output directory that is not writable exits 1 with a one-line error before any call (test `Unwritable_out_dir_exits_1_before_calls`: exit code 1 and 0 recorded requests).
BDD scenarios:
```gherkin
@unit @AJ-S055 @REQ-017
Feature: AJ-055 Write run files atomically after every task and repeat
  @positive
  Scenario: AJ-055 run file name follows the committed pattern
    Given a run for tier "nvidia" starting at 2026-10-22 09:15:30 UTC
    When the run file name is created
    Then the name is "20261022T091530Z-nvidia.json"
  @positive
  Scenario: AJ-055 non-Nemotron model switches the baseline suffix on
    Given a run where one agent model ID does not contain "nemotron"
    When the run file name is created
    Then the name ends with "-baseline.json"
  @positive
  Scenario: AJ-055 first checkpoint has status running
    Given a started run
    When the first checkpoint is written
    Then the file parses and run.status is "running"
  @negative
  Scenario: AJ-055 failed write keeps the previous complete file
    Given a complete file from checkpoint 1
    When writing checkpoint 2 fails after the partial file was created
    Then the file still holds the content of checkpoint 1
  @negative
  Scenario: AJ-055 N/A metric is serialised with a null value
    Given a step whose cost is N/A with reason "free-tier-no-price"
    When the document is serialised
    Then the cost value is null and the label is "N/A"
    And the text "0" does not appear as the cost value
  @negative
  Scenario: AJ-055 unwritable output directory exits 1 before any call
    Given an output directory that cannot be written
    When "run --tier nvidia --out <dir> --repeat 1" is executed
    Then the exit code is 1
    And the fake handler recorded 0 requests
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-055-T1 | Write the failing BDD feature and unit tests for the writer and file names | tests/AgentJoules.Specs/Features/Reporting/RunCheckpoint.feature, tests/AgentJoules.UnitTests/RunCheckpointTests.cs | 0.5 | AJ-040-T2 |
| AJ-055-T2 | Create the `RunDocument` record tree per the prompt skeleton | src/AgentJoules.Core/Results/RunDocument.cs, src/AgentJoules.Core/Results/RunInfo.cs, src/AgentJoules.Core/Results/EnvironmentInfo.cs, src/AgentJoules.Core/Results/Totals.cs | 1.5 | AJ-055-T1 |
| AJ-055-T3 | Create `IRunCheckpointWriter`, `ResultsFileWriter` (with the writable-directory probe) and the minimal `AgentJoulesJsonContext` | src/AgentJoules.Core/Runs/IRunCheckpointWriter.cs, src/AgentJoules.Reporting/Results/ResultsFileWriter.cs, src/AgentJoules.Reporting/Json/AgentJoulesJsonContext.cs | 1.75 | AJ-055-T2 |
| AJ-055-T4 | Create `RunFileName` with the regex and the baseline suffix rule | src/AgentJoules.Reporting/Results/RunFileName.cs | 0.75 | AJ-055-T1 |
| AJ-055-T5 | Add the `.partial` ignore entry and note the file rules | .gitignore, docs/build-log.md | 0.25 | AJ-055-T3 |
Test mapping: feature file `tests/AgentJoules.Specs/Features/Reporting/RunCheckpoint.feature`; class `RunCheckpointTests` (trait `Spec=REQ-017`); `SchemaValidationTests` consumes the same records in M3.
Docs impact: `.gitignore` (`results/*.partial`); `docs/build-log.md`.
Definition of done:
- [x] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings (2026-10-20: build 0 warnings exit 0 and 2,452/0/5)
- [x] docs listed above updated (2026-10-20: `.gitignore` entry already present (line 501) and this build-log entry)
- [x] the checkpoint file survives a simulated write failure (scenario above) (2026-10-20: scenario "failed write keeps the previous complete file" green and `Failed_write_keeps_previous_file` plus the `[WindowsFact]` real-conflict test green)
Demo relevance: no.

### AJ-056: Orchestrate a tier run over tasks, repeats and modes
Covers: REQ-017, REQ-013, EDGE-005
Note (2026-10-21, AJ-054): the `run` command, its options, the dry run, the preflight order (keys, one results writer per tier, models, GPU) and the `IRunOrchestrator` seam with `RunPlan` exist; AJ-056 replaces the `RunOrchestrator` placeholder (exit 1 until then, pinned by `The_production_orchestrator_is_a_placeholder_that_exits_1`), registers `AddAgentJoulesMeterGate(mode)` and `AgentJoulesInstruments` through the run command's service hook, keeps `StepFailedException` free of provider text (the AJ-052 leak proof relies on it), re-points the exit-5 row of `ExitCodeTests.RunCommandCodes` at the production orchestrator, takes the run-file timestamp once at command start, prints the sampling-fallback line once, and decides whether the console exporter's resource block on stdout at the end of a measured run is acceptable.
User story: As a user, I want `AJ run --tier <t>` to run every selected task for the requested number of repeats in the requested mode and write one results file per tier, so that one command produces the evidence behind the report.
Acceptance criteria:
1. `AJ run` accepts `--tier`, `--task` (default all), `--mode serialized|concurrent` (default serialized), `--out` (default `results/`), `--baseline-seconds` (default 30, local only) and `--repeat` (default 3); a fake run with 3 tasks, `--repeat 2` and 4 agents yields 24 step readings (test `Run_produces_tasks_x_repeats_x_agents`).
2. Fresh clients and a fresh workflow are built for each (task, repeat); one checkpoint is written after each (test `Fresh_clients_per_task_repeat`: the count of created client sets equals tasks x repeats and the writer received that many checkpoints).
3. `--tier all` expands to the tiers with `Tiers:<t>:Enabled` true in the fixed order local, cloudgpu, nvidia, nebius and writes one file per tier; with the committed configuration (cloudgpu disabled) three files are written (test `All_writes_one_file_per_enabled_tier`).
4. Keys of all selected tiers are checked before the first call (test `All_tier_keys_checked_first`: exit code 3 and 0 recorded requests).
5. For a measured tier the orchestrator opens the GPU port, starts the meter, calls the (no-op in M2) warm-up seam, then measures the idle baseline for `--baseline-seconds`; hosted tiers use `UnmeteredEnergyMeter` and skip these steps.
6. The console prints one line per finished step: `<task> r<repeat> <agent> <outcome> netJ=<v|N/A> avgW=<v|N/A> in=<v|not-reported> out=<v|not-reported> latency=<ms>`, then the results file path and the totals; a missing value is never printed as 0.
7. The run ends with `run.status: "completed"` and exit 0; the judge seam is a no-op (M3 fills it); `--task bogus` exits 2.
8. On host shutdown (`ApplicationStopping`, Ctrl+C, SIGTERM) the orchestrator marks every in-flight step `outcome: "incomplete"`, sets `run.status: "incomplete"`, writes the checkpoint by atomic temp-file move (ADR-17) within the 15 s termination timeout and exits 5 (task AJ-056-T6a; test `Shutdown_marks_in_flight_steps_incomplete`: the file parses, `run.status` is `incomplete`, the in-flight step is `incomplete`, exit code 5). A run that completes normally never carries `incomplete` (test `Completed_run_has_no_incomplete`).
9. Carried from AJ-030 and AJ-034 (2026-10-19): the run prints the EDGE-005 warning through ConsoleReporter on the redacting stderr writer when the baseline is unstable and records `baseline-unstable` in the run document; test trait `Spec=EDGE-005`.
BDD scenarios:
```gherkin
@unit @AJ-S056 @REQ-017 @REQ-013
Feature: AJ-056 Orchestrate a tier run over tasks, repeats and modes
  @positive
  Scenario: AJ-056 run produces tasks x repeats x agents readings
    Given fake clients, 3 committed tasks and tier "nvidia"
    When "run --tier nvidia --repeat 2" is executed
    Then the results file has 24 steps
    And run.status is "completed" and the exit code is 0
  @positive
  Scenario: AJ-056 tier all writes one file per enabled tier in fixed order
    Given the committed configuration with cloudgpu disabled and fakes for the other tiers
    When "run --tier all --repeat 1" is executed
    Then 3 run files exist for tiers "local", "nvidia" and "nebius"
  @positive
  Scenario: AJ-056 step line uses labels instead of zero
    Given a nvidia step whose response has no usage
    When the run prints the step line
    Then the line contains "netJ=N/A", "in=not-reported" and "out=not-reported"
  @negative
  Scenario: AJ-056 unknown task name exits 2
    Given the committed tasks
    When "run --tier nvidia --task bogus" is executed
    Then the exit code is 2
  @negative
  Scenario: AJ-056 missing key for any selected tier stops before the first call
    Given NEBIUS_API_KEY is absent and tiers "nvidia" and "nebius" are selected
    When "run --tier all" is executed
    Then the exit code is 3
    And no run file exists and the fake handler recorded 0 requests
  @negative
  Scenario: AJ-056 invalid mode value exits 2
    Given the run command
    When "run --tier nvidia --mode parallel" is executed
    Then the exit code is 2
  @positive
  Scenario: AJ-056 Ctrl+C mid-step leaves an incomplete file and exits 5
    Given a nvidia run whose second step is in flight
    When the cancellation token is signalled
    Then the results file has run.status "incomplete" and the in-flight step has outcome "incomplete"
    And the file was written before the termination timeout and the exit code is 5
  @negative
  Scenario: AJ-056 a run that completes normally never carries incomplete
    Given a nvidia run that finishes without interruption
    When the results file is read
    Then run.status is "completed" and no step has outcome "incomplete"
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-056-T1 | Write the failing BDD feature and unit tests for orchestration | tests/AgentJoules.Specs/Features/Cli/RunOrchestration.feature, tests/AgentJoules.UnitTests/RunOrchestrationTests.cs | 0.5 | AJ-054-T1, AJ-055-T1 |
| AJ-056-T2 | Implement `RunOrchestrator.RunTierAsync` loops, per-run clients and checkpoints | src/AgentJoules.Workflow/Runs/RunOrchestrator.cs | 2.0 | AJ-056-T1, AJ-050-T6, AJ-055-T3 |
| AJ-056-T3 | Add the measured-tier start sequence (GPU port, meter start, warm-up seam, baseline) | src/AgentJoules.Workflow/Runs/RunOrchestrator.cs, src/AgentJoules.Workflow/Local/ILocalRuntime.cs | 1.0 | AJ-056-T2 |
| AJ-056-T4a | Create `RunAssembler` with serialized totals, token and cost-per-currency sums, `wallClockMs`, hosted-tier energy `N/A`, `run.mode` and the `ReportSentences` constants | src/AgentJoules.Workflow/Runs/RunAssembler.cs, src/AgentJoules.Core/Results/ReportSentences.cs | 1.75 | AJ-056-T1, AJ-055-T2 |
| AJ-056-T4 | Fill `environment` and `run` fields available without a local runtime in `RunAssembler` | src/AgentJoules.Workflow/Runs/RunAssembler.cs | 1.5 | AJ-056-T2, AJ-056-T4a |
| AJ-056-T5 | Implement `--tier all` expansion and the all-tier key check | src/AgentJoules.Workflow/Runs/RunOrchestrator.cs, src/AgentJoules.Workflow/Runs/PreflightService.cs | 0.75 | AJ-056-T2 |
| AJ-056-T6 | Implement `ConsoleReporter` step lines and the closing summary | src/AgentJoules.Cli/Output/ConsoleReporter.cs | 0.75 | AJ-056-T2 |
| AJ-056-T6a | On `ApplicationStopping` or the CLI cancellation token, mark every in-flight step `outcome: incomplete`, set `run.status` to `incomplete`, write the checkpoint by atomic temp-file move within the 15 s termination timeout, and map the exit code to 5 | src/AgentJoules.Workflow/Runs/RunOrchestrator.cs, src/AgentJoules.Cli/ExitCodeMapper.cs | 0.75 | AJ-056-T2, AJ-055-T3 |
| AJ-056-T7 | Wire the `run` action and the `IJudgeService` no-op seam | src/AgentJoules.Cli/Commands/RunCommand.cs, src/AgentJoules.Workflow/Judge/IJudgeService.cs | 0.75 | AJ-056-T5, AJ-056-T6 |
| AJ-056-T8 | Document `--tier all` semantics and the fixed tier order | README.md | 0.25 | AJ-056-T7 |
Test mapping: class `RunOrchestrationTests` (trait `Spec=REQ-017`) and `ExecutionModeTests` for the mode wiring; feature file `tests/AgentJoules.Specs/Features/Cli/RunOrchestration.feature`.
Docs impact: README section "Run" (`--tier all` expands to enabled tiers; cloudgpu runs on the VM).
Definition of done:
- [ ] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [ ] a fake `--tier all` run writes the expected number of run files
Demo relevance: yes: step 3 of the demo is `AJ run --tier all`.
Conflict: C-06 (design 9.2) - `--tier all` versus `cloudgpu` running only on the VM; resolution (ADR-13): `all` expands to the tiers enabled in configuration in the fixed order local, cloudgpu, nvidia, nebius; cloudgpu is disabled in the committed file and enabled on the VM by `AGENTJOULES__Tiers__cloudgpu__Enabled=true`.

### AJ-057: Stop on a failed step, keep partial results and exit 5
Covers: REQ-017, EDGE-006
User story: As a user, I want a failed workflow step to stop the tier, keep the partial results file and exit 5, so that a rate-limit exhaustion or an unreachable endpoint never discards finished steps and never looks like success.
Acceptance criteria:
1. With an endpoint that answers 429 on every call the fake handler records exactly 5 HTTP requests, `AJ run` exits 5 and the results file contains the failed step with `outcome: "error"` (test `Exhausted_429_writes_partial_file_and_exits_5`).
2. The file has `run.status: "failed"`; steps finished before the failure are kept unchanged; steps that never ran are absent, not zero-filled.
3. A 500, a timeout or a connection refusal on a hosted endpoint also ends the tier with `outcome: "error"`, status `failed` and exit 5; no other status code is retried.
4. With `--tier all` the first failing tier stops the command with its exit code; files already written for earlier tiers remain.
5. The final console line names the partial file path and the failing agent.
BDD scenarios:
```gherkin
@unit @AJ-S057 @REQ-017 @EDGE-006
Feature: AJ-057 Stop on a failed step, keep partial results and exit 5
  @positive
  Scenario: AJ-057 steps finished before a failure stay in the partial file
    Given a nvidia run where the Reviewer of the second task fails after the first task completed
    When "run --tier nvidia --repeat 1" is executed
    Then the results file holds the 4 steps of the first task unchanged
  @positive
  Scenario: AJ-057 earlier tier files remain when a later tier fails
    Given tiers "nvidia" and "nebius" where nebius answers 429 on every call
    When "run --tier all --repeat 1" is executed
    Then the nvidia run file exists with status "completed"
    And the exit code is 5
  @negative
  Scenario: AJ-057 exhausted 429 retries write a failed step and exit 5
    Given an endpoint that answers 429 on every call
    When "run --tier nvidia --repeat 1" is executed
    Then exactly 5 HTTP requests were recorded for the first step
    And the results file has a step with outcome "error" and run.status "failed"
    And the exit code is 5
  @negative
  Scenario: AJ-057 a 500 response ends the tier with exit 5 and no retry
    Given an endpoint that answers 500
    When "run --tier nebius --repeat 1" is executed
    Then exactly 1 HTTP request was recorded
    And the exit code is 5
  @negative
  Scenario: AJ-057 steps that never ran are absent not zero
    Given a run that fails on its first step
    When the results file is read
    Then it contains 1 step and no step with a numeric 0 for tokens or joules
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-057-T1 | Write the failing BDD feature and unit tests for step failure and partial files | tests/AgentJoules.Specs/Features/Cli/StepFailure.feature, tests/AgentJoules.UnitTests/RetryPolicyTests.cs | 0.5 | AJ-056-T1, AJ-045-T1 |
| AJ-057-T2 | Stop the tier on an `error` outcome, set status `failed` and checkpoint | src/AgentJoules.Workflow/Runs/RunOrchestrator.cs | 1.0 | AJ-057-T1, AJ-056-T2 |
| AJ-057-T3 | Extend `StepFailedException` (created in AJ-050-T5a) and reuse `EndpointUnreachableException` and `ModelNotFoundException` (created in AJ-053-T4) | src/AgentJoules.Core/Cli/AgentJoulesException.cs | 0.5 | AJ-057-T2 |
| AJ-057-T4 | Stop `--tier all` at the first failing tier and keep earlier files | src/AgentJoules.Workflow/Runs/RunOrchestrator.cs | 0.75 | AJ-057-T2 |
| AJ-057-T5 | Print the partial file path and the failing agent | src/AgentJoules.Cli/Output/ConsoleReporter.cs | 0.5 | AJ-057-T3 |
| AJ-057-T6 | Record the `Spec=EDGE-006` end-to-end result in the build log | docs/build-log.md | 0.25 | AJ-057-T4 |
Test mapping: `RetryPolicyTests` (design class, trait `Spec=EDGE-006`); feature file `tests/AgentJoules.Specs/Features/Cli/StepFailure.feature`.
Docs impact: `docs/build-log.md`; the README troubleshooting entry for exit 5 is written in M6.
Definition of done:
- [ ] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [ ] `dotnet test --filter "Spec=EDGE-006"` shows at least one passed and no failed test
Demo relevance: no.

### AJ-058: Map every failure to the documented exit code
Covers: REQ-017, EDGE-001
Note (2026-10-20, M1 exit): EDGE-001 added per plan section 5 seam row 5 (`run --tier local` with the missing-NVML fake carries the EDGE-001 trait) and the M1 spec full pass, so the trait is not lost.
User story: As a script author, I want each failure class to end in a fixed exit code with one redacted error line, so that automation can react to missing keys, unknown models, unreachable endpoints and missing GPUs without parsing text.
Acceptance criteria:
1. `ExitCode` is the enum 0 success, 1 unexpected, 2 invalid arguments or configuration, 3 missing API key, 4 model not found, 5 unreachable or retries exhausted, 6 measurement precondition failed, 7 generated content differs.
2. One test per code 1 to 7 runs through `CliHarness` (test class `ExitCodeTests`): 1 by a service override that throws `InvalidOperationException`; 2 by `--tier bogus`; 3 by an empty key set; 4 by a model absent from the fake models response; 5 by a refused connection; 6 by an unavailable GPU factory on `--tier local`; 7 by a service override that throws `ContentDiffersException`.
3. Every failure prints exactly one line `error: <message> (<exception type>)` to stderr, redacted; the stack trace appears only at Debug level.
4. `OptionsValidationException` maps to 2; an unknown exception maps to 1.
5. Code 7 is proven here by the mapper and the override; the real `report --verify-readme` path is added when that flag is built in M6.
BDD scenarios:
```gherkin
@unit @AJ-S058 @REQ-017
Feature: AJ-058 Map every failure to the documented exit code
  @positive
  Scenario: AJ-058 exception classes carry their documented code
    Given the exception types ConfigurationException, MissingKeyException, ModelNotFoundException, EndpointUnreachableException, MeasurementPreconditionException and ContentDiffersException
    When each is mapped by the exit code mapper
    Then the codes are 2, 3, 4, 5, 6 and 7 in that order
  @positive
  Scenario: AJ-058 successful command exits 0
    Given fake keys and fake endpoints
    When "run --tier nvidia --dry-run" is executed
    Then the exit code is 0
  @negative
  Scenario Outline: AJ-058 failure class maps to its exit code
    Given the failure "<failure>"
    When the command runs through the harness
    Then the exit code is <code>
    And stderr has exactly one line starting with "error:"

    Examples:
      | failure                         | code |
      | unexpected exception            | 1    |
      | unknown tier value              | 2    |
      | no API key                      | 3    |
      | model absent from models list   | 4    |
      | connection refused              | 5    |
      | GPU unavailable on local        | 6    |
      | content differs exception       | 7    |
  @negative
  Scenario: AJ-058 error line is redacted
    Given a failing endpoint and a runtime-built key starting with "nvapi-"
    When the command fails
    Then stderr does not contain any character of the key after the nvapi- prefix
  @negative
  Scenario: AJ-058 stack trace is hidden at the default log level
    Given an unexpected exception
    When the command fails at the default log level
    Then stderr has no stack frame lines
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-058-T1 | Write the failing BDD feature and the seven-code unit test class | tests/AgentJoules.Specs/Features/Cli/ExitCodes.feature, tests/AgentJoules.UnitTests/ExitCodeTests.cs | 0.75 | AJ-054-T1 |
| AJ-058-T2 | Extend the `ExitCode` enum (created in AJ-027-T2) and the exception hierarchy | src/AgentJoules.Core/Cli/ExitCode.cs, src/AgentJoules.Core/Cli/AgentJoulesException.cs | 0.75 | AJ-058-T1 |
| AJ-058-T3 | Implement `ExitCodeMapper` and the one-line error output | src/AgentJoules.Cli/ExitCodeMapper.cs, src/AgentJoules.Cli/Output/ConsoleReporter.cs | 0.75 | AJ-058-T2 |
| AJ-058-T4 | Wire the mapper into `CliApplication` and add the `Log.cs` partial methods | src/AgentJoules.Cli/CliApplication.cs, src/AgentJoules.Cli/Log.cs | 0.75 | AJ-058-T3 |
| AJ-058-T5 | Verify the code-to-meaning mapping in code (`ExitCodeTests` asserts the meaning text per code) and add rows to the README exit-code table only if AJ-131-T4 has already created it | src/AgentJoules.Cli/ExitCodeMapper.cs, README.md | 0.25 | AJ-058-T4 |
Test mapping: `ExitCodeTests` (design class, trait `Spec=REQ-017`, with `Spec=EDGE-001` on the `run --tier local` missing-NVML case (exit 6)); feature file `tests/AgentJoules.Specs/Features/Cli/ExitCodes.feature`.
Docs impact: README section "Exit codes" is created by AJ-131-T4 in M6; this story only verifies the code-to-meaning mapping in code and adds rows if the table already exists (then the table exists exactly once).
Definition of done:
- [ ] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [ ] `dotnet test --filter "Spec=REQ-017"` shows at least one passed and no failed test
Demo relevance: no.

## Risks

| Risk | Trigger | Mitigation | Owner |
|---|---|---|---|
| Agent Framework workflow API differs from the design notes (executor generics, `OnMessageDeliveryFinishedAsync`) | AJ-050-T5 or T6 does not compile against 1.23.0 | cap the discovery at 1 hour inside AJ-050-T4, read the fork source named in the design, record the deviation in `docs/build-log.md`; do not upgrade packages | builder |
| 429 test double does not reproduce the SDK exception shape | AJ-045 handler-level 429 does not surface as the retried exception | drive 429 through `FakeHttpMessageHandler` and the real OpenAI SDK (design choice), not through a hand-made exception | builder |
| Ollama or a hosted endpoint omits streaming usage | tokens read `not-reported` in the live runs | already a first-class label (AJ-042); M4 records the finding per tier in `docs/endpoints.md` | builder |
| Reasoning-off request JSON is unverified per endpoint (design O-02) | live dry-run in M4 shows reasoning tokens | configuration-only fix through `ExtraBodyJson`; no code change | builder |
| Wall-clock timing makes `ExecutionModeTests` flaky on slow CI | overlap assertions fail intermittently | 1 second branches, assert overlap only, never exact durations (design R-09) | builder |
| `CliHarness` leaks the developer's real keys into tests | a missing-key test passes only on a clean machine | `CliHostOverrides` omits environment and user secrets unless requested (AJ-052-T2) | builder |
| Estimate exceeds the nominal three-day capacity | the sum in Estimate is larger than 18 hours | see Estimate: cut order and a request for the user to confirm builder parallelism | product owner |
| Keys (steps 5, 6) arrive late | live dry-run criterion cannot run on 2026-10-23 | item recorded BLOCKED and carried to M4; offline criteria unaffected | user |

## Estimate

Note (2026-10-18): any deferral or descope candidates named in this section are informational; the drop order in docs/plan/plan.md section 3.4 is the single source and classifies stories as MUST, SHOULD or COULD.

Sum of task estimates: 81.75 hours across 19 stories and 113 tasks (per story: AJ-040 2.75, AJ-041 4.75, AJ-042 3.0, AJ-043 4.0, AJ-044 5.75, AJ-045 5.0, AJ-046 2.75, AJ-047 4.0, AJ-048 3.25, AJ-049 3.0, AJ-050 5.75, AJ-051 4.25, AJ-052 5.0, AJ-053 3.0, AJ-054 4.0, AJ-055 4.75, AJ-056 10.0, AJ-057 3.5, AJ-058 3.25).

Nominal capacity: 3 working days x 6 hours = 18 hours (2026-10-21 to 2026-10-23). The sum is about 4.5 times that capacity (81.75 / 18). This is an unresolved planning gap that needs a decision by the coordinator or the user; cutting inside M2 cannot close it. The cut candidates are AJ-051 telemetry (4.25 h, keep only the `UseOpenTelemetry` grep), AJ-047 concurrent union totals (4.0 h, the demo runs serialized; the serialized totals, token and cost sums and `ReportSentences` now live in AJ-056-T4a, so no other story depends on AJ-047) and merging AJ-052 with AJ-053 (about 1 h saved); together about 9 hours, which leaves about 72 hours (81.75 - 9). REQ-014 and EDGE-004 must still be met before the Definition of Done, so a cut is a deferral to M4, not a removal. Other levers: extend the M2 calendar, or run independent stories in parallel sub-agents (assumption, throughput not measured).
