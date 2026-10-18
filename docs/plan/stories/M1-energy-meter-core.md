# M1: Energy meter core

M1 builds the part of Agent Joules that touches the GPU: the NVML P/Invoke binding with its library resolver and lifecycle, a fake NVML for unit tests and CI, the energy-counter probe with the power-sampling fallback, the integration and net-energy math, the meter abstraction with its windows, baseline and process-wide gate, throttling detection, the `probe` command with `--json`, the Linux and WSL2 library path, and the GitHub Actions matrix that runs build, unit tests and `AJ probe --json` on runners without a GPU. When M1 ends the laptop has a committed probe file, every energy formula from the prompt is covered by a test that needs no GPU, and the CLI reports "no GPU" instead of crashing where NVML is absent.

## Dates and exit criteria

Dates: 2026-10-19 to 2026-10-21.

Exit criteria:
- [ ] `dotnet build -warnaserror -tl:off --no-incremental` exits 0 with `0 Warning(s)` and `0 Error(s)`; `dotnet test` exits 0 with 0 failed and the gated NVML integration tests reported as skipped
- [ ] `dotnet test --filter "Spec=REQ-003"`, `"Spec=REQ-004"`, `"Spec=REQ-005"`, `"Spec=REQ-006"`, `"Spec=EDGE-001"`, `"Spec=EDGE-002"`, `"Spec=EDGE-003"`, `"Spec=EDGE-005"`, `"Spec=EDGE-013"` and `"Spec=EDGE-014"` each show at least 1 passed and 0 failed, results recorded under `## Per-ID filter results` in docs/build-log.md
- [ ] On the laptop, `AGENTJOULES_INTEGRATION=1 dotnet test --filter "Spec=REQ-003"` passes (device name contains `3080`, power limit between 50 and 200 W)
- [ ] 21 samples of 50 000 mW at 0.0 to 2.0 s integrate to 100 J within 1 percent; a 3 s window at 60 W against a 20 W baseline gives gross 180 J, net 120 J, average 60 W, peak 60 W (PowerIntegrationTests, AttributionBaselineTests)
- [ ] `AJ probe --json` on the laptop exits 0 with `gpuPresent: true`, `driverVersion` equal to the `nvidia-smi` driver version and a boolean `energyCounterSupported`; the output is committed as `results/probe-<yyyyMMddTHHmmssZ>.json`
- [ ] The EDGE-001 container command exits 0 and prints JSON with `gpuPresent: false`; the output of `AJ probe` inside WSL2 (or the reason it was not run) is recorded under `## Probe and WSL2 notes`
- [ ] `.github/workflows/ci.yml` contains `windows-latest`, `ubuntu-latest`, `dotnet build -warnaserror -tl:off`, `dotnet test` and a probe step; `grep -n "secrets\." .github/workflows/ci.yml` exits 1
- [ ] `dotnet test --filter "Spec=REQ-006"` includes the exit-code value test (ExitCodeTests); the README exit-code table is written once by M6 AJ-131
- [ ] Commit messages match `^AJ-[0-9]{3}( (REQ|EDGE)-[0-9]{3})*:` (story id first, then the ids delivered): `AJ-023 REQ-003:`, `AJ-027 REQ-004 EDGE-002:`, `AJ-031 REQ-005:`, `AJ-037 REQ-006:`, `AJ-038 REQ-024:`, `AJ-026 EDGE-003:`, `AJ-030 EDGE-005:`, `AJ-028 EDGE-013:` and `AJ-036 EDGE-001 EDGE-014:` exist (made by the builder, nothing pushed); `git log <build-start>..HEAD --format=%s | grep -vcE "^AJ-[0-9]{3}( (REQ|EDGE)-[0-9]{3})*:"` prints 0

## Coverage

REQ-003, REQ-004, REQ-005, REQ-006, REQ-024; EDGE-001, EDGE-002, EDGE-003, EDGE-005, EDGE-013, EDGE-014.

## Dependencies and user actions

Dependencies: M0 complete (solution builds with 0 warnings, test projects exist, build log exists, redaction helper exists).

User actions from prompt section 12: none block M1. No hosted endpoint is called, so steps 5 and 6 (NVIDIA_API_KEY, NEBIUS_API_KEY) are not read except that AJ-034 registers any present key value with the redactor. Docker Desktop must be running for the container check in AJ-036 (recorded by M0 AJ-002); WSL2 with a .NET SDK is optional and its absence is recorded, not fatal. Design gaps found while writing these stories are listed in Risks.

## Stories

### AJ-020: Provide the NVML and time test doubles
Covers: REQ-003, REQ-004
User story: As a developer, I want an `INvmlApi` seam, a scripted fake behind it and a fake-time driver, so that every NVML and sampling test runs on a machine with no GPU, no driver and no wall-clock waiting.
Acceptance criteria:
1. `src/AgentJoules.Nvml/INvmlApi.cs` declares the members of design 3.1 (Init, Shutdown, GetHandleByIndex, GetPowerUsage, GetTotalEnergyConsumption, GetClocksEventReasons, GetMigMode, GetCount, GetName, GetUuid, GetVbios, GetComputeCapability, GetPowerLimit, GetDriverVersion) returning `NvmlReturn`; `dotnet build -warnaserror -tl:off` exits 0 with 0 warnings.
2. `NvmlReturn` has `Success = 0`, `Uninitialized = 1`, `InvalidArgument = 2`, `NotSupported = 3` (values copied from nvml.h by the builder; rc 3 was measured on the laptop for the MIG query) and a test asserts `(int)NvmlReturn.NotSupported == 3`.
3. `ScriptedNvmlApi` (tests/AgentJoules.TestSupport) returns power in milliwatts as a pure function of `FakeTimeProvider` time, returns the energy counter as the matching millijoule integral, returns `NotSupported` for the counter when its switch is off, returns scripted clock-event bits, a scripted MIG return code, and in missing-library mode throws `DllNotFoundException` from `Init`.
4. Calling any read member before `Init`, or after `Shutdown`, returns `Uninitialized` (the fake mimics NVML).
5. `FakeTimeDriver.RunUntilCompleteAsync(task, fake, step)` advances fake time until the task completes: a test awaits `Task.Delay(TimeSpan.FromSeconds(1), fake)` and finishes in under 200 ms of real time.
6. `tests/AgentJoules.Specs/Support/ScenarioWorld.cs` and `Hooks.cs` create a `FakeTimeProvider` per scenario and dispose it afterwards; `dotnet test --filter "Spec=REQ-003"` runs the first feature of this story.
BDD scenarios:
```gherkin
@unit @AJ-S020 @REQ-003 @REQ-004
Feature: Provide the NVML and time test doubles
  @positive
  Scenario: AJ-020 Scripted power follows the script over fake time
    Given a ScriptedNvmlApi that draws a constant 50 W and was initialised
    When the fake time advances to 1.0 s and power is read
    Then the return code is Success and the value is 50000 milliwatts
  @positive
  Scenario: AJ-020 The scripted counter integrates the scripted power
    Given a ScriptedNvmlApi at a constant 50 W with the counter supported
    When the fake time advances 2.0 s between two counter reads
    Then the difference is 100000 millijoules
  @negative
  Scenario: AJ-020 An unsupported counter returns rc 3
    Given a ScriptedNvmlApi with the counter switch off
    When the total energy counter is read
    Then the return code is NotSupported (3) and no value is produced
  @negative
  Scenario: AJ-020 Missing-library mode throws a typed exception
    Given a ScriptedNvmlApi in missing-library mode
    When Init is called
    Then a DllNotFoundException is thrown
  @negative
  Scenario: AJ-020 Reads before Init are rejected
    Given a ScriptedNvmlApi that was never initialised
    When power is read
    Then the return code is Uninitialized
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-020-T1 | Write the failing feature and ScriptedNvmlApiTests with trait Spec=REQ-003 | tests/AgentJoules.Specs/Features/Metering/AJ-020-nvml-test-doubles.feature, tests/AgentJoules.Specs/Steps/MeteringSteps.cs, tests/AgentJoules.UnitTests/ScriptedNvmlApiTests.cs | 0.5 | M0 AJ-012-T3 |
| AJ-020-T2 | Declare INvmlApi and NvmlReturn | src/AgentJoules.Nvml/INvmlApi.cs, src/AgentJoules.Nvml/Interop/NvmlReturn.cs | 0.5 | AJ-020-T1 |
| AJ-020-T3 | Implement ScriptedNvmlApi and add the project references to TestSupport | tests/AgentJoules.TestSupport/ScriptedNvmlApi.cs, tests/AgentJoules.TestSupport/AgentJoules.TestSupport.csproj | 0.75 | AJ-020-T2 |
| AJ-020-T4 | Implement FakeTimeDriver and the Specs support classes | tests/AgentJoules.TestSupport/FakeTimeDriver.cs, tests/AgentJoules.Specs/Support/ScenarioWorld.cs, tests/AgentJoules.Specs/Support/Hooks.cs | 0.5 | AJ-020-T3 |
| AJ-020-T5 | Record the Spec filter result | docs/build-log.md | 0.25 | AJ-020-T4 |
Test mapping: ScriptedNvmlApiTests (new class, tests the fake itself); Specs feature tests/AgentJoules.Specs/Features/Metering/AJ-020-nvml-test-doubles.feature.
Docs impact: docs/build-log.md `## Per-ID filter results`.
Definition of done:
- [ ] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [ ] `INvmlApi` lives in its own file INvmlApi.cs (the design names only NvmlApi.cs for the real implementation); this small addition is noted in docs/build-log.md
Demo relevance: no

### AJ-021: Bind NVML through LibraryImport with a DllImport resolver
Covers: REQ-003
User story: As a developer, I want NVML bound through source-generated P/Invoke with a resolver for the library name, so that the meter can call the driver on Windows without a hard-coded path and a missing driver surfaces as a typed exception.
Acceptance criteria:
1. `src/AgentJoules.Nvml/AssemblyAttributes.cs` contains `[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]` and `[assembly: DisableRuntimeMarshalling]`; the build reports no CA5392 and no CA5393.
2. `NvmlNative.cs` declares one `[LibraryImport("nvml", EntryPoint = "...")]` per function of the design 3.1 table (nvmlInit_v2, nvmlShutdown, nvmlSystemGetDriverVersion, nvmlDeviceGetCount_v2, nvmlDeviceGetHandleByIndex_v2, nvmlDeviceGetName, nvmlDeviceGetUUID, nvmlDeviceGetVbiosVersion, nvmlDeviceGetCudaComputeCapability, nvmlDeviceGetEnforcedPowerLimit, nvmlDeviceGetPowerUsage, nvmlDeviceGetTotalEnergyConsumption, nvmlDeviceGetMigMode, nvmlDeviceGetCurrentClocksEventReasons), `int` return, `nint` device handle, `Span<byte>` buffers decoded as UTF-8 up to the first NUL, no `StringMarshalling` (no SYSLIB1051). `grep -c "docs.nvidia.com/deploy/nvml-api" src/AgentJoules.Nvml/Interop/NvmlNative.cs` equals `grep -c "LibraryImport" src/AgentJoules.Nvml/Interop/NvmlNative.cs` (each declaration cites the NVML API reference).
3. The builder re-checks each signature and unit (milliwatts, millijoules) against the NVML API reference and records the check in docs/build-log.md; a mismatch is fixed in code, not in the test.
4. `NvmlLibraryCandidates.For(OSPlatform.Windows, programW6432)` returns `nvml.dll` then `<programW6432>\NVIDIA Corporation\NVSMI\nvml.dll`; the second entry is omitted when `programW6432` is null or empty.
5. `NvmlLibraryResolver` registers once through a static `Lazy<bool>` and `NativeLibrary.SetDllImportResolver(typeof(NvmlNative).Assembly, ...)` for the logical name `nvml`; the first candidate is tried with `NativeLibrary.TryLoad(name, assembly, DllImportSearchPath.System32, out h)`, the absolute path with `TryLoad(path, out h)`.
6. An injected-loader overload `TryLoadFirst(candidates, loader, out handle)` returns the first successful handle, returns false when every candidate fails, and never throws for a missing file.
7. `NvmlApi` (the real `INvmlApi`) maps native return codes to `NvmlReturn`; a missing driver makes the first native call throw `DllNotFoundException` (caught by the factory in AJ-022), nothing else.
BDD scenarios:
```gherkin
@unit @AJ-S021 @REQ-003
Feature: Bind NVML through LibraryImport with a DllImport resolver
  @positive
  Scenario: AJ-021 Windows candidates are tried in the documented order
    Given the platform is Windows and ProgramW6432 is "C:\Program Files"
    When the candidate list is built
    Then it is "nvml.dll" followed by "C:\Program Files\NVIDIA Corporation\NVSMI\nvml.dll"
  @positive
  Scenario: AJ-021 The first loadable candidate wins
    Given an injected loader that fails the first candidate and succeeds on the second
    When TryLoadFirst runs
    Then it returns true with the second candidate's handle
  @negative
  Scenario: AJ-021 No loadable candidate returns false without an exception
    Given an injected loader that fails every candidate
    When TryLoadFirst runs
    Then it returns false and no exception escapes
  @negative
  Scenario: AJ-021 A missing ProgramW6432 does not produce a relative path
    Given ProgramW6432 is empty
    When the Windows candidate list is built
    Then it contains only "nvml.dll"
  @negative
  Scenario: AJ-021 An unknown platform has no candidates
    Given the platform is neither Windows nor Linux
    When the candidate list is built
    Then it is empty
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-021-T1 | Write the failing feature and NvmlBindingTests for candidates and TryLoadFirst with trait Spec=REQ-003 | tests/AgentJoules.Specs/Features/Metering/AJ-021-nvml-binding.feature, tests/AgentJoules.UnitTests/NvmlBindingTests.cs | 0.5 | AJ-020-T5 |
| AJ-021-T2 | Add the assembly attributes and the LibraryImport declarations with API reference comments | src/AgentJoules.Nvml/AssemblyAttributes.cs, src/AgentJoules.Nvml/Interop/NvmlNative.cs | 1.5 | AJ-021-T1 |
| AJ-021-T3 | Implement the candidate list and the resolver with the injected-loader overload | src/AgentJoules.Nvml/Interop/NvmlLibraryCandidates.cs, src/AgentJoules.Nvml/Interop/NvmlLibraryResolver.cs | 0.75 | AJ-021-T1 |
| AJ-021-T4 | Implement NvmlApi over NvmlNative with return-code mapping | src/AgentJoules.Nvml/NvmlApi.cs | 1.0 | AJ-021-T2, AJ-021-T3 |
| AJ-021-T5 | Verify signatures against the NVML API reference and record the check | docs/build-log.md | 0.5 | AJ-021-T4 |
Test mapping: NvmlBindingTests (candidate and loader tests); Specs feature tests/AgentJoules.Specs/Features/Metering/AJ-021-nvml-binding.feature. Linux candidates are added by AJ-036.
Docs impact: docs/build-log.md `## Per-ID filter results` and the signature check note.
Definition of done:
- [ ] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [ ] every NVML signature carries a URL comment (REQ-003)
Demo relevance: no

