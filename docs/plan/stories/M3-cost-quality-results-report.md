# M3: Cost, quality, results, report

Goal: turn the per-step readings of M2 into publishable, honestly labelled evidence. Cost is computed per currency (local electricity in EUR, hosted tokens in USD, cloudgpu rental in USD, N/A where no price exists); a hosted judge scores each step 1 to 5 against a committed rubric and a judge failure never changes the exit code; joules and cost per quality point exist only when both inputs exist; the results JSON model, its generated schema, the drift test and `AJ validate` make every run file checkable; `AJ report` renders the Markdown summary, the SVG chart (N/A drawn as a text marker, never an empty bar), the static HTML page for the demo URL and the README results block. It adds the optional live metering dashboard (loopback Server-Sent Events page fed through bounded, drop-oldest buffers) and its replay of a committed run file, which can never alter a measurement or a results file. The milestone ends by proving the six required unit test classes and the gated integration suite. Everything runs with `FakeChatClient`, `ScriptedNvmlApi`, `FakeHttpMessageHandler` and the in-process `CliHarness`: no GPU, network or key is needed.

## Dates and exit criteria

Dates: 2026-10-23 to 2026-10-25 (three working days, about 18 hours at the assumed six hours per day; see Estimate for the gap).

Exit criteria:
- [ ] `dotnet build -warnaserror -tl:off --no-incremental` exits 0 with 0 warnings and 0 errors.
- [ ] For each ID in REQ-009, REQ-010, REQ-015, REQ-016, REQ-030, REQ-032, EDGE-012, EDGE-018, EDGE-019, the command `dotnet test --filter "Spec=<ID>" --logger "console;verbosity=normal"` shows at least one passed test and no failed test; the nine results are recorded in `docs/build-log.md`.
- [ ] `dotnet test --list-tests` output contains `MeteredChatClientTests`, `AttributionBaselineTests`, `CostModelTests`, `JudgeParsingTests`, `ReportRenderingTests` and `SchemaValidationTests`; with `AGENTJOULES_INTEGRATION`, `NVIDIA_API_KEY` and `NEBIUS_API_KEY` unset `dotnet test` exits 0 with 0 failed and the integration tests are reported as skipped.
- [ ] `dotnet test --filter "FullyQualifiedName~CommittedSchemaMatchesGenerated"` exits 0 (the committed `docs/results-schema.json` equals the generated schema).
- [ ] `dotnet run --project src/AgentJoules.Cli -- validate tests/AgentJoules.TestSupport/Fixtures/results` prints `Validated 3 files` and exits 0; the same command on a directory holding one corrupted fixture exits 2.
- [ ] `dotnet run --project src/AgentJoules.Cli -- report --in tests/AgentJoules.TestSupport/Fixtures/results --out "$TMPDIR/report"` exits 0 and writes `summary.md`, `joules-per-step.svg` and `index.html`; `grep -c "<script" "$TMPDIR/report/index.html"` prints 0; `grep -c "joules-per-step.svg" "$TMPDIR/report/index.html"` prints at least 1; `grep -c "GPU-board energy only" "$TMPDIR/report/index.html"` prints at least 1; `docs/.nojekyll` exists.
- [ ] `grep -c "<text[^>]*>N/A</text>" "$TMPDIR/report/joules-per-step.svg"` prints at least 1 for the hosted-tier fixture, and `git grep -nEi "oxyplot|scottplot|livecharts|plotly|playwright|puppeteer|selenium" -- "*.csproj" "Directory.Packages.props"` returns no hit.
- [ ] `config/prices.json` validates against `config/prices.schema.json` and `config/judge-rubric.md` holds the five levels.
- [ ] `dotnet run --project src/AgentJoules.Cli -- dashboard --replay tests/AgentJoules.TestSupport/Fixtures/results/20260101T000000Z-nvidia.json --speed 50 --exit-after 5 --start-delay 0; echo $?` prints 0; `AJ run --tier local --dashboard --dry-run` (fake GPU in the harness) prints a line starting `Dashboard:` with `http://127.0.0.1:` and exits 0; `AJ run --help` lists nine options.
- [ ] Carried forward, not blocking: `AJ validate results/` printing a count equal to the number of committed run files, and `AJ report --in results/ --out docs/report/` on real runs, happen after the real runs exist (M4 and M6).

## Coverage

REQ: REQ-009, REQ-010, REQ-015, REQ-016, REQ-023, REQ-030, REQ-032.
EDGE: EDGE-012, EDGE-018, EDGE-019.

## Dependencies and user actions

Needs from M0: solution, central package pins including `JsonSchema.Net` 8.0.5, test projects, `AgentJoules.TestSupport`, `.gitignore` entries.
Needs from M1: `IntegrationGate` and `[IntegrationFact]` in `AgentJoules.TestSupport` (AJ-007-T4a (M0, creates the single IntegrationFactAttribute) and AJ-023-T2 (M1, points it at IntegrationGate)), `Metric` label object, `ReadingCalculator`, `DeterministicEnergyMeter`, `ScriptedNvmlApi`, `ProbeResult` and the `probe` command.
Needs from M2 (all assumed delivered): `StepReading`, `IMetricsSink`, `ICostCalculator` with `NotConfiguredCostCalculator`, `IJudgeService` no-op seam, `RunAssembler`, `RunOrchestrator`, `RunDocument` records with the minimal `AgentJoulesJsonContext`, `ResultsFileWriter`, `RunFileName`, `ReportSentences`, `ExitCode` and `ExitCodeMapper`, `CliHarness`, `FakeChatClient`, `FakeHttpMessageHandler`.
Needs from M1 also: `SampledEnergyMeter` samples that the dashboard forwards without an extra NVML call.
Needs from M2 also: `MeteredChatClient` step-started hook point and `RunOrchestrator` run, baseline and progress emit points for the live events.
Hands over: the committed schema, `AJ validate`, the renderers and `ReadmeResultsBlock` that M6 wires to `--update-readme` and `--verify-readme`; the judge service that M4 exercises live.

User actions from prompt section 12: none needed for the offline criteria. Step 6 (`NVIDIA_API_KEY`) is the default judge key and matters only for live judging in M4 and later. The Nebius price lookup in AJ-070 uses the public pricing page and needs no account.

## Stories

Note (2026-10-22, M3 session): feature files follow the repository convention from M1, tests/AgentJoules.Specs/Features/<Capability>/AJ-0nn-<slug>.feature; the file names in the story tables (for example PriceTable.feature) map to that form.

