using System.Text.Json;
using System.Text.RegularExpressions;

using AgentJoules.Core.Results;
using AgentJoules.Reporting.Readme;
using AgentJoules.Reporting.Render;
using AgentJoules.TestSupport;

using Reqnroll;

using Xunit;
using Xunit.Sdk;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-124 (EDGE-015): the energy method of the cloudgpu tier when the VM GPU gives no energy counter. The scenarios run the real <c>run</c>, <c>probe</c> and <c>calibrate</c> commands through <see cref="CloudGpuMeterRig"/>
/// (the production orchestrator on a fake clock, a scripted GPU whose counter is supported or not). Every step is scoped to the feature because sentences such as "I run" and "the exit code is 6" belong to other stories.
/// </summary>
public sealed partial class CloudGpuSteps
{
    private const string SamplingFeature = "Use power sampling when the energy counter is unsupported on the VM";
    private const string SamplingTierName = "cloudgpu";
    private const string CounterObjectZero = "\"counterJoules\":\\s*\\{\\s*\"value\":\\s*0[,.\\s}]";

    private static readonly TimeSpan SamplingHangGuard = TimeSpan.FromSeconds(60);

    private LocalRunRig? _samplingRig;
    private string? _samplingTier;
    private string? _samplingSummary;
    private string? _samplingReadme;

    [GeneratedRegex(@"^(?<stem>probe|calibration)-<yyyyMMddTHHmmssZ>-(?<suffix>[A-Za-z0-9]+)\.json$")]
    private static partial Regex SuffixedNamePattern();

    [AfterScenario]
    [Scope(Feature = SamplingFeature)]
    public void DisposeSamplingRig() => _samplingRig?.Dispose();

    private LocalRunRig SamplingRig => _samplingRig ?? throw new XunitException("The scenario has not set up the fake NVML yet.");

    private CliRunResult SamplingResult => SamplingRig.Result ?? throw new XunitException("The command has not run yet.");

    private string SamplingResultsFolder => Path.Combine(SamplingRig.Root, RunOrchestrationFixtures.ResultsFolder);

    private void StartSamplingRig(bool counterSupported, string? meterMethod = null, string? tier = null)
    {
        _samplingRig = CloudGpuMeterRig.Create(counterSupported, meterMethod);
        _samplingTier = tier;
    }

    private async Task RunSamplingAsync(string command)
    {
        // The calibrate command has no tier of its own: on the cloudgpu tier its file is named with the suffix, as the probe file is.
        if (_samplingTier == SamplingTierName && command.StartsWith("calibrate", StringComparison.Ordinal) && !command.Contains("--suffix", StringComparison.Ordinal))
        {
            command += " --suffix " + SamplingTierName;
        }

        _ = await SamplingRig.RunAsync(command).WaitAsync(SamplingHangGuard);
    }

    private string[] SamplingFiles(string prefix) =>
        Directory.Exists(SamplingResultsFolder) ? [.. Directory.EnumerateFiles(SamplingResultsFolder, prefix + "-*").Order(StringComparer.Ordinal)] : [];

    private JsonDocument SamplingCalibration() => JsonDocument.Parse(File.ReadAllText(Assert.Single(SamplingFiles("calibration"))));

    [Given(@"^the fake NVML supports the energy counter and the tier is ""cloudgpu""$")]
    [Scope(Feature = SamplingFeature)]
    public void GivenTheCounterIsSupportedOnCloudGpuSampling() => StartSamplingRig(counterSupported: true, tier: SamplingTierName);

    [Given(@"^the tier is ""cloudgpu""$")]
    [Scope(Feature = SamplingFeature)]
    public void GivenTheTierIsCloudGpuSampling() => StartSamplingRig(counterSupported: true, tier: SamplingTierName);

    [Given(@"^the fake NVML returns NOT_SUPPORTED for the counter and the tier is ""cloudgpu""$")]
    [Scope(Feature = SamplingFeature)]
    public void GivenTheCounterIsUnsupportedOnCloudGpuSampling() => StartSamplingRig(counterSupported: false, tier: SamplingTierName);

    [Given(@"^the fake NVML returns NOT_SUPPORTED for the counter$")]
    [Scope(Feature = SamplingFeature)]
    public void GivenTheCounterIsUnsupportedSampling() => StartSamplingRig(counterSupported: false);