### AJ-022: Open, read and dispose an NVML device with a typed result
Covers: REQ-003
User story: As a developer, I want a factory that returns either an open GPU telemetry source or a typed "unavailable" result with a reason, so that no NVML failure becomes an unhandled exception and the session is shut down exactly once.
Acceptance criteria:
1. Core declares `IGpuTelemetrySource` (Identity, EnergyCounterSupported, ReadPowerWatts, ReadEnergyMillijoules returning null when unsupported, ReadThrottleReasons), `GpuIdentity` (name, UUID, driver version, VBIOS, compute capability, power limit in watts, MIG mode `disabled|enabled|not-supported`), `ThrottleReasons`, `IGpuTelemetrySourceFactory` and `GpuOpenResult` (Available or Unavailable with a reason string).
2. With `ScriptedNvmlApi`, `Open(0)` returns Available and Identity holds the scripted name, UUID, driver version, VBIOS, compute capability and power limit (milliwatts divided by 1000).
3. A scripted `DllNotFoundException`, `EntryPointNotFoundException` or `BadImageFormatException` from any call yields Unavailable with a reason naming `nvml.dll` on Windows or `libnvidia-ml.so.1` on Linux; no exception escapes `Open`.
4. A non-zero `nvmlInit_v2` return yields Unavailable with reason `nvmlInit_v2 returned <rc>`; a device index at or above the count, or a negative index, yields Unavailable with reason `no device at index <n>`.
5. VBIOS or compute-capability queries returning a non-zero code leave those identity fields null (rendered `N/A` by the probe); MIG return code 3 maps to `not-supported` (measured on the laptop).
6. `Dispose` calls `Shutdown` exactly once, also when called twice or from two threads (Interlocked flag); a read after dispose throws `ObjectDisposedException`.
7. All reads share a private lock: eight threads times 100 power reads against the fake complete without exception and the fake records 800 reads (test `Reads_are_serialized_under_contention`).
BDD scenarios:
```gherkin
@unit @AJ-S022 @REQ-003
Feature: Open, read and dispose an NVML device with a typed result
  @positive
  Scenario: AJ-022 A scripted GPU opens with its identity
    Given a ScriptedNvmlApi with one device named "Scripted GPU 3080" and a 85 W power limit
    When the factory opens index 0
    Then the result is Available and the identity shows the name and a power limit of 85 W
  @positive
  Scenario: AJ-022 MIG not supported is reported as not-supported
    Given the scripted MIG query returns rc 3
    When the factory opens index 0
    Then the identity MIG mode is "not-supported"
  @positive
  Scenario: AJ-022 Dispose shuts NVML down exactly once
    Given an open source
    When Dispose is called twice
    Then Shutdown was called exactly once
  @negative
  Scenario: AJ-022 A missing library yields Unavailable naming the library
    Given the scripted API is in missing-library mode on Windows
    When the factory opens index 0
    Then the result is Unavailable, the reason contains "nvml.dll" and no exception escapes
  @negative
  Scenario: AJ-022 A failing init is reported with its return code
    Given nvmlInit_v2 returns rc 9
    When the factory opens index 0
    Then the result is Unavailable with reason "nvmlInit_v2 returned 9"
  @negative
  Scenario: AJ-022 An out-of-range index is Unavailable
    Given the device count is 1
    When the factory opens index 1
    Then the result is Unavailable with reason "no device at index 1"
  @negative
  Scenario: AJ-022 A read after dispose throws
    Given a disposed source
    When ReadPowerWatts is called
    Then an ObjectDisposedException is thrown
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-022-T1 | Write the failing feature and the factory and lifecycle tests | tests/AgentJoules.Specs/Features/Metering/AJ-022-open-dispose-device.feature, tests/AgentJoules.UnitTests/NvmlBindingTests.cs | 0.5 | AJ-021-T5 |
| AJ-022-T2 | Declare the Core GPU port types | src/AgentJoules.Core/Gpu/IGpuTelemetrySource.cs, src/AgentJoules.Core/Gpu/GpuIdentity.cs, src/AgentJoules.Core/Gpu/ThrottleReasons.cs, src/AgentJoules.Core/Gpu/IGpuTelemetrySourceFactory.cs, src/AgentJoules.Core/Gpu/GpuOpenResult.cs | 0.5 | AJ-022-T1 |
| AJ-022-T3 | Implement NvmlGpuTelemetrySource with the lock, identity and the dispose flag | src/AgentJoules.Nvml/NvmlGpuTelemetrySource.cs | 1.0 | AJ-022-T2 |
| AJ-022-T4 | Implement the factory with exception and return-code mapping | src/AgentJoules.Nvml/NvmlGpuTelemetrySourceFactory.cs | 0.75 | AJ-022-T3 |
| AJ-022-T5 | Record the Spec filter result | docs/build-log.md | 0.25 | AJ-022-T4 |
Test mapping: NvmlBindingTests (missing library yields typed Unavailable, MIG rc 3, lifecycle); Specs feature tests/AgentJoules.Specs/Features/Metering/AJ-022-open-dispose-device.feature.
Docs impact: docs/build-log.md `## Per-ID filter results`.
Definition of done:
- [ ] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [ ] no device handle is wrapped in a SafeHandle (handles are borrowed, design 3.1); the owned resource is the init and shutdown pair
Demo relevance: no

### AJ-023: Gate the NVML integration tests on AGENTJOULES_INTEGRATION
Covers: REQ-003
User story: As a developer, I want integration tests that run only when `AGENTJOULES_INTEGRATION=1`, so that CI and judges without a GPU see them as skipped while the laptop proves the real binding.
Acceptance criteria:
1. `IntegrationGate.SkipReason(string? value)` returns null only for exactly `"1"`, otherwise `Set AGENTJOULES_INTEGRATION=1`; `[IntegrationFact]` (defined once in TestSupport by M0 AJ-007) calls it. Values `0`, `true`, empty and unset all skip.
2. With the variable unset, `dotnet test --filter "Spec=REQ-003"` reports the NVML integration tests as skipped with that reason, never as passed.
3. On the laptop with `AGENTJOULES_INTEGRATION=1`, `dotnet test --filter "Spec=REQ-003" --logger "console;verbosity=normal"` passes: the device name contains `3080` and the enforced power limit is between 50 and 200 W. The test does not assert 94.62 W or any exact value (nvidia-smi reported 94.62 W, the NVML enforced limit differs, design C-11).
4. On a machine without NVML and with the variable set to `1`, the test fails with the Unavailable reason; it never skips silently.
5. `GpuAssertions.PowerLimitInRange(watts)` is unit tested with 85.059 (passes), 49.9 and 200.1 (fail).
6. `tests/AgentJoules.IntegrationTests/PlaceholderIntegrationTests.cs` is deleted (`grep -rn "placeholder" tests/AgentJoules.IntegrationTests` exits 1).
7. The counts of the gated and ungated runs are recorded under `## Test counts`.
BDD scenarios:
```gherkin
@integration @AJ-S023 @REQ-003
Feature: Gate the NVML integration tests on AGENTJOULES_INTEGRATION
  @positive
  Scenario: AJ-023 The laptop GPU is identified by the real binding
    Given AGENTJOULES_INTEGRATION is "1" and the NVIDIA driver is installed
    When the NVML integration test opens device 0
    Then the name contains "3080" and the power limit is between 50 and 200 W
  @positive @unit
  Scenario: AJ-023 The gate is open only for the value 1
    Given the variable value is "1"
    When IntegrationGate.SkipReason is evaluated
    Then it returns null
  @negative @unit
  Scenario: AJ-023 An unset variable skips the test with a reason
    Given the variable is unset
    When IntegrationGate.SkipReason is evaluated
    Then it returns "Set AGENTJOULES_INTEGRATION=1"
  @negative @unit
  Scenario: AJ-023 The value true does not open the gate
    Given the variable value is "true"
    When IntegrationGate.SkipReason is evaluated
    Then the test is skipped
  @negative
  Scenario: AJ-023 A missing driver with the gate open fails loudly
    Given AGENTJOULES_INTEGRATION is "1" and NVML cannot be loaded
    When the NVML integration test runs
    Then it fails with the Unavailable reason instead of skipping
  @negative @unit
  Scenario: AJ-023 A power limit outside 50 to 200 W fails the assertion
    Given a reported power limit of 300 W
    When PowerLimitInRange is evaluated
    Then it fails
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-023-T1 | Write the failing feature and IntegrationGateTests with trait Spec=REQ-003 | tests/AgentJoules.Specs/Features/Metering/AJ-023-integration-gate.feature, tests/AgentJoules.UnitTests/IntegrationGateTests.cs | 0.5 | AJ-022-T5 |
| AJ-023-T2 | Implement IntegrationGate and GpuAssertions and point the existing TestSupport IntegrationFactAttribute at the gate (no second attribute is created) | tests/AgentJoules.TestSupport/IntegrationGate.cs, tests/AgentJoules.TestSupport/GpuAssertions.cs, tests/AgentJoules.TestSupport/IntegrationFactAttribute.cs | 0.5 | AJ-023-T1 |
| AJ-023-T3 | Write NvmlIntegrationTests and delete the placeholder | tests/AgentJoules.IntegrationTests/NvmlIntegrationTests.cs, tests/AgentJoules.IntegrationTests/PlaceholderIntegrationTests.cs | 0.5 | AJ-023-T2 |
| AJ-023-T4 | Run the gated and ungated test commands on the laptop and record the counts | docs/build-log.md | 0.5 | AJ-023-T3 |
Test mapping: IntegrationGateTests (unit); NvmlIntegrationTests (gated, trait Spec=REQ-003); Specs feature tests/AgentJoules.Specs/Features/Metering/AJ-023-integration-gate.feature.
Docs impact: docs/build-log.md `## Test counts` and `## Per-ID filter results`.
Definition of done:
- [ ] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [ ] the gated run output on the laptop is quoted in docs/build-log.md with the UTC date
- [ ] REQ-003 commit made by the builder with a message starting `AJ-023 REQ-003:` (nothing pushed)
Demo relevance: no

