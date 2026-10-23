using System.Text.RegularExpressions;

using AgentJoules.Specs.Support;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-085: the static HTML report page (REQ-030). Scoped to the feature because sentences such as "the HTML page is rendered" are reused by other stories with other meanings. The state lives in
/// <see cref="HtmlPageContext"/>; every page scenario runs the real aggregator and the real renderer over run documents built in memory, and the marker scenarios run the real <c>NoJekyllWriter</c> on a docs folder
/// of a temporary workspace.
/// </summary>
[Binding]
[Scope(Feature = "AJ-085 Generate the static HTML report page for the demo URL")]
public sealed partial class HtmlPageSteps(HtmlPageContext context)
{
    private const string KnownMarkerContent = "keep me";

    private readonly HtmlPageContext _context = context;

    [Given(@"^a fixture with a local run and a nvidia run$")]
    public void GivenALocalRunAndANvidiaRun() => _context.AddLocalAndNvidia();

    [Given(@"^a fixture with 3 tasks, 4 agents and 1 tier$")]
    public void GivenThreeTasksFourAgentsOneTier() => _context.AddThreeTasksAndFourAgents();

    [Given(@"^any fixture$")]
    public void GivenAnyFixture() => _context.AddLocalAndNvidia();

    [Given(@"^a model ID containing ""([^""]*)""$")]
    public void GivenAModelIdContaining(string text) => _context.AddLocalStepNamed(text);

    [Given(@"^a docs folder without \.nojekyll$")]
    public void GivenADocsFolderWithoutNoJekyll() => _context.PrepareDocsFolder(content: null);

    [Given(@"^a docs folder with a \.nojekyll file of known content$")]
    public void GivenADocsFolderWithAKnownNoJekyll() => _context.PrepareDocsFolder(KnownMarkerContent);

    [When(@"^the HTML page is rendered$")]
    public void WhenTheHtmlPageIsRendered() => _context.Render();

    [When(@"^the report is written$")]
    public void WhenTheReportIsWritten() => _context.WriteReport();

    [Then(@"^it contains exactly one table, ""([^""]*)"" and ""([^""]*)""$")]
    public void ThenItContainsOneTableAndTheTwoTexts(string chart, string sentence)
    {
        Assert.Equal(1, _context.TableCount());
        Assert.Contains(chart, _context.Html, StringComparison.Ordinal);
        Assert.Contains(sentence, _context.Html, StringComparison.Ordinal);
    }

    [Then(@"^the aggregate table body has 12 rows$")]
    public void ThenTheAggregateTableBodyHasTwelveRows() => Assert.Equal(HtmlPageContext.ExpectedRows, _context.AggregateRowCount());

    [Then(@"^docs/\.nojekyll exists$")]
    public void ThenNoJekyllExists() => Assert.True(File.Exists(_context.NoJekyllPath));

    [Then(@"^the text ""([^""]*)"" does not appear$")]
    public void ThenTheTextDoesNotAppear(string text) => Assert.DoesNotContain(text, _context.Html, StringComparison.OrdinalIgnoreCase);

    [Then(@"^no link element and no src other than ""([^""]*)"" appears$")]
    public void ThenNoLinkElementAndNoOtherSrcAppears(string allowed)
    {
        Assert.DoesNotMatch(LinkElement(), _context.Html!);
        Assert.All(_context.SrcValues(), value => Assert.Equal(allowed, value));
    }

    [Then(@"^the text ""([^""]*)"" does not appear in the output$")]
    public void ThenTheTextDoesNotAppearInTheOutput(string text) => Assert.DoesNotContain(text, _context.Html, StringComparison.OrdinalIgnoreCase);

    [Then(@"^the file content is unchanged$")]
    public void ThenTheFileContentIsUnchanged() => Assert.Equal(_context.KnownContent, File.ReadAllBytes(_context.NoJekyllPath));

    [GeneratedRegex("<link[ >/]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LinkElement();
}