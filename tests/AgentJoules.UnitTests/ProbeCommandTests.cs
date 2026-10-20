using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using AgentJoules.Cli;
using AgentJoules.Core.Energy;
using AgentJoules.Core.Gpu;
using AgentJoules.Nvml.Interop;
using AgentJoules.TestSupport;

using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace AgentJoules.UnitTests;

/// <summary>
/// AJ-035 (REQ-006, EDGE-001): the text and JSON output of <c>probe</c> through the real application under <see cref="CliHarness"/>. The GPU
/// is a <see cref="ScriptedNvmlApi"/> behind the real <c>NvmlGpuTelemetrySourceFactory</c>, so every fact travels the whole binding; the
/// method and the rate come through the in-memory configuration. The no-GPU tests carry the EDGE-001 trait.
/// </summary>
[Trait("Spec", "REQ-006")]
public sealed partial class ProbeCommandTests
{
    private const string FallbackDisclosure = "Energy method: sampling (counter unsupported)";
    private const string MethodKey = "AgentJoules:Meter:Method";
    private const string RateKey = "AgentJoules:Meter:SampleRateHz";
    private const string BelowMinRateKey = "AgentJoules:Meter:AllowBelowMinRate";

    // The order of criterion 1 (text) and criterion 2 (JSON); the labels and keys are the contract.
    private static readonly string[] TextLabels =
    [
        "GPU name",
        "GPU UUID",
        "Driver version",
        "Compute capability",
        "VBIOS version",
        "Power limit W",
        "MIG mode",
        "Energy counter supported",
        "Method",
        "Sample rate Hz",
    ];

    private static readonly string[] JsonKeys =
    [
        "gpuPresent",
        "name",
        "gpuUuid",
        "driverVersion",
        "computeCapability",
        "vbiosVersion",
        "powerLimitW",
        "migMode",
        "energyCounterSupported",
        "method",
        "sampleRateHz",
    ];

    // AJ-037 criterion 5: the run-file pattern of design 3.6 (a probe file must never match it) and the shape a probe file name must have.
    // A stand-in: M2's RunFileName replaces this copy of its pattern.
    [GeneratedRegex(@"^\d{8}T\d{6}Z-(local|cloudgpu|nvidia|nebius)(-baseline)?\.json$", RegexOptions.CultureInvariant)]
    private static partial Regex RunFileName();

    [GeneratedRegex(@"^probe-\d{8}T\d{6}Z(-[A-Za-z0-9]{1,20})?\.json$", RegexOptions.CultureInvariant)]
    private static partial Regex ProbeFileName();

    // The scripted GPU of NewGpu with the default configuration (auto, 10 Hz).
    private static readonly string[] ScriptedLines =
    [
        "GPU name: Scripted GPU 3080",
        "GPU UUID: GPU-00000000-0000-0000-0000-000000000000",
        "Driver version: 616.92",
        "Compute capability: 8.6",
        "VBIOS version: 94.02.5C.00.01",
        "Power limit W: 95.283",
        "MIG mode: disabled",
        "Energy counter supported: true",
        "Method: counter",
        "Sample rate Hz: 10",
    ];

    // The scripted device of the other scenarios: the ScriptedNvmlApi defaults except the enforced power limit, which exercises three decimals.
    private static ScriptedNvmlApi NewGpu() => new(new FakeTimeProvider()) { PowerLimitMilliwatts = 95283 };

    private static ScriptedNvmlApi NewNoGpu(string cause)
    {
        ScriptedNvmlApi api = NewGpu();
        switch (cause)
        {
            case "missing-library":
                api.MissingLibrary = true;
                break;
            case "no-device":
                api.DeviceCount = 0;
                break;
            case "init-failed":
                // NVML_ERROR_DRIVER_NOT_LOADED (9) is not a member of NvmlReturn; any non-Success code from Init means no session.
                api.InitReturn = (NvmlReturn)9;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(cause), cause, "unknown no-GPU cause");
        }

