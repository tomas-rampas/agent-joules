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

### AJ-005 package-pin checks (2026-10-18T16:28Z to 16:34Z, measured)

| Check | Command | Exit code | Result |
|---|---|---|---|
| central management on | `grep -F "<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>" Directory.Packages.props` | 0 | one line |
| 22 rows | `grep -cE "<PackageVersion Include=" Directory.Packages.props` | 0 | prints 22; every name and version matches design section 2.4 (checked row by row by the spec reviewer) |
| forbidden names | `grep -E "AgentHooks|Agents.AI.Mcp|DevUI|Agents.AI.A2A|Tools.Shell|Workflows.Generators" Directory.Packages.props`; `grep -E "OllamaSharp|Moq|NSubstitute" Directory.Packages.props` | 1; 1 | no output |
| JsonSchema.Net licence | `grep -E "JsonSchema.Net" Directory.Packages.props` | 0 | Version 8.0.5 (MIT; 9.x is OSMF EULA, design C-03) |
| no project-level version | `grep -rnE --include=*.csproj "<PackageReference[^>]*Version" .` | 1 | no output |
| restore proof | `dotnet restore` (run by AJ-006 and AJ-007) | 0 | all 22 pins resolve; no NU1901 to NU1904 warning |

### AJ-006 solution and source-project checks (2026-10-18T16:28Z to 16:34Z, measured)

| Check | Command | Exit code | Result |
|---|---|---|---|
| one solution file | `ls *.sln *.slnx 2>/dev/null` counted with `wc -l` | 0 | 1, `AgentJoules.slnx` |
| seven projects listed | `dotnet sln AgentJoules.slnx list` | 0 | the seven `src\AgentJoules.*` csproj paths |
| reference graph | `grep -h ProjectReference` per csproj | 0 | Core none; Nvml Core; Metering Core; Workflow Core and Metering; Reporting Core; Dashboard Core and Metering; Cli Nvml, Workflow, Reporting, Metering and Dashboard (design 2.2) |
| ASP.NET Core confined | `grep -l "FrameworkReference" --include=*.csproj -r src`; grep for `Microsoft.AspNetCore` in the Core, Metering, Workflow and Reporting csproj files | 0; 1 | only `src/AgentJoules.Dashboard/AgentJoules.Dashboard.csproj`, with `Microsoft.AspNetCore.App`; no match in the four libraries; Directory.Packages.props stays at 22 rows |
| no TargetFramework in a csproj | `grep -rn --include=*.csproj "TargetFramework" .`; `dotnet msbuild src/AgentJoules.Core -getProperty:TargetFramework` | 1; 0 | no output; `net10.0` |
| unsafe only in Nvml | `grep -rl AllowUnsafeBlocks --include=*.csproj .` | 0 | `./src/AgentJoules.Nvml/AgentJoules.Nvml.csproj` only |
| Cli project | read `src/AgentJoules.Cli/AgentJoules.Cli.csproj` | n/a | `OutputType` Exe; `UserSecretsId` present (`dotnet user-secrets init`) |
| packages per project | `grep -h PackageReference` per csproj; `dotnet restore` | 0; 0 | Metering 2, Workflow 8, Reporting 1, Cli 6, Core, Nvml and Dashboard 0, exactly the "Referenced by" column of design 2.4; no Version attribute; restore exit 0 |
| placeholder CLI | `dotnet run --project src/AgentJoules.Cli` | 1 | stderr `AgentJoules: no commands implemented yet`, stdout empty |
| strict build | `dotnet build -warnaserror -tl:off --no-incremental`; `dotnet build -warnaserror -tl:off` | 0; 0 | `0 Warning(s)`, `0 Error(s)` for both |
| format | `dotnet format --verify-no-changes` | 0 | no diff |
| template leftovers | `find src -name Class1.cs` | 0 | prints nothing; the only .cs file is `src/AgentJoules.Cli/Program.cs` |

### AJ-007 test-project checks (2026-10-18T16:36Z to 16:44Z, measured)

