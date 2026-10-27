using AgentJoules.Specs.Support;
using AgentJoules.TestSupport;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>AJ-151: the cloudgpu run file, probe and run record, the README block and the key scan.</summary>
public sealed partial class EvidenceSteps
{
    // The run record of a fixture: every heading, the cost and teardown text of the scenario.
    private static string RecordText(string cost, string? teardown) =>
        "# Run record\n\n## Instance\n\nVM.\n\n## Commands\n\nNone.\n\n## Probe\n\nMIG disabled.\n\n## Runs\n\nOne run.\n\n## Cost\n\n- Hourly price: USD 4.63 per hour.\n- Whole-run cost: about "
        + cost + " (estimated).\n"
        + (teardown is null ? string.Empty : "\n## Teardown\n\n" + teardown + "\n");

    private void UseCloudGpuRecord(string text)
    {
        TempWorkspace workspace = _context.NewFixture();
        _ = workspace.WriteFile("docs/cloudgpu-run.md", text);
        _context.Check = () => EvidenceChecks.CloudGpuRecordViolations(_context.Root);
    }

    [Given(@"^a results folder with a completed cloudgpu file whose provider is ""([^""]+)"" and precision ""([^""]+)""$")]
    public void GivenACompletedCloudGpuFile(string provider, string precision)
    {
        TempWorkspace workspace = _context.NewFixture();
        EvidenceContext.WriteEditedRunFile(workspace, "cloudgpu", root =>
        {
            root["run"]!["status"] = "completed";
            root["environment"]!["provider"] = provider;
            root["environment"]!["precision"] = precision;
            root["environment"]!["instancePreset"] = "gpu-h100-sxm 1gpu-16vcpu-200gb";
        });
        _context.Check = () =>
        {
            CloudGpuRunEvidence evidence = EvidenceChecks.CloudGpuRunCheck(_context.Root);
            _context.Preset = evidence.Preset;
            return evidence.Violations;
        };
    }

    [Then(@"^it passes and records the preset$")]
    public void ThenItPassesAndRecordsThePreset()
    {
        AssertNoViolations();
        Assert.False(string.IsNullOrWhiteSpace(_context.Preset));
        _output.WriteLine("preset: " + _context.Preset);
    }

    [Given(@"^the committed docs/cloudgpu-run\.md$")]
    public void GivenTheCommittedRunRecord()
    {
        _context.UseRepository();
        _context.Check = () => EvidenceChecks.CloudGpuRecordViolations(_context.Root);
    }

    [When(@"^its sections are parsed$")]
    public void WhenTheSectionsAreParsed() => _context.Result = EvidenceChecks.CloudGpuRecordViolations(_context.Root);

    [Then(@"^it has Instance, Commands, Probe, Runs, Cost and Teardown$")]
    public void ThenItHasTheSixHeadings() =>
        Assert.DoesNotContain(_context.Violations, static violation => violation.StartsWith("missing heading", StringComparison.Ordinal));

    [Then(@"^the Cost section holds a USD value of 30 or less and the Teardown section holds ""deleted""$")]
    public void ThenTheCostAndTeardownAreRecorded() => AssertNoViolations();

    [Given(@"^the committed README\.md$")]
    public void GivenTheCommittedReadme()
    {
        _context.UseRepository();
        _context.Text = RepositoryRoot.ReadAllText("README.md");
    }

    [When(@"^the results block is read$")]
    public void WhenTheResultsBlockIsRead() => _context.Result = EvidenceChecks.ReadmeResultsViolations(_context.Text);

    [Then(@"^it contains ""cloudgpu""$")]
    public void ThenTheBlockNamesCloudGpu() => AssertNoViolations();

    [Given(@"^a cloudgpu-run\.md whose Cost section states ""([^""]+)""$")]
    public void GivenARecordWithACost(string cost) => UseCloudGpuRecord(RecordText(cost, "Deleted: yes."));

    [Given(@"^a cloudgpu-run\.md whose Teardown section lacks the word ""deleted""$")]
    public void GivenARecordWithoutDeletion() => UseCloudGpuRecord(RecordText("USD 7", "The VM is still running."));

    [Given(@"^a cloudgpu-run\.md without the heading ""([^""]+)""$")]
    public void GivenARecordWithoutAHeading(string heading)
    {
        Assert.Equal("Teardown", heading);
        UseCloudGpuRecord(RecordText("USD 7", teardown: null));
    }

    [Given(@"^a results folder whose only cloudgpu file has run\.status ""([^""]+)""$")]
    public void GivenAnIncompleteCloudGpuFile(string status)
    {
        TempWorkspace workspace = _context.NewFixture();
        EvidenceContext.WriteEditedRunFile(workspace, "cloudgpu", root => root["run"]!["status"] = status);
        _context.Check = () => EvidenceChecks.CloudGpuRunCheck(_context.Root).Violations;
    }

    [Given(@"^a cloudgpu probe fixture with migMode ""([^""]+)""$")]
    public void GivenAProbeWithMig(string mode)
    {
        TempWorkspace workspace = _context.NewFixture();
        string name = EvidenceChecks.NewestProbeFileName(RepositoryRoot.Path, cloudGpu: true) ?? throw new Xunit.Sdk.XunitException("results/ holds no cloudgpu probe file");
        _ = workspace.WriteFile("results/" + name, RunDocumentFixtures.Edit(RepositoryRoot.ReadAllText("results/" + name), root => root["migMode"] = mode) + "\n");
        _context.Check = () => EvidenceChecks.CloudGpuProbeViolations(_context.Root);
    }

    // AJ-151 @integration

    [Given(@"^NGC_API_KEY is set$")]
    public void GivenTheKeyIsSet() => _context.OpenGate("NGC_API_KEY");

    [When(@"^""grep -F"" for its value runs over docs/cloudgpu-run\.md$")]
    public void WhenTheKeyIsSearched()
    {
        if (!Skipped())
        {
            _context.Result = EvidenceChecks.KeyMaterialViolations(RepositoryRoot.ReadAllText("docs/cloudgpu-run.md"), _context.KeyValue);
        }
    }

    [Then(@"^the exit code is 1 and no line is printed$")]
    public void ThenNoHitIsFound()
    {
        if (!Skipped())
        {
            Assert.True(_context.Violations.Count == 0, "the scan of docs/cloudgpu-run.md reported a violation (the violations are not echoed)");
        }
    }

    [Given(@"^a copy of the run record containing the value of NGC_API_KEY$")]
    public void GivenARecordWithTheKey()
    {
        _context.OpenGate("NGC_API_KEY");
        if (_context.SkipNote is null)
        {
            _context.Text = RepositoryRoot.ReadAllText("docs/cloudgpu-run.md") + "\n" + _context.KeyValue + "\n";
        }
    }

    [When(@"^the scan runs$")]
    public void WhenTheScanRuns()
    {
        if (!Skipped())
        {
            _context.Result = EvidenceChecks.KeyMaterialViolations(_context.Text, _context.KeyValue);
        }
    }

    [Then(@"^it fails with ""key material in docs/cloudgpu-run\.md"" without printing the value$")]
    public void ThenTheScanFailsWithoutPrintingTheValue()
    {
        if (Skipped())
        {
            return;
        }

        Assert.False(_context.Violations.Any(violation => violation.Contains(_context.KeyValue!, StringComparison.Ordinal)), "a violation carries the value of NGC_API_KEY (the value is not echoed)");
        Assert.True(_context.Violations.Contains(EvidenceChecks.KeyInRecord), "the scan did not report the fixed key-material message (the violations are not echoed)");
    }
}