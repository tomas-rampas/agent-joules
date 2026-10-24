using System.Globalization;
using System.Xml.Linq;

using AgentJoules.Core.Labels;
using AgentJoules.Core.Runs;
using AgentJoules.Specs.Support;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-084: the SVG chart with N/A as a text marker (REQ-016). Scoped to the feature because sentences such as "the chart is rendered" are reused by other stories with other meanings. The state lives in
/// <see cref="SvgChartContext"/>; every scenario runs the real aggregator and the real renderer over run documents built in memory and parses the result with <c>XDocument.Parse</c>.
/// </summary>
[Binding]
[Scope(Feature = "AJ-084 Draw the SVG chart with N/A as a text marker")]
public sealed class SvgChartSteps(SvgChartContext context)
{
    private static readonly string[] FixedTierOrder = ["local", "cloudgpu", "nvidia", "nebius"];

    private readonly SvgChartContext _context = context;

    [Given(@"^a fixture local step with median net joules (-?\d+(?:\.\d+)?)$")]
    public void GivenALocalStepWithMedianNetJoules(string net) => _context.AddLocalStep(double.Parse(net, CultureInfo.InvariantCulture));

    [Given(@"^a fixture with all four tiers$")]
    public void GivenAllFourTiers() => _context.AddTiers(Tier.Local, Tier.CloudGpu, Tier.Nvidia, Tier.Nebius);

    [Given(@"^a fixture local step with net joules (-?\d+(?:\.\d+)?) flagged ""([^""]*)""$")]
    public void GivenALocalStepWithNetJoulesAndFlag(string net, string flag)
    {
        Assert.Equal("below-baseline", flag);
        _context.AddLocalStep(double.Parse(net, CultureInfo.InvariantCulture), ReadingFlags.BelowBaseline);
    }

    [Given(@"^a fixture hosted step whose net joules are N/A$")]
    public void GivenAHostedStepWhoseNetJoulesAreNa() => _context.AddHostedStep();

    [Given(@"^a fixture with no cloudgpu run$")]
    public void GivenNoCloudGpuRun() => _context.AddTiers(Tier.Local, Tier.Nvidia, Tier.Nebius);

    [Given(@"^a model ID containing ""([^""]*)"" and ""([^""]*)""$")]
    public void GivenAModelIdContaining(string first, string second) => _context.AddLocalStepNamed($"a{first}b{second}c");

    [When(@"^the chart is rendered$")]
    public void WhenTheChartIsRendered() => _context.Render();

    [Then(@"^the SVG has one bar for that step and the text ""([^""]*)""$")]
    public void ThenTheSvgHasOneBarAndTheText(string expected)
    {
        XElement bar = Assert.Single(_context.Elements("rect", "bar"));
        XElement slot = _context.SlotOf("local");

        Assert.Same(slot, bar.Parent);
        Assert.Equal([expected], SvgChartContext.TextsOf(slot, "value"));
    }

    [Then(@"^the output parses as XML$")]
    public void ThenTheOutputParsesAsXml() => Assert.NotNull(_context.Parse().Root);

    [Then(@"^the output still parses as XML$")]
    public void ThenTheOutputStillParsesAsXml() => Assert.NotNull(_context.Parse().Root);

    [Then(@"^the tier slots appear in the order local, cloudgpu, nvidia, nebius$")]
    public void ThenTheTierSlotsAppearInTheFixedOrder() =>
        Assert.Equal(FixedTierOrder, _context.Slots().Select(slot => (string?)slot.Attribute("data-tier")));

    [Then(@"^the bar extends below the axis line$")]
    public void ThenTheBarExtendsBelowTheAxisLine()
    {
        XElement axis = Assert.Single(_context.Elements("line", "axis"));
        XElement bar = Assert.Single(_context.Elements("rect", "bar"));
        double axisY = SvgChartContext.Number(axis, "y1");

        Assert.Equal(axisY, SvgChartContext.Number(axis, "y2"));
        Assert.Equal(axisY, SvgChartContext.Number(bar, "y"), precision: 2);
        Assert.True(SvgChartContext.Number(bar, "height") > 0.0);
    }

    [Then(@"^the SVG has a text element ""([^""]*)"" for that step$")]
    public void ThenTheSvgHasATextElementForThatStep(string expected) =>
        Assert.Equal([expected], SvgChartContext.TextsOf(_context.SlotOf("nvidia"), "marker"));

    [Then(@"^the SVG has no rect element for that step$")]
    public void ThenTheSvgHasNoRectElementForThatStep()
    {
        Assert.Empty(SvgChartContext.RectsOf(_context.SlotOf("nvidia")));
        Assert.Empty(_context.AllRects());
    }

    [Then(@"^the cloudgpu slot has the text ""([^""]*)"" and no bar$")]
    public void ThenTheCloudGpuSlotHasTheTextAndNoBar(string expected)
    {
        XElement slot = _context.SlotOf("cloudgpu");

        Assert.Equal([expected], SvgChartContext.TextsOf(slot, "marker"));
        Assert.Empty(SvgChartContext.RectsOf(slot));
    }
}