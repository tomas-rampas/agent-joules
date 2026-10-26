using System.Security.Cryptography;

using AgentJoules.Specs.Support;
using AgentJoules.TestSupport;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-131: the README quick start, CLI reference and reproduce steps (REQ-025, REQ-032). Scoped to the feature because sentences such as "it fails with" and "the exit code is 0" belong to this story only. The Given
/// steps read the committed README through <see cref="RepositoryRoot"/> and the negative ones mutate the text in memory; the checks are the pure functions of <see cref="DocumentChecks"/>. The help of a command is the real
/// command line run in process through <see cref="CliHarness"/>.
/// </summary>
[Binding]
[Scope(Feature = "AJ-131 README quick start, CLI reference and reproduce steps")]
public sealed class ReadmeGuidesSteps(ReadmeGuidesContext context)
{
    private const string ReadmePath = "README.md";
    private const string DemoStoryPath = "docs/plan/stories/M6-docs-content-demo.md";
    private const int ReproduceStepCount = 10;

    private readonly ReadmeGuidesContext _context = context;

    private static string Committed() => RepositoryRoot.ReadAllText(ReadmePath);

    [Given(@"^the committed README\.md$")]
    [Given(@"^the CLI reference table of the README$")]
    public void GivenTheCommittedReadme() => _context.Readme = Committed();

    [Given(@"^a copy of the README whose run options include ""([^""]*)""$")]
    public void GivenAReadmeWithAnOption(string option)
    {
        _context.Readme = DocumentChecks.WithOptionInCommand(Committed(), "run", option);
        Assert.Contains(option, DocumentChecks.DocumentedOptions(_context.Readme, "run"));
    }

    // The value is built at run time: the prefix and the 16 characters never appear together in the source.
    [Given(@"^a copy of the README containing a runtime-built ""nvapi-"" plus 16 characters value$")]
    public void GivenAReadmeWithKeyMaterial() =>
        _context.Readme = Committed() + "\n" + string.Concat("nvapi", "-", Convert.ToHexString(RandomNumberGenerator.GetBytes(8))) + "\n";

    [Given(@"^a copy of the README without that heading$")]
    public void GivenAReadmeWithoutTheJudgesHeading() => _context.Readme = DocumentChecks.WithoutHeading(Committed(), DocumentChecks.JudgesHeading);

    [Given(@"^a copy of the README whose Judges without a GPU section mentions NVIDIA_API_KEY$")]
    public void GivenAReadmeWhoseJudgesSectionAsksForAKey()
    {
        string committed = Committed();
        IReadOnlyList<string> body = DocumentChecks.SectionAtAnyLevel(committed, DocumentChecks.JudgesHeading);
        _context.Readme = DocumentChecks.WithSectionBody(committed, DocumentChecks.JudgesHeading, [.. body, "Set NVIDIA_API_KEY before you start."]);
    }

    [Given(@"^a copy of the README whose Judges without a GPU section has a fifth action that runs a tier$")]
    public void GivenAReadmeWhoseJudgesSectionHasAFifthAction()
    {
        string committed = Committed();
        IReadOnlyList<string> body = DocumentChecks.SectionAtAnyLevel(committed, DocumentChecks.JudgesHeading);
        _context.Readme = DocumentChecks.WithSectionBody(committed, DocumentChecks.JudgesHeading, [.. body, "5. `dotnet run --project src/AgentJoules.Cli -- run` runs the local tier on the GPU."]);
    }

    [Given(@"^a copy of the README whose Reproduce step 3 runs the local tier only$")]
    public void GivenAReadmeWhoseReproduceStepThreeDiffers()
    {
        string committed = Committed();
        IReadOnlyList<string> body = DocumentChecks.SectionAtAnyLevel(committed, "Reproduce");
        Assert.Contains(body, static line => line.StartsWith("3. ", StringComparison.Ordinal) && line.Contains("run --tier all", StringComparison.Ordinal));
        _context.Readme = DocumentChecks.WithSectionBody(
            committed,
            "Reproduce",
            [.. body.Select(static line => line.StartsWith("3. ", StringComparison.Ordinal) ? line.Replace("run --tier all", "run --tier local", StringComparison.Ordinal) : line)]);
    }

