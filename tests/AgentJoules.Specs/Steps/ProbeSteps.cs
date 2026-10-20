using System.Text.Json;

using AgentJoules.TestSupport;

using Microsoft.Extensions.Time.Testing;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-035: the probe command's text and JSON output. Self-contained: its state lives here, not in ScenarioWorld. The class is scoped to the
/// feature because "I run ..." and "the exit code is ..." are shared sentences of every CLI story; a scoped binding wins inside this feature
/// and never matches another one. The GPU is a ScriptedNvmlApi behind the real NvmlGpuTelemetrySourceFactory, so the whole read path of the
/// binding runs; a missing NVML loader is the scripted API in missing-library mode. Nothing here sets a process environment variable: the
/// method and the rate go through the in-memory configuration of the harness.
/// </summary>
[Binding]
[Scope(Feature = "Print the probe result as text and JSON, including the no-GPU path")]
public sealed class ProbeSteps
{
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

    private readonly ScriptedNvmlApi _api = new(new FakeTimeProvider());
    private readonly Dictionary<string, string?> _configuration = new(StringComparer.Ordinal);
    private string _expectedName = "Scripted GPU 3080";
    private string _expectedDriver = "616.92";
    private CliRunResult? TextRun { get; set; }
    private CliRunResult? JsonRun { get; set; }

    private CliRunResult Text => TextRun ?? throw new InvalidOperationException("The text form was not run.");

    private CliRunResult Json => JsonRun ?? throw new InvalidOperationException("The JSON form was not run.");

    private static IReadOnlyList<string> Lines(string stdout)
    {
        Assert.EndsWith("\n", stdout, StringComparison.Ordinal);
        string body = stdout[..^1];
        return [.. body.Split('\n').Select(line => line.TrimEnd('\r'))];
    }

    private static JsonDocument ParseOneObject(string stdout)
    {
        JsonDocument document = JsonDocument.Parse(stdout);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        return document;
    }

    private Task<CliRunResult> RunAsync(string arguments) =>
        CliHarness.RunAsync(arguments, ScriptedGpuOverrides.Create(_api, _configuration.Select(pair => (pair.Key, pair.Value))));

    [Given(@"^a scripted GPU named ""([^""]*)"" with driver ""([^""]*)"" and a counter$")]
    public void GivenAScriptedGpu(string name, string driver)
    {
        _api.Name = name;
        _api.DriverVersion = driver;
        _api.CounterSupported = true;
        _expectedName = name;
        _expectedDriver = driver;
    }

    [Given(@"^the same scripted GPU$")]
    public void GivenTheSameScriptedGpu()
    {
        GivenAScriptedGpu(_expectedName, _expectedDriver);
    }

    [Given(@"^the NVML loader is faked as missing$")]
    public void GivenTheLoaderIsMissing()
    {
        _api.MissingLibrary = true;
    }

    [Given(@"^a scripted GPU whose counter is unsupported under method auto$")]
    public void GivenAnUnsupportedCounterUnderAuto()
    {
        _api.CounterSupported = false;
        _configuration["AgentJoules:Meter:Method"] = "auto";

        // Debug logging makes the host write real records, so the scenario proves that log lines stay on stderr, not only the disclosure.
        _configuration["Logging:LogLevel:Default"] = "Debug";
    }

    [Given(@"^the scripted VBIOS query returns an error$")]
    public void GivenTheVbiosQueryFails()
    {
        // ScriptedNvmlApi answers NVML_ERROR_NOT_SUPPORTED to the VBIOS query when the version is null.
        _api.Vbios = null;
    }

    [Given(@"^the counter is unsupported and Meter:Method is counter$")]
    public void GivenAForcedCounterOnAnUnsupportedGpu()
    {
        _api.CounterSupported = false;
        _configuration["AgentJoules:Meter:Method"] = "counter";
    }

    [Given(@"^the option ""--config"" points at a file that does not exist$")]
    public static void GivenTheConfigFileDoesNotExist()
    {
        // The harness workspace holds only config/appsettings.json, so nope.json does not exist; the When step names it.
    }

    [When(@"^I run ""([^""]*)""$")]
    public async Task WhenIRun(string arguments)
    {
        CliRunResult result = await RunAsync(arguments);
        TextRun = result;
        JsonRun = result;
    }