| Check | Command | Exit code | Result |
|---|---|---|---|
| six class names listed | `dotnet test --list-tests` then `grep -c <Name>` for MeteredChatClientTests, AttributionBaselineTests, CostModelTests, JudgeParsingTests, ReportRenderingTests, SchemaValidationTests | 0 | 1 each |
| placeholders skipped, never passed | `dotnet test`; `grep -rn "Assert.True(true)" tests` | 0; 1 | `Failed: 0`; UnitTests `Skipped: 6`, IntegrationTests `Skipped: 1`, Specs `No test is available` (zero tests, exit 0); skip reasons `placeholder: replaced by AJ-040` (MeteredChatClientTests), `AJ-026` (AttributionBaselineTests), `AJ-070` (CostModelTests), `AJ-073` (JudgeParsingTests), `AJ-081` (ReportRenderingTests), `AJ-078` (SchemaValidationTests), `AJ-023` (PlaceholderIntegrationTests); no `Assert.True(true)` |
| TestSupport is a plain library | `dotnet msbuild tests/AgentJoules.TestSupport -getProperty:IsTestProject`; read its csproj | 0 | `false`; one PackageReference `xunit` (provides FactAttribute), no runner, no Microsoft.NET.Test.Sdk; UnitTests, Specs and IntegrationTests do not redeclare IsTestProject |
| references per project | `grep -h ProjectReference` and `grep -h PackageReference` per test csproj | 0 | UnitTests and Specs: seven src projects plus TestSupport; IntegrationTests: Core, Nvml, Metering, Workflow, TestSupport; Specs packages Reqnroll.xUnit and Microsoft.Extensions.TimeProvider.Testing; UnitTests packages Microsoft.Extensions.TimeProvider.Testing and OpenTelemetry.Exporter.InMemory; no Version attribute |
| Reqnroll settings | `grep missingOrPendingStepsOutcome tests/AgentJoules.Specs/reqnroll.json`; `dotnet msbuild tests/AgentJoules.Specs -getProperty:ReqnrollUseIntermediateOutputPathForCodeBehind`; `find tests/AgentJoules.Specs/Features -name .gitkeep` counted | 0; 0; 0 | `"missingOrPendingStepsOutcome": "Error"`; `true`; 7 folders (Metering, Workflow, Cli, Reporting, Configuration, CloudGpu, Dashboard); no .feature file |
| unbound step fails the run (scenario 6, scratch copy) | copy of the tree in the scratchpad with `Features/Configuration/Unbound.feature` holding one unbound step; `dotnet test tests/AgentJoules.Specs` there | 1 | `Reqnroll.MissingStepDefinitionException`, `Failed: 1`; the code-behind was generated under `obj/`, so the Reqnroll generator works in this project shape; a control run without `reqnroll.json` also failed, so the file pins Reqnroll's default outcome rather than changing it |
| integration placeholder | read `tests/AgentJoules.IntegrationTests/PlaceholderIntegrationTests.cs` | n/a | one `[Fact(Skip = "placeholder: replaced by AJ-023")]`, no live code |
| eleven projects | `dotnet sln AgentJoules.slnx list` | 0 | 7 src and 4 tests paths; no Dashboard test class placeholder (owned by M3) |
| single IntegrationFact | `grep -rn "class IntegrationFactAttribute" tests` | 0 | exactly 1 hit, `tests/AgentJoules.TestSupport/IntegrationFactAttribute.cs`; derives from FactAttribute; sets `Skip = "Set AGENTJOULES_INTEGRATION=1"` unless the variable is exactly `1` |
| Skip count | `grep -rc "Skip =" tests --include=*.cs` summed; `grep -rc 'Skip = "placeholder' tests --include=*.cs` summed | 0 | 8 and 7: the extra line is the attribute's own assignment in IntegrationFactAttribute.cs, not a placeholder; the skipped-test total is 7 |
| strict build and format with the test projects | `dotnet build -warnaserror -tl:off --no-incremental`; `dotnet format --verify-no-changes` | 0; 0 | `0 Warning(s)`, `0 Error(s)`; no diff |

### AJ-008 gate checks (2026-10-18T16:42Z to 16:44Z, measured by an executor; raw logs kept in the session scratchpad; exit codes as reported by the executor, not saved to files)

