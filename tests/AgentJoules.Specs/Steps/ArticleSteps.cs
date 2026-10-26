using AgentJoules.Specs.Support;
using AgentJoules.TestSupport;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-137: the long-form article (REQ-027). Scoped to the feature because sentences such as "it fails with" and "the article check runs" belong to every documentation story. The committed article, results files and
/// the list of run files are read through <see cref="RepositoryRoot"/> and <see cref="RepositoryFiles"/>; the negative scenarios mutate a copy in memory, and the cloudgpu-absent rule is modelled on articles built in
/// memory, so no file is written. The state lives in <see cref="ContentDocumentContext"/>.
/// </summary>
[Binding]
[Scope(Feature = "AJ-137 Long-form article")]
public sealed class ArticleSteps(ContentDocumentContext context)
{
    private const string ArticlePath = "content/article.md";

    private readonly ContentDocumentContext _context = context;

    private static string Committed() => RepositoryRoot.ReadAllText(ArticlePath);

    private static bool CloudGpuRan() =>
        DocumentChecks.CloudGpuRan(RepositoryFiles.ResultsFileNames(), static name => RepositoryRoot.ReadAllText("results/" + name));

    private static IReadOnlyList<string> AllChecks(string article) =>
        DocumentChecks.ArticleChecks(article, RepositoryFiles.Exists, static name => RepositoryFiles.ReadOrNull("results/" + name), CloudGpuRan());

    [Given(@"^the committed content/article\.md$")]
    [Given(@"^the committed article$")]
    public void GivenTheCommittedArticle() => _context.Text = Committed();

    [Given(@"^a copy of the article with the line ""([^""]*)""$")]
    public void GivenACopyWithTheLine(string line) => _context.Text = Committed().TrimEnd('\n') + "\n\n" + line + "\n";

    [Given(@"^a copy of the article without the heading ""([^""]*)""$")]
    public void GivenACopyWithoutTheHeading(string heading)
    {
        _context.Text = DocumentChecks.WithoutHeading(Committed(), heading);
        Assert.NotNull(DocumentChecks.HasHeadingAtAnyLevel(_context.Text, heading));
    }

    [Given(@"^a copy of the article saying ""([^""]*)""$")]
    public void GivenACopySaying(string sentence) => _context.Text = Committed().TrimEnd('\n') + "\n\n" + sentence + ".\n";

    [Given(@"^a copy of the article without ""([^""]*)""$")]
    public void GivenACopyWithout(string word)
    {
        _context.Text = DocumentChecks.WithoutPhrase(Committed(), word, new string('x', word.Length));
        Assert.DoesNotContain(word, _context.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Given(@"^a copy of the article whose driver version differs from the local run file$")]
    public void GivenADifferentDriver()
    {
        string local = RepositoryFiles.LatestRunFile("local") ?? throw new Xunit.Sdk.XunitException("results/ holds no local run file");
        string driver = DocumentChecks.RunFileStrings(RepositoryRoot.ReadAllText(local)).First(static pair => pair.Field == "driverVersion").Value;
        _context.Text = Committed().Replace(driver, "000.00", StringComparison.Ordinal);
        Assert.NotEqual(Committed(), _context.Text);
    }

    [Given(@"^a copy of the article whose local runtime version differs from the local run file$")]
    public void GivenADifferentRuntimeVersion()
    {
        string local = RepositoryFiles.LatestRunFile("local") ?? throw new Xunit.Sdk.XunitException("results/ holds no local run file");
        System.Text.Json.Nodes.JsonNode environment = System.Text.Json.Nodes.JsonNode.Parse(RepositoryRoot.ReadAllText(local))!["environment"]!;
        string recorded = "local runtime: " + environment["localRuntime"]!.GetValue<string>() + " " + environment["runtimeVersion"]!.GetValue<string>();
        _context.Text = Committed().Replace(recorded, "local runtime: " + environment["localRuntime"]!.GetValue<string>() + " 9.9.9", StringComparison.Ordinal);
        Assert.NotEqual(Committed(), _context.Text);
    }

    [Given(@"^a copy of the article whose local environment header is prose$")]
    public void GivenAProseHeader()
    {
        string local = RepositoryFiles.LatestRunFile("local") ?? throw new Xunit.Sdk.XunitException("results/ holds no local run file");
        string[] lines = Committed().Split('\n');
        int at = Array.FindIndex(lines, line => line.StartsWith("GPU: ", StringComparison.Ordinal) && line.Contains(local, StringComparison.Ordinal));
        Assert.True(at >= 0, "the article has no environment header for the local run file");
        lines[at] = "The laptop ran the workflow on its own GPU.";
        _context.Text = string.Join('\n', lines);
    }

    [Given(@"^a copy of the article whose greentoken citation is written ""([^""]*)""$")]
    public void GivenACitationInAnotherCase(string written)
    {
        _context.Text = Committed().Replace("greentoken", written, StringComparison.Ordinal);
        Assert.DoesNotContain("greentoken", _context.Text, StringComparison.Ordinal);
    }

    [Given(@"^a copy of the article cut to (\d+) words$")]
    public void GivenACopyCut(int words) => _context.Text = string.Join(' ', Committed().Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries).Take(words));

    [Given(@"^a copy of the article with a non-ASCII character$")]
    public void GivenANonAsciiCopy() => _context.Text = Committed().TrimEnd('\n') + "\ncaf\u00e9\n";