### AJ-024: Define the Metric label object and the reading flags
Covers: REQ-005
User story: As a reader of a results file, I want every metric to carry exactly one label and a value that is null exactly when the label is N/A or not-reported, so that a missing number can never appear as zero.
Acceptance criteria:
1. `new Metric(...)` throws `ArgumentException` when the value is non-null for `N/A` or `not-reported`, when the value is null for `measured`, `estimated` or `model-judged`, when `reason` is empty for `N/A` or `not-reported`, and when the value is NaN or infinity.
2. Factories exist and set unit or currency: `Measured(v, unit)`, `Estimated(v, currency)`, `ModelJudged(v)`, `NotApplicable(reason)`, `NotReported(reason)`.
3. Serialized with System.Text.Json and a string-enum converter, the labels are exactly `measured`, `estimated`, `model-judged`, `N/A`, `not-reported`; `value` is always written, including `"value": null`; `reason`, `unit` and `currency` are omitted when null.
4. `ReadingFlags` is a `[Flags]` enum with the seven names of the vocabulary; `ReadingFlagNames.ToStrings(flags)` returns exactly `sampling-fallback`, `low-resolution`, `shared-window`, `baseline-unstable`, `below-baseline`, `throttled`, `partial-offload`; parsing an unknown string fails with a typed error.
5. A measured value of 0 is legal when it was measured; no factory or caller path produces 0 for a missing value (test: `NotReported("x").Value` is null).
BDD scenarios:
```gherkin
@unit @AJ-S024 @REQ-005
Feature: Define the Metric label object and the reading flags
  @positive
  Scenario: AJ-024 A measured metric keeps its number and unit
    Given a metric created with Measured(180, "J")
    When it is serialized
    Then the JSON has "value": 180, "label": "measured" and "unit": "J"
  @positive
  Scenario: AJ-024 A not-reported metric has a null value and a reason
    Given a metric created with NotReported("provider-omitted-usage")
    When it is serialized
    Then the JSON has "value": null, "label": "not-reported" and the reason
  @positive
  Scenario: AJ-024 Flags map to the vocabulary strings
    Given the flags BelowBaseline and Throttled
    When ToStrings is called
    Then the result is "below-baseline" and "throttled"
  @negative
  Scenario: AJ-024 An N/A metric with a number is rejected
    Given a metric constructed with label N/A and value 0
    When the constructor runs
    Then an ArgumentException is thrown
  @negative
  Scenario: AJ-024 A measured metric without a value is rejected
    Given a metric constructed with label measured and a null value
    When the constructor runs
    Then an ArgumentException is thrown
  @negative
  Scenario: AJ-024 An N/A metric without a reason is rejected
    Given a metric constructed with label N/A and an empty reason
    When the constructor runs
    Then an ArgumentException is thrown
  @negative
  Scenario: AJ-024 NaN is rejected
    Given a measured metric with value NaN
    When the constructor runs
    Then an ArgumentException is thrown
  @negative
  Scenario: AJ-024 An unknown flag string is rejected
    Given the string "overheated"
    When ReadingFlagNames.Parse runs
    Then it fails with a typed error
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-024-T1 | Write the failing feature and MetricTests with trait Spec=REQ-005 | tests/AgentJoules.Specs/Features/Metering/AJ-024-metric-labels.feature, tests/AgentJoules.UnitTests/MetricTests.cs | 0.5 | AJ-023-T4 |
| AJ-024-T2 | Implement MetricLabel and Metric with invariants and factories | src/AgentJoules.Core/Labels/MetricLabel.cs, src/AgentJoules.Core/Labels/Metric.cs | 0.75 | AJ-024-T1 |
| AJ-024-T3 | Implement ReadingFlags and the name mapping | src/AgentJoules.Core/Labels/ReadingFlags.cs | 0.5 | AJ-024-T1 |
| AJ-024-T4 | Record the Spec filter result | docs/build-log.md | 0.25 | AJ-024-T2, AJ-024-T3 |
Test mapping: MetricTests (new class); the schema-level rule "no N/A or not-reported metric has a numeric value" is added to SchemaValidationTests by M3. Specs feature tests/AgentJoules.Specs/Features/Metering/AJ-024-metric-labels.feature.
Docs impact: docs/build-log.md `## Per-ID filter results`.
Definition of done:
- [ ] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [ ] Metric is the only representation of a metric value in M1 code (design 3.2)
Demo relevance: no

### AJ-025: Integrate power samples into joules
Covers: REQ-004
User story: As a developer, I want the sampling integral computed from actual sample timestamps with the first and last sample held to the window edges, so that irregular timers do not distort the measured energy.
Acceptance criteria:
1. Test: constant 50 000 mW with samples at t = 0.0, 0.1, ..., 2.0 s (21 samples, edges included), window 0.0 to 2.0 s, gives 100 J within 1 percent.
2. Test: a linear ramp from 40 W at t = 0 to 60 W at t = 2 sampled at the irregular times 0.0, 0.05, 0.40, 1.00, 1.75, 2.0 gives 100 J within 1 percent (the trapezoid is exact for a linear ramp).
3. Test: samples at t = 0.2 and 0.8 s, both 50 W, window 0.0 to 1.0 s gives 50 J: the first sample is held from 0.0 to 0.2 and the last from 0.8 to 1.0.
4. Test: samples at t = 0, 0.1, 0.5, 0.6, 1.0 s with power 0, 10, 50, 60, 100 W (power = 100 x t) over window 0 to 1 s give 50 J within 1 percent, which differs from a nominal-interval integral, proving actual timestamps are used.
5. Non-monotonic or equal timestamps throw `ArgumentException`; a sample outside the window throws `ArgumentException`; fewer than two samples returns null (the caller labels the result not-reported, never 0).
BDD scenarios:
```gherkin
@unit @AJ-S025 @REQ-004
Feature: Integrate power samples into joules
  @positive
  Scenario: AJ-025 Constant 50 W over two seconds is 100 J
    Given 21 samples of 50000 mW at t = 0.0, 0.1, ..., 2.0 s
    When the window 0.0 to 2.0 s is integrated
    Then the result is 100 J within 1 percent
  @positive
  Scenario: AJ-025 Irregular timestamps give the analytic integral
    Given a 40 W to 60 W linear ramp sampled at 0.0, 0.05, 0.40, 1.00, 1.75 and 2.0 s
    When the window 0.0 to 2.0 s is integrated
    Then the result is 100 J within 1 percent
  @positive
  Scenario: AJ-025 The first and last samples are held to the window edges
    Given two 50 W samples at 0.2 s and 0.8 s
    When the window 0.0 to 1.0 s is integrated
    Then the result is 50 J
  @positive
  Scenario: AJ-025 Actual timestamps beat the nominal interval
    Given samples at 0, 0.1, 0.5, 0.6 and 1.0 s following power = 100 W/s x t
    When the window 0 to 1 s is integrated
    Then the result is 50 J within 1 percent
  @negative
  Scenario: AJ-025 Non-monotonic timestamps are rejected
    Given samples at 0.0, 0.3 and 0.2 s
    When the integrator runs
    Then an ArgumentException is thrown
  @negative
  Scenario: AJ-025 One sample yields no value
    Given a single sample in the window
    When the integrator runs
    Then the result is null, not 0
  @negative
  Scenario: AJ-025 A sample outside the window is rejected
    Given a sample at 2.5 s and the window 0.0 to 2.0 s
    When the integrator runs
    Then an ArgumentException is thrown
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-025-T1 | Write the failing feature and PowerIntegrationTests with trait Spec=REQ-004 | tests/AgentJoules.Specs/Features/Metering/AJ-025-integrate-power.feature, tests/AgentJoules.UnitTests/PowerIntegrationTests.cs | 0.5 | AJ-024-T4 |
| AJ-025-T2 | Implement PowerSample and PowerIntegrator | src/AgentJoules.Core/Energy/PowerSample.cs, src/AgentJoules.Core/Energy/PowerIntegrator.cs | 0.75 | AJ-025-T1 |
| AJ-025-T3 | Record the Spec filter result | docs/build-log.md | 0.25 | AJ-025-T2 |
Test mapping: PowerIntegrationTests; Specs feature tests/AgentJoules.Specs/Features/Metering/AJ-025-integrate-power.feature.
Docs impact: docs/build-log.md `## Per-ID filter results`.
Definition of done:
- [ ] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [ ] timestamps are seconds since meter start derived from `TimeProvider.GetTimestamp()`, never a nominal interval
Demo relevance: no

### AJ-026: Compute gross, net, average and peak energy per window
Covers: REQ-005, EDGE-003
User story: As a developer, I want one pure function that turns samples, an optional counter delta and a baseline into a reading, so that gross, net, average and peak values, flags and honesty labels are computed in exactly one place.
Acceptance criteria:
1. Test: a 3 s window at constant 60 W, 31 samples (10 Hz, edges included), baseline 20 W gives gross 180 J, net 120 J, average 60 W, peak 60 W, `sampleCount` equal to the supplied 31 and `sampleRateHz` 10, method `sampling`, all labelled `measured`.
2. Test: a 3 s window at 15 W against a 20 W baseline gives gross 45 J and net -15 J (kept, not clamped to zero) with flag `below-baseline`.
3. Test: method `counter` with a counter delta of 170 000 mJ gives gross 170 J while average, peak, `sampleCount` and `sampleRateHz` still come from the samples (REQ-005).
4. Test (EDGE-003): a 150 ms window at 10 Hz carries flag `low-resolution`; exactly 200 ms and 250 ms do not (strict "shorter than 2 / rate", compared in integer ticks).
5. Test: no baseline supplied gives net `not-reported` with reason `no-baseline` while gross stays measured.
6. Test: a negative counter delta gives gross and net `not-reported` with reason `counter-went-backwards`, never 0. The design is silent on counter resets; this default is recorded in docs/build-log.md for owner confirmation.
7. Test: fewer than two samples gives sampling gross, average and peak `not-reported` with reason `insufficient-samples`; with method `counter` only average and peak are not-reported.
8. `EnergyReading` holds gross, net, average and peak as `Metric`, duration, method (`counter` or `sampling`), `sampleCount`, `sampleRateHz`, flags and `throttleReasons`.
BDD scenarios:
```gherkin
@unit @AJ-S026 @REQ-005 @EDGE-003
Feature: Compute gross, net, average and peak energy per window
  @positive
  Scenario: AJ-026 Three seconds at 60 W against a 20 W baseline
    Given 31 samples of 60 W over a 3 s window and a baseline of 20 W
    When the reading is computed
    Then gross is 180 J, net is 120 J, average is 60 W, peak is 60 W and sampleCount is 31
  @positive
  Scenario: AJ-026 A window below baseline keeps the negative net value
    Given a 3 s window at 15 W and a baseline of 20 W
    When the reading is computed
    Then net is -15 J and the flags contain "below-baseline"
  @positive
  Scenario: AJ-026 Counter method takes gross from the counter only
    Given method counter, a counter delta of 170000 mJ and samples at 60 W for 3 s
    When the reading is computed
    Then gross is 170 J and average is 60 W from the samples
  @positive
  Scenario: AJ-026 A 150 ms window is flagged low-resolution
    Given a 150 ms window at 10 Hz sampling
    When the reading is computed
    Then the flags contain "low-resolution" and the reading is kept
  @negative
  Scenario: AJ-026 A 200 ms window is not low-resolution
    Given a 200 ms window at 10 Hz sampling
    When the reading is computed
    Then the flags do not contain "low-resolution"
  @negative
  Scenario: AJ-026 No baseline leaves net not-reported
    Given a window and no baseline
    When the reading is computed
    Then net is label not-reported with reason "no-baseline" and value null
  @negative
  Scenario: AJ-026 A counter that went backwards is not-reported
    Given a counter delta of -5000 mJ
    When the reading is computed
    Then gross and net are not-reported with reason "counter-went-backwards" and no value is 0
  @negative
  Scenario: AJ-026 Fewer than two samples is not-reported
    Given a window with one sample and method sampling
    When the reading is computed
    Then gross, average and peak are not-reported with reason "insufficient-samples"
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-026-T1 | Replace the AttributionBaselineTests placeholder with the failing reading tests and write the feature | tests/AgentJoules.UnitTests/AttributionBaselineTests.cs, tests/AgentJoules.Specs/Features/Metering/AJ-026-compute-reading.feature | 0.75 | AJ-025-T3 |
| AJ-026-T2 | Define EnergyReading | src/AgentJoules.Core/Energy/EnergyReading.cs | 0.5 | AJ-026-T1 |
| AJ-026-T3 | Implement ReadingCalculator.Compute | src/AgentJoules.Core/Energy/ReadingCalculator.cs | 1.0 | AJ-026-T2 |
| AJ-026-T4 | Record the Spec filter results for REQ-005 and EDGE-003 | docs/build-log.md | 0.25 | AJ-026-T3 |
Test mapping: AttributionBaselineTests (traits Spec=REQ-005 and Spec=EDGE-003); Specs feature tests/AgentJoules.Specs/Features/Metering/AJ-026-compute-reading.feature.
Docs impact: docs/build-log.md `## Per-ID filter results`, plus the counter-reset default.
Definition of done:
- [ ] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [ ] EDGE-003 commit by the builder with a message starting `AJ-026 EDGE-003:` once the flag test passes
Demo relevance: no
Conflict: EDGE-003 acceptance also requires "the rendered summary row for that reading contains `low-resolution`", which needs the summary renderer (REQ-016, M3). Proposed resolution: M1 delivers the flag in `EnergyReading` and its test; the M3 report story must assert that the rendered row contains `low-resolution` and reference EDGE-003 in its trait.