    // The span of step 3 gains an option: the substring "run --tier all" would still be found, the backticked span would not.
    [Given(@"^a copy of the README whose Reproduce step 3 adds --dry-run to the command$")]
    public void GivenAReadmeWhoseReproduceStepThreeHasAnExtraOption()
    {
        string committed = Committed();
        IReadOnlyList<string> body = DocumentChecks.SectionAtAnyLevel(committed, "Reproduce");
        const string Span = "`dotnet run --project src/AgentJoules.Cli -- run --tier all`";
        Assert.Contains(body, line => line.StartsWith("3. ", StringComparison.Ordinal) && line.Contains(Span, StringComparison.Ordinal));
        _context.Readme = DocumentChecks.WithSectionBody(
            committed,
            "Reproduce",
            [.. body.Select(line => line.StartsWith("3. ", StringComparison.Ordinal) ? line.Replace(Span, "`dotnet run --project src/AgentJoules.Cli -- run --tier all --dry-run`", StringComparison.Ordinal) : line)]);
    }

    [Given(@"^a workspace where docs/report/dashboard\.png exists$")]
    public void GivenAWorkspaceWithTheScreenshot() => _context.PrepareWorkspace(Committed(), screenshotExists: true, regenerate: true);

    [Given(@"^a README that links docs/report/dashboard\.png and a workspace where the file is absent$")]
    public void GivenALinkWithoutTheFile() =>
        _context.PrepareWorkspace(Committed() + "\n![Live dashboard](" + DocumentChecks.DashboardScreenshot + ")\n", screenshotExists: false, regenerate: false);

    [Given(@"^a README without a link to docs/report/dashboard\.png and a workspace where the file exists$")]
    public void GivenAFileWithoutTheLink() =>
        _context.PrepareWorkspace(Committed().Replace(DocumentChecks.DashboardScreenshot, "docs/report/removed", StringComparison.Ordinal), screenshotExists: true, regenerate: false);

    [When(@"^the section ""([^""]*)"" is extracted$")]
    public void WhenTheSectionIsExtracted(string heading)
    {
        _context.Section = DocumentChecks.SectionAtAnyLevel(_context.Readme, heading);
        Assert.NotEmpty(_context.Section);
    }

    [When(@"^""run --help"" is executed in process$")]
    public async Task WhenRunHelpIsExecuted() => _ = await _context.RunHelpAsync("run");

    [When(@"^""--help"" is executed in process for each of the six commands$")]
    public async Task WhenEveryHelpIsExecuted()
    {
        foreach (string command in DocumentChecks.CliCommands)
        {
            _ = await _context.RunHelpAsync(command);
        }
    }

    [When(@"^the option check runs against ""run --help""$")]
    public async Task WhenTheOptionCheckRuns()
    {
        _ = await _context.RunHelpAsync("run");
        _context.Failures = DocumentChecks.OptionsNotInHelp(DocumentChecks.DocumentedOptions(_context.Readme, "run"), _context.HelpText("run"));
    }

    [When(@"^the exit-code table is parsed$")]
    public void WhenTheExitCodeTableIsParsed()
    {
        _context.Rows = DocumentChecks.ExitCodeRows(_context.Readme);
        _context.Failures = DocumentChecks.ExitCodeTable(_context.Readme);
    }

    [When(@"^the secret check runs on the file$")]
    public void WhenTheSecretCheckRuns() => _context.Failures = [.. new[] { DocumentChecks.KeyMaterial(_context.Readme, ReadmePath) }.OfType<string>()];

    [When(@"^the README structure check runs$")]
    public void WhenTheStructureCheckRuns() => _context.Failures = DocumentChecks.GuideStructure(_context.Readme);

    [When(@"^the Reproduce content check runs$")]
    public void WhenTheReproduceContentCheckRuns() =>
        _context.Failures = DocumentChecks.ReproduceMatchesDemo(_context.Readme, RepositoryRoot.ReadAllText(DemoStoryPath), ReproduceStepCount);

    [When(@"^the README link check runs$")]
    public async Task WhenTheLinkCheckRuns()
    {
        if (_context.Regenerate)
        {
            Assert.Equal(0, (await _context.UpdateWorkspaceReadmeAsync()).ExitCode);
        }

        _context.Failures = [.. new[] { DocumentChecks.DashboardLink(_context.WorkspaceReadme, _context.ScreenshotOnDisk()) }.OfType<string>()];
    }

