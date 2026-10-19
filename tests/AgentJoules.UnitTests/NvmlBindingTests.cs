using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using AgentJoules.Core.Gpu;
using AgentJoules.Nvml;
using AgentJoules.Nvml.Interop;
using AgentJoules.TestSupport;

using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace AgentJoules.UnitTests;

[Trait("Spec", "REQ-003")]
public sealed class NvmlBindingTests
{
    private const string NvmlNativeRelativePath = "src/AgentJoules.Nvml/Interop/NvmlNative.cs";

    private static readonly string[] ExpectedEntryPoints =
    [
        "nvmlInit_v2",
        "nvmlShutdown",
        "nvmlSystemGetDriverVersion",
        "nvmlDeviceGetCount_v2",
        "nvmlDeviceGetHandleByIndex_v2",
        "nvmlDeviceGetName",
        "nvmlDeviceGetUUID",
        "nvmlDeviceGetVbiosVersion",
        "nvmlDeviceGetCudaComputeCapability",
        "nvmlDeviceGetEnforcedPowerLimit",
        "nvmlDeviceGetPowerUsage",
        "nvmlDeviceGetTotalEnergyConsumption",
        "nvmlDeviceGetMigMode",
        "nvmlDeviceGetCurrentClocksEventReasons",

        // The deprecated export; ClocksEventReader falls back to it on drivers without the renamed export, and AJ-028 wires the throttle source to that reader.
        "nvmlDeviceGetCurrentClocksThrottleReasons",
    ];

    // The exports without a device handle as their first parameter; every other export takes an nint device first.
    private static readonly HashSet<string> NonDeviceEntryPoints =
    [
        "nvmlInit_v2",
        "nvmlShutdown",
        "nvmlSystemGetDriverVersion",
        "nvmlDeviceGetCount_v2",
        "nvmlDeviceGetHandleByIndex_v2",
    ];

