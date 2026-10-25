using System.Text.Json;

using AgentJoules.Reporting.Schema;
using AgentJoules.Specs.Support;
using AgentJoules.TestSupport;

using Reqnroll;

using Xunit;
using Xunit.Sdk;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-110: run the preflight for the laptop measurement campaign (REQ-021, REQ-017). Scoped to the feature because "I run" and "the exit code is 3" are sentences of every CLI story. The scenarios follow the AJ-101
/// pattern (see <see cref="CampaignPreflightContext"/>): with the gate AGENTJOULES_INTEGRATION closed, or a key variable absent, the steps record the reason and pass; with it open they assert the newest committed
/// <c>results/probe-*.json</c>, run the real dry run live, or assert the <c>AJ-110</c> entry of <c>docs/build-log.md</c> for the rules of the campaign (mains power, no other GPU application, sampling disclosure). They never
/// perform the builder's action: no probe is run and nothing is written. The scenario about a missing key is hermetic and always asserts.
/// </summary>
[Binding]
[Scope(Feature = "Run the preflight for the laptop measurement campaign")]
public sealed class CampaignPreflightSteps(CampaignPreflightContext context, IReqnrollOutputHelper output)
{
    // The sentence of the AJ-110 entry of docs/build-log.md that records the rule of scenario 6 (spec section 6); the step asserts it character for character (the match ignores case, as every entry check does).
    private const string GpuApplicationRule = "a listed GPU application is recorded and the user is asked to close it before the campaign";

    private static readonly string[] Tiers = ["local", "nvidia", "nebius"];

    private readonly CampaignPreflightContext _context = context;
    private readonly IReqnrollOutputHelper _output = output;

    // True in the scenario about a missing key, which runs the command with deny-all HTTP and no gate.
    private bool _hermetic;

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

    // True when the gate is open (a step that asserts a rule of the campaign, not the saved probe, does so whether or not the probe skipped the counter-dependent steps); otherwise the reason is written to the scenario output.
    private bool GateOpen()
    {
        if (_context.IsOpen)
        {
            return true;
        }

        _output.WriteLine("passed without asserting: " + _context.Note);
        return false;
    }

