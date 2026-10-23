using System.Globalization;
using System.Text;

using AgentJoules.Core.Results;
using AgentJoules.Specs.Support;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-087: the README results block as a deterministic function (REQ-016, EDGE-002). Scoped to the feature because sentences such as "the block is rendered" are reused by other stories with other meanings. The
/// state lives in <see cref="ReadmeBlockContext"/>; every scenario runs the real aggregator and the real block over run documents built in memory.
/// </summary>
[Binding]
[Scope(Feature = "AJ-087 Render the README results block as a deterministic function")]
public sealed class ReadmeBlockSteps(ReadmeBlockContext context)
{
    private readonly ReadmeBlockContext _context = context;

    [Given(@"^latest-run fixtures for local, nvidia and nebius only$")]
    public void GivenLatestRunFixturesForThreeTiers() => _context.AddLocalNvidiaNebius();

    [Given(@"^the same latest-run fixtures$")]
    public void GivenTheSameLatestRunFixtures() => _context.AddLocalNvidiaNebius();

    [Given(@"^fixtures where run\.selfJudged is true$")]
    public void GivenSelfJudgedFixtures() => _context.AddSelfJudgedNvidia();

    [Given(@"^a nvidia fixture$")]
    public void GivenANvidiaFixture() => _context.AddNvidia();

    [Given(@"^a cloudgpu fixture with status ""partial"" and method ""sampling""$")]
    public void GivenAPartialSamplingCloudGpuFixture() => _context.AddCloudGpuPartialSampling();

    [When(@"^the block is rendered$")]
    public void WhenTheBlockIsRendered() => _context.Render();

    [When(@"^the block is rendered twice$")]
    public void WhenTheBlockIsRenderedTwice() => _context.RenderTwice();

    [Then(@"^the block has rows for ""([^""]*)"", ""([^""]*)"" and ""([^""]*)""$")]
    public void ThenTheBlockHasRowsFor(string first, string second, string third)
    {
        foreach (string tier in new[] { first, second, third })
        {
            Assert.NotEmpty(_context.RowOf(tier));
        }
    }

    [Then(@"^it has the row ""([^""]*)""$")]
    public void ThenItHasTheRow(string row) => Assert.Contains(row, _context.TableLines().Select(line => string.Join(" | ", ReadmeBlockContext.CellsOf(line).Take(2))));

    [Then(@"^the two outputs are byte-identical$")]
    public void ThenTheTwoOutputsAreIdentical()
    {
        Assert.Equal(_context.Text, _context.SecondText);
        Assert.NotNull(_context.Text);
        Assert.Equal(Encoding.UTF8.GetBytes(_context.Text), Encoding.UTF8.GetBytes(_context.SecondText!));
    }

    [Then(@"^the block names the judge model, says judge calls are excluded from workflow totals and discloses self-judging$")]
    public void ThenTheBlockNamesTheJudgeAndDiscloses()
    {
        string text = _context.Text ?? throw new InvalidOperationException("The block was not rendered.");

        Assert.Contains("; judge model-a;", text, StringComparison.Ordinal);
        Assert.Contains(ReportSentences.JudgeExcluded, text, StringComparison.Ordinal);
        Assert.Contains(ReportSentences.SelfJudged, text, StringComparison.Ordinal);
    }

    [Then(@"^its energy cells contain ""N/A"" and not ""0""$")]
    public void ThenItsEnergyCellsAreNa()
    {
        string[] row = _context.RowOf("nvidia");

        foreach (string header in new[] { "net J (run total)", "gross J (run total)" })
        {
            string cell = row[_context.ColumnOf(header)];

            Assert.StartsWith("N/A", cell, StringComparison.Ordinal);
            Assert.False(double.TryParse(cell, NumberStyles.Float, CultureInfo.InvariantCulture, out _));
        }
    }

    [Then(@"^the cloudgpu row contains ""([^""]*)"" and ""([^""]*)""$")]
    public void ThenTheCloudGpuRowContains(string first, string second)
    {
        string row = string.Join(' ', _context.RowOf("cloudgpu"));

        Assert.Contains(first, row, StringComparison.Ordinal);
        Assert.Contains(second, row, StringComparison.Ordinal);
    }
}