    [Given(@"^Meter:Method is ""counter"" and the counter is unsupported$")]
    [Scope(Feature = SamplingFeature)]
    public void GivenAForcedCounterWithNoCounterSampling() => StartSamplingRig(counterSupported: false, meterMethod: "counter", tier: SamplingTierName);

    [Given(@"^Meter:Method is ""sampling"" and the counter is supported$")]
    [Scope(Feature = SamplingFeature)]
    public void GivenAForcedSamplingWithACounterSampling() => StartSamplingRig(counterSupported: true, meterMethod: "sampling", tier: SamplingTierName);

    [Given(@"^a cloudgpu run file with method ""sampling""$")]
    [Scope(Feature = SamplingFeature)]
    public async Task GivenACloudGpuRunFileWithMethodSamplingSampling()
    {
        StartSamplingRig(counterSupported: false, tier: SamplingTierName);
        await RunSamplingAsync(CloudGpuMeterRig.RunCommand);
        Assert.True(SamplingResult.ExitCode == 0, $"The run exited {SamplingResult.ExitCode}; stderr: {SamplingResult.Stderr}");
        using JsonDocument document = SamplingRig.ReadRunFile();
        Assert.All(CloudGpuMeterRig.ReadingSteps(document), static step => Assert.Equal("sampling", step.GetProperty("method").GetString()));
    }

    [When(@"^I run ""([^""]*)""$")]
    [Scope(Feature = SamplingFeature)]
    public Task WhenIRunSampling(string command) => RunSamplingAsync(command);

    [When(@"^I run ""([^""]*)"" and ""([^""]*)""$")]
    [Scope(Feature = SamplingFeature)]
    public async Task WhenIRunTwoCommandsSampling(string first, string second)
    {
        await RunSamplingAsync(first);
        Assert.True(SamplingResult.ExitCode == 0, $"The first command exited {SamplingResult.ExitCode}; stderr: {SamplingResult.Stderr}");
        await RunSamplingAsync(second);
        Assert.True(SamplingResult.ExitCode == 0, $"The second command exited {SamplingResult.ExitCode}; stderr: {SamplingResult.Stderr}");
    }

    [When(@"^the report and the README block are rendered$")]
    [Scope(Feature = SamplingFeature)]
    public async Task WhenTheReportAndTheReadmeBlockAreRenderedSampling()
    {
        _samplingSummary = await SamplingRig.SummaryAsync();
        RunDocument document = RunDocumentFixtures.Read(File.ReadAllText(Assert.Single(SamplingRig.RunFiles())));
        _samplingReadme = ReadmeResultsBlock.Render(ReportAggregator.Build([ReportRunFixtures.Wrap(document)]), dashboardScreenshotExists: false);
    }

    [Then(@"^every reading has method ""(counter|sampling)"" and no ""sampling-fallback"" flag$")]
    [Scope(Feature = SamplingFeature)]
    public void ThenEveryReadingHasMethodAndNoFlagSampling(string method)
    {
        Assert.True(SamplingResult.ExitCode == 0, $"The run exited {SamplingResult.ExitCode}; stderr: {SamplingResult.Stderr}");
        using JsonDocument document = SamplingRig.ReadRunFile();
        JsonElement[] steps = CloudGpuMeterRig.ReadingSteps(document);
        Assert.NotEmpty(steps);
        Assert.All(steps, step => Assert.Equal(method, step.GetProperty("method").GetString()));
        Assert.All(steps, static step => Assert.DoesNotContain("sampling-fallback", CloudGpuMeterRig.FlagsOf(step)));
        Assert.DoesNotContain(CloudGpuMeterRig.FallbackLine, SamplingResult.Stdout + SamplingResult.Stderr, StringComparison.Ordinal);
    }

    [Then(@"^every reading has method ""sampling"" and the flag ""sampling-fallback""$")]
    [Scope(Feature = SamplingFeature)]
    public void ThenEveryReadingHasSamplingAndTheFlagSampling()
    {
        Assert.True(SamplingResult.ExitCode == 0, $"The run exited {SamplingResult.ExitCode}; stderr: {SamplingResult.Stderr}");
        using JsonDocument document = SamplingRig.ReadRunFile();
        JsonElement[] steps = CloudGpuMeterRig.ReadingSteps(document);
        Assert.NotEmpty(steps);
        Assert.All(steps, static step => Assert.Equal("sampling", step.GetProperty("method").GetString()));
        Assert.All(steps, static step => Assert.Contains("sampling-fallback", CloudGpuMeterRig.FlagsOf(step)));
    }

