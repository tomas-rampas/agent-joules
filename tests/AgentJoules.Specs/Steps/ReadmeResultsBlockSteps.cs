using System.Globalization;
using System.Text.RegularExpressions;

using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Reporting.Readme;
using AgentJoules.Specs.Support;
using AgentJoules.TestSupport;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-133: generate and verify the README results block (REQ-025, EDGE-002). Scoped to the feature because sentences such as "the exit code is 7" are reused by other stories with other meanings. The state lives in
/// <see cref="ReadmeResultsBlockContext"/>. Every scenario runs the real command line through the harness in a temporary workspace over fixture run files; the table of the block is read cell by cell, so the order of
/// the rows, the status, the flags and the N/A of a hosted row are checked on the cells and not on a substring. The committed README is only read.
/// </summary>
[Binding]
[Scope(Feature = "AJ-133 README results block")]
public sealed partial class ReadmeResultsBlockSteps(ReadmeResultsBlockContext context)
{
    private const string UpdateArguments = "report --update-readme";
    private const string VerifyArguments = "report --verify-readme";
    private const string Joiner = "&#8288;";
    private const string DateFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    private readonly ReadmeResultsBlockContext _context = context;

    [GeneratedRegex(@"(?<!\\)\|", RegexOptions.CultureInvariant)]
    private static partial Regex UnescapedPipe();

    private static string[] CellsOf(string line) => [.. UnescapedPipe().Split(line).Skip(1).SkipLast(1).Select(static cell => cell.Trim())];

    private static string[][] RowsOf(string block) => [.. block.Split('\n').Where(static line => line.StartsWith('|')).Skip(2).Select(CellsOf)];

    private static string[] HeaderOf(string block) => CellsOf(block.Split('\n').First(static line => line.StartsWith('|')));

    private static string[] RowOf(string block, string tier) => Assert.Single(RowsOf(block), cells => cells[0] == tier);

    private static string Plain(string text) => text.Replace(Joiner, string.Empty, StringComparison.Ordinal);

    // The block writes a word joiner before the dots and the colons of a text from a file and backslash-escapes the markdown punctuation: the text as it reads on screen has neither.
    private static string Unescaped(string text) => Plain(text).Replace("\\", string.Empty, StringComparison.Ordinal);

    [Given(@"^a workspace with latest results files for local, nvidia and nebius and a README with markers$")]
    [Given(@"^a workspace without a cloudgpu run file$")]
    public void GivenThreeTiersAndAReadme()
    {
        _context.AddThreeTiers();
        _context.WriteReadme();
    }

    [Given(@"^a workspace with all four tiers and a README with markers$")]
    public void GivenFourTiersAndAReadme()
    {
        _context.AddThreeTiers();
        _context.AddCloudGpu(partial: false);
        _context.WriteReadme();
    }

    [Given(@"^a workspace whose local run used the sampling method$")]
    public void GivenALocalSamplingRun()
    {
        _context.AddTiers(Tier.Nvidia, Tier.Nebius);
        _context.WriteResult(RunDocumentFixtures.CommittedFileName(Tier.Local), ReadmeFixtures.LocalSamplingText());
        _context.WriteReadme();
    }

    [Given(@"^an updated README whose results block has one number edited by hand$")]
    public async Task GivenAnUpdatedReadmeEditedByHand()
    {
        _context.AddThreeTiers();
        _context.WriteReadme();
        Assert.Equal(0, (await _context.RunAsync(UpdateArguments)).ExitCode);
        _context.EditBlockByHand();
    }

    [Given(@"^a README without the line ""([^""]*)""$")]
    public void GivenAReadmeWithoutTheLine(string line)
    {
        _context.AddThreeTiers();
        _context.WriteReadme(ReportFlagsContext.ReadmeTemplate.Replace(line + "\n", string.Empty, StringComparison.Ordinal));
        Assert.DoesNotContain(line, _context.ReadmeText(), StringComparison.Ordinal);
    }