### AJ-027: Choose the counter or the sampling method and disclose the fallback
Covers: REQ-004, EDGE-002
User story: As a user, I want the meter to use the energy counter when the GPU supports it and to fall back to power sampling with a visible disclosure when it does not, so that the method behind every joule value is known.
Acceptance criteria:
1. The selector performs one `nvmlDeviceGetTotalEnergyConsumption` read at startup; return code 0 means supported, any non-zero code means unsupported with the reason text `counter-not-supported: NVML_ERROR_NOT_SUPPORTED` (or the numeric code when the name is unknown). The fake records exactly 1 counter read for the probe.
2. `Meter:Method=auto` with the counter supported gives method `counter`, `IsFallback` false.
3. `auto` with the counter unsupported gives method `sampling`, `IsFallback` true, and every reading from that meter carries flag `sampling-fallback` (test with `ScriptedNvmlApi` returning NotSupported).
4. `sampling` forced while the counter is supported gives method `sampling`, `IsFallback` false and no `sampling-fallback` flag.
5. `counter` forced while unsupported throws `MeasurementPreconditionException` (exit code 6) whose message names `Meter:Method=counter` and the reason; an unknown method string throws `ConfigurationException` (exit code 2).
6. `MeterCapabilities.Describe()` returns `Energy method: sampling (counter unsupported)` for the fallback and `Energy method: counter` otherwise; this is the startup console disclosure used by AJ-035.
7. `ExitCode` (0 Success, 1 Unexpected, 2 InvalidArguments, 3 MissingKey, 4 ModelNotFound, 5 Unreachable, 6 PreconditionFailed, 7 ContentDiffers), `AgentJoulesException(ExitCode, string)`, `ConfigurationException` and `MeasurementPreconditionException` exist in Core; the remaining subclasses of design 3.2 are added by the story that first throws them.
BDD scenarios:
```gherkin
@unit @AJ-S027 @REQ-004 @EDGE-002
Feature: Choose the counter or the sampling method and disclose the fallback
  @positive
  Scenario: AJ-027 Auto uses the counter when it is supported
    Given a fake NVML whose counter is supported
    When the method is selected with Meter:Method auto
    Then the method is "counter" and IsFallback is false
  @positive
  Scenario: AJ-027 Auto falls back to sampling and flags every reading
    Given a fake NVML returning NOT_SUPPORTED for the counter
    When the method is selected with Meter:Method auto and a reading is computed
    Then the method is "sampling", the reading flags contain "sampling-fallback" and Describe says "Energy method: sampling (counter unsupported)"
  @positive
  Scenario: AJ-027 Forced sampling is not a fallback
    Given a fake NVML whose counter is supported
    When the method is selected with Meter:Method sampling
    Then the method is "sampling" and the flags do not contain "sampling-fallback"
  @negative
  Scenario: AJ-027 Forced counter on an unsupported GPU is a precondition failure
    Given a fake NVML returning NOT_SUPPORTED for the counter
    When the method is selected with Meter:Method counter
    Then a MeasurementPreconditionException with exit code 6 is thrown and its message names "Meter:Method=counter"
  @negative
  Scenario: AJ-027 An unknown method is a configuration error
    Given Meter:Method is "turbo"
    When the method is selected
    Then a ConfigurationException with exit code 2 is thrown
  @negative
  Scenario: AJ-027 Any non-zero counter return code means unsupported
    Given the counter read returns rc 999
    When the method is selected with Meter:Method auto
    Then the method is "sampling" with reason "counter-not-supported" including the code
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-027-T1 | Write the failing feature and the method tests in PowerIntegrationTests with traits Spec=REQ-004 and Spec=EDGE-002 | tests/AgentJoules.Specs/Features/Metering/AJ-027-method-selection.feature, tests/AgentJoules.UnitTests/PowerIntegrationTests.cs | 0.5 | AJ-026-T4 |
| AJ-027-T2 | Add ExitCode and the exception types | src/AgentJoules.Core/Cli/ExitCode.cs, src/AgentJoules.Core/Cli/AgentJoulesException.cs | 0.5 | AJ-027-T1 |
| AJ-027-T3 | Implement MeterMethodSelector and MeterCapabilities | src/AgentJoules.Core/Energy/MeterMethodSelector.cs, src/AgentJoules.Core/Energy/IEnergyMeter.cs | 0.75 | AJ-027-T2 |
| AJ-027-T4 | Record the Spec filter results for REQ-004 and EDGE-002 | docs/build-log.md | 0.25 | AJ-027-T3 |
Test mapping: PowerIntegrationTests (design 6.1: auto fallback and NOT_SUPPORTED); Specs feature tests/AgentJoules.Specs/Features/Metering/AJ-027-method-selection.feature.
Docs impact: docs/build-log.md `## Per-ID filter results`.
Definition of done:
- [ ] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [ ] REQ-004 and EDGE-002 are delivered in one commit by the builder with a message starting `AJ-027 REQ-004 EDGE-002:` once their tests pass
Demo relevance: no
Conflict: EDGE-002 requires the fallback to be disclosed "in the report header and in the README" and, if the committed laptop probe shows `energyCounterSupported: false`, the README results block to contain the word `sampling`. Those surfaces are produced by REQ-016 and REQ-025 (M3, M6). Proposed resolution: M1 delivers the method, the per-reading flag, the console line and the probe output; the M3 report story and the M6 README story carry the header and README assertions and reference EDGE-002.

### AJ-028: Detect GPU throttling during a window
Covers: EDGE-013
User story: As a reader of a results file, I want readings taken while the GPU was power or thermal throttled to say so and name the reason, so that a slow, low-power step is not mistaken for an efficient one.
Acceptance criteria:
1. Test: a fake NVML reporting the power-cap bit (0x4) during a window gives a reading with flag `throttled` and `throttleReasons` containing `SwPowerCap`.
2. The mapping is `SwPowerCap` 0x4, `HwSlowdown` 0x8, `SwThermalSlowdown` 0x20, `HwThermalSlowdown` 0x40, `HwPowerBrakeSlowdown` 0x80, each setting `throttled`; the builder copies the values from nvml.h and records the check (values inferred from macro names in the design).
3. Test: only `GpuIdle` (0x1), applications-clocks (0x2) or sync-boost (0x10) bits give no `throttled` flag and an empty reason list.
4. Test: a reason present in only one sample of the window (an edge sample included) still flags the reading; reasons are the distinct union ordered by bit value.
5. `nvmlDeviceGetCurrentClocksEventReasons` is called first; on `EntryPointNotFoundException` the deprecated `nvmlDeviceGetCurrentClocksThrottleReasons` is used (test with a scripted missing-export switch); when both are missing the source reports throttling as unsupported.
6. When throttling is unsupported, `throttleReasons` is null (not an empty list), no flag is set, and no `throttled: false` is claimed; an empty list means "supported and nothing active".
7. On the laptop (`@integration`), whether the API is supported is recorded under `## Probe and WSL2 notes`; if unsupported, the note hands the sentence "throttle reasons are unavailable on this GPU" to the M4 methodology story.
BDD scenarios:
```gherkin
@unit @AJ-S028 @EDGE-013
Feature: Detect GPU throttling during a window
  @positive
  Scenario: AJ-028 A power cap during the window flags the reading
    Given a fake NVML reporting clock event bit 0x4 for the whole window
    When the reading is computed
    Then the flags contain "throttled" and throttleReasons contains "SwPowerCap"
  @positive
  Scenario: AJ-028 A thermal slowdown in one sample is enough
    Given bit 0x40 appears only in the last sample of the window
    When the reading is computed
    Then the flags contain "throttled" and throttleReasons contains "HwThermalSlowdown"
  @positive
  Scenario: AJ-028 The deprecated export is used when the new one is missing
    Given the fake has no nvmlDeviceGetCurrentClocksEventReasons export but has the throttle-reasons export
    When throttle reasons are read
    Then the bits are returned from the deprecated export
  @negative
  Scenario: AJ-028 An idle bit alone is not throttling
    Given the fake reports only the GpuIdle bit 0x1
    When the reading is computed
    Then the flags do not contain "throttled" and throttleReasons is an empty list
  @negative
  Scenario: AJ-028 An unsupported API yields null reasons and no flag
    Given both throttle exports are missing
    When the reading is computed
    Then throttleReasons is null, the flags do not contain "throttled" and nothing claims the GPU was not throttled
  @integration @negative
  Scenario: AJ-028 The laptop result is recorded either way
    Given AGENTJOULES_INTEGRATION is "1"
    When the throttle API is queried on the laptop
    Then docs/build-log.md records "supported" or "unsupported" with the UTC date
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-028-T1 | Write the failing feature and the throttle tests with trait Spec=EDGE-013 | tests/AgentJoules.Specs/Features/Metering/AJ-028-throttle-detection.feature, tests/AgentJoules.UnitTests/PowerIntegrationTests.cs | 0.5 | AJ-027-T4 |
| AJ-028-T2 | Implement the bit-to-reason mapping | src/AgentJoules.Core/Gpu/ThrottleReasons.cs | 0.5 | AJ-028-T1 |
| AJ-028-T3 | Read clock events with the deprecated-export fallback | src/AgentJoules.Nvml/NvmlApi.cs, src/AgentJoules.Nvml/NvmlGpuTelemetrySource.cs | 0.75 | AJ-028-T2 |
| AJ-028-T4 | Add the throttle flag and reasons to ReadingCalculator | src/AgentJoules.Core/Energy/ReadingCalculator.cs | 0.5 | AJ-028-T2 |
| AJ-028-T5 | Query the laptop and record the result | docs/build-log.md | 0.5 | AJ-028-T3, AJ-028-T4 |
Test mapping: PowerIntegrationTests (design 6.1: power-cap bit gives `throttled` plus reason); Specs feature tests/AgentJoules.Specs/Features/Metering/AJ-028-throttle-detection.feature.
Docs impact: docs/build-log.md `## Per-ID filter results` and `## Probe and WSL2 notes`.
Definition of done:
- [ ] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [ ] EDGE-013 commit by the builder with a message starting `AJ-028 EDGE-013:` (nothing pushed)
Demo relevance: no
Conflict: EDGE-013 acceptance says that if the API is unsupported on the laptop, `docs/methodology.md` says so. That file is owned by REQ-022 (M4). Proposed resolution: M1 records the laptop result in docs/build-log.md and the M4 methodology story copies the sentence when the result is "unsupported".

### AJ-029: Sample power and serve measurement windows
Covers: REQ-004, REQ-005
User story: As a developer, I want one process-wide meter that samples power in the background and hands out windows with exact edge samples, so that every window's energy is computed from the same buffer, with or without the counter.
Acceptance criteria:
1. `IEnergyMeter` exposes `Capabilities`, `StartAsync`, `MeasureBaselineAsync` (implemented by AJ-030) and `OpenWindow`; `IMeterWindow` exposes `Span` and `Close`; `WindowSpan` is a record of start and stop in seconds. `ComputeUnion` is not declared in M1; REQ-013 (M2) adds it, so no stub that throws exists.
2. `StartAsync` starts one background loop over `PeriodicTimer(TimeSpan.FromSeconds(1 / SampleRateHz), timeProvider)`; with `FakeTimeProvider` advanced by 2.0 s at 10 Hz, the fake NVML records exactly 21 power reads (one at start plus 20 ticks).
3. Sample timestamps are seconds since meter start from `TimeProvider.GetTimestamp()`; advancing fake time in irregular steps stores those actual times.
4. `OpenWindow` and `Close` each take an immediate sample and, when the method is `counter`, an immediate counter read, so a 150 ms window with no timer tick still has at least 2 samples.
5. A 3 s window at a scripted constant 60 W returns gross 180 J within 1 percent (sampling) and exactly 180 J (counter); average, peak, `sampleCount` and `sampleRateHz` always come from the samples.
6. Two overlapping windows opened at 0 s and 1 s on one meter each report their own energy (buffer slices), not a shared total (test `Overlapping_windows_report_own_energy`: 180 J and 120 J at 60 W).
7. `OpenWindow` before `StartAsync` throws `InvalidOperationException`; a second `Close` on the same window throws `InvalidOperationException`.
8. A power read that fails (the source throws) skips that sample, never records 0, and the loop continues; a window ending with fewer than two samples reports average and peak `not-reported` with reason `insufficient-samples` (AJ-026).
9. `DisposeAsync` cancels the timer wait and awaits the loop; after it, advancing fake time causes no further NVML reads; disposing twice is safe.
BDD scenarios:
```gherkin
@unit @AJ-S029 @REQ-004 @REQ-005
Feature: Sample power and serve measurement windows
  @positive
  Scenario: AJ-029 Ten hertz for two seconds takes 21 reads
    Given a started meter at 10 Hz over a fake NVML
    When the fake clock advances 2.0 s
    Then the fake recorded exactly 21 power reads
  @positive
  Scenario: AJ-029 A three second window at 60 W is 180 J
    Given a started meter and a scripted constant 60 W
    When a window is opened, the clock advances 3 s and the window is closed
    Then gross is 180 J within 1 percent and average is 60 W
  @positive
  Scenario: AJ-029 The counter supplies gross while samples supply average
    Given a started meter on the counter method
    When a window spans 3 s at 60 W
    Then gross is 180 J from the counter delta and sampleCount comes from the samples
  @positive
  Scenario: AJ-029 Overlapping windows report their own energy
    Given windows opened at 0 s and 1 s on one meter
    When both close at 3 s at a constant 60 W
    Then the first reports 180 J and the second reports 120 J
  @negative
  Scenario: AJ-029 Opening a window before start is rejected
    Given a meter that was not started
    When OpenWindow is called
    Then an InvalidOperationException is thrown
  @negative
  Scenario: AJ-029 Closing a window twice is rejected
    Given a closed window
    When Close is called again
    Then an InvalidOperationException is thrown
  @negative
  Scenario: AJ-029 A failed power read is skipped, not recorded as zero
    Given the fake throws on the third power read
    When the clock advances 1 s
    Then no sample with 0 W exists and the loop is still running
  @negative
  Scenario: AJ-029 Disposing the meter stops the sampling
    Given a started meter
    When the meter is disposed and the clock advances 5 s
    Then the fake records no further power reads
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-029-T1 | Write the failing feature and SampledEnergyMeterTests with traits Spec=REQ-004 and Spec=REQ-005 | tests/AgentJoules.Specs/Features/Metering/AJ-029-sampled-meter.feature, tests/AgentJoules.UnitTests/SampledEnergyMeterTests.cs | 0.75 | AJ-028-T5 |
| AJ-029-T2 | Declare IEnergyMeter, IMeterWindow and WindowSpan | src/AgentJoules.Core/Energy/IEnergyMeter.cs, src/AgentJoules.Core/Energy/WindowSpan.cs | 0.5 | AJ-029-T1 |
| AJ-029-T3 | Implement the sampling loop and the locked sample buffer | src/AgentJoules.Core/Energy/SampledEnergyMeter.cs | 1.5 | AJ-029-T2 |
| AJ-029-T4 | Implement windows with edge samples, counter marks and the reading call | src/AgentJoules.Core/Energy/SampledEnergyMeter.cs | 1.5 | AJ-029-T3 |
| AJ-029-T5 | Implement read-failure skipping and disposal | src/AgentJoules.Core/Energy/SampledEnergyMeter.cs | 0.75 | AJ-029-T4 |
| AJ-029-T6 | Record the Spec filter results | docs/build-log.md | 0.25 | AJ-029-T5 |
Test mapping: SampledEnergyMeterTests (new class); PowerIntegrationTests for the integral; Specs feature tests/AgentJoules.Specs/Features/Metering/AJ-029-sampled-meter.feature.
Docs impact: docs/build-log.md `## Per-ID filter results`.
Definition of done:
- [ ] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [ ] no `Task.Run` for I/O and no wall-clock wait in the tests (fake time only)
Demo relevance: yes: the per-window joules and watts that the demo prints per agent step come from this meter.