    [Given(@"^an article that names the cloudgpu tier only in a sentence that it did not run$")]
    public void GivenAnArticleThatSaysItDidNotRun() => _context.Text = "The cloudgpu tier did not run, so this article has no cloudgpu result.\n";

    [Given(@"^an article with a cloudgpu result line$")]
    public void GivenAnArticleWithACloudResult() => _context.Text = "The cloudgpu tier used 55 J measured (results/20260101T000000Z-cloudgpu.json).\n";

    [Given(@"^an article that does not mention the cloudgpu tier$")]
    public void GivenAnArticleWithoutTheTier() => _context.Text = "Only the laptop ran.\n";

    [When(@"^the citations and headings are checked$")]
    public void WhenCitationsAndHeadingsAreChecked() => _context.Failures = [.. DocumentChecks.ArticleCitations(_context.Text), .. DocumentChecks.ArticleSections(_context.Text)];

    [When(@"^each line with a measured quantity is checked$")]
    public void WhenQuantitiesAreChecked() => _context.Failures = DocumentChecks.ArticleQuantities(_context.Text, RepositoryFiles.Exists);

    [When(@"^the words are counted$")]
    public void WhenTheWordsAreCounted() => _context.Failures = DocumentChecks.ArticleLength(_context.Text);

    [When(@"^the article check runs$")]
    public void WhenTheArticleCheckRuns() => _context.Failures = AllChecks(_context.Text);

    [When(@"^the sections are checked$")]
    public void WhenTheSectionsAreChecked() => _context.Failures = DocumentChecks.ArticleSections(_context.Text);

    [When(@"^the environment of each results table is compared with its run file$")]
    public void WhenTheEnvironmentIsCompared() => _context.Failures = DocumentChecks.ArticleEnvironment(_context.Text, static name => RepositoryFiles.ReadOrNull("results/" + name));

    [When(@"^the character set is checked$")]
    public void WhenTheCharacterSetIsChecked() => _context.Failures = DocumentChecks.AsciiAndLf(_context.Text);

    [When(@"^the cloudgpu-absent rule is applied with no cloudgpu run file$")]
    public void WhenTheAbsentRuleIsApplied() => _context.Failures = DocumentChecks.CloudGpuAbsence(_context.Text, cloudGpuRan: false);

    [Then(@"^it contains 2407\.16893, TokenPowerBench, 2604\.09048, greentoken and one ""Limitations"" heading$")]
    public void ThenItContainsTheCitations()
    {
        Assert.Empty(DocumentChecks.ArticleCitations(_context.Text));
        Assert.Null(DocumentChecks.HasHeadingAtAnyLevel(_context.Text, "Limitations"));
    }

    [Then(@"^the Limitations section contains GPU-only, display, power limit, sampling, offload, hosted and cloud$")]
    public void ThenTheLimitationsAreThere() => Assert.DoesNotContain(_context.Failures, static failure => failure.StartsWith("Limitations", StringComparison.Ordinal));

    [Then(@"^each names an existing results file and one label word$")]
    public void ThenEachQuantityIsSourced()
    {
        Assert.NotEmpty(DocumentChecks.QuantityPattern().Matches(_context.Text));
        Assert.Empty(_context.Failures);
    }

    [Then(@"^the count is between 800 and 2500$")]
    public void ThenTheCountIsInRange()
    {
        Assert.InRange(DocumentChecks.WordCount(_context.Text), DocumentChecks.ArticleMinWords, DocumentChecks.ArticleMaxWords);
        Assert.Empty(_context.Failures);
    }

    [Then(@"^it fails with ""([^""]*)""$")]
    public void ThenItFailsWith(string failure) => Assert.Contains(failure, _context.Failures);

    [Then(@"^it fails with a message starting ""([^""]*)""$")]
    public void ThenItFailsWithAPrefix(string prefix) => Assert.Contains(_context.Failures, failure => failure.StartsWith(prefix, StringComparison.Ordinal));

    [Then(@"^it fails with a message ending ""([^""]*)""$")]
    public void ThenItFailsWithASuffix(string suffix) => Assert.Contains(_context.Failures, failure => failure.EndsWith(suffix, StringComparison.Ordinal));

    [Then(@"^it reports no failure$")]
    [Then(@"^it passes$")]
    [Then(@"^no character failure is found$")]
    public void ThenNoFailure() => Assert.Empty(_context.Failures);

    [Then(@"^What is new names per-agent-step attribution, \.NET, Microsoft Agent Framework, Nemotron and the search of 2026-10-17$")]
    public void ThenWhatIsNewIsComplete()
    {
        Assert.DoesNotContain(_context.Failures, static failure => failure.StartsWith("What is new", StringComparison.Ordinal) || failure.StartsWith("missing heading: What is new", StringComparison.Ordinal));
    }

    [Then(@"^GPU name, driver, run date, local runtime and model tags equal the strings of the run file and at least one table names a run file$")]
    public void ThenTheEnvironmentEqualsTheRunFile()
    {
        Assert.Empty(_context.Failures);
        Assert.Contains(DocumentChecks.Tables(_context.Text), static table => DocumentChecks.RunFilesMentioned(string.Join('\n', table.Rows.SelectMany(static row => row.Cells))).Count > 0);
    }
}