using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using AgentJoules.Nvml;
using AgentJoules.Nvml.Interop;

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
}