### AJ-030: Measure the idle baseline and flag an unstable one
Covers: REQ-005, EDGE-005
User story: As a user, I want the idle baseline measured with its mean and standard deviation and a warning when it is unstable, so that net joules are computed against a trustworthy floor and I know when they are not.
Acceptance criteria:
1. Test: 30 s of constant 20 W gives `meanW` 20, `stdDevW` 0, `seconds` 30 and no flags; `meanW` and `stdDevW` are `measured` with unit W.
2. Test (EDGE-005): samples alternating 20 W and 30 W give mean 25 W, population standard deviation 5 W, coefficient of variation 0.20 and flag `baseline-unstable` when the threshold is 0.10; gross and net are still computed for readings against that baseline (net uses 25 W).
3. The console warning is exactly `Warning: idle baseline unstable (CV 0.20 > 0.10)`; it contains the words `baseline` and `unstable`.
4. A coefficient of variation exactly equal to the threshold does not set the flag (comparison is strictly greater) (test `Cv_equal_to_threshold_is_not_flagged` asserts empty flags).
5. The standard deviation is the population one: samples 10 and 20 give 5, not 7.07 (test `Population_standard_deviation` asserts 5).
6. A mean of 0 W or less gives `meanW` `not-reported` with reason `baseline-mean-nonpositive`, no flag and a net value of `not-reported` downstream; fewer than two samples gives `not-reported` with reason `insufficient-samples`.
7. `SampledEnergyMeter.MeasureBaselineAsync(duration)` waits the duration on the injected `TimeProvider` (fake time in tests) while the sampler collects, then calls the calculator.
BDD scenarios:
```gherkin
@unit @AJ-S030 @REQ-005 @EDGE-005
Feature: Measure the idle baseline and flag an unstable one
  @positive
  Scenario: AJ-030 A steady 20 W baseline is stable
    Given 30 s of samples at a constant 20 W
    When the baseline is calculated with threshold 0.10
    Then mean is 20 W, standard deviation is 0 W and there are no flags
  @positive
  Scenario: AJ-030 An alternating baseline is flagged and warned about
    Given samples alternating 20 W and 30 W
    When the baseline is calculated with threshold 0.10
    Then mean is 25 W, standard deviation is 5 W, the flags contain "baseline-unstable" and the console line is "Warning: idle baseline unstable (CV 0.20 > 0.10)"
  @positive
  Scenario: AJ-030 The meter waits for the baseline duration on fake time
    Given a started meter at 10 Hz
    When MeasureBaselineAsync(30 s) runs and the fake clock advances 30 s
    Then the baseline seconds is 30 and no real time was spent waiting
  @negative
  Scenario: AJ-030 A coefficient of variation equal to the threshold is not flagged
    Given a baseline whose CV is exactly 0.10
    When the baseline is calculated with threshold 0.10
    Then the flags are empty
  @negative
  Scenario: AJ-030 A zero-mean baseline is not-reported
    Given samples that are all 0 W
    When the baseline is calculated
    Then meanW is not-reported with reason "baseline-mean-nonpositive" and no CV is divided by zero
  @negative
  Scenario: AJ-030 One baseline sample is not enough
    Given a single baseline sample
    When the baseline is calculated
    Then meanW is not-reported with reason "insufficient-samples"
  @negative
  Scenario: AJ-030 The population standard deviation is used
    Given samples 10 W and 20 W
    When the baseline is calculated
    Then the standard deviation is 5 W, not 7.07 W
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-030-T1 | Write the failing feature and the baseline tests with traits Spec=REQ-005 and Spec=EDGE-005 | tests/AgentJoules.Specs/Features/Metering/AJ-030-baseline.feature, tests/AgentJoules.UnitTests/AttributionBaselineTests.cs | 0.5 | AJ-029-T6 |
| AJ-030-T2 | Implement BaselineCalculator and BaselineResult | src/AgentJoules.Core/Energy/BaselineCalculator.cs | 0.5 | AJ-030-T1 |
| AJ-030-T3 | Implement MeasureBaselineAsync on SampledEnergyMeter | src/AgentJoules.Core/Energy/SampledEnergyMeter.cs | 0.75 | AJ-030-T2 |
| AJ-030-T4 | Implement the console warning in ConsoleReporter | src/AgentJoules.Cli/Output/ConsoleReporter.cs | 0.5 | AJ-030-T2 |
| AJ-030-T5 | Record the Spec filter results for REQ-005 and EDGE-005 | docs/build-log.md | 0.25 | AJ-030-T3, AJ-030-T4 |
Test mapping: AttributionBaselineTests (design 6.1: alternating 20 W and 30 W baseline gives `baseline-unstable` and the console line); Specs feature tests/AgentJoules.Specs/Features/Metering/AJ-030-baseline.feature.
Docs impact: docs/build-log.md `## Per-ID filter results`.
Definition of done:
- [ ] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [ ] EDGE-005 commit by the builder with a message starting `AJ-030 EDGE-005:` (nothing pushed)
Demo relevance: yes: the demo shows the measured idle baseline before the first agent step.

### AJ-031: Provide the deterministic fake meter
Covers: REQ-005
User story: As a developer, I want a deterministic `IEnergyMeter` that computes readings from a power function without any background thread, so that middleware and report tests in later milestones get exact joules with no timing noise.
Acceptance criteria:
1. `DeterministicEnergyMeter` (namespace `AgentJoules.Core.Energy.Testing`) takes a `TimeProvider`, a `Func<double, double>` watts-at-time function, a sample rate and a counter-support switch plus a method setting, and uses the same `ReadingCalculator` and `MeterMethodSelector` as the real meter.
2. Test (REQ-005): a 3 s window at constant 60 W with a 20 W baseline gives gross 180 J, net 120 J, average 60 W, peak 60 W and `sampleCount` equal to `LastWindowSampleCount` (31 at 10 Hz).
3. Test: a window at 15 W against a 20 W baseline gives a negative net value with flag `below-baseline`.
4. Synthesized samples sit on the nominal grid inside the window plus both edges, with no duplicate when the stop lies on the grid: a 150 ms window at 10 Hz has 3 samples (0, 0.1, 0.15 s) and the flag `low-resolution`.
5. `MeasureBaselineAsync(30 s)` completes immediately (no fake or real wait); a watts function alternating 20 W and 30 W on the grid yields CV 0.20 and `baseline-unstable`.
6. Counter switch off with method `auto` gives method `sampling` and flag `sampling-fallback`; method `counter` with the switch off throws `MeasurementPreconditionException`.
7. `Close` twice throws `InvalidOperationException`, like the real meter.
BDD scenarios:
```gherkin
@unit @AJ-S031 @REQ-005
Feature: Provide the deterministic fake meter
  @positive
  Scenario: AJ-031 The reference window yields the documented numbers
    Given a deterministic meter at constant 60 W, 10 Hz and a 20 W baseline
    When a window is opened, fake time advances 3 s and the window is closed
    Then gross is 180 J, net is 120 J, average is 60 W, peak is 60 W and sampleCount equals LastWindowSampleCount (31)
  @positive
  Scenario: AJ-031 A window under the baseline has negative net energy
    Given a deterministic meter at constant 15 W and a 20 W baseline
    When a 3 s window is closed
    Then net is -15 J and the flags contain "below-baseline"
  @positive
  Scenario: AJ-031 A short window keeps both edge samples
    Given a deterministic meter at 10 Hz
    When a 150 ms window is closed
    Then sampleCount is 3 and the flags contain "low-resolution"
  @negative
  Scenario: AJ-031 The baseline does not wait
    Given a deterministic meter with an alternating 20 W and 30 W function
    When MeasureBaselineAsync(30 s) is awaited without advancing time
    Then it completes and the flags contain "baseline-unstable"
  @negative
  Scenario: AJ-031 Forced counter without counter support is a precondition failure
    Given a deterministic meter with counter support off and method counter
    When the meter is constructed
    Then a MeasurementPreconditionException is thrown
  @negative
  Scenario: AJ-031 A window cannot be closed twice
    Given a closed window
    When Close is called again
    Then an InvalidOperationException is thrown
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-031-T1 | Write the failing feature and the REQ-005 acceptance tests against the fake | tests/AgentJoules.Specs/Features/Metering/AJ-031-deterministic-meter.feature, tests/AgentJoules.UnitTests/AttributionBaselineTests.cs | 0.5 | AJ-030-T5 |
| AJ-031-T2 | Implement DeterministicEnergyMeter | src/AgentJoules.Core/Energy/DeterministicEnergyMeter.cs | 1.0 | AJ-031-T1 |
| AJ-031-T3 | Record the Spec filter result | docs/build-log.md | 0.25 | AJ-031-T2 |
Test mapping: AttributionBaselineTests (exact REQ-005 acceptance: gross 180, net 120, 60 W, 60 W, sample count equals the fake's count; 15 W against 20 W); Specs feature tests/AgentJoules.Specs/Features/Metering/AJ-031-deterministic-meter.feature.
Docs impact: docs/build-log.md `## Per-ID filter results`.
Definition of done:
- [ ] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [ ] REQ-005 commit by the builder with a message starting `AJ-031 REQ-005:` (nothing pushed)
Demo relevance: no

### AJ-032: Share one meter gate per process
Covers: REQ-005
User story: As a developer, I want one process-wide gate that serializes measurement windows or lets them overlap, so that "one agent at a time" is enforced in one place and concurrent mode is a configuration choice, not a code path.
Acceptance criteria:
1. `IMeterGate.AcquireAsync(CancellationToken)` returns a `ValueTask<IAsyncDisposable>`; `SerializedMeterGate` wraps one `SemaphoreSlim(1, 1)`, `NoOpMeterGate` returns a cached completed releaser.
2. Test: with the serialized gate, the second `AcquireAsync` stays incomplete until the first releaser is disposed, then completes.
3. Test: cancelling a waiting `AcquireAsync` throws `OperationCanceledException` and leaves the gate usable (the next acquire after the holder releases succeeds, count not leaked).
4. Test: disposing a releaser twice releases once (a third waiter is not admitted early).
5. Test: with `NoOpMeterGate`, two simultaneous acquires both complete immediately.
6. Test: three tasks each acquiring the serialized gate around a `DeterministicEnergyMeter` window produce `WindowSpan`s that do not overlap.
7. The gate lives in `src/AgentJoules.Metering` and references only Core.
BDD scenarios:
```gherkin
@unit @AJ-S032 @REQ-005
Feature: Share one meter gate per process
  @positive
  Scenario: AJ-032 The second acquire waits for the first release
    Given a serialized gate held by task A
    When task B calls AcquireAsync
    Then task B is still waiting until task A disposes its releaser, and then completes
  @positive
  Scenario: AJ-032 Serialized windows never overlap
    Given three tasks that each acquire the serialized gate around a deterministic window
    When all tasks finish
    Then the three window spans are pairwise disjoint
  @positive
  Scenario: AJ-032 The no-op gate admits everyone at once
    Given a no-op gate
    When two tasks acquire it at the same time
    Then both complete immediately
  @negative
  Scenario: AJ-032 Cancelling a waiter does not leak the gate
    Given a serialized gate held by task A and a waiting task B that is cancelled
    When task A releases and task C acquires
    Then task B ended with OperationCanceledException and task C acquired successfully
  @negative
  Scenario: AJ-032 A double release admits only one waiter
    Given a serialized gate held by task A with tasks B and C waiting
    When task A disposes its releaser twice
    Then only task B is admitted
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-032-T1 | Write the failing feature and MeterGateTests with trait Spec=REQ-005 | tests/AgentJoules.Specs/Features/Metering/AJ-032-meter-gate.feature, tests/AgentJoules.UnitTests/MeterGateTests.cs | 0.5 | AJ-031-T3 |
| AJ-032-T2 | Declare IMeterGate and implement NoOpMeterGate | src/AgentJoules.Metering/IMeterGate.cs, src/AgentJoules.Metering/NoOpMeterGate.cs | 0.5 | AJ-032-T1 |
| AJ-032-T3 | Implement SerializedMeterGate with idempotent release | src/AgentJoules.Metering/SerializedMeterGate.cs | 0.75 | AJ-032-T2 |
| AJ-032-T4 | Record the Spec filter result | docs/build-log.md | 0.25 | AJ-032-T3 |
Test mapping: MeterGateTests (new class; ExecutionModeTests for `--mode` belongs to M2); Specs feature tests/AgentJoules.Specs/Features/Metering/AJ-032-meter-gate.feature.
Docs impact: docs/build-log.md `## Per-ID filter results`.
Definition of done:
- [ ] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [ ] the M2 story for REQ-013 reuses these types and adds the mode wiring, `shared-window` flags and union totals
Demo relevance: no
Conflict: the gate is specified by REQ-013 (serialized and concurrent modes), which the milestone table assigns to M2, while this milestone's brief asks for "the meter abstraction and the process-wide meter gate". Proposed resolution: M1 delivers `IMeterGate`, `SerializedMeterGate`, `NoOpMeterGate` and their tests under REQ-005 (readings from serialized windows never overlap); M2 keeps REQ-013 and its acceptance (`--mode`, `shared-window`, union totals, ExecutionModeTests).

