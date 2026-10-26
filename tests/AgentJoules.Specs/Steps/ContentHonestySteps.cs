using System.Globalization;
using System.Text.RegularExpressions;

using AgentJoules.Specs.Support;
using AgentJoules.TestSupport;
using AgentJoules.TestSupport.Docs;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-143: the content honesty audit (REQ-025 to REQ-028). Scoped to the feature because sentences such as "the honesty audit runs" belong to this story only. The committed files and results files are read through
/// <see cref="RepositoryRoot"/> and <see cref="RepositoryFiles"/>; the negative scenarios mutate copies in memory, and the cloudgpu-free scenarios are modelled on fixture texts built in memory, so no file is written.
/// The key-shaped and non-ASCII values are built at run time and never appear in the source or in a failure text.
/// </summary>
[Binding]
[Scope(Feature = "AJ-143 Content honesty audit")]
public sealed partial class ContentHonestySteps(ContentHonestyContext context)
{
    private const string FixtureResults = "results/20260101T000000Z-local.json";
    private const string ScopeSentence = "Energy is GPU-board energy only.";

    private static readonly string[] PostVariants = ["launch", "lesson-learned", "hackathon"];

    private readonly ContentHonestyContext _context = context;

    [GeneratedRegex(@"recorded without (?:a browser|one)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex BrowserStatement();

    private static string FixtureLine(string text) => text + "\n";

    // Fallback texts: every rule holds, the cloudgpu tier did not run, and each sentence that names Nebius AI Cloud says so.
    private static Dictionary<string, string> FallbackFiles()
    {
        string readme = FixtureLine("# Agent Joules") + "\nAgent Joules: energy per agent step, measured on your GPU\n\n"
            + $"{ScopeSentence} The local tier measured 120 J ({FixtureResults}).\n\n"
            + "The cloudgpu tier did not run, so Nebius AI Cloud is not part of the results.\n\n"
            + "The published results were recorded without a browser open.\n";
        string posts = "# Posts\n\n"
            + string.Concat(PostVariants.Select(static name => $"## Variant: {name}\n\n{ScopeSentence}\n\n"));
        string scoped = $"# Document\n\n{ScopeSentence} The local tier measured 120 J ({FixtureResults}).\n";
        string pitch = "# Pitch\n\n## Devpost elevator pitch (limit 200 characters)\n\n**A-local (fallback)** (192 characters)\n\n> Which agent step burns the most energy? A .NET middleware meters local GPU joules per step.\n\n"
            + "## Rubric and scores\n\nRubric fixed before scoring.\n";
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["README.md"] = readme,
            ["content/linkedin-posts.md"] = posts,
            ["content/demo-script.md"] = scoped + "\nThe published results were recorded without a browser open.\n",
            ["content/article.md"] = scoped,
            ["docs/devpost-story.md"] = scoped,
            ["docs/devpost-feedback.md"] = "# Feedback\n\n## Q1. Model\n\nThe tier did not run on Nebius AI Cloud.\n",
            ["docs/devpost-checklist.md"] = "# Checklist\n\nThe cloudgpu tier did not run.\n",
            ["docs/devpost-pitch.md"] = pitch,
            ["docs/methodology.md"] = "# Methodology\n\nA browser tab adds compositing load, so published runs are recorded without one.\n",
        };

        return files;
    }

    private static Dictionary<string, string> Committed() =>
        DocumentChecks.HonestyInputFiles.ToDictionary(static path => path, static path => RepositoryRoot.ReadAllText(path), StringComparer.Ordinal);

    private void UseCommitted()
    {
        _context.Files.Clear();
        foreach ((string path, string text) in Committed())
        {
            _context.Files[path] = text;
        }

        _context.Exists = RepositoryFiles.Exists;
        _context.Cloud = CloudGpuTaglineRule.Evaluate(RepositoryFiles.ResultsFileNames(), static name => RepositoryRoot.ReadAllText("results/" + name));
    }

    private void UseFallback()
    {
        _context.Files.Clear();
        foreach ((string path, string text) in FallbackFiles())
        {
            _context.Files[path] = text;
        }

        _context.Exists = static path => path == FixtureResults;
        _context.Cloud = new CloudGpuTierState(CloudGpuTaglineRule.Absent, null, null, null);
    }

    private void Append(string path, string paragraph) =>
        _context.Files[path] = _context.Files[path].TrimEnd('\n') + "\n\n" + paragraph + "\n";

    [Given(@"^the audited files and the committed results files$")]
    public void GivenTheAuditedFiles() => UseCommitted();

    [Given(@"^a workspace with no cloudgpu run file and content updated by the fallback paths$")]
    public void GivenAFallbackWorkspace() => UseFallback();

    [Given(@"^a workspace with no cloudgpu run file and a story sentence ""([^""]*)""$")]
    public void GivenAFallbackWorkspaceWithAStorySentence(string sentence)
    {
        UseFallback();
        Append("docs/devpost-story.md", sentence + ".");
    }

    [Given(@"^a copy of the article with ""([^""]*)""$")]
    public void GivenACopyOfTheArticle(string sentence)
    {
        UseCommitted();
        Append("content/article.md", sentence);
    }

    [Given(@"^a copy of the posts without the Sources line and with ""([^""]*)""$")]
    public void GivenPostsWithoutSources(string sentence)
    {
        UseCommitted();
        string posts = _context.Files["content/linkedin-posts.md"];
        Assert.StartsWith("Sources:", posts, StringComparison.Ordinal);
        _context.Files["content/linkedin-posts.md"] = posts[(posts.IndexOf('\n', StringComparison.Ordinal) + 1)..];
        Append("content/linkedin-posts.md", sentence);
    }

    [Given(@"^a copy of the article with a results file in one paragraph and ""([^""]*)"" in the next$")]
    public void GivenAResultsFileInAnotherParagraph(string sentence)
    {
        UseCommitted();
        Append("content/article.md", $"The run is {FixtureResults}.");
        Append("content/article.md", sentence);
    }

    [Given(@"^a copy of the README with ""([^""]*)""$")]
    public void GivenACopyOfTheReadme(string text)
    {
        UseCommitted();
        Append("README.md", text);
    }

    [Given(@"^a copy of a post saying ""([^""]*)""$")]
    public void GivenACopyOfAPost(string text)
    {
        UseCommitted();
        Append("content/linkedin-posts.md", text);
    }

    [Given(@"^a copy of a file containing a runtime-built ""nvapi-"" plus 16 characters value$")]
    public void GivenAKeyShapedValue()
    {
        UseCommitted();
        Append("docs/devpost-story.md", string.Concat("nvapi", "-", new string('k', 16)));
    }

    [Given(@"^a copy of a file containing the character U\+2014$")]
    public void GivenAnEmDash()
    {
        UseCommitted();
        Append("docs/devpost-story.md", "a" + char.ConvertFromUtf32(0x2014) + "b");
    }

    [Given(@"^a copy of the posts where the hackathon variant lacks ""([^""]*)""$")]
    public void GivenPostsWithoutTheScope(string phrase)
    {
        UseCommitted();
        string posts = _context.Files["content/linkedin-posts.md"];
        const string heading = "## Variant: hackathon";
        int start = posts.IndexOf(heading, StringComparison.Ordinal);
        Assert.True(start >= 0, "The posts hold no hackathon variant.");
        int next = posts.IndexOf("\n## ", start + heading.Length, StringComparison.Ordinal);
        string variant = next < 0 ? posts[start..] : posts[start..next];
        Assert.Contains(phrase, variant, StringComparison.Ordinal);
        _context.Files["content/linkedin-posts.md"] = posts[..start] + variant.Replace(phrase, "GPU energy", StringComparison.Ordinal) + (next < 0 ? string.Empty : posts[next..]);
    }

    [Given(@"^a copy of the article with a sourced quantity ""([^""]*)"" and no label$")]
    public void GivenASourcedUnlabelledQuantity(string quantity)
    {
        UseCommitted();
        string results = RepositoryFiles.ResultsFileNames()[0];
        Append("content/article.md", $"The run results/{results} gave {quantity}.");
    }

    [Given(@"^a workspace where the cloudgpu run file names an H100 and a story sentence ""([^""]*)""$")]
    public void GivenAnH100Workspace(string sentence)
    {
        UseCommitted();
        _context.Cloud = new CloudGpuTierState(CloudGpuTaglineRule.Complete, "results/fixture-cloudgpu.json", "NVIDIA H100 80GB HBM3", "completed");
        Append("docs/devpost-story.md", sentence);
    }

    [Given(@"^a copy of ""([^""]*)"" without the browser statement$")]
    public void GivenACopyWithoutTheBrowserStatement(string path)
    {
        UseCommitted();
        string text = _context.Files[path];
        string changed = BrowserStatement().Replace(text, "recorded with the tab open");
        Assert.NotEqual(text, changed);
        _context.Files[path] = changed;
    }

    [Given(@"^a copy of the README whose browser statement reads ""([^""]*)""$")]
    public void GivenAReadmeWhoseBrowserStatementReads(string statement)
    {
        UseCommitted();
        string text = _context.Files["README.md"];
        string changed = BrowserStatement().Replace(text, statement);
        Assert.NotEqual(text, changed);
        _context.Files["README.md"] = changed;
    }

    [Given(@"^a copy of the article with a lone carriage return in the middle of a line$")]
    public void GivenALoneCarriageReturn()
    {
        UseCommitted();
        Append("content/article.md", "The first half of the line" + "\r" + "continues after the carriage return.");
    }

    [When(@"^the honesty audit runs$")]
    public void WhenTheHonestyAuditRuns() => _context.Findings = DocumentChecks.HonestyAudit(_context.Files, _context.Exists, _context.Cloud);

    [Then(@"^it reports 0 findings for the rules source, label, zero, scope, ranking, format and cloudgpu$")]
    [Then(@"^it reports 0 findings$")]
    public void ThenNoFindings() =>
        Assert.True(_context.Findings.Count == 0, _context.Findings.Count.ToString(CultureInfo.InvariantCulture) + " findings:\n" + string.Join('\n', _context.Findings));

    [Then(@"^a finding reads ""([^""]*)""$")]
    public void ThenAFindingReads(string finding) =>
        Assert.Contains(_context.Findings, found => found.Contains(finding, StringComparison.Ordinal));
}