using AgentJoules.Core.Runs;
using AgentJoules.Reporting.Content;
using AgentJoules.Specs.Support;
using AgentJoules.TestSupport;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-134: the three LinkedIn post drafts (REQ-026). Scoped to the feature because sentences such as "the exit code is 0" and "it fails with" belong to every documentation story. The committed template is read through
/// <see cref="RepositoryRoot"/>; the negative template scenarios mutate a copy in memory; the render scenarios run the real <c>report --render-content</c> in a temporary workspace that holds a copy of the committed
/// run files (so the committed template meets the numbers it was written against) or the in-memory fixtures. The state lives in <see cref="LinkedInPostsContext"/>.
/// </summary>
[Binding]
[Scope(Feature = "AJ-134 LinkedIn post drafts")]
public sealed class LinkedInPostsSteps(LinkedInPostsContext context)
{
    private const string PromptPath = "docs/agent-joules.prompt.md";
    private const string BuildLogPath = "docs/build-log.md";

    private readonly LinkedInPostsContext _context = context;

    private static string Committed() => RepositoryRoot.ReadAllText(LinkedInPostsContext.TemplatePath);

    [Given(@"^a workspace with latest results files for local, nvidia and nebius and the committed template$")]
    public void GivenAWorkspaceWithTheCommittedTemplate()
    {
        _context.AddCommittedRunFiles();
        _context.WriteTemplate(Committed());
    }

    [Given(@"^the committed template$")]
    public void GivenTheCommittedTemplate() => _context.Template = Committed();

    [Given(@"^a results fixture where the nvidia energy metric has value null and label ""N/A""$")]
    public static void GivenANullEnergyFixture() => Assert.Contains("\"label\": \"N/A\"", RunDocumentFixtures.CommittedText(Tier.Nvidia), StringComparison.Ordinal);

    [Given(@"^a template placeholder ""([^""]*)"" that the file lacks$")]
    public void GivenAPlaceholderTheFileLacks(string placeholder)
    {
        _context.AddFixtureRunFiles(Tier.Local);
        _context.WriteTemplate("## Variant: One\n" + placeholder + "\n");
    }

    [Given(@"^a template variant whose rendered text is (\d+) characters$")]
    public void GivenAVariantOfLength(int length)
    {
        _context.AddFixtureRunFiles(Tier.Local);
        _context.WriteLongVariantTemplate(length);
    }

    [Given(@"^a copy of the template containing ""([^""]*)""$")]
    public void GivenACopyContaining(string text) => _context.Template = Committed().TrimEnd('\n') + "\n" + text + "\n";

    [Given(@"^a workspace without a completed cloudgpu run file and a variant citing a cloudgpu placeholder$")]
    public void GivenNoCloudGpuFile()
    {
        _context.AddFixtureRunFiles(Tier.Local, Tier.Nvidia, Tier.Nebius);
        _context.WriteTemplate("## Variant: One\n{{results.cloudgpu.totals.netJoules.value}}\n");
    }

    [Given(@"^a copy of the template whose launch variant lacks ""([^""]*)""$")]
    public void GivenALaunchVariantWithoutThePlaceholder(string placeholder)
    {
        _context.Template = DocumentChecks.WithVariantBody(Committed(), "launch", body => body.Replace(placeholder, string.Empty, StringComparison.Ordinal));
        Assert.DoesNotContain(placeholder, Assert.Single(DocumentChecks.Variants(_context.Template), static variant => variant.Name == "launch").Body, StringComparison.Ordinal);
    }

    [Given(@"^a copy of the template with a fourth variant$")]
    public void GivenAFourthVariant() =>
        _context.Template = Committed().TrimEnd('\n') + "\n\n## Variant: extra\nGPU {{results.local.totals.netJoules.value}} J measured, {{repo.url}}\n";

    [Given(@"^a copy of the template whose launch variant cites a cloudgpu placeholder with the laptop-only scope sentence$")]
    public void GivenACloudVariantWithTheLaptopSentence() =>
        _context.Template = DocumentChecks.WithVariantBody(
            Committed(),
            "launch",
            body => body.Replace(DocumentChecks.CloudScope, DocumentChecks.LaptopScope, StringComparison.Ordinal) + "\n{{results.cloudgpu.totals.netJoules.value|F0}} J measured\n");

    [Given(@"^a copy of the template with a metric placeholder that no label word follows$")]
    public void GivenAnUnlabelledPlaceholder() =>
        _context.Template = DocumentChecks.WithVariantBody(Committed(), "launch", body => body + "\nEnergy {{results.local.totals.netJoules.value}}\n");