    [When(@"^I run ""([^""]*)"" and ""([^""]*)""$")]
    public async Task WhenIRunBoth(string textArguments, string jsonArguments)
    {
        TextRun = await RunAsync(textArguments);
        JsonRun = await RunAsync(jsonArguments);
    }

    [Then(@"^stdout has the ten labelled lines and the exit code is 0$")]
    public void ThenStdoutHasTheTenLines()
    {
        Assert.Equal(0, Text.ExitCode);
        IReadOnlyList<string> lines = Lines(Text.Stdout);
        Assert.Equal(TextLabels.Length, lines.Count);
        for (int i = 0; i < TextLabels.Length; i++)
        {
            Assert.StartsWith(TextLabels[i] + ": ", lines[i], StringComparison.Ordinal);
        }

        Assert.Equal("GPU name: " + _expectedName, lines[0]);
        Assert.Equal("Driver version: " + _expectedDriver, lines[2]);
        Assert.Equal("Energy counter supported: true", lines[7]);
    }

    [Then(@"^stdout is one JSON object with gpuPresent true and a boolean energyCounterSupported, and the exit code is 0$")]
    public void ThenStdoutIsOneJsonObject()
    {
        Assert.Equal(0, Json.ExitCode);
        using JsonDocument document = ParseOneObject(Json.Stdout);
        Assert.Equal(JsonKeys, document.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal(JsonValueKind.True, document.RootElement.GetProperty("gpuPresent").ValueKind);
        JsonValueKind counter = document.RootElement.GetProperty("energyCounterSupported").ValueKind;
        Assert.True(counter is JsonValueKind.True or JsonValueKind.False, "energyCounterSupported is not a boolean: " + counter);
    }

    [Then(@"^stdout is the single line ""no GPU"" and the exit code is 0$")]
    public void ThenStdoutIsNoGpu()
    {
        Assert.Equal(0, Text.ExitCode);
        Assert.Equal("no GPU", Assert.Single(Lines(Text.Stdout)));
    }

    [Then(@"^gpuPresent is false, every other field is null and the exit code is 0$")]
    public void ThenEveryOtherFieldIsNull()
    {
        Assert.Equal(0, Json.ExitCode);
        using JsonDocument document = ParseOneObject(Json.Stdout);
        Assert.Equal(JsonKeys, document.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            Assert.Equal(property.Name == "gpuPresent" ? JsonValueKind.False : JsonValueKind.Null, property.Value.ValueKind);
        }
    }

    [Then(@"^stdout parses as one JSON object and the fallback disclosure appears only on stderr$")]
    public void ThenStdoutIsCleanAndTheDisclosureIsOnStderr()
    {
        Assert.Equal(0, Json.ExitCode);
        using JsonDocument document = ParseOneObject(Json.Stdout);
        Assert.Equal("sampling", document.RootElement.GetProperty("method").GetString());
        Assert.DoesNotContain("Energy method", Json.Stdout, StringComparison.Ordinal);
        Assert.Contains("Energy method: sampling (counter unsupported)", Json.StderrLines);
        Assert.Contains("Opening the GPU at index 0", Json.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("Opening the GPU", Json.Stdout, StringComparison.Ordinal);
    }

    [Then(@"^the text line is ""VBIOS version: N/A"" and the JSON value is null$")]
    public void ThenVbiosIsNotAvailable()
    {
        Assert.Equal(0, Text.ExitCode);
        Assert.Contains("VBIOS version: N/A", Lines(Text.Stdout));

        Assert.Equal(0, Json.ExitCode);
        using JsonDocument document = ParseOneObject(Json.Stdout);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("vbiosVersion").ValueKind);
        Assert.Equal(_expectedName, document.RootElement.GetProperty("name").GetString());
    }

    [Then(@"^method is null, the exit code is 0 and stdout is still valid JSON$")]
    public void ThenTheMethodIsNull()
    {
        Assert.Equal(0, Json.ExitCode);
        using JsonDocument document = ParseOneObject(Json.Stdout);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("method").ValueKind);
        Assert.Equal(JsonValueKind.False, document.RootElement.GetProperty("energyCounterSupported").ValueKind);
        Assert.Equal(JsonValueKind.True, document.RootElement.GetProperty("gpuPresent").ValueKind);
    }

    [Then(@"^the exit code is 2$")]
    public void ThenTheExitCodeIs2()
    {
        Assert.Equal(2, Text.ExitCode);
        Assert.Equal(string.Empty, Text.Stdout);
    }
}