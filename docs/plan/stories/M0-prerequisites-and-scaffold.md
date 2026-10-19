# M0: Prerequisites and scaffold

M0 turns the existing repository (README, LICENSE, .gitignore, .gitattributes, .editorconfig and the Devpost drafts, all created on 2026-10-18) into a warning-free, empty-but-compiling .NET 10 solution. It first checks the machine (SDK, Docker Desktop, WSL2, Ollama, GPU driver), then adds the folder layout, the shared build files, the 22 central package pins, seven source projects (the seventh, Dashboard, is the only one with an ASP.NET Core framework reference) and four test projects exactly as docs/design/technical-design.md lays them out, extends the hygiene files, builds the secret-handling baseline (redaction helper, secret scan) and records which user actions from prompt section 12 gate M2 and M5. No product behaviour exists yet, so most M0 stories are verified by shell commands that the builder runs and logs in docs/build-log.md; only the redaction helper (AJ-010) is test-first code.

Convention for M0 scenarios: the Gherkin below is the verification contract. Where a story has no executable code, each scenario is run as the shell commands named in its acceptance criteria and the result (command, exit code, UTC date, label measured or user-reported) is appended to docs/build-log.md. No Reqnroll feature file is created for those stories. AJ-010 is the exception and gets a real feature file and xUnit class.

## Dates and exit criteria

Dates: 2026-10-18 to 2026-10-19.

Exit criteria:
- [x] `dotnet --version` prints 10.0.401 (or a higher 10.0.4xx patch) and the toolchain facts are recorded under `## Toolchain check` in docs/build-log.md
- [x] `dotnet build -warnaserror -tl:off --no-incremental` exits 0 and its output contains `0 Warning(s)` and `0 Error(s)`
- [x] `dotnet test` exits 0 with 0 failed; `dotnet test --list-tests` output contains MeteredChatClientTests, AttributionBaselineTests, CostModelTests, JudgeParsingTests, ReportRenderingTests and SchemaValidationTests
- [x] `dotnet format --verify-no-changes` exits 0
- [x] `grep -rnE --include=*.csproj "<PackageReference[^>]*Version" .` returns no hits and `Directory.Packages.props` holds the 22 pinned rows of design section 2.4
- [x] `git check-ignore results/x.raw` prints `results/x.raw`; the same check prints the path for `a.pem`, `id_ed25519`, `id_rsa` and `results/x.partial`
- [x] `dotnet test --filter "Spec=REQ-029" --logger "console;verbosity=normal"` shows at least 1 passed and 0 failed
- [x] The secret scan name and exit code are recorded under `## Secret scan` in docs/build-log.md
- [x] `## User actions` in docs/build-log.md lists prompt section 12 steps 1 to 13 with owner, status and the milestone each one gates
- [x] Commit messages match `^AJ-[0-9]{3}( (REQ|EDGE)-[0-9]{3})*:` (story id first, then the ids delivered, standing order 5): `AJ-008 REQ-001:`, `AJ-009 REQ-002:` and `AJ-012 REQ-029:` exist (made by the builder, one per completed requirement, nothing pushed); check `git log <build-start>..HEAD --format=%s | grep -vcE "^AJ-[0-9]{3}( (REQ|EDGE)-[0-9]{3})*:"` prints 0

## Coverage

REQ-001, REQ-002, REQ-029.

## Dependencies and user actions

Dependencies: none. M0 starts from the repository as created by the user on 2026-10-18.

User actions from prompt section 12 (status is recorded by AJ-012; the builder never creates accounts, keys or cloud resources):
- Steps 5, 6 and 7: the three keys are already set on the laptop as user environment variables NEBIUS_API_KEY, NVIDIA_API_KEY and NGC_API_KEY (user-reported 2026-10-18). Steps 5 and 6 gate M2 (`AJ run --tier nvidia|nebius|all` and the judge); step 7 gates the M4 NIM attempt.
- Steps 1 to 4 (Devpost account, join, read rules, request the credits): status unknown to the builder; step 4 must be done before the 45-minute Nebius onboarding box in M2.
- Step 10 (Nebius AI Cloud account at console.nebius.com, USD 25 minimum first payment) is still open and gates M5; step 11 (ed25519 SSH key pair kept outside the repository) gates M5.
- Steps 12 and 13 (create and delete the VM) happen only when the builder asks, in M5. Steps 8 and 9 (submission) belong to M7.

## Stories

### AJ-001: Create the build log with fixed sections
Covers: REQ-002
User story: As the build session, I want one docs/build-log.md with fixed section headings, so that every version deviation, scan result, test count and blocked requirement that the requirements demand has a known home.
Acceptance criteria:
1. `test -f docs/build-log.md` exits 0 and the file contains each of these exact heading lines, once each: `## Toolchain check`, `## Version deviations and additions`, `## Secret scan`, `## Test counts`, `## Per-ID filter results`, `## Probe and WSL2 notes`, `## User actions`, `## Requirements that cannot be met`, `## Base commits` (checked with `grep -cxF "<heading>" docs/build-log.md` printing 1 for each).
2. `grep -nP "[^\x00-\x7F]" docs/build-log.md` exits 1 (ASCII only) and `grep -c $'\r' docs/build-log.md` prints 0 (LF only).
3. `grep -nE "nvapi-[A-Za-z0-9_-]{8,}" docs/build-log.md` exits 1 (no key material).
BDD scenarios:
```gherkin
@unit @AJ-S001 @REQ-002
Feature: Create the build log with fixed sections
  @positive
  Scenario: AJ-001 All nine headings exist exactly once
    Given docs/build-log.md was created from the heading list
    When each heading is counted with grep -cxF
    Then every count is 1 and the exit code of the check is 0
  @positive
  Scenario: AJ-001 A later story appends a row under an existing heading
    Given the heading "## Secret scan" exists
    When AJ-011 appends its result row below that heading
    Then the heading count is still 1 and no new heading was created
  @negative
  Scenario: AJ-001 A missing heading is reported by name
    Given the heading "## Test counts" was deleted
    When the heading check runs
    Then it exits non-zero and prints "missing heading: ## Test counts"
  @negative
  Scenario: AJ-001 A non-ASCII dash is rejected
    Given the file contains a character outside 0x00-0x7F
    When grep -nP "[^\x00-\x7F]" runs
    Then it exits 0 with the offending line number and the story fails until the character is replaced
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-001-T1 | Create the build log with the nine headings and a one-line purpose under each | docs/build-log.md | 0.25 | - |
| AJ-001-T2 | Run the heading, ASCII, LF and key-pattern checks and note the commands under `## Toolchain check` | docs/build-log.md | 0.25 | AJ-001-T1 |
Test mapping: no xUnit class (no behaviour); shell checks listed in the criteria, results logged in docs/build-log.md.
Docs impact: docs/build-log.md (created here); later stories append rows.
Definition of done:
- [x] scenarios green (shell checks), no projects exist yet so no build or unit test is run
- [x] docs listed above updated
- [x] the file is committed with the REQ-002 commit, never with a key or an em-dash
Demo relevance: no

