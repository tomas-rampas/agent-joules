using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Specs.Support;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-081: the selection of the run files a report may use (REQ-016). Scoped to the feature because sentences such as "the older file is returned" are reused by other stories with other meanings.
/// The state lives in <see cref="RunFileSelectionContext"/>. Every scenario runs the real <see cref="AgentJoules.Reporting.Results.RunFileSelector"/> on a folder of run files in a scratch workspace.
/// </summary>
[Binding]
[Scope(Feature = "AJ-081 Select the run files a report may use")]
public sealed class RunFileSelectionSteps(RunFileSelectionContext context)
{
    private const string Completed = "completed";
    private const string OlderStamp = "20261022T093000Z";
    private const string NewerStamp = "20261023T093000Z";

    private readonly RunFileSelectionContext _context = context;

    [Given(@"^nvidia run files from 2026-10-22 and 2026-10-23 both completed$")]
    public void GivenNvidiaRunFilesFromTwoDays()
    {
        string older = _context.WriteRunFile(OlderStamp, Tier.Nvidia, Completed, []);
        string newest = _context.WriteRunFile(NewerStamp, Tier.Nvidia, Completed, []);
        _context.Remember(newest, older);
    }

    [Given(@"^a nebius file with status ""partial"" and a nebius baseline file$")]
    public void GivenAPartialNebiusFileAndABaselineFile()
    {
        // The baseline file is the newer name, so only the rule (never the order) keeps it out of Latest.
        string partial = _context.WriteRunFile(OlderStamp, Tier.Nebius, "partial", []);
        string baseline = _context.WriteRunFile(NewerStamp, Tier.Nebius, Completed, [], baseline: true);
        _context.Remember(partial, baseline: baseline);
    }

    [Given(@"^the newest nvidia file has status ""failed"" and an older one is completed$")]
    public void GivenTheNewestNvidiaFileFailed()
    {
        string older = _context.WriteRunFile(OlderStamp, Tier.Nvidia, Completed, []);
        string newest = _context.WriteRunFile(NewerStamp, Tier.Nvidia, "failed", []);
        _context.Remember(newest, older);
    }

    [Given(@"^a folder holding only a probe file and a calibration file$")]
    public void GivenAFolderHoldingOnlyAProbeAndACalibrationFile()
    {
        _context.WriteNonRunFile("probe-20261019T205344Z.json");
        _context.WriteNonRunFile("calibration-20261019T205344Z.json");
        _context.Remember(newest: null);
    }

    [Given(@"^the newest local file carries run flag ""non-publishable"" and an older local file has no flags$")]
    public void GivenTheNewestLocalFileIsNonPublishable()
    {
        string older = _context.WriteRunFile(OlderStamp, Tier.Local, Completed, []);
        string newest = _context.WriteRunFile(NewerStamp, Tier.Local, Completed, [RunFlagNames.NonPublishable]);
        _context.Remember(newest, older);
    }

    [Given(@"^the newest local file is completed and has an empty run\.flags$")]
    public void GivenTheNewestLocalFileHasEmptyFlags()
    {
        string older = _context.WriteRunFile(OlderStamp, Tier.Local, Completed, []);
        string newest = _context.WriteRunFile(NewerStamp, Tier.Local, Completed, []);
        _context.Remember(newest, older);
    }

    [When(@"^the latest (local|nvidia) file is selected$")]
    public void WhenTheLatestFileIsSelected(string tierName) =>
        _context.SelectLatest(tierName == "local" ? Tier.Local : Tier.Nvidia);

    [When(@"^latest and latest baseline are selected for nebius$")]
    public void WhenLatestAndLatestBaselineAreSelectedForNebius() => _context.SelectLatestAndBaseline(Tier.Nebius);

    [Then(@"^the 2026-10-23 file is returned$")]
    public void ThenTheNewerFileIsReturned()
    {
        Assert.StartsWith("20261023T", _context.LatestName(), StringComparison.Ordinal);
        Assert.Equal(_context.NewestName, _context.LatestName());
    }

    [Then(@"^the partial file is the latest and the baseline file is the latest baseline$")]
    public void ThenThePartialFileIsTheLatestAndTheBaselineFileIsTheLatestBaseline()
    {
        Assert.Equal(_context.NewestName, _context.LatestName());
        Assert.Equal(_context.BaselineName, _context.LatestBaselineName());
        Assert.NotEqual(_context.LatestName(), _context.LatestBaselineName());
    }

    [Then(@"^the older completed file is returned$")]
    public void ThenTheOlderCompletedFileIsReturned()
    {
        Assert.Equal(_context.OlderName, _context.LatestName());
        Assert.Contains(_context.Selection!.Skipped, skip => skip.FileName == _context.NewestName && skip.Reason == "status-failed");
    }

    [Then(@"^no file is returned and the tier is reported as not run$")]
    public void ThenNoFileIsReturnedAndTheTierIsNotRun()
    {
        // The report draws "not run" from the absence of the tier in Latest; nothing is skipped or a problem because the two files are filtered by name and never opened.
        Assert.Null(_context.Latest);
        Assert.DoesNotContain(Tier.Local, _context.Selection!.Latest.Keys);
        Assert.Empty(_context.Selection.Latest);
        Assert.Empty(_context.Selection.Skipped);
        Assert.Empty(_context.Selection.Problems);
    }

    [Then(@"^the older file is returned$")]
    public void ThenTheOlderFileIsReturned()
    {
        Assert.Equal(_context.OlderName, _context.LatestName());
        Assert.Contains(_context.Selection!.Skipped, skip => skip.FileName == _context.NewestName && skip.Reason == "non-publishable");
    }

    [Then(@"^the newest file is returned$")]
    public void ThenTheNewestFileIsReturned()
    {
        Assert.Equal(_context.NewestName, _context.LatestName());
        Assert.Empty(_context.Selection!.Skipped);
    }
}