### AJ-070: Load a price table that cannot represent a zero price
Covers: REQ-009
Note (2026-10-22, AJ-070): feature file `tests/AgentJoules.Specs/Features/Reporting/AJ-070-price-table.feature` (the table's PriceTable.feature, same content as the story block); pinned P-a to P-f applied (row shape and schema; loader in Core without JsonSchema.Net, exit 2 messages never echo a value; committed file checked through schema and loader; positional PriceRow and sealed PriceTable with Rows and Find; Specs call PriceTable.Load directly; lookup with verbatim evidence, no zero price, USD only); the nebius row takes its sourceUrl from the Token Factory model catalog `https://tokenfactory.nebius.com/model-catalog.md` (fetched 2026-10-22), because the pricing page is a script-rendered single-page app with no price text; currency USD inferred; no nvidia row (no published price on build.nvidia.com); the .cs files end without a final newline per .editorconfig.
User story: As a reader of cost numbers, I want every price to come from a committed table with its source URL and retrieval date, so that no cost is based on an unsourced or zero price.
Acceptance criteria:
1. `config/prices.schema.json` requires `tier`, `modelId`, `inputUsdPer1M`, `outputUsdPer1M`, `sourceUrl` and `retrievedDate` on every row, with both price fields `exclusiveMinimum: 0`; the test `Committed_prices_validate` in `CostModelTests` validates the committed `config/prices.json` against it.
2. A row without `sourceUrl` or `retrievedDate`, with a price of 0 or below, or with a malformed date is rejected and the load exits 2 naming the row index and the field.
3. Two rows with the same `(tier, modelId)` exit 2.
4. `PriceTable.Find(tier, modelId)` returns the row or null (null leads to cost N/A in AJ-071).
5. The `nvidia` tier has no row unless the builder finds a published price (then `sourceUrl` and `retrievedDate` are recorded); the `nebius` row comes from the Nebius Token Factory pricing page with its URL and retrieval date; if the page cannot be read there is no row and `docs/build-log.md` has a `price-not-found` entry. No price is ever invented and 0 is never written.
BDD scenarios:
```gherkin
@unit @AJ-S070 @REQ-009
Feature: AJ-070 Load a price table that cannot represent a zero price
  @positive
  Scenario: AJ-070 valid row loads and is found by tier and model
    Given a price file with one nebius row that has both prices, a source URL and a retrieval date
    When the price table is loaded and "nebius" with that model ID is looked up
    Then the row is returned
  @positive
  Scenario: AJ-070 model without a row is not found
    Given a price file with one nebius row
    When "nvidia" with another model ID is looked up
    Then the lookup returns no row
  @negative
  Scenario: AJ-070 row without a source URL is rejected
    Given a price file whose only row has no sourceUrl
    When the price table is loaded
    Then the exit code is 2
    And the output names row 0 and the field "sourceUrl"
  @negative
  Scenario: AJ-070 zero price is rejected
    Given a price file whose only row has inputUsdPer1M 0
    When the price table is loaded
    Then the exit code is 2
    And the output names the field "inputUsdPer1M"
  @negative
  Scenario: AJ-070 duplicate tier and model rows are rejected
    Given a price file with two rows for the same tier and model ID
    When the price table is loaded
    Then the exit code is 2
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-070-T1 | Write the failing BDD feature and unit tests for the price table | tests/AgentJoules.Specs/Features/Reporting/PriceTable.feature, tests/AgentJoules.UnitTests/CostModelTests.cs | 0.5 | - |
| AJ-070-T2 | Author the price row schema | config/prices.schema.json | 0.75 | AJ-070-T1 |
| AJ-070-T3 | Implement `PriceTable.Load` and `Find` with schema validation and duplicate check | src/AgentJoules.Core/Cost/PriceTable.cs | 1.0 | AJ-070-T2 |
| AJ-070-T4 | Look up the Nebius Token Factory price and the nvidia price, then write the rows that have a source | config/prices.json | 1.0 | AJ-070-T3 |
| AJ-070-T5 | Record each lookup outcome (URL, date, or `price-not-found`) in the build log | docs/build-log.md | 0.25 | AJ-070-T4 |
Test mapping: `CostModelTests` (required test class, trait `Spec=REQ-009`); feature file `tests/AgentJoules.Specs/Features/Reporting/PriceTable.feature`.
Docs impact: `config/prices.json`, `config/prices.schema.json`, `docs/build-log.md`; the README prices note is written in M6.
Definition of done:
- [x] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [x] docs listed above updated
- [x] every row in `config/prices.json` has `sourceUrl` and `retrievedDate` (validated by `Committed_prices_validate`)
Demo relevance: no (prices feed the cost column of the report).

### AJ-071: Compute hosted token cost in USD or label it N/A
Covers: REQ-009
Note (2026-10-22, AJ-071): feature file `tests/AgentJoules.Specs/Features/Reporting/AJ-071-hosted-cost.feature` (the table's HostedCost.feature, same content as the story block); pinned P-a to P-f applied (cost = count x price per 1M in decimal, once to double, USD; row lookup first, then token counts, then arithmetic; `price-not-representable` for a product that is 0 although a token was counted, non-finite or overflowing; local and cloudgpu stay N/A `cost-not-configured` until AJ-072; `Paths:Prices` through `ContentPaths.Resolve`, lazy, exit 2 with key-only messages; Specs call `CostCalculator` directly); D1, a stated 0/0 token step costs an estimated 0 USD rather than N/A because the counts exist (accepted reading, reviewers may contest); the registration lives in Workflow's `AddAgentJoulesCost`, called by the host factory with the content root and resolved eagerly by `run` after the task load, and the task table's `ServiceCollectionExtensions.cs` is matched; battery 1 17:57:58Z to 17:59:25Z: 3108 passed, 0 failed, 4 skipped, REQ-009 151 of 151.
User story: As a reader of hosted-tier results, I want token cost computed from the price table and labelled `estimated` in USD, or N/A with a reason, so that a free tier is never shown as a price of zero.
Acceptance criteria:
1. 1 000 000 input and 500 000 output tokens at 0.10 and 0.40 USD per 1M give `{value:0.30,label:"estimated",currency:"USD"}` (test `Hosted_cost_example`).
2. A model without a price row gives `{value:null,label:"N/A"}` with `reason` taken from `Tiers:<t>:MissingPriceReason`: `free-tier-no-price` for `nvidia`, `price-not-found` otherwise (test `Nvidia_without_row_is_NA_not_zero`).
3. A call whose tokens are not reported (either count) gives N/A with reason `tokens-not-reported`.
4. Each agent's model ID is priced from its own row when `AgentModelIds` overrides the tier model.
5. `CostCalculator` replaces `NotConfiguredCostCalculator` in the service collection; every cost carries its currency and the label `estimated` or `N/A`, never `measured`.
BDD scenarios:
```gherkin
@unit @AJ-S071 @REQ-009
Feature: AJ-071 Compute hosted token cost in USD or label it N/A
  @positive
  Scenario: AJ-071 one million input and half a million output tokens cost 0.30 USD
    Given a price row with 0.10 USD per 1M input and 0.40 USD per 1M output tokens
    When the cost of 1000000 input and 500000 output tokens is computed
    Then the cost is 0.30 with label "estimated" and currency "USD"
  @positive
  Scenario: AJ-071 a per-agent model override uses its own price row
    Given rows for models "model-a" and "model-b" with different prices
    When the Writer uses "model-b" and the Planner uses "model-a"
    Then each cost is computed from its own model row
  @negative
  Scenario: AJ-071 nvidia free tier without a row is N/A with its reason
    Given no price row for tier "nvidia"
    When the cost of a nvidia step is computed
    Then the cost has label "N/A", reason "free-tier-no-price" and a null value
  @negative
  Scenario: AJ-071 missing nebius row is N/A with the default reason
    Given no price row for the nebius model
    When the cost of a nebius step is computed
    Then the cost has label "N/A", reason "price-not-found" and a null value
  @negative
  Scenario: AJ-071 unreported tokens give N/A not zero
    Given a price row exists and the output tokens are not-reported
    When the cost is computed
    Then the cost has label "N/A", reason "tokens-not-reported" and a null value
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-071-T1 | Write the failing BDD feature and unit tests for hosted cost | tests/AgentJoules.Specs/Features/Reporting/HostedCost.feature, tests/AgentJoules.UnitTests/CostModelTests.cs | 0.5 | AJ-070-T1 |
| AJ-071-T2 | Implement the hosted branch of `CostCalculator` | src/AgentJoules.Core/Cost/CostCalculator.cs | 1.0 | AJ-071-T1, AJ-070-T3 |
| AJ-071-T3 | Map missing-row reasons from `Tiers:<t>:MissingPriceReason` | src/AgentJoules.Core/Configuration/TierOptions.cs, src/AgentJoules.Core/Cost/CostCalculator.cs | 0.5 | AJ-071-T2 |
| AJ-071-T4 | Register `CostCalculator` in place of `NotConfiguredCostCalculator` | src/AgentJoules.Workflow/ServiceCollectionExtensions.cs | 0.5 | AJ-071-T2 |
| AJ-071-T5 | Record the `Spec=REQ-009` hosted-cost results in the build log | docs/build-log.md | 0.25 | AJ-071-T4 |
Test mapping: `CostModelTests` (required test class, trait `Spec=REQ-009`); feature file `tests/AgentJoules.Specs/Features/Reporting/HostedCost.feature`.
Docs impact: `docs/build-log.md`; README cost note (estimated, per currency, no conversion) is written in M6.
Definition of done:
- [x] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [x] docs listed above updated
- [x] `dotnet test --filter "Spec=REQ-009"` shows at least one passed and no failed test
Demo relevance: yes: hosted rows in the demo table show cost in USD or an explicit N/A, never 0.

### AJ-072: Compute local electricity cost in EUR and cloudgpu rental cost in USD
Covers: REQ-009
Note (2026-10-22, AJ-072): feature file `tests/AgentJoules.Specs/Features/Reporting/AJ-072-local-and-cloud-cost.feature` (the story table's `LocalAndCloudCost.feature`, the story block verbatim). Pinned readings applied: a below-baseline step is N/A and keeps its negative joules (reading P1), cloudgpu cost is the hourly price times wall seconds over 3600 in USD with no electricity model (ADR-15), and `totals.gco2e` and the assumptions follow the factor rule with `electricityPrice` always labelled estimated. Deviations: the Core test `The_measured_tiers_stay_cost_not_configured_until_the_local_and_cloudgpu_branches_exist` is renamed to `The_measured_tiers_answer_with_their_own_reason_and_no_longer_cost_not_configured`, two M2 run-document assertions are re-anchored to the estimated price and the N/A `not-configured` factor, and the feature file name follows the repository convention.
User story: As a reader comparing tiers, I want local cost from net energy times a labelled electricity price and cloudgpu cost from the VM rental price, each in its own currency, so that currencies are never mixed and every cost is marked as an estimate.
Acceptance criteria:
1. 3 600 000 J net at 0.30 EUR/kWh gives `{value:0.30,label:"estimated",currency:"EUR"}` (test `Local_cost_example`); the price and currency come from `Cost:ElectricityPricePerKwh` and `Cost:Currency`.
2. A step with energy N/A gives cost N/A with reason `energy-not-measured`.
3. Assumption to confirm with the architect: a step flagged `below-baseline` (negative net joules) gives cost N/A with reason `below-baseline`, because a negative cost is meaningless; the negative joules stay in the reading.
4. cloudgpu cost = `CloudGpu:HourlyPriceUsd` x step wall seconds / 3600, `estimated`, currency USD (fixture: 3.60 USD per hour and 1000 s give 1.00 USD); an unset price gives N/A with reason `hourly-price-not-configured`.
5. `Cost:GCo2ePerKwh` null gives `totals.gco2e` N/A with reason `not-configured`; a set factor gives net kWh x factor labelled `estimated`.
6. `environment.assumptions.electricityPrice` is labelled `estimated` and `assumptions.gco2ePerKwh` is `estimated` or N/A `not-configured`; the README labels the electricity price as an assumption.
BDD scenarios:
```gherkin
@unit @AJ-S072 @REQ-009
Feature: AJ-072 Compute local electricity cost in EUR and cloudgpu rental cost in USD
  @positive
  Scenario: AJ-072 3.6 million joules net at 0.30 EUR per kWh cost 0.30 EUR
    Given net energy of 3600000 J and an electricity price of 0.30 EUR per kWh
    When the local cost is computed
    Then the cost is 0.30 with label "estimated" and currency "EUR"
  @positive
  Scenario: AJ-072 cloudgpu cost is the hourly price times wall time
    Given an hourly price of 3.60 USD and a step wall time of 1000 seconds
    When the cloudgpu cost is computed
    Then the cost is 1.00 with label "estimated" and currency "USD"
  @positive
  Scenario: AJ-072 emissions are estimated when a factor is configured
    Given net energy of 3600000 J and a factor of 400 gCO2e per kWh
    When emissions are computed
    Then totals gco2e is 400 with label "estimated"
  @negative
  Scenario: AJ-072 energy N/A gives cost N/A
    Given a local step whose energy is N/A with reason "counter-and-sampling-unavailable"
    When the local cost is computed
    Then the cost has label "N/A", reason "energy-not-measured" and a null value
  @negative
  Scenario: AJ-072 below-baseline step has no cost
    Given a local step with negative net joules and flag "below-baseline"
    When the local cost is computed
    Then the cost has label "N/A" and reason "below-baseline"
    And the net joules remain negative in the reading
  @negative
  Scenario: AJ-072 unset hourly price gives cloudgpu cost N/A
    Given CloudGpu:HourlyPriceUsd is not configured
    When the cloudgpu cost is computed
    Then the cost has label "N/A" and reason "hourly-price-not-configured"
  @negative
  Scenario: AJ-072 unset emission factor gives emissions N/A
    Given Cost:GCo2ePerKwh is null
    When the run totals are assembled
    Then totals gco2e has label "N/A" and reason "not-configured"
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-072-T1 | Write the failing BDD feature and unit tests for local, cloudgpu and emission cost | tests/AgentJoules.Specs/Features/Reporting/LocalAndCloudCost.feature, tests/AgentJoules.UnitTests/CostModelTests.cs | 0.5 | AJ-071-T1 |
| AJ-072-T2 | Implement the local electricity branch including the below-baseline rule | src/AgentJoules.Core/Cost/CostCalculator.cs | 0.75 | AJ-072-T1, AJ-071-T2 |
| AJ-072-T3 | Add the minimal `CloudGpuOptions.HourlyPriceUsd` and the cloudgpu branch | src/AgentJoules.Core/Configuration/CloudGpuOptions.cs, src/AgentJoules.Core/Cost/CostCalculator.cs | 0.75 | AJ-072-T2 |
| AJ-072-T4 | Compute `totals.gco2e` from net kWh and the factor | src/AgentJoules.Workflow/Runs/RunAssembler.cs | 0.75 | AJ-072-T2 |
| AJ-072-T5 | Fill `environment.assumptions` with labelled price and factor | src/AgentJoules.Workflow/Runs/RunAssembler.cs | 0.5 | AJ-072-T4 |
| AJ-072-T6 | Document the electricity assumption and the cloudgpu rental-time rule | docs/build-log.md | 0.25 | AJ-072-T5 |
Test mapping: `CostModelTests` (required test class, trait `Spec=REQ-009`); feature file `tests/AgentJoules.Specs/Features/Reporting/LocalAndCloudCost.feature`.
Docs impact: `docs/build-log.md`; `docs/methodology.md` states the rental-time rule (written in M4); the README labels the electricity price as an assumption (M6).
Definition of done:
- [x] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [x] `dotnet test --filter "Spec=REQ-009"` shows at least one passed and no failed test for the local and cloudgpu tests
- [ ] the below-baseline rule is confirmed or replaced by the architect and the outcome is noted in `docs/build-log.md`
Demo relevance: yes: the local row shows cost in EUR next to joules, labelled as an estimate.
Conflict: C-08 (design 9.2) - REQ-009 defines no cloudgpu cost; resolution (ADR-15): step cost = configured hourly GPU price x step wall time, `estimated`, USD, with the whole-run billed cost in `environment.cloudCostUsd`; no electricity model is applied to a rented VM.

### AJ-073: Parse judge replies into an integer score from 1 to 5
Covers: REQ-010
Note (2026-10-22, AJ-073): the feature file is `tests/AgentJoules.Specs/Features/Reporting/AJ-073-judge-parsing.feature` (the table's `JudgeParsing.feature`, renamed to the M1 convention, deviation D1). Pinned readings P-a to P-f applied: think blocks removed by IndexOf loops, exactly one digit 1 to 5 after trimming, never clamped, failure excerpt cleaned by TextEcho and capped at 60 UTF-16 code units, `(empty)` for an empty reply, unterminated `<think>` left in place, null reply throws; measured: unit 51/51 and Specs 9/9 green, `Spec=REQ-010` 51 passed 0 failed, build 0 warnings. Q1 (secret redaction of the excerpt) was resolved in the fix round: the parser takes the `SecretRedactor` and redacts the think-stripped reply before trimming and the digit check. Criterion 3's `ParseFailure` is `JudgeParseResult` with `Parsed == false`; its excerpt is taken after think-stripping, redaction and trimming (design 3.2 amendment).
User story: As a reader of quality numbers, I want a judge reply accepted only when it is exactly an integer from 1 to 5, so that free-text replies never turn into invented scores.
Acceptance criteria:
1. `JudgeScoreParser.Parse("4")` gives quality `{value:4,label:"model-judged"}`; surrounding whitespace and newlines are ignored (test `Plain_integer_parses`).
2. Any `<think>...</think>` block is stripped before parsing (test `Think_block_is_stripped`).
3. Replies `4/5`, `6`, `0`, `3.5`, empty, `four` and `4 because it is good` give `ParseFailure` carrying the first 60 characters of the reply; the quality is `{value:null,label:"N/A"}` with reason `parse-error: <excerpt>` (test `Non_integer_reply_is_NA`).
4. The score value is the integer, never a string and never clamped (test `Score_is_integer_not_clamped`: the value is an integer and an input of 6 is not clamped to 5).
BDD scenarios:
```gherkin
@unit @AJ-S073 @REQ-010
Feature: AJ-073 Parse judge replies into an integer score from 1 to 5
  @positive
  Scenario: AJ-073 plain integer reply parses
    Given a judge reply " 4\n"
    When the reply is parsed
    Then the quality is 4 with label "model-judged"
  @positive
  Scenario: AJ-073 think block is removed before parsing
    Given a judge reply "<think>the answer is fine</think>5"
    When the reply is parsed
    Then the quality is 5 with label "model-judged"
  @negative
  Scenario Outline: AJ-073 invalid replies give N/A with a parse error
    Given a judge reply "<reply>"
    When the reply is parsed
    Then the quality has label "N/A" and a null value
    And the reason starts with "parse-error:"

    Examples:
      | reply                    |
      | 4/5                      |
      | 6                        |
      | 0                        |
      | 3.5                      |
      | four                     |
      | 4 because it is good     |
  @negative
  Scenario: AJ-073 excerpt is capped at 60 characters
    Given a judge reply of 200 characters that is not an integer
    When the reply is parsed
    Then the recorded excerpt has at most 60 characters
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-073-T1 | Write the failing BDD feature and unit tests for score parsing | tests/AgentJoules.Specs/Features/Reporting/JudgeParsing.feature, tests/AgentJoules.UnitTests/JudgeParsingTests.cs | 0.5 | - |
| AJ-073-T2 | Implement `JudgeScoreParser` with think-block stripping and the strict integer rule | src/AgentJoules.Core/Quality/JudgeScoreParser.cs | 0.75 | AJ-073-T1 |
| AJ-073-T3 | Map parse results to the quality `Metric` and the reason text | src/AgentJoules.Core/Quality/JudgeScoreParser.cs | 0.5 | AJ-073-T2 |
| AJ-073-T4 | Record the `Spec=REQ-010` parsing results in the build log | docs/build-log.md | 0.25 | AJ-073-T3 |
Test mapping: `JudgeParsingTests` (required test class, trait `Spec=REQ-010`); feature file `tests/AgentJoules.Specs/Features/Reporting/JudgeParsing.feature`.
Docs impact: `docs/build-log.md`.
Definition of done:
- [x] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [x] docs listed above updated
- [x] `dotnet test --filter "Spec=REQ-010"` shows at least one passed and no failed test
Demo relevance: no.

### AJ-074: Derive joules and cost per quality point only when both inputs exist
Covers: REQ-010
Note (2026-10-24, M4 K0): criterion 2 and the scenario label of costPerQualityPoint changed from model-judged to estimated by user decision 9 (docs/build-log.md, the decisions bullet of the M3 exit addendum); code, schema and fixtures follow.
Note (2026-10-22, AJ-074): feature file `tests/AgentJoules.Specs/Features/Reporting/AJ-074-derived-metrics.feature` (the story table's `DerivedMetrics.feature`); pinned readings applied: criterion 2 governs over design 3.2 line 222 through `Metric.ModelJudgedCost`, and `RunAssembler.Assemble` applies the derived metrics to every step, so an unscored step reads `missing-input` after assembly. Deviations: a `MetricTests` JSON row and a factory-count test name were updated (D2, D3), and `RetryPolicyTests.Steps_finished_before_the_failure_are_kept_unchanged` moved from `Assert.Same` to value equality after battery 1 (D7). Criterion 4 applies when the quality holds a value; a step that is both unscored and below baseline reads `missing-input` by the pinned check order (quality, energy, flag), recorded 2026-10-22 for the user to confirm or revise at the M3 exit.
User story: As a reader comparing efficiency within one tier, I want joules per quality point and cost per quality point computed only from real inputs and labelled `model-judged`, so that a missing input never produces a made-up ratio.
Acceptance criteria:
1. Quality 4 and net energy 120 J (fixture) give `joulesPerQualityPoint {value:30,label:"model-judged"}`; the test name is `Joules_per_point_is_net_over_quality`.
2. Cost 0.08 EUR (fixture) and quality 4 give `costPerQualityPoint {value:0.02,label:"estimated",currency:"EUR"}`; the currency of the cost is kept.
3. Energy N/A (hosted tier) gives `joulesPerQualityPoint` N/A with reason `missing-input`; quality N/A gives both derived metrics N/A with reason `missing-input`; cost N/A gives `costPerQualityPoint` N/A.
4. Assumption consistent with AJ-072: a step flagged `below-baseline` gives `joulesPerQualityPoint` N/A with reason `below-baseline`.
5. Derived values are never labelled `measured`, and no derived value is computed across tiers.
BDD scenarios:
```gherkin
@unit @AJ-S074 @REQ-010
Feature: AJ-074 Derive joules and cost per quality point only when both inputs exist
  @positive
  Scenario: AJ-074 joules per quality point equals net joules divided by the score
    Given a step with net energy 120 J and quality 4
    When the derived metrics are computed
    Then joulesPerQualityPoint is 30 with label "model-judged"
  @positive
  Scenario: AJ-074 cost per quality point keeps the currency
    Given a step with cost 0.08 EUR and quality 4
    When the derived metrics are computed
    Then costPerQualityPoint is 0.02 with label "estimated" and currency "EUR"
  @negative
  Scenario: AJ-074 hosted step has no joules per quality point
    Given a hosted step whose energy is N/A and whose quality is 4
    When the derived metrics are computed
    Then joulesPerQualityPoint has label "N/A", reason "missing-input" and a null value
  @negative
  Scenario: AJ-074 unscored step has no derived metrics
    Given a local step with net energy 120 J and quality N/A
    When the derived metrics are computed
    Then both derived metrics have label "N/A" and a null value
  @negative
  Scenario: AJ-074 below-baseline step has no joules per quality point
    Given a local step with negative net energy flagged "below-baseline" and quality 3
    When the derived metrics are computed
    Then joulesPerQualityPoint has label "N/A" and reason "below-baseline"
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-074-T1 | Write the failing BDD feature and unit tests for derived metrics | tests/AgentJoules.Specs/Features/Reporting/DerivedMetrics.feature, tests/AgentJoules.UnitTests/JudgeParsingTests.cs | 0.5 | AJ-073-T1 |
| AJ-074-T2 | Implement `DerivedMetrics.JoulesPerQualityPoint` and `CostPerQualityPoint` | src/AgentJoules.Core/Quality/DerivedMetrics.cs | 0.75 | AJ-074-T1, AJ-072-T2 |
| AJ-074-T3 | Apply the derived metrics per step in `RunAssembler` | src/AgentJoules.Workflow/Runs/RunAssembler.cs | 0.5 | AJ-074-T2 |
| AJ-074-T4 | Record the `Spec=REQ-010` derived-metric results in the build log | docs/build-log.md | 0.25 | AJ-074-T3 |
Test mapping: `JudgeParsingTests` (required test class, trait `Spec=REQ-010`); feature file `tests/AgentJoules.Specs/Features/Reporting/DerivedMetrics.feature`.
Docs impact: `docs/build-log.md`; the metric definitions go into `docs/methodology.md` in M4.
Definition of done:
- [x] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [x] `dotnet test --filter "Spec=REQ-010"` shows at least one passed and no failed test
Demo relevance: yes: the local rows show joules per quality point, labelled `model-judged`; hosted rows show N/A.

### AJ-075: Commit the judge rubric and load it
Covers: REQ-010
Note (2026-10-22, AJ-075): feature file `tests/AgentJoules.Specs/Features/Reporting/AJ-075-judge-rubric.feature` (the table's `JudgeRubric.feature`, repository convention from M1); pinned readings P-a to P-e applied (each of the five level lines required once in any order, the lowest missing level named, the path already resolved by the caller with the key named in the error, a 64 KiB strict-UTF-8 read, the rubric naming no model or vendor and presenting no number as a result). Measured: Specs `Category=AJ-S075` 4 of 4 and `JudgeParsingTests` 217 of 217 green after the fix and final rounds (192 at the draft), build 0 warnings; deviations D1 (a control-character file-name test dropped, Windows cannot create such a file) and D2 (feature file named per the M1 convention).
User story: As a reviewer of the quality scores, I want the judging rubric committed as `config/judge-rubric.md` with five defined levels, so that a score of 4 means the same thing in every run.
Acceptance criteria:
1. `config/judge-rubric.md` defines the levels 1 to 5 against the role of the judged step: 1 off-task or wrong, 2 major gaps, 3 usable with gaps, 4 correct and complete, 5 correct, complete and concise.
2. `RubricLoader.Load` returns the text verbatim; a missing file exits 2 naming `Paths:Rubric`; a rubric missing any level line `1` to `5` exits 2 naming the missing level.
3. The judge prompt contains the rubric text unchanged (asserted in AJ-076 by test `Prompt_contains_rubric_text`).
4. The rubric contains no key, no personal data and no number presented as a result (check: `grep -nE "nvapi-|[A-Za-z0-9]{32,}" config/judge-rubric.md` returns no hit).
BDD scenarios:
```gherkin
@unit @AJ-S075 @REQ-010
Feature: AJ-075 Commit the judge rubric and load it
  @positive
  Scenario: AJ-075 committed rubric has all five levels
    Given the committed config/judge-rubric.md
    When the rubric is loaded
    Then levels 1, 2, 3, 4 and 5 are present
  @positive
  Scenario: AJ-075 rubric text is returned verbatim
    Given a rubric file with known text
    When the rubric is loaded
    Then the returned text equals the file content
  @negative
  Scenario: AJ-075 missing rubric file exits 2
    Given Paths:Rubric points to a file that does not exist
    When the rubric is loaded
    Then the exit code is 2
    And the output names the configured path
  @negative
  Scenario: AJ-075 rubric without level 3 exits 2
    Given a rubric file that defines levels 1, 2, 4 and 5 only
    When the rubric is loaded
    Then the exit code is 2
    And the output names level 3
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-075-T1 | Write the failing BDD feature and unit tests for the rubric loader | tests/AgentJoules.Specs/Features/Reporting/JudgeRubric.feature, tests/AgentJoules.UnitTests/JudgeParsingTests.cs | 0.5 | AJ-073-T1 |
| AJ-075-T2 | Author the judge rubric | config/judge-rubric.md | 1.0 | AJ-075-T1 |
| AJ-075-T3 | Implement `RubricLoader` with the level check | src/AgentJoules.Workflow/Judge/RubricLoader.cs | 0.75 | AJ-075-T1 |
| AJ-075-T4 | Describe the rubric and the judge model disclosure rule in the build log | docs/build-log.md | 0.25 | AJ-075-T2 |
Test mapping: `JudgeParsingTests` (required test class, trait `Spec=REQ-010`); feature file `tests/AgentJoules.Specs/Features/Reporting/JudgeRubric.feature`.
Docs impact: `config/judge-rubric.md`; the README judge section (model ID, exclusion from totals, self-judging) is written in M6.
Definition of done:
- [x] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [x] `config/judge-rubric.md` is committed and loads without error
Demo relevance: no.

### AJ-076: Score each ok step with a metered hosted judge
Covers: REQ-010
Note (2026-10-22, AJ-075): AJ-075 criterion 3 names the test `Prompt_contains_rubric_text`, which AJ-076 delivers; the AJ-076 gate must cover it (spec remark R-2 of AJ-075).
Note (2026-10-22, AJ-076): feature file `tests/AgentJoules.Specs/Features/Workflow/AJ-076-judge-scoring.feature` (the table's `JudgeScoring.feature`, repository convention from M1); the pinned readings P-1 to P-11 and the kept pins applied per the architect's record of 2026-10-22 (the judge takes a `JudgeRequest` and returns a `JudgeResult`; `judgedAgent` is an optional `StepReading` member; the judge total carries no energy member; `JudgeService` writes the reason table through `RunAssembler.WithQuality`; `AddAgentJoulesJudge` is registered in the Workflow `ServiceCollectionExtensions`). Criterion 2 now reads "... and `qualityReason` `not-scored`" (was "(assumption: same `qualityReason` value `not-scored` as AJ-077)"); criterion 5 now reads "... and no energy member; each judge reading of a hosted judge carries energy N/A reason `hosted-tier`" (was "with energy N/A reason `hosted-tier` for a hosted judge"); T6 files add `src/AgentJoules.Cli/Hosting/AgentJoulesHostFactory.cs` and `src/AgentJoules.Cli/Commands/RunCommand.cs`. Measured: builder `JudgeParsingTests` 286 of 286 and Specs `Category=AJ-S076` 6 of 6, build 0 warnings; battery 1 (orchestrator) 3539 passed, 0 failed, 3 skipped. Deviations: `AddAgentJoulesJudge` sits in `src/AgentJoules.Workflow/ServiceCollectionExtensions.cs` beside `AddAgentJoulesCost` (the brief's Cli path does not exist), and an empty `Judge:ModelId` exits 2 at start (the shape rule for empty values); an unset or white-space value gives every judgeable step `judge-failed: judge-model-not-configured` without a call (P-10 read accordingly). Criterion 2's "any other step" means a step handed to the judge; a tier stopped by a non-ok step is not judged and its steps keep `not-scored` (P-7); the fix round tagged the prompt delimiters with a digest of the output and replaced the excerpt of an over-long or unterminated-think reply with a fixed text (security L-1 and L-2). M3 exit (2026-10-24, peer review M-1 and M-2): criterion 6's `selfJudged` stays the file's model-id equality fact, now `bool?` and null when nothing was judged, and the rendered line has three states that print the compared ids and never a bare "no"; the judge request carries `Judge:MaxOutputTokens` (default 32) and the judge tier's `SystemPromptPrefix` as its system message (design amendment of 2026-10-24 under Judge).
User story: As a reader of quality numbers, I want every successful workflow step scored by a configurable hosted judge whose calls are metered but kept out of the workflow totals, so that quality exists without distorting energy, tokens or cost.
Acceptance criteria:
1. A fake judge returning `4` yields step `quality {value:4,label:"model-judged"}` and `joulesPerQualityPoint` equal to net joules divided by 4 (test `Judge_four_gives_quality_four`).
2. Only steps with `outcome: "ok"` are judged; any other step gets quality N/A with reason `step-not-ok` and `qualityReason` `not-scored`.
3. The judge prompt is the rubric text, the task prompt, the judged agent name and the step output, ending with "Reply with one integer from 1 to 5 and nothing else."; the call uses temperature 0 and the buffered path.
4. Judge calls are metered with identity agent "Judge", tier `Judge:Tier`, model `Judge:ModelId`, published only to the judge sink and stored in the separate `judge` array with `judgedAgent`; workflow totals in a fixture are unchanged when judge readings are added (test `Totals_unchanged_by_judge`).
5. `totals.judge` reports judge input and output tokens and `costByCurrency`, and no energy member; each judge reading of a hosted judge carries energy N/A reason `hosted-tier`.
6. `run.selfJudged` is true when the judge model ID equals any step model ID.
7. `JudgeService` replaces the no-op `IJudgeService` seam of M2 in the orchestrator.
8. A step whose output is empty (`outputChars` equal to 0) is not sent to the judge: its quality is N/A with reason `empty-output` and `qualityReason` is `not-scored` (test `Empty_output_not_judged`: the fake judge received 0 calls for that step).
BDD scenarios:
```gherkin
@unit @AJ-S076 @REQ-010
Feature: AJ-076 Score each ok step with a metered hosted judge
  @positive
  Scenario: AJ-076 judge reply 4 becomes quality 4 and a derived ratio
    Given a completed local step with net energy 120 J and a fake judge that replies "4"
    When the run is judged
    Then the step quality is 4 with label "model-judged"
    And joulesPerQualityPoint is 30
  @positive
  Scenario: AJ-076 judge readings stay out of the workflow totals
    Given 4 workflow readings and 4 judge readings with known token counts
    When the run totals are assembled
    Then totals inputTokens equals the sum over the workflow readings only
    And totals.judge inputTokens equals the sum over the judge readings
  @positive
  Scenario: AJ-076 self-judging is flagged when the judge model equals a step model
    Given a judge model ID equal to the Writer model ID
    When the run is judged
    Then run.selfJudged is true
  @negative
  Scenario: AJ-076 a failed step is not sent to the judge
    Given a step with outcome "error"
    When the run is judged
    Then the fake judge received no call for that step
    And the step quality has label "N/A"
  @negative
  Scenario: AJ-076 judge reading never appears in the workflow sink
    Given a judged run
    When the workflow sink snapshot is read
    Then it contains no reading for agent "Judge"
  @negative
  Scenario: AJ-076 an empty step output is not judged
    Given a step with outcome "ok" and outputChars 0
    When the run is judged
    Then the fake judge received no call for that step
    And the step quality has label "N/A" and reason "empty-output"
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-076-T1 | Write the failing BDD feature and unit tests for judging | tests/AgentJoules.Specs/Features/Workflow/JudgeScoring.feature, tests/AgentJoules.UnitTests/JudgeParsingTests.cs | 0.5 | AJ-074-T1, AJ-075-T1 |
| AJ-076-T2 | Implement the judge prompt builder | src/AgentJoules.Workflow/Judge/JudgePromptBuilder.cs | 0.75 | AJ-076-T1, AJ-075-T3 |
| AJ-076-T3 | Implement `JudgeService.ScoreAsync` over metered buffered judge clients and the judge sink, skipping empty outputs | src/AgentJoules.Workflow/Judge/JudgeService.cs | 1.75 | AJ-076-T2, AJ-073-T3 |
| AJ-076-T4 | Fill the `judge` array and `totals.judge` in `RunAssembler` | src/AgentJoules.Workflow/Runs/RunAssembler.cs | 1.0 | AJ-076-T3 |
| AJ-076-T5 | Set `run.selfJudged` | src/AgentJoules.Workflow/Runs/RunAssembler.cs | 0.25 | AJ-076-T4 |
| AJ-076-T6 | Replace the no-op `IJudgeService` in the orchestrator | src/AgentJoules.Workflow/Runs/RunOrchestrator.cs, src/AgentJoules.Workflow/ServiceCollectionExtensions.cs | 0.5 | AJ-076-T3 |
| AJ-076-T7 | Record the `Spec=REQ-010` judge results in the build log | docs/build-log.md | 0.25 | AJ-076-T6 |
Test mapping: `JudgeParsingTests` (required test class, trait `Spec=REQ-010`); feature file `tests/AgentJoules.Specs/Features/Workflow/JudgeScoring.feature`.
Docs impact: `docs/build-log.md`; the README judge section (model ID, exclusion from totals, self-judging) is written in M6.
Definition of done:
- [x] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [x] `dotnet test --filter "Spec=REQ-010"` shows at least one passed and no failed test
Demo relevance: yes: the quality column in the demo table comes from this judge and is labelled `model-judged`.

### AJ-077: Never let a judge failure change the exit code
Covers: EDGE-012
Note (2026-10-22, AJ-077): feature file `tests/AgentJoules.Specs/Features/Workflow/AJ-077-judge-failure.feature` (the table's `JudgeFailure.feature`, named per the M1 repository convention). Pinned readings applied from the architect's record of 2026-10-22: one `JudgeFailed` line per tier run through `IRunListener`, told after the completed write (P-12, P-13); the missing judge key is pinned by tests only, `PreflightService.cs` unchanged (P-14); failed-judge reasons are exact, `judge-failed: <type>` or `parse-error: <excerpt>`, `not-scored`, derived N/A `missing-input` (P-15); the per-step conversion stays in `JudgeService` (P-16). A-13 applied in place: criterion 5 now says one warning line per tier run (per results file), T3 adds the listener and orchestrator files, T4 is the tests-only pin of the existing skip. Deviations: the warning names the run's tier, not the judge tier (D1); the lone-surrogate reason is reached only through a JSON escape in the HTTP body (D2); the fourteen EDGE-012 tests (twelve at the draft, two from the fix round) sit in the nested class `JudgeParsingTests.JudgeFailureNeverChangesTheExitCode` (D6); D3 to D5, D7 and D8 are test-side choices that change no criterion. A judge endpoint unreachable at preflight exits 5 under the preflight rule (design 3.4 Preflight step 3; an absent judge model ID is EDGE-008, exit 4) (spec R-1); the quality reason uses the raw exception type name, identical to its TextEcho form for every reachable name (spec R-4).
User story: As a user, I want a failing or unparseable judge to leave the workflow results intact and the exit code at 0, so that a rate-limited judge never destroys a measured run.
Acceptance criteria:
1. A failing fake judge (endpoint answers 500) gives `quality.label == "N/A"`, `qualityReason == "not-scored"`, `joulesPerQualityPoint.label == "N/A"` and exit code 0 (test `Failing_judge_keeps_exit_0`).
2. A judge that answers 429 on every call (5 attempts) behaves the same; an unparseable reply records `parse-error: <excerpt>` and the same labels.
3. A missing judge key outside `--dry-run` produces one warning, every quality N/A, and exit 0; with `--dry-run` it is reported by AJ-052 and exits 3.
4. The workflow steps, their totals and `run.status: "completed"` are identical to a run whose judge succeeded, except for quality and derived metrics.
5. One warning line per tier run (per results file) names the judge failure (not one per step); no key value appears in it (test `One_judge_warning_no_key`: exactly 1 warning line and it does not contain the runtime-built key).
6. When some judge calls succeed and some fail, the successful steps keep their scores (test `Partial_judge_failure_keeps_scores`).
BDD scenarios:
```gherkin
@unit @AJ-S077 @EDGE-012
Feature: AJ-077 Never let a judge failure change the exit code
  @positive
  Scenario: AJ-077 partial judge failure keeps the scores that succeeded
    Given a judge that fails for the third of 12 steps and answers "4" otherwise
    When the run is judged
    Then 11 steps have quality 4 with label "model-judged"
    And the third step has quality label "N/A" and qualityReason "not-scored"
  @positive
  Scenario: AJ-077 one warning line is printed per run
    Given a judge that fails for every step
    When the run completes
    Then exactly 1 judge warning line is printed
  @negative
  Scenario: AJ-077 failing judge leaves exit 0 and the results written
    Given a judge endpoint that answers 500 on every call
    When "run --tier nvidia --repeat 1" is executed
    Then the exit code is 0
    And every step has quality label "N/A", qualityReason "not-scored" and joulesPerQualityPoint label "N/A"
    And the results file has run.status "completed"
  @negative
  Scenario: AJ-077 judge 429 exhaustion does not change the exit code
    Given a judge endpoint that answers 429 on every call
    When "run --tier nvidia --repeat 1" is executed
    Then the exit code is 0
  @negative
  Scenario: AJ-077 unparseable judge reply is recorded as a parse error
    Given a judge that replies "excellent work"
    When the run is judged
    Then the quality reason starts with "parse-error:" and the exit code is 0
  @negative
  Scenario: AJ-077 missing judge key outside dry-run warns and exits 0
    Given the judge key is absent and the workflow tier key is present
    When "run --tier nebius --repeat 1" is executed
    Then the exit code is 0
    And every step quality is N/A
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-077-T1 | Write the failing BDD feature and unit tests for judge failure | tests/AgentJoules.Specs/Features/Workflow/JudgeFailure.feature (AJ-077-judge-failure.feature per the M1 convention), tests/AgentJoules.UnitTests/JudgeParsingTests.cs, JudgeParsingTests.Service.cs, JudgeParsingTests.Command.cs, tests/AgentJoules.TestSupport/JudgeFailedThrows.cs, OrderRecordingListener.cs, RunListenerFixtures.cs, ConfigurationFixtures.cs, tests/AgentJoules.Specs/Steps/JudgeFailureSteps.cs, tests/AgentJoules.Specs/Support/JudgeFailureContext.cs | 0.5 | AJ-076-T1 |
| AJ-077-T2 | Convert judge exceptions and parse failures into N/A quality with `not-scored` | none (the conversion shipped in AJ-076; pinned here by tests) | 1.0 | AJ-077-T1, AJ-076-T3 |
| AJ-077-T3 | Print one judge warning per tier run | src/AgentJoules.Cli/Output/ConsoleReporter.cs, src/AgentJoules.Cli/Output/ConsoleRunListener.cs, src/AgentJoules.Workflow/Runs/IRunListener.cs, src/AgentJoules.Workflow/Runs/RunOrchestrator.cs | 0.5 | AJ-077-T2 |
| AJ-077-T4 | Pin the existing missing-judge-key skip with tests | tests only | 0.75 | AJ-077-T2 |
| AJ-077-T5 | Record the `Spec=EDGE-012` result in the build log | docs/build-log.md | 0.25 | AJ-077-T4 |
Test mapping: `JudgeParsingTests.JudgeFailureNeverChangesTheExitCode` (a nested class carrying the trait `Spec=EDGE-012`; the outer class keeps `Spec=REQ-010`); feature file `tests/AgentJoules.Specs/Features/Workflow/JudgeFailure.feature`.
Docs impact: `docs/build-log.md`; the README exit-code note that judge failures never change the exit code is written in M6.
Definition of done:
- [x] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [x] `dotnet test --filter "Spec=EDGE-012"` shows at least one passed and no failed test
Demo relevance: no.

### AJ-078: Complete the results model and its serialisation
Covers: REQ-015
Note (2026-10-22, AJ-076): criterion 1 reads `judge[]` as StepReading elements with `judgedAgent` (no `JudgeReading` type, P-5), so AJ-078-T3 drops `src/AgentJoules.Core/Results/JudgeReading.cs` from its files; AJ-078 also takes the document invariant of P-5 (every `judge[]` element names its judged agent and `steps[]` elements never carry it) and a round trip that contains it.
Note (2026-10-22, AJ-078): feature file `tests/AgentJoules.Specs/Features/Reporting/AJ-078-results-model.feature` (the table's `ResultsModel.feature`, the story block verbatim, cmp exit 0); pinned readings P-1 to P-9 of the architect's record applied (`gitCommit` is `unknown`; `run.flags` is checked against `RunFlagNames` on construction, `with` and read; no `Equals` override, documents are compared by the test-side `ResultsEquality`, which skips `OutputText` and `Window`; each `costByCurrency` entry is checked; T4 is tests only; no `JudgeReading` type; the `judge[]` pairing invariant; the strict read path fails with `JsonException` or `ArgumentException`). In-place edits made, as the architect's record names them: criterion 1 (the `judge[]` element wording), criterion 6 (the round-trip wording), the T1 row and the test mapping (feature file name), the T3 row (`JudgeReading.cs` dropped) and the T4 row (tests only). Measured: `Spec=REQ-015` 73 passed 0 failed 0 skipped (battery 1); the unit filter 73 of 73 and Specs `Category=AJ-S078` 9 of 9 (builder-measured); build 0 warnings. Deviations: the strict context rejects unknown members everywhere (`UnmappedMemberHandling = Disallow` at context level, not on `BaselineResult` alone), and the constructors of `RunInfo`, `Totals` and `JudgeTotals` take camelCase parameter names, so existing named arguments changed.
User story: As a consumer of results files, I want one complete, strictly typed results model with the exact label strings, so that every file carries the documented fields and no missing value can hide as a zero.
Acceptance criteria:
1. The records of M2 are completed to the full prompt skeleton: `schemaVersion` "1"; `run` (`id`, `tier`, `tasks`, `mode`, `repeat`, `startUtc`, `toolVersion`, `gitCommit`, plus `status`, `flags` (values `partial-offload` and `non-publishable`), `stopReason`, `selfJudged`); `environment` (including `energyScope` constant "GPU-board only", `assumptions`, `models[]` with `baseline`); `probe`; `baseline` (`meanW`, `stdDevW`, `seconds`, `flags`); `steps[]`; `judge[]` (StepReading elements with `judgedAgent`, paired with `steps[]` by task, repeat and judged agent); `totals` (including `costByCurrency`, `gco2e`, `judge`).
2. Labels serialise exactly as `measured`, `estimated`, `model-judged`, `N/A`, `not-reported`; `value` is always written, null included.
3. Model types reject unknown properties on read (`JsonUnmappedMemberHandling.Disallow`), and numbers use strict handling (a string where a number is expected fails).
4. A unit test builds a document containing every label kind and asserts that no metric labelled `N/A` or `not-reported` has a numeric value (test `No_NA_metric_has_a_number`).
5. `StepReading.OutputText` is never persisted; only `outputSha256` (lowercase hex) and `outputChars` are.
6. A document round-trips through serialisation without change (test `Document_round_trips`: the read-back document equals the original member by member, the in-memory `OutputText` and `Window` excepted, and serialises to the same bytes).
7. `run.toolVersion` and `run.gitCommit` are read from `AssemblyInformationalVersionAttribute` (the text before and after `+`) and `gitCommit` is `unknown` when there is no `+` suffix (test `Version_and_commit_from_informational_version`); `environment.cloudCostObservedUtc` exists and is null outside the cloudgpu tier (test `Cloud_cost_observed_utc_is_nullable`).
BDD scenarios:
```gherkin
@unit @AJ-S078 @REQ-015
Feature: AJ-078 Complete the results model and its serialisation
  @positive
  Scenario: AJ-078 document with every label kind round-trips unchanged
    Given a fixture document with measured, estimated, model-judged, N/A and not-reported metrics
    When it is serialised and read back
    Then the read-back document equals the original
  @positive
  Scenario: AJ-078 label strings are the documented vocabulary
    Given a fixture document
    When it is serialised
    Then the label values used are only "measured", "estimated", "model-judged", "N/A" and "not-reported"
  @negative
  Scenario: AJ-078 N/A metric with a number is rejected on read
    Given a results text where a metric has label "N/A" and value 0
    When it is read
    Then reading fails
  @negative
  Scenario: AJ-078 unknown property is rejected on read
    Given a results text with an extra property "surprise" in a step
    When it is read
    Then reading fails and names the property
  @negative
  Scenario: AJ-078 step output text is never written
    Given a step reading whose in-memory output text is "secret text"
    When the document is serialised
    Then the text "secret text" does not appear and outputSha256 and outputChars are present
  @positive
  Scenario: AJ-078 the non-publishable run flag round-trips
    Given a fixture document whose run.flags is ["non-publishable"]
    When it is serialised and read back
    Then the read-back run.flags equals ["non-publishable"]
  @positive
  Scenario: AJ-078 tool version and commit come from the informational version
    Given an informational version "0.1.0+abc1234"
    When the run info is created
    Then run.toolVersion is "0.1.0" and run.gitCommit is "abc1234"
  @negative
  Scenario: AJ-078 an unlisted run flag is rejected on read
    Given a results text whose run.flags contains "made-up"
    When it is read
    Then reading fails and names the flag
  @negative
  Scenario: AJ-078 a version without a commit suffix gives unknown
    Given an informational version "0.1.0"
    When the run info is created
    Then run.gitCommit is "unknown"
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-078-T1 | Write the failing BDD feature and unit tests for the model | tests/AgentJoules.Specs/Features/Reporting/AJ-078-results-model.feature, tests/AgentJoules.UnitTests/SchemaValidationTests.cs | 0.5 | AJ-055-T2 |
| AJ-078-T2 | Complete the run, environment, baseline and totals records, with the run flag list and the informational-version split | src/AgentJoules.Core/Results/RunInfo.cs, src/AgentJoules.Core/Results/EnvironmentInfo.cs, src/AgentJoules.Core/Results/Totals.cs | 1.75 | AJ-078-T1 |
| AJ-078-T3 | Complete the step record (judged-agent rule from AJ-076) and the document invariants | src/AgentJoules.Core/Results/StepReading.cs, src/AgentJoules.Core/Results/RunDocument.cs, src/AgentJoules.Core/Results/RunInfo.cs, src/AgentJoules.Core/Results/Totals.cs, src/AgentJoules.Core/Results/JudgeTotals.cs, src/AgentJoules.Core/Results/RunFlagNames.cs | 1.0 | AJ-078-T2 |
| AJ-078-T4 | Pin the label names and the strict context options by tests | tests/AgentJoules.UnitTests/SchemaValidationTests.cs | 1.0 | AJ-078-T3 |
| AJ-078-T5 | Create `RunDocumentFixtures` with builders for every label kind | tests/AgentJoules.TestSupport/RunDocumentFixtures.cs | 1.0 | AJ-078-T3 |
| AJ-078-T6 | Document the results fields and label vocabulary | docs/build-log.md | 0.25 | AJ-078-T4 |
Test mapping: `SchemaValidationTests` (required test class, trait `Spec=REQ-015`); feature file `tests/AgentJoules.Specs/Features/Reporting/AJ-078-results-model.feature`.
Docs impact: `docs/build-log.md`; the field reference lives in the generated schema (AJ-079) and the README in M6.
Definition of done:
- [x] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [x] `dotnet test --filter "Spec=REQ-015"` shows at least one passed and no failed test for the model tests
Demo relevance: no.

### AJ-079: Generate the JSON Schema and guard it against drift
Covers: REQ-015
Note (2026-10-22, AJ-079): feature file `tests/AgentJoules.Specs/Features/Reporting/AJ-079-results-schema.feature` (the table's `ResultsSchema.feature`, the story block verbatim); pinned readings P-10 to P-15 of the architect's record applied (no `run.rubricSha256` in M3; four `$defs` with per-member refinements and the member-to-def table in `MetricSchemaDefs`; `$defs/probe` only, `$defs/calibration` in AJ-109; the quality refinement rejects 4.5; the resource name `AgentJoules.Reporting.Schema.results-schema.json`; the schema as the floor, with `SchemaValidator.Validate` reading the model as well in AJ-080; the drift helper `CommittedFileDrift.Difference` shared with AJ-080). In-place edits made: criterion 2 (the label sets and refinements of design 3.5), the T1 row and the test mapping (feature file name), and the design 3.5 and 3.2 amendments (S-1 to S-5). Measured (battery 1, orchestrator, 00:43:09Z to 00:44:46Z): `Spec=REQ-015` 222 passed 0 failed 0 skipped, Specs 317 passed 0 failed, build 0 warnings. Deviation: task T7 (the README section) is not edited in this story, because the README field reference is M6.
User story: As a consumer of results files, I want `docs/results-schema.json` generated from the model and checked by a test, so that the documented schema can never silently diverge from what the tool writes.
Acceptance criteria:
1. `docs/results-schema.json` is JSON Schema draft 2020-12 with `$schema` and `$id`, generated by `SchemaGenerator` from `AgentJoulesJsonContext` using the framework schema exporter.
2. Every metric property refers to one of four `$defs`: `metricMeasured`, `metricEstimated`, `metricJudged`, `metricAny`; each enforces "if label is N/A or not-reported then value is null and reason is non-empty, else value is a number"; `$defs/probe` covers the probe file; the label sets and per-member refinements are those of design 3.5 (Schema); the plain members carry the ranges and vocabularies of design 3.5 (Schema) (`minLength` 1 on the identifiers and `gitCommit`, counts and waits at least 0, `repeat` at least 1, `sampleRateHz` above 0, the baseline ranges, `environment.energyScope` the constant `GPU-board only`, enumerations from the code vocabularies); the `$defs` are ordered metricMeasured, metricEstimated, metricJudged, metricAny, step, probe.
3. The test `CommittedSchemaMatchesGenerated` regenerates the schema and compares with `JsonNode.DeepEquals`; `AGENTJOULES_UPDATE_SCHEMA=1 dotnet test --filter "FullyQualifiedName~CommittedSchemaMatchesGenerated"` rewrites the file and reports it with exit 1 (see the note below).
4. A generated results file validates against the schema (test `Generated_document_validates`); the validator reports failures with hierarchical output (a filtered walk, one line per real failure at the innermost member pointer).
5. A mutated fixture is invalid: N/A with a number, a `measured` label on a quality metric, an N/A metric without `reason`, an extra property.
6. The schema is an embedded resource of `AgentJoules.Reporting`, so validation works without the `docs/` folder; the validator is `JsonSchema.Net` 8.0.5 (MIT; the 9.x line is not OSI licensed).
7. `run.flags` items are restricted by the schema to the enumeration `partial-offload` and `non-publishable` (test `Run_flags_enum_in_schema`).
BDD scenarios:
```gherkin
@unit @AJ-S079 @REQ-015
Feature: AJ-079 Generate the JSON Schema and guard it against drift
  @positive
  Scenario: AJ-079 generated document validates against the schema
    Given a generated fixture document
    When it is validated against the schema
    Then the result is valid
  @positive
  Scenario: AJ-079 committed schema equals the generated schema
    Given the committed docs/results-schema.json
    When the schema is regenerated from the model
    Then the two JSON trees are equal
  @negative
  Scenario: AJ-079 N/A metric with a number is invalid
    Given a fixture where cost has label "N/A" and value 0.5
    When it is validated
    Then the result is invalid at the cost location
  @negative
  Scenario: AJ-079 quality labelled measured is invalid
    Given a fixture where quality has label "measured" and value 4
    When it is validated
    Then the result is invalid at the quality location
  @negative
  Scenario: AJ-079 stale committed schema fails the drift test
    Given a committed schema that lacks a field the model now has
    When the drift test runs
    Then the test fails and names the update variable
  @positive
  Scenario: AJ-079 a run flagged non-publishable validates
    Given a generated fixture whose run.flags is ["non-publishable"]
    When it is validated against the schema
    Then the result is valid
  @negative
  Scenario: AJ-079 an unknown run flag is invalid
    Given a generated fixture whose run.flags is ["made-up"]
    When it is validated against the schema
    Then the result is invalid at the run flags location
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-079-T1 | Write the failing BDD feature and unit tests for schema generation, validity and drift | tests/AgentJoules.Specs/Features/Reporting/AJ-079-results-schema.feature, tests/AgentJoules.UnitTests/SchemaValidationTests.cs | 0.75 | AJ-078-T5 |
| AJ-079-T2 | Implement `SchemaGenerator` with the exporter and `TransformSchemaNode` | src/AgentJoules.Reporting/Schema/SchemaGenerator.cs | 1.5 | AJ-079-T1, AJ-078-T4 |
| AJ-079-T3 | Author the four metric `$defs` and the probe `$def` | src/AgentJoules.Reporting/Schema/MetricSchemaDefs.cs | 1.5 | AJ-079-T2 |
| AJ-079-T4 | Implement `SchemaValidator` with hierarchical output | src/AgentJoules.Reporting/Schema/SchemaValidator.cs | 1.0 | AJ-079-T3 |
| AJ-079-T5 | Bootstrap and commit the generated schema | docs/results-schema.json | 0.5 | AJ-079-T4 |
| AJ-079-T6 | Embed the schema resource in the Reporting project | src/AgentJoules.Reporting/AgentJoules.Reporting.csproj | 0.25 | AJ-079-T5 |
| AJ-079-T7 | Document the update command and the schema location | README.md | 0.25 | AJ-079-T5 |
Test mapping: `SchemaValidationTests` (required test class, trait `Spec=REQ-015`); feature file `tests/AgentJoules.Specs/Features/Reporting/AJ-079-results-schema.feature`.
Docs impact: `docs/results-schema.json`; README section "Results schema" (location, update command), deferred to M6 (AJ-131 Docs impact, 2026-10-23): the README field reference is M6's.
Definition of done:
- [x] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [x] `dotnet test --filter "FullyQualifiedName~CommittedSchemaMatchesGenerated"` exits 0
Demo relevance: no.

Note (2026-10-23, AJ-079 gates, security L-4): criterion 3's "rewrites the file instead of failing" is superseded. The update run (`AGENTJOULES_UPDATE_SCHEMA=1`) refuses to write when the `CI` variable is set; otherwise it rewrites `docs/results-schema.json`, reports `rewrote docs/results-schema.json; rebuild, then re-run without AGENTJOULES_UPDATE_SCHEMA` and exits 1, so an update run is never green; the next plain run after a rebuild is green. The hint text is unchanged.

### AJ-080: Validate run files with `AJ validate`
Covers: REQ-015
Note (2026-10-22, AJ-080): feature file `tests/AgentJoules.Specs/Features/Cli/AJ-080-validate-command.feature` (the table's `Validate.feature`, the story block verbatim, cmp exit 0). Pinned readings P-16 to P-21 applied: selection by the run-file name test with probe and calibration files ignored; `RunFileValidator.Check` runs the schema first and reads the model only when the schema passes, one stdout line per failure and the first 20 per file; `results/` is the default path, a folder without run files counts 0, and a missing path or a named non-run file exits 2; `--config` has zero-or-one arity and exits 2 until M4; the three committed fixtures come from `RunDocumentFixtures.Committed`, pulled forward from AJ-086-T2. P-22 (criterion 7) is carried: not run on the real `results/`, which holds only a probe file. Measured (builder, 01:46Z to 02:02Z): the DoD command prints `Validated 3 files` and exits 0; the red run failed 28 of 283 unit tests and 4 of 5 scenarios, and the green run passes 283 of 283 and 5 of 5, exit 0 each; battery 1 (orchestrator, 02:02:25Z to 02:04:12Z) passed the full suite (3885 passed, 0 failed, 2 skipped) and `Spec=REQ-015` (283 passed), exit 0. Deviation D1: an unknown option is rejected by an argument validator, so a path that starts with `-` must be written `./-x` (`--` does not escape the dash; the parse message ends with a period). Deviation D4, delivered in the fix round: an unreadable folder exits 2 with `the folder "<path as given>" could not be read`, naming the path only. Fix-round guards: a lone-surrogate escape is one root failure line, and any other failure of the checks is one `<file>: (root) could not be checked (<exception type>)` line, so one file never aborts a scan (the guard is provable only with a stand-in, since no real input reaches it). Model ranges: non-finite doubles in `environment` and `probe` are model failures, and `environment.gpuOffloadPercent` outside 0 to 100 or `run.repeat` below 1 is rejected by the model and by the schema.
User story: As a reviewer, I want `AJ validate <path|dir>` to check run files against the schema and print how many were validated, so that I can trust every committed result file mechanically.
Acceptance criteria:
1. `AJ validate <dir>` over three valid fixture run files prints `Validated 3 files` and exits 0 (test `Validate_counts_files`).
2. A corrupted file (an N/A metric with a number) gives one stdout line `<file>: <instanceLocation> <error>` per failure, the error text being the schema's (for example `Required properties ["reason"] are not present`), then `Validated <n> files`, one stderr error line and exit 2; at most the first 20 failures of a file are listed, then `<file>: (root) and <N> more failures`.
3. Only files matching `^\d{8}T\d{6}Z-(local|cloudgpu|nvidia|nebius)(-baseline)?\.json$` are counted; `probe-*.json` and `calibration-*.json` are ignored by the count.
4. A path that does not exist or a file that is not JSON exits 2 naming the file; with no argument the default is `results/`; a named file whose name is not a run-file name exits 2; a folder without run files prints `Validated 0 files` and exits 0; a folder that cannot be listed exits 2 with `the folder "<path as given>" could not be read` (added 2026-10-23, AJ-080 gates).
5. The embedded schema is used, so the command works on a machine without `docs/`.
6. `validate --config` is registered with zero-or-one arity; in M3 it exits 2 stating that the check arrives in M4; M4 implements it.
7. Carried forward: on the real committed results, `AJ validate results/` prints a number equal to the number of committed run files.
BDD scenarios:
```gherkin
@unit @AJ-S080 @REQ-015
Feature: AJ-080 Validate run files with AJ validate
  @positive
  Scenario: AJ-080 three valid files are counted
    Given a folder with 3 valid run files and 1 probe file
    When "validate <folder>" is executed
    Then the output contains "Validated 3 files" and the exit code is 0
  @positive
  Scenario: AJ-080 validation works without the docs folder
    Given a working directory that has no docs folder
    When "validate <folder>" is executed on valid files
    Then the exit code is 0
  @negative
  Scenario: AJ-080 corrupted file exits 2 and names the location
    Given a folder with one run file where cost has label "N/A" and value 0.5
    When "validate <folder>" is executed
    Then the exit code is 2
    And one output line names the file and the cost location
  @negative
  Scenario: AJ-080 missing path exits 2
    Given a path that does not exist
    When "validate <path>" is executed
    Then the exit code is 2
  @negative
  Scenario: AJ-080 non-JSON run file exits 2 naming the file
    Given a run file name that holds the text "not json"
    When "validate <folder>" is executed
    Then the exit code is 2 and the output names the file
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-080-T1 | Write the failing BDD feature and unit tests for `validate` | tests/AgentJoules.Specs/Features/Cli/AJ-080-validate-command.feature, tests/AgentJoules.Specs/Steps/ValidateCommandSteps.cs, tests/AgentJoules.Specs/Support/ValidateCommandContext.cs, tests/AgentJoules.UnitTests/SchemaValidationTests.cs | 0.5 | AJ-079-T1, AJ-058-T1 |
| AJ-080-T2 | Implement `ValidateCommand` with the path argument and the `--config` option shape | src/AgentJoules.Cli/Commands/ValidateCommand.cs | 1.0 | AJ-080-T1, AJ-079-T4 |
| AJ-080-T3 | Implement file selection by name regex and the failure line formatter | src/AgentJoules.Reporting/Results/RunFileValidator.cs | 0.75 | AJ-080-T2 |
| AJ-080-T4 | Pin exit 2 for a failed validation by test | tests/AgentJoules.UnitTests/SchemaValidationTests.cs | 0.25 | AJ-080-T3 |
| AJ-080-T5 | Document `validate` usage | README.md | 0.25 | AJ-080-T4 |
| AJ-080-T6 | Generate and commit the three fixture run files | the three paths | 0.75 | AJ-080-T3 |
Test mapping: `SchemaValidationTests` (required test class, trait `Spec=REQ-015`; the validate tests are `Validate_counts_files`, `Corrupted_file_prints_its_location_then_the_count`, `Missing_path_exits_2_naming_it`, `Non_json_run_file_exits_2_naming_it`, `Default_path_is_results`, `Validates_without_config_or_docs`, `Empty_folder_validates_0_files`, `Named_non_run_file_exits_2`, `Config_flag_parses_with_zero_or_one_value`, `Unknown_option_exits_2_through_the_parse_check`, `Committed_fixture_folder_validates_3_files` and `CommittedFixtureRunFilesMatchGenerated`); feature file `tests/AgentJoules.Specs/Features/Cli/AJ-080-validate-command.feature`.
Docs impact: README section "Validate" (usage, exit codes 0 and 2).
Definition of done:
- [x] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [x] docs listed above updated
- [x] `dotnet run --project src/AgentJoules.Cli -- validate tests/AgentJoules.TestSupport/Fixtures/results` prints `Validated 3 files`
Demo relevance: yes: step 6 of the demo is `AJ validate results/`.
Conflict: C-04 (design 9.2) - invalid arguments must exit 2 while System.CommandLine returns 1; resolution: the shared `ParseResult.Errors` check from AJ-054 applies to `validate`.
Conflict: C-05 (design 9.2) - `--config` has two meanings; resolution: on `validate` the option has arity zero-or-one (flag form checks the model IDs in the configuration, implemented in M4); on other commands it is a path option.

### AJ-081: Select the run files a report may use
Covers: REQ-016
Note (2026-10-22, AJ-081): feature file `tests/AgentJoules.Specs/Features/Reporting/AJ-081-run-file-selection.feature` (the story table's `RunFileSelection.feature`, the story block verbatim); pinned readings applied: P-1 the selector API (`RunFileSelector.Select`, `Latest`, `LatestBaseline`, `RunHeaderReader.Read`), P-2 an unreadable header is a Problem with one reason token, P-3 one accept rule for both variants with `tier-mismatch` as a Problem, P-4 baseline rows keyed (Tier, Variant, Task, Agent) and drawn by the renderers (AJ-082 to AJ-084), P-25 the command resolves and checks each `--in` (AJ-086); measured: battery 1 `Spec=REQ-016` 59 passed 0 failed, `dotnet test --no-build` 3972 passed 0 failed 1 skipped, builder `Category=AJ-S081` 6 of 6 green; deviations D1 to D7 are listed in the dated note under this section.
User story: As a reader of the report, I want only completed run files of the right shape considered, with the newest per tier chosen, so that failed, partial-write or probe files never produce numbers.
Acceptance criteria:
1. `RunFileSelector.Latest(dir, tier)` returns the newest file by name among files matching the run-file regex without the `-baseline` suffix whose `run.status` is `completed` or `partial` and whose `run.flags` does not contain `non-publishable` (the flag of a run made with a baseline shorter than 30 s); `RunFileSelector.Select(inputs)` applies the same rule to files and folders together and reports an unreadable header or a tier that differs from the file name as a problem (exit 2 in `report`), never as `not run`; `Latest` and `LatestBaseline` throw `InvalidDataException` (`<file>: <reason>`) when a file of that tier newer than the winner is a problem.
2. Files with status `running`, `failed` or `incomplete`, `probe-*.json`, `calibration-*.json` and names not matching the regex are never selected.
3. `LatestBaseline(dir, tier)` returns the newest `-baseline` file of a tier accepted by the same status and `non-publishable` rule as `Latest`; the report shows its rows after the tier's rows with the tier cell `<tier> baseline`.
4. `--in` accepts files, directories, or both; a tier with no selectable file is reported as `not run`.
5. Selection reads only the run header, not the whole document (test `Selector_reads_header_only`: a file whose body after the header is invalid JSON is still selected).
BDD scenarios:
```gherkin
@unit @AJ-S081 @REQ-016
Feature: AJ-081 Select the run files a report may use
  @positive
  Scenario: AJ-081 newest completed file wins
    Given nvidia run files from 2026-10-22 and 2026-10-23 both completed
    When the latest nvidia file is selected
    Then the 2026-10-23 file is returned
  @positive
  Scenario: AJ-081 partial runs are accepted and baseline runs are returned separately
    Given a nebius file with status "partial" and a nebius baseline file
    When latest and latest baseline are selected for nebius
    Then the partial file is the latest and the baseline file is the latest baseline
  @negative
  Scenario: AJ-081 failed and running files are skipped
    Given the newest nvidia file has status "failed" and an older one is completed
    When the latest nvidia file is selected
    Then the older completed file is returned
  @negative
  Scenario: AJ-081 probe and calibration files are never selected
    Given a folder holding only a probe file and a calibration file
    When the latest local file is selected
    Then no file is returned and the tier is reported as not run
  @negative
  Scenario: AJ-081 a 10 s baseline run is skipped by Latest
    Given the newest local file carries run flag "non-publishable" and an older local file has no flags
    When the latest local file is selected
    Then the older file is returned
  @positive
  Scenario: AJ-081 a run with an empty flag list is selected
    Given the newest local file is completed and has an empty run.flags
    When the latest local file is selected
    Then the newest file is returned
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-081-T1 | Write the failing BDD feature and unit tests for file selection | tests/AgentJoules.Specs/Features/Reporting/AJ-081-run-file-selection.feature, tests/AgentJoules.UnitTests/ReportRenderingTests.cs | 0.5 | AJ-080-T1 |
| AJ-081-T2 | Implement the run header reader (status and flags) | src/AgentJoules.Reporting/Results/RunHeaderReader.cs | 0.75 | AJ-081-T1 |
| AJ-081-T3 | Implement `RunFileSelector.Latest` and `LatestBaseline` | src/AgentJoules.Reporting/Results/RunFileSelector.cs | 1.0 | AJ-081-T2 |
| AJ-081-T4 | Record the selection rules in the build log | docs/build-log.md | 0.25 | AJ-081-T3 |
Test mapping: `ReportRenderingTests` (required test class, trait `Spec=REQ-016`); feature file `tests/AgentJoules.Specs/Features/Reporting/AJ-081-run-file-selection.feature`.
Docs impact: `docs/build-log.md`.
Definition of done:
- [x] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [x] docs listed above updated
- [x] selection never returns a probe, calibration, failed or running file
Demo relevance: no.

Note (2026-10-23, AJ-081, as delivered): the pinned readings P-1 to P-4 and P-25 are applied as in the note under the Covers line; the builder deviations as delivered: D1 `Select` stops at the first accepted file per tier and variant, newest first, so `Skipped` and `Problems` list only the files newer than the winner (every file when none is accepted), except name-level problems (`duplicate-file-name`, an unreadable folder), which are decided before any header is read and reported whatever their age; D2 `Select` throws `ArgumentException` for a blank input or a name that fails `RunFileName.IsValid` and `ArgumentNullException` for a null list, a valid-named missing file or an unlistable folder is `unreadable`, and relative paths resolve against the process directory, so AJ-086 passes absolute paths; D3 `Latest` and `LatestBaseline` read only the requested tier and throw `InvalidDataException` with `<file>: <reason>` for the first Problem of that tier; D4 the header reader treats a non-object `run` as `run-missing`, a non-string `tier` or `status` as `tier-unknown` or `status-unknown`, takes the last of duplicate `tier`, `status` or `flags` members inside `run`, and the first of duplicate top-level `run` members, maps invalid UTF-8 and JSON errors to `header-not-json`, and probes one byte past 64 KiB to tell `header-too-long` apart; D5 the values of `Latest` and `LatestBaseline` are full paths, and `Skipped` and `Problems` carry the file name only, ordinal by file name, then reason; D6 the tier-mismatch check runs before the status rule; D7 the Specs steps run the real selector on TempWorkspace files built from the committed fixture text, and one unit test reads the committed fixtures folder in place. Fix round (2026-10-23): every input must be a fully qualified path (`ArgumentException` otherwise, the input echoed bounded); a non-regular file is refused without blocking (a stream that cannot seek is `unreadable` on Windows; on Unix a size-0 entry is `header-not-json` before any open, so a FIFO reads as a problem and never blocks, and the two fix-round deviations are D9 and D10 in the build-log entry); one pooled buffer is rented per `Select`, `Latest` or `LatestBaseline` call and returned cleared. The Docs impact is now complete: the design (the Run files bullet and the AJ-081 amendment) and this story (criteria 1 and 3, the test mapping, T1 and this note) carry the changes, so the DoD box "docs listed above updated" is ticked.

### AJ-082: Aggregate repeats into report rows without merging tiers
Covers: REQ-016
Note (2026-10-22, AJ-082): feature file `tests/AgentJoules.Specs/Features/Reporting/AJ-082-aggregation.feature` (the story table's `Aggregation.feature`, the story block verbatim, cmp exit 0 checked by the docs writer); pinned readings P-4 to P-10, P-14 and P-15 and the kept pins P-a to P-d applied (the row key carries the variant, only `ok` steps enter cells, the median and `n` over the valued repeats, no cross-tier member). Measured: 131 ReportRenderingTests and 5 AJ-S082 scenarios green (battery 1 `Spec=REQ-016` 131 passed, 1 skipped, exit 0); deviations: criteria 1, 4 and 5 amended per S-3, S-4 and S-5 and the design per S-11.
User story: As a reader of the report, I want one row per tier, task and agent showing the median with its range and the number of repeats, so that variation is visible and laptop and cloud numbers are never mixed.
Acceptance criteria:
1. A report row is `(tier, variant, task, agent)` (variant: the latest file or the `-baseline` file); each value cell holds the median with `[min to max]` and `n` over the `ok` repeats that have a value (`total`: the `ok` repeats) (test `Median_min_max_n`; fixture repeats 10, 12, 14 give median 12 and range 10-14, n 3).
2. Rows of different tiers are never merged or averaged, including `local` and `cloudgpu` with equal model IDs; nothing is ranked and no ratio between tiers is computed.
3. A cell whose repeats are all N/A or not-reported keeps that label with a null value; N/A is never averaged as zero (test `All_NA_cell_stays_NA`).
4. When some repeats lack a value, the median is taken over the repeats that have one, `n` counts only those, the cell keeps their label and shows `n=<n> of <total>` (confirmed 2026-10-22, M3 kickoff).
5. The cell keeps the label of its valued repeats, so a label other than `measured` is carried to the aggregate cell; every flag found on any `ok` repeat, and the file's baseline flags, are carried to the aggregate row (flags column) (for example `low-resolution`).
6. Cost cells keep their currency code; quality cells keep the `model-judged` label.
BDD scenarios:
```gherkin
@unit @AJ-S082 @REQ-016
Feature: AJ-082 Aggregate repeats into report rows without merging tiers
  @positive
  Scenario: AJ-082 three repeats give median, range and n
    Given 3 repeats of net joules 10, 12 and 14 for one agent
    When the rows are aggregated
    Then the cell shows median 12, range 10 to 14 and n 3
  @positive
  Scenario: AJ-082 a flag on one repeat is shown on the aggregate cell
    Given 3 repeats of which one has flag "low-resolution"
    When the rows are aggregated
    Then the aggregate cell carries "low-resolution"
  @negative
  Scenario: AJ-082 all-N/A repeats stay N/A and are not averaged as zero
    Given 3 hosted repeats whose net joules are N/A
    When the rows are aggregated
    Then the cell has label "N/A" and a null value
  @negative
  Scenario: AJ-082 local and cloudgpu rows are not merged
    Given runs for tiers "local" and "cloudgpu" with the same model ID
    When the rows are aggregated
    Then the result has separate rows for "local" and "cloudgpu" and no combined row
  @negative
  Scenario: AJ-082 no cross-tier ratio is produced
    Given runs for tiers "local" and "nvidia"
    When the rows are aggregated
    Then the report model contains no cross-tier ratio or ranking field
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-082-T1 | Write the failing BDD feature and unit tests for aggregation | tests/AgentJoules.Specs/Features/Reporting/AJ-082-aggregation.feature, tests/AgentJoules.UnitTests/ReportRenderingTests.cs | 0.5 | AJ-081-T1 |
| AJ-082-T2 | Create the report model records | src/AgentJoules.Reporting/Render/ReportModel.cs | 0.75 | AJ-082-T1 |
| AJ-082-T3 | Implement median, min, max and n over repeats with label handling | src/AgentJoules.Reporting/Render/ReportAggregator.cs | 1.5 | AJ-082-T2 |
| AJ-082-T4 | Carry labels, flags and currency to aggregate cells | src/AgentJoules.Reporting/Render/ReportAggregator.cs | 0.75 | AJ-082-T3 |
| AJ-082-T5 | Record the row definition and the mixed-label assumption in the build log | docs/build-log.md | 0.25 | AJ-082-T4 |
Test mapping: `ReportRenderingTests` (required test class, trait `Spec=REQ-016`); feature file `tests/AgentJoules.Specs/Features/Reporting/AJ-082-aggregation.feature`.
Docs impact: `docs/build-log.md`; `docs/methodology.md` states the median rule (written in M4).
Definition of done:
- [x] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [x] the mixed-label assumption in criterion 4 is confirmed or replaced and the outcome noted in `docs/build-log.md`
Demo relevance: yes: every table and chart row in the demo is one aggregate row.
Conflict: C-07 (design 9.2) - the page requirement of one row per step conflicts with the median over repeats; resolution: a row is the aggregate of one `(tier, task, agent)` over its repeats.

Note (2026-10-23, AJ-082, as delivered): builder choices 1 to 5 as delivered: 1 the model IDs of a row and the energy methods of a tier read every step of the key or file (only the sampling-fallback fact reads ok steps), accepted by the spec review 2026-10-23: no AJ-082 criterion names which steps feed the row model IDs or the tier energy methods; 2 valued repeats of one cell with more than one label or more than one unit throw `ArgumentException` (the mixed-currency check runs first and gives N/A without throwing); 3 `ModelsText` prints N/A when a file names no model; 4 `Build` refuses a step whose tier differs from the run tier, naming `runs[i].Document.Steps[j]`, because neither the model nor the schema checks it and the fix round added the check (the row tier is `Document.Run.Tier`); 5 `Build` refuses a null list, a null element or a null document with `ArgumentNullException`, and the name, tier and variant rules with `ArgumentException`, and `Build` of an empty list is valid (no rows, all four tiers not run, two disclosures). Fix round (2026-10-23, 04:46Z to 04:53Z): the rows are grouped in one pass over `steps[]` (2,000 distinct-key steps in 101 ms, 84.3 s before the round); a located throw names `runs[i].Document.Steps[j].<member>` with ParamName `runs`; `Build` refuses a run whose status is not `completed` or `partial`, a run whose flags hold `non-publishable`, and a file name whose timestamp is not `run.startUtc` to the UTC second (the name comes from the same instant the writer records). This makes the `SelectedRun` contract stricter for AJ-083 to AJ-086; every file the tool writes and the committed fixtures satisfy it.

### AJ-083: Render the Markdown summary with honest labels
Covers: REQ-016, EDGE-003, EDGE-002, EDGE-004, EDGE-007
Note (2026-10-23, AJ-083): feature file `tests/AgentJoules.Specs/Features/Reporting/AJ-083-summary-markdown.feature` (the story block verbatim, seven scenarios, cmp exit 0); the pinned readings P-11 to P-18, P-26 and P-43 are applied, and the builder's decisions 1 to 6 are recorded in the 2026-10-23 note at the end of this section; the four EDGE traits map to `Low_resolution_flag_stays_visible_in_the_row_of_the_value` (EDGE-003), `Sampling_fallback_header` (EDGE-002), `Concurrent_sentence` (EDGE-004) and `Usage_free_row_shows_not_reported_and_NA` (EDGE-007); measured by the builder (exit 0) and battery 1 (PASS): `Spec=REQ-016` 246 passed, 0 failed, 1 skipped.
Note (2026-10-21, M2 exit): EDGE-004 and EDGE-007 added because criteria 4 and 5 finish the EDGE-004 rendered overlap sentence and the EDGE-007 report cells (M2 full spec pass, remark 2), so the traits are not lost.
Note (2026-10-20, M1 exit): EDGE-003 and EDGE-002 added per plan section 5 seam row 6 (report row and header reference EDGE-003 and EDGE-002) and the M1 spec full pass (the rendered-row half of EDGE-003 and the report-header half of EDGE-002 are carried here from M1).
User story: As a reader of the report, I want `summary.md` to carry the measurement context and one table with every label and flag next to its value, so that no number appears without its evidence class.
Acceptance criteria:
1. The header lists GPU name, driver version, run date (UTC), local runtime, model IDs and precision per tier, reasoning mode per tier, power source, judge model ID and self-judging disclosure, followed by the sentence `Energy is GPU-board energy only.` (test `Header_and_honesty_sentence`).
2. The table has the columns tier, task, agent, gross J, net J, avg W, peak W, input tokens, output tokens, latency ms, TTFT ms, cost, quality, labels, flags; joules and watts use one decimal, latency none, cost four decimals with the currency code, quality and tokens an integer, or one decimal when an even-n median falls between two integers, all with invariant culture; cells follow the grammar `<median>[ <currency>][ [<min> to <max>]] n=<n>[ of <total>][ <label>]` or `<label> (<reason>; <reason>; <reason>; +<k> more)` with at most three reasons named, as the code prints them. Amended 2026-10-23 (AJ-083 gates): "cost four decimals" is amended to four decimals from 0.0001 upward and for an exact zero, and below 0.0001 two significant digits in fixed notation, never an exponent (for example `0.0000058 EUR n=1 estimated`), because every real local step at the shipped price would otherwise print `0.0000 EUR`; a value that rounds up to 0.0001 prints `0.0001`; the user confirms or reverts this amendment at the M3 exit.
3. A tier without a selectable run file renders as the row `<tier> | not run`. Amended 2026-10-23 (AJ-083 gates): a selected file with no completed step prints the row `| <tier> | no completed steps |` (`<tier> baseline` for a baseline file) followed by twelve empty cells and the flags cell, which holds the run flags of the file and `partial` for a partial file; the `not run` row is unchanged.
4. A no-usage fixture row shows `not-reported` in both token cells and `N/A` in the cost cell; hosted energy cells show `N/A`; a flag such as `low-resolution` appears in the row's flags column.
5. A concurrent fixture adds the sentence `Per-step energy overlaps in concurrent mode and does not sum to the run total.`; a sampling-fallback fixture adds `Energy method: sampling (counter unsupported)`; a `partial` run says `partial`.
6. When model IDs or precision differ across tiers the header states `Models differ across tiers: <tier>=<id> (<precision>), ...` and no cross-tier efficiency figure is printed.
7. The output is deterministic: no generation timestamp, LF newlines.
BDD scenarios:
```gherkin
@unit @AJ-S083 @REQ-016
Feature: AJ-083 Render the Markdown summary with honest labels
  @positive
  Scenario: AJ-083 header carries the context and the GPU-board sentence
    Given a fixture with a local run and a nvidia run
    When the summary is rendered
    Then the header lists GPU name, driver, run date, runtime, model IDs and precision
    And the text contains "Energy is GPU-board energy only."
  @positive
  Scenario: AJ-083 tier without a run file is shown as not run
    Given a fixture with only a nvidia run
    When the summary is rendered
    Then the table has the rows "local | not run", "cloudgpu | not run" and "nebius | not run"
  @positive
  Scenario: AJ-083 concurrent run adds the overlap sentence
    Given a fixture whose run.mode is "concurrent"
    When the summary is rendered
    Then the text contains "Per-step energy overlaps in concurrent mode and does not sum to the run total."
  @negative
  Scenario: AJ-083 usage-free row shows not-reported and N/A
    Given a fixture row whose response had no usage
    When the summary is rendered
    Then both token cells contain "not-reported" and the cost cell contains "N/A"
  @negative
  Scenario: AJ-083 hosted energy is N/A and never 0
    Given a fixture hosted row
    When the summary is rendered
    Then the gross J, net J, avg W and peak W cells contain "N/A"
    And no energy cell contains "0.0"
  @negative
  Scenario: AJ-083 differing models produce a disclosure and no efficiency claim
    Given runs whose tiers use different model IDs
    When the summary is rendered
    Then the header contains "Models differ across tiers"
    And the text has no cross-tier ratio
  @negative
  Scenario: AJ-083 low-resolution flag stays visible
    Given a fixture row with flag "low-resolution"
    When the summary is rendered
    Then the row contains "low-resolution" next to the value
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-083-T1 | Write the failing BDD feature and unit tests for the summary | tests/AgentJoules.Specs/Features/Reporting/AJ-083-summary-markdown.feature, tests/AgentJoules.UnitTests/ReportRenderingTests.cs | 0.75 | AJ-082-T1 |
| AJ-083-T2 | Add fixtures for hosted, no-usage, concurrent, low-resolution, sampling and partial runs | tests/AgentJoules.TestSupport/RunDocumentFixtures.cs | 1.0 | AJ-083-T1, AJ-078-T5 |
| AJ-083-T3 | Create the invariant number and cell formatter | src/AgentJoules.Reporting/Render/InvariantFormat.cs | 0.5 | AJ-083-T1 |
| AJ-083-T4 | Implement `SummaryMarkdownRenderer` table rows with labels and flags | src/AgentJoules.Reporting/Render/SummaryMarkdownRenderer.cs | 1.5 | AJ-083-T3, AJ-082-T4 |
| AJ-083-T5 | Implement the header, the disclosures and the not-run rows | src/AgentJoules.Reporting/Render/SummaryMarkdownRenderer.cs | 1.25 | AJ-083-T4 |
| AJ-083-T6 | Record the `Spec=REQ-016` Markdown results in the build log | docs/build-log.md | 0.25 | AJ-083-T5 |
Test mapping: `ReportRenderingTests` (required test class, trait `Spec=REQ-016`, with `Spec=EDGE-003` on `Low_resolution_flag_stays_visible_in_the_row_of_the_value`, `Spec=EDGE-002` on `Sampling_fallback_header`, `Spec=EDGE-004` on `Concurrent_sentence` and `Spec=EDGE-007` on `Usage_free_row_shows_not_reported_and_NA`); feature file `tests/AgentJoules.Specs/Features/Reporting/AJ-083-summary-markdown.feature`.
Docs impact: `docs/build-log.md`; the sample table appears in the README results block (AJ-087) and in M6.
Definition of done:
- [x] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [x] docs listed above updated
- [x] `dotnet test --filter "Spec=REQ-016"` shows at least one passed and no failed test for the Markdown tests
Demo relevance: yes: the summary table is the evidence behind every number in the video and posts.
Note (2026-10-23, AJ-083, as delivered): the builder's decisions, one sentence each. (1) The currency code is escaped as well as the reasons, because the AJ-082 binding note names `Currency` as raw document text, although pinned P-26 said codes are not escaped; a normal code passes unchanged, and `Unit` is never printed. (2) The models-differ line is escaped per `tier=models (precision)` entry (split on `, `, each entry escaped and bounded, rejoined) rather than as one 128-character remainder, so a four-tier line is not cut and a forged entry cannot carry markup. (3) No blank line follows the title: the first header bullet is line 2 (P-13 read literally), and the disclosures are one per line, which Markdown renders as one paragraph; the Markdown is a data file, and the HTML page of AJ-085 has its own layout. (4) Rows and bullets follow tier order, primary before baseline, and a baseline-only tier shows `<tier> | not run` immediately before its `<tier> baseline` rows. (5) The sign is dropped whenever a result rounds to zero (-0.0 and -0.04 print 0.0); real negatives keep it. (6) `ReportHeader.Rank` and `ReportHeader.NotRun` are internal helpers for AJ-084 to AJ-087, and `Report_fixtures_pass_the_run_file_validator` confirms every new fixture passes `RunFileValidator.Check`. Deviations as delivered: criteria 2 and 4 are reworded above (P-11 quality and tokens, P-12 flags column); the Gherkin scenario `the row contains "low-resolution" next to the value` is kept verbatim because the feature file must equal the story block; the header is one bullet per file (P-13); the formatter parameter is typed `LabelMetric`, the alias of `AgentJoules.Core.Labels.Metric`, because the method is itself named `Metric`; the new shared builders are in `tests/AgentJoules.TestSupport/ReportRunFixtures.cs`. Fix round (2026-10-23, 06:03Z to 06:13Z, after the gates): the cleaner masks a key pattern before the cut; the models-differ split is capped at the number of known tiers, so the line stays bounded, and a plain-text entry forged inside a model ID can still read as an extra entry (the tier's own bullet shows whether that tier ran); only the six sentence constants (`GpuBoardOnly`, `JudgeExcluded`, `SelfJudged`, `ConcurrentOverlap`, `ConcurrentCostOverlap`, `SamplingFallback`) pass a disclosure line unchanged, any other text is escaped, has its leading spaces stripped, and has a leading list or heading marker neutralised; `$` is escaped; a selected file with no completed step prints the `no completed steps` row (criterion 3); the cost rule is the amendment of criterion 2; the cleaner walks code points, so supplementary format characters and non-characters become `?`. Open for the M3 exit: the committed fixture costs are 5.83e-6 to 6.75e-6 EUR per step (local) and 4.0e-5 to 5.1e-5 USD (nebius); under the four-decimal rule they would print `0.0000 EUR` (every local cell) and `0.0000 USD` (ten of the twelve nebius cells, the other two `0.0001 USD`), so the two-significant-digit form below 0.0001 (criterion 2, amended 2026-10-23) is for the user to confirm or revert at the M3 exit.

### AJ-084: Draw the SVG chart with N/A as a text marker
Covers: REQ-016
Note (2026-10-23, AJ-084): feature file `tests/AgentJoules.Specs/Features/Reporting/AJ-084-svg-chart.feature` (the story block verbatim, six scenarios, `cmp` exit 0); the pinned readings P-18 to P-22 and the kept draft pins P-a to P-e are applied, and the builder's deviations 1 to 9 are recorded in the 2026-10-23 note at the end of this section; measured: `Spec=REQ-016` 372 passed, 0 failed, 1 skipped in battery 1 (07:10:04Z to 07:11:56Z).
User story: As a reader of the chart, I want net joules per step drawn as bars only where a measured value exists and an explicit `N/A` text where none exists, so that a missing value is never mistaken for a tiny bar.
Acceptance criteria:
1. `joules-per-step.svg` is produced by project code with `XmlWriter` and parses as XML (`XDocument.Parse`); the root is `<svg xmlns="http://www.w3.org/2000/svg" ...>`.
2. For each hosted-tier aggregate row (tier, task, agent) the SVG contains a `<text>` element `N/A` and no `<rect>` (test `NA_step_has_no_rect`).
3. A measured median net joules value gives one `<rect>` with height scaled so the largest positive and the largest negative median fit on one axis and a value label; a negative net value draws a bar below the axis.
   Note (2026-10-23, AJ-084 gates, layout as delivered): the chart has one row per task, in the order the tasks first appear in the model rows, each row labelled with the cleaned task name and drawn with its own axis on one chart-wide scale; each agent of a task is one group labelled with the cleaned agent name only, and a group that exists only in a baseline file sits in its task row. The axis sits at the bottom of each plot when no median is negative and at the top for an all-negative chart (a P-21 amendment). At most 1,000 groups are drawn; a model with more gets one notice, `The chart shows the first 1000 task and agent groups; summary.md lists every row.` (`ReportSentences.ChartTruncated`). The title is `Measured net energy per step in joules (J), median of the repeats of each bar`. The committed fixtures render three rows of four groups at 1,530 x 1,352 (1,334 as first committed; the first row top moved from 44 to 62 px in the follow-up of 2026-10-23 after a file screenshot showed the scope sentence overlapping the first row label) with 12 bars, 3 axes and 24 `N/A` markers.
4. One slot per tier in the fixed order local, cloudgpu, nvidia, nebius, and a baseline slot after a tier that has a `-baseline` file; a tier without a run file is a slot whose marker is a `<text>` element `not run`, with no `<rect>`. Amended 2026-10-23 (AJ-084 gates): this replaces the earlier wording of this criterion.
5. The chart carries the sentence `Energy is GPU-board energy only.`; text values are XML-escaped so a model ID with `&` or `<` still parses.
6. `git grep -nEi "oxyplot|scottplot|livecharts|plotly|playwright|puppeteer|selenium" -- "*.csproj" "Directory.Packages.props"` returns no hit.
BDD scenarios:
```gherkin
@unit @AJ-S084 @REQ-016
Feature: AJ-084 Draw the SVG chart with N/A as a text marker
  @positive
  Scenario: AJ-084 measured value is drawn as a bar with a label
    Given a fixture local step with median net joules 12.3
    When the chart is rendered
    Then the SVG has one bar for that step and the text "12.3"
  @positive
  Scenario: AJ-084 chart is well-formed XML with the fixed tier order
    Given a fixture with all four tiers
    When the chart is rendered
    Then the output parses as XML
    And the tier slots appear in the order local, cloudgpu, nvidia, nebius
  @positive
  Scenario: AJ-084 negative net value is drawn below the axis
    Given a fixture local step with net joules -2.0 flagged "below-baseline"
    When the chart is rendered
    Then the bar extends below the axis line
  @negative
  Scenario: AJ-084 N/A step has a text marker and no bar
    Given a fixture hosted step whose net joules are N/A
    When the chart is rendered
    Then the SVG has a text element "N/A" for that step
    And the SVG has no rect element for that step
  @negative
  Scenario: AJ-084 tier without a run file is labelled not run
    Given a fixture with no cloudgpu run
    When the chart is rendered
    Then the cloudgpu slot has the text "not run" and no bar
  @negative
  Scenario: AJ-084 hostile model ID is escaped
    Given a model ID containing "&" and "<"
    When the chart is rendered
    Then the output still parses as XML
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-084-T1 | Write the failing BDD feature and unit tests for the SVG | tests/AgentJoules.Specs/Features/Reporting/AJ-084-svg-chart.feature, tests/AgentJoules.UnitTests/ReportRenderingTests.cs | 0.5 | AJ-083-T2 |
| AJ-084-T2 | Implement the scale and slot layout | src/AgentJoules.Reporting/Render/SvgLayout.cs | 1.0 | AJ-084-T1 |
| AJ-084-T3 | Implement `JoulesSvgRenderer` bars, labels and the root element with `XmlWriter` | src/AgentJoules.Reporting/Render/JoulesSvgRenderer.cs | 1.5 | AJ-084-T2 |
| AJ-084-T4 | Draw N/A and not-run text markers | src/AgentJoules.Reporting/Render/JoulesSvgRenderer.cs | 0.75 | AJ-084-T3 |
| AJ-084-T5 | Draw negative bars and the axis | src/AgentJoules.Reporting/Render/JoulesSvgRenderer.cs | 0.5 | AJ-084-T3 |
| AJ-084-T6 | Run the package grep and record it in the build log | docs/build-log.md | 0.25 | AJ-084-T5 |
Test mapping: `ReportRenderingTests` (required test class, trait `Spec=REQ-016`); feature file `tests/AgentJoules.Specs/Features/Reporting/AJ-084-svg-chart.feature`.
Docs impact: `docs/build-log.md`.
Definition of done:
- [x] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [x] docs listed above updated
- [x] the package grep returns no hit
Demo relevance: yes: the chart is shown in the video with hosted tiers marked `N/A`.
Note (2026-10-23, AJ-084, as delivered): the pinned readings applied are P-18 (cleaning, then the XML writer), P-19 (one bar or marker per aggregate row, a baseline slot after its tier, the marker holds the label), P-20 (an empty chart draws four `not run` slots), P-21 (one chart-wide scale from the largest positive and the largest negative median, one axis per row after the fix round), P-22 (`XmlWriter` settings and `Render(ReportModel)`), P-10 and P-9, and the kept draft pins P-a to P-e. The builder's deviations as delivered: (1) the marker holds the label text only (`N/A` or `not-reported`), not the reason, because P-19 governs over an orchestrator note; a `<title>` with the cleaned reason is an optional later addition. (2) The writer is an `XmlWriter` over a `MemoryStream` with ASCII encoding, so non-ASCII becomes character references and no UTF-16 declaration is written. (3) The chart adds a `text.title` and a root background style and no legend or extra rect or line, so "no rect other than bars" holds without exception and, after the fix round, "one axis" holds per row at one chart-wide scale. (4) The hostile-ID theory indexes a payload array, because xUnit gave two lone surrogates one test ID. (5) Tests use `Tier.CloudGpu` where a measured value or an energy flag is needed, because `StepReading` rejects those on nvidia and nebius. (6) The feature file is `AJ-084-svg-chart.feature`, not `SvgChart.feature`. (7) The first delivered layout drew 12 groups in one 4,346-px row, which a 1,000-px page shrinks to 2 to 3-px markers; the fix round lays the chart out one row per task (the committed fixtures: three rows of four groups at 1,530 x 1,352 after the follow-up of 2026-10-23), and AJ-085 embeds the SVG at its intrinsic size in a horizontally scrolling container, through an `img` reference with a constant alt text. (8) The axis sits at the bottom of each plot when no median is negative, so an all-zero or empty chart no longer draws it at the top; the top is kept for an all-negative chart (P-21 amended at the gates). (9) Marker facts the design did not name: a selected file with no rows draws `no completed steps`; a tier with rows but none for the group draws `N/A`; a partial file adds `(partial)` to its slot label; an empty model draws one group with an empty label and four `not run` slots; the chart carries the title and `ReportSentences.GpuBoardOnly` as text elements.

### AJ-085: Generate the static HTML report page for the demo URL
Covers: REQ-030
Note (2026-10-23, AJ-085): feature file `tests/AgentJoules.Specs/Features/Reporting/AJ-085-html-page.feature` (the table's `HtmlPage.feature`, the story block verbatim, six scenarios, `cmp` exit 0 against the block after the amendments below); the pinned readings P-26 to P-32 are applied; the criteria 2, 4, 5 and 6 and scenario 2 carry the wording of the 2026-10-23 docs draft (S-17, S-18, S-19); the builder deviations 1 to 9 are recorded in the 2026-10-23 note at the end of this section.
User story: As a judge without a GPU, I want a static HTML page with the summary table and the chart, so that I can see the results from the demo URL without running anything.
Acceptance criteria:
1. After rendering, `docs/report/index.html` exists, `grep -c "<script" docs/report/index.html` prints 0, `grep -c "joules-per-step.svg" docs/report/index.html` prints at least 1, `grep -c "GPU-board energy only" docs/report/index.html` prints at least 1.
2. The page has the report header fields (GPU, driver, run date, local runtime, model IDs), the summary as one `<table>`, an `<img>` whose `src` is `joules-per-step.svg`, with a constant `alt`, and, when `Report:RepositoryUrl` is configured, a link whose href is the configured scheme, punycode host, port when present and path, followed by `#readme` (a fragment is dropped; otherwise the sentence `Repository link not configured (Report:RepositoryUrl).`). A configured value that is not an absolute https URL, carries user information, carries a query, or would be changed by the secret redactor (a key in the path, the query or the fragment) is refused with an `ArgumentException` that never echoes the URL.
3. A unit test renders a fixture and asserts exactly one `<tr>` per `(task, agent)` per tier in the first (aggregate) table body (test `Html_has_one_row_per_aggregate`).
4. There is no `<script>`, no `<link>` and no resource other than the sibling SVG; all file-sourced text goes through `ReportText.Clean`, then `WebUtility.HtmlEncode`, then every character above U+007E is written as a numeric character reference, so the page is ASCII.
5. `docs/.nojekyll` under the working directory is created when absent (only when `docs/` exists) and left untouched when present; an empty one is committed.
6. A tier without a run file appears as a `not run` row in a second table body.
BDD scenarios:
```gherkin
@unit @AJ-S085 @REQ-030
Feature: AJ-085 Generate the static HTML report page for the demo URL
  @positive
  Scenario: AJ-085 page has the table, the chart reference and the sentence
    Given a fixture with a local run and a nvidia run
    When the HTML page is rendered
    Then it contains exactly one table, "joules-per-step.svg" and "GPU-board energy only"
  @positive
  Scenario: AJ-085 one table row per task and agent per tier
    Given a fixture with 3 tasks, 4 agents and 1 tier
    When the HTML page is rendered
    Then the aggregate table body has 12 rows
  @positive
  Scenario: AJ-085 nojekyll file is created when absent
    Given a docs folder without .nojekyll
    When the report is written
    Then docs/.nojekyll exists
  @negative
  Scenario: AJ-085 page contains no script and no external resource
    Given any fixture
    When the HTML page is rendered
    Then the text "<script" does not appear
    And no link element and no src other than "joules-per-step.svg" appears
  @negative
  Scenario: AJ-085 hostile model ID is HTML-encoded
    Given a model ID containing "<script>alert(1)</script>"
    When the HTML page is rendered
    Then the text "<script" does not appear in the output
  @negative
  Scenario: AJ-085 existing nojekyll file is not rewritten
    Given a docs folder with a .nojekyll file of known content
    When the report is written
    Then the file content is unchanged
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-085-T1 | Write the failing BDD feature and unit tests for the HTML page | tests/AgentJoules.Specs/Features/Reporting/HtmlPage.feature, tests/AgentJoules.UnitTests/ReportRenderingTests.cs | 0.5 | AJ-083-T2 |
| AJ-085-T2 | Implement `ReportHtmlRenderer` page skeleton, inline style, header fields and chart image | src/AgentJoules.Reporting/Render/ReportHtmlRenderer.cs | 1.5 | AJ-085-T1, AJ-082-T4 |
| AJ-085-T3 | Render the aggregate table rows with `HtmlEncode` | src/AgentJoules.Reporting/Render/ReportHtmlRenderer.cs | 1.0 | AJ-085-T2 |
| AJ-085-T4 | Write `docs/.nojekyll` when absent | src/AgentJoules.Reporting/Render/NoJekyllWriter.cs | 0.25 | AJ-085-T1 |
| AJ-085-T5 | Describe the GitHub Pages demo URL path in the README | README.md | 0.25 | AJ-085-T3 |
Test mapping: `ReportRenderingTests` (required test class, trait `Spec=REQ-030`); feature file `tests/AgentJoules.Specs/Features/Reporting/HtmlPage.feature`.
Docs impact: README section "Demo URL" (Pages path `/report/`).
Definition of done:
- [x] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [x] docs listed above updated
- [x] `dotnet test --filter "Spec=REQ-030"` shows at least one passed and no failed test
Demo relevance: yes: this page is the demo or test build URL submitted to the hackathon.
Conflict: C-07 (design 9.2) - "one table row per step" versus the median over repeats; resolution: one row per `(task, agent)` per tier, each the aggregate of its repeats (AJ-082).
Note (2026-10-23, AJ-085, as delivered): the pinned readings applied are P-26 (cells from `InvariantFormat.Cell` with the clean-then-encode escape, the header from `ReportHeader.Lines` with its Markdown prefixes stripped, the models-differ line built per tier), P-27 (the first tbody holds the aggregate rows; a second `tbody class="not-run"` holds the missing tiers and is omitted when none is missing), P-28 (`Render(ReportModel, Uri?)`, no URL literal in C#, the not-configured sentence when the URI is null), P-29 (`NoJekyllWriter.EnsureExists` with `FileMode.CreateNew`, and a committed empty `docs/.nojekyll`), P-30 (one clean and one `HtmlEncode` per string, no double escaping), P-31 (the fixed skeleton, one inline style, the constant image alt text) and P-32 (the same bytes under the invariant, de-DE and tr-TR cultures, no clock read), with the kept draft pins P-7 and the docs-draft amendments S-17 (scenario 2 and criterion 6), S-18 (criteria 2 and 4) and S-19 (criterion 5). The builder deviations as delivered: (1) reversed by the fix round (2026-10-23): the page head now carries a meta CSP directly after the charset meta, `default-src 'none'; img-src 'self'; style-src 'sha256-<hash>'; base-uri 'none'; form-action 'none'`, the hash being the base64 SHA-256 of the exact style text the renderer emits, computed once at type initialisation; it was added at the security gate's recommendation, which measured in headless Chromium and Firefox over `file://` that the chart and the inline style render with this policy, with 0 violations in Chromium's console and identical rendering in Firefox (the first build had no meta tag). (2) A configured URL is refused with `ArgumentException`, and the message never echoes the URL, when it is not an absolute https URL, carries user information, carries a query (a bare question mark included), or would be changed by the secret redactor (a key in the path, the query or the fragment) (orchestrator position: accepted; the list is the spec gate's item D-2). (3) The href is built from the scheme, the punycode host (`Uri.IdnHost`), the port when present and the path, plus `#readme`, not from `AbsoluteUri`, so a configured fragment is dropped rather than doubled (orchestrator position: accepted; identical when the configured URL has no fragment and an ASCII host). (4) A selected file with no rows gets the AJ-083 `no completed steps` row with 15 cells in the first tbody (orchestrator position: accepted; consistent with summary.md). (5) The 15 headers, the label order and the flags logic are duplicated from the private members of `SummaryMarkdownRenderer` (which the brief forbids editing), with Markdown escaping replaced by clean-then-encode; six drift tests guard it after the fix round (heading equality against the summary header row; the 36 committed-fixture rows, every report fixture, a rich model and an empty later-tier file compared with the summary rows cell by cell) (orchestrator position: accepted for this story with the drift tests; a shared internal helper is carried over to AJ-087 by decision, the gatekeeper having accepted the drift tests). (6) The page prints exactly the model's disclosures, one `<p>` each, and does not force the GPU-board sentence; the aggregator always supplies `GpuBoardOnly` (orchestrator position: accepted; the page is not the honesty owner). (7) The Specs step "the report is written" calls `NoJekyllWriter.EnsureExists` directly, standing in for the AJ-086 call (orchestrator position: accepted). (8) The feature file carries S-17; the story gets S-17 from the docs draft (scenario 2 line and criterion 6 wording) (orchestrator position: accepted; the spec gate checks `cmp` after the draft lands, and it exits 0 at the end of the draft). (9) The unit tests build the repository URI at run time from the reserved name `example.test`; no `"http://` or `"https://` literal in any new or changed C# (orchestrator position: accepted as an RFC 6761 reserved name that resolves nowhere; the security gate confirms or asks for an address-free form). Fix round (2026-10-23): 79 REQ-030 unit cases and 464 class cases after the fix round (was 70 and 455); the drift tests now compare the page's cells with summary.md over every fixture and a rich model; the encoding is `ReportText.Clean`, then `WebUtility.HtmlEncode`, then a numeric character reference for every character above U+007E, so the page is ASCII, and the repository link's host is punycode (`Uri.IdnHost`).

### AJ-086: Wire the `report` command
Covers: REQ-016, REQ-030
Note (2026-10-23, AJ-086): feature file `tests/AgentJoules.Specs/Features/Cli/AJ-086-report-command.feature` (the table's `ReportCommand.feature`, the story block verbatim, `cmp` exit 0). Pinned readings P-28 to P-38 applied: `--in`, `--out` and `--config` only, `Report:RepositoryUrl` read from `--config` and refused by the configuration rule, only the selected run files checked, nothing written before every check and render succeeds, `docs/.nojekyll` only when `docs` exists. Measured: battery 1 (orchestrator, 10:02:39Z to 10:05:11Z) passed the full suite with 4437 passed, 0 failed, 2 skipped; the nine builder deviations are in the dated note at the end of this section.
Note (2026-10-23, AJ-086, spec gate D-3; not a criterion change): `report` calls `NoJekyllWriter.EnsureExists(docsDirectory)` after it writes the page, with `docsDirectory` the `docs` folder under the working directory and only when that folder exists, independent of `--out` (P-29, AJ-085). The call maps its `IOException` and `UnauthorizedAccessException` to one bounded line (a directory at the marker path raises the latter on Windows). The configuration rule (spec gate D-4) refuses `Report:RepositoryUrl` in `report` and in every command that loads the file when the value is not an absolute https URL, carries user information, a query, a fragment, an `nvapi-` key (the redactor's key pattern; `report` also refuses any registered key value before it reads a run file; the configuration is read before the input and output paths are resolved, so a configuration error precedes an input error), or surrounding whitespace; the message is the constant `ReportOptions.RepositoryUrlMessage` and never shows the value. The renderer's own refusal is a second line of defence that the command no longer reaches.
User story: As a user, I want `AJ report` to turn run files into the Markdown summary, the SVG chart and the HTML page in one command, so that regenerating the evidence is a single step.
Acceptance criteria:
1. `AJ report --in tests/AgentJoules.TestSupport/Fixtures/results --out "$TMPDIR/report"` exits 0 and writes `summary.md`, `joules-per-step.svg` and `index.html` there.
2. Defaults are `--in results/` and `--out docs/report/`; `--in` accepts files and directories.
3. With no selectable run file the command still exits 0 and renders `not run` rows and slots for every tier.
4. A missing `--in` path exits 2; a run file that fails the schema exits 2 naming the file; only the selected files are validated, the failure lines use the `validate` format on stdout, and nothing is written before every check passes.
5. Output is deterministic: two runs over the same input produce files with identical SHA-256 sums.
6. The SVG written by the command contains `<text>N/A</text>` for each hosted-tier aggregate row of the fixture. Note (2026-10-23, AJ-084 gates): the literal `<text>N/A</text>` never occurs, because every SVG text element carries attributes (`class`, `x`, `y`); the criterion is met by a `<text>` element `N/A`, counted by `grep -c "<text[^>]*>N/A</text>"`.
7. The README and content flags of the command are not registered in this milestone (M6) (check: `AJ report --help` lists `--in`, `--out` and `--config` only and exits 0); `--config` is read for `Report:RepositoryUrl` only: the whole file must pass the shape and type check (unknown keys, empty values or wrong types anywhere exit 2), the file is bounded at 1 MiB, and no other cross-field or range rule is applied by `report`.
8. Three small fixture run files (local, nvidia, nebius) are committed under `tests/AgentJoules.TestSupport/Fixtures/results/`; they are marked as fixtures through `run.id` and `run.gitCommit` and never feed any published number.
BDD scenarios:
```gherkin
@unit @AJ-S086 @REQ-016 @REQ-030
Feature: AJ-086 Wire the report command
  @positive
  Scenario: AJ-086 report writes the three files from fixtures
    Given the fixture results folder
    When "report --in <fixtures> --out <tmp>" is executed
    Then the exit code is 0
    And summary.md, joules-per-step.svg and index.html exist in the output folder
  @positive
  Scenario: AJ-086 empty input still renders four not-run rows
    Given an empty results folder
    When "report --in <empty> --out <tmp>" is executed
    Then the exit code is 0
    And the summary lists "local | not run", "cloudgpu | not run", "nvidia | not run" and "nebius | not run"
  @positive
  Scenario: AJ-086 two runs produce identical files
    Given the fixture results folder
    When the report is generated twice
    Then the SHA-256 sums of the three files are equal between the two runs
  @negative
  Scenario: AJ-086 missing input path exits 2
    Given a path that does not exist
    When "report --in <path>" is executed
    Then the exit code is 2
  @negative
  Scenario: AJ-086 schema-invalid run file exits 2 naming the file
    Given a results folder with one run file where cost has label "N/A" and value 0.5
    When "report --in <folder> --out <tmp>" is executed
    Then the exit code is 2 and the output names the file
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-086-T1 | Write the failing BDD feature and unit tests for the report command | tests/AgentJoules.Specs/Features/Cli/ReportCommand.feature, tests/AgentJoules.UnitTests/ReportRenderingTests.cs | 0.5 | AJ-085-T1, AJ-084-T1 |
| AJ-086-T2 | Confirm the three fixture run files committed in AJ-080 render | tests/AgentJoules.TestSupport/Fixtures/results/20260101T000000Z-local.json, tests/AgentJoules.TestSupport/Fixtures/results/20260101T000000Z-nvidia.json, tests/AgentJoules.TestSupport/Fixtures/results/20260101T000000Z-nebius.json | 0.25 | AJ-086-T1, AJ-079-T4 |
| AJ-086-T3 | Implement `ReportCommand` options and defaults | src/AgentJoules.Cli/Commands/ReportCommand.cs | 1.0 | AJ-086-T1 |
| AJ-086-T4 | Wire selector, validator, aggregator and the three renderers | src/AgentJoules.Cli/Commands/ReportCommand.cs, src/AgentJoules.Core/Configuration/ReportOptions.cs, config/appsettings.json | 1.25 | AJ-086-T3, AJ-081-T3 |
| AJ-086-T5 | Document `report` usage and its outputs | README.md | 0.25 | AJ-086-T4 |
Test mapping: `ReportRenderingTests` (required test class, traits `Spec=REQ-016` and `Spec=REQ-030`); feature file `tests/AgentJoules.Specs/Features/Cli/ReportCommand.feature`.
Docs impact: README section "Report" (command, defaults, outputs); `docs/report/` is generated, not hand-written.
Definition of done:
- [x] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [x] docs listed above updated
- [x] the exit-criteria commands for `report` in this file pass
Demo relevance: yes: step 7 of the demo is `AJ report`.
Conflict: C-05 (design 9.2) - `--config` has per-command meaning; on `report` it is a plain path option.
Note (2026-10-23, AJ-086, as delivered): the pinned readings P-28 to P-38 are applied as the brief records them: P-28 through `ReportOptions` (`AgentJoulesOptions.Report`, checked by the `CrossFieldValidator` rule, which uses `ReportOptions.IsRepositoryUrl`), and P-29 with `docs/.nojekyll` created only when `docs` exists, whatever `--out` is. Public surface beyond the brief: `CommandHost` gains the internal members `IncludeProcessEnvironment` and `Redactor`; `ReportOptions.IsRepositoryUrl(string?, out Uri?)` is a public helper; `ReportOptions.RepositoryUrlMessage` is the one constant message of the configuration rule. The fix round (2026-10-23) changed user-visible behaviour: (a) keys: a selected run file whose text the host's secret redactor would change is refused with the validate-format line `<file>: (root) holds a registered key` (exit 2, nothing written); a rendered text that would carry a registered value is refused, not masked, with `report: <name> holds a registered key; nothing written` (exit 2, nothing written), so the written bytes are always the exact deterministic render; (b) writes: the three texts are staged as `<name>.partial` and flushed first, then moved with `File.Move(overwrite: true)` in the order summary.md, joules-per-step.svg, index.html; a staging failure discards the partial files, leaves the previous set in place and exits 1 naming the file, while a failed move (a read-only or locked target, a folder at the target) discards the remaining partial files and exits 1 naming the file but leaves the files moved before it new, so the folder can hold a mixed set until the command is re-run, and `docs/report/*.partial` is ignored by git; (c) the `--in`, `--out` and `--config` values and the written line are redacted before the 256-character cut, in `report` and in `validate`; (d) the configuration file is read with a bound of 1 MiB (one byte over is refused), a non-regular file is refused on Unix, and a lone surrogate or any malformed content exits 2 naming the file only; the whole file passes the shape and type check (unknown keys, empty values or wrong types anywhere exit 2), and only `Report:RepositoryUrl` is judged beyond that, the cross-field and range rules of other sections not being applied by `report`; (e) a selected file the aggregator refuses (status, non-publishable flag, tier or variant against the name, timestamp against the name) prints `<file>: <member text>` on stdout and `report: 1 run file cannot be used` on stderr, exit 2, never a value; (f) the configuration rule refuses `Report:RepositoryUrl` in `report` and in every command that loads the file when it is not an absolute https URL, carries user information, a query, a fragment, an `nvapi-` key (the redactor's key pattern; `report` also refuses any registered key value before it reads a run file; the configuration is read before the input and output paths are resolved, so a configuration error precedes an input error), or surrounding whitespace; the message is `ReportOptions.RepositoryUrlMessage` and never shows the value; the renderer's own refusal is a second line of defence that the command no longer reaches; (g) an `--out` that cannot be created (a path under a file) exits 1 with `report: the output folder could not be created`, naming no file; an `--out` that is a file exits 2; the tails read `1 more file cannot be used`, `N more files cannot be used`, `Skipped 1 more file` and `Skipped N more files`; `--help` prints each default once. Counts (h): the first build added 27 unit cases and 5 configuration tests, the fix round 20 more and 1 more configuration test; after the last minors `Spec=REQ-030` has 102 cases, the rendering class 521 and the configuration class 165. The builder deviations as delivered, with the orchestrator position: (1) the process environment is a configuration source when `CommandHost.IncludeProcessEnvironment` is true (`overrides is null || overrides.IncludeProcessEnvironment`), where the brief said "unless `CliHostOverrides` is non-null", so one test can prove `AGENTJOULES__Report__RepositoryUrl` works (accepted); (2) reversed by the fix round: the three rendered texts are no longer passed through `CommandHost.Redactor` and nothing is rewritten; a text that would carry a registered value is refused under (a) instead, so the bytes stay the exact render (accepted; the refusal replaces the masking); (3) `ReportOptions.IsRepositoryUrl` is a public helper shared by the validator and the command, where the brief named only the validator rule (accepted); (4) `--in` is `Option<string[]>` with `AllowMultipleArgumentsPerToken = false` and no `ExactlyOne` arity, because `ExactlyOne` broke the `string[]` conversion, so a bare `--in` and `--in a b` are parse errors and exit 2 (accepted); (5) messages name the configuration file by file name only, and the `--in`, `--out` and `--config` echoes use the value as typed, redacted, then cut to 256 characters (accepted); (6) `Report written to <--out as given>` echoes the value as typed (redacted under (c)), so an absolute `--out` is echoed back (accepted; the gates judge it against the path rule, since the user typed it); (7) a malformed configuration file raised a `JsonReaderException` that the first catch did not cover and exited 1; `JsonException` is now caught and gives exit 2 naming the file only (accepted); (8) no separate R1 re-check: `ReportAggregator.Build` already refuses a status other than completed or partial, a `non-publishable` flag and a tier or variant that disagrees with the file name, each an `ArgumentException` mapped to exit 2, and a test covers the timestamp mismatch (accepted; the spec gate confirms against the R1 wording); (9) Problem lines are capped at 20, then `N more files cannot be used`, a cap the brief did not ask for (accepted). Builder red and green (09:43Z to 10:04Z, first build): red, exit 1, `ReportRenderingTests` 25 failed, 465 passed, 1 skipped of 491, and `Category=AJ-S086` 5 failed, 0 passed; green, `ReportRenderingTests` 490 passed, 1 skipped, 0 failed, and `Category=AJ-S086` 5 passed. The exit-criteria command writes `summary.md` (10,431 bytes), `joules-per-step.svg` (20,860 bytes) and `index.html` (15,299 bytes) with exit 0; the SVG holds 24 `N/A` text markers and 12 `not run` markers; `validate` on the same folder prints `Validated 3 files`. Docs amendments of 2026-10-23 (AJ-086 docs): S-7 (criterion 6, "aggregate row"), S-20 (criteria 4 and 7) and S-21 (the T4 files cell) are applied in place, the fix-round wording (d) and (f) is applied to criterion 7 and to the spec-gate note above; the Gherkin block is unchanged. The README section for `report` sits under CLI reference, not under a section named Report; the story's Docs impact line is left as written.

### AJ-087: Render the README results block as a deterministic function
Covers: REQ-016, EDGE-002
Note (2026-10-23, AJ-087): feature file `tests/AgentJoules.Specs/Features/Reporting/AJ-087-readme-block.feature` (the story's `ReadmeBlock.feature` mapping, renamed; the Gherkin block is identical to the story block, `cmp` exit 0). Pinned readings P-39 to P-44 applied: `Render(model, dashboardScreenshotExists)` over the `ReportModel` of the latest and latest-baseline files, one row per file from its totals with the baseline as `<tier> baseline` after its tier, `ConcurrentCostOverlap` under the table for a concurrent tier, scenario 4 reading the N/A cells by header index, the trait `Spec=REQ-016` with `Spec=EDGE-002` on the sampling cases, and the fixtures `CloudGpuPartialSampling` and `SelfJudged`. Measured: red `ReadmeBlockTests` failed 39, passed 3 (exit 1) and `Category=AJ-S087` failed 5 (exit 1); green `ReadmeBlockTests` 42 passed, `Category=AJ-S087` 5 passed, `Spec=REQ-016` 564 passed 1 skipped, `Spec=EDGE-002` 14 passed; two renders of the committed fixtures byte-identical (`cmp` exit 0); summary.md, index.html and joules-per-step.svg sha256 equal before and after the shared helper; battery 1 (orchestrator, 12:12:13Z to 12:14:42Z) 4517 passed, 0 failed, 2 skipped. Deviations: (1) the block has 11 columns in the P-40 order, the story naming none; (2) an empty cost map printed `N/A (no cost)` and a N/A cost entry `N/A (reason)` without its currency key (both changed in the fix round below); (3) the block writes every character above U+007E as a numeric character reference after the Markdown escape, so the committed README stays ASCII, while summary.md still writes characters above U+00FF raw (the AJ-083 residual); (4) the shared `ReportTable` helper (internal, `Reporting.Render`) is now called by the summary, the page and the block, and `ReportHeader.Status` became internal rather than duplicated, with no output change; (5) the block ends with one LF, and the fixtures set their environment (`Cloud GPU`, driver 555.00, FP8 for `CloudGpuPartialSampling`; the judge reading on the nvidia tier for `SelfJudged`) with no existing fixture output changed. Fix round (2026-10-23, after three APPROVED round-1 gates): an N/A cost entry prints `N/A <CUR> (<reason>)` per currency key (spec gate round 1, R-1: the N/A metric holds no currency, so the entry's key is printed beside the label and the reason), the empty cost map prints `N/A (no priced step)` (P-40 amended by the orchestrator for the nvidia free-tier case), and the P-44 test `Baseline_row_after_tier` was delivered as `A_baseline_file_is_the_row_after_its_tier`. The README block emits the numeric reference `&#8288;` (word joiner) before every `:` and `.` of a file-sourced string (the cut marker and version strings included; the `@` joiner of the first fix round was dropped in the second, because the round-2 gatekeeper measured that it turns a defused e-mail address into a GitHub user mention in comment rendering while the dot joiner alone breaks the e-mail autolink in both modes) and replaces `$` with `?`, because GitHub's post-render filters autolink URLs, `www.` hosts and e-mail addresses and turn `:name:` into emoji whatever the Markdown escape (measured by the security gate on api.github.com/markdown); summary.md and the page keep the shared escape, an M3-exit item, and the cloud fixture's VM cost is a measured USD metric because the schema types it so (design item C-18). Counts: `ReadmeBlockTests` 65 passed (42 at first green, 60 after fix round 1, 65 after fix round 2), `Spec=REQ-016` 587 passed 1 skipped, `Spec=EDGE-002` 14 passed, `Category=AJ-S087` 5 passed, build 0 warnings, format clean, the three committed renders byte-identical to HEAD. A selected file without rows prints its totals as written (P-40, orchestrator ruling), with `partial` in the status and flags cells; the summary's `no completed steps` row is that surface's rule.
Note (2026-10-20, M1 exit): EDGE-002 added per plan section 5 seam row 6 (README and tier-row `sampling` disclosures reference EDGE-002).
User story: As a reader of the README, I want a results table generated from the latest run file per tier with the same honesty labels, so that the README never carries a hand-typed number.
Acceptance criteria:
1. `ReadmeResultsBlock.Render(model, dashboardScreenshotExists)` over the report model of the latest and latest-baseline file per tier returns a table with one row per selected file (tier and GPU; a baseline file as `<tier> baseline`) built from the file's totals, including `cloudgpu | not run` when no cloudgpu file exists, and `local`, `nvidia`, `nebius` rows from fixtures (test `Block_has_a_row_per_tier`).
2. The block header lists GPU name, driver version, run date (UTC), local runtime and model IDs, and the sentence `Energy is GPU-board energy only.`
3. The words `partial` and `sampling` appear where the run status or method says so; baseline runs are labelled `baseline`.
4. The block names the judge model, states that judge calls are excluded from workflow totals, and discloses self-judging when `run.selfJudged` is true.
5. Hosted energy cells read `N/A`; cost keeps its currency; tiers are never merged.
6. Output is deterministic: two renders are byte-identical; no generation timestamp; LF newlines (test `Two_renders_identical`: the two strings are equal and contain no CR).
7. This story delivers the pure function; M6 wires the update and verify flags and the README markers (check: `grep -rn "results:begin" src` returns no hit in this milestone).
BDD scenarios:
```gherkin
@unit @AJ-S087 @REQ-016
Feature: AJ-087 Render the README results block as a deterministic function
  @positive
  Scenario: AJ-087 block has one row per tier and a not-run cloudgpu row
    Given latest-run fixtures for local, nvidia and nebius only
    When the block is rendered
    Then the block has rows for "local", "nvidia" and "nebius"
    And it has the row "cloudgpu | not run"
  @positive
  Scenario: AJ-087 two renders are identical
    Given the same latest-run fixtures
    When the block is rendered twice
    Then the two outputs are byte-identical
  @positive
  Scenario: AJ-087 judge disclosure is present and names self-judging
    Given fixtures where run.selfJudged is true
    When the block is rendered
    Then the block names the judge model, says judge calls are excluded from workflow totals and discloses self-judging
  @negative
  Scenario: AJ-087 hosted energy is N/A in the README block
    Given a nvidia fixture
    When the block is rendered
    Then its energy cells contain "N/A" and not "0"
  @negative
  Scenario: AJ-087 partial and sampling runs are labelled
    Given a cloudgpu fixture with status "partial" and method "sampling"
    When the block is rendered
    Then the cloudgpu row contains "partial" and "sampling"
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-087-T1 | Write the failing BDD feature and unit tests for the README block | tests/AgentJoules.Specs/Features/Reporting/ReadmeBlock.feature, tests/AgentJoules.UnitTests/ReadmeBlockTests.cs | 0.5 | AJ-083-T2 |
| AJ-087-T2 | Implement `ReadmeResultsBlock.Render` rows and header | src/AgentJoules.Reporting/Readme/ReadmeResultsBlock.cs | 1.5 | AJ-087-T1, AJ-082-T4 |
| AJ-087-T3 | Add the judge, partial, sampling and baseline disclosures | src/AgentJoules.Reporting/Readme/ReadmeResultsBlock.cs | 0.75 | AJ-087-T2 |
| AJ-087-T4 | Assert byte-identical output across two renders | tests/AgentJoules.UnitTests/ReadmeBlockTests.cs | 0.5 | AJ-087-T2 |
| AJ-087-T5 | Note the pending marker wiring in the build log | docs/build-log.md | 0.25 | AJ-087-T3 |
Test mapping: `ReadmeBlockTests` (design class, trait `Spec=REQ-016`, with `Spec=EDGE-002` on the `sampling` disclosure case); feature file `tests/AgentJoules.Specs/Features/Reporting/AJ-087-readme-block.feature`.
Docs impact: `docs/build-log.md`; README markers and the generated block are written in M6.
Definition of done:
- [x] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [x] docs listed above updated
- [x] two renders of the same fixtures are byte-identical
Demo relevance: yes: the README block is shown in the video with the honesty sentence.

### AJ-088: Prove the required test classes and gate the integration suite
Covers: REQ-023
User story: As a reviewer, I want proof that the six required unit test classes exist, that the unit suite needs no GPU, network or key, and that integration tests are skipped unless explicitly enabled, so that `dotnet test` is safe on any machine.
Acceptance criteria:
1. `dotnet test --list-tests` output contains `MeteredChatClientTests`, `AttributionBaselineTests`, `CostModelTests`, `JudgeParsingTests`, `ReportRenderingTests` and `SchemaValidationTests` (checked by `dotnet test --list-tests | grep -c "<name>"` printing at least 1 for each, and by the unit test `Required_classes_exist`).
2. With `AGENTJOULES_INTEGRATION`, `NVIDIA_API_KEY` and `NEBIUS_API_KEY` unset, `dotnet test` exits 0 with 0 failed and the integration tests are reported as skipped.
3. `IntegrationGate` and `[IntegrationFact]` live in `tests/AgentJoules.TestSupport` and are defined once by an earlier story (AJ-007-T4a (M0, creates the single IntegrationFactAttribute) and AJ-023-T2 (M1, points it at IntegrationGate)); this story only adds tests that use them: with the variable not `1` they are skipped with `Set AGENTJOULES_INTEGRATION=1`; with the variable set but a required key absent, the key-bound tests are skipped with a reason naming the missing variable, not failed (test `Key_bound_test_skips_naming_variable`).
4. `grep -rn "new HttpClient(" tests/AgentJoules.UnitTests` returns no hit, and setting `NVIDIA_API_KEY` to a runtime-built value does not change the unit suite result.
5. The passed, failed and skipped counts of the run are recorded in `docs/build-log.md` (the final counts are re-recorded in M7).
6. `AgentJoules.IntegrationTests` holds a gated live-model-list test per tier that has a key (it never runs in CI).
BDD scenarios:
```gherkin
@unit @AJ-S088 @REQ-023
Feature: AJ-088 Prove the required test classes and gate the integration suite
  @positive
  Scenario: AJ-088 the six required classes are present
    Given the unit test assembly
    When the required class names are checked
    Then no required class is missing
  @positive
  Scenario: AJ-088 integration facts are skipped without the gate variable
    Given AGENTJOULES_INTEGRATION is not set
    When the integration attribute is evaluated
    Then the skip reason is "Set AGENTJOULES_INTEGRATION=1"
  @negative
  Scenario: AJ-088 a missing required class is detected
    Given a class list that lacks "CostModelTests"
    When the required class names are checked
    Then the check fails and names "CostModelTests"
  @negative
  Scenario: AJ-088 gate on but key absent skips instead of failing
    Given AGENTJOULES_INTEGRATION is "1" and NVIDIA_API_KEY is absent
    When the nvidia live-model test is evaluated
    Then it is skipped with a reason naming "NVIDIA_API_KEY"
  @negative
  Scenario: AJ-088 a set key does not change the unit suite
    Given NVIDIA_API_KEY is set to a runtime-built value
    When the unit suite runs
    Then the failed count is 0 and no test contacted a network endpoint
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-088-T1 | Write the failing BDD feature and the required-class check test | tests/AgentJoules.Specs/Features/Cli/TestSuites.feature, tests/AgentJoules.UnitTests/RequiredClassesTests.cs | 0.5 | AJ-087-T1 |
| AJ-088-T2 | Implement `RequiredClasses.Missing` over a type list | tests/AgentJoules.TestSupport/RequiredClasses.cs | 0.5 | AJ-088-T1 |
| AJ-088-T3 | Add the gated live model-list tests per tier using the existing `[IntegrationFact]` | tests/AgentJoules.IntegrationTests/LiveModelsIntegrationTests.cs | 1.0 | AJ-088-T1 |
| AJ-088-T4 | Run the suite with the gate and keys unset and record the counts | docs/build-log.md | 0.5 | AJ-088-T3, AJ-088-T2 |
| AJ-088-T5 | Document how to run unit and integration tests | README.md | 0.5 | AJ-088-T4 |
Test mapping: `RequiredClassesTests` (spec trait `Spec=REQ-023`) over the six required classes; feature file `tests/AgentJoules.Specs/Features/Cli/TestSuites.feature`.
Docs impact: README section "Testing" (`dotnet test`, `AGENTJOULES_INTEGRATION=1`); `docs/build-log.md` (counts).
Definition of done:
- [ ] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [ ] `dotnet test --list-tests` contains all six class names and `dotnet test` exits 0 with the gate variable unset
Demo relevance: no.

### AJ-089: Fan readings out to a dashboard sink that can never block the run
Covers: REQ-032, EDGE-019
Note (2026-10-23, AJ-089): feature file `tests/AgentJoules.Specs/Features/Dashboard/AJ-089-sink-fan-out.feature` (the `SinkFanOut.feature` mapping of the test mapping, renamed; the Gherkin block is identical to the story block, `cmp` exit 0). Pinned readings P-39 to P-60 applied as the brief records them, with the kept draft pins P-1 and P-3 to P-8; S-1 to S-5, S-7, S-16 and S-17 applied to the story and design texts. Measured: red `DashboardSinkTests` failed 37 and passed 6 of 43, `Category=AJ-S089` failed 10 and passed 2 of 12 (exit codes not recorded); green `DashboardSinkTests` 43 of 43, `Category=AJ-S089` 12 of 12, `Spec=EDGE-019` 43 passed, `Spec=REQ-032` 52 passed, `ConfigValidationTests` 174 of 174; a 1 s window at 10 Hz makes 13 power and 2 counter NVML calls with and without the dashboard; battery 1 4604 passed, 0 failed, 2 skipped. Deviations: `FanOutMetricsSink` has an ordinary constructor, the orchestrator locals are `workflowSink` and `liveReadings`, and the members beyond P-44 were made internal in the fix round; `DashboardJsonContext` of P-45 is created by AJ-090-T2 when it fills `data`; AJ-089 writes `"data":{}` through Utf8JsonWriter and needs none (orchestrator ruling). Open questions 1 to 3 (a `Connect` after the end returns a completed connection; the snapshot clock and replay flag; whole-millisecond rounding of the `ShortIntervalNote` period) are accepted by the orchestrator. Fix round (2026-10-23, builder-reported, exit 0 each): the forwarding effective interval is the smallest whole multiple of the sampling period at or above `Dashboard:PowerIntervalMs` (P-55 amended by the orchestrator after the spec gate measured 150 ms at 10 Hz forwarding every 100 ms), with the half-period jitter tolerance kept. `FlushAsync` counts an event before the write so every returned `Emit` is covered (a race both the security and the gatekeeper passes measured), and on the first pump fault, the one that is logged, the loop logs outside the lock and then counts the event as settled (a later fault settles in one pass under the lock) (a log-after-flush ordering the builder caught in its own runs). The snapshot's state members beyond P-44 are internal; the two Warning calls and the `DisposeAsync` await never throw; `AllowSynchronousContinuations = false` is explicit and pinned. Counts: `DashboardSinkTests` 67 passed (43 at first green, 57 after the fix round, 67 after fix round 2), `Spec=REQ-032` 76 passed, `Spec=EDGE-019` 67 passed, `Category=AJ-S089` 12 passed, the five-class regression filter 680 passed, build 0 warnings, format clean; 17 of 18 mutants killed, the one survivor (the `DisposeAsync` SuppressThrowing guard) unkillable once every logger call is guarded. Fix round 2, after the round-2 gates: the forwarding ceiling allows one tick of slack per period, because `TimeSpan.FromMilliseconds` truncates a period that is not a whole number of ticks (6 Hz with 1000 ms now forwards every 6th sample, 30 Hz with 100 ms every 3rd); the two-phase fault ordering is pinned by the gatekeeper's race probes; the pump's per-event closure is gone (a static non-capturing log helper, pinned by reflection); five more mutants killed.
User story: As a user watching a run live, I want readings and power samples forwarded to a dashboard through bounded, drop-oldest buffers, so that an absent or slow browser can never delay a measured window or change a result.
Acceptance criteria:
1. `FanOutMetricsSink` forwards each reading to every target; an exception from one target is caught, logged once per target at Warning with type names only, and never reaches the caller or the other targets (test `Throwing_target_leaves_in_memory_sink_complete`).
2. `DashboardSink.Emit` performs one `TryWrite` into a bounded ingress channel (capacity 1000, `DropOldest`) and returns; 1 500 events with no reader leave the per-connection buffer holding the newest 1 000 (test `Connection_buffer_keeps_newest_1000`).
3. `Emit` keeps returning normally while the pump is faulted; the first pump fault is logged at Warning with the exception type only, the faulting event is dropped and the pump loop continues (test `Emit_returns_while_pump_faulted`).
4. A fake slow client that never reads does not stop publishing: 10 000 `Emit` calls complete within the test timeout and the per-connection buffer never exceeds 1 000 entries.
5. `LiveEvent` records (`RunStartedEvent`, `BaselineEvent`, `ProgressEvent`, `StepStartedEvent`, `StepProgressEvent`, `StepCompletedEvent`, `PowerEvent`, `RunCompletedEvent`) and `ILiveEventSink` with `NullLiveEventSink` live in `AgentJoules.Core`; with no `--dashboard` the null sink is used.
6. `src/AgentJoules.Dashboard` references Core and Metering only; `grep -rn "Microsoft.AspNetCore" src --include=*.csproj` hits only the Dashboard project file.
7. `Dashboard:PowerIntervalMs` (default 100, allowed 50 to 1000) is bound and validated: a value of 25 or 1001 exits 2 naming the key (test `Power_interval_out_of_range_exits_2`); a value of 50, 100 or 1000 passes.
8. The meter forwards the power samples it already took to `ILiveEventSink` at most once per `Dashboard:PowerIntervalMs` and makes no extra NVML call (test `Power_forwarding_follows_interval_without_extra_nvml_calls`: with a 100 ms interval on a fake clock a 1 s window emits at most 10 `PowerEvent`s, and the `ScriptedNvmlApi` call count equals the count of a run without the dashboard); when the interval is shorter than the sampling period every sample is forwarded and one startup note says so (test `Short_interval_forwards_every_sample`) (the effective interval is the smallest whole multiple of the sampling period at or above the configured value).
9. `StepProgressEvent` carries the count of output chunks received so far for the active step, is emitted from the streaming path of `MeteredChatClient` and is throttled to at most 10 events per second (test `Step_progress_throttled_to_10_per_second`: 100 chunks within 1 s on a fake clock give at most 10 events and the last count is monotonic); a buffered call emits none.
BDD scenarios:
```gherkin
@unit @AJ-S089 @REQ-032 @EDGE-019
Feature: AJ-089 Fan readings out to a dashboard sink that can never block the run
  @positive
  Scenario: AJ-089 a reading reaches both the in-memory sink and the dashboard sink
    Given a fan-out sink over an in-memory sink and a dashboard sink
    When one reading is published
    Then the in-memory snapshot holds 1 reading
    And the dashboard sink received a step-completed event
  @positive
  Scenario: AJ-089 without the dashboard the null live sink is used
    Given a run without --dashboard
    When the live sink is resolved
    Then it is the null live sink
  @negative
  Scenario: AJ-089 a throwing target does not break the other target
    Given a fan-out sink whose second target throws on every reading
    When 5 readings are published
    Then the in-memory sink holds 5 readings
    And no exception reached the caller
  @negative
  Scenario: AJ-089 a slow client keeps only the newest 1000 events
    Given a connected client that never reads
    When 1500 events are emitted
    Then the connection buffer holds 1000 events and they are the newest 1000
  @negative
  Scenario: AJ-089 emit returns while the pump is faulted
    Given a dashboard sink whose pump throws on the first event
    When 10 events are emitted
    Then every emit call returned normally
    And the pump error was logged once
  @positive
  Scenario: AJ-089 power interval within range is accepted
    Given Dashboard:PowerIntervalMs is set to 100
    When any command starts
    Then configuration validation passes
  @positive
  Scenario: AJ-089 power samples are forwarded at the configured interval without extra NVML calls
    Given a scripted NVML API and Dashboard:PowerIntervalMs 100 on a fake clock
    When a 1 second window is metered
    Then at most 10 power events were emitted
    And the NVML call count equals the count of a run without the dashboard
  @positive
  Scenario: AJ-089 streaming progress is throttled to ten events per second
    Given a streaming step that delivers 100 output chunks within 1 second on a fake clock
    When the step is metered
    Then at most 10 step-progress events were emitted and the last chunk count is monotonic
  @negative
  Scenario: AJ-089 power interval below 50 exits 2
    Given Dashboard:PowerIntervalMs is set to 25
    When any command starts
    Then the exit code is 2
    And the output names "Dashboard:PowerIntervalMs"
  @negative
  Scenario: AJ-089 power interval above 1000 exits 2
    Given Dashboard:PowerIntervalMs is set to 1001
    When any command starts
    Then the exit code is 2
  @negative
  Scenario: AJ-089 a buffered call emits no step-progress event
    Given a buffered step
    When the step is metered
    Then no step-progress event was emitted
  @negative
  Scenario: AJ-089 an interval shorter than the sampling period forwards every sample
    Given a meter sampling every 200 ms and Dashboard:PowerIntervalMs 100
    When a 1 second window is metered
    Then every sample is forwarded as a power event
    And one startup note says the interval is shorter than the sampling period
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-089-T1 | Write the failing BDD feature and unit tests for fan-out and the sink | tests/AgentJoules.Specs/Features/Dashboard/AJ-089-sink-fan-out.feature, tests/AgentJoules.UnitTests/DashboardSinkTests.cs | 0.75 | AJ-056-T1 |
| AJ-089-T2 | Create the Dashboard project file with the ASP.NET Core framework reference and add it to the solution | src/AgentJoules.Dashboard/AgentJoules.Dashboard.csproj, AgentJoules.slnx | 0.5 | AJ-089-T1 |
| AJ-089-T3 | Create `LiveEvent`, `ILiveEventSink` and `NullLiveEventSink` | src/AgentJoules.Core/Live/LiveEvent.cs, src/AgentJoules.Core/Live/ILiveEventSink.cs, src/AgentJoules.Core/Live/NullLiveEventSink.cs | 1.0 | AJ-089-T1 |
| AJ-089-T4 | Implement `FanOutMetricsSink` with per-target isolation | src/AgentJoules.Metering/FanOutMetricsSink.cs | 0.75 | AJ-089-T1, AJ-040-T3 |
| AJ-089-T5 | Implement `DashboardSink` ingress channel, pump and per-connection channels | src/AgentJoules.Dashboard/DashboardSink.cs, src/AgentJoules.Dashboard/DashboardConnection.cs, src/AgentJoules.Dashboard/DashboardEventSerializer.cs, src/AgentJoules.Dashboard/AssemblyAttributes.cs | 1.75 | AJ-089-T3, AJ-089-T2 |
| AJ-089-T6 | Implement `DashboardSnapshot` (latest run, baseline, per-agent state, in memory only) | src/AgentJoules.Dashboard/DashboardSnapshot.cs | 1.0 | AJ-089-T5 |
| AJ-089-T6a | Add `DashboardOptions.PowerIntervalMs` (default 100, range 50 to 1000) to the options and the validator | src/AgentJoules.Core/Configuration/DashboardOptions.cs, src/AgentJoules.Core/Configuration/FlatSectionsValidator.cs, config/appsettings.json, src/AgentJoules.Core/Configuration/AgentJoulesOptions.cs, src/AgentJoules.Core/Configuration/ConfigurationShape.cs | 0.25 | AJ-089-T1 |
| AJ-089-T6b | Create `StepProgressEvent` and emit it, throttled to 10 per second, from the streaming path | src/AgentJoules.Core/Live/LiveEvent.cs, src/AgentJoules.Metering/MeteredChatClient.cs, src/AgentJoules.Metering/MeteringServices.cs | 0.5 | AJ-089-T3, AJ-089-T6a |
| AJ-089-T6c | Forward the power samples the meter already took to the live sink at the `Dashboard:PowerIntervalMs` interval (every sample when the interval is shorter) and wire the live sink, the fan-out and the forwarding into the orchestrator | src/AgentJoules.Core/Energy/SampledEnergyMeter.cs, src/AgentJoules.Workflow/Runs/RunOrchestrator.cs, src/AgentJoules.Cli/Hosting/AgentJoulesHostFactory.cs | 1.0 | AJ-089-T6a, AJ-089-T3 |
| AJ-089-T7 | Record the `Spec=EDGE-019` buffer results in the build log | docs/build-log.md | 0.25 | AJ-089-T6 |
Test mapping: `DashboardSinkTests` (design class, traits `Spec=EDGE-019` and `Spec=REQ-032`, including the power forwarding and progress throttling tests; the range test `Power_interval_out_of_range_exits_2` lives in `ConfigValidationTests`); feature file `tests/AgentJoules.Specs/Features/Dashboard/AJ-089-sink-fan-out.feature`.
Docs impact: `docs/build-log.md`; `config/appsettings.json` and the README configuration table gain `Dashboard:PowerIntervalMs`; the dashboard section of the README is written in AJ-091 and AJ-092.
Definition of done:
- [x] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [x] docs listed above updated
- [x] `dotnet test --filter "Spec=EDGE-019"` shows at least one passed and no failed test
- [x] `grep -rn "Microsoft.AspNetCore" src --include=*.csproj` lists only the Dashboard project
Demo relevance: no (internal; keeps the live view from touching the measured path).
Conflict: C-13 (design 9.2) - REQ-032 says readings stream "from the same metrics sink", but live power and step status are not readings; resolution: readings reach the dashboard through `FanOutMetricsSink` over the same sink instances as the results file, while power, progress and step-started travel as `LiveEvent`s from samples and state the run already computed.

### AJ-090: Serialize live events and embed the dashboard page
Covers: REQ-032
Note (2026-10-23, AJ-090): feature file `tests/AgentJoules.Specs/Features/Dashboard/AJ-090-live-page.feature` (the `LivePage.feature` mapping of the test mapping, renamed; the Gherkin block is identical to the story block, `cmp` exit 0 as the builder measured). Pinned readings P-41, P-45, P-46, P-61 to P-69 and the P-7 page rules applied as the brief records them, with the kept draft pins P-2 to P-6, P-8 and P-9; S-18 to S-21 applied to this story, S-8 and S-11 to the design. Measured: red, the four unit classes failed 49 and passed 5 of 54 and `Category=AJ-S090` failed 25 of 25 (exit codes not recorded); green, the four unit classes 54 of 54, `Category=AJ-S090` 25 of 25, `Spec=REQ-032` 130 passed 0 failed, Specs `AJ-S089|AJ-S085` 18 of 18, build 0 warnings; battery 1 4707 passed, 0 failed, 2 skipped (Specs 399/0/0, UnitTests 4308/0/1); page greps `<script src` 0, `innerHTML` 0, `setTimeout` 0. Deviations: a new `DashboardPageRules.cs` test-support file holds the page checks shared by the unit tests and the Specs; the snapshot and the sink take a `SecretRedactor?` with a null default that masks the `nvapi-` pattern only, so AJ-091 must pass the registered redactor; the page link follows the AJ-085 URL rule (no fragment, `#readme` appended); wire members beyond the pins are listed in the build log. Rulings: the run-level producers (`RunStartedEvent` and the rest) are AJ-091-T6, and the snapshot carries no power history, a decision item for the user at the M3 exit. Fix round (2026-10-23, after the three gates): the redactor is now a required, non-null constructor parameter of `DashboardSink` and `DashboardSnapshot` (`ArgumentNullException.ThrowIfNull`), so the null default named above no longer exists and the host passes its registered `SecretRedactor`. The snapshot tracks at most the four known agents plus eight more in order of appearance; a step of a further agent still counts in `steps` and the completed list, and every payload with agents carries an `agentsOmitted` count that the page shows as a fixed sentence (binding for AJ-092's replay input). The snapshot carries the last power sample (`watts` and `sampleSeconds`, label kept, N/A included), which a late or reconnecting client restores; the running chunk count is not-reported `no-progress` until the first progress, and a judge reading adds no card, count or flag. Sums and running energy are not-reported `overflow` when a value is not finite, a snapshot always serializes (a minimal fallback otherwise), and every control, format (bidi included), separator and non-character in a string is replaced by `?` after redaction through the Core `UnsafeText` helper shared with the report renderers, while the repository link is built by the Core `RepositoryLink` helper shared with the report page. The page wraps flags and reasons, labels the baseline, schedules the next frame in a finally block and bounds the axis ticks, and the no-estimates test requires a numeric running estimate. Fix-round counts (builder-measured, exit 0 each): the four AJ-090 unit classes 83 passed (54 at first green, 75 after the fix round, 83 after fix round 2), `Spec=REQ-032` 161 passed, `DashboardSinkTests` 67 passed, `Category=AJ-S090|AJ-S089` 37 passed, build 0 warnings, format clean, seven report renders hash-identical before and after, every named mutant killed. Fix round 2, after the round-2 gates: a judge step-started creates no running entry, card, active agent or flag; a capped agent's running window still counts for the live overlap rule; every agents entry carries `configured` and an extra agent's card shows the fixed badge "not a configured agent"; a fallback snapshot carries `snapshotDegraded: true`, shown as a fixed sentence and logged once at Warning with the type only; the AJ-089 stress test uses a per-writer oracle that catches the flush race mutant 3 of 3; the rune filter scans without allocating and the serializer skips the token rewrite for a payload that is plain printable ASCII without a backslash and unchanged by the redactor, so the three AJ-089 race tests run in about 2 s each. Fix round 3, tests only: the degraded-snapshot log is pinned as running outside the sink lock (a connect from another thread returns while the logger records event 302) and the bound of 64 kept running starts of capped agents is pinned (the newest refused); five dashboard classes 152 passed.
User story: As a viewer of the live dashboard, I want every event serialized with the same label objects as the results file and an embedded page that renders a missing value as its label, so that the live view obeys the honesty rules.
Acceptance criteria:
1. `DashboardEventSerializer.Serialize` of a fixture reading gives one JSON envelope `{"type","atUtc","replay","data"}` whose text contains the agent name, `netJoules` with its label, and no key material (test `Step_completed_payload`, run with a runtime-built key present in the configuration).
2. A hosted step serializes `netJoules` as `{"value":null,"label":"N/A","reason":"hosted-tier"}` and unreported tokens as `not-reported`; no payload writes a missing value as 0.
3. Flags such as `shared-window` are present in the payload; the event type names are `run-started`, `baseline`, `progress`, `step-started`, `step-progress`, `step-completed`, `power`, `run-completed` and `snapshot`.
4. The embedded page `wwwroot/index.html` contains no `<script src`, no `<link href`, and no `http://` or `https://` URL other than the repository README link after placeholder replacement (test `Page_has_no_external_resource`).
5. The page contains `EventSource("/events")`, a formatter that prints the label text when `value` is null, the footer `Energy is GPU-board energy only.`, per-agent cards, a header with tier, models, mode, progress and flags, and one canvas power chart drawn with the 2D API with a dashed idle-baseline line; the page contains no `createElementNS`, no SVG namespace string and no `innerHTML`, and sets server-provided strings with `textContent` only (test `Page_has_no_innerHTML_or_createElementNS`, which asserts both).
6. A Specs scenario asserts that a power metric without a value is delivered as `N/A` and that the served page contains the formatter that renders it.
7. The page is served only by the CLI and never written to `docs/report/`; `docs/report/index.html` still has no `<script`.
8. `step-completed` and `power` payloads carry server-side cumulative values per agent (`cumulativeNetJoules`, `cumulativeInputTokens`, `cumulativeOutputTokens`) as label objects, so the page never sums events itself; a test drops the earlier events and the cumulative stays correct (test `Cumulative_survives_dropped_events` in `DashboardEventSerializationTests`).
9. The embedded page source obeys the render model (test `Page_source_obeys_render_rules` in `DashboardPageTests`): exactly one `EventSource`, exactly one `requestAnimationFrame` loop, no `innerHTML`, no `setInterval`, no `setTimeout`, no `fetch(`, `createElement` only inside the function that builds the agent cards, no `transition` declaration on layout properties in the stylesheet, value cells styled with `font-variant-numeric: tabular-nums` and a fixed width, and event handlers that only mutate state and set a dirty flag.
10. The repaint loop returns without painting while `document.hidden` is true, and a reconnecting page keeps its last state until the `snapshot` item arrives (tests `Hidden_tab_stops_repainting` and `Reconnect_keeps_state_until_snapshot`, both source-level assertions: the loop starts with a `document.hidden` check; the error handler contains no state reset and the reset function is called only from the snapshot handler).
11. During a step the running energy estimate is integrated server-side in `DashboardSnapshot` from the `PowerEvent`s of the active step (trapezoid over the sample timestamps) and carried as a label object with label `estimated`, together with the running output-chunk count also labelled `estimated`; both are replaced by the measured `StepReading` values on `step-completed` (tests `Running_energy_is_trapezoid_estimate` and `Estimated_replaced_by_measured_on_step_completed` in `LiveEstimateTests`). The estimate is net of the idle baseline mean and the first sample after `step-started` is held to the step start; fixture (not a measurement): 60 W at 0.0, 0.1 and 0.2 s with a 20 W baseline mean gives the trapezoid of 40 W over 0.2 s, 8 J `estimated`. A hosted-tier step shows running energy `N/A` with reason `hosted-tier`, and two overlapping active steps both carry `shared-window` (tests `Hosted_running_energy_is_NA` and `Overlapping_steps_flag_shared_window`).
12. Estimates never enter results files: a run with the dashboard enabled writes a results file with no running-estimate field and no `estimated` label on any energy metric (test `Estimates_never_enter_results_file`), and the page states that NVML power on the laptop GPU is a one-second driver average (test `Page_states_one_second_average`: the page text contains "one-second driver average").
13. The page has a deliberate design (test `Page_defines_design_tokens_and_regions` in `DashboardStyleTests`; also asserts no `<img`): exactly one `<style>` block defining at least six CSS custom properties including `--bg`, `--panel`, `--fg`, `--muted`, `--accent`, `--measured`, `--estimated`, `--na`, `--grid`, `--space-1` to `--space-3` and `--font-size-base`; a dark default theme and a light theme through a `prefers-color-scheme: light` media query; a system font stack with `font-variant-numeric: tabular-nums`; the region ids `header`, `agents`, `chart` and `footer`; and no `<link`, no `@import` and no `url(http`.
14. The regions have the specified content: the header shows title, tier, model IDs with precision, mode and task and repeat progress; `agents` is a CSS grid of per-agent cards with watts, cumulative net joules, tokens, latency, status and label badges coloured by label (`--measured`, `--estimated`, `--na`), each card using the same agent colour as its chart line; `chart` has axis labels and a legend that includes the baseline; `footer` has `Energy is GPU-board energy only.` and the repository link (without `Report:RepositoryUrl`: `Repository link not configured (Report:RepositoryUrl).`) (test `Regions_have_required_content`, a page-source assertion for each element name).
15. Text contrast meets WCAG AA and the layout fits 1280 px and 1920 px windows without horizontal scrolling: test `Palette_meets_wcag_aa` computes the contrast ratio of every text-colour custom property (`--fg`, `--muted`, `--na`, `--accent`, `--measured`, `--estimated`, `--agent-1` to `--agent-4`) on `--panel` in both themes and asserts each is at least 4.5; test `Layout_has_no_fixed_wide_width` asserts the stylesheet has no `width:` declaration above 1248 px (`max-width` is exempt) and no `overflow-x: scroll`, and that the grid uses `minmax`. The visual check at 1280 and 1920 px belongs to the demo rehearsal, and the screenshot `docs/report/dashboard.png` is a user action in M6, not part of this story.
BDD scenarios:
```gherkin
@unit @AJ-S090 @REQ-032
Feature: AJ-090 Serialize live events and embed the dashboard page
  @positive
  Scenario: AJ-090 step payload carries the agent name and the labelled net joules
    Given a fixture local step for agent "Writer" with net joules measured
    When the event is serialized
    Then the payload contains "Writer" and a netJoules object with label "measured"
  @positive
  Scenario: AJ-090 page contains the stream client, the formatter and the sentence
    Given the embedded dashboard page
    When it is loaded
    Then it contains EventSource "/events", the null-to-label formatter and "GPU-board energy only"
  @negative
  Scenario: AJ-090 hosted step power renders as N/A not zero
    Given a hosted step whose power metric has no value
    When the event is serialized
    Then the power metric has label "N/A", reason "hosted-tier" and a null value
    And the page formatter prints the label text for a null value
  @negative
  Scenario: AJ-090 page has no external resource
    Given the embedded dashboard page after placeholder replacement
    When the page text is scanned
    Then there is no script src, no link href and no http or https URL other than the repository link
  @negative
  Scenario: AJ-090 payload contains no key material
    Given a runtime-built key starting with "nvapi-" present in the configuration
    When every event type is serialized
    Then no payload contains any character of the key after the nvapi- prefix
  @positive
  Scenario: AJ-090 payload carries cumulative values as label objects
    Given two completed steps of agent "Writer" with net joules 10 and 12 (fixture values)
    When the second step event is serialized
    Then cumulativeNetJoules is 22 with label "measured"
  @negative
  Scenario: AJ-090 cumulative stays correct after earlier events are dropped
    Given 1500 events emitted with no reader so the earliest are dropped
    When the last step event is serialized
    Then its cumulative values equal the sum over all emitted steps
  @negative
  Scenario: AJ-090 page builds no markup from event strings
    Given the embedded dashboard page
    When the page text is scanned
    Then it contains no innerHTML and no createElementNS
  @positive
  Scenario: AJ-090 page source obeys the render model
    Given the embedded dashboard page source
    When the render rules are checked
    Then there is exactly one EventSource and one requestAnimationFrame loop
    And createElement appears only inside the agent card builder
  @positive
  Scenario: AJ-090 running energy is a baseline-net trapezoid estimate labelled estimated
    Given power events of 60 W at 0.0 s, 0.1 s and 0.2 s for the active step and a 20 W baseline mean
    When the snapshot is serialized
    Then the running energy is 8 with label "estimated"
  @positive
  Scenario: AJ-090 page states the one-second driver average
    Given the embedded dashboard page
    When the page text is scanned
    Then it contains "one-second driver average"
  @negative
  Scenario: AJ-090 a page source with innerHTML fails the render rules
    Given a page source that assigns innerHTML
    When the render rules are checked
    Then the check fails and names innerHTML
  @negative
  Scenario: AJ-090 a page source with a timer or fetch fails the render rules
    Given a page source that calls setInterval and fetch
    When the render rules are checked
    Then the check fails and names setInterval and fetch
  @negative
  Scenario: AJ-090 a hidden tab stops repainting
    Given the repaint loop of the embedded page
    When the document is hidden
    Then the loop returns without painting
  @negative
  Scenario: AJ-090 estimate is replaced by the measured value on step completion
    Given a running step with an estimated energy of 118 and an estimated chunk count
    When the step-completed event with measured net joules arrives
    Then the displayed value carries label "measured" and no estimated value remains for that step
  @negative
  Scenario: AJ-090 estimates are absent from the results file
    Given a run executed with the dashboard enabled
    When the results file is read
    Then it contains no running-estimate field and no estimated energy label
  @negative
  Scenario: AJ-090 reconnect keeps the last state until the snapshot
    Given the page source error handler
    When the stream reconnects
    Then no state is cleared before the snapshot item arrives
  @positive
  Scenario: AJ-090 page defines design tokens, regions and the light theme
    Given the embedded dashboard page source
    When the design rules are checked
    Then it has at least 6 custom properties, a prefers-color-scheme media query and the regions header, agents, chart and footer
    And it has no link element, no import and no external url
  @positive
  Scenario: AJ-090 palette meets the WCAG AA contrast ratio in both themes
    Given the palette values of the dark and the light theme
    When the contrast ratios of every text-colour token on the panel are computed
    Then every ratio is at least 4.5
  @negative
  Scenario: AJ-090 a page without the light theme media query fails the design check
    Given a page source without a prefers-color-scheme media query
    When the design rules are checked
    Then the check fails and names the media query
  @negative
  Scenario: AJ-090 a page with an import fails the design check
    Given a page source that contains an @import rule
    When the design rules are checked
    Then the check fails and names the import
  @negative
  Scenario: AJ-090 a low-contrast palette fails the contrast check
    Given a palette whose muted text on the panel has a ratio of 3.0
    When the contrast ratios are computed
    Then the check fails and names the muted-on-panel pair
  @positive
  Scenario: AJ-090 two overlapping active steps both carry shared-window
    Given two steps active at the same time in concurrent mode
    When the snapshot is serialized
    Then both running estimates carry the flag "shared-window"
  @positive
  Scenario: AJ-090 the first power sample after step start is held to the step start
    Given a step started at 1.00 s and a first power sample at 1.05 s of 60 W with a 20 W baseline mean
    When the next sample at 1.15 s of 60 W arrives
    Then the running energy covers 0.15 s of net 40 W, which is 6 with label "estimated"
  @negative
  Scenario: AJ-090 hosted step running energy is N/A not zero
    Given a step of tier "nvidia" with no power samples
    When the snapshot is serialized
    Then the running energy has label "N/A", reason "hosted-tier" and a null value
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-090-T1 | Write the failing BDD feature and unit tests for serialization and the page | tests/AgentJoules.Specs/Features/Dashboard/LivePage.feature, tests/AgentJoules.UnitTests/DashboardEventSerializationTests.cs, tests/AgentJoules.UnitTests/DashboardPageTests.cs | 0.75 | AJ-089-T3 |
| AJ-090-T2 | Create the source-generated `DashboardJsonContext` with the results naming and enum options | src/AgentJoules.Dashboard/DashboardJsonContext.cs | 0.75 | AJ-090-T1 |
| AJ-090-T3 | Complete `DashboardEventSerializer` (envelope from AJ-089) with the data of the nine event types with per-agent cumulative label objects | src/AgentJoules.Dashboard/DashboardEventSerializer.cs | 1.5 | AJ-090-T2 |
| AJ-090-T3a | Compute the trapezoid running energy and chunk count in `DashboardSnapshot` and serialize them as `estimated` labels | src/AgentJoules.Dashboard/DashboardSnapshot.cs, src/AgentJoules.Dashboard/DashboardEventSerializer.cs | 0.5 | AJ-090-T3, AJ-089-T6b |
| AJ-090-T4 | Write the page header, agent cards, formatter and `EventSource` client | src/AgentJoules.Dashboard/wwwroot/index.html | 1.75 | AJ-090-T3 |
| AJ-090-T4a | Apply the render model: dirty flag, single `requestAnimationFrame` loop, `document.hidden` pause, tabular fixed-width cells, cards created once, reconnect keeps state | src/AgentJoules.Dashboard/wwwroot/index.html | 0.75 | AJ-090-T4, AJ-090-T3a |
| AJ-090-T4b | Write the stylesheet (custom properties, dark and light themes, type scale, grid) and the region markup with badges, legend and footer | src/AgentJoules.Dashboard/wwwroot/index.html | 1.0 | AJ-090-T4a |
| AJ-090-T5 | Add the canvas 2D power chart with the baseline line and shared-window shading | src/AgentJoules.Dashboard/wwwroot/index.html | 1.5 | AJ-090-T4 |
| AJ-090-T6 | Implement `DashboardPage` with the embedded resource and encoded repository placeholder | src/AgentJoules.Dashboard/DashboardPage.cs | 0.5 | AJ-090-T4 |
| AJ-090-T7 | Record the page rules and the separation from the report page in the build log | docs/build-log.md | 0.25 | AJ-090-T6 |
Test mapping: `DashboardEventSerializationTests` and `DashboardPageTests` (design classes, trait `Spec=REQ-032`, including the render-rule and one-second-average tests), `LiveEstimateTests` (criteria 11 and 12) and `DashboardStyleTests` (criteria 13 to 15); feature file `tests/AgentJoules.Specs/Features/Dashboard/AJ-090-live-page.feature`; criterion 7's second half (the report page keeps no script) is pinned by AJ-085's `Page_has_no_script_no_link_and_no_resource_but_the_sibling_svg`.
Docs impact: `docs/build-log.md`; the README usage is written in AJ-091; `docs/methodology.md` gains the sentence "Estimates shown on the live page never enter results files; only the measured step values are recorded." (handed over to AJ-112 in M4, which writes the methodology).
Definition of done:
- [x] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [x] docs listed above updated
- [x] `dotnet test --filter "Spec=REQ-032"` shows at least one passed and no failed test for the serialization and page tests
Demo relevance: yes: the live page is shown beside the terminal in the video.
Conflict: C-15 (design 9.2) - the static report page may not contain `<script>` but the live page needs inline JavaScript; resolution: they are different pages; the dashboard page is served only by the CLI and never written to `docs/report/`.

### AJ-091: Serve the dashboard on loopback with SSE and a port fallback
Covers: REQ-032, EDGE-018, REQ-017
Note (2026-10-23, AJ-091): feature file `tests/AgentJoules.Specs/Features/Dashboard/AJ-091-dashboard-server.feature` (the story block verbatim, `cmp` exit 0; the names `DashboardServer.feature` and `DashboardLatency.feature` in the table and the test mapping map to it, its latency scenarios included). Pinned readings P-47, P-50, P-56, P-62, P-70 to P-80 and T-1, T-3 and T-5 applied as the brief records them, with the AJ-089 and AJ-090 gate notes: the registered `SecretRedactor`, the serve-time page check, `MaxClients` 16 with 503, the 403 Origin rule with no CORS header, the hash-pinned Content-Security-Policy computed from the served page (the `'unsafe-inline'` policy of criterion 8 and design 686 superseded), nosniff and bounded disposal. Measured: red, `DashboardSinkTests` failed 25 and passed 76 of 101, `Category=AJ-S091` failed 8 and passed 2 of 10 (exit codes not recorded); green (exit 0), `DashboardSinkTests` 101 of 101, `Category=AJ-S091` 10 of 10, `Spec=EDGE-018` 5 of 5, `Spec=REQ-032` 194 of 194, `Spec=REQ-017` 281 of 281, the nine options in `run --help` (`--dashboard-port` with `[default: 5178]`), a latency maximum of 2.5 ms (3.0 ms on the first green run) against 250 ms; battery 1 4781 passed, 0 failed, 2 skipped, build 0 warnings. Deviations and rulings: `DashboardSink` gained one internal `Redactor` property for the page check; the server API beyond P-47 adds `FailureReason`, `MaxClients` and the third failure reason `start failed (<Type>)`, the type name only; step bindings are scoped per feature; the system-clock latency tests are open to the gates' flakiness check; `ShortIntervalNote` is printed only when a measured tier is selected, and an access-denied bind is treated like address-in-use (both fix-round items with tests); the grace test is AJ-093-T4's. Rulings: `docs/methodology.md` does not exist in this repository, so the methodology sentence lives in the README section "Methodology and honesty rules" (T8 and the Docs impact amended); the URL-literal check is the battery's REQ-012 grep (`http://` and `https://` literals, the SVG namespace and json-schema.org allowed), which passes, and the brief's looser pattern is not a rule. Fix round (builder-reported, 2026-10-23): the server builder is constructed inside the try, so a construction failure, such as a malformed host-builder environment variable, returns null with `start failed (<Type>)` instead of aborting the run, and the builder stays `CreateSlimBuilder`. The hosting URL preference is pinned off after the configuration sources are cleared and the bound address is checked after the start, so no environment variable moves the listener off 127.0.0.1; `Sec-Fetch-Site` other than `same-origin` or `none` is refused; an access-denied bind, bare or wrapped, falls back like an address in use. The short-interval note prints only for a measured tier, the did-not-start record is Debug so the command's warning is the only stderr line, and a failed stream is logged once at Warning with the type only. Run-completed is emitted after every attempted final write, including a failed completed write and the shutdown paths; the help text says a dashboard failure never stops the run; on the shutdown path the sink is disposed without waiting, and `DashboardSink.DisposeAsync` is itself bounded by `DisposeTimeout` (2 s) on the injected clock (an AJ-089 contract amendment). Measured (builder, exit 0 each): `DashboardSinkTests` 118 passed (101 at first green, 117 after the fix round, 118 after a tests-only round 2), `Spec=REQ-032` 211, `Spec=EDGE-018` 6, `Spec=REQ-017` 281, Specs `AJ-S091|AJ-S090|AJ-S089` 47, build 0 warnings, format clean, fifteen named mutants killed. Fix round 2, tests only: the dispose-versus-start race runs for real over sixty rounds, the bounded-dispose test caps its real-time wait at 500 ms after the fake advance, the stream-fault once-gate is pinned with two failed streams, and a shutdown inside the completed write is pinned to one run-completed; four mutants killed, no production line changed.
Note (2026-10-21, M2 exit): REQ-017 added because criterion 4 completes the nine-option `run --help` of REQ-017 (`--dashboard` and `--dashboard-port`; M2 full spec pass, remark 3), so the trait is not lost.
User story: As a user, I want `AJ run --dashboard` to start a loopback-only server that streams events and picks the next free port when mine is taken, so that the live view starts without configuration and never blocks the run.
Acceptance criteria:
1. `AJ run --tier local --dashboard --dry-run` prints a line starting `Dashboard:` containing `http://127.0.0.1:` and exits 0 without a browser (Specs scenario with the fake GPU factory).
2. The server binds `IPAddress.Loopback` only; `GET /` returns the page (`text/html; charset=utf-8`); `GET /events` returns `text/event-stream`, first a `snapshot` item, then each payload as a line `data: {json}` that is raw JSON, not a quoted string (wire-format scenario; fallback to a hand-written `text/event-stream` writer is recorded in the build log if the quoting inference fails).
3. Port fallback: with a listener holding the preferred port, the dashboard reports a different port within the next 19 and the run proceeds to completion (test `Port_in_use_falls_back` and `A_run_whose_preferred_port_is_taken_serves_the_next_port_and_completes`); with all 20 ports busy the run prints a warning and continues without a dashboard (exit 0).
4. `AJ run --help` lists nine options: `--tier`, `--task`, `--mode`, `--out`, `--baseline-seconds`, `--repeat`, `--dry-run`, `--dashboard` and `--dashboard-port`; a port outside 1 to 65535 exits 2.
5. The dashboard is off by default: without `--dashboard` no listener is opened and no `Dashboard:` line is printed.
6. Startup is bounded by 5 seconds and happens before warm-up and baseline; the server stays up for 2 seconds after the last `RunCompletedEvent` of the command (one per tier run) so the browser renders the final state.
7. The printed URI is built with `UriBuilder`, so the URL-literal grep over `src` stays clean.
8. Every response the application writes carries the hash-pinned policy of `DashboardServer.ContentSecurityPolicy(page)` (`default-src 'none'; script-src 'sha256-...'; style-src 'sha256-...'; connect-src 'self'; img-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'`) and `X-Content-Type-Options: nosniff`; a request with a `Host` header other than `127.0.0.1:<port>` or `localhost:<port>` gets status 421 (tests `Security_headers_present` and `Foreign_host_gets_421`).
9. Every live event reaches a connected page within 250 ms of its emit on loopback: a Specs scenario with a headless SSE client compares each event's `atUtc` with its receipt time and asserts a maximum of 250 ms over a fixture run (test `Events_arrive_within_250ms`; the scenario uses `TimeProvider.System`, emits at least 50 events including `power` events at the default 100 ms interval, and is one of the two latency scenarios, the only dashboard scenarios on the system clock; the headless client lives in `tests/AgentJoules.TestSupport/HeadlessSseClient.cs`).
BDD scenarios:
```gherkin
@unit @AJ-S091 @REQ-032 @EDGE-018
Feature: AJ-091 Serve the dashboard on loopback with SSE and a port fallback
  @positive
  Scenario: AJ-091 dry-run prints the dashboard URL and exits 0
    Given the run command with fake keys, a scripted GPU and the dashboard enabled
    When "run --tier local --dashboard --dry-run" is executed
    Then the output has a line starting "Dashboard:" containing "http://127.0.0.1:"
    And the exit code is 0
  @positive
  Scenario: AJ-091 events are sent as raw data lines
    Given a started dashboard server with one emitted step event
    When a client reads "/events"
    Then the response content type is "text/event-stream"
    And the first item is a snapshot and a later line starts with "data: {"
  @positive
  Scenario: AJ-091 occupied preferred port falls back to the next free port
    Given a listener holds the preferred port
    When the dashboard starts during a run
    Then it reports a different port and the run completes with exit code 0
  @negative
  Scenario: AJ-091 all twenty ports busy lets the run continue without a dashboard
    Given listeners hold 20 consecutive ports starting at the preferred port
    When "run --tier nvidia --dashboard --repeat 1" is executed
    Then a warning is printed and no "Dashboard:" line appears
    And the exit code is 0 and the results file is complete
  @negative
  Scenario: AJ-091 no dashboard flag means no listener
    Given the run command without --dashboard
    When "run --tier nvidia --repeat 1" is executed
    Then no "Dashboard:" line appears and no port was opened
  @negative
  Scenario: AJ-091 invalid dashboard port exits 2
    Given the run command
    When "run --tier nvidia --dashboard --dashboard-port 70000 --dry-run" is executed
    Then the exit code is 2
  @positive
  Scenario: AJ-091 every response carries the CSP and nosniff headers
    Given a started dashboard server
    When a client requests "/" and "/events"
    Then both responses carry the Content-Security-Policy and X-Content-Type-Options "nosniff" headers
  @negative
  Scenario: AJ-091 a foreign Host header gets 421
    Given a started dashboard server
    When a client sends a request with Host "evil.example"
    Then the status is 421 and no event data is returned
  @positive
  Scenario: AJ-091 every live event arrives within 250 ms on loopback
    Given a started dashboard server, a headless SSE client and the system clock
    When a fixture run emits at least 50 events including power events at the default interval
    Then each event was received within 250 ms of its atUtc
  @negative
  Scenario: AJ-091 the latency check detects a delayed delivery
    Given a dashboard server variant that delays delivery by 400 ms
    When a fixture run emits its events
    Then the latency check fails and reports a maximum above 250 ms
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-091-T1 | Write the failing BDD feature and unit tests for the server and port fallback | tests/AgentJoules.Specs/Features/Dashboard/AJ-091-dashboard-server.feature, tests/AgentJoules.UnitTests/DashboardSinkTests.cs | 0.75 | AJ-090-T1 |
| AJ-091-T2 | Implement `DashboardServer.TryStartAsync` on a loopback Kestrel endpoint with `GET /` | src/AgentJoules.Dashboard/DashboardServer.cs | 1.5 | AJ-091-T1, AJ-090-T6 |
| AJ-091-T3 | Implement the `/events` endpoint with the snapshot item and per-connection channels | src/AgentJoules.Dashboard/DashboardServer.cs | 1.5 | AJ-091-T2, AJ-089-T6 |
| AJ-091-T4 | Implement the 20-port fallback, the 5 second start bound and the printed `Dashboard:` URI | src/AgentJoules.Dashboard/DashboardServer.cs | 1.0 | AJ-091-T3 |
| AJ-091-T4a | Add the Host check (421) and the security headers middleware | src/AgentJoules.Dashboard/DashboardServer.cs | 0.75 | AJ-091-T4 |
| AJ-091-T4b | Add the headless SSE client and the 250 ms latency assertion (system clock, at least 50 events) | tests/AgentJoules.TestSupport/HeadlessSseClient.cs, the latency scenarios of tests/AgentJoules.Specs/Features/Dashboard/AJ-091-dashboard-server.feature | 0.5 | AJ-091-T4a |
| AJ-091-T5 | Add `--dashboard` and `--dashboard-port` to `run` and register the `DashboardSink` as the run's live sink and start the server (fan-out and forwarding wired in AJ-089) | src/AgentJoules.Cli/Commands/RunCommand.cs, src/AgentJoules.Workflow/Runs/RunOrchestrator.cs | 1.5 | AJ-091-T4, AJ-056-T7 |
| AJ-091-T6 | Emit run-started, baseline, progress and step-started events from their producers | src/AgentJoules.Workflow/Runs/RunOrchestrator.cs, src/AgentJoules.Metering/MeteredChatClient.cs | 1.5 | AJ-091-T5 |
| AJ-091-T8 | Write the README dashboard usage and the methodology sentence | README.md (its methodology section; no docs/methodology.md exists) | 0.5 | AJ-091-T6 |
Test mapping: Specs feature `tests/AgentJoules.Specs/Features/Dashboard/AJ-091-dashboard-server.feature` (its latency scenarios included) (traits `@REQ-032`, `@EDGE-018`); class `DashboardSinkTests` for the port test (trait `Spec=EDGE-018`).
Docs impact: the run options under the README "run" section (`--dashboard`, `--dashboard-port`, loopback only, the `Dashboard:` line); the README section "Methodology and honesty rules" in place of a `docs/methodology.md` that does not exist (the dashboard displays what the meter already samples; a browser adds compositing load to the measured GPU, so published results files are recorded without a browser open and only the demo recording shows the live page).
Definition of done:
- [x] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [x] docs listed above updated
- [x] `dotnet test --filter "Spec=EDGE-018"` shows at least one passed and no failed test
- [x] the URL-literal grep over `src` returns no hit
Demo relevance: yes: the video runs `AJ run --tier local --task energy-explainer --repeat 1 --dashboard` and shows the live page.
Conflict: C-14 (design 9.2) - the URL-literal grep forbids `"http` in C# while the dashboard must print its URL; resolution: the URI is built with `UriBuilder(Uri.UriSchemeHttp, ...)`, and the page uses relative URLs and a configured repository link.

### AJ-092: Replay a committed run file on its recorded timeline
Covers: REQ-032
Note (2026-10-24, AJ-092): the feature file is `tests/AgentJoules.Specs/Features/Dashboard/AJ-092-replay.feature`, identical to the story block (builder cmp exit 0). Pinned readings applied: the hosted `dashboard` command (P-81), the name, bounded reader, `RunFileValidator.Check` and step-agent checks in `DashboardCommand` (P-82), the baseline only when the file has one (P-83), `--exit-after` from event 0 in wall seconds (P-85), the start gated on the first client or `--start-delay` with the 120 s warning (P-86), and fake clocks in the replay tests (P-90). Measured by the builder: red 42 failed of 55 and 10 failed of 11, green 55/55, 11/11 and `Spec=REQ-032` 266/266 (after the fix round 86/86, 11/11 and 297/297), fixture span 23 s, the exit-criteria command exit 0 in 7.83 s on the system clock. Deviations: the step-agent rule with exit 2 is new against this story, the banner drops the AJ-090 "replay of a recorded run:" prefix, any start failure exits 5, and `ReplayRig` lives in TestSupport. Docs: README `### dashboard` (the story's "Live dashboard" section) and the replay sentence in the README methodology paragraph; docs/methodology.md does not exist yet, so its replay caveat carries to the story that creates the file. Fix round (2026-10-24, from the three round-1 gates): a flush after every replayed event (events were lost at very high speed), a gap check that exits 2 for a `--speed` too small for the recorded gaps and a one-tick minimum for `--exit-after` and `--start-delay`, the agent rule ordinal and case-sensitive and extended to `environment.models[].agent`, zero-length steps ordered start before end with run-completed after every step entry, an event exactly at the deadline emitted, cancellation returning 0 at every phase including the file read, pipes and devices refused by the shared readers of `report`, `validate` and `dashboard`.
User story: As a judge without a GPU, I want `AJ dashboard --replay <run file>` to serve the same page and replay the recorded steps on their timeline, so that I can see the live view from committed data.
Acceptance criteria:
1. `AJ dashboard --replay <fixture run file> --speed 50 --exit-after 5` exits 0 and a test asserts one `step-completed` SSE event per step of the fixture (test `Replay_emits_one_event_per_step`).
2. `ReplayTimeline.Build` creates events at the recorded offsets from `run.startUtc`: run started at 0, baseline at 0 when the file has one, progress at the first step of each (task, repeat), step started at `step.startUtc`, step completed at `step.endUtc`, power as one event per step start and end carrying the step's recorded `avgW`, run completed at the last `endUtc` with the file's totals and flags; every event has `replay: true`.
3. `--speed 4` divides every delay by 4 (driven by `FakeTimeProvider`); `--speed` of 0 or below exits 2.
4. A file that does not match the run-file name pattern or fails the schema exits 2 naming the file; when 20 consecutive ports are busy the command exits 5 and prints `no free dashboard port in <start>-<end>`.
5. A hosted fixture replays power as N/A reason `hosted-tier`; the page shows the banner `power shown as recorded per-step average` and a replay marker.
6. Without `--exit-after` the server runs until Ctrl+C; the replay server handles its own cancellation and returns 0 (it is not the shutdown-exit-5 path of a measured run); nothing is written to disk (test `Replay_cancellation_exits_0`: exit code 0 after the token is cancelled, and the output folder is unchanged).
7. After printing `Dashboard: <uri>` the replay emits no event until the first `/events` connection or until `--start-delay <seconds>` elapses (test `No_event_before_first_client`: the sink holds 0 emitted events before a client connects).
8. With no client and no start delay the command prints a warning and exits 0 after the shorter of 120 seconds and `--exit-after` (fake-clock test `No_client_times_out_with_warning`: exit code 0 and one warning line).
9. `--exit-after` counts from the first event (event 0), not from startup, and a client that reconnects is rebuilt from the `snapshot` item (test `Reconnect_rebuilds_from_snapshot`: the snapshot after reconnect equals the snapshot before the drop).
10. `AJ dashboard --help` lists `--replay`, `--speed`, `--exit-after`, `--start-delay` and `--dashboard-port` (checked by the help test).
BDD scenarios:
```gherkin
@unit @AJ-S092 @REQ-032
Feature: AJ-092 Replay a committed run file on its recorded timeline
  @positive
  Scenario: AJ-092 replay emits one step-completed event per step
    Given a fixture run file with 12 steps
    When "dashboard --replay <fixture> --speed 50 --exit-after 5" is executed and a client reads "/events"
    Then the client received 12 step-completed events
    And the exit code is 0
  @positive
  Scenario: AJ-092 speed divides the recorded delays
    Given a fixture whose first two steps start 8 seconds apart
    When the timeline is replayed at speed 4 on a fake clock
    Then the second step start is emitted 2 seconds after the first
  @positive
  Scenario: AJ-092 all replay events are marked
    Given a fixture run file
    When the timeline is built
    Then every event has replay true and the page shows the replay banner
  @negative
  Scenario: AJ-092 invalid run file exits 2
    Given a file whose name does not match the run-file pattern
    When "dashboard --replay <file>" is executed
    Then the exit code is 2 and the output names the file
  @negative
  Scenario: AJ-092 non-positive speed exits 2
    Given a fixture run file
    When "dashboard --replay <fixture> --speed 0" is executed
    Then the exit code is 2
  @negative
  Scenario: AJ-092 hosted replay shows power as N/A not zero
    Given a nvidia fixture run file
    When the timeline is built
    Then every power event has label "N/A" and a null value
  @negative
  Scenario: AJ-092 no free port exits 5
    Given listeners hold 20 consecutive ports starting at the preferred port
    When "dashboard --replay <fixture> --exit-after 1" is executed
    Then the exit code is 5
  @positive
  Scenario: AJ-092 replay waits for the first client
    Given a started replay of a fixture run file and no connected client
    When the clock advances 30 seconds
    Then no event was emitted
    And after a client connects the first event is emitted
  @positive
  Scenario: AJ-092 a reconnecting client is rebuilt from the snapshot
    Given a replay in progress after 6 of 12 steps
    When the client disconnects and reconnects
    Then its first item is a snapshot holding the state after 6 steps
  @negative
  Scenario: AJ-092 no client within the timeout exits 0 with a warning
    Given a started replay with no client and --exit-after 60
    When 60 seconds pass on the fake clock
    Then a warning is printed and the exit code is 0
  @positive
  Scenario: AJ-092 Ctrl+C during a replay exits 0
    Given a replay in progress with a connected client
    When the cancellation token is signalled
    Then the replay server stops by itself and the exit code is 0
    And no file was written
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-092-T1 | Write the failing BDD feature and unit tests for replay | tests/AgentJoules.Specs/Features/Dashboard/Replay.feature, tests/AgentJoules.UnitTests/ReplayTimelineTests.cs | 0.75 | AJ-091-T1 |
| AJ-092-T2 | Implement `ReplayTimeline.Build` from a `RunDocument` | src/AgentJoules.Dashboard/ReplayTimeline.cs | 1.5 | AJ-092-T1, AJ-078-T3 |
| AJ-092-T3 | Implement `ReplayService.RunAsync` with delays on `TimeProvider` and `--exit-after` (the name and schema checks run in `DashboardCommand`) | src/AgentJoules.Dashboard/ReplayService.cs | 1.5 | AJ-092-T2, AJ-091-T4, AJ-079-T4 |
| AJ-092-T3a | Add `DashboardSink.FirstClientConnected` and make the replay wait for the first `/events` connection | src/AgentJoules.Dashboard/DashboardSink.cs, src/AgentJoules.Dashboard/ReplayService.cs | 0.5 | AJ-092-T3 |
| AJ-092-T4 | Add the `dashboard` command with `--replay`, `--speed`, `--exit-after`, `--dashboard-port` | src/AgentJoules.Cli/Commands/DashboardCommand.cs | 1.0 | AJ-092-T3, AJ-080 (`RunFileValidator`) |
| AJ-092-T4a | Add the `--start-delay` option, the 120 s no-client timeout and the `--exit-after` clock from event 0 | src/AgentJoules.Cli/Commands/DashboardCommand.cs, src/AgentJoules.Dashboard/ReplayService.cs | 0.5 | AJ-092-T4, AJ-092-T3a |
| AJ-092-T5 | Show the replay banner and marker in the page | src/AgentJoules.Dashboard/wwwroot/index.html | 0.5 | AJ-092-T2, AJ-090-T4 |
| AJ-092-T6 | Write the README replay usage for judges without a GPU | README.md | 0.25 | AJ-092-T4 |
Test mapping: `ReplayTimelineTests` (design class, trait `Spec=REQ-032`); feature file `tests/AgentJoules.Specs/Features/Dashboard/Replay.feature`.
Docs impact: README section "Live dashboard" (replay command and the per-step average caveat); `docs/methodology.md` (replayed power is the recorded per-step average, not a trace).
Definition of done:
- [x] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [x] docs listed above updated (README `### dashboard` stands for the "Live dashboard" section; docs/methodology.md does not exist yet and its caveat is carried forward, see the Note)
- [x] `dotnet run --project src/AgentJoules.Cli -- dashboard --replay tests/AgentJoules.TestSupport/Fixtures/results/20260101T000000Z-nvidia.json --speed 50 --exit-after 5 --start-delay 0; echo $?` prints 0
Demo relevance: yes: the fallback for the video when the local tier is unavailable at recording time, and the way judges without a GPU see the live view.

### AJ-093: Leave the run and its results unchanged when the dashboard fails
Covers: REQ-032, EDGE-019
User story: As a user, I want any dashboard failure logged and ignored by the run, so that the live view can never alter a measurement, an exit code or a results file.
Acceptance criteria:
1. A test that makes the dashboard pump throw and the server unable to bind any port finishes the run with exit 0, and the results file equals the file of a run without `--dashboard` in step count, outcomes and status (test `Run_result_unchanged_when_dashboard_fails`).
2. The server exception is logged at Warning and never reaches the orchestrator (test `Server_fault_is_logged_not_thrown`: the logger sink holds exactly one Warning entry and the run exit code is 0).
3. With no browser connected the run completes normally and publishing never blocks (test `Run_completes_without_client`: exit code 0 and 10 000 `Emit` calls finish within the test timeout).
4. The dashboard adds no NVML call and never takes the meter gate (test `No_extra_nvml_calls`: the scripted NVML API call count of a run with the dashboard equals the count without it, and the gate acquire count equals the number of metered calls).
5. `docs/methodology.md` states the sentence "The live dashboard displays readings and power samples the meter already takes; it does not change what is measured." and that published results files are recorded without a browser open.
6. The dashboard server is disposed after the run-completed event plus the 2 second grace, with no listener left behind (test `Server_disposed_after_grace`: after 2 seconds on the fake clock a connection attempt to the reported port is refused).
BDD scenarios:
```gherkin
@unit @AJ-S093 @REQ-032 @EDGE-019
Feature: AJ-093 Leave the run and its results unchanged when the dashboard fails
  @positive
  Scenario: AJ-093 a run with an idle dashboard equals a run without one
    Given the same fake run executed once with --dashboard and no client and once without
    When the two results files are compared
    Then step count, outcomes and run status are equal
  @positive
  Scenario: AJ-093 the dashboard makes no extra NVML calls
    Given a scripted NVML API counting calls
    When the same run executes with and without --dashboard
    Then the call counts are equal
  @negative
  Scenario: AJ-093 a faulted pump leaves exit code and results unchanged
    Given a dashboard sink whose pump throws on every event
    When "run --tier nvidia --dashboard --repeat 1" is executed
    Then the exit code is 0 and the results file equals the one from a run without the dashboard
  @negative
  Scenario: AJ-093 a server that cannot bind logs a warning and the run completes
    Given listeners hold 20 consecutive ports starting at the preferred port
    When "run --tier nvidia --dashboard --repeat 1" is executed
    Then a warning is logged and the exit code is 0
  @negative
  Scenario: AJ-093 an exception thrown inside the server is logged not propagated
    Given a server endpoint that throws on the first request
    When a client requests it during a run
    Then the exception is logged at Warning and the run exit code is 0
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-093-T1 | Write the failing BDD feature and unit tests for dashboard failure isolation | tests/AgentJoules.Specs/Features/Dashboard/FailureIsolation.feature, tests/AgentJoules.UnitTests/DashboardSinkTests.cs | 0.75 | AJ-091-T1 |
| AJ-093-T2 | Create `RunResultComparer` for step count, outcomes and status | tests/AgentJoules.TestSupport/RunResultComparer.cs | 0.75 | AJ-093-T1 |
| AJ-093-T3 | Catch and log server and pump faults without propagating to the orchestrator | src/AgentJoules.Dashboard/DashboardServer.cs, src/AgentJoules.Workflow/Runs/RunOrchestrator.cs | 1.0 | AJ-093-T1, AJ-091-T5 |
| AJ-093-T4 | Dispose the server after the completed event and the 2 second grace | src/AgentJoules.Dashboard/DashboardServer.cs | 0.75 | AJ-093-T3 |
| AJ-093-T5 | Assert equal NVML call counts with and without the dashboard | tests/AgentJoules.UnitTests/DashboardSinkTests.cs | 0.5 | AJ-093-T3 |
| AJ-093-T6 | Add the methodology sentence and the no-browser recording note | docs/methodology.md | 0.25 | AJ-093-T4 |
Test mapping: `DashboardSinkTests` (design class, traits `Spec=EDGE-019` and `Spec=REQ-032`); feature file `tests/AgentJoules.Specs/Features/Dashboard/FailureIsolation.feature`.
Docs impact: `docs/methodology.md` (both dashboard facts); README "Live dashboard" caveat that a browser tab adds compositing load.
Definition of done:
- [ ] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [ ] `dotnet test --filter "Spec=EDGE-019"` shows at least one passed and no failed test
Demo relevance: no (the property it protects makes the live view safe to show).

## Risks

| Risk | Trigger | Mitigation | Owner |
|---|---|---|---|
| JsonSchema.Net 8.0.5 or the schema exporter API differs from the design notes (`TransformSchemaNode`, `JsonUnmappedMemberHandling`) | AJ-079-T2 or AJ-078-T4 does not compile | names were read from the package XML docs; where a name is inferred, check it first in AJ-078-T4 and record the deviation in `docs/build-log.md`; do not move to the 9.x line (not OSI licensed) | builder |
| Nebius or nvidia price cannot be found | AJ-070-T4 finds no published price | no row is written; cost is N/A with its reason and a build-log entry (honest default of the prompt) | builder |
| Judge rate limit on build.nvidia.com (about 40 requests per minute, 36 judge calls per tier) | live judge steps exhaust retries | judge failure is non-fatal (AJ-077); retry client already applies | builder |
| Unconfirmed rules in AJ-072, AJ-074 (below-baseline cost and ratio N/A), AJ-076 (non-ok step quality) and AJ-082 (mixed-label median) | architect disagrees | each is labelled an assumption in its story; confirm before the Definition of Done | product owner |
| `TypedResults.ServerSentEvents` JSON-quotes string data (design R-12) | the AJ-091 wire-format scenario fails | hand-written `text/event-stream` writer (ADR-22), recorded in the build log | builder |
| Dashboard work (design R-11 estimates 6 to 8 hours) crowds out the report stories | the estimate gap below | build order: AJ-089 to AJ-092 after the report stories; replay (AJ-092) ships before page polish because the video and judges depend on it | product owner |
| A browser tab adds compositing load to the measured GPU (design R-13) | a published run is recorded with a browser open | published runs are made without a browser; stated in `docs/methodology.md` (AJ-093) and in `docs/build-log.md` | builder |
| Fixture run files drift from the model | schema or model change leaves fixtures invalid | the fixtures are validated by `AJ validate` in the unit suite (AJ-080) | builder |
| Estimate exceeds the nominal three-day capacity | the sum in Estimate is larger than 18 hours | see Estimate | product owner |

## Estimate

Note (2026-10-18): any deferral or descope candidates named in this section are informational; the drop order in docs/plan/plan.md section 3.4 is the single source and classifies stories as MUST, SHOULD or COULD.

Sum of task estimates: 105.0 hours across 24 stories and 142 tasks (per story: AJ-070 3.5, AJ-071 2.75, AJ-072 3.5, AJ-073 2.0, AJ-074 2.0, AJ-075 2.5, AJ-076 5.0, AJ-077 3.0, AJ-078 5.5, AJ-079 5.75, AJ-080 2.75, AJ-081 2.5, AJ-082 3.75, AJ-083 5.25, AJ-084 4.5, AJ-085 3.5, AJ-086 3.75, AJ-087 3.5, AJ-088 3.0, AJ-089 7.75, AJ-090 9.25, AJ-091 9.5, AJ-092 6.5, AJ-093 4.0). The dashboard stories AJ-089 to AJ-093 account for 37.0 hours; the rest is 68.0 hours. The design's own dashboard estimate is 6 to 8 hours (R-11, inferred); this plan's task-level sum is about four times that because it includes red-first tests, Specs features and documentation for each story, so the figure should be re-checked with the architect.

Nominal capacity: 3 working days x 6 hours = 18 hours (2026-10-23 to 2026-10-25), shared with the last day of M2. The sum is about 5.8 times that capacity (105.0 / 18). This is an unresolved planning gap that needs a decision by the coordinator or the user; cutting inside M3 cannot close it. Deferral candidates in order: AJ-089 to AJ-093 dashboard (37.0 h, REQ-032 is a user-added scope item; replay AJ-092 is the part the video and judges need), AJ-087 README block function (3.5 h, only needed once M6 wires it), AJ-081 baseline-row handling. REQ-009, REQ-010, REQ-015, REQ-016, REQ-023, REQ-030 and EDGE-012 must still be met before the Definition of Done.