    [Given(@"^a copy of the template with a metric placeholder followed by the wrong label word$")]
    public void GivenAWronglyLabelledPlaceholder() =>
        _context.Template = DocumentChecks.WithVariantBody(Committed(), "launch", body => body + "\nEnergy {{results.local.totals.netJoules.value}} J estimated\n");

    [Given(@"^a copy of the template with a metric placeholder whose label word follows only the next placeholder$")]
    public void GivenALabelAfterTheNextPlaceholder() =>
        _context.Template = DocumentChecks.WithVariantBody(Committed(), "launch", body => body + "\nEnergy {{results.local.totals.netJoules.value}} then {{results.local.totals.inputTokens.value}} tokens measured\n");

    [Given(@"^a copy of the template with a metric placeholder followed by the word unmeasured$")]
    public void GivenAPlaceholderFollowedByUnmeasured() =>
        _context.Template = DocumentChecks.WithVariantBody(Committed(), "launch", body => body + "\nEnergy {{results.local.totals.netJoules.value}} J unmeasured\n");

    [Given(@"^a copy of the template whose hackathon variant opens with another sentence$")]
    public void GivenAnotherOpening() =>
        _context.Template = DocumentChecks.WithVariantBody(Committed(), "hackathon", body => "Energy matters for agents.\n" + body);

    [Given(@"^a copy of the template whose lesson-learned hook line carries a digit$")]
    public void GivenAHookWithADigit()
    {
        _context.Template = DocumentChecks.WithVariantBody(Committed(), "lesson-learned", body => body.Replace("workflow.", "workflow, all 4 of them.", StringComparison.Ordinal));
        Assert.NotEqual(Committed(), _context.Template);
    }

    [When(@"^""(report [^""]*)"" runs$")]
    public async Task WhenReportRuns(string arguments) => _ = await _context.RunAsync(arguments);

    [When(@"^hand-typed quantities are searched outside placeholders$")]
    public void WhenQuantitiesAreSearched() => _context.Failures = DocumentChecks.HandTypedQuantities(_context.Template);

    [When(@"^a placeholder for that metric is rendered$")]
    public void WhenThePlaceholderIsRendered()
    {
        var sources = new Dictionary<Tier, ContentSource> { [Tier.Nvidia] = new(RunDocumentFixtures.CommittedFileName(Tier.Nvidia), RunDocumentFixtures.CommittedText(Tier.Nvidia)) };
        string output = ContentTemplateRenderer.Render("## Variant: One\n{{results.nvidia.totals.grossJoules.value}}\n", sources);
        string[] lines = output.Split('\n');
        _context.Rendered = lines[Array.FindIndex(lines, static line => line.StartsWith("## Variant:", StringComparison.Ordinal)) + 1];
    }

    [When(@"^the template check runs$")]
    public void WhenTheTemplateCheckRuns() =>
        _context.Failures = DocumentChecks.LinkedInTemplate(_context.Template, RepositoryRoot.ReadAllText(PromptPath), RepositoryRoot.ReadAllText(BuildLogPath), RepositoryFiles.LatestRunText);

    [When(@"^the variant headings and the hooks are checked$")]
    public void WhenHeadingsAndHooksAreChecked() =>
        _context.Failures =
        [
            .. DocumentChecks.VariantHeadings(_context.Template),
            .. DocumentChecks.Hooks(_context.Template, DocumentChecks.PromptHooks(RepositoryRoot.ReadAllText(PromptPath)), DocumentChecks.HookRefinements(RepositoryRoot.ReadAllText(BuildLogPath))),
        ];

    [When(@"^the character set is checked$")]
    public void WhenTheCharacterSetIsChecked() => _context.Failures = DocumentChecks.AsciiAndLf(_context.Template);

    [Then(@"^the exit code is (\d+)$")]
    public void ThenTheExitCodeIs(int code) => Assert.True(code == _context.Last().ExitCode, $"Expected exit code {code} but got {_context.Last().ExitCode}; stderr: {_context.Last().Stderr}");

    [Then(@"^the output has three variants of 1300 characters or fewer$")]
    public void ThenThreeVariantsFit()
    {
        string content = _context.ContentText();
        IReadOnlyList<ContentVariant> variants = DocumentChecks.Variants(content);

        Assert.Equal(3, variants.Count);
        Assert.All(variants, static variant => Assert.True(variant.Body.Length <= DocumentChecks.MaxVariantCharacters, $"variant {variant.Name} has {variant.Body.Length} characters"));
        Assert.Equal(0, DocumentChecks.CountOf(content, "{{results."));
        Assert.Equal(3, DocumentChecks.CountOf(content, DocumentChecks.RepoUrlPlaceholder));
    }