    [Then(@"^the console shows the fallback line$")]
    [Scope(Feature = SamplingFeature)]
    public void ThenTheConsoleShowsTheFallbackLineSampling() => _ = Assert.Single(SamplingResult.StderrLines, static line => line == CloudGpuMeterRig.FallbackLine);

    [Then(@"^the files match ""(probe-<yyyyMMddTHHmmssZ>-cloudgpu\.json)"" and ""(calibration-<yyyyMMddTHHmmssZ>-cloudgpu\.json)""$")]
    [Scope(Feature = SamplingFeature)]
    public void ThenTheFilesMatchSampling(string probePattern, string calibrationPattern)
    {
        Assert.Matches(SuffixedNamePattern(), probePattern);
        Assert.Matches(SuffixedNamePattern(), calibrationPattern);
        Assert.Matches(@"^probe-\d{8}T\d{6}Z-cloudgpu\.json$", Path.GetFileName(Assert.Single(SamplingFiles("probe"))));
        Assert.Matches(@"^calibration-\d{8}T\d{6}Z-cloudgpu\.json$", Path.GetFileName(Assert.Single(SamplingFiles("calibration"))));
    }

    [Then(@"^counterJoules is null with label ""N/A"" and a non-empty reason$")]
    [Scope(Feature = SamplingFeature)]
    public void ThenCounterJoulesIsNotApplicableSampling()
    {
        Assert.True(SamplingResult.ExitCode == 0, $"The command exited {SamplingResult.ExitCode}; stderr: {SamplingResult.Stderr}");
        using JsonDocument file = SamplingCalibration();
        JsonElement counter = file.RootElement.GetProperty("counterJoules");
        Assert.Equal(JsonValueKind.Null, counter.GetProperty("value").ValueKind);
        Assert.Equal("N/A", counter.GetProperty("label").GetString());
        Assert.False(string.IsNullOrWhiteSpace(counter.GetProperty("reason").GetString()));
        Assert.Equal("measured", file.RootElement.GetProperty("samplingJoules").GetProperty("label").GetString());
    }

    [Then(@"^relativeDifference is null with label ""N/A""$")]
    [Scope(Feature = SamplingFeature)]
    public void ThenRelativeDifferenceIsNotApplicableSampling()
    {
        using JsonDocument file = SamplingCalibration();
        JsonElement difference = file.RootElement.GetProperty("relativeDifference");
        Assert.Equal(JsonValueKind.Null, difference.GetProperty("value").ValueKind);
        Assert.Equal("N/A", difference.GetProperty("label").GetString());
    }

    [Then(@"^counterJoules has a null value and the file contains no counterJoules value of 0$")]
    [Scope(Feature = SamplingFeature)]
    public void ThenCounterJoulesHasNoZeroSampling()
    {
        string text = File.ReadAllText(Assert.Single(SamplingFiles("calibration")));
        using JsonDocument file = JsonDocument.Parse(text);
        Assert.Equal(JsonValueKind.Null, file.RootElement.GetProperty("counterJoules").GetProperty("value").ValueKind);
        Assert.DoesNotMatch(CounterObjectZero, text);
    }

    [Then(@"^the header contains ""Energy method: sampling \(counter unsupported\)""$")]
    [Scope(Feature = SamplingFeature)]
    public void ThenTheHeaderContainsTheSamplingSentenceSampling()
    {
        Assert.Contains("Energy method: sampling (counter unsupported)", _samplingSummary, StringComparison.Ordinal);
        Assert.Contains(_samplingSummary!.Split('\n'), static line => line.StartsWith("- cloudgpu:", StringComparison.Ordinal));
    }

    [Then(@"^the cloudgpu README row contains ""sampling""$")]
    [Scope(Feature = SamplingFeature)]
    public void ThenTheReadmeRowContainsSamplingSampling()
    {
        string row = Assert.Single(_samplingReadme!.Split('\n'), static line => line.StartsWith("| cloudgpu |", StringComparison.Ordinal));
        Assert.Contains("sampling", row, StringComparison.Ordinal);
    }

    [Then(@"^the exit code is 6$")]
    [Scope(Feature = SamplingFeature)]
    public void ThenTheExitCodeIsSampling()
    {
        CliRunResult result = SamplingResult;
        Assert.True(result.ExitCode == 6, $"Expected exit code 6 but got {result.ExitCode}; stdout: {result.Stdout}; stderr: {result.Stderr}");
        Assert.Empty(SamplingRig.RunFiles());
    }
}