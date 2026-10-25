using System.Text.Json;

using AgentJoules.Reporting.Results;
using AgentJoules.Reporting.Schema;
using AgentJoules.Specs.Support;
using AgentJoules.TestSupport;
using AgentJoules.Workflow.Tasks;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-109: compute the counter versus sampling calibration (REQ-021). Scoped to the feature because sentences such as "I run ..." and "the exit code is 0" are reused by other stories with other meanings. The state lives
/// in <see cref="CalibrationContext"/>. Every scenario runs the real <c>calibrate</c> command through the real command line harness against the scripted GPU behind the real NVML binding and a loopback fake that stands
/// where the NIM container stands, on a fake clock, so the preflight, the window, the file writer and the exit codes are the real ones.
/// </summary>
[Binding]
[Scope(Feature = "Compute the counter versus sampling calibration")]
public sealed class CalibrationSteps(CalibrationContext context)
{
    // The story's literal values are reached to far less than this.
    private const double StoryTolerance = 1e-9;
    private const double CounterJoules = 100.0;
    private const double SamplingJoules = 98.0;
    private const string UnsupportedCounterReason = "counter-not-supported: NVML_ERROR_NOT_SUPPORTED";

    private readonly CalibrationContext _context = context;

    [Given(@"^a fake meter whose counter reads 100 J and whose sampling integral reads 98 J for the window$")]
    public void GivenTheCounterReads100AndTheSamplingIntegralReads98()
    {
        // The scripted GPU draws 1.634 W on every third tenth of a second and 1.633 W otherwise, which the samples integrate to 98 J over the 60 s window; a counter that runs 100 / 98 as fast counts 100 J over the same window,
        // so the relative difference is (100 - 98) / 100 = 0.02, the literal values of the story.
        _context.WattsAt = static t => ((long)Math.Round(t * 10, MidpointRounding.AwayFromZero)) % 3 == 0 ? 1.634 : 1.633;
        _context.CounterScale = CounterJoules / SamplingJoules;
    }

    [Given(@"^the fake NVML returns NOT_SUPPORTED for the energy counter$")]
    public void GivenTheCounterIsNotSupported() => _context.CounterNotSupported = true;

    [Given(@"^the fake counter reads 0 J for the window$")]
    public void GivenTheCounterReadsZero() => _context.CounterScale = 0.0;

    [Given(@"^the NVML loader is faked as missing$")]
    public void GivenTheLoaderIsMissing() => _context.LibraryMissing = true;

    [When(@"^I run ""([^""]*)""$")]
    public async Task WhenIRun(string arguments) => await _context.RunAsync(arguments).WaitAsync(TimeSpan.FromSeconds(90));

    [Then(@"^the file has counterJoules 100, samplingJoules 98 and relativeDifference 0\.02, each labelled ""measured""$")]
    public void ThenTheFileHasBothTotalsAndTheDifference()
    {
        using JsonDocument file = _context.ReadFile();
        JsonElement root = file.RootElement;
        foreach (string name in new[] { "counterJoules", "samplingJoules", "relativeDifference" })
        {
            Assert.Equal("measured", root.GetProperty(name).GetProperty("label").GetString());
        }

        Assert.Equal(CounterJoules, root.GetProperty("counterJoules").GetProperty("value").GetDouble(), StoryTolerance);
        Assert.Equal(SamplingJoules, root.GetProperty("samplingJoules").GetProperty("value").GetDouble(), StoryTolerance);
        Assert.Equal(0.02, root.GetProperty("relativeDifference").GetProperty("value").GetDouble(), StoryTolerance);
    }

    [Then(@"^a file matching ""calibration-<yyyyMMddTHHmmssZ>\.json"" exists in the output folder$")]
    public void ThenACalibrationFileExists()
    {
        string path = Assert.Single(_context.CalibrationFiles());
        Assert.True(CalibrationFile.IsValid(Path.GetFileName(path)), $"The name {Path.GetFileName(path)} is not a calibration file name.");
        Assert.Equal(CalibrationFile.Name(RunOrchestrationFixtures.CommandStartUtc), Path.GetFileName(path));
    }

    [Then(@"^it validates against the calibration schema definition$")]
    public void ThenItValidates()
    {
        using JsonDocument file = _context.ReadFile();
        Assert.Empty(SchemaValidator.Validate(file.RootElement, SchemaTarget.Calibration));
    }

    [Then(@"^the file names the first task file and seconds 60$")]
    public void ThenTheFileNamesTheFirstTask()
    {
        string tasks = Path.Combine(_context.Rig.Root, RunCommandHarness.TasksFolder);
        string first = TaskSetLoader.Load(tasks)[0].Name;
        using JsonDocument file = _context.ReadFile();
        Assert.Equal(first, file.RootElement.GetProperty("task").GetString());
        Assert.Equal(60, file.RootElement.GetProperty("seconds").GetInt32());
    }

    [Then(@"^counterJoules is null with label ""N/A"" and a non-empty reason$")]
    public void ThenTheCounterIsNotApplicable()
    {
        using JsonDocument file = _context.ReadFile();
        JsonElement counter = file.RootElement.GetProperty("counterJoules");
        Assert.Equal(JsonValueKind.Null, counter.GetProperty("value").ValueKind);
        Assert.Equal("N/A", counter.GetProperty("label").GetString());
        string? reason = counter.GetProperty("reason").GetString();
        Assert.False(string.IsNullOrWhiteSpace(reason));
        Assert.Equal(UnsupportedCounterReason, reason);
    }

    [Then(@"^relativeDifference is null with label ""N/A""$")]
    public void ThenTheDifferenceIsNotApplicable()
    {
        using JsonDocument file = _context.ReadFile();
        JsonElement difference = file.RootElement.GetProperty("relativeDifference");
        Assert.Equal(JsonValueKind.Null, difference.GetProperty("value").ValueKind);
        Assert.Equal("N/A", difference.GetProperty("label").GetString());
    }

    [Then(@"^relativeDifference is null with reason ""([^""]*)""$")]
    public void ThenTheDifferenceHasTheReason(string reason)
    {
        using JsonDocument file = _context.ReadFile();
        JsonElement difference = file.RootElement.GetProperty("relativeDifference");
        Assert.Equal(JsonValueKind.Null, difference.GetProperty("value").ValueKind);
        Assert.Equal(reason, difference.GetProperty("reason").GetString());
    }

    [Then(@"^the exit code is (\d+)$")]
    public void ThenTheExitCodeIs(int expected) =>
        Assert.True(_context.Result.ExitCode == expected, $"Expected exit code {expected} but got {_context.Result.ExitCode}; stderr: {_context.Result.Stderr}");

    [Then(@"^no calibration file exists$")]
    public void ThenNoCalibrationFileExists() => Assert.Empty(_context.CalibrationFiles());
}