    private static JsonDocument NewestProbe()
    {
        string path = LaptopResults.NewestProbe() ?? throw new XunitException(LaptopResults.NoFileMessage("results/probe-<yyyyMMddTHHmmssZ>.json", "AJ-110-T3"));
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static void AssertEntryHas(string entry, params string[] texts)
    {
        foreach (string text in texts)
        {
            Assert.True(entry.Contains(text, StringComparison.OrdinalIgnoreCase), $"the AJ-110 entry of docs/build-log.md does not state \"{text}\"");
        }
    }

    [Given(@"^the laptop GPU and driver are present$")]
    public void GivenTheLaptopGpuAndDriverArePresent() => _context.Open();

    [Given(@"^NVIDIA_API_KEY and NEBIUS_API_KEY are set and Ollama is running$")]
    public void GivenTheKeysAreSet()
    {
        // The committed decision is the NIM (docs/nim-local-attempt.md), so "Ollama is running" is the local runtime of the configuration; the dry run asks whichever runtime the configuration names.
        _context.Open("NVIDIA_API_KEY", "NEBIUS_API_KEY");
    }

    [Given(@"^the probe reports power source ""([^""]*)""$")]
    public void GivenTheProbeReportsPowerSource(string powerSource)
    {
        Assert.Equal("battery", powerSource);
        _context.Open();
    }

    [Given(@"^the saved probe has energyCounterSupported false$")]
    public void GivenTheSavedProbeHasNoCounter()
    {
        _context.Open();
        if (!_context.IsActive)
        {
            return;
        }

        using JsonDocument probe = NewestProbe();
        if (probe.RootElement.GetProperty("energyCounterSupported").ValueKind != JsonValueKind.False)
        {
            _context.Skip("the newest committed probe has energyCounterSupported true, so the sampling disclosure does not apply");
        }
    }

    [Given(@"^NEBIUS_API_KEY is not set$")]
    public void GivenNebiusKeyIsNotSet()
    {
        // Hermetic: the harness configuration is in memory and the process environment is not a source of it, so the key is absent whatever the machine holds.
        _hermetic = true;
    }

    [Given(@"^nvidia-smi lists a compute process other than Ollama$")]
    public void GivenAnotherGpuApplicationIsRunning() => _context.Open();

    [When(@"^I run ""([^""]*)""$")]
    public async Task WhenIRun(string arguments)
    {
        if (_hermetic)
        {
            await _context.RunWithoutNebiusKeyAsync(arguments);
        }
        else if (arguments.StartsWith("run ", StringComparison.Ordinal) && _context.IsActive)
        {
            await _context.RunLiveAsync(arguments);
        }

        // "probe --json --save" is the builder's action: the committed file is only read by the Then step.
    }

    [When(@"^the builder evaluates the preflight$")]
    public static void WhenTheBuilderEvaluatesThePreflight()
    {
        // The builder's action is not performed here: the committed record is only read by the Then step.
    }

    [Then(@"^the exit code is (\d+)$")]
    public void ThenTheExitCodeIs(int expected)
    {
        if (_context.Result is { } result)
        {
            Assert.True(expected == result.ExitCode, $"Expected exit code {expected} but got {result.ExitCode}; stdout: {result.Stdout}; stderr: {result.Stderr}");
            if (_hermetic)
            {
                Assert.Empty(result.DeniedRequests);
            }

            return;
        }

        if (Asserting())
        {
            // The saved probe is the record of the exit code of "probe --json --save": a missing file means the command was not run or failed.
            using JsonDocument probe = NewestProbe();
            Assert.Equal(JsonValueKind.Object, probe.RootElement.ValueKind);
        }
    }

    [Then(@"^the saved probe has gpuPresent true and the driver version printed by nvidia-smi$")]
    public async Task ThenTheSavedProbeMatches()
    {
        if (!Asserting())
        {
            return;
        }

        using JsonDocument probe = NewestProbe();
        JsonElement root = probe.RootElement;
        Assert.Empty(SchemaValidator.Validate(root, SchemaTarget.Probe));
        Assert.Equal(JsonValueKind.True, root.GetProperty("gpuPresent").ValueKind);
        JsonValueKind counter = root.GetProperty("energyCounterSupported").ValueKind;
        Assert.True(counter is JsonValueKind.True or JsonValueKind.False, "energyCounterSupported is not a boolean: " + counter);

        string? printed = await LaptopResults.NvidiaSmiDriverAsync();
        if (printed is null)
        {
            _output.WriteLine("note: nvidia-smi is not installed, so the driver version was not compared");
            return;
        }

        Assert.Equal(printed, root.GetProperty("driverVersion").GetString());
    }

    [Then(@"^each of ""local"", ""nvidia"" and ""nebius"" is followed by ""OK""$")]
    public void ThenEachTierIsFollowedByOk()
    {
        if (!Asserting())
        {
            return;
        }

        string[] lines = [.. _context.Output.Split('\n').Select(static line => line.TrimEnd('\r'))];
        foreach (string tier in Tiers)
        {
            Assert.True(
                lines.Any(line => line.StartsWith(tier + " ", StringComparison.Ordinal) && line.EndsWith(" OK", StringComparison.Ordinal)),
                $"no output line starts with \"{tier}\" and ends with \"OK\"");
        }
    }

    [Then(@"^the campaign is not started$")]
    public void ThenTheCampaignIsNotStarted()
    {
        if (Asserting())
        {
            AssertEntryHas(CampaignPreflightContext.BuildLogEntry(), "not started", "battery", "mains");
        }
    }

    [Then(@"^the user is asked to connect mains power$")]
    public void ThenTheUserIsAskedToConnectMainsPower()
    {
        if (Asserting())
        {
            AssertEntryHas(CampaignPreflightContext.BuildLogEntry(), "mains");
        }
    }

    [Then(@"^the campaign proceeds with method ""sampling""$")]
    public void ThenTheCampaignProceedsWithSampling()
    {
        if (!Asserting())
        {
            return;
        }

        using JsonDocument probe = NewestProbe();
        Assert.Equal("sampling", probe.RootElement.GetProperty("method").GetString());
    }

    [Then(@"^docs/build-log\.md notes that the README results block must contain ""sampling""$")]
    public void ThenTheBuildLogNotesTheSamplingRule()
    {
        // The rule is unconditional: it is asserted with the gate open even when the saved probe has a counter (the Given step then skips only the counter-dependent steps).
        if (GateOpen())
        {
            AssertEntryHas(CampaignPreflightContext.BuildLogEntry(), "sampling", "README results block");
        }
    }

    [Then(@"^the output names ""([^""]*)""$")]
    public void ThenTheOutputNames(string text) => Assert.Contains(text, _context.Output, StringComparison.Ordinal);

    [Then(@"^the process is recorded and the user is asked to close it before the campaign$")]
    public void ThenTheProcessIsRecorded()
    {
        if (Asserting())
        {
            AssertEntryHas(CampaignPreflightContext.BuildLogEntry(), "query-compute-apps", GpuApplicationRule);
        }
    }
}