| Check | Command | Exit code | Result |
|---|---|---|---|
| restore | `dotnet restore` | 0 | no NU1901 to NU1904 line, no missing version; the restore was a no-op (`All projects are up-to-date for restore`), and none of the 11 `obj/project.nuget.cache` files holds an NU19 code |
| strict build | `dotnet build -warnaserror -tl:off --no-incremental` | 0 | `0 Warning(s)`, `0 Error(s)` |
| REQ-001 literal command | `dotnet build -warnaserror -tl:off` | 0 | `0 Warning(s)`, `0 Error(s)` |
| test | `dotnet test` | 0 | passed 0, failed 0, skipped 7 (UnitTests 6, IntegrationTests 1, Specs no tests) |
| format | `dotnet format --verify-no-changes` | 0 | no output |
| target framework of all 11 projects | `dotnet msbuild <project> -getProperty:TargetFramework`, once per project | 0 each | `net10.0` for Core, Nvml, Metering, Workflow, Reporting, Dashboard, Cli, TestSupport, UnitTests, Specs, IntegrationTests; re-run by the spec reviewer at 16:52Z with the same result; the 11 `bin/Debug/net10.0/` output paths in the strict-build log agree |
| placeholder count | `grep -rc "Skip =" tests --include=*.cs` summed; `grep -rc 'Skip = "placeholder' tests --include=*.cs` summed | 0 | 8; 7; the skipped count 7 equals the placeholder count, the eighth line is the attribute definition |
| mutation: unused using (scratch copy m1) | `using System.Text;` prepended to `src/AgentJoules.Cli/Program.cs`, then `dotnet build -warnaserror -tl:off --no-incremental src/AgentJoules.Cli` | 1 | `error IDE0005: Using directive is unnecessary.` |
| mutation: nullable violation (scratch copy m2) | `string s = null;` prepended and the `Console.Error.WriteLine` argument replaced by `s`, same build | 1 | `error CS8600: Converting null literal or possible null value to non-nullable type.` |
| mutation: CRLF source file (scratch copy m3) | Program.cs given CRLF endings (one CRLF plus a lone trailing CR, 2 CR bytes), then `dotnet format --verify-no-changes` | 2 | `error ENDOFLINE: Fix end of line marker.` (the criterion) and `error FINALNEWLINE` from the trailing CR, both on Program.cs |
| working tree unchanged by the mutations | `git status --porcelain` captured before and after, compared with `diff` | 0 | identical, 7 lines each; the copies were made with `git ls-files -co --exclude-standard` into the scratchpad, never inside the repository |

### AJ-009 hygiene checks (2026-10-18T16:41Z, measured)

| Check | Command | Exit code | Result |
|---|---|---|---|
| licence | `head -n 1 LICENSE` then `grep -F "MIT License"` | 0 | `MIT License` |
| README untouched | `git diff --numstat README.md` | 0 | no output; README.md is not edited in M0 |
| ignore matrix | `git check-ignore results/x.raw results/x.partial src/AgentJoules.Core/bin/x src/AgentJoules.Core/obj/x a.pem id_ed25519 id_ed25519.pub id_rsa id_rsa.pub artifacts/linux-x64/x .code-review-graph/graph.db` | 0 | all eleven paths printed |
| controls not ignored | `git check-ignore results/20261018T120000Z-local.json results/probe-20261018T120000Z.json docs/report/index.html` | 1 | no output |
| eol attribute | `git check-attr eol -- global.json src/a.csproj .github/workflows/ci.yml README.md results/x.json docs/report/index.html`; `git check-attr eol -- a.ps1 a.sh` | 0; 0 | `eol: lf` for all six; `a.ps1: eol: crlf`, `a.sh: eol: lf` |
| numstat | `git diff --numstat .gitignore .gitattributes` | 0 | `2 0 .gitignore` (a comment line and `results/*.partial`) and `1 0 .gitattributes` (`* text=auto eol=lf` as the new first line); 0 removed lines in both |
| working-tree renormalisation | `rm .editorconfig && git checkout -- .editorconfig`; `git ls-files --eol .editorconfig .gitignore .gitattributes README.md` | 0 | `i/lf w/lf` for all four; `.editorconfig` content unchanged (`git status --short .editorconfig` empty); `.gitignore` converted to LF in place after the append |
| negative, scratch repository: removed secret pattern | a throwaway repository in the scratchpad with this `.gitignore` minus the `id_rsa*` line; `git check-ignore id_rsa` | 1 | no output, so the deletion is caught |
| tool state ignored | `git check-ignore .serena/project.yml` | 0 | printed |

### AJ-010 redaction checks (2026-10-18T16:49Z to 16:54Z, measured)

