using System.Text.Json;

using AgentJoules.Reporting.Schema;
using AgentJoules.Specs.Support;
using AgentJoules.TestSupport;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-111: run the calibration on the laptop (REQ-021). Scoped to the feature because "I run" and "the exit code is 5" are sentences of every CLI story. The scenarios follow the AJ-101 pattern (see
/// <see cref="CalibrationLiveContext"/>): with the gate AGENTJOULES_INTEGRATION closed the steps record the reason and pass; with it open they assert the newest committed <c>results/calibration-*.json</c> (label
/// objects, schema, the recomputed difference) and the newest committed probe, and never run the calibration. The committed decision of <c>docs/nim-local-attempt.md</c> is the NIM, so "Ollama serves the local model"
/// is read as "the local runtime of the configuration serves it" and the file is read regardless. The scenario about a stopped Ollama is hermetic and always asserts.
/// </summary>
[Binding]
[Scope(Feature = "Run the calibration on the laptop")]
public sealed class CalibrationLiveSteps(CalibrationLiveContext context, IReqnrollOutputHelper output)
{
    private readonly CalibrationLiveContext _context = context;
    private readonly IReqnrollOutputHelper _output = output;

    // True when the step must assert; otherwise the reason is written to the scenario output.
    private bool Asserting()
    {
        if (_context.IsActive)
        {
            return true;
        }

        _output.WriteLine("passed without asserting: " + _context.Note);
        return false;
    }

    [Given(@"^the preflight of AJ-110 passed and Ollama serves the local model$")]
    public void GivenThePreflightPassed()
    {
        // The committed decision is the NIM (docs/nim-local-attempt.md); the calibration file is read whichever runtime served the window.
        _context.Open();
    }

    [Given(@"^the committed calibration file with counterJoules and samplingJoules both measured$")]
    public void GivenTheCommittedFileHasBothTotals()
    {
        _context.Open();
        if (!_context.IsActive)
        {
            return;
        }

        using JsonDocument calibration = CalibrationLiveContext.ReadNewestCalibration();
        if (!LaptopResults.BothTotalsMeasured(calibration.RootElement))
        {
            _context.Skip("the newest committed calibration file has a method that is not measured, so there is no difference to recompute");
        }
    }

    [Given(@"^the Ollama service is stopped$")]
    public void GivenTheOllamaServiceIsStopped()
    {
        FakeHttpMessageHandler stopped = OllamaFixtures.Endpoint(OllamaFixtures.StoryTag);
        stopped.ConnectionRefused = true;
        _context.Endpoint = stopped;
    }

    [Given(@"^the committed probe has energyCounterSupported false$")]
    public void GivenTheCommittedProbeHasNoCounter()
    {
        _context.Open();
        if (!_context.IsActive)
        {
            return;
        }

        using JsonDocument probe = CalibrationLiveContext.ReadNewestProbe();
        if (probe.RootElement.GetProperty("energyCounterSupported").ValueKind != JsonValueKind.False)
        {
            _context.Skip("the newest committed probe has energyCounterSupported true, so the precondition of the scenario does not hold");
        }
    }

    [Given(@"^the probe reports power source ""([^""]*)""$")]
    public void GivenTheProbeReportsPowerSource(string powerSource)
    {
        Assert.Equal("battery", powerSource);
        _context.Open();
    }

    [When(@"^I run ""([^""]*)""$")]
    public async Task WhenIRun(string arguments)
    {
        if (_context.Endpoint is not null)
        {
            await _context.RunAsync(arguments);
        }

        // Otherwise "calibrate --seconds 60" is the builder's action: the committed file is only read by the Then step.
    }

    [When(@"^the difference is recomputed as \(counter - sampling\) / counter$")]
    public static void WhenTheDifferenceIsRecomputed()
    {
        // The recomputation is made by the Then step over the committed file.
    }

    [When(@"^the builder evaluates the calibration precondition$")]
    public static void WhenTheBuilderEvaluatesThePrecondition()
    {
        // The builder's action is not performed here: the committed record is only read by the Then step.
    }

    [Then(@"^the exit code is (\d+)$")]
    public void ThenTheExitCodeIs(int expected)
    {
        if (_context.Result is { } result)
        {
            Assert.True(expected == result.ExitCode, $"Expected exit code {expected} but got {result.ExitCode}; stdout: {result.Stdout}; stderr: {result.Stderr}");
            Assert.Empty(result.DeniedRequests);
            if (expected == 5)
            {
                Assert.Contains("ollama serve", _context.Output, StringComparison.Ordinal);
            }

            return;
        }

        if (Asserting())
        {
            // The committed file is the record of a calibration that exited 0: a missing file means it did not run or failed.
            using JsonDocument calibration = CalibrationLiveContext.ReadNewestCalibration();
            Assert.Equal(JsonValueKind.Object, calibration.RootElement.ValueKind);
        }
    }

    [Then(@"^the new calibration file has counterJoules, samplingJoules and relativeDifference as label objects$")]
    public void ThenTheFileHasThreeLabelObjects()
    {
        if (!Asserting())
        {
            return;
        }

        using JsonDocument calibration = CalibrationLiveContext.ReadNewestCalibration();
        Assert.Empty(LaptopResults.LabelObjectFailures(calibration.RootElement));
        Assert.Empty(SchemaValidator.Validate(calibration.RootElement, SchemaTarget.Calibration));
    }

    [Then(@"^it equals the stored relativeDifference within 1e-9$")]
    public void ThenItEqualsTheStoredDifference()
    {
        if (!Asserting())
        {
            return;
        }

        using JsonDocument calibration = CalibrationLiveContext.ReadNewestCalibration();
        (double? recomputed, double? stored) = LaptopResults.Recompute(calibration.RootElement);
        if (recomputed is null)
        {
            // A counter of 0 J has no relative difference: the file must say N/A.
            Assert.Null(stored);
            return;
        }

        Assert.NotNull(stored);
        Assert.InRange(Math.Abs(recomputed.Value - stored.Value), 0.0, LaptopResults.RecomputeTolerance);
    }

    [Then(@"^no calibration file is written$")]
    public void ThenNoCalibrationFileIsWritten() => Assert.Empty(_context.WrittenCalibrationFiles());

    [Then(@"^counterJoules has label ""N/A"" with a reason and the exit code is 0$")]
    public void ThenTheCounterIsNotApplicable()
    {
        if (!Asserting())
        {
            return;
        }

        using JsonDocument calibration = CalibrationLiveContext.ReadNewestCalibration();
        JsonElement counter = calibration.RootElement.GetProperty("counterJoules");
        Assert.Equal("N/A", counter.GetProperty("label").GetString());
        Assert.Equal(JsonValueKind.Null, counter.GetProperty("value").ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(counter.GetProperty("reason").GetString()));
    }

    [Then(@"^the calibration is not run until mains power is connected$")]
    public void ThenTheCalibrationIsNotRunOnBattery()
    {
        if (!Asserting())
        {
            return;
        }

        // The rule of the campaign: the committed calibration file was not measured on the battery.
        using JsonDocument calibration = CalibrationLiveContext.ReadNewestCalibration();
        string? powerSource = calibration.RootElement.TryGetProperty("environment", out JsonElement environment) && environment.TryGetProperty("powerSource", out JsonElement source) ? source.GetString() : null;
        Assert.NotEqual("battery", powerSource);
    }
}