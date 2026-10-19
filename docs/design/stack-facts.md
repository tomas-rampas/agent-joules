# Agent Joules - verified stack facts (as of 2026-10-18)

Evidence labels: **verified** = measured by me (build, run, exit code) or read verbatim from a primary source; **inferred** = follows from verified facts but not tested; **could not verify** = no evidence obtained.
Probe projects (throwaway, not in the repository) live beside this file in the scratchpad: `probe/` (M.E.AI + OpenAI + Agent Framework + OTel + NVML + schema + time), `xv2/`, `xv3vs/`, `xv3mtp/`, `xv2mtp/`, `rq2/`, `rq3/`, `fmt/`, `lib/`, `cli/`.
Machine: SDK 10.0.401 (`dotnet --list-sdks` also shows 10.0.112, 9.0.318, 8.0.425), Windows 11, NVIDIA GeForce RTX 3080 Ti Laptop GPU, driver 616.92.
NuGet versions and dates come from `https://api.nuget.org/v3-flatcontainer/<id>/index.json` and the registration API (published timestamps in UTC).

---

## 1. Microsoft.Extensions.AI (M.E.AI)

**Label: verified.**

- Current stable: `Microsoft.Extensions.AI` **10.11.0** (published 2026-10-14). `Microsoft.Extensions.AI.Abstractions` **10.11.0**. Newest versions of the previous line: 10.10.0, 10.10.1 (Abstractions only).
- Targets (nuspec): net10.0, net9.0, net8.0, netstandard2.0, net462. Dependencies at net10.0: Abstractions 10.11.0, Caching.Abstractions / DI.Abstractions / Logging.Abstractions 10.0.13, System.Numerics.Tensors 10.0.13.
- Types: `IChatClient` and `DelegatingChatClient` live in Abstractions/M.E.AI (namespace `Microsoft.Extensions.AI`); `ChatClientBuilder` and `AsBuilder()` in `Microsoft.Extensions.AI` package.
- `Use` overloads on `ChatClientBuilder` (compiled in probe): `Use(Func<IChatClient,IChatClient>)`, `Use(Func<IChatClient,IServiceProvider,IChatClient>)`, shared `Use(Func<IEnumerable<ChatMessage>, ChatOptions?, Func<...> next, CancellationToken, Task>)`, and the pair
  `Use(getResponseFunc, getStreamingResponseFunc)` where the third delegate argument is the **inner `IChatClient`** (call `next.GetResponseAsync(...)` / `next.GetStreamingResponseAsync(...)`), not a delegate. I first wrote `next(messages, options, ct)` and got CS0149 - the docs text is explicit about `IChatClient` as the third parameter.
- Streaming middleware pattern (compiled and run): an `async IAsyncEnumerable<ChatResponseUpdate>` iterator that does `await foreach (var u in next.GetStreamingResponseAsync(...))`, inspects `u.Contents`, `yield return u`, and runs post-stream code after the loop (my probe recorded "completed after 18 ms" only after the stream was fully enumerated). In a `DelegatingChatClient` subclass override `GetStreamingResponseAsync` with `[EnumeratorCancellation] CancellationToken`. Metering must stop its window in a `finally`, because a consumer can abandon the enumerator early.
- Usage location (measured against a local OpenAI-compatible mock server):
  - Non-streaming: `ChatResponse.Usage` (`UsageDetails`: `InputTokenCount`, `OutputTokenCount`, `TotalTokenCount`, `CachedInputTokenCount`, `ReasoningTokenCount`, all `long?`). Observed in=11, out=2, total=13.
  - Streaming: a `UsageContent` (`.Details` is a `UsageDetails`) inside `ChatResponseUpdate.Contents` of the last update (4 updates for 3 content chunks plus the usage chunk). `updates.ToChatResponse()` folds it into `ChatResponse.Usage` (in=11, out=2).
  - If the server omits the usage chunk, folded `Usage` is **null** (measured: `Usage is null` = True). Handle null.
  - The captured streaming request body was `{"messages":[...],"model":"m","stream":true,"stream_options":{"include_usage":true}}` - `AsIChatClient()` asks for the usage chunk by itself. Whether Nebius, build.nvidia.com, NIM and Ollama honour `include_usage`: **could not verify** (mock only).
- Telemetry: `UseOpenTelemetry(...)` is in the `Microsoft.Extensions.AI` package (see item 10).
- Sources: https://learn.microsoft.com/en-us/dotnet/ai/ichatclient (Custom middleware, Use overloads, UseOpenTelemetry), https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai, nuspec files fetched from nuget.org, XML docs in `~/.nuget/packages/microsoft.extensions.ai.abstractions/10.11.0/lib/net10.0/Microsoft.Extensions.AI.Abstractions.xml`, probe `probe/Program.cs`.

## 2. Microsoft.Extensions.AI.OpenAI and the OpenAI .NET SDK

**Label: verified.**