    private static string ReadNvmlNativeSource()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "global.json")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory, NvmlNativeRelativePath));
    }

    [Theory]
    [InlineData(@"C:\Program Files", @"C:\Program Files\NVIDIA Corporation\NVSMI\nvml.dll")]
    [InlineData(@"C:\Program Files\", @"C:\Program Files\NVIDIA Corporation\NVSMI\nvml.dll")]
    [InlineData(@"C:\Program Files\\", @"C:\Program Files\NVIDIA Corporation\NVSMI\nvml.dll")]
    [InlineData(@"\\server\share", @"\\server\share\NVIDIA Corporation\NVSMI\nvml.dll")]
    [InlineData("C:/Program Files", @"C:/Program Files\NVIDIA Corporation\NVSMI\nvml.dll")]
    public void Windows_candidates_are_the_library_name_then_the_nvsmi_path(string programW6432, string expectedNvsmi)
    {
        IReadOnlyList<string> candidates = NvmlLibraryCandidates.For(OSPlatform.Windows, programW6432);

        string[] expected = ["nvml.dll", expectedNvsmi];
        Assert.Equal(expected, candidates);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"\")]
    [InlineData("C:foo")]
    [InlineData(@"\foo")]
    [InlineData("relative")]
    [InlineData(@"relative\dir")]
    public void Windows_candidates_omit_the_nvsmi_path_without_ProgramW6432(string? programW6432)
    {
        IReadOnlyList<string> candidates = NvmlLibraryCandidates.For(OSPlatform.Windows, programW6432);

        string[] expected = ["nvml.dll"];
        Assert.Equal(expected, candidates);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"C:\Program Files")]
    public void Linux_candidates_are_ordered(string? programW6432)
    {
        IReadOnlyList<string> candidates = NvmlLibraryCandidates.For(OSPlatform.Linux, programW6432);

        string[] expected = ["libnvidia-ml.so.1", "libnvidia-ml.so"];
        Assert.Equal(expected, candidates);
    }

    [Fact]
    public void An_unknown_platform_has_no_candidates()
    {
        Assert.Empty(NvmlLibraryCandidates.For(OSPlatform.OSX, @"C:\Program Files"));
        Assert.Empty(NvmlLibraryCandidates.For(OSPlatform.FreeBSD, null));
    }

    [Fact]
    public void TryLoadFirst_returns_the_second_candidate_when_the_first_fails()
    {
        var loaded = new List<string>();
        nint Loader(string name)
        {
            loaded.Add(name);
            return name == "second" ? 0x2000 : 0;
        }

        bool found = NvmlLibraryResolver.TryLoadFirst(["first", "second", "third"], Loader, out nint handle);

        Assert.True(found);
        Assert.Equal(0x2000, handle);
        string[] expected = ["first", "second"];
        Assert.Equal(expected, loaded);
    }

    [Fact]
    public void TryLoadFirst_stops_at_the_first_success()
    {
        var loaded = new List<string>();
        nint Loader(string name)
        {
            loaded.Add(name);
            return 0x1000;
        }

        bool found = NvmlLibraryResolver.TryLoadFirst(["first", "second"], Loader, out nint handle);

        Assert.True(found);
        Assert.Equal(0x1000, handle);
        string[] expected = ["first"];
        Assert.Equal(expected, loaded);
    }

    [Fact]
    public void TryLoadFirst_returns_false_after_trying_every_candidate_when_all_fail()
    {
        var loaded = new List<string>();
        nint Loader(string name)
        {
            loaded.Add(name);
            return 0;
        }

        bool found = NvmlLibraryResolver.TryLoadFirst(["first", "second"], Loader, out nint handle);

        Assert.False(found);
        Assert.Equal(0, handle);
        string[] expected = ["first", "second"];
        Assert.Equal(expected, loaded);
    }

    [Fact]
    public void TryLoadFirst_returns_false_for_an_empty_candidate_list()
    {
        bool found = NvmlLibraryResolver.TryLoadFirst([], _ => 0x1000, out nint handle);

        Assert.False(found);
        Assert.Equal(0, handle);
    }

    [Fact]
    public void TryLoadFirst_does_not_swallow_an_exception_thrown_by_the_loader()
    {
        // A missing file is the loader returning 0, which TryLoadFirst absorbs; a loader that throws has a real defect and must surface it.
        var thrown = new InvalidOperationException("loader defect");

        InvalidOperationException caught = Assert.Throws<InvalidOperationException>(
            () => NvmlLibraryResolver.TryLoadFirst(["first"], _ => throw thrown, out _));

        Assert.Same(thrown, caught);
    }

    [Fact]
    public void The_assembly_pins_the_search_path_to_System32_and_disables_runtime_marshalling()
    {
        // InternalsVisibleTo("AgentJoules.UnitTests") now exists, but the attributes are read from the assembly of the public INvmlApi, so this test does not depend on it.
        Assembly assembly = typeof(INvmlApi).Assembly;

        DefaultDllImportSearchPathsAttribute? searchPaths = assembly.GetCustomAttribute<DefaultDllImportSearchPathsAttribute>();
        Assert.NotNull(searchPaths);
        Assert.Equal(DllImportSearchPath.System32, searchPaths.Paths);
        Assert.NotNull(assembly.GetCustomAttribute<DisableRuntimeMarshallingAttribute>());
    }

    [Fact]
    public void ToLoadPath_qualifies_a_bare_name_with_the_system_directory_and_keeps_a_full_path()
    {
        Assert.Equal(@"C:\Windows\system32\nvml.dll", NvmlLibraryResolver.ToLoadPath("nvml.dll", @"C:\Windows\system32"));
        Assert.Equal(@"C:\Windows\system32\nvml.dll", NvmlLibraryResolver.ToLoadPath("nvml.dll", @"C:\Windows\system32\"));

        const string nvsmi = @"C:\Program Files\NVIDIA Corporation\NVSMI\nvml.dll";
        Assert.Equal(nvsmi, NvmlLibraryResolver.ToLoadPath(nvsmi, @"C:\Windows\system32"));
    }

    [Fact]
    public void The_Windows_load_sequence_asks_for_the_system_directory_path_first_and_never_the_bare_name()
    {
        IReadOnlyList<string> loadPaths = NvmlLibraryResolver.LoadPaths(OSPlatform.Windows, @"C:\Program Files", @"C:\Windows\system32");
        var asked = new List<string>();

        bool found = NvmlLibraryResolver.TryLoadFirst(loadPaths, path => { asked.Add(path); return 0; }, out _);

        Assert.False(found);
        string[] expected = [@"C:\Windows\system32\nvml.dll", @"C:\Program Files\NVIDIA Corporation\NVSMI\nvml.dll"];
        Assert.Equal(expected, asked);
        Assert.DoesNotContain("nvml.dll", asked);
    }

    [Fact]
    public void The_Linux_load_sequence_passes_the_sonames_unchanged()
    {
        IReadOnlyList<string> loadPaths = NvmlLibraryResolver.LoadPaths(OSPlatform.Linux, null, @"C:\Windows\system32");

        string[] expected = ["libnvidia-ml.so.1", "libnvidia-ml.so"];
        Assert.Equal(expected, loadPaths);
    }

    [Fact]
    public void Resolve_leaves_every_other_library_name_to_the_runtime()
    {
        Assert.Equal(IntPtr.Zero, NvmlLibraryResolver.Resolve("other", typeof(INvmlApi).Assembly, DllImportSearchPath.System32));
        Assert.Equal(IntPtr.Zero, NvmlLibraryResolver.Resolve("nvml.dll", typeof(INvmlApi).Assembly, null));
    }

    [Fact]
    public void EnsureRegistered_is_idempotent_and_thread_safe()
    {
        NvmlLibraryResolver.EnsureRegistered();
        NvmlLibraryResolver.EnsureRegistered();
        Parallel.For(0, 16, _ => NvmlLibraryResolver.EnsureRegistered());

        // A resolver is now registered for the assembly, so a second registration is rejected by the runtime; that proves the Lazy ran once.
        Assert.Throws<InvalidOperationException>(
            () => NativeLibrary.SetDllImportResolver(typeof(INvmlApi).Assembly, static (_, _, _) => IntPtr.Zero));
    }

    /// <summary>
    /// Proves criterion 7 without AGENTJOULES_INTEGRATION: with no NVML library on the machine the first native call throws
    /// <see cref="DllNotFoundException"/> (the branch the GPU-less CI runners and the EDGE-001 container take); with the library
    /// present the resolver loads it and returns a non-zero handle (the branch the NVIDIA laptop takes). Real NVML calls stay in the
    /// gated integration tests, so this test never calls Init or Shutdown on a machine with a driver.
    /// </summary>
    [Fact]
    public void NvmlApi_init_throws_DllNotFound_without_a_driver_or_the_resolver_loads_one()
    {
        OSPlatform platform = OperatingSystem.IsWindows() ? OSPlatform.Windows : OperatingSystem.IsLinux() ? OSPlatform.Linux : OSPlatform.OSX;
        IReadOnlyList<string> loadPaths = NvmlLibraryResolver.LoadPaths(platform, Environment.GetEnvironmentVariable("ProgramW6432"), Environment.SystemDirectory);

        // The same load calls the resolver makes, so the probe and the binding agree on whether a driver is present.
        bool driverPresent = NvmlLibraryResolver.TryLoadFirst(loadPaths, static path => NativeLibrary.TryLoad(path, out nint handle) ? handle : 0, out nint probed);
        if (driverPresent)
        {
            NativeLibrary.Free(probed);
        }

        if (!driverPresent)
        {
            Assert.Throws<DllNotFoundException>(() => new NvmlApi().Init());
            return;
        }

        Assert.NotEqual(IntPtr.Zero, NvmlLibraryResolver.Resolve("nvml", typeof(INvmlApi).Assembly, null));
    }

    [Fact]
    public void Every_LibraryImport_line_is_preceded_by_an_NVML_reference_comment()
    {
        string[] lines = ReadNvmlNativeSource().ReplaceLineEndings("\n").Split('\n');

        int mentions = lines.Count(static line => line.Contains("LibraryImport", StringComparison.Ordinal));
        int citations = lines.Count(static line => line.Contains("docs.nvidia.com/deploy/nvml-api", StringComparison.Ordinal));

        Assert.True(mentions > 0, "NvmlNative.cs declares no LibraryImport.");
        Assert.Equal(mentions, citations);

        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains("[LibraryImport(", StringComparison.Ordinal))
            {
                Assert.True(i > 0, "A LibraryImport declaration is on the first line.");
                Assert.StartsWith("// https://docs.nvidia.com/deploy/nvml-api/api/group__", lines[i - 1].TrimStart(), StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void NvmlNative_declares_the_fifteen_exports_in_the_pinned_shape()
    {
        Type native = typeof(NvmlNative);
        MethodInfo[] imports =
        [
            .. native
                .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(static method => method.GetCustomAttribute<LibraryImportAttribute>() is not null),
        ];

        string[] entryPoints = [.. imports.Select(static method => method.GetCustomAttribute<LibraryImportAttribute>()!.EntryPoint ?? method.Name)];
        Assert.Equal(ExpectedEntryPoints.Order(), entryPoints.Order());

        foreach (MethodInfo method in imports)
        {
            LibraryImportAttribute attribute = method.GetCustomAttribute<LibraryImportAttribute>()!;
            string name = attribute.EntryPoint ?? method.Name;
            ParameterInfo[] parameters = method.GetParameters();

            Assert.Equal("nvml", attribute.LibraryName);
            // StringMarshalling has no Undefined member: its default (Custom, with no custom type) is the "not set" state.
            Assert.Equal(StringMarshalling.Custom, attribute.StringMarshalling);
            Assert.Null(attribute.StringMarshallingCustomType);
            Assert.Equal(typeof(int), method.ReturnType);
            Assert.DoesNotContain(parameters, static parameter => parameter.ParameterType == typeof(string));

            if (!NonDeviceEntryPoints.Contains(name))
            {
                Assert.NotEmpty(parameters);
                Assert.Equal(typeof(nint), parameters[0].ParameterType);
            }
        }
    }

    [Fact]
    public void NvmlNative_does_not_use_StringMarshalling()
    {
        Assert.DoesNotContain("StringMarshalling", ReadNvmlNativeSource(), StringComparison.Ordinal);
    }

    // ---- AJ-022: open, read and dispose an NVML device with a typed result (REQ-003) ----

    private const string WindowsLibrary = "nvml.dll";
    private const string LinuxLibrary = "libnvidia-ml.so.1";

    private static readonly (OSPlatform Platform, string Library)[] PlatformLibraries =
    [
        (OSPlatform.Windows, WindowsLibrary),
        (OSPlatform.Linux, LinuxLibrary),
    ];

    // Every member Open calls on a healthy single-device session, in the order the factory may call them.
    private static readonly string[] OpenMembers =
    [
        "Init",
        "GetCount",
        "GetHandleByIndex",
        "GetDriverVersion",
        "GetName",
        "GetUuid",
        "GetPowerLimit",
        "GetVbios",
        "GetComputeCapability",
        "GetMigMode",
        "GetTotalEnergyConsumption",
    ];

    private static readonly string[] FaultTypeNames =
    [
        nameof(DllNotFoundException),
        nameof(EntryPointNotFoundException),
        nameof(BadImageFormatException),
    ];

    // The seam member and the NVML function the reason text names when that query returns a non-Success code.
    private static readonly (string Member, string Function)[] MandatoryQueries =
    [
        ("GetCount", "nvmlDeviceGetCount_v2"),
        ("GetHandleByIndex", "nvmlDeviceGetHandleByIndex_v2"),
        ("GetDriverVersion", "nvmlSystemGetDriverVersion"),
        ("GetName", "nvmlDeviceGetName"),
        ("GetUuid", "nvmlDeviceGetUUID"),
        ("GetPowerLimit", "nvmlDeviceGetEnforcedPowerLimit"),
    ];

    public static TheoryData<string, string> OpenFaults
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (string member in OpenMembers)
            {
                foreach (string faultType in FaultTypeNames)
                {
                    data.Add(member, faultType);
                }
            }

            return data;
        }
    }

    public static TheoryData<string, string> MandatoryQueryFailures
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach ((string member, string function) in MandatoryQueries)
            {
                data.Add(member, function);
            }

            return data;
        }
    }

    private static ScriptedNvmlApi NewApi(FakeTimeProvider? time = null) =>
        new(time ?? new FakeTimeProvider()) { CounterOffsetMillijoules = 0 };

    private static Exception CreateFault(string typeName, string message) => typeName switch
    {
        nameof(DllNotFoundException) => new DllNotFoundException(message),
        nameof(EntryPointNotFoundException) => new EntryPointNotFoundException(message),
        nameof(BadImageFormatException) => new BadImageFormatException(message),
        _ => throw new ArgumentOutOfRangeException(nameof(typeName), typeName, "Unknown fault type."),
    };

    private static IGpuTelemetrySource OpenSource(INvmlApi api)
    {
        GpuOpenResult result = new NvmlGpuTelemetrySourceFactory(api).Open(0);
        Assert.True(result.IsAvailable, result.Reason);
        Assert.NotNull(result.Source);
        return result.Source;
    }

    private static void AssertUnavailable(GpuOpenResult result, string reason)
    {
        Assert.False(result.IsAvailable);
        Assert.Null(result.Source);
        Assert.Equal(reason, result.Reason);
    }

    // Cycles through the three read members so a racing test exercises all of them.
    private static void ReadOnce(IGpuTelemetrySource source, int step)
    {
        Action read = (step % 3) switch
        {
            0 => () => source.ReadPowerWatts(),
            1 => () => source.ReadEnergyMillijoules(),
            _ => () => source.ReadThrottleReasons(),
        };
        read();
    }

    // Starts the workers together behind a barrier, joins them and fails with the first exception any of them threw.
    private static void RunConcurrently(int threads, Action action)
    {
        var failures = new ConcurrentQueue<Exception>();
        using var barrier = new Barrier(threads);
        var workers = new Thread[threads];
        for (int i = 0; i < threads; i++)
        {
            workers[i] = new Thread(() =>
            {
                Exception? failure = Record.Exception(() =>
                {
                    barrier.SignalAndWait();
                    action();
                });
                if (failure is not null)
                {
                    failures.Enqueue(failure);
                }
            });
            workers[i].Start();
        }

        foreach (Thread worker in workers)
        {
            worker.Join();
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void Open_returns_Available_with_the_scripted_identity()
    {
        ScriptedNvmlApi api = NewApi();
        api.Name = "Scripted GPU 3080";
        api.Uuid = "GPU-11111111-2222-3333-4444-555555555555";
        api.DriverVersion = "616.92";
        api.Vbios = "94.02.5C.00.01";
        api.ComputeCapability = (8, 6);
        api.PowerLimitMilliwatts = 85059;

        GpuOpenResult result = new NvmlGpuTelemetrySourceFactory(api).Open(0);

        Assert.True(result.IsAvailable);
        Assert.Null(result.Reason);
        Assert.NotNull(result.Source);
        using IGpuTelemetrySource source = result.Source;
        GpuIdentity identity = source.Identity;
        Assert.Equal("Scripted GPU 3080", identity.Name);
        Assert.Equal("GPU-11111111-2222-3333-4444-555555555555", identity.Uuid);
        Assert.Equal("616.92", identity.DriverVersion);
        Assert.Equal("94.02.5C.00.01", identity.VbiosVersion);
        Assert.Equal("8.6", identity.ComputeCapability);
        Assert.Equal(85.059, identity.PowerLimitWatts);
        Assert.Equal(1, api.InitCallCount);
        Assert.Equal(0, api.ShutdownCallCount);
        Assert.True(api.IsInitialised);
    }

    [Fact]
    public void The_identity_follows_the_scripted_values_rather_than_constants()
    {
        ScriptedNvmlApi api = NewApi();
        api.Name = "Other GPU";
        api.Uuid = "GPU-abcdef";
        api.DriverVersion = "580.1";
        api.Vbios = "1.2.3";
        api.ComputeCapability = (7, 5);
        api.PowerLimitMilliwatts = 300000;
        api.MigModeReturn = NvmlReturn.Success;
        api.MigModeCurrent = 0;

        using IGpuTelemetrySource source = OpenSource(api);

        GpuIdentity identity = source.Identity;
        Assert.Equal("Other GPU", identity.Name);
        Assert.Equal("GPU-abcdef", identity.Uuid);
        Assert.Equal("580.1", identity.DriverVersion);
        Assert.Equal("1.2.3", identity.VbiosVersion);
        Assert.Equal("7.5", identity.ComputeCapability);
        Assert.Equal(300.0, identity.PowerLimitWatts);
        Assert.Equal("disabled", identity.MigMode);
    }

    [Fact]
    public void The_compute_capability_is_formatted_with_the_invariant_culture()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            ScriptedNvmlApi api = NewApi();
            api.ComputeCapability = (8, 6);

            using IGpuTelemetrySource source = OpenSource(api);

            Assert.Equal("8.6", source.Identity.ComputeCapability);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void The_source_does_not_wrap_the_device_handle_in_a_SafeHandle()
    {
        FieldInfo[] fields = typeof(NvmlGpuTelemetrySource).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        Assert.DoesNotContain(fields, static field => typeof(SafeHandle).IsAssignableFrom(field.FieldType));
    }

    [Theory]
    [MemberData(nameof(OpenFaults))]
    public void A_native_load_failure_from_any_call_yields_Unavailable_with_the_fixed_library_text(string member, string faultType)
    {
        foreach ((OSPlatform platform, string library) in PlatformLibraries)
        {
            ScriptedNvmlApi api = NewApi();
            api.FaultFor = called => called == member ? CreateFault(faultType, "scripted load failure") : null;

            GpuOpenResult result = new NvmlGpuTelemetrySourceFactory(api, platform).Open(0);

            AssertUnavailable(result, $"NVML library not found: {library} ({faultType})");

            // Shutdown is attempted once when Init had succeeded, and never when Init itself was the failing call.
            Assert.Equal(member == "Init" ? 0 : 1, api.ShutdownCallCount);
            Assert.False(api.IsInitialised);
        }
    }

    [Fact]
    public void A_missing_library_yields_Unavailable_and_never_calls_Shutdown()
    {
        foreach ((OSPlatform platform, string library) in PlatformLibraries)
        {
            ScriptedNvmlApi api = NewApi();
            api.MissingLibrary = true;

            GpuOpenResult result = new NvmlGpuTelemetrySourceFactory(api, platform).Open(0);

            AssertUnavailable(result, $"NVML library not found: {library} ({nameof(DllNotFoundException)})");
            Assert.Equal(1, api.InitCallCount);
            Assert.Equal(0, api.ShutdownCallCount);
        }
    }

    [Fact]
    public void The_running_platform_names_its_own_library_when_none_is_given()
    {
        ScriptedNvmlApi api = NewApi();
        api.MissingLibrary = true;

        GpuOpenResult result = new NvmlGpuTelemetrySourceFactory(api).Open(0);

        string library = OperatingSystem.IsWindows() ? WindowsLibrary : LinuxLibrary;
        AssertUnavailable(result, $"NVML library not found: {library} ({nameof(DllNotFoundException)})");
    }

    [Fact]
    public void Every_non_Windows_platform_names_the_Linux_library()
    {
        foreach (OSPlatform platform in new[] { OSPlatform.Linux, OSPlatform.OSX, OSPlatform.FreeBSD })
        {
            ScriptedNvmlApi api = NewApi();
            api.MissingLibrary = true;

            GpuOpenResult result = new NvmlGpuTelemetrySourceFactory(api, platform).Open(0);

            AssertUnavailable(result, $"NVML library not found: {LinuxLibrary} ({nameof(DllNotFoundException)})");
        }
    }

    [Fact]
    public void Identity_and_the_counter_state_stay_readable_after_Dispose()
    {
        ScriptedNvmlApi api = NewApi();
        api.CounterReturn = NvmlReturn.NotSupported;
        IGpuTelemetrySource source = OpenSource(api);
        GpuIdentity before = source.Identity;
        source.Dispose();

        Assert.Equal(before, source.Identity);
        Assert.Equal("Scripted GPU 3080", source.Identity.Name);
        Assert.False(source.EnergyCounterSupported);
        Assert.Equal("counter-not-supported: NVML_ERROR_NOT_SUPPORTED", source.EnergyCounterUnsupportedReason);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(999)]
    public void ReadThrottleReasons_throws_on_a_non_Success_code_other_than_NotSupported(int code)
    {
        ScriptedNvmlApi api = NewApi();
        api.ClockEventReturn = (NvmlReturn)code;
        using IGpuTelemetrySource source = OpenSource(api);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => source.ReadThrottleReasons());

        Assert.Contains("GetClocksEventReasons", error.Message, StringComparison.Ordinal);
        Assert.Contains(code.ToString(CultureInfo.InvariantCulture), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_reason_never_carries_the_exception_message_or_a_load_path()
    {
        const string message = @"Unable to load 'C:\Users\secret\nvml.dll' or '/opt/private/lib/libnvidia-ml.so.1'";
        foreach ((OSPlatform platform, string library) in PlatformLibraries)
        {
            foreach (string faultType in FaultTypeNames)
            {
                ScriptedNvmlApi api = NewApi();
                api.FaultFor = called => called == "GetName" ? CreateFault(faultType, message) : null;

                GpuOpenResult result = new NvmlGpuTelemetrySourceFactory(api, platform).Open(0);

                AssertUnavailable(result, $"NVML library not found: {library} ({faultType})");
                Assert.DoesNotContain("secret", result.Reason, StringComparison.Ordinal);
                Assert.DoesNotContain(@"C:\", result.Reason, StringComparison.Ordinal);
                Assert.DoesNotContain("/opt", result.Reason, StringComparison.Ordinal);
                Assert.DoesNotContain("Unable to load", result.Reason, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void An_exception_from_Shutdown_during_the_unavailable_path_is_swallowed()
    {
        ScriptedNvmlApi api = NewApi();
        api.FaultFor = called => called is "GetName" or "Shutdown" ? new DllNotFoundException("scripted") : null;

        GpuOpenResult result = new NvmlGpuTelemetrySourceFactory(api, OSPlatform.Windows).Open(0);

        AssertUnavailable(result, $"NVML library not found: {WindowsLibrary} ({nameof(DllNotFoundException)})");
        Assert.Equal(1, api.ShutdownCallCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(9)]
    [InlineData(999)]
    public void A_failing_init_is_reported_with_its_return_code_and_Shutdown_is_not_called(int code)
    {
        ScriptedNvmlApi api = NewApi();
        api.InitReturn = (NvmlReturn)code;

        GpuOpenResult result = new NvmlGpuTelemetrySourceFactory(api).Open(0);

        AssertUnavailable(result, $"nvmlInit_v2 returned {code}");
        Assert.Equal(1, api.InitCallCount);
        Assert.Equal(0, api.ShutdownCallCount);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(5, 1)]
    [InlineData(0, 0)]
    public void An_index_at_or_above_the_count_is_Unavailable_after_the_session_is_shut_down(int index, int count)
    {
        ScriptedNvmlApi api = NewApi();
        api.DeviceCount = (uint)count;

        GpuOpenResult result = new NvmlGpuTelemetrySourceFactory(api).Open(index);

        AssertUnavailable(result, $"no device at index {index}");
        Assert.Equal(1, api.ShutdownCallCount);
        Assert.False(api.IsInitialised);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void A_negative_index_is_Unavailable_before_any_NVML_call(int index)
    {
        ScriptedNvmlApi api = NewApi();

        GpuOpenResult result = new NvmlGpuTelemetrySourceFactory(api).Open(index);

        AssertUnavailable(result, $"no device at index {index}");
        Assert.Equal(0, api.InitCallCount);
        Assert.Equal(0, api.ShutdownCallCount);
    }

    [Theory]
    [MemberData(nameof(MandatoryQueryFailures))]
    public void A_failing_mandatory_query_is_Unavailable_naming_the_function_and_its_return_code(string member, string function)
    {
        ScriptedNvmlApi inner = NewApi();
        var api = new ReturnCodeOverrideApi(inner, member, NvmlReturn.InvalidArgument);

        GpuOpenResult result = new NvmlGpuTelemetrySourceFactory(api).Open(0);

        AssertUnavailable(result, $"{function} returned 2");
        Assert.Equal(1, inner.ShutdownCallCount);
        Assert.False(inner.IsInitialised);
    }

    [Fact]
    public void Open_leaves_the_vbios_and_compute_capability_null_when_the_fake_reports_NotSupported()
    {
        ScriptedNvmlApi api = NewApi();
        api.Vbios = null;
        api.ComputeCapability = null;

        using IGpuTelemetrySource source = OpenSource(api);

        Assert.Null(source.Identity.VbiosVersion);
        Assert.Null(source.Identity.ComputeCapability);
        Assert.Equal(api.Name, source.Identity.Name);
    }

    [Theory]
    [InlineData("GetVbios")]
    [InlineData("GetComputeCapability")]
    public void Any_non_Success_code_from_an_optional_query_leaves_that_field_null(string member)
    {
        ScriptedNvmlApi inner = NewApi();
        var api = new ReturnCodeOverrideApi(inner, member, NvmlReturn.InvalidArgument);

        using IGpuTelemetrySource source = OpenSource(api);

        GpuIdentity identity = source.Identity;
        Assert.Equal(member != "GetVbios", identity.VbiosVersion is not null);
        Assert.Equal(member != "GetComputeCapability", identity.ComputeCapability is not null);
    }

    [Theory]
    [InlineData(3, 0u, "not-supported")]
    [InlineData(1, 0u, "not-supported")]
    [InlineData(2, 1u, "not-supported")]
    [InlineData(999, 0u, "not-supported")]
    [InlineData(0, 0u, "disabled")]
    [InlineData(0, 1u, "enabled")]
    [InlineData(0, 2u, "enabled")]
    public void The_MIG_mode_maps_the_return_code_and_the_current_mode(int code, uint current, string expected)
    {
        ScriptedNvmlApi api = NewApi();
        api.MigModeReturn = (NvmlReturn)code;
        api.MigModeCurrent = current;

        using IGpuTelemetrySource source = OpenSource(api);

        Assert.Equal(expected, source.Identity.MigMode);
    }

    [Fact]
    public void Open_reads_the_energy_counter_exactly_once_and_reports_it_supported()
    {
        ScriptedNvmlApi api = NewApi();

        using IGpuTelemetrySource source = OpenSource(api);

        Assert.Equal(1, api.CounterCallCount);
        Assert.True(source.EnergyCounterSupported);
        Assert.Null(source.EnergyCounterUnsupportedReason);
    }

    [Theory]
    [InlineData(3, "counter-not-supported: NVML_ERROR_NOT_SUPPORTED")]
    [InlineData(2, "counter-not-supported: 2")]
    [InlineData(999, "counter-not-supported: 999")]
    public void An_unsupported_counter_is_reported_with_its_reason_and_Open_still_succeeds(int code, string reason)
    {
        ScriptedNvmlApi api = NewApi();
        api.CounterReturn = (NvmlReturn)code;

        using IGpuTelemetrySource source = OpenSource(api);

        Assert.Equal(1, api.CounterCallCount);
        Assert.False(source.EnergyCounterSupported);
        Assert.Equal(reason, source.EnergyCounterUnsupportedReason);
        Assert.Null(source.ReadEnergyMillijoules());

        // An unsupported counter is decided once at Open; a read does not ask NVML again.
        Assert.Equal(1, api.CounterCallCount);
    }

    [Fact]
    public void ReadEnergyMillijoules_returns_the_absolute_counter_not_a_delta_since_Open()
    {
        // The fake's default offset (123456789) models a counter that counts since driver load.
        var time = new FakeTimeProvider();
        var api = new ScriptedNvmlApi(time) { ConstantPowerWatts = 100 };
        using IGpuTelemetrySource source = OpenSource(api);

        time.Advance(TimeSpan.FromSeconds(2));
        double? millijoules = source.ReadEnergyMillijoules();

        Assert.Equal(123456789.0 + 200000.0, millijoules);
        Assert.Equal(2, api.CounterCallCount);
    }

    [Fact]
    public void Open_calls_exactly_the_members_in_OpenMembers()
    {
        var called = new List<string>();
        ScriptedNvmlApi api = NewApi();
        api.FaultFor = member =>
        {
            called.Add(member);
            return null;
        };

        using IGpuTelemetrySource source = OpenSource(api);

        Assert.Equal(OpenMembers, called);
    }

    [Fact]
    public void A_fault_outside_the_contract_propagates_but_the_session_is_shut_down_once()
    {
        // Only the three native load exceptions are mapped to Unavailable; anything else is a defect and surfaces.
        ScriptedNvmlApi api = NewApi();
        api.FaultFor = called => called == "GetName" ? new InvalidOperationException("scripted defect") : null;

        Assert.Throws<InvalidOperationException>(() => new NvmlGpuTelemetrySourceFactory(api).Open(0));

        Assert.Equal(1, api.ShutdownCallCount);
        Assert.False(api.IsInitialised);
    }

    [Fact]
    public void Dispose_swallows_a_native_load_exception_from_Shutdown_and_still_disposes()
    {
        ScriptedNvmlApi api = NewApi();
        IGpuTelemetrySource source = OpenSource(api);
        api.FaultFor = called => called == "Shutdown" ? new EntryPointNotFoundException("scripted") : null;

        source.Dispose();
        source.Dispose();

        Assert.Equal(1, api.ShutdownCallCount);
        Assert.Throws<ObjectDisposedException>(() => source.ReadPowerWatts());
    }

    [Fact]
    public void Reason_and_message_integers_are_formatted_with_the_invariant_culture()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            // sv-SE writes the minus sign as U+2212 when ICU is present.
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("sv-SE");

            GpuOpenResult noDevice = new NvmlGpuTelemetrySourceFactory(NewApi()).Open(-1);
            ScriptedNvmlApi initFails = NewApi();
            initFails.InitReturn = (NvmlReturn)(-7);
            GpuOpenResult initResult = new NvmlGpuTelemetrySourceFactory(initFails).Open(0);
            ScriptedNvmlApi counterFails = NewApi();
            counterFails.CounterReturn = (NvmlReturn)(-7);
            counterFails.PowerReturnForRead = static _ => (NvmlReturn)(-7);
            using IGpuTelemetrySource source = OpenSource(counterFails);
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => source.ReadPowerWatts());

            AssertUnavailable(noDevice, "no device at index -1");
            AssertUnavailable(initResult, "nvmlInit_v2 returned -7");
            Assert.Equal("counter-not-supported: -7", source.EnergyCounterUnsupportedReason);
            Assert.Contains("returned -7", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void A_read_racing_Dispose_throws_only_ObjectDisposedException()
    {
        for (int round = 0; round < 100; round++)
        {
            ScriptedNvmlApi api = NewApi();
            IGpuTelemetrySource source = OpenSource(api);
            var unexpected = new ConcurrentQueue<Exception>();
            using var barrier = new Barrier(5);
            var readers = new Thread[4];
            for (int i = 0; i < readers.Length; i++)
            {
                int first = i;
                readers[i] = new Thread(() =>
                {
                    barrier.SignalAndWait();
                    for (int step = first; ; step++)
                    {
                        int current = step;
                        Exception? failure = Record.Exception(() => ReadOnce(source, current));
                        if (failure is ObjectDisposedException)
                        {
                            return;
                        }

                        if (failure is not null)
                        {
                            unexpected.Enqueue(failure);
                            return;
                        }
                    }
                });
                readers[i].Start();
            }

            barrier.SignalAndWait();
            Thread.SpinWait(round * 200);
            source.Dispose();
            foreach (Thread reader in readers)
            {
                reader.Join();
            }

            Assert.Empty(unexpected);
            Assert.Equal(1, api.ShutdownCallCount);
            Assert.False(api.IsInitialised);
        }
    }

    [Fact]
    public void ReadEnergyMillijoules_throws_on_a_non_Success_code_and_never_returns_a_value()
    {
        ScriptedNvmlApi api = NewApi();
        api.CounterReturnForRead = static call => call == 1 ? NvmlReturn.Success : (NvmlReturn)999;
        using IGpuTelemetrySource source = OpenSource(api);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => source.ReadEnergyMillijoules());

        Assert.Contains("GetTotalEnergyConsumption", error.Message, StringComparison.Ordinal);
        Assert.Contains("999", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadPowerWatts_returns_milliwatts_divided_by_1000()
    {
        ScriptedNvmlApi api = NewApi();
        api.ConstantPowerWatts = 120.5;
        using IGpuTelemetrySource source = OpenSource(api);

        Assert.Equal(120.5, source.ReadPowerWatts());
    }

    [Fact]
    public void ReadPowerWatts_throws_on_a_non_Success_code_instead_of_returning_zero_and_recovers()
    {
        ScriptedNvmlApi api = NewApi();
        api.ConstantPowerWatts = 120.5;
        api.PowerReturnForRead = static call => call == 1 ? (NvmlReturn)999 : NvmlReturn.Success;
        using IGpuTelemetrySource source = OpenSource(api);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => source.ReadPowerWatts());

        Assert.Contains("GetPowerUsage", error.Message, StringComparison.Ordinal);
        Assert.Contains("999", error.Message, StringComparison.Ordinal);
        Assert.Equal(120.5, source.ReadPowerWatts());
    }

    [Fact]
    public void ReadThrottleReasons_is_Unsupported_when_the_clock_event_query_returns_NotSupported()
    {
        ScriptedNvmlApi api = NewApi();
        api.ClockEventReturn = NvmlReturn.NotSupported;
        using IGpuTelemetrySource source = OpenSource(api);

        ThrottleReasons reasons = source.ReadThrottleReasons();

        Assert.False(reasons.Supported);
        Assert.Equal(0UL, reasons.Bits);
        Assert.Equal(ThrottleReasons.Unsupported, reasons);
    }

    [Fact]
    public void ReadThrottleReasons_returns_the_bits_when_supported()
    {
        ScriptedNvmlApi api = NewApi();
        api.ClockEventReasons = 0x4UL | 0x20UL;
        using IGpuTelemetrySource source = OpenSource(api);

        ThrottleReasons reasons = source.ReadThrottleReasons();

        Assert.True(reasons.Supported);
        Assert.Equal(0x24UL, reasons.Bits);
    }

    [Fact]
    public void Dispose_calls_Shutdown_exactly_once_when_called_twice()
    {
        ScriptedNvmlApi api = NewApi();
        IGpuTelemetrySource source = OpenSource(api);

        source.Dispose();
        source.Dispose();

        Assert.Equal(1, api.ShutdownCallCount);
        Assert.False(api.IsInitialised);
    }

    [Fact]
    public void Dispose_calls_Shutdown_exactly_once_when_two_threads_race_to_dispose()
    {
        for (int round = 0; round < 100; round++)
        {
            ScriptedNvmlApi api = NewApi();
            IGpuTelemetrySource source = OpenSource(api);

            RunConcurrently(2, source.Dispose);

            Assert.Equal(1, api.ShutdownCallCount);
            Assert.False(api.IsInitialised);
        }
    }

    [Theory]
    [InlineData("ReadPowerWatts", true)]
    [InlineData("ReadEnergyMillijoules", true)]
    [InlineData("ReadEnergyMillijoules", false)]
    [InlineData("ReadThrottleReasons", true)]
    [InlineData("ReadThrottleReasons", false)]
    public void Every_read_after_Dispose_throws_ObjectDisposedException_without_calling_NVML(string member, bool counterSupported)
    {
        ScriptedNvmlApi api = NewApi();
        api.CounterSupported = counterSupported;
        IGpuTelemetrySource source = OpenSource(api);
        source.Dispose();
        int callsBefore = api.PowerCallCount + api.CounterCallCount + api.ShutdownCallCount;
        Action read = member switch
        {
            "ReadPowerWatts" => () => source.ReadPowerWatts(),
            "ReadEnergyMillijoules" => () => source.ReadEnergyMillijoules(),
            "ReadThrottleReasons" => () => source.ReadThrottleReasons(),
            _ => throw new ArgumentOutOfRangeException(nameof(member), member, "Unknown read member."),
        };

        Assert.Throws<ObjectDisposedException>(read);

        Assert.Equal(callsBefore, api.PowerCallCount + api.CounterCallCount + api.ShutdownCallCount);
    }

    [Fact]
    public void Reads_are_serialized_under_contention()
    {
        ScriptedNvmlApi api = NewApi();
        api.ConstantPowerWatts = 75;
        using IGpuTelemetrySource source = OpenSource(api);

        RunConcurrently(8, () =>
        {
            for (int i = 0; i < 100; i++)
            {
                Assert.Equal(75.0, source.ReadPowerWatts());
            }
        });

        Assert.Equal(800, api.PowerReadCount);
        Assert.Equal(1, api.MaxConcurrentReads);
    }

    [Fact]
    public void Mixed_reads_share_the_same_lock()
    {
        ScriptedNvmlApi api = NewApi();
        api.ConstantPowerWatts = 75;
        using IGpuTelemetrySource source = OpenSource(api);

        RunConcurrently(4, () =>
        {
            for (int i = 0; i < 50; i++)
            {
                Assert.Equal(75.0, source.ReadPowerWatts());
                Assert.NotNull(source.ReadEnergyMillijoules());
                Assert.True(source.ReadThrottleReasons().Supported);
            }
        });

        Assert.Equal(200, api.PowerReadCount);
        Assert.Equal(201, api.CounterCallCount);
        Assert.Equal(1, api.MaxConcurrentReads);
    }

    [Fact]
    public void A_null_api_is_rejected_by_the_factory()
    {
        Assert.Throws<ArgumentNullException>(static () => new NvmlGpuTelemetrySourceFactory(null!));
    }

    [Fact]
    public void GpuOpenResult_factories_carry_the_source_or_the_reason()
    {
        using IGpuTelemetrySource source = OpenSource(NewApi());

        GpuOpenResult available = GpuOpenResult.Available(source);
        GpuOpenResult unavailable = GpuOpenResult.Unavailable("no device at index 3");

        Assert.True(available.IsAvailable);
        Assert.Same(source, available.Source);
        Assert.Null(available.Reason);
        Assert.False(unavailable.IsAvailable);
        Assert.Null(unavailable.Source);
        Assert.Equal("no device at index 3", unavailable.Reason);
    }

    // Wraps a ScriptedNvmlApi and replaces the return code of one member, which the fake cannot script for the identity queries.
    private sealed class ReturnCodeOverrideApi(ScriptedNvmlApi inner, string member, NvmlReturn code) : INvmlApi
    {
        public NvmlReturn Init() => member == nameof(Init) ? code : inner.Init();

        public NvmlReturn Shutdown() => member == nameof(Shutdown) ? code : inner.Shutdown();

        public NvmlReturn GetHandleByIndex(uint index, out nint device)
        {
            if (member == nameof(GetHandleByIndex))
            {
                device = 0;
                return code;
            }

            return inner.GetHandleByIndex(index, out device);
        }

        public NvmlReturn GetPowerUsage(nint device, out uint milliwatts)
        {
            if (member == nameof(GetPowerUsage))
            {
                milliwatts = 0;
                return code;
            }

            return inner.GetPowerUsage(device, out milliwatts);
        }

        public NvmlReturn GetTotalEnergyConsumption(nint device, out ulong millijoules)
        {
            if (member == nameof(GetTotalEnergyConsumption))
            {
                millijoules = 0;
                return code;
            }

            return inner.GetTotalEnergyConsumption(device, out millijoules);
        }

        public NvmlReturn GetClocksEventReasons(nint device, out ulong reasons)
        {
            if (member == nameof(GetClocksEventReasons))
            {
                reasons = 0;
                return code;
            }

            return inner.GetClocksEventReasons(device, out reasons);
        }

        public NvmlReturn GetMigMode(nint device, out uint current, out uint pending)
        {
            if (member == nameof(GetMigMode))
            {
                current = 0;
                pending = 0;
                return code;
            }

            return inner.GetMigMode(device, out current, out pending);
        }

        public NvmlReturn GetCount(out uint count)
        {
            if (member == nameof(GetCount))
            {
                count = 0;
                return code;
            }

            return inner.GetCount(out count);
        }

        public NvmlReturn GetName(nint device, out string name)
        {
            if (member == nameof(GetName))
            {
                name = string.Empty;
                return code;
            }

            return inner.GetName(device, out name);
        }

        public NvmlReturn GetUuid(nint device, out string uuid)
        {
            if (member == nameof(GetUuid))
            {
                uuid = string.Empty;
                return code;
            }

            return inner.GetUuid(device, out uuid);
        }

        public NvmlReturn GetVbios(nint device, out string vbios)
        {
            if (member == nameof(GetVbios))
            {
                vbios = string.Empty;
                return code;
            }

            return inner.GetVbios(device, out vbios);
        }

        public NvmlReturn GetComputeCapability(nint device, out int major, out int minor)
        {
            if (member == nameof(GetComputeCapability))
            {
                major = 0;
                minor = 0;
                return code;
            }

            return inner.GetComputeCapability(device, out major, out minor);
        }

        public NvmlReturn GetPowerLimit(nint device, out uint milliwatts)
        {
            if (member == nameof(GetPowerLimit))
            {
                milliwatts = 0;
                return code;
            }

            return inner.GetPowerLimit(device, out milliwatts);
        }

        public NvmlReturn GetDriverVersion(out string version)
        {
            if (member == nameof(GetDriverVersion))
            {
                version = string.Empty;
                return code;
            }

            return inner.GetDriverVersion(out version);
        }
    }
}