| Check | Command | Exit code | Result |
|---|---|---|---|
| red first | `dotnet test --filter "Category=AJ-S010"` and `dotnet test --filter "Spec=REQ-029"` run before SecretRedactor and RedactingTextWriter existed (16:49Z) | 1; 1 | both failed at build with CS0234 (namespaces AgentJoules.Core.Security and AgentJoules.Cli.Output absent) and CS0246 (SecretRedactor, RedactingTextWriter not found); no test ran |
| green: REQ-029 filter | `dotnet test --filter "Spec=REQ-029" --logger "console;verbosity=normal"` | 0 | Total tests 19, Passed 19, Failed 0 (12 before the review fix round at 16:54Z, 19 after it at 17:02Z) |
| green: story scenarios | `dotnet test --filter "Category=AJ-S010"` | 0 | Passed 7, Failed 0, Skipped 0; the Reqnroll tags surface as the `Category` trait |
| full suite | `dotnet test` | 0 | UnitTests passed 19 skipped 6, Specs passed 7, IntegrationTests skipped 1; failed 0 (17:02Z, after the review fix round) |
| strict build and format | `dotnet build -warnaserror -tl:off --no-incremental`; `dotnet format --verify-no-changes` | 0; 0 | `0 Warning(s)`, `0 Error(s)`; no diff |
| no literal key | `git grep -nE "nvapi-[A-Za-z0-9_-]{8,}" -- src tests`; `grep -rnE "nvapi-[A-Za-z0-9_-]{8,}" src tests --include=*.cs --include=*.feature` | 1; 1 | no tracked or untracked line holds a literal key; every fake key is built at runtime |
| encoding of the five new files | `tr -cd '\r'` byte count and `grep -cP '[^\x00-\x7F]'` per file | 0 | 0 CR bytes and 0 non-ASCII characters in SecretRedactor.cs, RedactingTextWriter.cs, RedactionTests.cs, ConfigurationSteps.cs and the feature file |
| review fix round | regex-first ordering in `Redact`, boundary tests (6 and 7 characters, text after the last newline, `Write(char)`, `WriteLine`), try/finally in `Dispose`, `FormatProvider` and `NewLine` pass-through, trimmed-value registration, deterministic tie-break; then `dotnet build -warnaserror -tl:off --no-incremental`, `dotnet format --verify-no-changes`, `dotnet test` | 0; 0; 0 | `0 Warning(s)`, `0 Error(s)`; no diff; 26 passed, 0 failed, 7 skipped (17:02Z) |

## Version deviations and additions

Every package whose used version differs from the pins of design section 2.4 (package, pinned version, used version, reason) and every package added beyond the design list (REQ-002).

Decision (2026-10-18, AJ-005): stay on the prompt pins. The coordinator versions Microsoft.Extensions.AI 10.11.0, Microsoft.Agents.AI 1.25.0 and OpenTelemetry 1.20.0 are rejected (design C-01, ADR-20 row 1, open point O-01). `dotnet restore` exit 0 on all 22 pins (measured 16:28Z and 16:36Z); no NuGet audit warning; no deviation recorded. M0 adds no package beyond design section 2.4; M1 story AJ-033 adds the Options packages and records them here as additions.

| Package | Pinned version | Used version | Reason |
|---|---|---|---|
| none | | | no deviation in M0 |

AJ-008 (2026-10-18T16:42Z, measured): `dotnet restore` exit 0 with no missing version and no NU1901 to NU1904 audit warning, so no deviation row and no NoWarn entry were needed; the restore was a no-op and the 11 project.nuget.cache files hold no NU19 code.

## Secret scan

Scan name, exact command (variable names only, never values), exit codes, UTC date and verdict of every secret scan run (REQ-029, AJ-011); the latest row is the one that counts.