    [Then(@"^the first line starts with ""Sources:"" and names run files that exist$")]
    public void ThenTheSourcesLineNamesFiles()
    {
        Assert.Empty(DocumentChecks.RenderedPosts(_context.ContentText(), _context.RunFileExists, 3));
        Assert.StartsWith("Sources: ", _context.ContentText().Split('\n')[0], StringComparison.Ordinal);
        Assert.DoesNotContain("none", _context.ContentText().Split('\n')[0], StringComparison.Ordinal);
    }

    [Then(@"^the rendered posts equal the committed content/linkedin-posts\.md byte for byte$")]
    public void ThenTheRenderEqualsTheCommittedPosts() =>
        Assert.True(
            File.ReadAllBytes(RepositoryRoot.Resolve(LinkedInPostsContext.ContentPath)).AsSpan().SequenceEqual(_context.ContentBytes()),
            "rendered posts differ from the committed file");

    [Then(@"^none are found$")]
    public void ThenNoneAreFound() => Assert.Empty(_context.Failures);

    [Then(@"^no failure is found$")]
    [Then(@"^it reports no failure$")]
    public void ThenNoFailure() => Assert.Empty(_context.Failures);

    [Then(@"^each metric placeholder is followed by a unit and one of measured, estimated, model-judged$")]
    public void ThenEachMetricPlaceholderIsLabelled()
    {
        Assert.Contains("{{results.", _context.Template, StringComparison.Ordinal);
        Assert.Empty(DocumentChecks.LabelAdjacency(_context.Template, RepositoryFiles.LatestRunText));
    }

    [Then(@"^the output reads ""N/A"" and never ""0"", ""null"" or an empty string$")]
    public void ThenTheOutputIsTheLabel()
    {
        Assert.Equal("N/A", _context.Rendered);
        Assert.NotEqual("0", _context.Rendered);
        Assert.NotEqual("null", _context.Rendered);
        Assert.NotEqual(string.Empty, _context.Rendered);
    }

    [Then(@"^the exit code is 2 and the message names the placeholder$")]
    public void ThenExit2NamesThePlaceholder()
    {
        Assert.Equal(2, _context.Last().ExitCode);
        Assert.Contains("at line 2: {{results.local.", _context.Last().Stderr, StringComparison.Ordinal);
    }

    [Then(@"^the exit code is 2 and the message names the variant and its length$")]
    public void ThenExit2NamesTheVariantAndItsLength()
    {
        Assert.Equal(2, _context.Last().ExitCode);
        Assert.Contains("Long", _context.Last().Stderr, StringComparison.Ordinal);
        Assert.Contains(_context.VariantLength.ToString(System.Globalization.CultureInfo.InvariantCulture), _context.Last().Stderr, StringComparison.Ordinal);
    }

    [Then(@"^the exit code is 2 and the message names the cloudgpu placeholder$")]
    public void ThenExit2NamesTheCloudGpuPlaceholder()
    {
        Assert.Equal(2, _context.Last().ExitCode);
        Assert.Contains("cloudgpu", _context.Last().Stderr, StringComparison.Ordinal);
    }

    [Then(@"^it fails with ""([^""]*)""$")]
    public void ThenItFailsWith(string failure) => Assert.Contains(failure, _context.Failures);

    [Then(@"^there are exactly three headings launch, lesson-learned and hackathon$")]
    public void ThenThereAreThreeHeadings()
    {
        Assert.Equal(DocumentChecks.VariantNames, DocumentChecks.Variants(_context.Template).Select(static variant => variant.Name).ToArray());
        Assert.DoesNotContain(_context.Failures, static failure => failure.StartsWith("expected 3", StringComparison.Ordinal) || failure.StartsWith("missing variant", StringComparison.Ordinal));
    }

    [Then(@"^the first sentence of each variant is a hook of the prompt or a recorded refinement without a digit$")]
    public void ThenTheHooksAreValid()
    {
        Assert.Equal(3, DocumentChecks.PromptHooks(RepositoryRoot.ReadAllText(PromptPath)).Count);
        Assert.DoesNotContain(_context.Failures, static failure => failure.Contains("hook", StringComparison.Ordinal));
    }
}