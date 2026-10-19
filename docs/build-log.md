# Agent Joules build log

Purpose: the single record of toolchain facts, version deviations, scan results, test counts, per-ID filter results, probe and WSL2 notes, user actions, requirements that cannot be met and base commits (REQ-002, REQ-029, builder standing order 6). ASCII only, LF only; never a key value, a hostname or an IP. Every number carries one label: measured, user-reported, inferred or assumed.

## Toolchain check

Commands, exit codes, UTC dates and labels of the SDK, Docker, WSL2, Ollama, GPU driver and git checks (AJ-002), plus the hygiene, property and grep checks that later M0 stories run.

### AJ-001 checks (2026-10-18T16:17:53Z, measured)

| Check | Command | Exit code | Result |
|---|---|---|---|
| nine headings once each | for each of the nine headings: `grep -cxF "<heading>" docs/build-log.md`; a count other than 1 prints `missing heading: <heading>` and the loop exits 1 | 0 | every count is 1 |
| ASCII only | `grep -nP "[^\x00-\x7F]" docs/build-log.md` | 1 | no match |
| LF only, story command | `grep -c $'\r' docs/build-log.md` | prints 0 | printed 0; the M0 peer reviewer measured that GNU grep 3.0 does count CR bytes when bash expands `$'\r'`, so a 0 on a CRLF file came from the executor's POSIX-sh shell not expanding `$'\r'`, not from grep; the byte count on the next row is the binding check |
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
| seven folders exist | `for d in src tests docs content results tasks config; do test -d "$d" \|\| echo "missing $d"; done` | 0 | printed nothing |
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
| post-exit: xUnit1004 at info severity | `dotnet format --verify-no-changes --severity info` before and after the `.editorconfig` section that sets `dotnet_diagnostic.xUnit1004.severity = none` for the seven placeholder files | 2 before; 0 after | seven informational hits on the placeholders before; none after; a scratch copy with a new skipped test still reports xUnit1004 for that file (2026-10-18T18:01Z, measured) |

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

### AJ-012 key presence and SSH hygiene checks (2026-10-18T16:48Z, measured)

| Check | Command | Exit code | Result |
|---|---|---|---|
| keys present, non-printing | `[ -n "${NVIDIA_API_KEY:-}" ] && echo set \|\| echo "missing: NVIDIA_API_KEY"` and the same for NEBIUS_API_KEY and NGC_API_KEY; run with shell tracing off (`set +x`), because `set -x` would echo the value to stderr | 0 | `set` three times; no value, length or suffix printed |
| NVIDIA prefix | `case "${NVIDIA_API_KEY:-}" in nvapi-*) echo prefix-ok;; esac` | 0 | `prefix-ok`; only the public prefix is tested; run with shell tracing off |
| missing-key rule | a missing or empty variable would be logged as exactly `missing: <NAME>` (matched by `grep -nE "^missing: [A-Z_]+$"`), M2 marked BLOCKED for that tier, and the user asked to open a new terminal, never to paste a key | n/a | not triggered: no line matches because all three are set |
| no private key tracked | `git ls-files` filtered with `grep -E "id_(rsa\|ed25519)\|\.pem$"` | 1 | no output |
| no private key in the tree | `find . -name "id_ed25519*" -not -path "./.git/*"` and the same for `id_rsa*` and `*.pem` outside bin and obj | 0 | 0 files each |
| build log free of key pattern | `git grep -nE "nvapi-[A-Za-z0-9_-]{8,}" -- docs/build-log.md` | 1 | no output; re-checked at 17:16Z after the user-action table was written |
| build still green with the AJ-012 docs changes | the M0 exit battery (executor, 17:13Z): `dotnet build -warnaserror -tl:off --no-incremental`, `dotnet test`, `dotnet format --verify-no-changes` | 0; 0; 0 | `0 Warning(s)`, `0 Error(s)`; 26 passed, 0 failed, 7 skipped; no diff |

## Version deviations and additions

Every package whose used version differs from the pins of design section 2.4 (package, pinned version, used version, reason) and every package added beyond the design list (REQ-002).

Decision (2026-10-18, AJ-005): stay on the prompt pins. The coordinator versions Microsoft.Extensions.AI 10.11.0, Microsoft.Agents.AI 1.25.0 and OpenTelemetry 1.20.0 are rejected (design C-01, ADR-20 row 1, open point O-01). `dotnet restore` exit 0 on all 22 pins (measured 16:28Z and 16:36Z); no NuGet audit warning; no deviation recorded. M0 adds no package beyond design section 2.4; M1 story AJ-033 adds the Options packages and records them here as additions.

| Package | Pinned version | Used version | Reason |
|---|---|---|---|
| none | | | no deviation in M0 |

AJ-008 (2026-10-18T16:42Z, measured): `dotnet restore` exit 0 with no missing version and no NU1901 to NU1904 audit warning, so no deviation row and no NoWarn entry were needed; the restore was a no-op and the 11 project.nuget.cache files hold no NU19 code.

Post-exit check (2026-10-18T17:59Z, measured): `dotnet restore --force` prints no NU1901 to NU1904 line; `dotnet list package --vulnerable --include-transitive` lists no vulnerable package; `dotnet list package --deprecated` flags only `xunit 2.9.3` as Legacy (alternative xunit.v3), which is the deliberate xUnit v2 plus VSTest choice of ADR-03 and ADR-04 and raises no restore warning; `dotnet list package --outdated` lists exactly the newer coordinator versions the design rejects (C-01: MEAI 10.11.0, Agent Framework 1.25.0, OpenTelemetry 1.20.0; C-03: JsonSchema.Net 9.x) plus Microsoft.Extensions.Hosting and Http 10.0.13 and xunit.runner.visualstudio 4.0.1, all informational, no pin changed.