### AJ-033: Bind and validate the GPU, meter and baseline options
Covers: REQ-004
User story: As a user, I want the GPU index, meter method, sample rate and baseline settings read from `config/appsettings.json` and overridable by environment variables, with invalid values rejected at start, so that a typo cannot silently change what is measured.
Acceptance criteria:
1. `config/appsettings.json` has the root section `AgentJoules` with `Gpu:Index` 0, `Meter:Method` "auto", `Meter:SampleRateHz` 10, `Meter:AllowBelowMinRate` false, `Baseline:Seconds` 30, `Baseline:MaxCoefficientOfVariation` 0.10; it parses as JSON in a unit test, and `config/.gitkeep` is deleted in the same change.
2. Options are mutable `sealed class` types (`GpuOptions`, `MeterOptions`, `BaselineOptions`, `AgentJoulesOptions`) with `{ get; set; }` properties, no `required`, no `init` and no positional record (with the binding generator `required` fails CS9035 and `init` is never bound, design 3.2); the generator binds them; absent keys give the same defaults as the file. Proof: `grep -rnE "required|init;|record " src/AgentJoules.Core/Configuration` exits 1.
3. A test passes `AGENTJOULES__Gpu__Index=7` through the isolated in-memory `IConfiguration` of the CLI harness (the keys are normalised the way the environment provider does) and asserts `Gpu:Index` is 7 (`ConfigValidationTests.EnvironmentOverrideBinds`); no test sets a process environment variable (design 6.2).
4. A sample rate below 10 Hz without `Meter:AllowBelowMinRate` fails validation with a message naming both keys (exit code 2 through `OptionsValidationException`); with `AllowBelowMinRate` true, 5 Hz is valid; a rate of 0 or less is invalid even with the override.
5. `Meter:Method` outside `auto|counter|sampling` (case-insensitive) fails validation; `Gpu:Index` below 0, `Baseline:Seconds` of 0 or less and `Baseline:MaxCoefficientOfVariation` of 0 or less fail validation. These ranges are assumed (the prompt gives only the 10 Hz minimum) and recorded in docs/build-log.md.
6. `Directory.Packages.props` gains the Options packages the design does not list (Microsoft.Extensions.Options and Microsoft.Extensions.Configuration.Binder at 10.0.12, the Hosting 10.0.12 family; the builder confirms each version exists on NuGet) and each addition is recorded under `## Version deviations and additions` as an addition, not a deviation.
7. The validator is `FlatSectionsValidator : IValidateOptions<AgentJoulesOptions>` (design name), a source-generated `[OptionsValidator]` partial, plus a hand-written `CrossFieldValidator` for the rate rule; `grep -rn "class FlatSectionsValidator" src` prints 1 hit.
BDD scenarios:
```gherkin
@unit @AJ-S033 @REQ-004
Feature: Bind and validate the GPU, meter and baseline options
  @positive
  Scenario: AJ-033 The shipped file binds to the documented defaults
    Given config/appsettings.json
    When the options are bound
    Then Gpu:Index is 0, Meter:Method is "auto", Meter:SampleRateHz is 10, AllowBelowMinRate is false, Baseline:Seconds is 30 and the CV limit is 0.10
  @positive
  Scenario: AJ-033 An environment-style key overrides the file
    Given the isolated configuration holds AGENTJOULES__Gpu__Index "7"
    When the options are bound
    Then Gpu:Index is 7 and no process environment variable was set
  @positive
  Scenario: AJ-033 Five hertz is allowed only with the override
    Given Meter:SampleRateHz is 5 and Meter:AllowBelowMinRate is true
    When the options are validated
    Then validation succeeds
  @negative
  Scenario: AJ-033 Five hertz without the override is rejected with exit code 2
    Given Meter:SampleRateHz is 5 and Meter:AllowBelowMinRate is false
    When the CLI starts with these options
    Then the exit code is 2 and the message names Meter:SampleRateHz and Meter:AllowBelowMinRate
  @negative
  Scenario: AJ-033 An unknown method is rejected
    Given Meter:Method is "turbo"
    When the options are validated
    Then validation fails naming Meter:Method
  @negative
  Scenario: AJ-033 A negative GPU index is rejected
    Given Gpu:Index is -1
    When the options are validated
    Then validation fails naming Gpu:Index
  @negative
  Scenario: AJ-033 A zero baseline window is rejected
    Given Baseline:Seconds is 0
    When the options are validated
    Then validation fails naming Baseline:Seconds
  @negative
  Scenario: AJ-033 A zero rate is rejected even with the override
    Given Meter:SampleRateHz is 0 and Meter:AllowBelowMinRate is true
    When the options are validated
    Then validation fails naming Meter:SampleRateHz
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-033-T1 | Write the failing feature and the options tests in ConfigValidationTests with trait Spec=REQ-004 | tests/AgentJoules.Specs/Features/Configuration/AJ-033-meter-options.feature, tests/AgentJoules.UnitTests/ConfigValidationTests.cs | 0.75 | AJ-032-T4 |
| AJ-033-T2 | Add and verify the Options package pins and record them as additions | Directory.Packages.props, src/AgentJoules.Core/AgentJoules.Core.csproj, docs/build-log.md | 0.25 | AJ-033-T1 |
| AJ-033-T3 | Implement the options classes with get and set properties | src/AgentJoules.Core/Configuration/AgentJoulesOptions.cs, src/AgentJoules.Core/Configuration/GpuOptions.cs, src/AgentJoules.Core/Configuration/MeterOptions.cs, src/AgentJoules.Core/Configuration/BaselineOptions.cs | 0.5 | AJ-033-T2 |
| AJ-033-T4 | Implement FlatSectionsValidator and the cross-field validator | src/AgentJoules.Core/Configuration/FlatSectionsValidator.cs, src/AgentJoules.Core/Configuration/CrossFieldValidator.cs | 0.75 | AJ-033-T3 |
| AJ-033-T5 | Write config/appsettings.json and delete config/.gitkeep | config/appsettings.json, config/.gitkeep | 0.25 | AJ-033-T3 |
| AJ-033-T6 | Record the Spec filter result and the assumed ranges | docs/build-log.md | 0.25 | AJ-033-T4, AJ-033-T5 |
Test mapping: ConfigValidationTests (design 6.1: sample rate 5 Hz without override gives exit 2); Specs feature tests/AgentJoules.Specs/Features/Configuration/AJ-033-meter-options.feature.
Docs impact: config/appsettings.json (sample and default file); docs/build-log.md.
Definition of done:
- [ ] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [ ] no model ID, base URL or key appears in config/appsettings.json (those arrive in M2)
Demo relevance: no

### AJ-034: Build the CLI foundation with exit codes and redacted output
Covers: REQ-006
User story: As a user, I want a CLI that returns documented exit codes, prints errors as one redacted line and keeps stdout for command output, so that scripts and CI can rely on `AJ probe --json` and no key can leak through an error message.
Acceptance criteria:
1. `src/AgentJoules.Cli/Program.cs` is the single statement `return await CliApplication.RunAsync(args, CancellationToken.None);` (the M0 placeholder is replaced).
2. A parse error prints the errors and returns 2: `AJ probe --bogus` exits 2 (System.CommandLine returns 1 by default, design C-04). Running with no command is a parse error with usage on stderr, exit 2 (assumed, the design is silent).
3. `AgentJoulesException` subclasses return their own code (`MeasurementPreconditionException` returns 6, `ConfigurationException` 2); `OptionsValidationException` returns 2; any other exception prints `error: <message> (<exception type>)` on stderr and returns 1, with the stack trace only at Debug level.
4. Console.SetOut and Console.SetError are replaced by `RedactingTextWriter` (M0 AJ-010) before the host is built; a test throws an exception whose message contains a runtime-built `"nvapi-" + "abcdef1234567890"` and asserts stderr shows `nvapi-****` and not the suffix.
5. The redactor registers the values of NVIDIA_API_KEY, NEBIUS_API_KEY and NGC_API_KEY found in configuration (environment or user-secrets) when present (test `Registered_configuration_keys_are_redacted` builds the values at runtime and asserts the masked output).
6. The host is built with `HostApplicationBuilder` and `DisableDefaults`; configuration sources are exactly: the JSON file (`--config` or `config/appsettings.json`), optional user-secrets, environment variables; options use `BindConfiguration("AgentJoules")` and `ValidateOnStart()`, triggered by `StartAsync`.
7. With `CliHostOverrides` (`IncludeProcessEnvironment` false, `IncludeUserSecrets` false) neither environment variables nor user-secrets are sources: a test with `NVIDIA_API_KEY` set in the process still sees an empty key through the harness.
8. Logging goes to stderr at every level (`LogToStandardErrorThreshold = Trace`); stdout carries only command output; `AJ --help` and `AJ probe --help` exit 0.
9. Host shutdown (Ctrl+C, SIGTERM, SIGHUP) arrives through `IHostApplicationLifetime.ApplicationStopping` and cancels the token handed to the command action; the host registers a no-op `IHostLifetime` (so the console lifetime does not race System.CommandLine) and `InvocationConfiguration.ProcessTerminationTimeout` is 15 s (design 3.6). `ExitCodeMapper` maps an `OperationCanceledException` caused by that shutdown to exit 5 (design 3.4 step 3 and 5.2 EDGE-016: in-flight steps become `incomplete`, `run.status` `incomplete`; the run orchestrator that writes them is built in M2 and M5, M1 delivers and tests the signal path and the mapping with a fake long-running command). An `OperationCanceledException` not caused by shutdown still maps to 1. Proof: `ExitCodeTests` cases `Shutdown_cancels_command_and_exits_5` and `Unrelated_cancellation_exits_1`; a test reads `ProcessTerminationTimeout` and asserts 15 s.
BDD scenarios:
```gherkin
@unit @AJ-S034 @REQ-006
Feature: Build the CLI foundation with exit codes and redacted output
  @positive
  Scenario: AJ-034 Help exits with code 0
    Given the CLI harness with no overrides
    When I run "probe --help"
    Then the exit code is 0 and stdout lists the probe options
  @positive
  Scenario: AJ-034 A precondition failure maps to exit code 6
    Given a service override that throws MeasurementPreconditionException
    When I run "probe"
    Then the exit code is 6 and stderr has one line starting "error:"
  @positive
  Scenario: AJ-034 A configuration failure maps to exit code 2
    Given Meter:SampleRateHz is 5 without the override in the in-memory configuration
    When I run "probe"
    Then the exit code is 2
  @negative
  Scenario: AJ-034 An unknown option exits with code 2, not 1
    Given the CLI harness
    When I run "probe --bogus"
    Then the exit code is 2
  @negative
  Scenario: AJ-034 An unexpected exception exits with code 1
    Given a service override that throws InvalidOperationException
    When I run "probe"
    Then the exit code is 1 and stderr shows "error: <message> (InvalidOperationException)"
  @negative
  Scenario: AJ-034 A key inside an error message is redacted
    Given a service override that throws an exception whose message contains a runtime-built nvapi key
    When I run "probe"
    Then stderr contains "nvapi-****" and does not contain the key suffix
  @negative
  Scenario: AJ-034 The laptop's real keys never reach a test
    Given NVIDIA_API_KEY is set in the process environment
    When the harness runs with the default overrides
    Then the configuration seen by the command has no NVIDIA_API_KEY value
  @positive
  Scenario: AJ-034 Host shutdown cancels the running command and exits 5
    Given a fake long-running command that waits on its cancellation token
    When the host raises ApplicationStopping (the Ctrl+C, SIGTERM and SIGHUP path)
    Then the command sees the cancellation, the exit code is 5 and ProcessTerminationTimeout is 15 s
  @negative
  Scenario: AJ-034 A cancellation that is not a shutdown exits 1
    Given a command that throws OperationCanceledException with no shutdown requested
    When I run it through the harness
    Then the exit code is 1 and stderr has one "error:" line
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-034-T1 | Write the failing feature, ExitCodeTests (codes 1, 2, 6) and the CliHarness and TempWorkspace | tests/AgentJoules.Specs/Features/Cli/AJ-034-cli-foundation.feature, tests/AgentJoules.UnitTests/ExitCodeTests.cs, tests/AgentJoules.TestSupport/CliHarness.cs, tests/AgentJoules.TestSupport/TempWorkspace.cs | 0.75 | AJ-033-T6 |
| AJ-034-T2 | Implement CliApplication with parse-error handling and CliHostOverrides | src/AgentJoules.Cli/CliApplication.cs | 1.0 | AJ-034-T1 |
| AJ-034-T3 | Implement the host factory with sources, options binding and ValidateOnStart | src/AgentJoules.Cli/Hosting/AgentJoulesHostFactory.cs | 1.0 | AJ-034-T2 |
| AJ-034-T4 | Implement ExitCodeMapper, redacting writers, stderr logging and Log.cs | src/AgentJoules.Cli/ExitCodeMapper.cs, src/AgentJoules.Cli/Log.cs | 0.75 | AJ-034-T3 |
| AJ-034-T4a | Wire ApplicationStopping to the command token, the no-op IHostLifetime, the 15 s ProcessTerminationTimeout and the exit 5 mapping | src/AgentJoules.Cli/CliApplication.cs, src/AgentJoules.Cli/ExitCodeMapper.cs, src/AgentJoules.Cli/Hosting/AgentJoulesHostFactory.cs | 0.75 | AJ-034-T4 |
| AJ-034-T5 | Replace the placeholder Program.cs and record the Spec filter result | src/AgentJoules.Cli/Program.cs, docs/build-log.md | 0.25 | AJ-034-T4a |
Test mapping: ExitCodeTests (design 6.1, exit codes 1, 2 and 6 here; 3, 4, 5, 7 follow in M2); RedactionTests for the CLI path; Specs feature tests/AgentJoules.Specs/Features/Cli/AJ-034-cli-foundation.feature.
Docs impact: docs/build-log.md `## Per-ID filter results`; the README exit-code table is written by M6 AJ-131.
Definition of done:
- [ ] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [ ] the M0 placeholder behaviour (exit 1 with "no commands implemented yet") is gone: `grep -rn "no commands implemented" src` exits 1
Demo relevance: no