- `Microsoft.Extensions.AI.OpenAI` **10.11.0** (2026-10-14), depends on `OpenAI` **2.14.0** (2026-09-15) and Abstractions 10.11.0. Targets net10.0, net9.0, net8.0, netstandard2.0, net462.
- Pattern (compiled with `-warnaserror`, executed against the mock server; the request landed on `POST /v1/chat/completions`):
  ```csharp
  using System.ClientModel; using OpenAI; using Microsoft.Extensions.AI;
  var client = new OpenAIClient(new ApiKeyCredential(key),
      new OpenAIClientOptions { Endpoint = new Uri("https://host/v1") });
  IChatClient chat = client.GetChatClient("model-id").AsIChatClient();
  ```
  The SDK appends `/chat/completions` to the endpoint, so the base URI must end in `/v1` (or whatever prefix the server uses). Runtime type returned: `Microsoft.Extensions.AI.OpenAIChatClient`.
- The Agent Framework packages pin `Microsoft.Extensions.AI.OpenAI` 10.10.1; a direct reference to 10.11.0 resolved without conflict in my probe (restore + build exit 0).
- Exact base URLs of Nebius Token Factory, build.nvidia.com, NIM and Ollama: **could not verify** (not requested; to confirm during implementation).
- Sources: nuget.org nuspec for `microsoft.extensions.ai.openai/10.11.0`, `probe/Program.cs`.

## 3. Microsoft Agent Framework

**Label: verified** (fork source read; NuGet package built and run against a mock chat client).

**NuGet** (all stable, published 2026-10-14 for 1.25.0; 1.0.0 GA was 2026-04-02):
| Package ID | Latest stable |
|---|---|
| `Microsoft.Agents.AI` | 1.25.0 |
| `Microsoft.Agents.AI.Abstractions` | 1.25.0 |
| `Microsoft.Agents.AI.Workflows` | 1.25.0 |
| `Microsoft.Agents.AI.Workflows.Generators` (optional source generator) | 1.25.0 |
| `Microsoft.Agents.AI.OpenAI` | 1.25.0 |
Dependencies of 1.25.0 at net10.0: M.E.AI 10.10.0, Abstractions 10.10.1, `OpenTelemetry.Api` 1.18.0 (Workflows), `Microsoft.Extensions.AI.Evaluation` 10.10.0, `Microsoft.ML.Tokenizers` 2.0.0.

**Fork version:** `D:\src\github\agent-framework\dotnet\nuget\nuget-package.props` has `<VersionPrefix>1.23.0</VersionPrefix>`; the fork is therefore two releases behind NuGet (1.23.0 was published 2026-09-29, 1.25.0 on 2026-10-14). `D:\src\github\agent-framework\dotnet\global.json`: SDK 10.0.401, `rollForward: minor`, `allowPrerelease: false`, test runner `Microsoft.Testing.Platform`. `Directory.Build.props`: `AnalysisLevel` 10.0-all, `TreatWarningsAsErrors` true, `TargetFrameworks` net10.0;net9.0;net8.0;netstandard2.0;net472.

