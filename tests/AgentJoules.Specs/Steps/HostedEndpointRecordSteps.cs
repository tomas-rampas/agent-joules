using System.Text.RegularExpressions;

using AgentJoules.Reporting.Endpoints;
using AgentJoules.Specs.Support;
using AgentJoules.TestSupport;
using AgentJoules.Workflow.Runs;

using Reqnroll;

using Xunit;
using Xunit.Sdk;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-101: the nvidia and nebius endpoint verification is recorded in <c>docs/endpoints.md</c> (REQ-018). The five scenarios about "the builder" follow the AJ-023 pattern: the Given step evaluates
/// <see cref="IntegrationGate.SkipReason"/> for AGENTJOULES_INTEGRATION and reads the named key variable (read, never set, never printed); when the gate is closed or the key is absent the remaining steps
/// record the reason and pass. With both present they assert the content of the committed <c>docs/endpoints.md</c> and never perform the builder's action: no call to a hosted endpoint is made here (the live
/// comparison is the gated <c>HostedEndpointListingTests</c>), and nothing is written. A scenario whose precondition the committed record does not meet (for example the nebius listing lacks the nvidia model, so the
/// scenario about the same model has nothing to compare) records that as its note and passes. The scenario about a missing key needs neither the gate nor a key: it runs the real command line through the harness,
/// whose configuration is in memory and whose HTTP is deny-all, so no process variable is read and no request can leave. The record formats the open-gate steps read are listed on
/// <see cref="HostedEndpointRecordContext"/>. Scoped to the feature because "the exit code is 3" and "I run ..." are sentences of every CLI story.
/// </summary>
[Binding]
[Scope(Feature = "Record the nvidia and nebius endpoint verification")]
public sealed partial class HostedEndpointRecordSteps(HostedEndpointRecordContext context, IReqnrollOutputHelper output)
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan OnboardingBox = TimeSpan.FromMinutes(45);

    private readonly HostedEndpointRecordContext _context = context;
    private readonly IReqnrollOutputHelper _output = output;

    private string[] _lines = [];
    private string[] _nvidiaChosen = [];
    private bool _preconditionHolds = true;
    private CliRunResult? _run;
    private RunCommandSetup? _setup;

    private EndpointsSection? LoadedSection { get; set; }

    private EndpointsSection Section => LoadedSection ?? throw new XunitException("The section was not loaded.");

    // True when the step must assert; otherwise the reason is written to the scenario output.
    private bool Asserting()
    {
        if (!_context.IsActive)
        {
            _output.WriteLine("skipped: " + _context.Note);
            return false;
        }

        if (!_preconditionHolds)
        {
            _output.WriteLine("passed without asserting: " + _context.Note);
            return false;
        }

        return true;
    }

    private void Load(string tier)
    {
        LoadedSection = HostedEndpointRecordContext.SectionOf(tier);
        _lines = HostedEndpointRecordContext.LinesOf(tier);
    }

    private void PreconditionFails(string note)
    {
        _preconditionHolds = false;
        _context.Skip(note);
    }

    [Given(@"^(NVIDIA_API_KEY|NEBIUS_API_KEY) is set in the environment$")]
    public void GivenTheKeyIsSetInTheEnvironment(string variable) => _context.Open(variable);

    [Given(@"^NEBIUS_API_KEY is set and the nebius listing contains the model chosen for nvidia$")]
    public void GivenNebiusKeyIsSetAndTheListingContainsTheNvidiaModel()
    {
        _context.Open("NEBIUS_API_KEY");
        if (!_context.IsActive)
        {
            return;
        }

        EndpointsSection nvidia = HostedEndpointRecordContext.SectionOf("nvidia");
        _nvidiaChosen = HostedEndpointRecordContext.ChosenAgentModels(nvidia, HostedEndpointRecordContext.LinesOf("nvidia"));
        EndpointsSection nebius = HostedEndpointRecordContext.SectionOf("nebius");
        if (_nvidiaChosen.Length == 0 || !_nvidiaChosen.All(nebius.Lists))
        {
            PreconditionFails("the committed nebius listing does not contain the model chosen for nvidia, so the scenario of the other branch applies");
        }
    }

    [Given(@"^the nebius listing lacks the nvidia model but lists two other Nemotron 3 models$")]
    public void GivenTheNebiusListingLacksTheNvidiaModel()
    {
        _context.Open("NEBIUS_API_KEY");
        if (!_context.IsActive)
        {
            return;
        }

        EndpointsSection nvidia = HostedEndpointRecordContext.SectionOf("nvidia");
        _nvidiaChosen = HostedEndpointRecordContext.ChosenAgentModels(nvidia, HostedEndpointRecordContext.LinesOf("nvidia"));
        EndpointsSection nebius = HostedEndpointRecordContext.SectionOf("nebius");
        int nemotron3 = nebius.ModelIds.Count(static id => id.Contains("nemotron-3", StringComparison.OrdinalIgnoreCase));
        if (_nvidiaChosen.Length == 0 || _nvidiaChosen.All(nebius.Lists) || nemotron3 < 2)
        {
            PreconditionFails("the committed nebius listing does not lack the nvidia model while listing two other Nemotron 3 models, so another scenario applies");
        }
    }

    [Given(@"^the nebius listing contains no model whose ID contains ""nemotron""$")]
    public void GivenTheNebiusListingContainsNoNemotronModel()
    {
        _context.Open("NEBIUS_API_KEY");
        if (_context.IsActive && HostedEndpointRecordContext.SectionOf("nebius").ModelIds.Any(static id => id.Contains("nemotron", StringComparison.OrdinalIgnoreCase)))
        {
            PreconditionFails("the committed nebius listing contains a Nemotron model, so the tier is not blocked");
        }
    }

    [Given(@"^NEBIUS_API_KEY is not set$")]
    public void GivenNebiusKeyIsNotSet()
    {
        // The harness configuration is in memory and the process environment is not a source of it, so the key is absent whatever the machine holds; the judge key is present so that the one missing variable is the named one.
        _setup = RunCommandSetup.Create([new KeyValuePair<string, string?>("NVIDIA_API_KEY", ProviderClientFixtures.FakeKey())]);
        Assert.False(_setup.Settings.ContainsKey("NEBIUS_API_KEY"));
    }

    [Given(@"^the nebius onboarding started more than 45 minutes ago without a listed model$")]
    public void GivenTheNebiusOnboardingStartedMoreThan45MinutesAgo()
    {
        _context.Open("NEBIUS_API_KEY");
        if (!_context.IsActive)
        {
            return;
        }

        string[] lines = HostedEndpointRecordContext.LinesOf("nebius");
        DateTimeOffset? started = HostedEndpointRecordContext.TimeOf(lines, "Onboarding started:");
        DateTimeOffset? ended = HostedEndpointRecordContext.TimeOf(lines, "Onboarding ended:");
        bool exceeded = started is { } begin && ended is { } end && end - begin > OnboardingBox;
        if (!exceeded || HostedEndpointRecordContext.SectionOf("nebius").ModelIds.Count > 0)
        {
            PreconditionFails("the committed nebius record shows no onboarding box that ran out without a listed model");
        }
    }

    [When(@"^the builder calls GET \{BaseUrl\}/models for the nvidia tier and writes the ""## nvidia"" section$")]
    public void WhenTheBuilderRecordsTheNvidiaSection()
    {
        if (Asserting())
        {
            Load("nvidia");
        }
    }

    [When(@"^the builder writes the ""## nebius"" section$")]
    public void WhenTheBuilderRecordsTheNebiusSection()
    {
        if (Asserting())
        {
            Load("nebius");
        }
    }

    [When(@"^the builder chooses the nebius model$")]
    public void WhenTheBuilderChoosesTheNebiusModel()
    {
        if (Asserting())
        {
            Load("nebius");
        }
    }

    [When(@"^the builder closes the box$")]
    public void WhenTheBuilderClosesTheBox()
    {
        if (Asserting())
        {
            Load("nebius");
        }
    }

    [When(@"^I run ""([^""]*)""$")]
    public async Task WhenIRun(string arguments)
    {
        RunCommandSetup setup = _setup ?? throw new XunitException("The scenario named no configuration.");
        _run = await RunCommandHarness.RunAsync(arguments, setup).WaitAsync(HangGuard);
    }

    [Then(@"^the section has ""Verified:"" with a UTC date of 2026-10-17 or later$")]
    public void ThenTheSectionHasAVerifiedDate()
    {
        if (Asserting())
        {
            DateOnly verified = Section.Verified ?? throw new XunitException("The section has no well-formed Verified line.");
            Assert.True(verified >= HostedEndpointRecordContext.EarliestVerification, $"Verified {verified:yyyy-MM-dd} is before the start of the build.");
        }
    }

    [Then(@"^the section lists every returned Nemotron and Qwen model as ""- model: <id>""$")]
    public void ThenTheSectionListsTheModels()
    {
        if (Asserting())
        {
            Assert.NotEmpty(Section.ModelIds);
            Assert.All(Section.ModelIds, id => Assert.True(id.Contains("nemotron", StringComparison.OrdinalIgnoreCase) || id.Contains("qwen", StringComparison.OrdinalIgnoreCase), $"The model line '{id}' is neither a Nemotron nor a Qwen model."));
            Assert.Contains(_lines, static line => line.StartsWith("Total models returned:", StringComparison.Ordinal));
        }
    }

    [Then(@"^the recorded command shows ""Bearer \*\*\*\*"" and no key value$")]
    public void ThenTheRecordedCommandShowsTheRedactedBearer()
    {
        if (Asserting())
        {
            Assert.Contains(_lines, static line => line.Contains("Bearer ****", StringComparison.Ordinal));

            // The assertions name the finding, never the text: a failure message must not repeat a key.
            string text = string.Join('\n', _lines);
            string? key = _context.KeyValue();
            Assert.False(key is not null && text.Contains(key, StringComparison.Ordinal), "The section contains the value of " + _context.KeyVariable + ".");
            Assert.False(KeyShapedValue().IsMatch(text), "The section contains a value shaped like an nvapi key.");
        }
    }

    [Then(@"^the chosen nebius model equals the chosen nvidia model$")]
    public void ThenTheChosenNebiusModelEqualsTheChosenNvidiaModel()
    {
        if (Asserting())
        {
            Assert.Equal(_nvidiaChosen, HostedEndpointRecordContext.ChosenAgentModels(Section, _lines));
        }
    }

    [Then(@"^""AJ validate --config"" exits 0$")]
    public async Task ThenValidateConfigExitsZero()
    {
        if (!Asserting())
        {
            return;
        }

        // The committed configuration and the committed document in a temporary workspace; the check is file-only.
        using TempWorkspace workspace = TempWorkspace.Create();
        _ = workspace.WriteFile(Path.Combine("docs", "endpoints.md"), HostedEndpointRecordContext.ReadCommittedDocument());
        CliRunResult result = await CliHarness.RunTokensAsync(["validate", "--config"], new AgentJoules.Cli.CliHostOverrides(WorkingDirectory: workspace.Root)).WaitAsync(HangGuard);

        Assert.True(result.ExitCode == 0, $"validate --config exited {result.ExitCode}; stdout: {result.Stdout}; stderr: {result.Stderr}");
        Assert.Empty(result.DeniedRequests);
    }

    [Then(@"^the smaller of the two is chosen and the reason is recorded in the section$")]
    public void ThenTheSmallerNemotron3ModelIsChosen()
    {
        if (!Asserting())
        {
            return;
        }

        string[] chosen = HostedEndpointRecordContext.ChosenAgentModels(Section, _lines);
        string[] nemotron3 = [.. Section.ModelIds.Where(static id => id.Contains("nemotron-3", StringComparison.OrdinalIgnoreCase))];
        string model = Assert.Single(chosen);
        Assert.Contains(model, nemotron3);
        double?[] sizes = [.. nemotron3.Select(HostedEndpointRecordContext.SizeInBillions)];
        if (sizes.All(static size => size is not null))
        {
            Assert.Equal(sizes.Min(), HostedEndpointRecordContext.SizeInBillions(model));
        }

        Assert.Contains(_lines, static line => line.Contains("smallest", StringComparison.OrdinalIgnoreCase));
    }

    [Then(@"^the section says ""BLOCKED"" and names the missing item$")]
    public void ThenTheSectionSaysBlockedAndNamesTheMissingItem()
    {
        if (Asserting())
        {
            string line = Assert.Single(_lines, static line => line.StartsWith("BLOCKED", StringComparison.Ordinal));
            Assert.Matches(@"^BLOCKED:?\s+\S", line);
        }
    }

    [Then(@"^the final report lists the nebius tier as blocked and not skipped$")]
    public void ThenTheNebiusTierIsBlockedAndNotSkipped()
    {
        if (Asserting())
        {
            Assert.Contains(_lines, static line => line.StartsWith("BLOCKED", StringComparison.Ordinal));
            Assert.DoesNotContain(_lines, static line => line.Contains("skipped", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Then(@"^the section records what was verified and what failed$")]
    public void ThenTheSectionRecordsWhatWasVerifiedAndWhatFailed()
    {
        if (Asserting())
        {
            Assert.Contains(_lines, static line => line.StartsWith("Box exceeded:", StringComparison.Ordinal));
            Assert.Contains(_lines, static line => line.Contains("verified", StringComparison.OrdinalIgnoreCase) && !line.StartsWith("Verified:", StringComparison.Ordinal));
            Assert.Contains(_lines, static line => line.Contains("failed", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Then(@"^the build is reported blocked on the nebius item$")]
    public void ThenTheBuildIsReportedBlockedOnTheNebiusItem()
    {
        if (Asserting())
        {
            Assert.Contains(_lines, static line => line.StartsWith("BLOCKED", StringComparison.Ordinal));
        }
    }

    [Then(@"^the exit code is (\d+)$")]
    public void ThenTheExitCodeIs(int expected)
    {
        CliRunResult run = _run ?? throw new XunitException("The command has not run yet.");

        Assert.True(expected == run.ExitCode, $"Expected exit code {expected} but got {run.ExitCode}; stderr: {run.Stderr}");
        Assert.Empty(run.DeniedRequests);
    }

    [Then(@"^the output names ""([^""]*)"" and the user-secrets alternative$")]
    public void ThenTheOutputNamesTheVariableAndTheUserSecretsAlternative(string variable)
    {
        CliRunResult run = _run ?? throw new XunitException("The command has not run yet.");

        string message = Assert.Single(run.ErrorLines);
        Assert.Contains(variable, message, StringComparison.Ordinal);
        Assert.Contains(MissingKeyMessage.UserSecretsAlternative, message, StringComparison.Ordinal);
        Assert.DoesNotContain(ProviderClientFixtures.FakeKey(), run.Stderr, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"nvapi-[A-Za-z0-9_-]{8,}", RegexOptions.CultureInvariant)]
    private static partial Regex KeyShapedValue();
}