using AgentJoules.Specs.Support;
using AgentJoules.TestSupport;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-136: the demo video script (REQ-027). Scoped to the feature because sentences such as "it fails with" and "the script check runs" belong to every documentation story. The committed script is read through
/// <see cref="RepositoryRoot"/>; the negative scenarios mutate a copy in memory with the helpers of <see cref="DocumentChecks"/>, so no file is written. The help of each command shown is the real command line run in
/// process (<see cref="ReadmeGuidesContext"/>). Existence of a results file is asked of the file system through <see cref="RepositoryFiles"/>.
/// </summary>
[Binding]
[Scope(Feature = "AJ-136 Demo video script")]
public sealed class DemoScriptSteps(ContentDocumentContext context, ReadmeGuidesContext guides)
{
    private const string ScriptPath = "content/demo-script.md";

    private readonly ContentDocumentContext _context = context;
    private readonly ReadmeGuidesContext _guides = guides;

    private static string Committed() => RepositoryRoot.ReadAllText(ScriptPath);

    private static string StoryText() => RepositoryRoot.ReadAllText("docs/plan/stories/M6-docs-content-demo.md");

    private static ShotRow MainShot(string script, int shot) =>
        Assert.Single(DocumentChecks.ShotTables(script).First(static table => !table.Fallback).Rows, row => row.Shot == shot);

    [Given(@"^the committed content/demo-script\.md$")]
    [Given(@"^the parsed shot list$")]
    [Given(@"^the commands listed under ""Commands shown""$")]
    public void GivenTheCommittedScript() => _context.Text = Committed();

    [Given(@"^a copy of the script whose durations sum to (\d+)$")]
    public void GivenADurationSum(int total)
    {
        string script = Committed();
        ShotRow last = DocumentChecks.ShotTables(script).First(static table => !table.Fallback).Rows[^1];
        int current = DocumentChecks.ShotTables(script).First(static table => !table.Fallback).Rows.Sum(static row => row.Duration);
        _context.Text = DocumentChecks.WithShotDuration(script, last.Shot, last.Duration + total - current);
        Assert.Equal(total, DocumentChecks.ShotTables(_context.Text).First(static table => !table.Fallback).Rows.Sum(static row => row.Duration));
    }

    [Given(@"^a copy of the script whose (\d+) second shot (\d+) has (\d+) narration words$")]
    public void GivenALongNarration(int seconds, int shot, int words)
    {
        string script = Committed();
        Assert.Equal(seconds, MainShot(script, shot).Duration);
        _context.Text = DocumentChecks.WithShotNarration(script, shot, string.Join(' ', Enumerable.Repeat("word", words)));
        Assert.Equal(words, DocumentChecks.SpokenWords(MainShot(_context.Text, shot).Narration));
    }

    [Given(@"^a copy of the script whose shot (\d+) narration says ""([^""]*)"" with no results file$")]
    public void GivenANarrationWithAnUnsourcedNumber(int shot, string said) => _context.Text = DocumentChecks.WithShotNarration(Committed(), shot, "The Writer step used " + said + " here.");

    [Given(@"^a copy of the script whose shot (\d+) narration says ""([^""]*)""$")]
    public void GivenANarration(int shot, string said) => _context.Text = DocumentChecks.WithShotNarration(Committed(), shot, said);

    [Given(@"^a copy of the script that shows ""AJ bench""$")]
    public void GivenAnUnknownCommandIsShown()
    {
        _context.Text = DocumentChecks.WithCommandShown(Committed(), "AJ bench");
        Assert.Contains("AJ bench", _context.Text, StringComparison.Ordinal);
    }

    [Given(@"^a copy of the script whose Commands shown add ""([^""]*)""$")]
    public void GivenACommandIsAddedToCommandsShown(string command)
    {
        _context.Text = DocumentChecks.WithCommandShown(Committed(), command);
        Assert.Contains(command, _context.Text, StringComparison.Ordinal);
    }

    [Given(@"^a copy of the script without the sentence that published results were recorded without a browser open$")]
    public void GivenAScriptWithoutTheBrowserSentence()
    {
        _context.Text = DocumentChecks.WithoutPhrase(Committed(), "recorded without a browser", "recorded elsewhere");
        Assert.NotNull(DocumentChecks.BrowserStatement(_context.Text));
    }

    [Given(@"^a copy of the script that never mentions ""--dashboard"" or ""dashboard --replay""$")]
    public void GivenAScriptWithoutDashboardCommands()
    {
        string text = Committed().Replace("dashboard --replay", "dashboard --playback", StringComparison.Ordinal).Replace("--dashboard", "--live", StringComparison.Ordinal);
        Assert.DoesNotContain("--dashboard", text, StringComparison.Ordinal);
        Assert.DoesNotContain("dashboard --replay", text, StringComparison.Ordinal);
        _context.Text = text;
    }