**Exact type names** (fork paths relative to `D:\src\github\agent-framework\dotnet\src\Microsoft.Agents.AI.Workflows\`; all in namespace `Microsoft.Agents.AI.Workflows` unless noted; the same code compiled and ran against the 1.25.0 package):
- Builder: `WorkflowBuilder` (`WorkflowBuilder.cs`): ctor `WorkflowBuilder(ExecutorBinding start)`; `AddEdge(...)`; **fan-out** `AddFanOutEdge(ExecutorBinding source, IEnumerable<ExecutorBinding> targets)` (line 406) plus a selector overload `AddFanOutEdge<T>(..., Func<T?, int, IEnumerable<int>>? targetSelector)`; **fan-in** `AddFanInBarrierEdge(IEnumerable<ExecutorBinding> sources, ExecutorBinding target)` (line 499); `WithOutputFrom(params ExecutorBinding[])`; `WithName`; `Build(bool validateOrphans = true)` -> `Workflow`. The fan-in method is named `...BarrierEdge`, not `AddFanInEdge`.
- Executors: `Executor`, `Executor<TInput>`, `Executor<TInput,TOutput>` (`Executor.cs`, abstract `HandleAsync(TInput, IWorkflowContext, CancellationToken)`, overridable `OnMessageDeliveryFinishedAsync`), `FunctionExecutor<...>`; bindings via `ExecutorBinding` with implicit conversions from `Executor`, `AIAgent`, `RequestPort` (`ExecutorBinding.cs`); `BindAsExecutor` extension methods (`ExecutorBindingExtensions.cs`).
- Message types an executor sends/yields must be declared: `[SendsMessage(typeof(T))]`, `[YieldsOutput(typeof(T))]` on the class (`Attributes\`). Without the declaration the run fails with `Executor 'start' cannot send messages of type 'Microsoft.Extensions.AI.ChatMessage'` (measured). The attributes are read by reflection (`ProtocolBuilder.cs`), so the generator is optional: my probe built with `-warnaserror` and ran correctly **without** `Microsoft.Agents.AI.Workflows.Generators`. If the generator package is referenced, classes with these attributes must be `partial` (error `MAFGENWF008`, measured).
- Agent backed by `IChatClient`: `ChatClientAgent` (`..\Microsoft.Agents.AI\ChatClient\ChatClientAgent.cs`; ctor `ChatClientAgent(IChatClient chatClient, string? instructions = null, string? name = null, string? description = null, IList<AITool>? tools = null, ILoggerFactory? ..., IServiceProvider? ...)`), extension `IChatClient.AsAIAgent(...)` in `ChatClientExtensions.cs` (namespace `Microsoft.Extensions.AI`; read in the fork, not compiled against 1.25.0 - I used `new ChatClientAgent(...)` there). Agent to workflow node: `agent.BindAsExecutor(new AIAgentHostOptions { ForwardIncomingMessages = false })` (`ExecutorBindingExtensions.cs` line 431, `AIAgentHostOptions.cs`).
- Agent nodes only start when they receive a `TurnToken`: the start executor sends the `ChatMessage` and then `new TurnToken(emitEvents: false)` through `IWorkflowContext.SendMessageAsync` (fork sample `samples\03-workflows\Concurrent\Concurrent\Program.cs`).
- Running and events: `InProcessExecution.RunStreamingAsync(workflow, input)` -> `StreamingRun`; `await foreach (WorkflowEvent e in run.WatchStreamAsync())` (`InProcessExecution.cs`, `StreamingRun.cs`). Non-streaming: `InProcessExecution.RunAsync` -> `Run` with `OutgoingEvents`/`NewEvents` (`Run.cs`). Event classes: `WorkflowStartedEvent`, `SuperStepStartedEvent`/`SuperStepCompletedEvent`, `ExecutorInvokedEvent`/`ExecutorCompletedEvent`/`ExecutorFailedEvent` (all `ExecutorEvent` with `ExecutorId`), `WorkflowOutputEvent` (`Data`, `ExecutorId`), `AgentResponseUpdateEvent`, `AgentResponseEvent`, `WorkflowErrorEvent` (`Exception`), `WorkflowWarningEvent`, `RequestInfoEvent`.
- Measured event order for start -> {Physicist, Chemist} fan-out -> aggregator fan-in: WorkflowStarted, SuperStepStarted, ExecutorInvoked(start), ExecutorCompleted(start), SuperStepCompleted, SuperStepStarted, both agents invoked and completed in the same super step, SuperStepCompleted, then aggregator, `WorkflowOutputEvent` with `Chemist:Hello / Physicist:Hello` (agent order is not deterministic; the second run printed Physicist first). Agent executor ids carry a GUID suffix (`Physicist_5dfce0...`).
- Observability helper: `WorkflowBuilder.WithOpenTelemetry(...)` (`OpenTelemetryWorkflowBuilderExtensions.cs`) - not exercised.
- Sources: files above; fork sample `samples\03-workflows\Concurrent\Concurrent\Program.cs` and `...\MapReduce\Program.cs`; nuget.org registration; `probe/Program.cs`.

## 4. System.CommandLine

**Label: verified.**

- 2.0 is GA. `2.0.0` was published 2025-11-11; latest stable is **2.0.13** (2026-10-13). A `3.0.0-rc.2.26504.105` prerelease exists (same day) - do not take it.
- Target frameworks in the 2.0.13 nuspec: **net8.0 and netstandard2.0** (no net10.0 asset). It restores and runs on `net10.0` through the net8.0 asset (built with `-warnaserror`, exit 0).
- The Microsoft Learn overview page does not itself say "stable" (it just links the package; page date 2025-10-21), so GA status rests on NuGet listing 2.0.0-2.0.13 without a prerelease suffix.
- 2.0 API compiled and executed in `cli/P.cs`: `new Option<string>("--model") { Required = true }`, `DefaultValueFactory = _ => 3`, `command.SetAction(async (parseResult, ct) => ...)`, `parseResult.GetValue(option)`, `root.Subcommands.Add(cmd)`, `await root.Parse(args).InvokeAsync()`. A missing required option printed help and returned exit code **1**.
- No fallback needed. If you wanted one: Spectre.Console.Cli 0.57.2 is the newest stable (1.0.0 is alpha only) - not evaluated further.
- Sources: https://api.nuget.org/v3-flatcontainer/system.commandline/index.json, nuspec 2.0.13, https://learn.microsoft.com/en-us/dotnet/standard/commandline/, `cli/`.

## 5. Reqnroll

**Label: verified.**

- Current: `Reqnroll` **3.3.4**, released 2026-03-23 (CHANGELOG: "Improve the command line dotnet new project templates to support .NET 10, xUnit v3"). Companion packages, all 3.3.4: `Reqnroll.xunit`, `Reqnroll.xunit.v3`, `Reqnroll.nunit`, `Reqnroll.mstest`, `Reqnroll.tunit`, `Reqnroll.Tools.MsBuild.Generation`.
- Runners: NUnit, MSTest (v4 supported from Reqnroll 3.2), TUnit, xUnit v2 (`Reqnroll.xunit`, depends on `xunit.core` 2.8.1) and xUnit v3 (`Reqnroll.xunit.v3`, since 3.1.0). The compatibility page lists .NET Framework 4.6.2/4.7.2/4.8.1, .NET 8.0, 9.0, **10.0**. CHANGELOG 3.3.0: "Updated TUnit integration to support ... .NET 10 SDK compatibility".
- Measured on net10.0 with SDK 10.0.401:
  - `rq2` (xunit 2.9.3 + xunit.runner.visualstudio 3.1.5 + Microsoft.NET.Test.Sdk 18.11.0 + Reqnroll.xunit 3.3.4): `dotnet test` exit 0, "Passed! Failed: 0, Passed: 1".
  - `rq3` (xunit.v3 4.0.2 + Reqnroll.xunit.v3 3.3.4, MTP runner in `global.json`): `dotnet test` exit 0, "total: 1, succeeded: 1".
- **Listing difference that matters for any test-ID matching:** Reqnroll scenarios list by **display name only**, not fully-qualified. Exact `dotnet test --list-tests` output: `Idle GPU reports zero delta` (v2: under "The following Tests are available:"; v3: under "Discovered 1 tests in assembly"). xUnit-native tests do print the FQ class name (item 6).
- Fallback (Given-When-Then named xUnit tests) is **not required**; keep it as the escape hatch only.
- Sources: https://raw.githubusercontent.com/reqnroll/Reqnroll/main/CHANGELOG.md, https://docs.reqnroll.net/latest/installation/compatibility.html, `rq2/`, `rq3/`.

## 6. xunit v2 vs v3 on net10.0 and `dotnet test --list-tests`

**Label: verified (measured).**

- v2: `xunit` **2.9.3** (2025-01-08), `xunit.runner.visualstudio` 3.1.5, `Microsoft.NET.Test.Sdk` 18.11.0. v3: `xunit.v3` **4.0.2** (2026-10-09; note the version jump 3.2.2 -> 4.0.0), `xunit.runner.visualstudio` 4.0.1; test project must be `OutputType` Exe. `xunit.v3` 4.0.2 depends on `xunit.v3.mtp-v2` and targets net8.0 (runs on net10.0).
- Test class used: `AgentJoules.Tests.Energy.MeterTests` with one `[Fact]` and one `[Theory]` with two `[InlineData]`. Every command below was run in the named directory; exit codes are `$?`.

**v2 - `xv2/`, VSTest mode** - command `dotnet test --list-tests`, exit **0**:
```
Test run for ...\xv2\bin\Debug\net10.0\xv2.dll (.NETCoreApp,Version=v10.0)
The following Tests are available:
    AgentJoules.Tests.Energy.MeterTests.Given_idle_gpu_When_sampled_Then_energy_is_zero
    AgentJoules.Tests.Energy.MeterTests.Given_n_samples_When_summed_Then_ok(n: 1)
    AgentJoules.Tests.Energy.MeterTests.Given_n_samples_When_summed_Then_ok(n: 2)
```
**v3 - `xv3mtp/`, `global.json` has `"test": { "runner": "Microsoft.Testing.Platform" }`** - command `dotnet test --list-tests`, exit **0**:
```
Discovering tests from ...\xv3mtp\bin\Debug\net10.0\xv3mtp.dll (net10.0|x64)

Discovered 3 tests in assembly - ...\xv3mtp\bin\Debug\net10.0\xv3mtp.dll (net10.0|x64)
  AgentJoules.Tests.Energy.MeterTests.Given_idle_gpu_When_sampled_Then_energy_is_zero
  AgentJoules.Tests.Energy.MeterTests.Given_n_samples_When_summed_Then_ok(n: 1)
  AgentJoules.Tests.Energy.MeterTests.Given_n_samples_When_summed_Then_ok(n: 2)

Discovered 3 tests.
```
Both include the fully qualified class name. Parsing difference: v2 indents 4 spaces after a header line; v3 indents 2 spaces and adds blank lines plus "Discovered N tests".

Mode constraints (all measured, SDK 10.0.401):
- `xunit.v3` + `Microsoft.NET.Test.Sdk` + no MTP opt-in (`xv3vs/`): `dotnet test --list-tests` exit **1**: `error : Testing with VSTest target is no longer supported by Microsoft.Testing.Platform on .NET 10 SDK and later. If you use dotnet test, you should opt-in to the new dotnet test experience. For more information, see https://aka.ms/dotnet-test-mtp-error`.
- xunit v2 project under a `global.json` with the MTP runner (`xv2mtp/`): exit **1**: `global.json defines test runner to be Microsoft.Testing.Platform. All projects must use that test runner. The following test projects are using VSTest test runner: xv2.csproj`. A solution cannot mix v2 (VSTest) and v3 (MTP) test projects under one `global.json` runner setting; pick one.
- Run results: `xv3mtp` `dotnet test --no-build` exit 0 ("total: 3, succeeded: 3"); `xv2` exit 0 ("Passed! Failed: 0, Passed: 3").
- Sources: `xv2/`, `xv3vs/`, `xv3mtp/`, `xv2mtp/`, nuget.org flat container.

## 7. `System.Text.Json.Schema.JsonSchemaExporter`

**Label: verified.**

- Namespace `System.Text.Json.Schema`; class `JsonSchemaExporter` (introduced in .NET 9); extension methods `JsonSerializerOptions.GetJsonSchemaAsNode(Type, JsonSchemaExporterOptions?)` and `JsonTypeInfo.GetJsonSchemaAsNode(JsonSchemaExporterOptions?)`; returns `JsonNode`. Options type `JsonSchemaExporterOptions` (`TreatNullObliviousAsNonNullable`, `TransformSchemaNode`). Runtime, so suitable at run time or in a build-time tool/test; no MSBuild integration exists (inferred).
- Traps measured on net10.0:
  1. `new JsonSerializerOptions(JsonSerializerDefaults.Web)` without a resolver throws `InvalidOperationException: JsonSerializerOptions instance must specify a TypeInfoResolver setting before being marked as read-only.` Set `TypeInfoResolver = new DefaultJsonTypeInfoResolver()` or use a source-generated context (`Ctx.Default.RunResult.GetJsonSchemaAsNode(...)`, identical output: `JsonNode.DeepEquals` = True), or `JsonSerializerOptions.Default`.
  2. `JsonSerializerDefaults.Web` sets `NumberHandling = AllowReadingFromString`, so every number becomes `{"type":["string","number"],"pattern":"..."}`. Set `NumberHandling = JsonNumberHandling.Strict` to get `{"type":"number"}` / `{"type":["integer","null"]}`.
  3. `UnmappedMemberHandling = Disallow` emits `"additionalProperties": false`; otherwise it is absent. No `$schema` keyword is emitted (add it in `TransformSchemaNode` or by editing the root node). All constructor/record parameters are listed under `required`, including nullable ones.
- Strict output for `record RunResult(string RunId, DateTimeOffset StartedUtc, double EnergyJoules, long? InputTokens, string[] Tags, Dictionary<string,double> Extra)`:
  `{"type":"object","properties":{"runId":{"type":"string"},"startedUtc":{"type":"string","format":"date-time"},"energyJoules":{"type":"number"},"inputTokens":{"type":["integer","null"]},"tags":{"type":"array","items":{"type":"string"}},"extra":{"type":"object","additionalProperties":{"type":"number"}}},"required":["runId","startedUtc","energyJoules","inputTokens","tags","extra"],"additionalProperties":false}`
- Sources: https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/extract-schema (page date 2026-09-24), `probe/Program.cs`.

## 8. `LibraryImport` + `NativeLibrary.SetDllImportResolver`

**Label: verified on Windows; Linux resolution inferred (no Linux host with an NVIDIA driver here).**

- `[LibraryImport("nvml", EntryPoint = "nvmlInit_v2")] internal static partial int Init();` plus `NativeLibrary.SetDllImportResolver(typeof(Nvml).Assembly, resolver)` in a static constructor works; the resolver picks `nvml.dll` on Windows and `libnvidia-ml.so.1` (then `libnvidia-ml.so`) elsewhere via `OperatingSystem.IsWindows()`. Proof that the resolver is what makes it work (`lib/P.cs`): with a logical name `agentjoules-nvml-logical` that exists as no file, `Init()` returned 0 when the resolver mapped it to `nvml.dll`, and threw `System.DllNotFoundException: Unable to load DLL 'agentjoules-nvml-logical' or one of its dependencies` when the resolver returned 0. Missing driver therefore surfaces as `DllNotFoundException` on the first call - catch it, and also handle `nvmlInit_v2` != 0.
- `LibraryImport` is a source generator that emits the `DllImport` internally, so the runtime resolver applies unchanged.
- `StringMarshalling`: a `string` parameter without it fails to compile with `SYSLIB1051: Marshalling string or char without explicit marshalling information is not supported. Specify 'LibraryImportAttribute.StringMarshalling' ...` (measured); adding `StringMarshalling = StringMarshalling.Utf8` compiles. The NVML functions needed here take only output `char*` buffers, which I marshalled as `Span<byte>` (no string marshalling needed) and decoded with UTF-8 up to the first NUL.
- Analyzer interaction (measured with `AnalysisLevel latest-all` and `-warnaserror`): every `LibraryImport` raises **CA5392** ("didn't use DefaultDllImportSearchPaths attribute"), and `[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32 | DllImportSearchPath.AssemblyDirectory)]` then raises **CA5393** ("unsafe DllImportSearchPath value AssemblyDirectory"). `[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]` builds clean; the attribute is valid only on assembly or method, not on a class (CS0592). The real NVML calls still worked (`nvml.dll` is in `C:\Windows\System32` on this machine). Older drivers placed `nvml.dll` under `C:\Program Files\NVIDIA Corporation\NVSMI` (inferred, not tested) - not covered by System32-only probing.
- Sources: `probe/Nvml.cs`, `lib/P.cs`; https://learn.microsoft.com/dotnet/standard/native-interop/pinvoke-source-generation (not fetched; behaviour above is measured).

Note (2026-10-19, AJ-021 security gate, measured): the System32 search-path attribute keeps analyzers CA5392 and CA5393 quiet but does not make the load System32-only at run time; the host's native-library folders are probed first, and a resolver returning zero falls through to the runtime's default search. The project loads `nvml.dll` through its full `Environment.SystemDirectory` path and throws `DllNotFoundException` from the resolver when nothing loads (design 3.1 as amended).

## 9. NVML functions

**Label: verified** (declarations and doc comments read from NVIDIA's `nvml.h`, NVML 13.4.92, copyright 2026, mirrored at https://raw.githubusercontent.com/NVIDIA/go-nvml/main/gen/nvml/nvml.h and sourced from `cuda_nvml_dev` 13.4.92; official HTML reference: https://docs.nvidia.com/deploy/nvml-api/api/group__nvmlDeviceQueries.html, whose function index confirms the signatures, units for energy/power and the memory description; the "Volta" qualifier is in the header comment, not in the visible index text). Then **every call executed on the local RTX 3080 Ti Laptop GPU** with the P/Invoke in `probe/Nvml.cs`.

`nvmlReturn_t` is a 32-bit enum (`NVML_SUCCESS = 0`, `NVML_ERROR_NOT_SUPPORTED = 3`, `NVML_ERROR_NO_PERMISSION = 4`, `NVML_ERROR_INSUFFICIENT_SIZE = 7`, `NVML_ERROR_DRIVER_NOT_LOADED = 9`, `NVML_ERROR_LIBRARY_NOT_FOUND = 12`); `nvmlDevice_t` is an opaque pointer (`nint`).

| Function | C signature | Units / notes | Local result |
|---|---|---|---|
| `nvmlInit_v2` | `nvmlReturn_t nvmlInit_v2(void)` | ref-counted; pair each call with `nvmlShutdown` | rc 0 |
| `nvmlShutdown` | `nvmlReturn_t nvmlShutdown(void)` | | rc 0 |
| `nvmlDeviceGetHandleByIndex_v2` | `(unsigned int index, nvmlDevice_t *device)` | | rc 0 |
| `nvmlDeviceGetName` | `(nvmlDevice_t, char *name, unsigned int length)` | max 96 chars incl. NUL (`NVML_DEVICE_NAME_V2_BUFFER_SIZE` 96) | "NVIDIA GeForce RTX 3080 Ti Laptop GPU" |
| `nvmlDeviceGetUUID` | `(nvmlDevice_t, char *uuid, unsigned int length)` | `NVML_DEVICE_UUID_V2_BUFFER_SIZE` 96 | "GPU-7687512b-..." |
| `nvmlDeviceGetPowerUsage` | `(nvmlDevice_t, unsigned int *power)` | **milliwatts**; "Fermi or newer"; Ampere (except GA100) or newer returns power **averaged over 1 s**, GA100 and older instantaneous | 16047 mW |
| `nvmlDeviceGetTotalEnergyConsumption` | `(nvmlDevice_t, unsigned long long *energy)` | **millijoules** "since the driver was last reloaded"; "For **Volta** or newer fully supported devices" | t0 15269240786 mJ, +2 s 15269274626 mJ, delta 33840 mJ = 16.9 W average, consistent with the 16 W reading |
| `nvmlDeviceGetEnforcedPowerLimit` | `(nvmlDevice_t, unsigned int *limit)` | **milliwatts**; "Kepler or newer" | 85059 mW |
| `nvmlDeviceGetMigMode` | `(nvmlDevice_t, unsigned int *currentMode, unsigned int *pendingMode)` | "For **Ampere** or newer"; `NVML_DEVICE_MIG_DISABLE` 0 / `NVML_DEVICE_MIG_ENABLE` 1; returns NOT_SUPPORTED when MIG unsupported | rc 3 (NOT_SUPPORTED) on this laptop GPU, outputs left 0 - treat rc 3 as "MIG off/unknown", not as an error |
| `nvmlDeviceGetCurrentClocksEventReasons` | `(nvmlDevice_t, unsigned long long *clocksEventReasons)` | bitmask, "For all fully supported products" | 0x1 |
| `nvmlDeviceGetCurrentClocksThrottleReasons` | same shape, `DEPRECATED(13.0)` in the header: "Use nvmlDeviceGetCurrentClocksEventReasons instead" | still exported by driver 616.92 | 0x1 |
| `nvmlSystemGetDriverVersion` | `(char *version, unsigned int length)` | <= 80 chars incl. NUL (`NVML_SYSTEM_DRIVER_VERSION_BUFFER_SIZE` 80) | "616.92" |
| `nvmlDeviceGetMemoryInfo_v2` | `(nvmlDevice_t, nvmlMemory_v2_t *memory)` | bytes; struct `{ unsigned int version; ull total; ull reserved; ull free; ull used; }`; `version` must be set to `sizeof(nvmlMemory_v2_t) \| (2 << 24)` (macro `NVML_STRUCT_VERSION`); `reserved` exists in v2 only | sizeof = 40 (4-byte pad after `version`); total 17179869184, reserved 221249536, free 14151172096, used 2807447552 |

Practical notes: the energy counter is a **device-wide** cumulative counter, so any attribution to one inference call is only valid when nothing else uses the GPU (see recommendations). The clock-event reason name migration (`Throttle` -> `Event`) happened in NVML 13.0 (header `DEPRECATED(13.0)`); older drivers may only export the old name (inferred): probe for the new export first and fall back with `NativeLibrary.TryGetExport`. Power doc: "NVML_FI_DEV_POWER_AVERAGE / _INSTANT" field values exist for explicit control (header text).

## 10. OpenTelemetry for .NET

**Label: verified.**

- `OpenTelemetry` (SDK) **1.20.0** (2026-10-15); `OpenTelemetry.Exporter.Console` **1.20.0**; `OpenTelemetry.Exporter.OpenTelemetryProtocol` **1.20.0**; `OpenTelemetry.Extensions.Hosting` 1.20.0; `OpenTelemetry.Api` 1.20.0. All have a net10.0 asset in the nuspec. (Agent Framework Workflows depends on `OpenTelemetry.Api` 1.18.0 - resolved to 1.20.0 without conflict.)
- `Microsoft.Extensions.AI` provides `ChatClientBuilder.UseOpenTelemetry(ILoggerFactory? loggerFactory = null, string? sourceName = null, Action<OpenTelemetryChatClient>? configure = null)` (extension class `OpenTelemetryChatClientBuilderExtensions`; `OpenTelemetryChatClient.EnableSensitiveData`). Package: `Microsoft.Extensions.AI`, **not** Abstractions. Default `ActivitySource`/`Meter` name string in the DLL: `Experimental.Microsoft.Extensions.AI` (found by scanning the binary; override with `sourceName`).
- Measured span (console exporter, `AddSource("agentjoules.probe")`): `Activity.DisplayName: chat m`, kind Client, tags `gen_ai.operation.name=chat`, `gen_ai.request.model`, `gen_ai.provider.name=openai`, `server.address`, `server.port`, `openai.api.type=chat_completions`, `gen_ai.response.finish_reasons`, `gen_ai.response.id`, `gen_ai.response.model`, `gen_ai.usage.input_tokens=11`, `gen_ai.usage.output_tokens=2`; resource `telemetry.sdk.version 1.20.0`, schema `https://opentelemetry.io/schemas/1.44.0`. The `gen_ai` conventions are still "Experimental" in naming (inferred from the source name).
- Sources: https://learn.microsoft.com/en-us/dotnet/ai/ichatclient ("Use telemetry"), https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai, nuget.org, `probe/Program.cs`.

## 11. .NET 10 SDK facts

**Label: verified (measured on SDK 10.0.401; global.json semantics from Learn).**

- `global.json` (Learn, page date 2026-03-05): `version` needs the full number (no wildcards). Feature band = the hundreds digit, so 10.0.401 is band 10.0.4xx. `rollForward: "latestPatch"` = "latest installed patch level that matches the requested major, minor, and feature band with a patch level greater than or equal to the specified value" -> this is the pin to 10.0.4xx. I ran all probe projects with `{ "sdk": { "version": "10.0.401", "rollForward": "latestPatch" } }` successfully. Other values: `patch` (default when `version` is set), `feature`, `minor`, `major`, `latestFeature`, `latestMinor`, `latestMajor`, `disable`. `allowPrerelease` defaults to **true** outside Visual Studio, so set `"allowPrerelease": false` explicitly. `test.runner: "Microsoft.Testing.Platform"` available since .NET 10 SDK (item 6). Pitfall: with `latestPatch` the CI machine must have a 10.0.4xx SDK or the build fails; in GitHub Actions use `actions/setup-dotnet@v6` with `global-json-file: global.json` (README: V6 is current; it also documents `dotnet-version: 10.0.4xx` feature-band syntax). Whether the `windows-latest`/`ubuntu-latest` images already carry 10.0.401: **could not verify**.
- `AnalysisLevel` `latest-all` + `-warnaserror`: enabled additional CA rules that fail the build (CA5392, CA5393 in item 8). `EnforceCodeStyleInBuild=true` makes IDE rules run in the build, but only rules whose severity is configured (`.editorconfig` `csharp_style_namespace_declarations = file_scoped:warning` gave `warning IDE0161`). With `EnforceCodeStyleInBuild=false` and `--no-incremental`, IDE0161 count in the log = 0 (measured).
- `dotnet build -warnaserror` on that project: `--no-incremental` build exit **1** (`error IDE0161`); the **same command on an already-built project exited 0 without compiling** (warnings are only produced by CoreCompile, and an up-to-date build skips it). In CI, build from a clean checkout or add `--no-incremental`.
- `-tl:off`: forces the classic MSBuild logger. With output redirected, `dotnet build` without the switch also printed classic output (exit 0); `-tl:on` forced the terminal logger and wrote ANSI/OSC escape sequences into the log file. Use `-tl:off` for parseable CI logs.
- `dotnet format --verify-no-changes` (SDK 10.0.401, `fmt/`): dirty tree (block namespace + one-line method) exit **2**, printed `error WHITESPACE: Fix whitespace formatting...` lines plus the IDE0161 warning; after `dotnet format` (exit 0) a re-verify exits **0**; non-existent project path exits **1** with an unhandled `FileNotFoundException`. Learn says only "Terminates with a non zero exit code if any files would have been formatted" - the specific 0/1/2 values are measured, not documented. Default `--severity` is `warn`.
- Sources: https://learn.microsoft.com/en-us/dotnet/core/tools/global-json, https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-format, https://github.com/actions/setup-dotnet, `fmt/`, `probe/build.log`.

## 12. `TimeProvider`, `PeriodicTimer`, test helper

**Label: verified.**

- `System.TimeProvider` (since .NET 8) and `System.Threading.PeriodicTimer` (since .NET 6, with a `PeriodicTimer(TimeSpan, TimeProvider)` constructor since .NET 8) are in the net10.0 BCL; compiled with `-warnaserror`.
- Test helper: `Microsoft.Extensions.TimeProvider.Testing` **10.11.0** (2026-10-14), net10.0 asset, type `Microsoft.Extensions.Time.Testing.FakeTimeProvider`. Measured: `new PeriodicTimer(TimeSpan.FromSeconds(1), fake)` fired exactly 3 ticks after three `fake.Advance(TimeSpan.FromSeconds(1))` calls; `GetUtcNow()` = `1970-01-01T00:00:03Z`. In the probe I `await Task.Delay(50)` before each `Advance` so the waiter had registered; a deterministic test should await a signal from the consumer instead (inferred).

---

## Recommendations for the design

1. Pin `Microsoft.Extensions.AI` 10.11.0, `Microsoft.Extensions.AI.OpenAI` 10.11.0 (-> `OpenAI` 2.14.0), `Microsoft.Agents.AI.Workflows` 1.25.0 from NuGet; do not build against the fork (1.23.0, two releases behind). Same API shape compiled against 1.25.0.
2. Metering middleware: `builder.Use(getResponseFunc, getStreamingResponseFunc)` or a `DelegatingChatClient`; for streaming, wrap the enumerator in `try/finally`, read tokens from `UsageContent` and tolerate `Usage == null` (providers that omit the usage chunk).
3. The NVML energy counter is device-wide and fan-out agents overlap on one GPU: attribute energy per workflow run window, and report per-call figures only as an approximation (inferred; counter updates every 20-100 ms per secondary source, not measured by me).
4. NVML: `[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]`, `Span<byte>` for char buffers, `SetDllImportResolver` with `nvml.dll` / `libnvidia-ml.so.1`, treat `DllNotFoundException`, init rc != 0 and rc 3 (NOT_SUPPORTED, e.g. MIG, power) as "unavailable" - ubuntu-latest and windows-latest runners have no NVIDIA driver (inferred), so CI must exercise a fake NVML interface.
5. Schema: generate from a source-generated `JsonSerializerContext` with `NumberHandling.Strict` and `UnmappedMemberHandling.Disallow`, add `$schema` yourself, and diff the emitted schema in a unit test.
6. Tests: choose one runner mode for the repo. `xunit.v3` 4.0.2 + `Reqnroll.xunit.v3` 3.3.4 + `"test.runner": "Microsoft.Testing.Platform"` works on net10.0 and prints FQ names for xUnit tests; Reqnroll scenarios list by display name only. xunit v2 stays on VSTest and cannot share a MTP `global.json`.
7. CLI: `System.CommandLine` 2.0.13 (GA; net8.0 asset), `SetAction` + `Parse(args).InvokeAsync()`; avoid the 3.0.0-rc line.
8. CI: `global.json` = 10.0.401 + `latestPatch` + `allowPrerelease:false`; `actions/setup-dotnet@v6` with `global-json-file`; `dotnet build -warnaserror -tl:off --no-incremental`; `dotnet format --verify-no-changes` (exit 2 = needs formatting).
9. Agent Framework: executors declare `[SendsMessage]`/`[YieldsOutput]`; the `Workflows.Generators` package is optional (needed only for trim/AOT or `[MessageHandler]`); agent nodes need a `TurnToken`; fan-in is `AddFanInBarrierEdge`.
10. OpenTelemetry: `OpenTelemetry` / console / OTLP exporter 1.20.0 and `UseOpenTelemetry(sourceName: ...)`; register the same name with `AddSource`, and keep `EnableSensitiveData` off by default.