### AJ-035: Print the probe result as text and JSON, including the no-GPU path
Covers: REQ-006, EDGE-001
User story: As a user, I want `AJ probe` to tell me what GPU, driver, power limit and energy method will be measured, and `AJ probe --json` to say the same as one JSON object, with a clean "no GPU" answer on machines without NVML, so that I can check my machine before any run.
Acceptance criteria:
1. Text output has one `label: value` line each, in this order: `GPU name`, `GPU UUID`, `Driver version`, `Compute capability`, `VBIOS version`, `Power limit W`, `MIG mode`, `Energy counter supported`, `Method`, `Sample rate Hz`; an unavailable compute capability or VBIOS prints `N/A`. The exact label text is chosen here (the design leaves it open).
2. `--json` prints one JSON object with the keys `gpuPresent`, `name`, `driverVersion`, `computeCapability`, `vbiosVersion`, `powerLimitW`, `energyCounterSupported`, `method`, `sampleRateHz` plus `gpuUuid` and `migMode`; unavailable values are `null`, never an empty string or 0. Serialization uses the source-generated `AgentJoulesJsonContext` (camelCase, indented, LF).
3. Without NVML or a device (EDGE-001): text prints the single line `no GPU`; JSON has `gpuPresent: false` and every other field null; the exit code is 0 in both forms.
4. stdout holds only the probe output; log lines and the method disclosure go to stderr; `AJ probe --json > out.json` parses with System.Text.Json in a test.
5. The method comes from the AJ-027 selector with `Meter:Method`; `sampleRateHz` equals `Meter:SampleRateHz`; with the counter unsupported under `auto`, the text shows `Method: sampling (sampling-fallback)` and stderr shows `Energy method: sampling (counter unsupported)`.
6. With `Meter:Method=counter` on an unsupported GPU, probe still prints its facts, with `method` null in JSON and `Method: N/A (counter forced but unsupported)` in text, exit 0. The design is silent here: probe is diagnostic, `run` fails with exit 6 later (M2). The default is recorded in docs/build-log.md.
7. The code-level exit-code mapping is tested: `ExitCodeTests.ExitCode_values_match_the_documented_list` asserts `Success` 0, `Unexpected` 1, `InvalidArguments` 2, `MissingKey` 3, `ModelNotFound` 4, `Unreachable` 5, `PreconditionFailed` 6, `ContentDiffers` 7 (`dotnet test --filter "Spec=REQ-006"` passes). The README exit-code table is not written here: M6 AJ-131 owns it (one table, written once). This story adds only a `### probe` subsection under `## CLI reference` listing `--json`, `--save`, `--suffix` and `--config`; the existing placeholder paragraph is left untouched (`git diff --numstat README.md` shows 0 removed lines).
BDD scenarios:
```gherkin
@unit @AJ-S035 @REQ-006 @EDGE-001
Feature: Print the probe result as text and JSON, including the no-GPU path
  @positive
  Scenario: AJ-035 Text probe lists every field of the scripted GPU
    Given a scripted GPU named "Scripted GPU 3080" with driver "616.92" and a counter
    When I run "probe"
    Then stdout has the ten labelled lines and the exit code is 0
  @positive
  Scenario: AJ-035 JSON probe has the documented keys
    Given the same scripted GPU
    When I run "probe --json"
    Then stdout is one JSON object with gpuPresent true and a boolean energyCounterSupported, and the exit code is 0
  @positive
  Scenario: AJ-035 Missing NVML prints no GPU in text
    Given the NVML loader is faked as missing
    When I run "probe"
    Then stdout is the single line "no GPU" and the exit code is 0
  @positive
  Scenario: AJ-035 Missing NVML prints null fields in JSON
    Given the NVML loader is faked as missing
    When I run "probe --json"
    Then gpuPresent is false, every other field is null and the exit code is 0
  @negative
  Scenario: AJ-035 Log lines never pollute the JSON on stdout
    Given a scripted GPU whose counter is unsupported under method auto
    When I run "probe --json"
    Then stdout parses as one JSON object and the fallback disclosure appears only on stderr
  @negative
  Scenario: AJ-035 Missing VBIOS is N/A in text and null in JSON
    Given the scripted VBIOS query returns an error
    When I run "probe" and "probe --json"
    Then the text line is "VBIOS version: N/A" and the JSON value is null
  @negative
  Scenario: AJ-035 Forced counter on an unsupported GPU still prints facts
    Given the counter is unsupported and Meter:Method is counter
    When I run "probe --json"
    Then method is null, the exit code is 0 and stdout is still valid JSON
  @negative
  Scenario: AJ-035 A missing config file path is an argument error
    Given the option "--config" points at a file that does not exist
    When I run "probe --config nope.json"
    Then the exit code is 2
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-035-T1 | Write the failing feature and ProbeCommandTests with traits Spec=REQ-006 and Spec=EDGE-001 | tests/AgentJoules.Specs/Features/Cli/AJ-035-probe-command.feature, tests/AgentJoules.UnitTests/ProbeCommandTests.cs | 0.75 | AJ-034-T5 |
| AJ-035-T2 | Define ProbeResult and the source-generated JSON context for it | src/AgentJoules.Core/Gpu/ProbeResult.cs, src/AgentJoules.Reporting/Json/AgentJoulesJsonContext.cs | 0.75 | AJ-035-T1 |
| AJ-035-T3 | Implement ProbeCommand with the text and JSON forms | src/AgentJoules.Cli/Commands/ProbeCommand.cs | 1.0 | AJ-035-T2 |
| AJ-035-T4 | Wire the method selector, the no-GPU path and the forced-counter path | src/AgentJoules.Cli/Commands/ProbeCommand.cs, src/AgentJoules.Cli/Output/ConsoleReporter.cs | 0.5 | AJ-035-T3 |
| AJ-035-T5 | Add the exit-code value test and the probe subsection to the README | tests/AgentJoules.UnitTests/ExitCodeTests.cs, README.md | 0.5 | AJ-035-T3 |
Test mapping: ProbeCommandTests (design 6.1: probe JSON keys and the `no GPU` path); Specs feature tests/AgentJoules.Specs/Features/Cli/AJ-035-probe-command.feature.
Docs impact: README.md `## CLI reference` (probe subsection only); docs/build-log.md for the forced-counter default.
Definition of done:
- [ ] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [ ] AJ-035 is committed with a message starting `AJ-035 REQ-006:`; EDGE-001 is claimed in the `AJ-036 EDGE-001 EDGE-014:` commit once the container path is proven
Demo relevance: yes: the demo opens with `AJ probe` showing the laptop GPU, driver and energy method.
Conflict: EDGE-001 acceptance also requires "a unit test with the NVML loader faked as missing asserts `run --tier local` returns 6". The `run` command is built in M2 (REQ-017). Proposed resolution: M1 proves the 6 mapping at its source (`MeasurementPreconditionException` maps to exit 6 in AJ-034, and the meter selection path raises it when NVML is unavailable); the M2 `run` story must add the CLI-level test `run --tier local` with the missing-NVML fake and reference EDGE-001 in its trait.

### AJ-036: Load NVML on Linux and WSL2 and prove the no-GPU container path
Covers: EDGE-014, EDGE-001
User story: As a user on Linux or WSL2, I want the meter to look for `libnvidia-ml.so.1` and to report "no GPU" instead of crashing when it is absent, so that the same CLI runs in a container, in CI and on the cloud VM.
Acceptance criteria:
1. `NvmlLibraryCandidates.For(OSPlatform.Linux, ...)` returns `libnvidia-ml.so.1` then `libnvidia-ml.so`, in that order (test).
2. With the Linux fake missing the library, `Open` returns Unavailable with a reason naming `libnvidia-ml.so.1`; no exception escapes (test).
3. `docker manifest inspect mcr.microsoft.com/dotnet/sdk:10.0` exits 0 (the tag was inferred); otherwise the verified tag is recorded and used.
4. The EDGE-001 container command exits 0 and prints JSON with `gpuPresent: false`: `MSYS_NO_PATHCONV=1 docker run --rm -e DOTNET_NOLOGO=1 -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -v "$(pwd -W)":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 dotnet run --project src/AgentJoules.Cli -- probe --json` (no `--gpus` flag; the path form and the two environment variables avoid Git Bash path rewriting and SDK banner text on stdout; builder confirms and records the exact command used).
5. After the container run, the Linux restore artifacts in `obj/` are repaired by `dotnet restore` on the host and `dotnet build -warnaserror -tl:off` exits 0 (design R-10).
6. Inside WSL2 on the laptop, `AJ probe` is run if a .NET SDK exists in the distro; its output (or `not run: <reason>`) is recorded under `## Probe and WSL2 notes`. If WSL2 reports no GPU although `nvidia-smi` sees it there, the note records it and proposes adding `/usr/lib/wsl/lib/libnvidia-ml.so.1` as a third candidate (a design amendment, not applied by this story).
7. If Docker is not running (M0 AJ-002 recorded it), the container check is marked blocked in docs/build-log.md with the date and is retried before M7; it is never silently skipped Check: `grep -n "blocked: Docker daemon not running" docs/build-log.md` matches when `docker info` exits non-zero.
BDD scenarios:
```gherkin
@unit @AJ-S036 @EDGE-014 @EDGE-001
Feature: Load NVML on Linux and WSL2 and prove the no-GPU container path
  @positive
  Scenario: AJ-036 Linux candidates are ordered soname first
    Given the platform is Linux
    When the candidate list is built
    Then it is "libnvidia-ml.so.1" followed by "libnvidia-ml.so"
  @positive
  Scenario: AJ-036 A Linux machine without the library reports no GPU
    Given the Linux loader is faked as missing
    When I run "probe --json"
    Then gpuPresent is false and the exit code is 0
  @positive @integration
  Scenario: AJ-036 The container prints gpuPresent false and exits 0
    Given Docker Desktop is running and the sdk:10.0 image tag exists
    When the EDGE-001 docker command runs without --gpus
    Then it exits 0 and stdout parses as JSON with gpuPresent false
  @negative
  Scenario: AJ-036 A missing library never crashes the process
    Given the native library cannot be found
    When the factory opens device 0
    Then the result is Unavailable naming "libnvidia-ml.so.1" and no exception escapes
  @negative @integration
  Scenario: AJ-036 A stopped Docker daemon is recorded as blocked
    Given "docker info" exits non-zero
    When the container check is attempted
    Then docs/build-log.md records "blocked: Docker daemon not running" with the date
  @negative @integration
  Scenario: AJ-036 An absent image tag is recorded, not assumed
    Given "docker manifest inspect mcr.microsoft.com/dotnet/sdk:10.0" fails
    When the container check is attempted
    Then the verified replacement tag is recorded before the run
  @negative @integration
  Scenario: AJ-036 WSL2 without a .NET SDK is recorded as not run
    Given the WSL2 distro has no dotnet command
    When the WSL2 probe is attempted
    Then docs/build-log.md records "not run" with the reason
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-036-T1 | Write the failing Linux candidate and absent-library tests with traits Spec=EDGE-014 and Spec=EDGE-001 | tests/AgentJoules.Specs/Features/Metering/AJ-036-linux-wsl2-path.feature, tests/AgentJoules.UnitTests/NvmlBindingTests.cs | 0.5 | AJ-035-T5 |
| AJ-036-T2 | Add the Linux candidates to the resolver | src/AgentJoules.Nvml/Interop/NvmlLibraryCandidates.cs | 0.25 | AJ-036-T1 |
| AJ-036-T3 | Run the image check and the EDGE-001 container command, then repair the host restore | docs/build-log.md | 0.75 | AJ-036-T2 |
| AJ-036-T4 | Run AJ probe inside WSL2 or record why not | docs/build-log.md | 0.5 | AJ-036-T2 |
| AJ-036-T5 | Write the container and WSL2 notes | docs/build-log.md | 0.25 | AJ-036-T3, AJ-036-T4 |
Test mapping: NvmlBindingTests (design 6.1: missing library gives typed Unavailable; Linux candidates); Specs feature tests/AgentJoules.Specs/Features/Metering/AJ-036-linux-wsl2-path.feature.
Docs impact: docs/build-log.md `## Probe and WSL2 notes`.
Definition of done:
- [ ] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [ ] commit by the builder with a message starting `AJ-036 EDGE-001 EDGE-014:` (the first GitHub Actions run is a user action after the push and is not a done item)
Demo relevance: no

