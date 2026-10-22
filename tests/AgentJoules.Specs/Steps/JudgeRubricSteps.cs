using System.Text.RegularExpressions;

using AgentJoules.Core.Cli;
using AgentJoules.Specs.Support;

using Reqnroll;

using Xunit;
using Xunit.Sdk;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-075: commit the judge rubric and load it (REQ-010). Scoped to the feature because sentences such as "the rubric is loaded" and "the exit code is 2" are reused by other stories with other meanings.
/// The state lives in <see cref="JudgeRubricContext"/>. The real loader runs over the committed rubric or over a file written to a temporary folder; "exit code" and "output" are the exit code
/// and the message of the <see cref="ConfigurationException"/> the loader throws.
/// </summary>
[Binding]
[Scope(Feature = "AJ-075 Commit the judge rubric and load it")]
public sealed class JudgeRubricSteps(JudgeRubricContext context)
{
    private readonly JudgeRubricContext _context = context;

    private ConfigurationException Failure => _context.Failure ?? throw new XunitException("The loader did not fail.");

    private string Text => _context.Loaded ?? throw new XunitException("The loader returned no text.");

    [Given(@"^the committed config/judge-rubric\.md$")]
    public void GivenTheCommittedRubric() => _context.UseCommittedRubric();

    [Given(@"^a rubric file with known text$")]
    public void GivenARubricWithKnownText() => _context.WriteKnownRubric();

    [Given(@"^Paths:Rubric points to a file that does not exist$")]
    public void GivenTheRubricFileIsMissing() => _context.UseMissingFile();

    [Given(@"^a rubric file that defines levels 1, 2, 4 and 5 only$")]
    public void GivenARubricWithoutLevelThree() => _context.WriteRubricWithLevels(1, 2, 4, 5);

    [When(@"^the rubric is loaded$")]
    public void WhenTheRubricIsLoaded() => _context.Load();

    [Then(@"^levels 1, 2, 3, 4 and 5 are present$")]
    public void ThenAllFiveLevelsArePresent()
    {
        Assert.Null(_context.Failure);
        foreach (int level in new[] { 1, 2, 3, 4, 5 })
        {
            Assert.Matches(new Regex($"^{level}\\. ", RegexOptions.Multiline, TimeSpan.FromSeconds(1)), Text);
        }
    }

    [Then(@"^the returned text equals the file content$")]
    public void ThenTheTextEqualsTheFile()
    {
        Assert.Null(_context.Failure);
        Assert.NotNull(_context.WrittenText);
        Assert.Equal(_context.WrittenText, Text);
    }

    [Then(@"^the exit code is 2$")]
    public void ThenTheExitCodeIsTwo()
    {
        Assert.Equal(ExitCode.InvalidArguments, Failure.ExitCode);
        Assert.Equal(2, (int)Failure.ExitCode);
    }

    [Then(@"^the output names the configured path$")]
    public void ThenTheOutputNamesTheConfiguredPath()
    {
        Assert.Contains("Paths:Rubric", Failure.Message, StringComparison.Ordinal);
        Assert.Contains(JudgeRubricContext.RubricFileName, Failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_context.Folder!, Failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Then(@"^the output names level 3$")]
    public void ThenTheOutputNamesLevelThree()
    {
        Assert.Contains("level 3", Failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("level 1", Failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("level 2", Failure.Message, StringComparison.Ordinal);
    }
}