        return api;
    }

    private static Task<CliRunResult> RunAsync(string arguments, ScriptedNvmlApi api, params (string Key, string? Value)[] configuration) =>
        CliHarness.RunAsync(arguments, ScriptedGpuOverrides.Create(api, configuration));

    // The lines of a text probe: the output must end with a line terminator, and the terminator is the platform's (WriteLine), so CR is trimmed.
    private static string[] Lines(string stdout)
    {
        Assert.EndsWith("\n", stdout, StringComparison.Ordinal);
        return [.. stdout[..^1].Split('\n').Select(line => line.TrimEnd('\r'))];
    }

    private static string[] Expected(params (int Index, string Line)[] changes)
    {
        string[] lines = [.. ScriptedLines];
        foreach ((int index, string line) in changes)
        {
            lines[index] = line;
        }

        return lines;
    }

    private static JsonDocument ParseOneObject(string stdout)
    {
        JsonDocument document = JsonDocument.Parse(stdout);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        return document;
    }

    private static string[] Keys(JsonDocument document) => [.. document.RootElement.EnumerateObject().Select(property => property.Name)];

    // Criterion 1, the labels: every line is "label: value" with the label of its position.
    private static void AssertLabelsInOrder(string[] lines)
    {
        Assert.Equal(TextLabels.Length, lines.Length);
        for (int i = 0; i < TextLabels.Length; i++)
        {
            Assert.StartsWith(TextLabels[i] + ": ", lines[i], StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Text_probe_prints_the_ten_labelled_lines_in_order_with_the_scripted_values()
    {
        CliRunResult result = await RunAsync("probe", NewGpu());

        Assert.Equal(0, result.ExitCode);
        string[] lines = Lines(result.Stdout);
        AssertLabelsInOrder(lines);
        Assert.Equal(ScriptedLines, lines);
    }

    [Theory]
    [InlineData(85059u, "85.059")]
    [InlineData(95283u, "95.283")]
    [InlineData(85000u, "85")]
    [InlineData(1500u, "1.5")]
    public async Task Power_limit_prints_up_to_three_decimals_in_text_and_the_same_number_in_JSON(uint milliwatts, string expected)
    {
        ScriptedNvmlApi api = NewGpu();
        api.PowerLimitMilliwatts = milliwatts;

        CliRunResult text = await RunAsync("probe", api);
        CliRunResult json = await RunAsync("probe --json", api);

        Assert.Equal(0, text.ExitCode);
        Assert.Equal("Power limit W: " + expected, Lines(text.Stdout)[5]);
        Assert.Equal(0, json.ExitCode);
        using JsonDocument document = ParseOneObject(json.Stdout);
        JsonElement limit = document.RootElement.GetProperty("powerLimitW");
        Assert.Equal(JsonValueKind.Number, limit.ValueKind);
        Assert.Equal(milliwatts / 1000.0, limit.GetDouble());
    }

    [Fact]
    public async Task Numbers_use_the_invariant_culture_whatever_the_machine_culture_is()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");

            CliRunResult text = await RunAsync("probe", NewGpu(), (RateKey, "2.5"), (BelowMinRateKey, "true"));
            CliRunResult json = await RunAsync("probe --json", NewGpu(), (RateKey, "2.5"), (BelowMinRateKey, "true"));

            Assert.Equal(0, text.ExitCode);
            string[] lines = Lines(text.Stdout);
            Assert.Equal("Power limit W: 95.283", lines[5]);
            Assert.Equal("Sample rate Hz: 2.5", lines[9]);
            Assert.Equal(0, json.ExitCode);
            using JsonDocument document = ParseOneObject(json.Stdout);
            Assert.Equal(95.283, document.RootElement.GetProperty("powerLimitW").GetDouble());
            Assert.Equal(2.5, document.RootElement.GetProperty("sampleRateHz").GetDouble());
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData("disabled", NvmlReturn.Success, 0u)]
    [InlineData("enabled", NvmlReturn.Success, 1u)]
    [InlineData("not-supported", NvmlReturn.NotSupported, 0u)]
    public async Task MIG_mode_is_printed_as_disabled_enabled_or_not_supported(string expected, NvmlReturn code, uint current)
    {
        ScriptedNvmlApi api = NewGpu();
        api.MigModeReturn = code;
        api.MigModeCurrent = current;

        CliRunResult text = await RunAsync("probe", api);
        CliRunResult json = await RunAsync("probe --json", api);

        Assert.Equal(0, text.ExitCode);
        Assert.Equal("MIG mode: " + expected, Lines(text.Stdout)[6]);
        Assert.Equal(0, json.ExitCode);
        using JsonDocument document = ParseOneObject(json.Stdout);
        Assert.Equal(expected, document.RootElement.GetProperty("migMode").GetString());
    }

    [Fact]
    public async Task Unavailable_compute_capability_and_VBIOS_print_N_A_in_text_and_null_in_JSON()
    {
        ScriptedNvmlApi api = NewGpu();
        api.ComputeCapability = null;
        api.Vbios = null;

        CliRunResult text = await RunAsync("probe", api);
        CliRunResult json = await RunAsync("probe --json", api);

        Assert.Equal(0, text.ExitCode);
        Assert.Equal(Expected((3, "Compute capability: N/A"), (4, "VBIOS version: N/A")), Lines(text.Stdout));
        Assert.Equal(0, json.ExitCode);
        using JsonDocument document = ParseOneObject(json.Stdout);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("computeCapability").ValueKind);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("vbiosVersion").ValueKind);
        Assert.Equal("Scripted GPU 3080", document.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Unavailable_VBIOS_alone_leaves_the_compute_capability_in_both_forms()
    {
        ScriptedNvmlApi api = NewGpu();
        api.Vbios = null;

        CliRunResult text = await RunAsync("probe", api);
        CliRunResult json = await RunAsync("probe --json", api);

        Assert.Equal(Expected((4, "VBIOS version: N/A")), Lines(text.Stdout));
        using JsonDocument document = ParseOneObject(json.Stdout);
        Assert.Equal("8.6", document.RootElement.GetProperty("computeCapability").GetString());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("vbiosVersion").ValueKind);
    }

    [Fact]
    public async Task Json_probe_has_exactly_the_eleven_keys_in_order_with_the_scripted_values()
    {
        CliRunResult result = await RunAsync("probe --json", NewGpu());

        Assert.Equal(0, result.ExitCode);
        using JsonDocument document = ParseOneObject(result.Stdout);
        JsonElement root = document.RootElement;
        Assert.Equal(JsonKeys, Keys(document));
        Assert.Equal(JsonValueKind.True, root.GetProperty("gpuPresent").ValueKind);
        Assert.Equal("Scripted GPU 3080", root.GetProperty("name").GetString());
        Assert.Equal("GPU-00000000-0000-0000-0000-000000000000", root.GetProperty("gpuUuid").GetString());
        Assert.Equal("616.92", root.GetProperty("driverVersion").GetString());
        Assert.Equal("8.6", root.GetProperty("computeCapability").GetString());
        Assert.Equal("94.02.5C.00.01", root.GetProperty("vbiosVersion").GetString());
        Assert.Equal(JsonValueKind.Number, root.GetProperty("powerLimitW").ValueKind);
        Assert.Equal(95.283, root.GetProperty("powerLimitW").GetDouble());
        Assert.Equal("disabled", root.GetProperty("migMode").GetString());
        Assert.Equal(JsonValueKind.True, root.GetProperty("energyCounterSupported").ValueKind);
        Assert.Equal("counter", root.GetProperty("method").GetString());
        Assert.Equal(JsonValueKind.Number, root.GetProperty("sampleRateHz").ValueKind);
        Assert.Equal(10.0, root.GetProperty("sampleRateHz").GetDouble());
    }

    [Fact]
    public async Task Json_probe_is_camelCase_indented_with_LF_only_and_one_trailing_newline()
    {
        CliRunResult result = await RunAsync("probe --json", NewGpu());

        Assert.Equal(0, result.ExitCode);
        string stdout = result.Stdout;
        Assert.StartsWith("{\n  \"gpuPresent\": true,\n  \"name\": \"Scripted GPU 3080\",\n", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', stdout);
        Assert.EndsWith("\n}\n", stdout, StringComparison.Ordinal);
        Assert.False(stdout.EndsWith("\n\n", StringComparison.Ordinal), "the JSON ends with more than one newline");

        // One line for each of the eleven members plus the two braces.
        Assert.Equal(JsonKeys.Length + 2, stdout.Count(character => character == '\n'));
        using JsonDocument document = ParseOneObject(stdout);
        Assert.All(Keys(document), key => Assert.True(char.IsLower(key[0]), "the key is not camelCase: " + key));
    }

    [Fact]
    public async Task Json_probe_redirected_to_a_file_parses_with_System_Text_Json()
    {
        // The test form of `AJ probe --json > out.json`: the application writes straight into a file stream.
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string path = Path.Combine(workspace.Root, "out.json");
        int exitCode;
        await using (var file = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            CliRunResult result = await CliHarness.RunAsync("probe --json", ScriptedGpuOverrides.Create(NewGpu()), stdout: file);
            exitCode = result.ExitCode;
        }

        Assert.Equal(0, exitCode);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
        Assert.Equal(JsonKeys, Keys(document));
        Assert.Equal("Scripted GPU 3080", document.RootElement.GetProperty("name").GetString());
    }

    [Theory]
    [InlineData("missing-library")]
    [InlineData("no-device")]
    [InlineData("init-failed")]
    [Trait("Spec", "EDGE-001")]
    public async Task Text_probe_without_NVML_or_a_device_prints_the_single_line_no_GPU_and_exits_0(string cause)
    {
        CliRunResult result = await RunAsync("probe", NewNoGpu(cause));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("no GPU", Assert.Single(Lines(result.Stdout)));
    }

    [Theory]
    [InlineData("missing-library")]
    [InlineData("no-device")]
    [InlineData("init-failed")]
    [Trait("Spec", "EDGE-001")]
    public async Task Json_probe_without_NVML_or_a_device_has_gpuPresent_false_and_every_other_key_null_and_exits_0(string cause)
    {
        CliRunResult result = await RunAsync("probe --json", NewNoGpu(cause));

        Assert.Equal(0, result.ExitCode);
        using JsonDocument document = ParseOneObject(result.Stdout);
        Assert.Equal(JsonKeys, Keys(document));
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            // Null, never "" or 0 or false: a missing GPU has no values.
            Assert.Equal(property.Name == "gpuPresent" ? JsonValueKind.False : JsonValueKind.Null, property.Value.ValueKind);
        }

        Assert.DoesNotContain('\r', result.Stdout);
        Assert.EndsWith("\n}\n", result.Stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("probe")]
    [InlineData("probe --json")]
    [Trait("Spec", "EDGE-001")]
    public async Task Probe_without_NVML_still_exits_0_when_the_counter_method_is_forced(string arguments)
    {
        // Probe is diagnostic: only run fails with exit 6 (M2) when the GPU is missing.
        CliRunResult result = await RunAsync(arguments, NewNoGpu("missing-library"), (MethodKey, "counter"));

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("error:", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Probe_ends_the_NVML_session_it_opened()
    {
        ScriptedNvmlApi api = NewGpu();

        CliRunResult result = await RunAsync("probe", api);

        Assert.Equal(0, result.ExitCode);
        Assert.True(api.InitCallCount >= 1, "probe never opened NVML");
        Assert.False(api.IsInitialised, "probe left the NVML session open");
        Assert.Equal(api.InitCallCount, api.ShutdownCallCount);
    }

    [Fact]
    public async Task Fallback_disclosure_is_on_stderr_only_and_stdout_stays_one_JSON_object()
    {
        ScriptedNvmlApi api = NewGpu();
        api.CounterSupported = false;

        // No Meter:Method key: the shipped default is auto. Debug logging makes the host write real records, which must all stay on stderr.
        CliRunResult result = await RunAsync("probe --json", api, ("Logging:LogLevel:Default", "Debug"));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(FallbackDisclosure, Assert.Single(result.StderrLines, line => line == FallbackDisclosure));
        Assert.Contains("Opening the GPU at index 0", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("Energy method", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("Opening the GPU", result.Stdout, StringComparison.Ordinal);
        using JsonDocument document = ParseOneObject(result.Stdout);
        Assert.Equal(JsonKeys, Keys(document));
        Assert.Equal("sampling", document.RootElement.GetProperty("method").GetString());
        Assert.Equal(JsonValueKind.False, document.RootElement.GetProperty("energyCounterSupported").ValueKind);
    }

    [Fact]
    public async Task Text_probe_under_auto_with_an_unsupported_counter_shows_the_sampling_fallback_and_discloses_it_on_stderr()
    {
        ScriptedNvmlApi api = NewGpu();
        api.CounterSupported = false;

        CliRunResult result = await RunAsync("probe", api, (MethodKey, "auto"));

        Assert.Equal(0, result.ExitCode);
        string[] lines = Lines(result.Stdout);
        AssertLabelsInOrder(lines);
        Assert.Equal(Expected((7, "Energy counter supported: false"), (8, "Method: sampling (sampling-fallback)")), lines);
        Assert.Equal(FallbackDisclosure, Assert.Single(result.StderrLines, line => line == FallbackDisclosure));
        Assert.DoesNotContain("Energy method", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_supported_counter_under_auto_prints_the_counter_method_and_no_fallback_disclosure()
    {
        CliRunResult result = await RunAsync("probe", NewGpu(), (MethodKey, "auto"));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Method: counter", Lines(result.Stdout));
        Assert.DoesNotContain("counter unsupported", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Forced_sampling_on_a_GPU_with_the_counter_is_sampling_without_the_fallback_marker()
    {
        CliRunResult text = await RunAsync("probe", NewGpu(), (MethodKey, "sampling"));
        CliRunResult json = await RunAsync("probe --json", NewGpu(), (MethodKey, "sampling"));

        Assert.Equal(0, text.ExitCode);
        Assert.Equal("Method: sampling", Lines(text.Stdout)[8]);
        Assert.Equal(0, json.ExitCode);
        using JsonDocument document = ParseOneObject(json.Stdout);
        Assert.Equal("sampling", document.RootElement.GetProperty("method").GetString());
        Assert.DoesNotContain("counter unsupported", text.Stderr + json.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Forced_counter_on_an_unsupported_GPU_prints_the_facts_with_method_null_and_exits_0()
    {
        ScriptedNvmlApi api = NewGpu();
        api.CounterSupported = false;

        CliRunResult json = await RunAsync("probe --json", api, (MethodKey, "counter"));

        // Probe is diagnostic: it reports and exits 0 where run fails with exit 6 (M2).
        Assert.Equal(0, json.ExitCode);
        using JsonDocument document = ParseOneObject(json.Stdout);
        JsonElement root = document.RootElement;
        Assert.Equal(JsonKeys, Keys(document));
        Assert.Equal(JsonValueKind.Null, root.GetProperty("method").ValueKind);
        Assert.Equal(JsonValueKind.True, root.GetProperty("gpuPresent").ValueKind);
        Assert.Equal(JsonValueKind.False, root.GetProperty("energyCounterSupported").ValueKind);
        Assert.Equal("Scripted GPU 3080", root.GetProperty("name").GetString());
        Assert.Equal("616.92", root.GetProperty("driverVersion").GetString());
        Assert.Equal(95.283, root.GetProperty("powerLimitW").GetDouble());
        Assert.Equal(10.0, root.GetProperty("sampleRateHz").GetDouble());
        Assert.DoesNotContain("error:", json.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Forced_counter_on_an_unsupported_GPU_prints_N_A_with_the_forced_but_unsupported_note_in_text()
    {
        ScriptedNvmlApi api = NewGpu();
        api.CounterSupported = false;

        CliRunResult text = await RunAsync("probe", api, (MethodKey, "counter"));

        Assert.Equal(0, text.ExitCode);
        string[] lines = Lines(text.Stdout);
        AssertLabelsInOrder(lines);
        Assert.Equal(
            Expected((7, "Energy counter supported: false"), (8, "Method: N/A (counter forced but unsupported)")),
            lines);
        Assert.DoesNotContain("error:", text.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("5", 5.0)]
    [InlineData("2.5", 2.5)]
    [InlineData("100", 100.0)]
    public async Task Sample_rate_equals_Meter_SampleRateHz_in_text_and_JSON(string configured, double expected)
    {
        (string, string?)[] configuration = [(RateKey, configured), (BelowMinRateKey, "true")];

        CliRunResult text = await RunAsync("probe", NewGpu(), configuration);
        CliRunResult json = await RunAsync("probe --json", NewGpu(), configuration);

        Assert.Equal(0, text.ExitCode);
        Assert.Equal("Sample rate Hz: " + configured, Lines(text.Stdout)[9]);
        Assert.Equal(0, json.ExitCode);
        using JsonDocument document = ParseOneObject(json.Stdout);
        Assert.Equal(expected, document.RootElement.GetProperty("sampleRateHz").GetDouble());
    }

    [Theory]
    [InlineData("probe --config nope.json")]
    [InlineData("probe --json --config nope.json")]
    public async Task A_missing_config_file_exits_2_with_nothing_on_stdout_and_one_error_line_naming_it(string arguments)
    {
        CliRunResult result = await RunAsync(arguments, NewGpu());

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(string.Empty, result.Stdout);
        Assert.Contains("nope.json", Assert.Single(result.ErrorLines), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Spec", "EDGE-001")]
    public void NoGpu_has_gpuPresent_false_and_every_other_member_null()
    {
        ProbeResult none = ProbeResult.NoGpu;

        Assert.False(none.GpuPresent);
        Assert.Null(none.Name);
        Assert.Null(none.GpuUuid);
        Assert.Null(none.DriverVersion);
        Assert.Null(none.ComputeCapability);
        Assert.Null(none.VbiosVersion);
        Assert.Null(none.PowerLimitW);
        Assert.Null(none.MigMode);
        Assert.Null(none.EnergyCounterSupported);
        Assert.Null(none.Method);
        Assert.Null(none.SampleRateHz);
        Assert.False(none.IsFallback);
    }

    [Theory]
    [InlineData(true, MeterMethodSetting.Auto, "counter", false)]
    [InlineData(true, MeterMethodSetting.Sampling, "sampling", false)]
    [InlineData(false, MeterMethodSetting.Auto, "sampling", true)]
    [InlineData(false, MeterMethodSetting.Sampling, "sampling", false)]
    public void Create_maps_the_identity_the_counter_state_and_the_selector_outcome(bool counter, MeterMethodSetting setting, string method, bool fallback)
    {
        var identity = new GpuIdentity("GPU", "GPU-1", "1.0", VbiosVersion: null, ComputeCapability: "8.6", PowerLimitWatts: 85.059, MigMode: "enabled");
        Assert.True(MeterMethodSelector.TrySelect(setting, counter, counter ? null : "counter-not-supported: x", 10.0, out MeterCapabilities? capabilities));

        ProbeResult result = ProbeResult.Create(identity, counter, capabilities, 10.0);

        Assert.Equal(new ProbeResult(true, "GPU", "GPU-1", "1.0", "8.6", null, 85.059, "enabled", counter, method, 10.0) { IsFallback = fallback }, result);
    }

    [Fact]
    public void Create_without_capabilities_keeps_the_facts_and_leaves_the_method_null()
    {
        var identity = new GpuIdentity("GPU", "GPU-1", "1.0", "94.02", "8.6", 85.0, "disabled");

        ProbeResult result = ProbeResult.Create(identity, energyCounterSupported: false, capabilities: null, sampleRateHz: 5.0);

        Assert.True(result.GpuPresent);
        Assert.Null(result.Method);
        Assert.False(result.EnergyCounterSupported);
        Assert.False(result.IsFallback);
        Assert.Equal("GPU", result.Name);
        Assert.Equal(5.0, result.SampleRateHz);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public async Task An_empty_or_blank_string_from_NVML_is_N_A_in_text_and_null_in_JSON(string blank)
    {
        ScriptedNvmlApi api = NewGpu();
        api.Name = blank;
        api.Uuid = blank;
        api.DriverVersion = blank;
        api.Vbios = blank;

        CliRunResult text = await RunAsync("probe", api);
        CliRunResult json = await RunAsync("probe --json", api);

        Assert.Equal(0, text.ExitCode);
        Assert.Equal(
            Expected((0, "GPU name: N/A"), (1, "GPU UUID: N/A"), (2, "Driver version: N/A"), (4, "VBIOS version: N/A")),
            Lines(text.Stdout));
        Assert.Equal(0, json.ExitCode);
        using JsonDocument document = ParseOneObject(json.Stdout);
        foreach (string key in new[] { "name", "gpuUuid", "driverVersion", "vbiosVersion" })
        {
            Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty(key).ValueKind);
        }
    }

    [Fact]
    public async Task A_power_limit_of_0_mW_is_N_A_in_text_and_null_in_JSON()
    {
        ScriptedNvmlApi api = NewGpu();
        api.PowerLimitMilliwatts = 0;

        CliRunResult text = await RunAsync("probe", api);
        CliRunResult json = await RunAsync("probe --json", api);

        Assert.Equal(0, text.ExitCode);
        Assert.Equal(Expected((5, "Power limit W: N/A")), Lines(text.Stdout));
        Assert.Equal(0, json.ExitCode);
        using JsonDocument document = ParseOneObject(json.Stdout);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("powerLimitW").ValueKind);
    }

    [Fact]
    public async Task A_name_with_a_line_break_cannot_forge_another_label_line_in_text()
    {
        ScriptedNvmlApi api = NewGpu();
        api.Name = "Evil GPU\nMethod: counter";

        CliRunResult text = await RunAsync("probe", api);
        CliRunResult json = await RunAsync("probe --json", api);

        Assert.Equal(0, text.ExitCode);
        string[] lines = Lines(text.Stdout);
        AssertLabelsInOrder(lines);
        Assert.Equal("GPU name: Evil GPU Method: counter", lines[0]);

        // The JSON keeps the exact value; System.Text.Json escapes the line break.
        Assert.Equal(0, json.ExitCode);
        using JsonDocument document = ParseOneObject(json.Stdout);
        Assert.Equal("Evil GPU\nMethod: counter", document.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public async Task A_name_with_escape_and_bell_characters_puts_neither_in_stdout()
    {
        ScriptedNvmlApi api = NewGpu();
        api.Name = "\u001b[31mRed\u0007 GPU";

        CliRunResult text = await RunAsync("probe", api);
        CliRunResult json = await RunAsync("probe --json", api);

        Assert.Equal(0, text.ExitCode);
        Assert.DoesNotContain('\u001b', text.Stdout);
        Assert.DoesNotContain('\u0007', text.Stdout);
        Assert.Equal(10, Lines(text.Stdout).Length);
        Assert.Equal(0, json.ExitCode);
        Assert.DoesNotContain('\u001b', json.Stdout);
        Assert.DoesNotContain('\u0007', json.Stdout);
        using JsonDocument document = ParseOneObject(json.Stdout);
        Assert.Equal("\u001b[31mRed\u0007 GPU", document.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public async Task A_stdout_writer_that_fails_leaves_the_NVML_session_closed_and_exits_1_with_one_error_line()
    {
        ScriptedNvmlApi api = NewGpu();
        using var failing = new ThrowOnceWriter();

        CliRunResult result = await CliHarness.RunAsync("probe", ScriptedGpuOverrides.Create(api), stdout: failing);

        Assert.Equal(1, result.ExitCode);
        Assert.Single(result.ErrorLines);
        Assert.Equal(api.InitCallCount, api.ShutdownCallCount);
        Assert.False(api.IsInitialised, "probe left the NVML session open");
    }

    [Fact]
    public async Task A_stderr_writer_that_fails_during_the_disclosure_leaves_the_NVML_session_closed_and_exits_1_with_one_error_line()
    {
        ScriptedNvmlApi api = NewGpu();
        api.CounterSupported = false;
        using var failing = new ThrowOnceWriter();

        // Auto with an unsupported counter writes the fallback disclosure, the first write to stderr; the writer throws on it and then recovers.
        CliRunResult result = await CliHarness.RunAsync("probe", ScriptedGpuOverrides.Create(api), stderr: failing);

        Assert.Equal(1, result.ExitCode);
        Assert.Single(result.ErrorLines);
        Assert.Equal(string.Empty, result.Stdout);
        Assert.Equal(api.InitCallCount, api.ShutdownCallCount);
        Assert.False(api.IsInitialised, "probe left the NVML session open");
    }

    [Fact]
    public void Create_turns_empty_and_blank_strings_into_null()
    {
        var identity = new GpuIdentity("", "   ", "\t", VbiosVersion: "", ComputeCapability: " ", PowerLimitWatts: 85.0, MigMode: "");

        ProbeResult result = ProbeResult.Create(identity, energyCounterSupported: false, capabilities: null, sampleRateHz: 10.0);

        Assert.True(result.GpuPresent);
        Assert.Null(result.Name);
        Assert.Null(result.GpuUuid);
        Assert.Null(result.DriverVersion);
        Assert.Null(result.VbiosVersion);
        Assert.Null(result.ComputeCapability);
        Assert.Null(result.MigMode);
        Assert.Equal(85.0, result.PowerLimitW);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void Create_turns_a_power_limit_that_is_not_finite_and_positive_into_null(double limit)
    {
        var identity = new GpuIdentity("GPU", "GPU-1", "1.0", "94.02", "8.6", limit, "disabled");

        ProbeResult result = ProbeResult.Create(identity, energyCounterSupported: false, capabilities: null, sampleRateHz: 10.0);

        Assert.Null(result.PowerLimitW);
        Assert.Equal("GPU", result.Name);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void Create_turns_a_sample_rate_that_is_not_finite_and_positive_into_null(double rate)
    {
        var identity = new GpuIdentity("GPU", "GPU-1", "1.0", "94.02", "8.6", 85.0, "disabled");

        ProbeResult result = ProbeResult.Create(identity, energyCounterSupported: false, capabilities: null, sampleRateHz: rate);

        Assert.Null(result.SampleRateHz);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Create_rejects_a_counter_state_that_contradicts_the_capabilities(bool capabilitiesCounter, bool stated)
    {
        var identity = new GpuIdentity("GPU", "GPU-1", "1.0", "94.02", "8.6", 85.0, "disabled");
        Assert.True(MeterMethodSelector.TrySelect(MeterMethodSetting.Auto, capabilitiesCounter, capabilitiesCounter ? null : "counter-not-supported: x", 10.0, out MeterCapabilities? capabilities));

        Assert.Throws<ArgumentException>(() => ProbeResult.Create(identity, stated, capabilities, 10.0));
    }

    [Fact]
    public void Create_rejects_a_supported_counter_without_capabilities()
    {
        var identity = new GpuIdentity("GPU", "GPU-1", "1.0", "94.02", "8.6", 85.0, "disabled");

        // No capabilities means the counter was forced and is unsupported; a supported counter cannot come with it.
        Assert.Throws<ArgumentException>(() => ProbeResult.Create(identity, energyCounterSupported: true, capabilities: null, sampleRateHz: 10.0));
    }

    // AJ-037 (criteria 1, 3 to 6): --save and --suffix. Each test owns a TempWorkspace that is the content root, so results/ resolves under it,
    // and a FakeTimeProvider at 2026-10-19T09:30:15Z as the host clock.
    public static TheoryData<string> InvalidSuffixes =>
    [
        "../x",
        "a/b",
        "a\\b",
        ".",
        "..",
        string.Empty,
        new string('a', 21),
        "cloud gpu",
        "cloud-gpu",
        "a.b",
        "caf" + (char)0xE9,
        "a" + (char)27 + "b",
        new string('9', 100),
    ];

    private static Task<CliRunResult> RunSaveAsync(string arguments, TempWorkspace workspace, ScriptedNvmlApi? api = null, TimeProvider? clock = null) =>
        CliHarness.RunAsync(arguments, ProbeSaveFixtures.Overrides(api ?? NewGpu(), workspace, clock ?? new FakeTimeProvider(ProbeSaveFixtures.FixedInstant)));

    private static byte[] SavedBytes(TempWorkspace workspace, string name) => File.ReadAllBytes(ProbeSaveFixtures.FilePath(workspace, name));

    // The one "saved: results/<name>" line of stderr.
    private static string SavedLine(CliRunResult result) =>
        Assert.Single(result.StderrLines, line => line.StartsWith("saved:", StringComparison.Ordinal));

    // A refused save creates nothing: not the results folder, not a file beside it.
    private static void AssertNothingWritten(TempWorkspace workspace)
    {
        Assert.False(Directory.Exists(ProbeSaveFixtures.ResultsPath(workspace)), "the results folder was created");
        Assert.Equal(["config"], ProbeSaveFixtures.RootEntries(workspace));
    }

    [Fact]
    public async Task Save_with_JSON_writes_the_timestamped_file_equal_to_stdout_and_reports_it_on_stderr()
    {
        using TempWorkspace workspace = TempWorkspace.Create();

        CliRunResult result = await RunSaveAsync("probe --json --save", workspace);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["probe-20261019T093015Z.json"], ProbeSaveFixtures.SavedNames(workspace));
        Assert.Equal(Encoding.UTF8.GetBytes(result.Stdout), SavedBytes(workspace, "probe-20261019T093015Z.json"));
        Assert.Equal("saved: results/probe-20261019T093015Z.json", SavedLine(result));
        Assert.Empty(result.ErrorLines);
    }

    [Fact]
    public async Task Save_without_JSON_writes_the_JSON_file_while_stdout_stays_the_text_form()
    {
        using TempWorkspace workspace = TempWorkspace.Create();
        CliRunResult plain = await RunAsync("probe --json", NewGpu());

        CliRunResult result = await RunSaveAsync("probe --save", workspace);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(ScriptedLines, Lines(result.Stdout));
        Assert.DoesNotContain("saved:", result.Stdout, StringComparison.Ordinal);
        Assert.Equal(Encoding.UTF8.GetBytes(plain.Stdout), SavedBytes(workspace, "probe-20261019T093015Z.json"));
        Assert.Equal("saved: results/probe-20261019T093015Z.json", SavedLine(result));
    }

    [Theory]
    [InlineData("cloudgpu")]
    [InlineData("a")]
    [InlineData("X9")]
    [InlineData("A1b2C3d4E5f6G7h8I9j0")]
    public async Task Suffix_is_appended_to_the_timestamped_file_name(string suffix)
    {
        using TempWorkspace workspace = TempWorkspace.Create();
        string name = $"probe-20261019T093015Z-{suffix}.json";

        CliRunResult result = await RunSaveAsync($"probe --json --save --suffix {suffix}", workspace);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal([name], ProbeSaveFixtures.SavedNames(workspace));
        Assert.Equal(Encoding.UTF8.GetBytes(result.Stdout), SavedBytes(workspace, name));
        Assert.Equal("saved: results/" + name, SavedLine(result));
    }

    [Fact]
    public async Task Saved_names_are_exact_for_the_fake_clock_and_never_match_the_run_file_pattern()
    {
        using TempWorkspace plain = TempWorkspace.Create();
        using TempWorkspace suffixed = TempWorkspace.Create();

        CliRunResult plainRun = await RunSaveAsync("probe --save", plain);
        CliRunResult suffixedRun = await RunSaveAsync("probe --save --suffix cloudgpu", suffixed);

        Assert.Equal(0, plainRun.ExitCode);
        Assert.Equal(0, suffixedRun.ExitCode);
        string[] plainNames = ProbeSaveFixtures.SavedNames(plain);
        string[] suffixedNames = ProbeSaveFixtures.SavedNames(suffixed);
        Assert.Equal(["probe-20261019T093015Z.json"], plainNames);
        Assert.Equal(["probe-20261019T093015Z-cloudgpu.json"], suffixedNames);

        // The pattern itself recognises a real run file of the same second, so the probe names escape it by their shape, not by a broken regex.
        Assert.Matches(RunFileName(), "20261019T093015Z-cloudgpu.json");
        foreach (string name in plainNames.Concat(suffixedNames))
        {
            Assert.DoesNotMatch(RunFileName(), name);
            Assert.Matches(ProbeFileName(), name);
        }
    }

    [Theory]
    [InlineData("probe --suffix cloudgpu")]
    [InlineData("probe --json --suffix cloudgpu")]
    public async Task Suffix_without_save_exits_2_with_one_error_line_naming_both_options_and_writes_nothing(string arguments)
    {
        using TempWorkspace workspace = TempWorkspace.Create();
        ScriptedNvmlApi api = NewGpu();

        CliRunResult result = await RunSaveAsync(arguments, workspace, api);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(string.Empty, result.Stdout);
        string line = Assert.Single(result.ErrorLines);
        Assert.Contains("--suffix", line, StringComparison.Ordinal);
        Assert.Contains("--save", line, StringComparison.Ordinal);
        Assert.EndsWith("(ConfigurationException)", line, StringComparison.Ordinal);
        AssertNothingWritten(workspace);

        // The usage error comes before the GPU is opened.
        Assert.Equal(0, api.InitCallCount);
    }

    [Theory]
    [MemberData(nameof(InvalidSuffixes))]
    public async Task A_suffix_outside_the_plain_name_rule_exits_2_names_the_option_and_writes_nothing(string suffix)
    {
        using TempWorkspace workspace = TempWorkspace.Create();
        ScriptedNvmlApi api = NewGpu();

        CliRunResult result = await RunSaveAsync($"probe --json --save --suffix \"{suffix}\"", workspace, api);

        AssertSuffixRefused(result, workspace, api, suffix);
    }

    [Theory]
    [InlineData("abc\n")]
    [InlineData("abc\r\n")]
    [InlineData("abc\r")]
    public async Task A_suffix_with_a_trailing_line_terminator_is_refused_because_the_rule_is_anchored_at_the_end_of_the_text(string suffix)
    {
        using TempWorkspace workspace = TempWorkspace.Create();
        ScriptedNvmlApi api = NewGpu();

        // The harness splits a command line on whitespace, so the tokens go in as they are.
        CliRunResult result = await CliHarness.RunTokensAsync(
            ["probe", "--json", "--save", "--suffix", suffix],
            ProbeSaveFixtures.Overrides(api, workspace, new FakeTimeProvider(ProbeSaveFixtures.FixedInstant)));

        AssertSuffixRefused(result, workspace, api, suffix);
    }

    // Exit 2, the option and the rule named, the echo capped at 16 characters without a control character or a path, nothing written, no GPU opened.
    private static void AssertSuffixRefused(CliRunResult result, TempWorkspace workspace, ScriptedNvmlApi api, string suffix)
    {
        Assert.Equal(2, result.ExitCode);
        Assert.Equal(string.Empty, result.Stdout);
        string line = Assert.Single(result.ErrorLines);
        Assert.Contains("--suffix", line, StringComparison.Ordinal);
        Assert.Contains("[A-Za-z0-9]{1,20}", line, StringComparison.Ordinal);
        if (suffix.Length > 16)
        {
            Assert.DoesNotContain(suffix[..17], result.Stderr, StringComparison.Ordinal);
        }

        Assert.DoesNotContain((char)27, result.Stderr);
        Assert.DoesNotContain("results", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(workspace.Root, result.Stderr, StringComparison.Ordinal);
        AssertNothingWritten(workspace);

        // The usage error comes before the GPU is opened.
        Assert.Equal(0, api.InitCallCount);
    }

    [Fact]
    public async Task Save_creates_the_results_folder_when_it_is_missing_and_keeps_the_files_already_in_it()
    {
        using TempWorkspace missing = TempWorkspace.Create();
        Assert.False(Directory.Exists(ProbeSaveFixtures.ResultsPath(missing)), "the workspace already has a results folder");
        using TempWorkspace existing = TempWorkspace.Create();
        existing.WriteFile(Path.Combine("results", "keep.txt"), "keep\n");

        CliRunResult first = await RunSaveAsync("probe --save", missing);
        CliRunResult second = await RunSaveAsync("probe --save", existing);

        Assert.Equal(0, first.ExitCode);
        Assert.True(Directory.Exists(ProbeSaveFixtures.ResultsPath(missing)), "the results folder was not created");
        Assert.Equal(["probe-20261019T093015Z.json"], ProbeSaveFixtures.SavedNames(missing));
        Assert.Equal(0, second.ExitCode);
        Assert.Equal(["keep.txt", "probe-20261019T093015Z.json"], ProbeSaveFixtures.SavedNames(existing));
        Assert.Equal("keep\n", File.ReadAllText(ProbeSaveFixtures.FilePath(existing, "keep.txt")));
    }

    [Theory]
    [InlineData("probe --json --save", "probe-20261019T093015Z.json")]
    [InlineData("probe --save", "probe-20261019T093015Z.json")]
    [InlineData("probe --json --save --suffix cloudgpu", "probe-20261019T093015Z-cloudgpu.json")]
    public async Task An_existing_file_is_never_overwritten_exit_1_with_file_exists_and_nothing_on_stdout(string arguments, string name)
    {
        using TempWorkspace workspace = TempWorkspace.Create();
        workspace.WriteFile(Path.Combine("results", name), "{\"preexisting\": true}\n");
        byte[] before = SavedBytes(workspace, name);

        CliRunResult result = await RunSaveAsync(arguments, workspace);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal($"error: file exists: results/{name} (InvalidOperationException)", Assert.Single(result.ErrorLines));

        // The save happens before any stdout write, so a failed save leaves stdout empty, JSON or text.
        Assert.Equal(string.Empty, result.Stdout);
        Assert.DoesNotContain("saved:", result.Stderr, StringComparison.Ordinal);
        Assert.Equal(before, SavedBytes(workspace, name));
        Assert.Equal([name], ProbeSaveFixtures.SavedNames(workspace));
    }

    [Fact]
    public async Task A_second_save_in_the_same_second_exits_1_and_a_save_one_second_later_writes_a_second_file()
    {
        using TempWorkspace workspace = TempWorkspace.Create();
        var clock = new FakeTimeProvider(ProbeSaveFixtures.FixedInstant);

        CliRunResult first = await RunSaveAsync("probe --json --save", workspace, clock: clock);
        byte[] original = SavedBytes(workspace, "probe-20261019T093015Z.json");
        CliRunResult sameSecond = await RunSaveAsync("probe --json --save", workspace, clock: clock);
        clock.Advance(TimeSpan.FromSeconds(1));
        CliRunResult nextSecond = await RunSaveAsync("probe --json --save", workspace, clock: clock);

        Assert.Equal(0, first.ExitCode);
        Assert.Equal(1, sameSecond.ExitCode);
        Assert.Contains("file exists", Assert.Single(sameSecond.ErrorLines), StringComparison.Ordinal);
        Assert.Equal(0, nextSecond.ExitCode);
        Assert.Equal(["probe-20261019T093015Z.json", "probe-20261019T093016Z.json"], ProbeSaveFixtures.SavedNames(workspace));
        Assert.Equal(original, SavedBytes(workspace, "probe-20261019T093015Z.json"));
    }

    [Theory]
    [InlineData("missing-library")]
    [InlineData("no-device")]
    [InlineData("init-failed")]
    [Trait("Spec", "EDGE-001")]
    public async Task A_no_GPU_save_writes_gpuPresent_false_with_every_other_key_null_and_exits_0(string cause)
    {
        using TempWorkspace workspace = TempWorkspace.Create();

        CliRunResult result = await RunSaveAsync("probe --json --save", workspace, NewNoGpu(cause));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["probe-20261019T093015Z.json"], ProbeSaveFixtures.SavedNames(workspace));
        byte[] saved = SavedBytes(workspace, "probe-20261019T093015Z.json");
        Assert.Equal(Encoding.UTF8.GetBytes(result.Stdout), saved);
        using JsonDocument document = ParseOneObject(Encoding.UTF8.GetString(saved));
        Assert.Equal(JsonKeys, Keys(document));
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            Assert.Equal(property.Name == "gpuPresent" ? JsonValueKind.False : JsonValueKind.Null, property.Value.ValueKind);
        }
    }

    [Fact]
    [Trait("Spec", "EDGE-001")]
    public async Task A_no_GPU_save_in_text_form_prints_no_GPU_and_still_saves_the_JSON_record()
    {
        using TempWorkspace workspace = TempWorkspace.Create();

        CliRunResult result = await RunSaveAsync("probe --save", workspace, NewNoGpu("missing-library"));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("no GPU", Assert.Single(Lines(result.Stdout)));
        using JsonDocument document = ParseOneObject(Encoding.UTF8.GetString(SavedBytes(workspace, "probe-20261019T093015Z.json")));
        Assert.Equal(JsonValueKind.False, document.RootElement.GetProperty("gpuPresent").ValueKind);
        Assert.Equal("saved: results/probe-20261019T093015Z.json", SavedLine(result));
    }

    [Fact]
    public async Task Save_keeps_stdout_one_JSON_object_and_puts_the_saved_line_and_the_log_records_on_stderr_only()
    {
        using TempWorkspace workspace = TempWorkspace.Create();
        CliHostOverrides overrides = ProbeSaveFixtures.Overrides(
            NewGpu(),
            workspace,
            new FakeTimeProvider(ProbeSaveFixtures.FixedInstant),
            [("Logging:LogLevel:Default", "Debug")]);

        CliRunResult result = await CliHarness.RunAsync("probe --json --save", overrides);

        Assert.Equal(0, result.ExitCode);
        using JsonDocument document = ParseOneObject(result.Stdout);
        Assert.Equal(JsonKeys, Keys(document));
        Assert.DoesNotContain("saved:", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("Opening the GPU", result.Stdout, StringComparison.Ordinal);
        Assert.Equal("saved: results/probe-20261019T093015Z.json", SavedLine(result));
        Assert.Contains("Opening the GPU at index 0", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_saved_file_is_byte_identical_to_the_stdout_of_a_plain_probe_json_with_LF_and_one_trailing_newline()
    {
        using TempWorkspace workspace = TempWorkspace.Create();
        CliRunResult plain = await RunAsync("probe --json", NewGpu());

        CliRunResult result = await RunSaveAsync("probe --json --save", workspace);

        Assert.Equal(0, plain.ExitCode);
        Assert.Equal(0, result.ExitCode);
        byte[] saved = SavedBytes(workspace, "probe-20261019T093015Z.json");
        Assert.Equal(Encoding.UTF8.GetBytes(plain.Stdout), saved);

        // No byte order mark, no CR, exactly one newline at the end.
        Assert.Equal((byte)'{', saved[0]);
        Assert.DoesNotContain((byte)'\r', saved);
        string text = Encoding.UTF8.GetString(saved);
        Assert.EndsWith("\n}\n", text, StringComparison.Ordinal);
        Assert.False(text.EndsWith("\n\n", StringComparison.Ordinal), "the saved JSON ends with more than one newline");
    }

    [Fact]
    public async Task The_longest_valid_suffix_resolves_to_a_file_directly_inside_the_results_folder()
    {
        using TempWorkspace workspace = TempWorkspace.Create();
        string suffix = new('Z', 20);
        string name = $"probe-20261019T093015Z-{suffix}.json";

        CliRunResult result = await RunSaveAsync($"probe --json --save --suffix {suffix}", workspace);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal([name], ProbeSaveFixtures.SavedNames(workspace));
        string resultsFolder = Path.GetFullPath(ProbeSaveFixtures.ResultsPath(workspace));
        string file = Path.GetFullPath(ProbeSaveFixtures.FilePath(workspace, name));
        Assert.Equal(resultsFolder, Path.GetDirectoryName(file));
        Assert.Equal(["config", "results"], ProbeSaveFixtures.RootEntries(workspace));
    }

    [Fact]
    public async Task The_timestamp_is_UTC_and_invariant_whatever_the_local_zone_and_the_machine_culture_are()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            // The Thai culture formats years with the Buddhist calendar (2569), so a culture-bound format would not write 2026.
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("th-TH");
            var clock = new FakeTimeProvider(ProbeSaveFixtures.FixedInstant);
            clock.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("plus5", TimeSpan.FromHours(5), "plus5", "plus5"));
            using TempWorkspace workspace = TempWorkspace.Create();

            CliRunResult result = await RunSaveAsync("probe --save", workspace, clock: clock);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(["probe-20261019T093015Z.json"], ProbeSaveFixtures.SavedNames(workspace));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public async Task The_name_comes_from_the_UTC_value_of_the_clock_not_from_its_offset_clock_face()
    {
        using TempWorkspace workspace = TempWorkspace.Create();

        // The clock returns 14:30:15 at +05:00; the file must say 093015Z, the UTC time of that instant.
        CliRunResult result = await RunSaveAsync("probe --save", workspace, clock: OffsetClock.FivePlus);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["probe-20261019T093015Z.json"], ProbeSaveFixtures.SavedNames(workspace));
    }

    [Fact]
    public async Task A_results_path_that_is_a_file_exits_1_with_one_error_line_that_holds_no_absolute_path_and_leaves_the_file()
    {
        using TempWorkspace workspace = TempWorkspace.Create();
        workspace.WriteFile("results", "not a folder\n");

        CliRunResult result = await RunSaveAsync("probe --json --save", workspace);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.Stdout);
        string line = Assert.Single(result.ErrorLines);
        Assert.Equal("error: cannot write results/probe-20261019T093015Z.json: IOException (InvalidOperationException)", line);
        AssertNoAbsolutePath(result, workspace);
        Assert.Equal("not a folder\n", File.ReadAllText(ProbeSaveFixtures.ResultsPath(workspace)));
    }

    [Fact]
    public async Task A_directory_at_the_target_name_exits_1_as_file_exists_and_leaves_it_with_no_temporary_file_behind()
    {
        using TempWorkspace workspace = TempWorkspace.Create();
        string target = ProbeSaveFixtures.FilePath(workspace, "probe-20261019T093015Z.json");
        Directory.CreateDirectory(target);

        CliRunResult result = await RunSaveAsync("probe --json --save", workspace);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.Stdout);
        Assert.Equal("error: file exists: results/probe-20261019T093015Z.json (InvalidOperationException)", Assert.Single(result.ErrorLines));
        AssertNoAbsolutePath(result, workspace);
        Assert.True(Directory.Exists(target), "the directory at the target name is gone");

        // The hidden temporary file is removed when the move did not happen. A dangling file symbolic link at the final name is not tested:
        // creating one needs a privilege. Both reviewers measured it: the move never overwrites, so the save exits 1 with "file exists" and no link
        // target is created; this test shows the same refusal path for a directory. The temporary name carries a fresh GUID, so there is no name
        // for a create-new open to follow, and a results folder that is a junction is followed (harmless, nothing can be overwritten).
        Assert.Empty(Directory.GetFiles(ProbeSaveFixtures.ResultsPath(workspace)));
    }

    [Fact]
    public async Task A_foreign_temporary_file_in_results_does_not_block_the_save_and_is_left_intact()
    {
        using TempWorkspace workspace = TempWorkspace.Create();
        workspace.WriteFile(Path.Combine("results", ".probe-20261019T093015Z.tmp"), "stale");

        CliRunResult result = await RunSaveAsync("probe --json --save", workspace);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("saved: results/probe-20261019T093015Z.json", SavedLine(result));
        Assert.Equal(Encoding.UTF8.GetBytes(result.Stdout), SavedBytes(workspace, "probe-20261019T093015Z.json"));
        Assert.Equal("stale", File.ReadAllText(ProbeSaveFixtures.FilePath(workspace, ".probe-20261019T093015Z.tmp")));

        // Nothing else is left: the foreign file and the probe, no temporary file of the save.
        Assert.Equal([".probe-20261019T093015Z.tmp", "probe-20261019T093015Z.json"], ProbeSaveFixtures.SavedNames(workspace));
    }

    [Fact]
    public async Task A_failed_save_keeps_the_absolute_path_off_stderr_even_when_the_debug_log_prints_the_exception_chain()
    {
        using TempWorkspace workspace = TempWorkspace.Create();
        workspace.WriteFile("results", "not a folder\n");
        CliHostOverrides overrides = ProbeSaveFixtures.Overrides(NewGpu(), workspace, new FakeTimeProvider(ProbeSaveFixtures.FixedInstant), [("Logging:LogLevel:Default", "Debug")]);

        CliRunResult result = await CliHarness.RunAsync("probe --json --save", overrides);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Command failed", result.Stderr, StringComparison.Ordinal);

        // The Debug record prints the exception chain with its stack trace (source paths of the build), so only the workspace path is checked: a
        // framework exception passed on as the inner exception would put it here ("Cannot create '<root>\results' because ...").
        Assert.DoesNotContain(workspace.Root, result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("Cannot create", result.Stderr, StringComparison.Ordinal);
    }

    // No drive path and no user folder: the framework messages of a failed save carry them, and Save must not pass them on.
    private static void AssertNoAbsolutePath(CliRunResult result, TempWorkspace workspace)
    {
        Assert.DoesNotContain(":\\", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("\\Users", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("/Users", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(workspace.Root, result.Stderr, StringComparison.Ordinal);
    }

    // Throws IOException on its first write and takes every later write: a failing stream that recovers for the error line.
    private sealed class ThrowOnceWriter : TextWriter
    {
        private readonly StringBuilder _text = new();
        private bool _thrown;

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            if (!_thrown)
            {
                _thrown = true;
                throw new IOException("scripted write failure");
            }

            _text.Append(value);
        }

        public override string ToString() => _text.ToString();
    }
}