using AgentJoules.Core.Configuration;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Runs;
using AgentJoules.Reporting.Render;
using AgentJoules.Specs.Support;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-082: the aggregation of repeats into report rows without merging tiers (REQ-016). Scoped to the feature because sentences such as "the rows are aggregated" are reused by other stories with
/// other meanings. The state lives in <see cref="AggregationContext"/>; every scenario runs the real <see cref="ReportAggregator"/> over run documents built in memory.
/// </summary>
[Binding]
[Scope(Feature = "AJ-082 Aggregate repeats into report rows without merging tiers")]
public sealed class AggregationSteps(AggregationContext context)
{
    private readonly AggregationContext _context = context;

    [Given(@"^(\d+) repeats of net joules (\d+), (\d+) and (\d+) for one agent$")]
    public void GivenRepeatsOfNetJoules(int count, double first, double second, double third)
    {
        double[] values = [first, second, third];
        Assert.Equal(count, values.Length);
        _context.AddNetJoulesRepeats(values);
    }

    [Given(@"^(\d+) repeats of which one has flag ""([^""]*)""$")]
    public void GivenRepeatsOfWhichOneHasFlag(int count, string flag) =>
        _context.AddRepeatsWithFlagOnFirst(count, ReadingFlagNames.Parse(flag));

    [Given(@"^(\d+) hosted repeats whose net joules are N/A$")]
    public void GivenHostedRepeatsWithNaNetJoules(int count) => _context.AddHostedRepeats(count);

    [Given(@"^runs for tiers ""([^""]*)"" and ""([^""]*)"" with the same model ID$")]
    public void GivenRunsForTiersWithTheSameModelId(string first, string second)
    {
        _context.AddSingleStepRun(TierOf(first), "shared-model");
        _context.AddSingleStepRun(TierOf(second), "shared-model");
    }

    [Given(@"^runs for tiers ""([^""]*)"" and ""([^""]*)""$")]
    public void GivenRunsForTiers(string first, string second)
    {
        _context.AddSingleStepRun(TierOf(first), "model-a");
        _context.AddSingleStepRun(TierOf(second), "model-b");
    }

    [When(@"^the rows are aggregated$")]
    public void WhenTheRowsAreAggregated() => _context.Aggregate();

    [Then(@"^the cell shows median (\d+), range (\d+) to (\d+) and n (\d+)$")]
    public void ThenTheCellShowsMedianRangeAndN(double median, double min, double max, int n)
    {
        ReportRow row = Assert.Single(Model().Rows);
        ReportCell cell = row.NetJoules;

        Assert.Equal(median, cell.Median);
        Assert.Equal(min, cell.Min);
        Assert.Equal(max, cell.Max);
        Assert.Equal(n, cell.N);
        Assert.Equal(n, cell.Total);
        Assert.Equal(MetricLabel.Measured, cell.Label);
    }

    [Then(@"^the aggregate cell carries ""([^""]*)""$")]
    public void ThenTheAggregateCellCarries(string flag)
    {
        ReportRow row = Assert.Single(Model().Rows);

        Assert.Contains(flag, ReadingFlagNames.ToStrings(row.Flags));
    }

    [Then(@"^the cell has label ""N/A"" and a null value$")]
    public void ThenTheCellHasLabelNaAndANullValue()
    {
        ReportRow row = Assert.Single(Model().Rows);
        ReportCell cell = row.NetJoules;

        Assert.Equal(MetricLabel.NotApplicable, cell.Label);
        Assert.Null(cell.Median);
        Assert.Null(cell.Min);
        Assert.Null(cell.Max);
        Assert.Equal(0, cell.N);
    }

    [Then(@"^the result has separate rows for ""([^""]*)"" and ""([^""]*)"" and no combined row$")]
    public void ThenTheResultHasSeparateRows(string first, string second)
    {
        IReadOnlyList<ReportRow> rows = Model().Rows;

        Assert.Equal(2, rows.Count);
        Assert.Equal([TierOf(first), TierOf(second)], rows.Select(row => row.Tier));
        Assert.All(rows, row => Assert.Equal(1, row.Repeats));
    }

    [Then(@"^the report model contains no cross-tier ratio or ranking field$")]
    public void ThenTheReportModelContainsNoCrossTierField()
    {
        Assert.Equal(2, Model().Rows.Count);
        Assert.Empty(AggregationContext.CrossTierMemberNames());
    }

    private static Tier TierOf(string name) =>
        Enum.GetValues<Tier>().Single(tier => string.Equals(TierOptions.NameOf(tier), name, StringComparison.Ordinal));

    private ReportModel Model() => _context.Model ?? throw new InvalidOperationException("The rows were not aggregated.");
}