### AJ-002: Verify toolchain and machine prerequisites
Covers: REQ-001
User story: As the build session, I want to check the SDK, Docker Desktop, WSL2, Ollama and the GPU driver before I write code, so that a missing tool is found in minutes and recorded instead of surfacing in the middle of M1 or M4.
Acceptance criteria:
1. `dotnet --version` prints `10.0.401`, or a higher 10.0.4xx patch (accepted later by `rollForward: latestPatch`); `dotnet --list-sdks` lists it. Any lower or missing SDK is a stop: written under `## Requirements that cannot be met`, and `global.json` is not lowered.
2. `docker --version` exits 0 (user-reported 29.6.1, to be confirmed) and `docker info --format '{{.ServerVersion}}'` exits 0. A non-zero exit records "Docker daemon not running" and marks the EDGE-001 and EDGE-014 container check in M1 as blocked, not skipped.
3. `wsl -l -v | tr -d '\0'` (the output is UTF-16, so NUL bytes are stripped) shows at least one distro with VERSION 2; the distro name is recorded.
4. `ollama --version` exits 0 and `ollama list` is recorded (tags only; the Qwen3 8B tag is noted; nothing is pulled in M0).
5. `nvidia-smi --query-gpu=name,driver_version,power.limit --format=csv,noheader` exits 0, the name contains `3080`, and the driver version is recorded as measured with the UTC date. The prompt value 616.92 is compared and a difference is recorded, never silently corrected. `test -f /c/Windows/System32/nvml.dll` exits 0.
6. `git --version` exits 0 and `git rev-parse --is-inside-work-tree` prints `true`. No `git init` is run (design C-10: the repository already exists).
7. Every result is appended under `## Toolchain check` as: command, exit code, UTC date, label (measured or user-reported).
8. The build-start commits are recorded under `## Base commits`: `git rev-parse origin/main` and `git rev-parse HEAD` with their UTC date (measured); `git rev-parse origin/main` printing a 40-character hash is the check, and a failure (no remote ref) is recorded as `no origin/main ref`. Later stories and the M7 commit-message check use the HEAD hash as `<build-start>` because planning commits made before the build are not build commits.
BDD scenarios:
```gherkin
@integration @AJ-S002 @REQ-001
Feature: Verify toolchain and machine prerequisites
  @positive
  Scenario: AJ-002 SDK 10.0.401 is present
    Given the repository root is the working directory
    When I run "dotnet --version"
    Then the exit code is 0 and the output starts with "10.0.4"
  @positive
  Scenario: AJ-002 GPU and driver are recorded as measured
    Given nvidia-smi is on the PATH
    When I run the nvidia-smi query for name, driver_version and power.limit
    Then the exit code is 0, the name contains "3080" and the driver version is logged with label measured and today's UTC date
  @positive
  Scenario: AJ-002 The existing repository is reused
    Given the folder contains a .git directory
    When I run "git rev-parse --is-inside-work-tree"
    Then it prints "true" and no "git init" command appears in the log
  @negative
  Scenario: AJ-002 An older SDK stops the build
    Given "dotnet --version" prints 10.0.300 or the command fails
    When the toolchain check evaluates the result
    Then the item is written under "Requirements that cannot be met" and global.json is not edited
  @negative
  Scenario: AJ-002 A stopped Docker daemon blocks only the container checks
    Given "docker info" exits non-zero
    When the toolchain check evaluates the result
    Then the log says "Docker daemon not running", the M1 container check is marked blocked and the remaining checks still run
  @negative
  Scenario: AJ-002 A missing nvidia-smi is recorded as no GPU
    Given "nvidia-smi" is not found
    When the toolchain check evaluates the result
    Then the log says "no NVIDIA GPU tooling on this machine", the gated NVML integration test is marked blocked and no driver version is invented
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-002-T1 | Run the SDK and git checks and capture their output | docs/build-log.md | 0.25 | AJ-001-T2 |
| AJ-002-T2 | Run the Docker, WSL2 and Ollama checks and capture their output | docs/build-log.md | 0.25 | AJ-002-T1 |
| AJ-002-T3 | Run the nvidia-smi and nvml.dll checks and compare with the prompt values | docs/build-log.md | 0.25 | AJ-002-T1 |
| AJ-002-T4 | Write the result table with command, exit code, UTC date and label | docs/build-log.md | 0.25 | AJ-002-T2, AJ-002-T3 |
| AJ-002-T5 | Record the origin/main and HEAD hashes at build start under Base commits | docs/build-log.md | 0.25 | AJ-002-T1 |
Test mapping: no xUnit class; shell checks in the criteria, results logged under `## Toolchain check`.
Docs impact: docs/build-log.md `## Toolchain check`.
Definition of done:
- [x] scenarios green (shell checks), no projects exist yet
- [x] docs listed above updated
- [x] every blocked item names the milestone it blocks (M1 container check, M1 gated NVML test, M4 local runtime)
- Note (2026-10-18): acceptance criterion 4 is unmet, Ollama is not installed (user action, recorded under Requirements that cannot be met in docs/build-log.md); the M4 local runtime is gated on it.
Demo relevance: no