| Scan | Command (variable names only) | Exit code | UTC date | Verdict |
|---|---|---|---|---|
| gitleaks presence | `gitleaks version` | 127 | 2026-10-18T16:48Z | not installed; the fallback is the scan of record |
| git grep fallback, tracked files | `git grep -nE "nvapi-[A-Za-z0-9_-]{8,}"` | 1 | 2026-10-18T16:48Z | clean (exit 1 means no match) |
| git grep fallback, untracked files | `git grep --untracked -nE "nvapi-[A-Za-z0-9_-]{8,}"` | 1 | 2026-10-18T16:48Z | clean |
| git grep fallback, history | `git grep -nE "nvapi-[A-Za-z0-9_-]{8,}" $(git rev-list --all)` over 37 commits | 1 | 2026-10-18T16:48Z | clean |
| key values, quiet | `git grep -qF -- "$NVIDIA_API_KEY"`, the same for NEBIUS_API_KEY and NGC_API_KEY, each also with `--untracked`; an empty variable is logged as `skipped: variable empty` and never grepped | 1 for each of the six runs | 2026-10-18T16:48Z | clean; all three variables were non-empty, none skipped, no value printed |
| build log | `git grep -nE "nvapi-[A-Za-z0-9_-]{8,}" -- docs/build-log.md`; `grep -nE "nvapi-[A-Za-z0-9_-]{8,}" docs/build-log.md` | 1; 1 | 2026-10-18T16:48Z | clean |
| negative, scratch repository: planted runtime-built key | a throwaway repository in the scratchpad with one tracked file holding `nvapi-` plus 16 runtime-built characters; `git grep -nE "nvapi-[A-Za-z0-9_-]{8,}"` with the output cut to file and line | 0 | 2026-10-18T16:48Z | detected at `planted.txt:1`, verdict failed as expected; the repository was removed afterwards |
| negative: empty variable | `( NAME=EMPTY_TEST_VAR; EMPTY_TEST_VAR=""; if [ -z "${!NAME:-}" ]; then echo "$NAME: skipped: variable empty"; else echo "would grep"; fi )` in a subshell, so the real NGC_API_KEY stays untouched | 0 | 2026-10-18T16:48Z | printed `EMPTY_TEST_VAR: skipped: variable empty`; no grep ran; the same branch guards NGC_API_KEY and the other two names in the key-value rows |
| re-run at commit 71a1cf4 after the AJ-008, AJ-009 and AJ-010 docs commits: tracked, untracked, history | `git grep -nE "nvapi-[A-Za-z0-9_-]{8,}"`; `git grep --untracked -nE "nvapi-[A-Za-z0-9_-]{8,}"`; `git grep -nE "nvapi-[A-Za-z0-9_-]{8,}" $(git rev-list --all)` over 40 commits | 1; 1; 1 | 2026-10-18T17:07Z | clean |
| re-run at commit 71a1cf4: key values, quiet | `git grep -qF -- "$NVIDIA_API_KEY"`, the same for NEBIUS_API_KEY and NGC_API_KEY, each also with `--untracked`; empty variables would be logged as `skipped: variable empty` | 1 for each of the six runs | 2026-10-18T17:07Z | clean; all three variables non-empty, none skipped, no value printed |
| re-run at commit 71a1cf4: key values across history, quiet | `git grep -qF -- "$NVIDIA_API_KEY" $(git rev-list --all)`, the same for NEBIUS_API_KEY and NGC_API_KEY (NEBIUS keys have no public prefix, so only the exact-value grep covers them in history); `git grep -lE "nvapi-[A-Za-z0-9_-]{8,}" $(git rev-list --all)` | 1; 1; 1; 1 | 2026-10-18T17:09Z | clean over 40 commits; no value printed |