    [Then(@"^it contains ""dotnet test"", ""validate results/"" and ""docs/report/index\.html""$")]
    public void ThenItContainsTheActions()
    {
        foreach (string action in new[] { "dotnet test", "validate results/", "docs/report/index.html" })
        {
            Assert.Contains(_context.Section, line => line.Contains(action, StringComparison.Ordinal));
        }

        Assert.Empty(DocumentChecks.JudgesSection(_context.Readme));
    }

    [Then(@"^it contains no ""([^""]*)"" text$")]
    public void ThenItContainsNoText(string text) => Assert.DoesNotContain(_context.Section, line => line.Contains(text, StringComparison.Ordinal));

    [Then(@"^the exit code is 0$")]
    public void ThenTheExitCodeIsZero() => Assert.Equal(0, _context.Help["run"].ExitCode);

    [Then(@"^the help text contains each option named for run: (.+)$")]
    public void ThenTheHelpContainsTheOptions(string options)
    {
        string[] named = options.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Empty(DocumentChecks.OptionsNotInHelp(named, _context.HelpText("run")));

        // The README names the same options for run, and every option it names is in the help.
        IReadOnlyList<string> documented = DocumentChecks.DocumentedOptions(_context.Readme, "run");
        Assert.All(named, option => Assert.Contains(option, documented));
        Assert.Empty(DocumentChecks.OptionsNotInHelp(documented, _context.HelpText("run")));
    }

    [Then(@"^each exits 0 and prints every long option the README names for it$")]
    public void ThenEveryHelpMatches()
    {
        foreach (string command in DocumentChecks.CliCommands)
        {
            Assert.Equal(0, _context.Help[command].ExitCode);
            IReadOnlyList<string> documented = DocumentChecks.DocumentedOptions(_context.Readme, command);
            Assert.True(documented.Count > 0, $"The CLI reference names no option for {command}.");
            Assert.Empty(DocumentChecks.OptionsNotInHelp(documented, _context.HelpText(command)));
        }
    }

    [Then(@"^it has the codes 0 1 2 3 4 5 6 7 each once$")]
    public void ThenTheTableHasEachCodeOnce()
    {
        Assert.Equal(Enumerable.Range(0, 8), _context.Rows.Select(static row => row.Code).Order());
        Assert.DoesNotContain(_context.Failures, static failure => failure.StartsWith("exit code missing", StringComparison.Ordinal) || failure.StartsWith("exit code repeated", StringComparison.Ordinal));
    }

    [Then(@"^each row names the meaning of the ExitCode of the same number$")]
    public void ThenEachRowMapsToTheExitCode() => Assert.Empty(_context.Failures);

    [Then(@"^it is an ordered list numbered 1 to 10$")]
    public void ThenItIsAnOrderedList()
    {
        Assert.Null(DocumentChecks.ReproduceSteps(_context.Readme, ReproduceStepCount));
        Assert.Equal(ReproduceStepCount, DocumentChecks.DemoDefinitionCommands(RepositoryRoot.ReadAllText(DemoStoryPath)).Count);
        Assert.Empty(DocumentChecks.ReproduceMatchesDemo(_context.Readme, RepositoryRoot.ReadAllText(DemoStoryPath), ReproduceStepCount));
    }

    [Then(@"^it cites arXiv 2407\.16893, TokenPowerBench, Watt Counts 2604\.09048 and greentoken with links and names the search of 2026-10-17$")]
    public void ThenItCitesTheSources() => Assert.Empty(DocumentChecks.PriorArt(_context.Readme));

    [Then(@"^the README links docs/report/dashboard\.png under the dashboard usage text$")]
    public void ThenTheReadmeLinksTheScreenshot()
    {
        Assert.Empty(_context.Failures);
        string block = ReadmeFixtures.BlockOf(_context.WorkspaceReadme);

        Assert.Contains("](" + DocumentChecks.DashboardScreenshot + ")", block, StringComparison.Ordinal);
    }

    [Then(@"^the check passes$")]
    public void ThenTheCheckPasses() => Assert.Empty(_context.Failures);

    [Then(@"^it fails with ""([^""]*)""$")]
    public void ThenItFailsWith(string failure) => Assert.Contains(failure, _context.Failures);
}