### AJ-003: Create the repository folders
Covers: REQ-001
User story: As a developer, I want the fixed top-level folders in place before any project exists, so that every later story writes into a layout that REQ-001 already guarantees.
Acceptance criteria:
1. `for d in src tests docs content results tasks config; do test -d "$d" || echo "missing $d"; done` prints nothing (docs/ already exists).
2. `find content results tasks config -maxdepth 1 -name .gitkeep | wc -l` prints 4, so git can track the otherwise empty folders.
3. The repository root contains only these directories: `.git`, `.code-review-graph` (tool state, ignored by AJ-009), `.github` (added in M1), `src`, `tests`, `docs`, `content`, `results`, `tasks`, `config` (check: `ls -d */` compared to that set). A drive-qualified path appended to the working directory (a literal `C:` folder) is a defect and fails the check.
BDD scenarios:
```gherkin
@unit @AJ-S003 @REQ-001
Feature: Create the repository folders
  @positive
  Scenario: AJ-003 All seven required folders exist
    Given the repository root is the working directory
    When the folder check loop runs
    Then it prints nothing and exits 0
  @positive
  Scenario: AJ-003 Empty folders are trackable
    Given content, results, tasks and config are empty
    When I list .gitkeep files in them
    Then exactly 4 are found
  @negative
  Scenario: AJ-003 A missing folder is named
    Given the folder config was not created
    When the folder check loop runs
    Then it prints "missing config" and exits non-zero
  @negative
  Scenario: AJ-003 A stray literal drive folder is detected
    Given a folder named "C:" or "D:" exists at the repository root
    When the root directory allow-list check runs
    Then it prints the unexpected folder name and exits non-zero
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-003-T1 | Create src, tests, content, results, tasks and config with a .gitkeep in content, results, tasks and config | content/.gitkeep, results/.gitkeep, tasks/.gitkeep, config/.gitkeep | 0.25 | AJ-001-T2 |
| AJ-003-T2 | Run the folder and root allow-list checks and log them | docs/build-log.md | 0.25 | AJ-003-T1 |
Test mapping: no xUnit class; shell checks in the criteria.
Docs impact: none (layout is defined by the design; results are logged in docs/build-log.md).
Definition of done:
- [x] scenarios green (shell checks)
- [x] docs listed above updated
- [x] the four .gitkeep files are the only files added by this story
Demo relevance: no
Conflict: REQ-001 requires `tasks/` to exist and git cannot track an empty folder, but design section 3.4 says `tasks/` holds exactly three JSON files and nothing else. Proposed resolution: M0 adds `tasks/.gitkeep`; the M2 story that adds the three task files (REQ-012) deletes `tasks/.gitkeep` in the same commit, so the loader and the REQ-012 acceptance ("2 or 3 .json files") see only task files.

### AJ-004: Pin the SDK and share the build properties
Covers: REQ-001
User story: As a developer, I want global.json, a shared Directory.Build.props, a test-only props file and the analyzer rules in .editorconfig, so that every project targets net10.0 with warnings as errors and nullable enabled without repeating settings.
Acceptance criteria:
1. `global.json` equals the design 2.3 content: `{ "sdk": { "version": "10.0.401", "rollForward": "latestPatch", "allowPrerelease": false } }`, and `grep -c '"test"' global.json` prints 0 (no Microsoft.Testing.Platform runner, ADR-03).
2. `grep -F "<TreatWarningsAsErrors>true</TreatWarningsAsErrors>" Directory.Build.props` and `grep -F "<Nullable>enable</Nullable>" Directory.Build.props` each print one line (REQ-001 literal strings).
3. Directory.Build.props sets, each found by grep: `TargetFramework` net10.0, `ImplicitUsings` enable, `LangVersion` latest, `AnalysisLevel` 10.0-recommended, `EnforceCodeStyleInBuild` true, `GenerateDocumentationFile` true, `NoWarn` `$(NoWarn);CS1591`, `EnableConfigurationBindingGenerator` true, `IsPackable` false, `Deterministic` true, and `ContinuousIntegrationBuild` true only under the condition `'$(GITHUB_ACTIONS)' == 'true'`.
3a. `InvariantGlobalization` is set only for `src/**`: `src/Directory.Build.props` imports the root file (`<Import Project="$([MSBuild]::GetPathOfFileAbove(Directory.Build.props, $(MSBuildThisFileDirectory)../))" />`) and then sets `InvariantGlobalization` true (design 2.3). Proof: `dotnet msbuild src/AgentJoules.Core -getProperty:InvariantGlobalization` prints `true`; `dotnet msbuild tests/AgentJoules.Specs -getProperty:InvariantGlobalization` does not print `true` (Reqnroll throws CultureNotFoundException under invariant mode, design-review B-02); `grep -c InvariantGlobalization Directory.Build.props` prints 0 and `grep -c InvariantGlobalization tests/Directory.Build.props` prints 0.
   Note (2026-10-18, post-exit review): the MSBuild property is replaced by two `RuntimeHostConfigurationOption` items (System.Globalization.Invariant and System.Globalization.PredefinedCulturesOnly) in src/Directory.Build.props, because the property is forwarded to the analyzers and silences CA1304 to CA1311, which design 5.5 relies on (measured: a culture-sensitive ToString built clean under the property and fails with CA1305 without it). Proof from now on: `grep -c '"System.Globalization.Invariant": true' src/AgentJoules.Cli/bin/Debug/net10.0/AgentJoules.Cli.runtimeconfig.json` prints 1 and `-getProperty:InvariantGlobalization` prints an empty value for every project, src and tests alike. The same review added a root `nuget.config` (single mapped source nuget.org, audit sources cleared) and made NU1900 (vulnerability feed unreachable) an error unless the developer opts in with `-p:AgentJoulesOfflineAudit=true`; NU1901 to NU1904 are always errors and NU1507 cannot fire with one mapped source.
4. tests/Directory.Build.props imports the root file (via `GetPathOfFileAbove`), sets `IsTestProject` true, `NoWarn` `CA1707;CA2007;CA1515`, and lists the shared test PackageReference items (xunit, xunit.runner.visualstudio, Microsoft.NET.Test.Sdk) without a Version attribute.
5. `.editorconfig` keeps all 389 original lines except one in-place change: `csharp_style_namespace_declarations = file_scoped:suggestion` (line 129, measured 2026-10-18) becomes `file_scoped:warning`. Added lines: `dotnet_diagnostic.IDE0005.severity = warning`, `dotnet_diagnostic.IDE0161.severity = warning`, `dotnet_diagnostic.CA1305.severity = warning`, the same for CA1304, CA1307, CA1309 and CA1310, `dotnet_diagnostic.CA1848.severity = warning`, and a section `[*.{cs,json,md,svg,html}]` with `end_of_line = lf`. Proof: `git diff --numstat .editorconfig` shows exactly 1 removed line, and `grep -c "file_scoped:suggestion" .editorconfig` prints 0.
6. `dotnet --version` run in the repository root still prints 10.0.401 or a higher 10.0.4xx patch (global.json resolves).
BDD scenarios:
```gherkin
@unit @AJ-S004 @REQ-001
Feature: Pin the SDK and share the build properties
  @positive
  Scenario: AJ-004 Warnings are errors and nullable is on in one place
    Given Directory.Build.props exists at the repository root
    When I grep for the two literal REQ-001 strings
    Then each grep prints exactly one line
  @positive
  Scenario: AJ-004 global.json resolves the installed SDK
    Given global.json pins 10.0.401 with rollForward latestPatch
    When I run "dotnet --version" in the repository root
    Then it exits 0 and prints a 10.0.4xx version
  @positive
  Scenario: AJ-004 Test projects inherit the root properties
    Given tests/Directory.Build.props imports the root file
    When AJ-007 exists and I run "dotnet msbuild tests/AgentJoules.UnitTests -getProperty:TreatWarningsAsErrors"
    Then it prints "true"
  @negative
  Scenario: AJ-004 A test section in global.json is rejected
    Given global.json contains a "test" section selecting Microsoft.Testing.Platform
    When the global.json check runs
    Then it fails because ADR-03 requires the VSTest runner
  @negative
  Scenario: AJ-004 Deleting an original editorconfig line is rejected
    Given the diff of .editorconfig removes more than the one namespace severity line
    When "git diff --numstat .editorconfig" is evaluated
    Then the removed-line count is not 1 and the story fails
  @positive
  Scenario: AJ-004 Source projects run with invariant globalization
    Given src/Directory.Build.props imports the root file and sets InvariantGlobalization
    When I run "dotnet msbuild src/AgentJoules.Core -getProperty:InvariantGlobalization"
    Then it prints "true"
  @negative
  Scenario: AJ-004 Test projects do not inherit invariant globalization
    Given InvariantGlobalization is set only in src/Directory.Build.props
    When I run "dotnet msbuild tests/AgentJoules.Specs -getProperty:InvariantGlobalization"
    Then it does not print "true"
  @negative
  Scenario: AJ-004 An SDK pin above the installed SDK fails loudly
    Given global.json pins a version higher than any installed SDK
    When I run "dotnet --version"
    Then the CLI reports that a compatible SDK was not found and the pin is corrected only after AJ-002 confirms the installed version
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-004-T1 | Write global.json from design 2.3 | global.json | 0.25 | AJ-002-T4 |
| AJ-004-T2 | Write the root build properties | Directory.Build.props | 0.5 | AJ-004-T1 |
| AJ-004-T3 | Write the test-only build properties | tests/Directory.Build.props | 0.25 | AJ-004-T2 |
| AJ-004-T4 | Extend the analyzer rules and the LF section | .editorconfig | 0.5 | AJ-004-T2 |
| AJ-004-T4a | Create the src-only build properties that import the root file and set InvariantGlobalization | src/Directory.Build.props | 0.25 | AJ-004-T2 |
| AJ-004-T5 | Run the grep, numstat and msbuild -getProperty checks and log them | docs/build-log.md | 0.25 | AJ-004-T3, AJ-004-T4, AJ-004-T4a |
Test mapping: no xUnit class; shell checks in the criteria. The mutation checks that prove the rules fire (unused using, nullable violation) run in AJ-008.
Docs impact: docs/build-log.md `## Toolchain check` (check results).
Definition of done:
- [x] scenarios green (shell checks), build checked in AJ-008
- [x] docs listed above updated
- [x] every rule that later proves a false positive is suppressed in .editorconfig with a one-line reason, never with an unexplained pragma (design 2.3)
Demo relevance: no

### AJ-005: Pin the central package versions
Covers: REQ-002
User story: As a developer, I want one Directory.Packages.props with central package management and the exact verified versions, so that the build is reproducible and no project carries a version number.
Acceptance criteria:
1. `grep -F "<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>" Directory.Packages.props` prints one line.
2. `grep -cE "<PackageVersion Include=" Directory.Packages.props` prints 22, one row per package of design section 2.4: Microsoft.Agents.AI, Microsoft.Agents.AI.Abstractions, Microsoft.Agents.AI.Workflows, Microsoft.Agents.AI.OpenAI (all 1.23.0); Microsoft.Extensions.AI 10.10.0; Microsoft.Extensions.AI.Abstractions and Microsoft.Extensions.AI.OpenAI 10.10.1; OpenAI 2.14.0; OpenTelemetry, OpenTelemetry.Extensions.Hosting, OpenTelemetry.Exporter.Console, OpenTelemetry.Exporter.OpenTelemetryProtocol, OpenTelemetry.Exporter.InMemory (all 1.18.0); Microsoft.Extensions.Hosting and Microsoft.Extensions.Http 10.0.12; System.CommandLine 2.0.13; JsonSchema.Net 8.0.5; xunit 2.9.3; xunit.runner.visualstudio 3.1.5; Microsoft.NET.Test.Sdk 18.11.0; Reqnroll.xUnit 3.3.4; Microsoft.Extensions.TimeProvider.Testing 10.11.0. (M1 story AJ-033 adds the Options packages that the design does not list and records them as additions; the count then rises.)
3. `grep -E "AgentHooks|Agents.AI.Mcp|DevUI|Agents.AI.A2A|Tools.Shell|Workflows.Generators" Directory.Packages.props` exits 1 with no output, and `grep -E "OllamaSharp|Moq|NSubstitute" Directory.Packages.props` exits 1 (design 2.4: not referenced on purpose).
4. `grep -E "JsonSchema.Net" Directory.Packages.props` shows Version `8.0.5` (every 9.x is licensed under the OSMF EULA, not OSI, design C-03).
5. `grep -rnE --include=*.csproj "<PackageReference[^>]*Version" .` exits 1 (no Version or VersionOverride attribute in any project).
6. The decision to stay on the prompt pins (coordinator versions MEAI 10.11.0, MAF 1.25.0, OpenTelemetry 1.20.0 rejected; design C-01, ADR-20, open point O-01) is written under `## Version deviations and additions`. A deviation is only recorded when restore (AJ-008) proves a pinned version is missing or breaks the build; it then names package, pinned version, used version (highest stable within the same major) and reason.
BDD scenarios:
```gherkin
@unit @AJ-S005 @REQ-002
Feature: Pin the central package versions
  @positive
  Scenario: AJ-005 Twenty-two rows with the exact versions
    Given Directory.Packages.props was written from design section 2.4
    When I count PackageVersion rows and compare each version to the table
    Then the count is 22 and every version matches
  @positive
  Scenario: AJ-005 Central management is switched on
    Given Directory.Packages.props exists
    When I grep for ManagePackageVersionsCentrally
    Then the value is true
  @negative
  Scenario: AJ-005 A preview Agent Framework package is rejected
    Given a row named Microsoft.Agents.AI.DevUI exists
    When the forbidden-name grep runs
    Then it exits 0 with the offending line and the story fails
  @negative
  Scenario: AJ-005 A newer coordinator version is rejected without a restore failure
    Given Microsoft.Extensions.AI is listed as 10.11.0
    When the version table check runs
    Then it fails because 10.10.0 is the binding pin and no restore failure is logged
  @negative
  Scenario: AJ-005 JsonSchema.Net 9.x is rejected
    Given JsonSchema.Net is listed with a 9.x version
    When the licence check runs
    Then it fails with "9.x is OSMF EULA, not OSI" and 8.0.5 is required
  @negative
  Scenario: AJ-005 A project-level version is rejected
    Given a csproj contains a PackageReference with a Version attribute
    When the csproj grep runs
    Then it exits 0 with the offending line and the story fails
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-005-T1 | Write Directory.Packages.props with central management and the 22 rows | Directory.Packages.props | 0.5 | AJ-004-T2 |
| AJ-005-T2 | Run the count, forbidden-name, JsonSchema.Net and csproj greps | docs/build-log.md | 0.25 | AJ-005-T1 |
| AJ-005-T3 | Record the stay-on-prompt-pins decision (C-01, ADR-20, O-01) | docs/build-log.md | 0.25 | AJ-005-T2 |
Test mapping: no xUnit class; shell checks in the criteria. Restore proof of the pins runs in AJ-008.
Docs impact: docs/build-log.md `## Version deviations and additions`.
Definition of done:
- [x] scenarios green (shell checks), restore proven in AJ-008
- [x] docs listed above updated
- [x] no package outside design section 2.4 is added in M0
Demo relevance: no

### AJ-006: Create the solution and the seven source projects
Covers: REQ-001
User story: As a developer, I want AgentJoules.slnx and the seven source projects (including the ASP.NET-based Dashboard project) with the design's reference graph, so that M1 can add code to a solution that already builds.
Acceptance criteria:
1. `ls *.sln *.slnx 2>/dev/null | wc -l` prints 1 and the file is `AgentJoules.slnx`.
2. `dotnet sln AgentJoules.slnx list` lists exactly the project files of src/AgentJoules.Nvml, Core, Metering, Workflow, Reporting, Dashboard and Cli (seven lines after the header).
3. Project references match design 2.2 exactly (`grep -h ProjectReference <csproj>`): Core none; Nvml -> Core; Metering -> Core; Workflow -> Core and Metering; Reporting -> Core; Dashboard -> Core and Metering; Cli -> Nvml, Workflow, Reporting, Metering and Dashboard. Core has no reference to Nvml, Metering, Workflow, Reporting, Dashboard or Cli.
3a. ASP.NET Core is confined to Dashboard (design 3.10, ADR-21): `grep -l "FrameworkReference" --include=*.csproj -r src` prints only src/AgentJoules.Dashboard/AgentJoules.Dashboard.csproj, which contains `<FrameworkReference Include="Microsoft.AspNetCore.App" />`; Core, Metering, Workflow and Reporting csproj files contain no FrameworkReference and no `Microsoft.AspNetCore` text. No new PackageVersion row is added (Directory.Packages.props stays at 22 rows).
4. `grep -rn --include=*.csproj "TargetFramework" .` exits 1 (the value comes only from Directory.Build.props), and `dotnet msbuild src/AgentJoules.Core -getProperty:TargetFramework` prints `net10.0`.
5. `AllowUnsafeBlocks` true appears only in src/AgentJoules.Nvml (`grep -rl AllowUnsafeBlocks --include=*.csproj .` prints one path); the Cli csproj has `OutputType` Exe and a `UserSecretsId`.
6. PackageReference items carry no Version and follow the "Referenced by" column of design 2.4: Workflow takes the four Microsoft.Agents.AI packages, Microsoft.Extensions.AI, Microsoft.Extensions.AI.OpenAI, OpenAI and Microsoft.Extensions.Http; Metering takes Microsoft.Extensions.AI and Microsoft.Extensions.AI.Abstractions; Cli takes the four OpenTelemetry packages, Microsoft.Extensions.Hosting and System.CommandLine; Reporting takes JsonSchema.Net. Proof: `grep -h PackageReference <csproj>` per project lists exactly those names and `dotnet restore` exits 0.
7. `src/AgentJoules.Cli/Program.cs` is a placeholder that writes "AgentJoules: no commands implemented yet" to stderr and returns 1; `dotnet run --project src/AgentJoules.Cli` exits 1. AJ-034 replaces it with the one-line CliApplication call.
8. `dotnet build -warnaserror -tl:off` exits 0 for the solution.
BDD scenarios:
```gherkin
@unit @AJ-S006 @REQ-001
Feature: Create the solution and the seven source projects
  @positive
  Scenario: AJ-006 One solution lists seven source projects
    Given AgentJoules.slnx was created
    When I run "dotnet sln AgentJoules.slnx list"
    Then seven src project paths are printed
  @positive
  Scenario: AJ-006 The reference graph matches the design
    Given the seven csproj files exist
    When I list ProjectReference lines per project
    Then Core has none, Dashboard references Core and Metering, and Cli references Nvml, Workflow, Reporting, Metering and Dashboard
  @positive
  Scenario: AJ-006 Only the Dashboard project references ASP.NET Core
    Given the seven csproj files exist
    When I grep for FrameworkReference across src
    Then only the Dashboard csproj matches, with Microsoft.AspNetCore.App
  @negative
  Scenario: AJ-006 ASP.NET Core must not leak into the library projects
    Given the Workflow csproj contains a FrameworkReference to Microsoft.AspNetCore.App
    When the FrameworkReference grep runs
    Then more than one path is printed and the story fails
  @positive
  Scenario: AJ-006 The placeholder CLI fails honestly
    Given Program.cs is the M0 placeholder
    When I run "dotnet run --project src/AgentJoules.Cli"
    Then the exit code is 1 and stderr says no commands are implemented yet
  @negative
  Scenario: AJ-006 Core must stay free of other projects
    Given Core references Nvml
    When the reference check runs
    Then it fails because Core has no native or AI dependency
  @negative
  Scenario: AJ-006 Unsafe code is limited to the NVML project
    Given AllowUnsafeBlocks appears in the Workflow csproj
    When "grep -rl AllowUnsafeBlocks" runs
    Then more than one path is printed and the story fails
  @negative
  Scenario: AJ-006 A project that redeclares its target framework is detected
    Given a csproj contains a TargetFramework element
    When the TargetFramework grep runs
    Then it exits 0 with the offending file and the story fails
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-006-T1 | Create the solution file | AgentJoules.slnx | 0.25 | AJ-005-T1 |
| AJ-006-T2 | Write the Core, Nvml and Metering project files | src/AgentJoules.Core/AgentJoules.Core.csproj, src/AgentJoules.Nvml/AgentJoules.Nvml.csproj, src/AgentJoules.Metering/AgentJoules.Metering.csproj | 0.5 | AJ-006-T1 |
| AJ-006-T3 | Write the Workflow and Reporting project files | src/AgentJoules.Workflow/AgentJoules.Workflow.csproj, src/AgentJoules.Reporting/AgentJoules.Reporting.csproj | 0.25 | AJ-006-T1 |
| AJ-006-T3b | Write the Dashboard project file with the Microsoft.AspNetCore.App FrameworkReference and references to Core and Metering | src/AgentJoules.Dashboard/AgentJoules.Dashboard.csproj | 0.25 | AJ-006-T2 |
| AJ-006-T4 | Write the Cli project file (references include Dashboard) and the placeholder Program.cs, then run user-secrets init | src/AgentJoules.Cli/AgentJoules.Cli.csproj, src/AgentJoules.Cli/Program.cs | 0.5 | AJ-006-T2, AJ-006-T3, AJ-006-T3b |
| AJ-006-T5 | Add the projects to the solution and run the reference, property and build checks | AgentJoules.slnx, docs/build-log.md | 0.25 | AJ-006-T4 |
Test mapping: no xUnit class; shell checks in the criteria.
Docs impact: none (layout defined by design section 2); check results go to docs/build-log.md.
Definition of done:
- [x] scenarios green (shell checks), `dotnet build -warnaserror -tl:off` 0 warnings
- [x] docs listed above updated
- [x] no Class1.cs or template leftovers remain (`find src -name Class1.cs` prints nothing)
Demo relevance: no

### AJ-007: Create the test projects and the placeholder test classes
Covers: REQ-001
User story: As a developer, I want the four test projects to compile with the six REQ-023 class names present as skipped placeholders, so that `dotnet test` is green from day one without a single fake pass.
Acceptance criteria:
1. `dotnet test --list-tests` output contains each of MeteredChatClientTests, AttributionBaselineTests, CostModelTests, JudgeParsingTests, ReportRenderingTests and SchemaValidationTests (six `grep -c` calls, each at least 1). The class names are only placeholders here; REQ-023 itself is owned by later milestones.
2. `dotnet test` exits 0 and its summary shows `Failed: 0`; every placeholder is reported as skipped with the reason text `placeholder: replaced by AJ-0nn` (the story that implements it). `grep -rn "Assert.True(true)" tests` exits 1: no placeholder may pass.
   Note (2026-10-18, post-exit review): the placeholders carry `[PlaceholderFact("AJ-0nn")]` from TestSupport, which sets the same Skip reason at runtime, so xUnit1004 needs no suppression; `grep -rn "Skip =" tests` therefore matches only the two attribute definitions.
3. tests/AgentJoules.TestSupport sets `<IsTestProject>false</IsTestProject>` and is a plain class library with no test packages; UnitTests, Specs and IntegrationTests do not redeclare `IsTestProject`.
4. UnitTests and Specs reference all seven src projects and TestSupport; IntegrationTests references Core, Nvml, Metering, Workflow and TestSupport. Specs references Reqnroll.xUnit and Microsoft.Extensions.TimeProvider.Testing; UnitTests references Microsoft.Extensions.TimeProvider.Testing and OpenTelemetry.Exporter.InMemory (all without Version).
5. tests/AgentJoules.Specs/reqnroll.json contains `"missingOrPendingStepsOutcome": "Error"` and its csproj sets `ReqnrollUseIntermediateOutputPathForCodeBehind` to true; the folders Features/Metering, Features/Workflow, Features/Cli, Features/Reporting, Features/Configuration, Features/CloudGpu and Features/Dashboard hold a .gitkeep each; no feature file exists yet.
6. IntegrationTests holds one skipped placeholder (reason `placeholder: replaced by AJ-023`) and runs no live code.
7. `dotnet sln AgentJoules.slnx list` shows 11 projects (7 src, 4 tests). The four Dashboard test classes (DashboardEventSerializationTests, ReplayTimelineTests, DashboardPageTests, DashboardSinkTests) are owned by M3 and get no placeholder in M0.
8. `tests/AgentJoules.TestSupport/IntegrationFactAttribute.cs` is the single definition of `[IntegrationFact]` (deriving from `FactAttribute`, setting `Skip = "Set AGENTJOULES_INTEGRATION=1"` unless the variable is exactly `1`); `grep -rn "class IntegrationFactAttribute" tests` prints exactly 1 hit, and IntegrationTests references it through TestSupport. Later stories (M1 AJ-023, M3) only add tests; none defines a second attribute.
   Note (2026-10-18, post-exit review): the comparison uses the trimmed value, so `set AGENTJOULES_INTEGRATION=1 && dotnet test` from cmd.exe (which stores a trailing space) counts as set.
BDD scenarios:
```gherkin
@unit @AJ-S007 @REQ-001
Feature: Create the test projects and the placeholder test classes
  @positive
  Scenario: AJ-007 The six class names are listed
    Given the six placeholder classes exist in tests/AgentJoules.UnitTests
    When I run "dotnet test --list-tests"
    Then the output contains all six class names
  @positive
  Scenario: AJ-007 Placeholders are skipped, never passed
    Given every placeholder carries Skip with a reason
    When I run "dotnet test"
    Then the exit code is 0, Failed is 0 and the skipped count equals the number of placeholders
  @positive
  Scenario: AJ-007 The solution lists eleven projects
    Given the four test projects were added
    When I run "dotnet sln AgentJoules.slnx list"
    Then eleven project paths are printed
  @negative
  Scenario: AJ-007 A fake passing placeholder is rejected
    Given a placeholder body is "Assert.True(true)"
    When "grep -rn 'Assert.True(true)' tests" runs
    Then it exits 0 with the offending line and the story fails
  @negative
  Scenario: AJ-007 A misspelled class name is caught
    Given the class is named MeteredChatClientTest
    When the six list-tests greps run
    Then the grep for MeteredChatClientTests finds no match and the story fails
  @negative
  Scenario: AJ-007 An unbound Reqnroll step fails the run instead of being pending
    Given a scratch copy of the repository contains a feature file with one unbound step
    When "dotnet test" runs in the scratch copy
    Then the run fails because missingOrPendingStepsOutcome is Error
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-007-T1 | Write the TestSupport project file | tests/AgentJoules.TestSupport/AgentJoules.TestSupport.csproj | 0.25 | AJ-006-T5 |
| AJ-007-T2 | Write the UnitTests project file and the six skipped placeholder classes | tests/AgentJoules.UnitTests/AgentJoules.UnitTests.csproj, tests/AgentJoules.UnitTests/MeteredChatClientTests.cs, tests/AgentJoules.UnitTests/AttributionBaselineTests.cs, tests/AgentJoules.UnitTests/CostModelTests.cs, tests/AgentJoules.UnitTests/JudgeParsingTests.cs, tests/AgentJoules.UnitTests/ReportRenderingTests.cs, tests/AgentJoules.UnitTests/SchemaValidationTests.cs | 0.5 | AJ-007-T1 |
| AJ-007-T3 | Write the Specs project file, reqnroll.json and the seven capability folders | tests/AgentJoules.Specs/AgentJoules.Specs.csproj, tests/AgentJoules.Specs/reqnroll.json, tests/AgentJoules.Specs/Features/Metering/.gitkeep (and the six sibling folders) | 0.5 | AJ-007-T1 |
| AJ-007-T4 | Write the IntegrationTests project file and its skipped placeholder | tests/AgentJoules.IntegrationTests/AgentJoules.IntegrationTests.csproj, tests/AgentJoules.IntegrationTests/PlaceholderIntegrationTests.cs | 0.25 | AJ-007-T1 |
| AJ-007-T4a | Create the single IntegrationFactAttribute in TestSupport and check the grep count | tests/AgentJoules.TestSupport/IntegrationFactAttribute.cs | 0.25 | AJ-007-T1 |
| AJ-007-T5 | Add the projects to the solution, run list-tests and test, and log the counts | AgentJoules.slnx, docs/build-log.md | 0.25 | AJ-007-T2, AJ-007-T3, AJ-007-T4, AJ-007-T4a |
Test mapping: placeholders for MeteredChatClientTests (owned by M2), AttributionBaselineTests (M1, AJ-026), CostModelTests, JudgeParsingTests, ReportRenderingTests and SchemaValidationTests (M3); no Specs feature yet.
Docs impact: docs/build-log.md `## Test counts`.
Definition of done:
- [x] scenarios green, `dotnet test` exit 0, `dotnet build -warnaserror -tl:off` 0 warnings
- [x] docs listed above updated
- [x] if the xUnit1004 "test methods should not be skipped" analyzer surfaces as a warning, it is lowered for tests/ only in .editorconfig with a one-line reason
- Note (2026-10-18): xUnit1004 did not surface as a build warning; it surfaced at info severity under `dotnet format --severity info` after the M0 exit; the placeholders now use `[PlaceholderFact("AJ-0nn")]` (TestSupport), which sets Skip at runtime, so no .editorconfig suppression is needed and none remains.
- Note (2026-10-19): `tests/AgentJoules.Specs/Features/Configuration/.gitkeep` was deleted once that folder held its first feature file (AJ-010); the other six capability folders keep theirs until their first feature lands.
Demo relevance: no

### AJ-008: Gate the skeleton with build, test and format checks
Covers: REQ-001
User story: As a reviewer, I want one recorded run of restore, build, test and format on the empty solution, so that the REQ-001 acceptance holds before any behaviour is added and every later regression is attributable.
Acceptance criteria:
1. `dotnet restore` exits 0. A missing package version, or a NuGet audit warning (NU1901 to NU1904) that fails the build, is recorded under `## Version deviations and additions` with package, advisory id, pinned version and the action taken; warnings are never silenced with an unexplained NoWarn.
2. `dotnet build -warnaserror -tl:off --no-incremental` exits 0 and its output contains `0 Warning(s)` and `0 Error(s)`; then the literal REQ-001 command `dotnet build -warnaserror -tl:off` also exits 0 with both strings.
3. `dotnet test` exits 0; the passed, failed and skipped counts are written under `## Test counts` (expected passed 0, failed 0, skipped equal to the placeholder count found by `grep -rc "Skip =" tests`).
   Note (2026-10-18, post-exit review): the placeholder count is now `grep -rc 'PlaceholderFact("' tests --include=*.cs` summed (7).
4. `dotnet format --verify-no-changes` exits 0.
5. `dotnet msbuild <project> -getProperty:TargetFramework` prints `net10.0` for each of the eleven projects.
6. Mutation checks run in a scratch copy of the repository inside the session scratchpad directory (copy the tracked and untracked files, never mutate the working tree): an unused `using` makes the build fail with IDE0005; a nullable violation fails with CS8600; a CRLF line ending in a .cs file makes `dotnet format --verify-no-changes` exit non-zero. The working tree is unchanged (`git status --porcelain` output identical before and after).
BDD scenarios:
```gherkin
@unit @AJ-S008 @REQ-001
Feature: Gate the skeleton with build, test and format checks
  @positive
  Scenario: AJ-008 The strict build prints zero warnings and zero errors
    Given the eleven projects exist
    When I run "dotnet build -warnaserror -tl:off --no-incremental"
    Then the exit code is 0 and the output contains "0 Warning(s)" and "0 Error(s)"
  @positive
  Scenario: AJ-008 Test and format are green on the skeleton
    Given the placeholders are skipped
    When I run "dotnet test" and "dotnet format --verify-no-changes"
    Then both exit 0 and the counts are logged under "Test counts"
  @negative
  Scenario: AJ-008 An unused using breaks the build
    Given a scratch copy has "using System.Text;" unused in a source file
    When the strict build runs in the scratch copy
    Then it fails with IDE0005 as an error
  @negative
  Scenario: AJ-008 A nullable violation breaks the build
    Given a scratch copy assigns null to a non-nullable string
    When the strict build runs in the scratch copy
    Then it fails with a CS86xx diagnostic as an error
  @negative
  Scenario: AJ-008 A CRLF source file breaks the format check
    Given a scratch copy has a .cs file with CRLF line endings
    When "dotnet format --verify-no-changes" runs in the scratch copy
    Then the exit code is not 0
  @negative
  Scenario: AJ-008 A NuGet audit warning is logged, not silenced
    Given restore reports NU1903 for a pinned package
    When the build fails under warnings-as-errors
    Then the package, advisory id and decision are written under "Version deviations and additions" and no NoWarn entry is added
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-008-T1 | Run restore and both build commands and capture the summary lines | docs/build-log.md | 0.5 | AJ-007-T5 |
| AJ-008-T2 | Run dotnet test, dotnet format and the per-project TargetFramework check | docs/build-log.md | 0.25 | AJ-008-T1 |
| AJ-008-T3 | Run the three mutation checks in a scratch copy | docs/build-log.md | 0.5 | AJ-008-T1 |
| AJ-008-T4 | Write the counts and any deviation rows | docs/build-log.md | 0.25 | AJ-008-T2, AJ-008-T3 |
Test mapping: no new xUnit class; exercises the skeleton from AJ-006 and AJ-007.
Docs impact: docs/build-log.md `## Test counts` and `## Version deviations and additions`.
Definition of done:
- [x] scenarios green, unit tests green (skipped placeholders only), `dotnet build -warnaserror -tl:off` 0 warnings
- [x] docs listed above updated
- [x] REQ-001 commit made by the builder with a message starting `AJ-008 REQ-001:` (nothing pushed)
Demo relevance: no

### AJ-009: Verify licence and README, extend the ignore and attribute files
Covers: REQ-002, REQ-029
User story: As a maintainer, I want the licence and README confirmed and the ignore rules for secrets, partial results and tool state completed, so that no key, private key or half-written results file can be committed and generated files stay byte-stable on Windows.
Acceptance criteria:
1. `head -n 1 LICENSE | grep -F "MIT License"` exits 0 (measured 2026-10-18: the first line is `MIT License`). If a different licence is required, docs/devpost-checklist.md (created in M6) names it; until then a non-MIT first line is a stop.
2. `README.md` exists and is extended in place, never replaced: `git diff --numstat README.md` shows 0 removed lines for any M0 change (M0 does not edit it).
3. Measured on 2026-10-18 by reading .gitignore: `[Bb]in/`, `[Oo]bj/`, `artifacts/`, `results/*.raw`, `*.pem`, `id_ed25519*`, `id_rsa*`, `.env`, `.env.*`, `*.secrets.json`, `secrets.json` and `appsettings.local.json` are already present (design conflict C-09 predates them). Missing and added here: `results/*.partial` and `.code-review-graph/` (tool state; a one-line comment gives the reason). `git check-ignore results/x.raw results/x.partial src/AgentJoules.Core/bin/x src/AgentJoules.Core/obj/x a.pem id_ed25519 id_ed25519.pub id_rsa id_rsa.pub artifacts/linux-x64/x .code-review-graph/graph.db` prints all eleven paths.
4. Controls are not ignored: `git check-ignore results/20261018T120000Z-local.json results/probe-20261018T120000Z.json docs/report/index.html` exits 1 with no output.
5. `.gitattributes` gets `* text=auto eol=lf` as its new first line (design 2.3) and every other original line is kept, so `*.csproj`, `*.props`, `*.slnx`, `*.yml`, `*.yaml`, `*.cs`, `*.md`, `*.json` and `*.svg` check out with LF under `core.autocrlf=true` (measured on this laptop). Proof: `git diff --numstat .gitattributes` shows 1 added and 0 removed lines; `git check-attr eol -- global.json src/a.csproj .github/workflows/ci.yml README.md results/x.json docs/report/index.html` prints `eol: lf` for each. `*.ps1 text eol=crlf` and `*.sh text eol=lf` stay as they are. Reason: `.editorconfig` demands `end_of_line = lf`, and a CRLF working tree would fail `dotnet format --verify-no-changes` and the README idempotence check of REQ-025.
6. `git diff --numstat .gitignore .gitattributes` shows 0 removed lines for `.gitignore` and 0 removed lines for `.gitattributes`.
BDD scenarios:
```gherkin
@unit @AJ-S009 @REQ-002 @REQ-029
Feature: Verify licence and README, extend the ignore and attribute files
  @positive
  Scenario: AJ-009 The licence is MIT
    Given LICENSE exists
    When I read its first line
    Then it contains "MIT License"
  @positive
  Scenario: AJ-009 Raw logs, partial results and key files are ignored
    Given the .gitignore additions are in place
    When I run git check-ignore on results/x.raw, results/x.partial, a.pem, id_ed25519 and id_rsa
    Then each path is printed and the exit code is 0
  @positive
  Scenario: AJ-009 Generated text files check out with LF
    Given the .gitattributes first line is "* text=auto eol=lf"
    When I run git check-attr eol on README.md, global.json and src/a.csproj
    Then all three report "eol: lf"
  @negative
  Scenario: AJ-009 Committed results are not ignored
    Given results/20261018T120000Z-local.json is a legitimate run file name
    When I run git check-ignore on it
    Then the exit code is 1 and nothing is printed
  @negative
  Scenario: AJ-009 A removed secret pattern is caught
    Given the line "id_rsa*" was deleted from .gitignore
    When git check-ignore runs on id_rsa
    Then it exits 1 and the story fails
  @negative
  Scenario: AJ-009 A non-MIT licence stops the build
    Given the first line of LICENSE is not "MIT License" and no checklist names another licence
    When the licence check runs
    Then the item is written under "Requirements that cannot be met" and work on REQ-002 stops
  @negative
  Scenario: AJ-009 A replaced README is flagged
    Given the README diff removes lines
    When "git diff --numstat README.md" is evaluated
    Then the removed count is above 0 and the story fails
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-009-T1 | Check the licence first line and the README diff | docs/build-log.md | 0.25 | AJ-001-T2 |
| AJ-009-T2 | Append the two missing ignore patterns with comments | .gitignore | 0.25 | AJ-009-T1 |
| AJ-009-T3 | Prepend the global eol=lf attribute line, keeping every other line | .gitattributes | 0.25 | AJ-009-T1 |
| AJ-009-T4 | Run the check-ignore, check-attr and numstat matrix and log it | docs/build-log.md | 0.5 | AJ-009-T2, AJ-009-T3 |
Test mapping: no xUnit class; shell checks in the criteria (they are the REQ-002 acceptance commands).
Docs impact: docs/build-log.md; README.md is not edited in M0.
Definition of done:
- [x] scenarios green (shell checks), `dotnet build -warnaserror -tl:off` 0 warnings
- [x] docs listed above updated
- [x] REQ-002 commit made by the builder with a message starting `AJ-009 REQ-002:` (nothing pushed)
- Note (2026-10-18): the commit subject is `AJ-009 REQ-002 REQ-029:` because the story covers both IDs (standing order 5); the literal `AJ-009 REQ-002:` is therefore not matched, the pattern check is.
Demo relevance: no

### AJ-010: Redact key values in any text output
Covers: REQ-029
User story: As a user who keeps API keys in environment variables, I want every console line and exception message to mask key values, so that a pasted log or a failing endpoint call can never leak a key.
Acceptance criteria:
1. `dotnet test --filter "Spec=REQ-029" --logger "console;verbosity=normal"` shows at least 1 passed and 0 failed.
2. A test builds a fake key at runtime as `"nvapi-" + "abcdef1234567890"`; `SecretRedactor.Redact` returns `nvapi-****` for it and the result does not contain `abcdef1234567890`.
3. A value registered with the redactor (any format, for example an opaque string built at runtime) is replaced everywhere it occurs, including inside a longer sentence and twice in one string, by `****` and nothing of the value remains (design 3.2 and design-review N-09; the six-character reveal in the first version of this story was superseded on 2026-10-18 at the M0 peer review).
4. The pattern `nvapi-[A-Za-z0-9_-]{4,}` masks an unregistered `nvapi-` value; `Redact(Redact(x))` equals `Redact(x)`.
5. `RedactingTextWriter` wrapping a StringWriter masks a key that arrives split across two `Write` calls: text is held until a newline, so no unmasked fragment reaches the inner writer (reworded 2026-10-19: `Flush` forwards nothing held back; `Dispose` drains the remainder).
   Note (2026-10-18, post-exit review): Flush forwards complete lines only and Dispose drains the remainder, because a Flush between two halves of a key forwarded both halves unmasked (measured); the writer rejects nothing on Flush and the scenario text is unchanged because it ends with a newline. Register returns a bool: the whole value and, after a split on CR and LF, each of its lines are registered in up to three forms each (trimmed at both ends only, keeping the original code units so a lone surrogate still matches a raw echo; the clean form with every Unicode format or control character and every invalid UTF-16 sequence removed by Rune and then trimmed; and the rune-normalised form of the ends-trimmed text with U+FFFD in place of a lone surrogate, which matches an echo that passed through UTF-8), each form ignored when shorter than five characters, containing `*`, or part of the `nvapi-****` mask; it returns false only when nothing is registrable, so a value echoed in its original decorated form and the bare key are both masked (2026-10-19 review: surrogates and line terminators); Redact merges overlapping and adjacent spans in one pass.
6. `git grep -nE "nvapi-[A-Za-z0-9_-]{8,}" -- src tests` exits 1: no tracked line contains a literal key.
7. Wiring into Console.SetOut and Console.SetError is done by AJ-034 (M1); this story delivers the types and their tests only.
BDD scenarios:
```gherkin
@unit @AJ-S010 @REQ-029
Feature: Redact key values in any text output
  @positive
  Scenario: AJ-010 A registered key is masked completely
    Given a redactor registered with a runtime-built opaque key
    When I redact "request failed for <key> twice <key>"
    Then both occurrences become "****" and no character of the key remains
  @positive
  Scenario: AJ-010 An unregistered nvapi value is masked by pattern
    Given a redactor with no registered values
    When I redact a runtime-built "nvapi-" plus 16 characters
    Then the result is "nvapi-****"
  @positive
  Scenario: AJ-010 A key split across two writes is masked
    Given a RedactingTextWriter over a StringWriter
    When "nvapi-" and "abcdef1234567890" are written separately and then a newline
    Then the inner writer receives only "nvapi-****" followed by the newline
  @negative
  Scenario: AJ-010 An empty configured value is ignored
    Given a redactor registered with null, empty and whitespace values
    When I redact "plain text"
    Then the text is unchanged and no exception is thrown
  @negative
  Scenario: AJ-010 A short configured value is fully masked
    Given a redactor registered with the 5-character value "abc12"
    When I redact "token abc12 failed"
    Then the output is "token **** failed" because every registered value is masked completely
  @negative
  Scenario: AJ-010 Null input yields an empty string
    Given a redactor
    When I redact null
    Then the result is an empty string and no exception is thrown
  @negative
  Scenario: AJ-010 Redaction is stable on already masked text
    Given the text "nvapi-****"
    When I redact it twice
    Then both results equal "nvapi-****"
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-010-T1 | Write the failing feature and the RedactionTests class with trait Spec=REQ-029 | tests/AgentJoules.Specs/Features/Configuration/AJ-010-redact-key-values.feature, tests/AgentJoules.Specs/Steps/ConfigurationSteps.cs, tests/AgentJoules.UnitTests/RedactionTests.cs | 0.5 | AJ-008-T4 |
| AJ-010-T2 | Implement SecretRedactor with registered values, the nvapi pattern and the short-value rule | src/AgentJoules.Core/Security/SecretRedactor.cs | 0.5 | AJ-010-T1 |
| AJ-010-T3 | Implement RedactingTextWriter with line buffering and flush | src/AgentJoules.Cli/Output/RedactingTextWriter.cs | 0.5 | AJ-010-T2 |
| AJ-010-T4 | Run the Spec=REQ-029 filter and the literal-key grep and log both | docs/build-log.md | 0.25 | AJ-010-T3 |
Test mapping: RedactionTests (tests/AgentJoules.UnitTests/RedactionTests.cs, trait Spec=REQ-029); Specs feature tests/AgentJoules.Specs/Features/Configuration/AJ-010-redact-key-values.feature. Masking rule: every registered value becomes `****` and `nvapi-` keys become `nvapi-****` (design 3.2, design-review N-09); the six-character reveal in the first version of this story was superseded on 2026-10-18.
Docs impact: docs/build-log.md `## Per-ID filter results` (REQ-029 filter row).
Definition of done:
- [x] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [x] docs listed above updated
- [x] the tests fail before SecretRedactor exists (red first), recorded in docs/build-log.md
- [x] no test fixture contains a literal matching `nvapi-[A-Za-z0-9_-]{8,}`
- Note (2026-10-18): criterion 3 and two scenarios were superseded at the M0 peer review (design 3.2, N-09): every registered value is masked as `****`; the feature file, steps and tests follow the updated text in the AJ-010 follow-up commit.
Demo relevance: no

### AJ-011: Run the secret scan and record it
Covers: REQ-029
User story: As a maintainer, I want a secret scan with a recorded name and exit code, so that "no key in the repository" is a fact in docs/build-log.md and not a hope.
Acceptance criteria:
1. If `gitleaks version` exits 0, `gitleaks detect --source . --no-banner` exits 0 and the scan is recorded as `gitleaks detect`, exit code 0. Otherwise the fallback is used and recorded as `git grep fallback`.
2. Fallback: `git grep -nE "nvapi-[A-Za-z0-9_-]{8,}"` exits 1 with no output. Exit 1 means no match and is the clean result; exit 0 means hits and fails the story.
3. Fallback with keys set: for each of NVIDIA_API_KEY, NEBIUS_API_KEY and NGC_API_KEY that is non-empty, `git grep -qF -- "$VAR"` exits 1 (quiet, so no value is printed). An unset or empty variable is logged as `skipped: variable empty`; the builder never runs `git grep -F ""` because an empty pattern matches every line.
   Note (2026-10-18, post-exit review): the value is passed to git on stdin (`printf '%s\n' "${!NAME}" | git grep -qF -f -`, also with `--untracked` and over `$(git rev-list --all)`), never as a command-line argument, so it cannot appear in process listings or git traces; an empty value makes git grep exit 128, so the empty guard stays.
4. The scan also covers untracked files and history: `git grep --untracked -nE "nvapi-[A-Za-z0-9_-]{8,}"` and `git grep -nE "nvapi-[A-Za-z0-9_-]{8,}" $(git rev-list --all)` each exit 1.
5. `## Secret scan` records scan name, the exact command with variable names only, exit codes, UTC date and verdict; afterwards `git grep -nE "nvapi-[A-Za-z0-9_-]{8,}" -- docs/build-log.md` still exits 1.
6. If a real key is ever found, the builder stops, reports the file and line number without printing the value, and tells the user to rotate the key; the builder does not rewrite history or push.
BDD scenarios:
```gherkin
@unit @AJ-S011 @REQ-029
Feature: Run the secret scan and record it
  @positive
  Scenario: AJ-011 gitleaks reports a clean repository
    Given gitleaks is installed
    When I run "gitleaks detect --source . --no-banner"
    Then the exit code is 0 and the scan name and exit code are logged
  @positive
  Scenario: AJ-011 The grep fallback treats exit 1 as clean
    Given gitleaks is not installed
    When "git grep -nE 'nvapi-[A-Za-z0-9_-]{8,}'" returns exit code 1 and no output
    Then the log records "git grep fallback" with exit code 1 and verdict clean
  @positive @integration
  Scenario: AJ-011 The three configured key values are absent from the repository
    Given NVIDIA_API_KEY, NEBIUS_API_KEY and NGC_API_KEY are set and non-empty
    When "git grep -qF" runs once per variable
    Then each exits 1 and no value appears in the log
  @negative
  Scenario: AJ-011 A planted runtime-built key is detected
    Given a scratch repository in the scratchpad directory with a tracked file holding "nvapi-" plus 16 runtime-built characters
    When the fallback grep runs there
    Then it exits 0 and the scan verdict is failed
  @negative
  Scenario: AJ-011 An empty variable is skipped, not matched
    Given NGC_API_KEY is set to the empty string
    When the key-value scan reaches that variable
    Then it logs "skipped: variable empty" and does not run git grep with an empty pattern
  @negative @integration
  Scenario: AJ-011 A real key in the repository stops the build
    Given a tracked file contains the current value of NVIDIA_API_KEY
    When the key-value scan runs
    Then it exits 0 for that variable, the story fails, only the file and line number are reported and the user is told to rotate the key
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-011-T1 | Detect gitleaks and run the primary scan | docs/build-log.md | 0.25 | AJ-010-T4 |
| AJ-011-T2 | Run the quiet key-value, untracked and history greps | docs/build-log.md | 0.25 | AJ-011-T1 |
| AJ-011-T3 | Run the planted-key detection in a scratch repository | docs/build-log.md | 0.5 | AJ-011-T1 |
| AJ-011-T4 | Write the scan name, command, exit codes and verdict | docs/build-log.md | 0.25 | AJ-011-T2, AJ-011-T3 |
Test mapping: no new xUnit class; the in-code half of REQ-029 is RedactionTests (AJ-010). The scan is a recorded command, rerun in M7.
Docs impact: docs/build-log.md `## Secret scan`.
Definition of done:
- [x] scenarios green (shell checks), `dotnet build -warnaserror -tl:off` 0 warnings
- [x] docs listed above updated
- [x] the scan is rerun after every commit that touches docs/ or config/ until M7 and the latest exit code is the one recorded
- Note (2026-10-18): broadened in docs/build-log.md to a re-run on the working tree before every commit until M7, not only those touching docs/ or config/.
Demo relevance: no

### AJ-012: Record the user actions that gate M2 and M5
Covers: REQ-029
User story: As the build session, I want the prompt section 12 steps recorded with owner, status and gating milestone, so that missing keys or the open Nebius AI Cloud account are reported by name before they block a milestone, and no key value is ever requested or stored.
Acceptance criteria:
1. A non-printing presence test passes for each variable: `[ -n "${NVIDIA_API_KEY:-}" ] && echo set || echo missing` prints `set`, likewise for NEBIUS_API_KEY and NGC_API_KEY (user-reported as set on 2026-10-18 as user environment variables; presence is measured). `case "${NVIDIA_API_KEY:-}" in nvapi-*) echo prefix-ok;; esac` prints `prefix-ok`. No value, length or character beyond the public `nvapi-` prefix is printed.
2. `## User actions` in docs/build-log.md holds a table with 13 rows (steps 1 to 13) with columns step, action, owner (user), status (done, open or unknown), gates (milestone) and evidence (user-reported or measured). Required content: steps 5, 6 and 7 done (user-reported, presence measured); step 10 open and gating M5; step 11 unknown and gating M5; steps 1 to 4 unknown; steps 8 and 9 after the build (M7); steps 12 and 13 on builder request (M5).
3. The table states: steps 5 and 6 gate M2 (`AJ run --tier nvidia|nebius|all`, the judge, the `validate --config` verification calls); step 4 comes before the 45-minute Nebius onboarding time box in M2; step 7 gates the M4 NIM attempt; steps 10 and 11 gate M5.
4. `git ls-files | grep -E "id_(rsa|ed25519)|\.pem$"` exits 1 and `find . -name "id_ed25519*" -not -path "./.git/*"` prints nothing (step 11: the private key lives outside the repository).
5. `git grep -nE "nvapi-[A-Za-z0-9_-]{8,}" -- docs/build-log.md` exits 1 after the table is written.
6. A missing or empty variable is reported by name only: the log line is exactly `missing: <NAME>` (check: `grep -nE "^missing: [A-Z_]+$" docs/build-log.md` matches and `git grep -nE "nvapi-[A-Za-z0-9_-]{8,}" -- docs/build-log.md` exits 1); the builder never asks the user to paste a key into the session.
BDD scenarios:
```gherkin
@integration @AJ-S012 @REQ-029
Feature: Record the user actions that gate M2 and M5
  @positive
  Scenario: AJ-012 All three keys are present and nothing is printed
    Given NVIDIA_API_KEY, NEBIUS_API_KEY and NGC_API_KEY are set as user environment variables
    When the presence test runs
    Then it prints "set" three times and no value, length or suffix
  @positive
  Scenario: AJ-012 The action table covers all thirteen steps
    Given the User actions section was written
    When I count table rows
    Then there are 13 and steps 5, 6 and 7 are marked done with evidence user-reported
  @positive
  Scenario: AJ-012 The open AI Cloud account is reported as an M5 gate
    Given step 10 has status open
    When the table is read
    Then step 10 names M5 as the gated milestone and no M2 item depends on it
  @negative
  Scenario: AJ-012 A missing key is reported by name only
    Given NEBIUS_API_KEY is not set in this session
    When the presence test runs
    Then the log says "missing: NEBIUS_API_KEY", M2 is marked BLOCKED for the nebius tier and no value is requested
  @negative
  Scenario: AJ-012 A key set after the session started is not guessed
    Given the user ran setx after this terminal opened
    When the presence test reports missing
    Then the builder asks the user to open a new terminal, and does not request the key text
  @negative
  Scenario: AJ-012 A private key inside the repository stops the build
    Given a file named id_ed25519 exists under the repository
    When the SSH hygiene check runs
    Then it reports the path, the story fails and the user is told to move and rotate the key
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-012-T1 | Run the non-printing presence tests for the three variables | docs/build-log.md | 0.25 | AJ-011-T4 |
| AJ-012-T2 | Write the 13-row User actions table with owner, status, gate and evidence | docs/build-log.md | 0.5 | AJ-012-T1 |
| AJ-012-T3 | Run the SSH key hygiene and key-pattern greps and log them | docs/build-log.md | 0.25 | AJ-012-T2 |
Test mapping: no xUnit class; shell checks in the criteria. The code-side key handling (missing key exit 3, user-secrets) is built in M2.
Docs impact: docs/build-log.md `## User actions`.
Definition of done:
- [x] scenarios green (shell checks), `dotnet build -warnaserror -tl:off` 0 warnings
- [x] docs listed above updated
- [x] REQ-029 commit made by the builder with a message starting `AJ-012 REQ-029:` (nothing pushed)
Demo relevance: no

## Risks

| Risk | Trigger | Mitigation | Owner |
|---|---|---|---|
| NuGet audit turns a pinned package into a build error | `dotnet restore` or build prints NU1901 to NU1904 under TreatWarningsAsErrors | record package and advisory in docs/build-log.md, move to the highest stable version within the same major (REQ-002 deviation rule), never add an unexplained NoWarn | builder |
| A pinned package version is not on NuGet | `dotnet restore` exits non-zero in AJ-008 | same deviation rule, then re-verify every API that story uses; M0 uses no API yet | builder |
| Docker Desktop not running | `docker info` exits non-zero in AJ-002 | start Docker Desktop (user), or mark the M1 container check blocked and continue; never skipped silently | user |
| Keys not visible to this session | presence test prints `missing` although setx was run | open a new terminal and restart the session; never paste a key | user |
| `.code-review-graph/` or other tool state gets committed | untracked directory shows in `git status` | ignore rule added in AJ-009 and checked with `git check-ignore` | builder |
| Estimate exceeds the two-day window | sum of task estimates is above 12 hours | see Estimate; AJ-011 and AJ-012 can slip into the first day of M1 without blocking it | builder |

## Estimate

Note (2026-10-18): any deferral or descope candidates named in this section are informational; the drop order in docs/plan/plan.md section 3.4 is the single source and classifies stories as MUST, SHOULD or COULD.

Sum of task estimates in hours:

| Story | Hours |
|---|---|
| AJ-001 | 0.50 |
| AJ-002 | 1.25 |
| AJ-003 | 0.50 |
| AJ-004 | 2.00 |
| AJ-005 | 1.00 |
| AJ-006 | 2.00 |
| AJ-007 | 2.00 |
| AJ-008 | 1.50 |
| AJ-009 | 1.25 |
| AJ-010 | 1.75 |
| AJ-011 | 1.25 |
| AJ-012 | 1.00 |
| Total | 16.00 |

Capacity for 2026-10-18 and 2026-10-19 at about six working hours per day is 12 hours, so M0 is 4.00 hours over. The overrun is flagged as a schedule risk, not hidden: it either pushes the M1 start by half a day or is recovered by moving AJ-011 and AJ-012 into the first M1 day.
