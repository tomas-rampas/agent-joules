using System.Globalization;
using System.Xml.Linq;

using AgentJoules.Core.Labels;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Reporting.Render;
using AgentJoules.TestSupport;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The state of one AJ-084 scenario (the SVG chart), handed to the step class by Reqnroll context injection. The Given steps build run documents in memory with <see cref="ReportRunFixtures"/> (the committed
/// fixture files are never read or changed); the When step runs the real <see cref="ReportAggregator"/> and <see cref="JoulesSvgRenderer"/>; the Then steps parse the text it returned with <c>XDocument.Parse</c>
/// and query it in the SVG namespace. There is no file, no HTTP, no model and no GPU.
/// </summary>
public sealed class SvgChartContext
{
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";

    private readonly List<SelectedRun> _runs = [];
    private XDocument? _document;

    /// <summary>The rendered SVG text, or null before the When step.</summary>
    public string? Text { get; private set; }

    /// <summary>Adds a local file with one measured step of the given net joules; a flag, when given, is carried by the step.</summary>
    /// <param name="net">The net joules of the step.</param>
    /// <param name="flags">The reading flags of the step.</param>
    public void AddLocalStep(double net, ReadingFlags flags = ReadingFlags.None) =>
        _runs.Add(ReportRunFixtures.RunOf(Tier.Local, [ReportRunFixtures.Step(Tier.Local, net: Metric.Measured(net, "J"), flags: flags)]));

    /// <summary>Adds a nvidia file with one hosted step: the net joules are N/A.</summary>
    public void AddHostedStep() => _runs.Add(ReportRunFixtures.RunOf(Tier.Nvidia, ReportRunFixtures.NetSteps(Tier.Nvidia, 1.0)));

    /// <summary>Adds one file with one step for each of the tiers in the list.</summary>
    /// <param name="tiers">The tiers to add.</param>
    public void AddTiers(params Tier[] tiers)
    {
        foreach (Tier tier in tiers)
        {
            _runs.Add(ReportRunFixtures.RunOf(tier, ReportRunFixtures.NetSteps(tier, 10.0)));
        }
    }

    /// <summary>Adds a local file whose task, agent and model ID all carry the given text.</summary>
    /// <param name="text">The hostile text.</param>
    public void AddLocalStepNamed(string text) =>
        _runs.Add(
            ReportRunFixtures.RunOf(
                Tier.Local,
                [ReportRunFixtures.Step(Tier.Local, task: text, agent: text, modelId: text)],
                edit: document => document with { Environment = document.Environment with { Models = [new EnvironmentModel(text, text, Baseline: false)] } }));

    /// <summary>Runs the real aggregator and the real renderer over the files added so far.</summary>
    public void Render()
    {
        Text = JoulesSvgRenderer.Render(ReportAggregator.Build(_runs));
        _document = null;
    }

    /// <summary>Parses the rendered text as XML; throws when it is not well-formed.</summary>
    /// <returns>The document.</returns>
    public XDocument Parse() => XDocument.Parse(Text ?? throw new InvalidOperationException("The chart was not rendered."));

    /// <summary>The elements of the SVG namespace with a name and a class, anywhere in the document.</summary>
    /// <param name="name">The element name.</param>
    /// <param name="cssClass">The class attribute value.</param>
    /// <returns>The elements.</returns>
    public XElement[] Elements(string name, string cssClass) =>
        [.. Document().Descendants(Svg + name).Where(element => (string?)element.Attribute("class") == cssClass)];

    /// <summary>The slots of the first group, in document order.</summary>
    /// <returns>The slot groups.</returns>
    public XElement[] Slots() => [.. Elements("g", "group").First().Elements(Svg + "g").Where(element => (string?)element.Attribute("class") == "slot")];

    /// <summary>The slot of a tier (the primary slot).</summary>
    /// <param name="tier">The tier name.</param>
    /// <returns>The slot element.</returns>
    public XElement SlotOf(string tier) =>
        Slots().Single(slot => (string?)slot.Attribute("data-tier") == tier && (string?)slot.Attribute("data-variant") == "primary");

    /// <summary>The text of the elements of a slot with a class.</summary>
    /// <param name="slot">The slot.</param>
    /// <param name="cssClass">The class.</param>
    /// <returns>The texts.</returns>
    public static string[] TextsOf(XElement slot, string cssClass) =>
        [.. slot.Elements(Svg + "text").Where(element => (string?)element.Attribute("class") == cssClass).Select(element => element.Value)];

    /// <summary>The rect elements of a slot.</summary>
    /// <param name="slot">The slot.</param>
    /// <returns>The rects.</returns>
    public static XElement[] RectsOf(XElement slot) => [.. slot.Elements(Svg + "rect")];

    /// <summary>All rect elements of the document.</summary>
    /// <returns>The rects.</returns>
    public XElement[] AllRects() => [.. Document().Descendants(Svg + "rect")];

    /// <summary>The value of a numeric attribute, read with the invariant culture.</summary>
    /// <param name="element">The element.</param>
    /// <param name="name">The attribute name.</param>
    /// <returns>The number.</returns>
    public static double Number(XElement element, string name) =>
        double.Parse((string?)element.Attribute(name) ?? throw new InvalidOperationException($"The element has no {name}."), NumberStyles.Float, CultureInfo.InvariantCulture);

    private XDocument Document() => _document ??= Parse();
}