    [Given(@"^a latest cloudgpu file with run\.status ""partial"" and stopReason ""([^""]*)""$")]
    public void GivenAPartialCloudGpuFile(string stopReason)
    {
        Assert.Equal(ReadmeFixtures.CostCapStopReason, stopReason);
        _context.AddThreeTiers();
        _context.AddCloudGpu(partial: true);
        _context.WriteReadme();
    }

    [Given(@"^a fixture where a hosted step has energy value 0 and label ""measured""$")]
    public void GivenAHostedStepWithZeroEnergy()
    {
        _context.WriteResult(RunDocumentFixtures.CommittedFileName(Tier.Local), RunDocumentFixtures.CommittedText(Tier.Local));
        _context.WriteResult(RunDocumentFixtures.CommittedFileName(Tier.Nvidia), ReadmeFixtures.HostedZeroEnergyText());
        _context.WriteResult(RunDocumentFixtures.CommittedFileName(Tier.Nebius), RunDocumentFixtures.CommittedText(Tier.Nebius));
        _context.WriteReadme();
        _context.SnapshotReadme();
    }

    [When(@"^""report --update-readme"" runs twice and then ""report --verify-readme"" runs$")]
    public async Task WhenUpdateTwiceThenVerify()
    {
        _ = await _context.RunAsync(UpdateArguments);
        _context.SnapshotReadme();
        _ = await _context.RunAsync(UpdateArguments);
        _ = await _context.RunAsync(VerifyArguments);
    }

    [When(@"^""(report [^""]*)"" runs$")]
    public async Task WhenReportRuns(string arguments) => _ = await _context.RunAsync(arguments);

    // The validation of the run files, then the same files through the README update: the first must refuse the file, the second must write no block.
    [When(@"^the results are validated$")]
    public async Task WhenTheResultsAreValidated()
    {
        _ = await _context.RunAsync("validate");
        _ = await _context.RunAsync(UpdateArguments);
    }

    [Then(@"^the second update changes nothing and verify exits 0$")]
    public void ThenTheSecondUpdateChangesNothing()
    {
        Assert.Equal([0, 0, 0], _context.Runs.Select(static run => run.ExitCode));
        Assert.Equal(_context.Snapshot, _context.ReadmeBytes());
        Assert.DoesNotContain((byte)'\r', _context.ReadmeBytes());
    }

    [Then(@"^the block has rows for local, nvidia and nebius and the row ""([^""]*)""$")]
    public void ThenTheBlockHasTheRows(string notRunRow)
    {
        Assert.Equal(0, _context.Last().ExitCode);
        string block = _context.Block();
        string[][] rows = RowsOf(block);

        Assert.Equal(["local", "cloudgpu", "nvidia", "nebius"], rows.Select(static cells => cells[0]));
        Assert.Contains(notRunRow, block, StringComparison.Ordinal);
        Assert.Equal("not run", RowOf(block, "cloudgpu")[1]);
        Assert.All(["local", "nvidia", "nebius"], tier => Assert.NotEqual("not run", RowOf(block, tier)[1]));
    }

    [Then(@"^hosted rows show ""N/A"" for energy and never ""0""$")]
    public void ThenHostedRowsShowNa()
    {
        string block = _context.Block();
        string[] header = HeaderOf(block);
        foreach (string tier in new[] { "nvidia", "nebius" })
        {
            string[] row = RowOf(block, tier);
            foreach (string column in new[] { "net J (run total)", "gross J (run total)" })
            {
                string cell = row[Array.IndexOf(header, column)];

                Assert.StartsWith("N/A", cell, StringComparison.Ordinal);
                Assert.NotEqual("0", cell);
            }
        }
    }

    [Then(@"^the exit code is (\d+)$")]
    public void ThenTheExitCodeIs(int code) => Assert.Equal(code, _context.Last().ExitCode);

    [Then(@"^the exit code is 2 and the message names the missing marker$")]
    public void ThenExit2NamesTheMarker()
    {
        Assert.Equal(2, _context.Last().ExitCode);
        Assert.Contains(ReadmeResultsBlock.BeginMarker, _context.Last().Stderr, StringComparison.Ordinal);
    }