Post-exit review (2026-10-18, from the user's code-review max run, all measured on scratch copies): `nuget.config` added at the root (single source nuget.org, `<clear/>`, package source mapping for every package) and, after the security gate, NU1900 (vulnerability feed unreachable) kept as an error unless the developer opts in with `-p:AgentJoulesOfflineAudit=true` through a conditional `WarningsNotAsErrors`; NU1507 is not downgraded because the single mapped source makes it impossible; the audit-source list is cleared in nuget.config so a user-level audit source cannot fail restore with NU1905; a vulnerable pin still fails with NU1903; NU1901 to NU1904 stay errors. `InvariantGlobalization` moved from an MSBuild property to two `RuntimeHostConfigurationOption` items (System.Globalization.Invariant, System.Globalization.PredefinedCulturesOnly) in src/Directory.Build.props and design 2.3 is amended: the property was forwarded to the analyzers and silenced CA1304 to CA1311 (a culture-sensitive `1.5.ToString("F1")` and `a.StartsWith(b)` built clean under the property and fail with CA1305 and CA1310 without it); the Cli runtimeconfig still carries `System.Globalization.Invariant: true`. No package or version changed.

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
| pre-commit re-run before the AJ-012 commit, at HEAD e48ba60 | `git grep -lE "nvapi-[A-Za-z0-9_-]{8,}"`; the same with `--untracked`; the same over `$(git rev-list --all)` (41 commits); `git grep -qF -- "$NAME"` for NVIDIA_API_KEY, NEBIUS_API_KEY and NGC_API_KEY, each on tracked files, with `--untracked`, and over all history | 1 for every run (twelve runs) | 2026-10-18T17:15Z | clean; no value printed |
| pre-commit re-run before the AJ-010 follow-up commit fcd3ec5, on the working tree at HEAD 506eb60 | `git grep --untracked -lE "nvapi-[A-Za-z0-9_-]{8,}"`; `git grep -qF --untracked -- "$NAME"` for NVIDIA_API_KEY, NEBIUS_API_KEY and NGC_API_KEY | 1; 1; 1; 1 | 2026-10-18T17:35Z | clean; no value printed |
| pre-commit re-run before the AJ-010 docs commit, at HEAD fcd3ec5 | `git grep -lE "nvapi-[A-Za-z0-9_-]{8,}"`; the same with `--untracked`; the same over `$(git rev-list --all)` (43 commits); `git grep -qF -- "$NAME"` for the three key names on tracked files, with `--untracked`, and over all history | 1 for every run (twelve runs) | 2026-10-18T17:39Z | clean; no value printed; re-run again immediately before the commit |
| re-run immediately before the AJ-010 docs commit f122b95 (working tree at HEAD fcd3ec5) | `git grep --untracked -lE "nvapi-[A-Za-z0-9_-]{8,}"`; `git grep -qF --untracked -- "$NAME"` for the three key names | 1; 1; 1; 1 | 2026-10-18T17:40Z | clean; no value printed |
| M0 exit re-run before the exit commit, at the merge commit 73a2a5d on main | `git grep -lE "nvapi-[A-Za-z0-9_-]{8,}"`; the same with `--untracked`; the same over `$(git rev-list --all)` (45 commits); `git grep -qF -- "$NAME"` for the three key names on tracked files, with `--untracked`, and over all history | 1 for every run (twelve runs) | 2026-10-18T17:43Z | clean; no value printed; the exit scan of record for M0 |
| pre-commit re-run before the post-exit hygiene commit, at HEAD e8563c6 on main | `git grep --untracked -lE "nvapi-[A-Za-z0-9_-]{8,}"`; `git grep -qF --untracked -- "$NAME"` for the three key names | 1; 1; 1; 1 | 2026-10-18T18:02Z | clean; no value printed |
| pre-commit re-run before the post-exit review commits, on the working tree of milestone/M0-review at HEAD c111848, stdin form | `git grep --untracked -lE "nvapi-[A-Za-z0-9_-]{8,}"`; `printf '%s\n' "${!NAME}" \| git grep -qF --untracked -f -` and the same over `$(git rev-list --all)` for the three key names | 1 for every run (seven runs) | 2026-10-18T19:52Z | clean; no value printed or passed as an argument |
| pre-commit re-run before the review2 commits, on the working tree of milestone/M0-review2 at HEAD 80554b6, stdin form | `git grep --untracked -lE "nvapi-[A-Za-z0-9_-]{8,}"`; `printf '%s\n' "${!NAME}" \| git grep -qF --untracked -f -` and the same over `$(git rev-list --all)` for the three key names | 1 for every run (seven runs) | 2026-10-19T07:59Z | clean; no value printed or passed as an argument |

Scan of record for AJ-011: `git grep fallback` (gitleaks not installed), exit code 1 (no match), verdict clean, measured first at commit 8635b32 (2026-10-18T16:48:49Z) and re-run at commit 71a1cf4 (2026-10-18T17:07:24Z) after the AJ-008, AJ-009 and AJ-010 commits that touched docs/ and tests/. Rule (broadened on the security reviewer's finding): the scan is re-run on the working tree before each commit until M7 (tracked, untracked and history greps, so the content about to be committed is covered), not only before commits that touch docs/ or config/, because test fixtures under tests/ and src/ are the likelier leak; the latest exit code is the one that counts; each re-run is appended as the next row of the table above, the M0 exit re-run included; future pattern re-runs use `-l` instead of `-n` so a hit prints only the file name. Known limits of the fallback against gitleaks: it cannot catch keys that are not in the current environment, credentials without the `nvapi-` prefix and without a registered value, encoded or split values, dangling objects, or high-entropy strings of unknown shape. Amended 2026-10-18 (post-exit review): the exact-value greps pass the value on stdin, `printf '%s\n' "${!NAME}" | git grep -qF -f -` (also with `--untracked` and over `$(git rev-list --all)`), never as a command-line argument, because an argument is visible in process listings and git traces while git runs; an empty value makes git grep exit 128, so the empty-variable guard stays.

## Test counts

Passed, failed and skipped counts of `dotnet test` per recorded run (AJ-007 and later).

| Run | UTC date | Command | Exit code | Passed | Failed | Skipped | Note |
|---|---|---|---|---|---|---|---|
| AJ-007 skeleton | 2026-10-18T16:41Z | `dotnet test` | 0 | 0 | 0 | 7 | UnitTests 6, IntegrationTests 1, Specs 0 tests (`No test is available`, exit 0); measured |
| AJ-008 gate | 2026-10-18T16:43Z | `dotnet test` | 0 | 0 | 0 | 7 | UnitTests 6, IntegrationTests 1, Specs 0 tests; placeholder count by grep 7; measured |
| AJ-010 redaction | 2026-10-18T17:02Z | `dotnet test` | 0 | 26 | 0 | 7 | UnitTests 19 passed 6 skipped, Specs 7 passed, IntegrationTests 1 skipped; measured after the review fix round |
| AJ-010 follow-up fcd3ec5 | 2026-10-18T17:31Z | `dotnet test` | 0 | 28 | 0 | 7 | UnitTests 21 passed 6 skipped, Specs 7 passed, IntegrationTests 1 skipped; measured before the follow-up commit and again by peer review round 2 after it |
| post-exit review branch | 2026-10-18T19:52Z | `dotnet test` | 0 | 38 | 0 | 7 | UnitTests 31 passed 6 skipped, Specs 7 passed, IntegrationTests 1 skipped; measured before the gate round |
| post-exit review branch, after the gate round | 2026-10-18T20:04Z | `dotnet test` | 0 | 42 | 0 | 7 | UnitTests 35 passed 6 skipped, Specs 7 passed, IntegrationTests 1 skipped; measured |
| review2 branch (final review findings) | 2026-10-19T08:40Z | `dotnet test` | 0 | 68 | 0 | 7 | UnitTests 61 passed 6 skipped, Specs 7 passed, IntegrationTests 1 skipped; measured |

## Per-ID filter results

One row per `dotnet test --filter "Spec=<ID>"` run: ID, passed, failed, skipped, UTC date.

| ID | UTC date | Command | Passed | Failed | Skipped | Note |
|---|---|---|---|---|---|---|
| REQ-029 | 2026-10-18T17:02Z | `dotnet test --filter "Spec=REQ-029" --logger "console;verbosity=normal"` | 19 | 0 | 0 | AJ-010 RedactionTests, 17:02Z after the review fix round (12 at 16:54Z); measured |
| REQ-029 | 2026-10-18T17:31Z | `dotnet test --filter "Spec=REQ-029" --logger "console;verbosity=normal"` | 21 | 0 | 0 | AJ-010 follow-up fcd3ec5 (full mask, leaveOpen, two dispose tests); measured |
| REQ-029 | 2026-10-18T19:52Z | `dotnet test --filter "Spec=REQ-029" --logger "console;verbosity=normal"` | 31 | 0 | 0 | post-exit review branch before the gate round; measured |
| REQ-029 | 2026-10-18T20:04Z | `dotnet test --filter "Spec=REQ-029" --logger "console;verbosity=normal"` | 35 | 0 | 0 | post-exit review branch after the gate round; measured |
| REQ-029 | 2026-10-19T08:40Z | `dotnet test --filter "Spec=REQ-029" --logger "console;verbosity=normal"` | 61 | 0 | 0 | review2 branch: lone-surrogate, whole-value plus per-line, line-separator and whitespace cases; measured; two theories cover all seven line terminators |

## Probe and WSL2 notes

Results of `probe` runs and of the Linux and WSL2 execution checks (M1 and later).

## User actions

Prompt section 12 steps 1 to 13 with owner, status, gating milestone and evidence (AJ-012).

Pre-M4 tool action, not one of the 13 prompt steps (added by AJ-002 on 2026-10-18, measured): Ollama is not installed on this laptop (`ollama --version` exit 127). Owner: user. Status: open. Gates: M4 local runtime (REQ-020; AJ-105, AJ-107; AJ-106 if NIM fails). Action: install Ollama and pull the Nemotron tag that REQ-020 names before M4 starts on 2026-10-24. AJ-012 adds the 13-step table after this note.

Prompt section 12 steps, recorded 2026-10-18 (status: done, open, unknown, after the build, or on builder request; evidence: user-reported, measured, not reported, or not yet for steps that come after the build or on builder request). Steps 5 and 6 gate M2 (`AJ run --tier nvidia\|nebius\|all`, the judge and the `validate --config` verification calls); step 4 comes before the 45-minute Nebius onboarding box (story text: M2; plan section 3.6: AJ-101 on 2026-10-24); step 7 gates the M4 NIM attempt; steps 10 and 11 gate M5; steps 12 and 13 happen on builder request in M5; steps 8 and 9 belong to M7. The builder never creates an account, a key or a cloud resource and never asks for a key value.

| Step | Action | Owner | Status | Gates | Evidence |
|---|---|---|---|---|---|
| 1 | Devpost account (sign in or create) | user | unknown | M6 AJ-138 (rules record), M7 submission | not reported |
| 2 | Join the hackathon on the Devpost page; record the join date and handle in docs/devpost-checklist.md | user | unknown | M6 AJ-139 (Registration placeholders), M7 | not reported |
| 3 | Read the Rules tab; paste its text to the builder if the fetch returns 403 | user | unknown | M6 AJ-138 | not reported |
| 4 | Request the USD 25 Token Factory credits and join the Builders Program | user | unknown | the 45-minute Nebius onboarding box (M2 per the story; AJ-101 in M4 per plan 3.6) | not reported |
| 5 | Nebius Token Factory API key stored as NEBIUS_API_KEY | user | done | M2: `AJ run --tier nebius\|all`, judge, `validate --config` | user-reported 2026-10-18; presence measured 16:48Z (`set`) |
| 6 | build.nvidia.com API key stored as NVIDIA_API_KEY | user | done | M2: `AJ run --tier nvidia\|all`, judge, `validate --config` | user-reported 2026-10-18; presence and `nvapi-` prefix measured 16:48Z |
| 7 | NGC personal key stored as NGC_API_KEY | user | done | M4 NIM-local attempt (REQ-019) | user-reported 2026-10-18; presence measured 16:48Z (`set`) |
| 8 | Submit on Devpost before 2026-10-30 10:00 PDT, fields from docs/devpost-checklist.md | user | after the build | M7 | not yet |
| 9 | Keep the repository public and unchanged on the submitted commit until results are announced (judging listed 2026-12-01 to 2026-12-15), or tag it v0.1.0 | user | after the build | M7 | not yet |
| 10 | Nebius AI Cloud account at console.nebius.com, project in eu-north1, card, USD 25 minimum first payment | user | open | M5 cloudgpu tier (AJ-120) | user-reported open 2026-10-18 (kickoff line: no cloud account yet) |
| 11 | ed25519 SSH key pair, private key kept outside the repository | user | unknown | M5 | not reported; hygiene measured 16:48Z: no id_ed25519*, id_rsa* or *.pem tracked or present |
| 12 | Create the VM when the builder asks (platform gpu-l40s-a, preset 1gpu-16vcpu-64gb with the prompt's fallbacks, eu-north1, image ubuntu24.04-cuda12, disk >= 150 GB, public IP, preemptible off), read the hourly price, set CLOUDGPU_SSH_HOST and CLOUDGPU_SSH_USER | user | on builder request | M5 (AJ-125) | not yet |
| 13 | Delete the VM and its boot disk after "results copied"; report deletion time and final cost (cap USD 30, stop at USD 25) | user | on builder request | M5 (AJ-128) | not yet |

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
- Deviations: the story's LF check `grep -c $'\r'` printed 0 even on CRLF files when run through the executor's POSIX-sh shell, which does not expand `$'\r'` (grep itself counts CR when bash expands it, measured by the M0 peer reviewer), so a byte count is logged beside it as the binding check. Design section 2.3 claims an LF working tree; `.editorconfig` and `.gitignore` are CRLF in the working tree (measured), to be fixed by AJ-009.
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
- Deviations: `grep -rc "Skip ="` sums to 8 against 7 skipped tests because the attribute's own assignment line counts (recorded in the check rows). The `## Test counts` purpose line now says "AJ-007 and later" because this story records the first counts. `dotnet sln add` wrote CRLF into the slnx, normalised to LF. Design 2.4 lists Microsoft.Extensions.TimeProvider.Testing under TestSupport; the story's criterion 4 leaves it out, so TestSupport references only `xunit` until an M1 fixture needs it.
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
- Deviations: the story's criterion 3 lists `.code-review-graph/` as missing; it was present, a measurement error in the story text. Noted for later, not applied: `*.cmd` and `*.bat` would need `text eol=crlf` lines if such scripts ever appear (code-review MINOR); `results/*.raw` and `results/*.partial` do not cover subfolders of `results/`, accepted because the design writes run files directly under `results/` (security LOW). The commit subject is `AJ-009 REQ-002 REQ-029:` (story ID then every covered ID, standing order 5) while the story's DoD box and exit criterion quote `AJ-009 REQ-002:` literally; the pattern check passes, the literal is not matched.
- Gates: code-review-gatekeeper APPROVED (no finding); security-specialist APPROVED (13 secret and tool-state paths ignored, 3 controls not ignored, no `!` rule re-includes a secret file); spec-compliance-reviewer APPROVED (criteria 1 to 6 PASS).
- Velocity: estimate 1.25 h; wall-clock 16:40Z (executor start) to 16:53Z (entry written), 13 min, commit follows; sub-agent tokens 257,263 (executor 51,589; gates 79,338 + 84,220 + 42,116).

### AJ-010 Redact key values in any text output (REQ-029)

- Shipped: `src/AgentJoules.Core/Security/SecretRedactor.cs` (Register, Redact, the `nvapi-[A-Za-z0-9_-]{4,}` pattern as a generated regex, longest-first registered values, the six-character reveal with the short-value rule, superseded by the follow-up entry below), `src/AgentJoules.Cli/Output/RedactingTextWriter.cs` (line-buffered TextWriter that redacts before anything reaches the inner writer; Flush and Dispose write the remainder), `tests/AgentJoules.UnitTests/RedactionTests.cs` (19 facts at that commit, 21 after the follow-up, trait Spec=REQ-029), `tests/AgentJoules.Specs/Features/Configuration/AJ-010-redact-key-values.feature` (the story's seven scenarios verbatim) and `tests/AgentJoules.Specs/Steps/ConfigurationSteps.cs`. Console wiring is AJ-034 (M1).
- Decisions: the regex runs before the registered values (a registered value such as `-` would otherwise split the `nvapi-` prefix and leak the suffix; found by the code review, fixed and guarded by a test); registered values are also registered trimmed, so a trailing CR from a CRLF file cannot leak the bare key; the writer owns and disposes its inner writer, passes FormatProvider and NewLine through, and is documented as not thread-safe (AJ-034 wraps it with TextWriter.Synchronized, one instance per stream); the first version of this commit kept the story's six-character reveal; the M0 peer review cited design-review N-09 (every other secret becomes `****`), so design 3.2 is the authority, the story text was superseded and the AJ-010 follow-up commit masks every registered value as `****` (entry below).
- Deviations: one step pattern in ConfigurationSteps.cs carried `^...$` anchors because Reqnroll parsed it as a Cucumber expression otherwise; the follow-up commit replaced that binding, so no binding is anchored now. Noted for AJ-034, not applied: a span-merging implementation would also cover a registered value that contains `nvapi-` followed by a character outside the key charset (real NVIDIA keys use only the charset, so it cannot trigger today); a registered value containing `*` breaks idempotence of the mask (output only grows, nothing leaks); the security reviewer's proposal to reveal nothing for values shorter than 16 characters and to lock the writer, both declined for M0 because they change the story's rule or are handled by the M1 wiring.
- Gates: code-review-gatekeeper round 1 CHANGES_REQUIRED (MAJOR: regex ordering leak; MAJOR: untested boundaries; MINOR: Dispose try/finally, FormatProvider and NewLine, trimmed registration, deterministic tie-break, Register inside the Given step, StartsWith guard), all applied; round 2 APPROVED (both MAJORs verified fixed and tested; MINORs recorded above); security-specialist APPROVED (no partial mask reaches the inner writer, regex linear, secrets held privately, no literal key; three LOW notes recorded above); spec-compliance-reviewer APPROVED (criteria 1 to 6 PASS, criterion 7 DEFERRED to AJ-034 by the story; the feature file equals the story's Gherkin byte for byte).
- Velocity: estimate 1.75 h; wall-clock 16:48Z (red phase start) to 17:06Z (entry written), 18 min, commit follows; sub-agent tokens 661,237 (csharp-expert 85,506 + 101,002 + 114,973; gates 116,390 + 98,701 + 102,972 + 41,693).

### AJ-011 Run the secret scan and record it (REQ-029)

- Shipped: the `## Secret scan` table (gitleaks absent, the `git grep fallback` on tracked, untracked and history, the quiet exact-value greps for the three key names in the tree and across history, the build-log greps, the planted-key and empty-variable negatives, the re-runs at commit 71a1cf4) and the scan-of-record paragraph with the re-run rule and the fallback's known limits.
- Decisions: gitleaks is not installed, so the fallback is the scan of record; exit 1 of `git grep` is the clean result; key values are only ever passed to `git grep -qF` and only their names are written; the re-run rule is broadened from "commits that touch docs/ or config/" to every commit, run on the working tree before the commit; future pattern re-runs use `-l` so a hit would print a file name, never the matched text.
- Deviations: the first scan (16:48Z at 8635b32) was three docs commits behind by the time the record was written; the gates caught it and the 17:07Z and 17:09Z re-runs at 71a1cf4 are the record. Known limits of the fallback against gitleaks are listed in the paragraph.
- Gates: round 1 code-review-gatekeeper CHANGES_REQUIRED (MAJOR: stale scan of record; MINOR: empty-variable command, wording), security-specialist CHANGES_REQUIRED (MAJOR: same; MINOR: exact-value greps across history, rule scope, `-l`), spec-compliance-reviewer CHANGES_REQUIRED (AC5 stale record); all applied; round 2 all three APPROVED (AC1 to AC5 PASS, AC6 N/A; reviewers re-ran every grep themselves with a positive control).
- Velocity: estimate 1.25 h; wall-clock 16:48Z (scan battery) to 17:11Z (entry written), 23 min, commit follows; sub-agent tokens 421,519 (executor 47,631; gates round 1 81,824 + 81,075 + 30,128; round 2 75,078 + 76,981 + 28,802).

### AJ-012 Record the user actions that gate M2 and M5 (REQ-029)

- Shipped: the `### AJ-012` check table under `## Toolchain check` (non-printing presence tests, the `nvapi-` prefix case, the `missing: <NAME>` rule, SSH key hygiene, the build-log key grep, the build result) and, under `## User actions`, the 13-step table from prompt section 12 with owner, status, gates and evidence, plus the Ollama pre-M4 tool action recorded by AJ-002.
- Decisions: steps 5, 6 and 7 are done (user-reported, presence measured, no value or length ever printed); step 10 is open and gates M5 (kickoff line: no cloud account yet); step 11 is unknown and gates M5; steps 1 to 4 are unknown; steps 8 and 9 come after the build (M7); steps 12 and 13 happen on builder request (M5). The story text puts the 45-minute Nebius onboarding box in M2 while plan 3.6 puts it at AJ-101 in M4; the table states both and the plan is the calendar of record.
- Deviations: none from the criteria. Two Markdown rendering defects (unescaped pipes inside code spans) were found by the gates and fixed, one of them in the AJ-003 folder-check row from an earlier commit.
- Gates: round 1 code-review-gatekeeper CHANGES_REQUIRED (MAJOR: unescaped pipes; MAJOR: no pre-commit scan row; MINOR: times, evidence vocabulary, row 9 wording, build result), security-specialist APPROVED (LOW: the recorded presence command should print `missing: <NAME>`, applied; INFO: shell tracing off, applied), spec-compliance-reviewer CHANGES_REQUIRED (C2 and C3 split rows); all applied; round 2 code-review-gatekeeper APPROVED and spec-compliance-reviewer APPROVED (C1 to C6 PASS).
- Velocity: estimate 1.00 h; wall-clock 16:48Z (presence test) to 17:18Z (entry written), 30 min, commit follows; sub-agent tokens 340,207 (gates round 1 100,176 + 83,526 + 35,120; round 2 79,279 + 42,106; the presence and hygiene commands ran inline).

### AJ-010 follow-up: M0 peer review round 1 (REQ-029)

- Shipped (commit fcd3ec5): `SecretRedactor` masks every registered value as `****` (`MaskOf` and the six-character reveal removed; regex-first and longest-first kept); `RedactingTextWriter` gains `leaveOpen` (default false) so AJ-034 can wrap Console.Out and Console.Error without closing them, with the try/finally kept; `RedactionTests` updated (21 facts on Spec=REQ-029, two dispose tests added); the feature file's first scenario and the short-value Then line follow the superseded story text and the file equals the story's Gherkin byte for byte; the step bindings rebound and the anchored pattern removed; the story's criterion 3, two scenario lines and test mapping updated with the N-09 citation and dated; peer-review MINORs 2 to 4 applied (deviation notes for the AJ-009 subject and TestSupport's missing TimeProvider.Testing, the corrected CR explanation, the AJ-011 rule note); the ten M0 exit-criteria boxes ticked.
- Decisions: design-review N-09 and design 3.2 are the authority over the story's six-character rule; the story text was superseded with a dated citation, not silently rewritten. The M2 and M3 story lines that still say "beyond the first six" (AJ-052 criterion 3 and two scenario lines, one M3 scenario line) are corrected in the M0 exit commit for the same reason.
- Deviations: this entry, the two count rows and the scan rows for fcd3ec5 were written in a separate docs commit after fcd3ec5 (peer review round 2 MAJOR: the follow-up commit carried no entry and no scan row); the scan before fcd3ec5 ran on the working tree and is recorded above. Resumed sub-agents report cumulative usage, so the AJ-010 entry's 301,481 tokens summed three cumulative reports and overstates by 186,508; the AJ-007 entry overstates by 81,326 for the same reason; the M0 exit block carries corrected totals.
- Gates: code-review-gatekeeper on the follow-up diff APPROVED (MINOR, recorded for AJ-034: star-only registered values are not idempotent; a registered value containing `nvapi-` mid-string can leak around the regex; helper naming and a spy writer for Flush); peer-review-critic round 2 CHANGES_REQUIRED on this missing record only (MINOR for AJ-034: a Flush in the middle of a key leaks both halves, so AJ-034 must never flush a partial line or Flush must hold back text after the last newline; a trailing CR in a registered value is dropped cosmetically); round 3 follows this commit.
- Velocity: estimate 0 h (rework inside AJ-010); wall-clock 17:19Z (peer review round 1 start) to 17:40Z (entry written); sub-agent tokens for the follow-up: csharp-expert cumulative 135,787 for all AJ-010 work (replaces the counted 114,973), gate 112,213; the peer-review rounds are counted at the milestone exit.

## Gate M0

Re-planning rule of plan section 3.5, applied at the M0 exit on 2026-10-18 (all values derived from the story estimates and the plan tables):

| Quantity | Value | Source |
|---|---|---|
| delivered hours (DoD boxes ticked and committed) | 16.00 (MUST 15.00, other 1.00: AJ-012, cut step C1 material) | story file estimate table |
| d, calendar days since 2026-10-18 | 1 | the session ran on day 1 |
| v, delivered MUST hours per day; v_w over the M0 window; v* = min(v, v_w) | 15.00; 15.00; 15.00 | derived |
| T, days to 2026-10-29 17:00 UTC from the exit (17:45Z) | 10.97 | derived |
| R_must, R_all remaining | 355.65 h; 423.40 h | plan 3.5 row M0 exit |
| R_all / v*; R_must / v* | 28.2 days; 23.7 days | derived; both above T, so rule 5 would fire |
| required on remaining (plan 3.5) | 35.6 MUST h per day; 42.3 all h per day | plan 3.5 row M0 exit |
| throughput reading | 16.00 estimated hours in 96 wall-clock minutes (16:14Z to 17:50Z), about 10 estimated hours per wall-clock hour; the required 42.3 h per day needs about 4.2 wall-clock hours per day at this rate | measured wall-clock, derived rate |

Decision: no cut at M0. Plan 3.5 rule 7 makes the M0 gate a sanity reading only (setup-dominated); the first real decision is the M1 gate on 2026-10-21 16:00 UTC with v_w from M1. If the formula were applied literally, Level 1 steps C1 to C9 and Level 2 steps D1 to D3 would fire in that order (C1 lists AJ-012, already delivered, so C1 would save 5.25 h, not 6.25 h) and Level 3 would be proposed to the user. The user confirms before the M1 session that no cut is applied before the M1 gate.

## M0 exit

- Merge commit: 73a2a5dc226232b276549d0efc354425fdff9027 (`git merge --no-ff milestone/M0` on main, 2026-10-18T17:42:38Z). Base commit aafaba934b50db74947090086bc32fb2441c2380. Fourteen story commits plus the merge; nothing pushed.
- Stories delivered: AJ-001 to AJ-012, every definition-of-done box ticked; AJ-010 carries two follow-up commits from the M0 peer review (fcd3ec5: full mask per design 3.2 and N-09, leaveOpen; f122b95: its build-log record). Stories blocked: none. Unmet item: AJ-002 acceptance criterion 4 (Ollama not installed; user action; gates the M4 local runtime), recorded under `## Requirements that cannot be met`.
- Exit criteria (executor battery 17:12Z to 17:13Z at e48ba60, re-run by the peer reviewer at f122b95; all measured): `dotnet --version` 10.0.401, exit 0, toolchain facts under `## Toolchain check`; `dotnet build -warnaserror -tl:off --no-incremental` exit 0 with `0 Warning(s)` and `0 Error(s)` (also the literal REQ-001 command); `dotnet test` exit 0 with 0 failed (26 passed and 7 skipped at e48ba60, 28 passed and 7 skipped at f122b95) and `--list-tests` lists the six REQ-023 class names once each; `dotnet format --verify-no-changes` exit 0; the csproj Version grep exit 1 and 22 PackageVersion rows; `git check-ignore` prints results/x.raw, a.pem, id_ed25519, id_rsa and results/x.partial; `dotnet test --filter "Spec=REQ-029"` 19 passed at e48ba60 and 21 at f122b95, 0 failed; the secret scan recorded under `## Secret scan` (git grep fallback, exit 1, latest row above); `## User actions` holds the 13 rows; `git log aafaba9..HEAD --format=%s | grep -vcE "^AJ-[0-9]{3}( (REQ|EDGE)-[0-9]{3})*:"` printed 0 on the branch (14 commits) and `AJ-008 REQ-001:`, `AJ-009 REQ-002 REQ-029:` and `AJ-012 REQ-029:` exist.
- Milestone gates: spec-compliance-reviewer full pass REQ-001, REQ-002 and REQ-029 all PASS, APPROVED; peer-review-critic round 1 CHANGES_REQUIRED (MAJOR: six-character reveal against design 3.2 and design-review N-09; MINOR: two undeclared deviations, the CR-check explanation, inner-writer disposal), round 2 CHANGES_REQUIRED (MAJOR: the follow-up commit had no build-log record), round 3 APPROVED (MINOR: the unrecorded re-run, now the row above; the partial 17:35Z scan, covered by the 17:39Z history scan).
- Velocity (labels: estimates from the story file, wall-clock measured, tokens from the Agent tool results; resumed agents report cumulative usage and are counted once at their final value): AJ-001 0.50 h, 360,480 tokens; AJ-002 1.25 h, 389,321; AJ-003 to AJ-006 5.50 h pooled, 377,849; AJ-007 2.00 h, 321,705; AJ-008 1.50 h, 274,838; AJ-009 1.25 h, 257,263; AJ-010 1.75 h including both follow-ups, 607,756; AJ-011 1.25 h, 421,519; AJ-012 1.00 h, 340,207; the shared docs writer for the AJ-003 to AJ-012 entries and fixes 340,872 before this exit turn; exit gates (battery 52,247, full spec pass 74,702, peer review 152,521 + 81,020 + 32,929) 393,419. Total 4,085,229 sub-agent tokens before this exit turn, about 255,000 per estimated hour; the user's own `/code-review medium` run (111,080) is outside the session count. Wall-clock 16:14Z (first measured command) to 17:50Z (exit commit), 96 minutes; the per-story spans overlapped because gates ran in parallel. Delivered MUST hours per calendar day: 15.00 on day 1.
- Conflicts recorded for later sessions: (1) the session prompt mandates the merge subject and the `M0 exit:` subject, which do not match the story's commit-message pattern; the M7 check should use `--no-merges` and exclude `^M[0-9] exit( addendum)?:` subjects (the addendum subject `M0 exit addendum:` would otherwise count as non-conforming). (2) The AJ-009 subject carries both covered IDs (standing order 5) while the story quotes `AJ-009 REQ-002:` literally. (3) Story AJ-010 criterion 3 versus design 3.2 on the mask: resolved for the design (N-09), the story text superseded and dated. (4) Story AJ-012 says the 45-minute onboarding box is in M2, plan 3.6 says AJ-101 in M4; the plan is the calendar of record. (5) Design 2.3's claim of an LF working tree was wrong for `.editorconfig` and `.gitignore`; fixed by AJ-009.
- Plan text corrected in this exit commit for the N-09 decision: M2 story AJ-052 criterion 3 and two scenario lines, and one M3 scenario line, no longer accept "the first six" characters of a key.
- Follow-ups carried to M1 AJ-034 (console wiring), from the gates and the user's code review: override the async TextWriter methods or document the Synchronized wrapper; never flush a partial line (or hold back text after the last newline on Flush); handle `\r` progress lines; add the `char[]` fast path; reject star-only registered values and decide a minimum length (the story's five-character scenario constrains it); handle a registered value containing `nvapi-` mid-string (span merging); rename the six-character-window helpers and add a spy writer for Flush; add a top-level catch so unhandled exceptions are redacted; TestSupport takes Microsoft.Extensions.TimeProvider.Testing when the first fixture needs it; consider a `nuget.config` with package source mapping; add `*.cmd` and `*.bat` eol rules only if such scripts appear.
- User actions due before the next milestones (prompt section 12, plan 3.6): none for M1. Before M2 (from 2026-10-21): step 4 (Token Factory credits and Builders Program); steps 5 and 6 are done. Before M4 (2026-10-24): install Ollama and pull the Nemotron tag that REQ-020 names (AJ-002 blocker), laptop on mains power during the campaign. Before M5 (2026-10-26): steps 10 (Nebius AI Cloud account, card, USD 25 minimum) and 11 (ed25519 key pair outside the repository).
- Decisions needed from the user: (1) confirm no cut before the M1 gate; (2) confirm the design's full `****` mask (N-09) as the rule for every later story that mentions key masking (the four M2/M3 lines are already corrected here).
- Next kickoff line, after `git push origin main`: `Run docs/plan/milestone-session-prompt.md for MILESTONE M1. Previous exit commit: origin/main. User actions done: NEBIUS_API_KEY, NVIDIA_API_KEY and NGC_API_KEY set in user scope (presence measured 2026-10-18); no Nebius AI Cloud account yet; Ollama not installed (M4 blocker recorded in docs/build-log.md).`

### AJ-007 follow-up: post-exit hygiene, zero warning-class output (REQ-001)

- Shipped (on main after the M0 exit, at the user's request that no linting, build or other warning remains): `.editorconfig` gains a one-line reason and two sections, scoped to the six M0 placeholder classes in tests/AgentJoules.UnitTests and to tests/AgentJoules.IntegrationTests/PlaceholderIntegrationTests.cs, setting `dotnet_diagnostic.xUnit1004.severity = none`; the rule stays active for every other test file (a scratch copy with a new skipped test still reports xUnit1004 for that file only).
- Measured after the change (2026-10-18T18:01Z): `dotnet restore --force` no warning line; `dotnet build -warnaserror -tl:off --no-incremental` exit 0 with `0 Warning(s)` and `0 Error(s)` (the non-strict build's only line containing "warning" is that summary); `dotnet format --verify-no-changes` exit 0 and `dotnet format --verify-no-changes --severity info` exit 0 (was exit 2 with seven informational xUnit1004 hits); `dotnet test` exit 0, 28 passed, 0 failed, 7 skipped, no warning line; package audit as recorded under `## Version deviations and additions`.
- Decisions: the suppression is file-scoped, not project-wide, so the stories that replace the placeholders (AJ-023, AJ-026, AJ-040, AJ-070, AJ-073, AJ-078, AJ-081) delete their file's entry from the glob when they land; the intentional NoWarn entries stay as designed (CS1591 at the root; CA1707, CA2007, CA1515 under tests/).
- Gates: code-review-gatekeeper on the diff (result recorded in the session recap; the change is seven configuration lines after the milestone gates).
- Velocity: estimate 0 h (hygiene); wall-clock 17:58Z to 18:03Z; sub-agent tokens csharp-expert 60,623 plus the gate.

### M0 post-exit review: findings of the user's code-review max run (REQ-001, REQ-029)

- Shipped on branch milestone/M0-review (commits named in the M0 exit addendum): AJ-004 scope: `nuget.config` (single mapped source, audit sources cleared), NU1900 an error unless `-p:AgentJoulesOfflineAudit=true`, invariant globalization as `RuntimeHostConfigurationOption` items (design 2.3 amended, AJ-004 criterion 3a superseded by a dated note). AJ-007 scope: `[PlaceholderFact("AJ-0nn")]` in TestSupport replaces `[Fact(Skip = ...)]` on the seven placeholders and the `.editorconfig` xUnit1004 suppression is removed; `[IntegrationFact]` compares the trimmed value; the seven DoD bullets added earlier today to M1 to M3 stories are withdrawn. AJ-010 scope: `SecretRedactor.Register` returns a bool and registers two forms of a value (trimmed at both ends; every Unicode format or control character removed by Rune), ignoring forms shorter than five characters, containing `*`, or part of the `nvapi-****` mask; `Redact` collects every regex and registered-value span over the original text, merges overlapping and adjacent spans and emits one mask per span; `RedactingTextWriter.Flush` forwards complete lines only, `Dispose` drains, the dispose gate is `_leaveOpen || !_disposed`, and Write or Flush after an owning Dispose throws `ObjectDisposedException`; fourteen tests added over two rounds (35 on Spec=REQ-029). AJ-011 scope: the scan passes key values on stdin.
- Measured after the changes (2026-10-18T19:52Z, re-measured 20:04Z after the gate round): `dotnet restore --force` no warning line; `dotnet build -warnaserror -tl:off --no-incremental` exit 0, `0 Warning(s)`, `0 Error(s)`; `dotnet format --verify-no-changes` exit 0 and with `--severity info` exit 0; `dotnet test` exit 0, 42 passed, 0 failed, 7 skipped; `Spec=REQ-029` 35 passed; the feature file equals the story's Gherkin; the stdin-form scan clean (row above).
- Decisions: findings about the M1 console wiring (drain at exit, escaped exceptions, whole-line producers, async overloads, `\r` lines, the `char[]` fast path) are recorded as notes under story AJ-034 instead of code in M0; AJ-052's criterion 3 is qualified to handled paths. The minimum registered length of five follows the story's five-character scenario; the user's medium review had proposed eight.
- Deviations: design 2.3's InvariantGlobalization mechanism and AJ-004 criterion 3a are superseded by dated notes rather than rewritten; the seven DoD bullets added at 18:05Z were withdrawn the same day because the attribute makes them unnecessary.
- Gates: code-review-gatekeeper APPROVED (seven MINOR: two registered forms, bool Register, Flush simplification, NU1900 scope, audit sources, an exact assertion, dead comparers; all applied); security-specialist round 1 CHANGES_REQUIRED (MAJOR: a value echoed in its original form with an interior invisible character was not masked; MINOR: NU1900 fail-open, NU1507 redundant; LOW: silent rejection), all applied, round 2 recorded in the exit addendum; spec-compliance-reviewer APPROVED (REQ-001 and REQ-029 PASS).
- Velocity: estimate 0 h (review rework); wall-clock 19:41Z to the merge; sub-agent tokens: csharp-expert (build config and placeholders) 77,720; csharp-expert (redactor, cumulative for all AJ-010 work) 169,296, replacing the 135,787 counted at the M0 exit; docs writer cumulative reported at the addendum; gates and peer review added at the addendum. The user's code-review max run (438,711) is outside the session count.

## M0 exit addendum

- Post-exit review merged: 61160d7c19aa786710e71bbee4d2b22603441d7b (`git merge --no-ff milestone/M0-review`, 2026-10-19T07:28Z) on top of the hygiene commit c111848; review commits 4e4191a (AJ-004), e100a20 (AJ-007), 5f8e68a (AJ-010). Nothing pushed; after this addendum commit main is 22 commits ahead of origin/main.
- Trigger: the user's request for zero warning-class output (hygiene commit c111848, 2026-10-18T18:05Z) and the user's `/code-review max` run (fifteen measured findings, 2026-10-18); the findings inside M0 artifacts are applied, the rest are AJ-034 notes.
- Gates on the review branch: code-review-gatekeeper APPROVED (seven MINOR, all applied); security-specialist round 1 CHANGES_REQUIRED (MAJOR: a value echoed with an interior invisible character was not masked; applied by registering two Rune-cleaned forms) and round 2 APPROVED (LOW for AJ-034: lone surrogates, variation selectors, a multi-line value written character by character); spec-compliance-reviewer APPROVED (REQ-001 and REQ-029 PASS); peer-review-critic APPROVED (MINOR: the lone-surrogate case is a regression against main for malformed in-memory values, two design lines needed the pointer note, the AJ-034 notes should become numbered criteria when M1 starts, and this addendum). The peer review call lasted 40,708 s because the session was idle overnight; the review-branch work ran 2026-10-18T19:41Z to 20:09Z.
- Applied in this commit: the dated pointers on design 5.5 and the section 7 checklist row; the AJ-034 note on `Register` returning false and on lone surrogates and variation selectors.
- Zero-warning state at 61160d7 (measured 2026-10-18T20:04Z on the identical tree): `dotnet restore --force` prints no warning line; `dotnet build -warnaserror -tl:off --no-incremental` exit 0 with `0 Warning(s)` and `0 Error(s)`; `dotnet format --verify-no-changes` exit 0 and `--severity info` exit 0; `dotnet test` exit 0 with 42 passed, 0 failed, 7 skipped; `Spec=REQ-029` 35 passed; no vulnerable package; the only deprecation marker is NuGet's Legacy flag on xunit 2.9.3 (ADR-03).
- Velocity: estimated hours unchanged at 16.00 (the post-exit work is rework). Sub-agent tokens for all of M0 including the hygiene and review rounds: 5,007,353 plus the docs-writer turn that writes this addendum (counting cumulative agents once at their final value: csharp-expert for AJ-010 185,731; docs writer 461,763 before this turn; review-branch gates and peer review 530,418; hygiene round 143,151). The user's own review runs (111,080 and 438,711) are outside the count.
- Next kickoff line (unchanged): `Run docs/plan/milestone-session-prompt.md for MILESTONE M1. Previous exit commit: origin/main. User actions done: NEBIUS_API_KEY, NVIDIA_API_KEY and NGC_API_KEY set in user scope (presence measured 2026-10-18); no Nebius AI Cloud account yet; Ollama not installed (M4 blocker recorded in docs/build-log.md).`

### M0 review2: suggestions, question and nitpicks of the final full-depth review (REQ-001, REQ-029)

- Shipped on branch milestone/M0-review2: `SecretRedactor.TrimEnds` returns a slice of the original string, decoding runes from both ends and keeping any invalid sequence (a lone surrogate) as visible, so the ends-trimmed form masks a raw echo of a malformed configured value; `RemoveInvisibleCharacters` drops invalid sequences for the clean form; `Register` registers the whole value and each of its CR- or LF-separated lines in up to three forms each (ends-trimmed, clean, rune-normalised with U+FFFD for an echo that passed through UTF-8), after the security gate showed that rejecting a multi-line value whole left a key on one of its lines unmasked and the code-review gate showed that per-line registration alone dropped short lines and newline-stripped echoes; the `RedactingTextWriter` summary is reflowed to 120 columns and states that a leaveOpen writer stays usable after Dispose and drains on every later Dispose (the AJ-034 console wrappers may receive output after the first drain and before the ProcessExit drain) while an owning writer throws; the generic Reqnroll step "the result is ..." asserts exact equality only; `tests/AgentJoules.Specs/Features/Configuration/.gitkeep` deleted because the folder holds a feature file; fifteen tests added over three gate rounds, two of them theories over the seven line terminators (61 cases on Spec=REQ-029). Docs: the M7 subject exclusion reads `^M[0-9] exit( addendum)?:`; the Test counts and Per-ID tables carry the 19:52Z, 20:04Z and 08:33Z rows; AJ-010 criterion 5 reworded to "held until a newline" with a dated note; the AJ-010 and AJ-034 notes state the Register contract (false only when no form is registrable; forms rejected for length, `*` or the mask; the whole value and each line registered; surrogates kept; variation selectors not stripped). The split also covers VT, FF, U+0085, U+2028 and U+2029 (security LOW). The clean form also drops the line-separator characters (code-review round 3 MINOR).
- Measured after the changes (2026-10-19T07:59Z, re-measured 08:40Z after the peer-review MINORs): `dotnet build -warnaserror -tl:off --no-incremental` exit 0, `0 Warning(s)`, `0 Error(s)`; `dotnet format --verify-no-changes --severity info` exit 0; `dotnet test` exit 0, 68 passed, 0 failed, 7 skipped; `Spec=REQ-029` 61 passed; the feature file equals the story's Gherkin; the stdin-form scan clean (row above).
- Decisions: the first version of this round rejected a multi-line value whole (false, nothing registered); the security gate measured that a key on one line of such a value then printed unmasked, so the rule became per-line registration, and the host still warns by variable name when Register returns false (AJ-034).
- Gates: recorded in the review2 addendum line of `## M0 exit addendum` after the commits. Peer-review-critic APPROVED with four MINOR (the seven terminators named in the notes, two phrases of this entry, the M7 check command, a theory over all terminators), all applied before the merge.
- Velocity: estimate 0 h (review rework); wall-clock 2026-10-19T07:56Z to the merge; sub-agent tokens: csharp-expert cumulative for all AJ-010 work 202,561 (replaces 185,731); docs writer cumulative reported at the addendum; the full-depth review itself (comprehensive-analyst) 227,905; gates and peer review added at the addendum.

### M0 exit addendum, review2

- Merged: 3e21420eb93b1876fc43f5735d7b2127be82b38d (`git merge --no-ff milestone/M0-review2`, 2026-10-19T08:24Z); review2 commits 84695f5, d7735ca, 3c35599, 6b48ea5 (all `AJ-010 REQ-029:`). Nothing pushed; after this addendum commit main is 28 commits ahead of origin/main.
- Trigger: the `agentic-framework:code-review` full-depth review over all of M0 (delegated to comprehensive-analyst, 54 of 54 files, APPROVED, three suggestions, one question, five nitpicks) and the user's instruction to implement every suggestion and nitpick now.
- Gates on the review2 branch: code-review-gatekeeper rounds 1 to 3 (APPROVED; CHANGES_REQUIRED, MAJOR: per-line registration alone dropped the whole-value forms; APPROVED, MINOR: the clean form must also drop line separators, applied); security-specialist rounds 1 and 2 (CHANGES_REQUIRED, MEDIUM: rejecting a multi-line value whole failed open, LOW: the U+FFFD echo; APPROVED, three LOW recorded: lines shorter than five characters are not masked alone, the other line separators now also split, echoes that change two things at once); spec-compliance-reviewer rounds 1 to 3 (CHANGES_REQUIRED on the note wording twice, then APPROVED); peer-review-critic APPROVED with four MINOR, all applied before the merge (the seven terminators named in the notes, two phrases of the review2 entry, the merge-aware subject check written into the M7 story, theories over all seven terminators).
- Zero-warning state at 3e21420 (measured 2026-10-19T08:24Z): strict build `0 Warning(s)`, `0 Error(s)`; `dotnet format --verify-no-changes --severity info` exit 0; `dotnet test` 68 passed, 0 failed, 7 skipped; `Spec=REQ-029` 61 cases; the stdin-form scan clean on tracked, untracked and history.
- Velocity: estimated hours unchanged at 16.00. Sub-agent tokens for the review2 round: csharp-expert cumulative for all AJ-010 work 256,402 (replaces 202,561); docs writer cumulative 552,519 before this turn; gates 111,509 + 96,354 + 86,942 (code review), 113,291 + 103,993 (security), 69,258 + 27,738 + 46,911 (spec), 71,751 (peer review); the full-depth review 227,905. M0 total including every round: about 6.2 million sub-agent tokens; the user's own review runs (111,080 and 438,711) stay outside the count.
- Next kickoff line unchanged: `Run docs/plan/milestone-session-prompt.md for MILESTONE M1. Previous exit commit: origin/main. User actions done: NEBIUS_API_KEY, NVIDIA_API_KEY and NGC_API_KEY set in user scope (presence measured 2026-10-18); no Nebius AI Cloud account yet; Ollama not installed (M4 blocker recorded in docs/build-log.md).`

### User decisions before M1 (2026-10-19, user-reported in the session)

- No cut-order step is applied before the M1 gate on 2026-10-21 16:00 UTC; the M1 exit re-runs the plan 3.5 rule with measured velocity and decides then.
- The full mask is the standing rule for every later story: NVIDIA keys show only the public prefix (`nvapi-****`), every other secret becomes `****` (design-review N-09, design 3.2); the M2 and M3 story lines were already corrected on 2026-10-18 and no later session reopens the rule.
- The local tier keeps the plan's shape: the NIM container is the first attempt in M4 within its 90-minute box, and Ollama stays the fallback (REQ-020, ADR-09); the user installs Ollama and pulls the Nemotron tag before M4 starts on 2026-10-24.