    [Given(@"^a copy of the script without the line ""([^""]*)""$")]
    public void GivenAScriptWithoutTheLine(string line)
    {
        _context.Text = DocumentChecks.WithoutLinesContaining(Committed(), line);
        Assert.DoesNotContain(line, _context.Text, StringComparison.Ordinal);
    }

    [Given(@"^a copy of the script whose shot (\d+) starts at second (\d+)$")]
    public void GivenAShotThatStartsLate(int shot, int start)
    {
        _context.Text = DocumentChecks.WithShotStart(Committed(), shot, start);
        Assert.Equal(start, MainShot(_context.Text, shot).Start);
    }

    [Given(@"^a copy of the script without the word ""([^""]*)""$")]
    public void GivenAScriptWithoutTheWord(string word)
    {
        _context.Text = DocumentChecks.WithoutPhrase(Committed(), word, "sample");
        Assert.DoesNotContain(word, _context.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Given(@"^a copy of the script without the sentence ""([^""]*)""$")]
    public void GivenAScriptWithoutTheSentence(string sentence)
    {
        _context.Text = DocumentChecks.WithoutPhrase(Committed(), sentence, "Energy is described elsewhere.");
        Assert.DoesNotContain(sentence, _context.Text, StringComparison.Ordinal);
    }

    [When(@"^the shot list is parsed$")]
    public void WhenTheShotListIsParsed() =>
        _context.Failures = [.. DocumentChecks.ShotBudget(_context.Text), .. DocumentChecks.ShotContiguity(_context.Text)];

    [When(@"^words per second are computed for each narration cell$")]
    public void WhenWordsPerSecondAreComputed() => _context.Failures = DocumentChecks.WordsPerSecond(_context.Text);

    [When(@"^each CLI command is run with ""--help"" in process$")]
    public async Task WhenEachCommandIsRunWithHelp()
    {
        _context.Failures = DocumentChecks.CommandsShown(_context.Text, StoryText());
        foreach (string verb in DocumentChecks.CommandVerbsShown(_context.Text))
        {
            _ = await _guides.RunHelpAsync(verb);
        }
    }

    [When(@"^the script check runs$")]
    public void WhenTheScriptCheckRuns() => _context.Failures = DocumentChecks.DemoScript(_context.Text, RepositoryFiles.Exists, StoryText());

    [When(@"^the character set is checked$")]
    public void WhenTheCharacterSetIsChecked() => _context.Failures = DocumentChecks.AsciiAndLf(_context.Text);

    [Then(@"^the durations sum to 165 and are at most 170$")]
    public void ThenTheDurationsFit()
    {
        Assert.DoesNotContain(_context.Failures, static failure => failure.StartsWith("total", StringComparison.Ordinal) || failure.StartsWith("missing shot list", StringComparison.Ordinal) || failure.StartsWith("shot list columns", StringComparison.Ordinal));
        Assert.Equal(DocumentChecks.PlannedSeconds, DocumentChecks.ShotTables(_context.Text).First(static table => !table.Fallback).Rows.Sum(static row => row.Duration));
    }

    [Then(@"^each start second equals the previous start plus the previous duration$")]
    public void ThenTheShotsAreContiguous() => Assert.Empty(DocumentChecks.ShotContiguity(_context.Text));

    [Then(@"^no shot exceeds 2\.5 words per second$")]
    public void ThenNoShotIsTooLong()
    {
        Assert.NotEmpty(DocumentChecks.ShotTables(_context.Text));
        Assert.Empty(_context.Failures);
    }

    [Then(@"^every exit code is 0$")]
    public void ThenEveryExitCodeIsZero()
    {
        string[] verbs = [.. DocumentChecks.CommandVerbsShown(_context.Text)];

        Assert.Empty(_context.Failures);
        Assert.Contains("probe", verbs);
        Assert.Contains("run", verbs);
        Assert.Contains("dashboard", verbs);
        foreach (string verb in verbs)
        {
            Assert.True(0 == _guides.Help[verb].ExitCode, $"{verb} --help exited {_guides.Help[verb].ExitCode}");
        }
    }

    [Then(@"^it fails with ""([^""]*)""$")]
    public void ThenItFailsWith(string failure) => Assert.Contains(failure, _context.Failures);

    [Then(@"^it reports no failure$")]
    [Then(@"^no character failure is found$")]
    public void ThenNoFailure() => Assert.Empty(_context.Failures);

    [Then(@"^a fallback shot table exists and its durations sum to 170 or less$")]
    public void ThenTheFallbackFits()
    {
        ShotTable[] fallbacks = [.. DocumentChecks.ShotTables(_context.Text).Where(static table => table.Fallback)];

        Assert.NotEmpty(fallbacks);
        Assert.All(fallbacks, static table => Assert.InRange(table.Rows.Sum(static row => row.Duration), 1, DocumentChecks.BudgetSeconds));
        Assert.Empty(_context.Failures);
    }
}