Scan of record for AJ-011: `git grep fallback` (gitleaks not installed), exit code 1 (no match), verdict clean, measured first at commit 8635b32 (2026-10-18T16:48:49Z) and re-run at commit 71a1cf4 (2026-10-18T17:07:24Z) after the AJ-008, AJ-009 and AJ-010 commits that touched docs/ and tests/. Rule (broadened on the security reviewer's finding): the scan is re-run on the working tree before each commit until M7 (tracked, untracked and history greps, so the content about to be committed is covered), not only before commits that touch docs/ or config/, because test fixtures under tests/ and src/ are the likelier leak; the latest exit code is the one that counts; each re-run is appended as the next row of the table above, the M0 exit re-run included; future pattern re-runs use `-l` instead of `-n` so a hit prints only the file name. Known limits of the fallback against gitleaks: it cannot catch keys that are not in the current environment, credentials without the `nvapi-` prefix and without a registered value, encoded or split values, dangling objects, or high-entropy strings of unknown shape.

## Test counts

Passed, failed and skipped counts of `dotnet test` per recorded run (AJ-007 and later).

| Run | UTC date | Command | Exit code | Passed | Failed | Skipped | Note |
|---|---|---|---|---|---|---|---|
| AJ-007 skeleton | 2026-10-18T16:41Z | `dotnet test` | 0 | 0 | 0 | 7 | UnitTests 6, IntegrationTests 1, Specs 0 tests (`No test is available`, exit 0); measured |
| AJ-008 gate | 2026-10-18T16:43Z | `dotnet test` | 0 | 0 | 0 | 7 | UnitTests 6, IntegrationTests 1, Specs 0 tests; placeholder count by grep 7; measured |
| AJ-010 redaction | 2026-10-18T17:02Z | `dotnet test` | 0 | 26 | 0 | 7 | UnitTests 19 passed 6 skipped, Specs 7 passed, IntegrationTests 1 skipped; measured after the review fix round |

## Per-ID filter results

One row per `dotnet test --filter "Spec=<ID>"` run: ID, passed, failed, skipped, UTC date.

| ID | UTC date | Command | Passed | Failed | Skipped | Note |
|---|---|---|---|---|---|---|
| REQ-029 | 2026-10-18T17:02Z | `dotnet test --filter "Spec=REQ-029" --logger "console;verbosity=normal"` | 19 | 0 | 0 | AJ-010 RedactionTests, 17:02Z after the review fix round (12 at 16:54Z); measured |

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

### AJ-005 Pin the central package versions (REQ-002)

- Shipped: `Directory.Packages.props` with `ManagePackageVersionsCentrally` true and the 22 rows of design section 2.4; the stay-on-prompt-pins decision under `## Version deviations and additions`.
- Decisions: prompt pins kept (C-01, ADR-20, O-01); JsonSchema.Net 8.0.5 for the MIT licence (C-03); no transitive pinning property added (design does not ask for one).
- Deviations: none; the restore proof (criterion 6) is the AJ-006 and AJ-007 `dotnet restore` runs, both exit 0.
- Gates: pooled with AJ-003 (see that entry); code-review-gatekeeper APPROVED (no finding); security-specialist APPROVED (all 22 names and versions checked against design 2.4, no preview or forbidden package); spec-compliance-reviewer APPROVED (criteria 1 to 5 PASS, criterion 6 written in this commit).
- Velocity: estimate 1.00 h; pooled wall-clock and tokens in the AJ-003 entry.

### AJ-006 Create the solution and the seven source projects (REQ-001)

- Shipped: `AgentJoules.slnx` (slnx format, the seven src projects under a `/src/` solution folder), the seven csproj files with the design 2.2 reference graph and the design 2.4 package placement, `src/AgentJoules.Cli/Program.cs` as the honest placeholder (stderr line, exit 1), a `UserSecretsId` from `dotnet user-secrets init`.
- Decisions: the Dashboard csproj carries the `Microsoft.AspNetCore.App` FrameworkReference but not the design's `wwwroot/index.html` EmbeddedResource, because that file does not exist until M3 and a missing EmbeddedResource fails the build; `dotnet user-secrets init` wrote a UTF-8 BOM into the Cli csproj and `dotnet new sln` wrote CRLF into the slnx, both normalised to ASCII and LF (0 CR bytes, measured); `Program.cs` ends without a trailing newline because the existing `.editorconfig` sets `insert_final_newline = false` for `[*.cs]` and `dotnet format` enforces it.
- Deviations: none from the story.
- Gates: pooled with AJ-003 (see that entry); code-review-gatekeeper APPROVED (no finding); security-specialist APPROVED (UserSecretsId is a GUID, no secret; Program.cs reads no input and no environment); spec-compliance-reviewer APPROVED (criteria 1 to 8 PASS, Class1.cs check PASS).
- Velocity: estimate 2.00 h; pooled wall-clock and tokens in the AJ-003 entry.

### AJ-007 Create the test projects and the placeholder test classes (REQ-001)

- Shipped: `tests/AgentJoules.TestSupport` (plain class library, `IsTestProject` false, `IntegrationFactAttribute.cs`), `tests/AgentJoules.UnitTests` with the six REQ-023 placeholder classes, `tests/AgentJoules.Specs` with `reqnroll.json`, the `ReqnrollUseIntermediateOutputPathForCodeBehind` property and the seven empty `Features/<Capability>/` folders, `tests/AgentJoules.IntegrationTests` with `PlaceholderIntegrationTests.cs`; the four projects added to `AgentJoules.slnx` under a `/tests/` folder (11 projects).
- Decisions: TestSupport references the `xunit` metapackage because `IntegrationFactAttribute` derives from `FactAttribute` and no other pinned package provides it; it carries no runner and no `Microsoft.NET.Test.Sdk`, which is how "no test packages" in criterion 3 is read (adding `xunit.extensibility.core` would be a new pin, forbidden in M0). Every placeholder is `[Fact(Skip = "placeholder: replaced by AJ-0nn")]` with an empty body; the integration placeholder uses `Fact`, and `[IntegrationFact]` is first used by AJ-023 in M1. The Specs project has zero tests in M0 (`No test is available`, exit 0), so no Specs placeholder was added. `reqnroll.json` pins Reqnroll's default outcome for a missing step (a control run without the file also failed), and Reqnroll copies it to the output directory itself.
- Deviations: `grep -rc "Skip ="` sums to 8 against 7 skipped tests because the attribute's own assignment line counts (recorded in the check rows). The `## Test counts` purpose line now says "AJ-007 and later" because this story records the first counts. `dotnet sln add` wrote CRLF into the slnx, normalised to LF.
- Gates: code-review-gatekeeper APPROVED (MINOR: the test-count row, written in this commit; a redundant `[AttributeUsage]` line, kept); security-specialist APPROVED (no key, no network, the `$schema` URL in reqnroll.json is editor metadata); spec-compliance-reviewer APPROVED (criteria 1 to 8 PASS; criteria 3 and 8 cannot both hold strictly, the `xunit` reference is the better reading).
- Velocity: estimate 2.00 h; wall-clock 16:29Z (first csharp-expert call) to 16:47Z (entry written), 18 min, commit follows; sub-agent tokens 403,031 (csharp-expert 81,326 + 93,629; gates 106,191 + 80,300 + 41,585).

### AJ-008 Gate the skeleton with build, test and format checks (REQ-001)

- Shipped: the recorded gate run on the AJ-007 tree (restore, both strict builds, test, format, the eleven TargetFramework values, the three mutation checks in scratch copies) under `## Toolchain check`, `## Test counts` and `## Version deviations and additions`. No code change.
- Decisions: the mutation checks ran in copies made with `git ls-files -co --exclude-standard` into the session scratchpad; the working tree was proven unchanged by comparing `git status --porcelain` before and after. The gate numbers were taken about three minutes before the AJ-007 commit on the same tracked content (the reviewer compared all 34 tracked build files byte for byte).
- Deviations: none from the story. Exit codes come from the executor's report and are not saved to files (future gates write them to `.exit` files beside the logs); the restore was a no-op because AJ-007 had restored already, so the audit claim rests on the cached results.
- Gates: code-review-gatekeeper APPROVED (five MINOR wording notes, applied in this commit); security-specialist APPROVED (LOW: the no-op restore, applied); spec-compliance-reviewer APPROVED (criteria 1 to 6 PASS from the raw logs; criterion 5 re-measured by the reviewer).
- Velocity: estimate 1.50 h; wall-clock 16:42Z (battery start) to 16:52Z (entry written), 10 min, commit follows; sub-agent tokens 274,838 (executor 56,487; gates 99,071 + 77,647 + 41,633).

### AJ-009 Verify licence and README, extend the ignore and attribute files (REQ-002, REQ-029)

- Shipped: `.gitignore` extended by a comment and `results/*.partial`; `.gitattributes` with `* text=auto eol=lf` as its first line and every other line kept; the working copies of `.editorconfig` and `.gitignore` renormalised to LF (content unchanged).
- Decisions: `.code-review-graph/` was already ignored at HEAD (line 499, under the existing tool-state comment), so it was not added twice; the old `* text=auto` line stays as line 2 because the story forbids removing lines, and since it names only `text`, `eol=lf` from line 1 survives (checked with `git check-attr`). README.md and LICENSE are untouched.
- Deviations: the story's criterion 3 lists `.code-review-graph/` as missing; it was present, a measurement error in the story text. Noted for later, not applied: `*.cmd` and `*.bat` would need `text eol=crlf` lines if such scripts ever appear (code-review MINOR); `results/*.raw` and `results/*.partial` do not cover subfolders of `results/`, accepted because the design writes run files directly under `results/` (security LOW).
- Gates: code-review-gatekeeper APPROVED (no finding); security-specialist APPROVED (13 secret and tool-state paths ignored, 3 controls not ignored, no `!` rule re-includes a secret file); spec-compliance-reviewer APPROVED (criteria 1 to 6 PASS).
- Velocity: estimate 1.25 h; wall-clock 16:40Z (executor start) to 16:53Z (entry written), 13 min, commit follows; sub-agent tokens 257,263 (executor 51,589; gates 79,338 + 84,220 + 42,116).

### AJ-010 Redact key values in any text output (REQ-029)

- Shipped: `src/AgentJoules.Core/Security/SecretRedactor.cs` (Register, Redact, the `nvapi-[A-Za-z0-9_-]{4,}` pattern as a generated regex, longest-first registered values, the six-character reveal with the short-value rule), `src/AgentJoules.Cli/Output/RedactingTextWriter.cs` (line-buffered TextWriter that redacts before anything reaches the inner writer; Flush and Dispose write the remainder), `tests/AgentJoules.UnitTests/RedactionTests.cs` (19 facts, trait Spec=REQ-029), `tests/AgentJoules.Specs/Features/Configuration/AJ-010-redact-key-values.feature` (the story's seven scenarios verbatim) and `tests/AgentJoules.Specs/Steps/ConfigurationSteps.cs`. Console wiring is AJ-034 (M1).
- Decisions: the regex runs before the registered values (a registered value such as `-` would otherwise split the `nvapi-` prefix and leak the suffix; found by the code review, fixed and guarded by a test); registered values are also registered trimmed, so a trailing CR from a CRLF file cannot leak the bare key; the writer owns and disposes its inner writer, passes FormatProvider and NewLine through, and is documented as not thread-safe (AJ-034 wraps it with TextWriter.Synchronized, one instance per stream); the story's rule of six revealed characters is kept (design 3.2 says `****` for non-nvapi secrets and is refined by the story's criterion 3, which is the acceptance contract).
- Deviations: one step pattern in ConfigurationSteps.cs carries `^...$` anchors because Reqnroll parsed it as a Cucumber expression otherwise; the other bindings are unanchored (known inconsistency). Noted for AJ-034, not applied: a span-merging implementation would also cover a registered value that contains `nvapi-` followed by a character outside the key charset (real NVIDIA keys use only the charset, so it cannot trigger today); a registered value containing `*` breaks idempotence of the mask (output only grows, nothing leaks); the security reviewer's proposal to reveal nothing for values shorter than 16 characters and to lock the writer, both declined for M0 because they change the story's rule or are handled by the M1 wiring.
- Gates: code-review-gatekeeper round 1 CHANGES_REQUIRED (MAJOR: regex ordering leak; MAJOR: untested boundaries; MINOR: Dispose try/finally, FormatProvider and NewLine, trimmed registration, deterministic tie-break, Register inside the Given step, StartsWith guard), all applied; round 2 APPROVED (both MAJORs verified fixed and tested; MINORs recorded above); security-specialist APPROVED (no partial mask reaches the inner writer, regex linear, secrets held privately, no literal key; three LOW notes recorded above); spec-compliance-reviewer APPROVED (criteria 1 to 6 PASS, criterion 7 DEFERRED to AJ-034 by the story; the feature file equals the story's Gherkin byte for byte).
- Velocity: estimate 1.75 h; wall-clock 16:48Z (red phase start) to 17:06Z (entry written), 18 min, commit follows; sub-agent tokens 661,237 (csharp-expert 85,506 + 101,002 + 114,973; gates 116,390 + 98,701 + 102,972 + 41,693).

### AJ-011 Run the secret scan and record it (REQ-029)

- Shipped: the `## Secret scan` table (gitleaks absent, the `git grep fallback` on tracked, untracked and history, the quiet exact-value greps for the three key names in the tree and across history, the build-log greps, the planted-key and empty-variable negatives, the re-runs at commit 71a1cf4) and the scan-of-record paragraph with the re-run rule and the fallback's known limits.
- Decisions: gitleaks is not installed, so the fallback is the scan of record; exit 1 of `git grep` is the clean result; key values are only ever passed to `git grep -qF` and only their names are written; the re-run rule is broadened from "commits that touch docs/ or config/" to every commit, run on the working tree before the commit; future pattern re-runs use `-l` so a hit would print a file name, never the matched text.
- Deviations: the first scan (16:48Z at 8635b32) was three docs commits behind by the time the record was written; the gates caught it and the 17:07Z and 17:09Z re-runs at 71a1cf4 are the record. Known limits of the fallback against gitleaks are listed in the paragraph.
- Gates: round 1 code-review-gatekeeper CHANGES_REQUIRED (MAJOR: stale scan of record; MINOR: empty-variable command, wording), security-specialist CHANGES_REQUIRED (MAJOR: same; MINOR: exact-value greps across history, rule scope, `-l`), spec-compliance-reviewer CHANGES_REQUIRED (AC5 stale record); all applied; round 2 all three APPROVED (AC1 to AC5 PASS, AC6 N/A; reviewers re-ran every grep themselves with a positive control).
- Velocity: estimate 1.25 h; wall-clock 16:48Z (scan battery) to 17:11Z (entry written), 23 min, commit follows; sub-agent tokens 421,519 (executor 47,631; gates round 1 81,824 + 81,075 + 30,128; round 2 75,078 + 76,981 + 28,802).