### AJ-037: Save the probe result and commit the laptop probe file
Covers: REQ-006
User story: As a reviewer, I want `AJ probe --json --save` to write a timestamped probe file and the laptop's probe to be committed, so that every later measurement run can point at the machine facts it was taken on.
Acceptance criteria:
1. `--save` writes `results/probe-<yyyyMMddTHHmmssZ>.json` (UTC from the injected `TimeProvider`, invariant culture) with the same content as `--json`; `--suffix cloudgpu` gives `probe-<yyyyMMddTHHmmssZ>-cloudgpu.json`. The folder is created when missing.
2. On the laptop, `AJ probe --json --save` exits 0 with `gpuPresent: true`; `driverVersion` equals the output of `nvidia-smi --query-gpu=driver_version --format=csv,noheader` (trimmed, string equality); `energyCounterSupported` is a boolean. The file is committed by the builder as `results/probe-<yyyyMMddTHHmmssZ>.json`.
3. `--suffix` without `--save`, or a suffix not matching `^[A-Za-z0-9]{1,20}$` (for example `../x`), exits 2 (assumed constraint, protects the path).
4. An existing target file is never overwritten: exit 1 with `error: file exists` (assumed, protects committed probes).
5. The probe file name does not match the run-file pattern `^\d{8}T\d{6}Z-(local|cloudgpu|nvidia|nebius)(-baseline)?\.json$` (test), so `validate` and `report` never mistake it for a run.
6. With no GPU, `--save` still writes a file with `gpuPresent: false` (an honest record); CI does not commit it.
7. docs/build-log.md records the laptop GPU name, driver, enforced power limit and counter support from the committed probe; if the limit differs from the 94.62 W that nvidia-smi printed on 2026-10-17, the note says the committed probe value is the authority (design C-11). If `energyCounterSupported` is false, the note records that the README must say `sampling` (M3, M6).
BDD scenarios:
```gherkin
@unit @AJ-S037 @REQ-006
Feature: Save the probe result and commit the laptop probe file
  @positive
  Scenario: AJ-037 Save writes a timestamped file with the JSON content
    Given the fake clock is 2026-10-19T09:30:15Z and a scripted GPU
    When I run "probe --json --save" in a temp workspace
    Then results/probe-20261019T093015Z.json exists and equals the stdout JSON
  @positive
  Scenario: AJ-037 The suffix is appended
    Given the same clock and GPU
    When I run "probe --json --save --suffix cloudgpu"
    Then results/probe-20261019T093015Z-cloudgpu.json exists
  @positive @integration
  Scenario: AJ-037 The laptop probe matches nvidia-smi
    Given the laptop GPU and AGENTJOULES_INTEGRATION is "1"
    When I run "AJ probe --json --save"
    Then gpuPresent is true, driverVersion equals the nvidia-smi driver version and energyCounterSupported is a boolean
  @positive
  Scenario: AJ-037 A no-GPU save is an honest record
    Given the NVML loader is faked as missing
    When I run "probe --json --save"
    Then the saved file has gpuPresent false and the exit code is 0
  @negative
  Scenario: AJ-037 A suffix without save is an argument error
    Given a scripted GPU
    When I run "probe --suffix cloudgpu"
    Then the exit code is 2
  @negative
  Scenario: AJ-037 A suffix that escapes the folder is rejected
    Given a scripted GPU
    When I run "probe --save --suffix ../x"
    Then the exit code is 2 and no file is written
  @negative
  Scenario: AJ-037 An existing file is never overwritten
    Given results/probe-20261019T093015Z.json already exists and the clock reads the same second
    When I run "probe --save"
    Then the exit code is 1, stderr says "file exists" and the file is unchanged
  @negative @integration
  Scenario: AJ-037 A driver mismatch against nvidia-smi is a defect
    Given the committed probe driverVersion differs from the nvidia-smi value
    When the comparison runs
    Then the story fails and the discrepancy is recorded in docs/build-log.md
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-037-T1 | Write the failing feature and the save tests in ProbeCommandTests with trait Spec=REQ-006 | tests/AgentJoules.Specs/Features/Cli/AJ-037-probe-save.feature, tests/AgentJoules.UnitTests/ProbeCommandTests.cs | 0.5 | AJ-036-T5 |
| AJ-037-T2 | Implement --save and --suffix with validation and the no-overwrite rule | src/AgentJoules.Cli/Commands/ProbeCommand.cs | 0.75 | AJ-037-T1 |
| AJ-037-T3 | Run the save on the laptop, compare with nvidia-smi and keep the file | results/probe-<yyyyMMddTHHmmssZ>.json | 0.5 | AJ-037-T2 |
| AJ-037-T4 | Record the laptop probe facts and the Spec filter result | docs/build-log.md | 0.25 | AJ-037-T3 |
Test mapping: ProbeCommandTests; Specs feature tests/AgentJoules.Specs/Features/Cli/AJ-037-probe-save.feature. Schema validation of the committed probe file belongs to SchemaValidationTests in M3.
Docs impact: README.md `### probe` (the two new options); docs/build-log.md.
Definition of done:
- [ ] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [ ] REQ-006 commit by the builder with a message starting `AJ-037 REQ-006:` including the probe file (nothing pushed)
Demo relevance: yes: the committed probe file is the machine fact sheet quoted next to every published table.

### AJ-038: Add the GitHub Actions matrix for build, tests and the no-GPU probe
Covers: REQ-024
User story: As a contributor, I want a CI workflow on Windows and Linux runners that builds with warnings as errors, runs the unit tests and runs `AJ probe --json`, so that a pull request proves the code works where no GPU exists.
Acceptance criteria:
1. `.github/workflows/ci.yml` contains `windows-latest`, `ubuntu-latest`, `dotnet build -warnaserror -tl:off`, `dotnet test` and a probe step; `grep -n "secrets\." .github/workflows/ci.yml` exits 1 (no secret reference) and it contains no `pull_request_target`.
2. Steps in order: `actions/checkout@v4`; `actions/setup-dotnet@v6` with `global-json-file: global.json`; `dotnet restore`; `dotnet build -warnaserror -tl:off --no-incremental --no-restore`; `dotnet format --verify-no-changes --no-restore`; `dotnet test --no-build`; `dotnet run --project src/AgentJoules.Cli --no-build -- probe --json`; and `git grep -nE "nvapi-[A-Za-z0-9_-]{8,}" && exit 1 || exit 0`.
3. The probe step runs under `shell: bash` on both runners, requires exit 0 and requires the output to contain `"gpuPresent": false` (hosted runners have no NVIDIA driver, inferred; this is the EDGE-001 path).
4. The workflow sets `permissions: contents: read` and triggers on push and pull_request.
5. `CiWorkflowTests` reads the file (repository root found by walking up to global.json) and asserts every string of criterion 1 and 2.
6. The same restore, build and test commands exit 0 locally; `AJ probe --json` on the laptop prints `gpuPresent: true`, which is expected because the false assertion applies to GPU-less runners only. If `actionlint` is installed, `actionlint .github/workflows/ci.yml` exits 0.
7. The first GitHub run happens after the user pushes and is not a Definition of Done item; the user action is noted in docs/build-log.md (check: `grep -n "first CI run" docs/build-log.md` matches under `## User actions`).
BDD scenarios:
```gherkin
@unit @AJ-S038 @REQ-024
Feature: Add the GitHub Actions matrix for build, tests and the no-GPU probe
  @positive
  Scenario: AJ-038 Both runners and the strict build command are present
    Given .github/workflows/ci.yml
    When the workflow text is read
    Then it contains "windows-latest", "ubuntu-latest" and "dotnet build -warnaserror -tl:off"
  @positive
  Scenario: AJ-038 The probe step expects the no-GPU answer
    Given .github/workflows/ci.yml
    When the probe step is read
    Then it runs "probe --json" under bash and checks for "gpuPresent": false
  @positive
  Scenario: AJ-038 The local commands match the workflow
    Given the workflow's restore, build and test commands
    When they run locally
    Then each exits 0
  @negative
  Scenario: AJ-038 A secret reference is rejected
    Given the workflow contains "secrets.NVIDIA_API_KEY"
    When "grep -n 'secrets\.' ci.yml" runs
    Then it exits 0 with the offending line and the story fails
  @negative
  Scenario: AJ-038 A build step without warnings as errors is rejected
    Given the build step reads "dotnet build" without -warnaserror
    When CiWorkflowTests runs
    Then the test fails
  @negative
  Scenario: AJ-038 A pull_request_target trigger is rejected
    Given the workflow triggers on pull_request_target
    When CiWorkflowTests runs
    Then the test fails because forks would run with repository privileges
```
Tasks:
| Task | Description (one verb, one artifact) | Files | Estimate (h) | Depends on |
|---|---|---|---|---|
| AJ-038-T1 | Write the failing feature and CiWorkflowTests with trait Spec=REQ-024 | tests/AgentJoules.Specs/Features/Cli/AJ-038-ci-workflow.feature, tests/AgentJoules.UnitTests/CiWorkflowTests.cs | 0.5 | AJ-037-T4 |
| AJ-038-T2 | Write the workflow file | .github/workflows/ci.yml | 0.75 | AJ-038-T1 |
| AJ-038-T3 | Run the local equivalents, the secrets grep and actionlint if present | docs/build-log.md | 0.5 | AJ-038-T2 |
| AJ-038-T4 | Record the Spec filter result and the first CI run as a post-push user action | docs/build-log.md | 0.25 | AJ-038-T3 |
Test mapping: CiWorkflowTests (new class, file-content assertions); Specs feature tests/AgentJoules.Specs/Features/Cli/AJ-038-ci-workflow.feature.
Docs impact: docs/build-log.md (user action: first CI run after the push).
Definition of done:
- [ ] scenarios green, unit tests green, `dotnet build -warnaserror -tl:off` 0 warnings
- [ ] docs listed above updated
- [ ] REQ-024 commit by the builder with a message starting `AJ-038 REQ-024:` (nothing pushed)
Demo relevance: no

## Risks

| Risk | Trigger | Mitigation | Owner |
|---|---|---|---|
| Energy counter unsupported on the GeForce laptop GPU (design R-01) | AJ-037 probe prints `energyCounterSupported: false` | auto falls back to sampling with the flag and console line (AJ-027); build-log notes the README must say `sampling`; nothing is estimated | builder |
| `nvmlDeviceGetPowerUsage` is a one-second driver average (design R-02) | sampling at 10 Hz looks smooth but lags | document as a limitation in M4 methodology; counter remains the gross source when supported | builder |
| NVML export or signature differs from the design (design R-08) | `EntryPointNotFoundException` or a wrong unit in AJ-021 or AJ-028 | check each signature against the NVML API reference, use the deprecated throttle export as fallback, record in docs/build-log.md | builder |
| LibraryImport analyzer findings CA5392, CA5393, SYSLIB1051 | build warnings under TreatWarningsAsErrors | System32-only search path attribute and no StringMarshalling, as measured in design 3.1 | builder |
| Container check rewrites `obj/` and Git Bash rewrites the mount path (design R-10) | AJ-036 build fails after the container run, or the volume path is mangled | `MSYS_NO_PATHCONV=1`, `$(pwd -W)`, `dotnet restore` on the host afterwards | builder |
| Docker Desktop not running or WSL2 has no .NET SDK | AJ-002 recorded it | mark blocked or not run with the date; retry before M7; EDGE-014 allows "not run and why" | user |
| `ExecutionModeTests`-style timing flakiness | tests wait on wall-clock time | M1 uses `FakeTimeProvider` only; no real sleeps | builder |
| Design gaps: Options packages missing from design 2.4; counter-reset handling; probe with forced counter; throttle support on the laptop | AJ-033, AJ-026, AJ-035, AJ-028 | each default is recorded in docs/build-log.md for owner confirmation; none changes a REQ | builder |
| Estimate far exceeds the three-day window | sum of task estimates is far above 18 hours | see Estimate; the coordinator should extend M1 or move AJ-036, AJ-038 and parts of AJ-028 to a later slot; the order of stories keeps the critical path (AJ-020 to AJ-035, AJ-037) first | coordinator |

## Estimate

Note (2026-10-18): any deferral or descope candidates named in this section are informational; the drop order in docs/plan/plan.md section 3.4 is the single source and classifies stories as MUST, SHOULD or COULD.

Sum of task estimates in hours:

| Story | Hours |
|---|---|
| AJ-020 | 2.50 |
| AJ-021 | 4.25 |
| AJ-022 | 3.00 |
| AJ-023 | 2.00 |
| AJ-024 | 2.00 |
| AJ-025 | 1.50 |
| AJ-026 | 2.50 |
| AJ-027 | 2.00 |
| AJ-028 | 2.75 |
| AJ-029 | 5.25 |
| AJ-030 | 2.50 |
| AJ-031 | 1.75 |
| AJ-032 | 2.00 |
| AJ-033 | 2.75 |
| AJ-034 | 4.50 |
| AJ-035 | 3.50 |
| AJ-036 | 2.25 |
| AJ-037 | 2.00 |
| AJ-038 | 2.00 |
| Total | 51.00 |

Capacity for 2026-10-19 to 2026-10-21 at about six working hours per day is 18 hours, so M1 as specified is about 33 hours over, and M0 is already over its window. This is stated plainly: the plan as written does not fit the dates. The realistic options are to extend M1 and compress later milestones, to run builder sessions with more than six productive hours per day, or to descope (candidates: AJ-036 WSL2 run, AJ-038 CI file, the throttle laptop check in AJ-028). The REQ and EDGE coverage does not change with either choice.