    [Then(@"^the cloudgpu row contains ""partial""$")]
    public void ThenTheCloudGpuRowIsPartial()
    {
        Assert.Equal(0, _context.Last().ExitCode);
        string block = _context.Block();
        string[] header = HeaderOf(block);
        string[] row = RowOf(block, "cloudgpu");

        Assert.Equal("partial", row[Array.IndexOf(header, "status")]);
        Assert.Contains("partial", row[Array.IndexOf(header, "flags")], StringComparison.Ordinal);
    }

    [Then(@"^validation fails and no README block is written$")]
    public void ThenValidationFailsAndNothingIsWritten()
    {
        Assert.Equal(2, _context.Runs[0].ExitCode);
        Assert.Equal(2, _context.Runs[1].ExitCode);
        Assert.Equal(_context.Snapshot, _context.ReadmeBytes());
        Assert.Contains("stale block", _context.ReadmeText(), StringComparison.Ordinal);
    }

    [Then(@"^the rows are in the order local, cloudgpu, nvidia, nebius$")]
    public void ThenTheRowsAreInOrder()
    {
        Assert.Equal(0, _context.Last().ExitCode);
        Assert.Equal(["local", "cloudgpu", "nvidia", "nebius"], RowsOf(_context.Block()).Select(static cells => cells[0]));
    }

    [Then(@"^the block names the GPU, the driver version, the run date in UTC, the local runtime and the model IDs with precision$")]
    public void ThenTheBlockNamesTheHeaderFacts()
    {
        RunDocument local = RunDocumentFixtures.Committed(Tier.Local);
        EnvironmentInfo environment = local.Environment;
        string bullet = Unescaped(Assert.Single(_context.Block().Split('\n'), static line => line.StartsWith("- local:", StringComparison.Ordinal)));

        Assert.Contains("; GPU " + environment.GpuName + ";", bullet, StringComparison.Ordinal);
        Assert.Contains("; driver " + environment.DriverVersion + ";", bullet, StringComparison.Ordinal);
        Assert.Contains("; run date " + local.Run.StartUtc.UtcDateTime.ToString(DateFormat, CultureInfo.InvariantCulture) + ";", bullet, StringComparison.Ordinal);
        Assert.Contains("; local runtime " + environment.LocalRuntime + " " + environment.RuntimeVersion + ";", bullet, StringComparison.Ordinal);
        Assert.Contains("; precision " + environment.Precision + ";", bullet, StringComparison.Ordinal);
        Assert.All(environment.Models.Select(static model => model.ModelId).Distinct(StringComparer.Ordinal), id => Assert.Contains(id, bullet, StringComparison.Ordinal));
    }

    [Then(@"^the block contains ""([^""]*)""$")]
    public void ThenTheBlockContains(string sentence) => Assert.Contains(sentence, _context.Block().Split('\n'));

    [Then(@"^the local row contains ""([^""]*)""$")]
    public void ThenTheLocalRowContains(string word)
    {
        Assert.Equal(0, _context.Last().ExitCode);
        string block = _context.Block();

        Assert.Contains(word, RowOf(block, "local")[Array.IndexOf(HeaderOf(block), "flags")], StringComparison.Ordinal);
    }

    [Then(@"^the README does not contain ""([^""]*)""$")]
    public void ThenTheReadmeDoesNotContain(string text)
    {
        Assert.DoesNotContain(text, _context.ReadmeText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(text, RepositoryRoot.ReadAllText("README.md"), StringComparison.OrdinalIgnoreCase);
    }

    [Then(@"^docs/report/index\.html contains no ""([^""]*)"" and docs/\.nojekyll exists$")]
    public void ThenThePageHasNoScript(string text)
    {
        Assert.Equal(0, _context.Last().ExitCode);
        Assert.DoesNotContain(text, _context.ReadFile("docs/report/index.html"), StringComparison.Ordinal);
        Assert.True(_context.Exists("docs/.nojekyll"), "docs/.nojekyll was not written.");
    }
}