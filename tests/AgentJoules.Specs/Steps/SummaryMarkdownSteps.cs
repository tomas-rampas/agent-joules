using AgentJoules.Specs.Support;
using AgentJoules.TestSupport;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-083: the Markdown summary with honest labels (REQ-016, EDGE-002, EDGE-003, EDGE-004, EDGE-007). Scoped to the feature because sentences such as "the summary is rendered" are reused by other stories
/// with other meanings. The state lives in <see cref="SummaryMarkdownContext"/>; every scenario runs the real aggregator and the real renderer over run documents built in memory.
/// </summary>
[Binding]
[Scope(Feature = "AJ-083 Render the Markdown summary with honest labels")]
public sealed class SummaryMarkdownSteps(SummaryMarkdownContext context)
{
    private readonly SummaryMarkdownContext _context = context;

    [Given(@"^a fixture with a local run and a nvidia run$")]
    public void GivenALocalRunAndANvidiaRun()
    {
        _context.AddLocalRun();
        _context.AddNvidiaRun();
    }

    [Given(@"^a fixture with only a nvidia run$")]
    public void GivenOnlyANvidiaRun() => _context.AddNvidiaRun();

    [Given(@"^a fixture whose run\.mode is ""concurrent""$")]
    public void GivenAConcurrentRun() => _context.AddConcurrentRun();

    [Given(@"^a fixture row whose response had no usage$")]
    public void GivenARowWithoutUsage() => _context.AddNoUsageRun();

    [Given(@"^a fixture hosted row$")]
    public void GivenAHostedRow() => _context.AddNvidiaRun();

    [Given(@"^runs whose tiers use different model IDs$")]
    public void GivenRunsWithDifferentModels() => _context.AddRunsWithDifferentModels();

    [Given(@"^a fixture row with flag ""low-resolution""$")]
    public void GivenARowWithTheLowResolutionFlag() => _context.AddLowResolutionRun();

    [When(@"^the summary is rendered$")]
    public void WhenTheSummaryIsRendered() => _context.Render();

    [Then(@"^the header lists GPU name, driver, run date, runtime, model IDs and precision$")]
    public void ThenTheHeaderListsTheContext()
    {
        string bullet = _context.Bullet("local");

        Assert.Contains("; GPU Test GPU;", bullet, StringComparison.Ordinal);
        Assert.Contains("; driver 555.00;", bullet, StringComparison.Ordinal);
        Assert.Contains("; run date 2026-10-23T09:30:00Z;", bullet, StringComparison.Ordinal);
        Assert.Contains("; local runtime ollama 0.1.0;", bullet, StringComparison.Ordinal);
        Assert.Contains("; models Planner=model-a, Writer=model-b;", bullet, StringComparison.Ordinal);
        Assert.Contains("; precision Q4", bullet, StringComparison.Ordinal);
    }

    [Then(@"^the text contains ""([^""]*)""$")]
    public void ThenTheTextContains(string expected) => Assert.Contains(expected, Text(), StringComparison.Ordinal);

    [Then(@"^the table has the rows ""([^""]*)"", ""([^""]*)"" and ""([^""]*)""$")]
    public void ThenTheTableHasTheRows(string first, string second, string third)
    {
        string[] table = _context.TableLines();

        foreach (string row in new[] { first, second, third })
        {
            Assert.Contains(table, line => line.StartsWith($"| {row} |", StringComparison.Ordinal));
        }
    }

    [Then(@"^both token cells contain ""([^""]*)"" and the cost cell contains ""([^""]*)""$")]
    public void ThenTheTokenAndCostCellsContain(string tokens, string cost)
    {
        string[] row = _context.ValueRow();

        Assert.Contains(tokens, row[_context.ColumnOf("input tokens")], StringComparison.Ordinal);
        Assert.Contains(tokens, row[_context.ColumnOf("output tokens")], StringComparison.Ordinal);
        Assert.Contains(cost, row[_context.ColumnOf("cost")], StringComparison.Ordinal);
    }

    [Then(@"^the gross J, net J, avg W and peak W cells contain ""([^""]*)""$")]
    public void ThenTheEnergyCellsContain(string expected)
    {
        string[] row = _context.ValueRow();

        foreach (string column in EnergyColumns)
        {
            Assert.Contains(expected, row[_context.ColumnOf(column)], StringComparison.Ordinal);
        }
    }

    [Then(@"^no energy cell contains ""([^""]*)""$")]
    public void ThenNoEnergyCellContains(string forbidden)
    {
        string[] row = _context.ValueRow();

        foreach (string column in EnergyColumns)
        {
            Assert.DoesNotContain(forbidden, row[_context.ColumnOf(column)], StringComparison.Ordinal);
        }
    }

    [Then(@"^the header contains ""([^""]*)""$")]
    public void ThenTheHeaderContains(string expected)
    {
        string[] lines = _context.Lines();
        int table = Array.FindIndex(lines, line => line.StartsWith('|'));

        Assert.Contains(lines.Take(table), line => line.Contains(expected, StringComparison.Ordinal));
    }

    [Then(@"^the text has no cross-tier ratio$")]
    public void ThenTheTextHasNoCrossTierRatio()
    {
        string text = Text();

        foreach (string word in ReportRunFixtures.CrossTierWords)
        {
            Assert.DoesNotContain(word, text, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotMatch(@"\d\s*x\b|x-fold|%", text);
    }

    [Then(@"^the row contains ""([^""]*)"" next to the value$")]
    public void ThenTheRowContainsTheFlagNextToTheValue(string flag)
    {
        string[] row = _context.ValueRow();

        Assert.Equal(flag, row[_context.ColumnOf("flags")]);
        Assert.StartsWith("60.0", row[_context.ColumnOf("net J")], StringComparison.Ordinal);
        string line = Assert.Single(_context.TableLines(), candidate => candidate.Contains(flag, StringComparison.Ordinal));
        Assert.True(line.IndexOf("60.0", StringComparison.Ordinal) < line.IndexOf(flag, StringComparison.Ordinal));
    }

    private static readonly string[] EnergyColumns = ["gross J", "net J", "avg W", "peak W"];

    private string Text() => _context.Text ?? throw new InvalidOperationException("The summary was not rendered.");
}