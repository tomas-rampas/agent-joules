using AgentJoules.Core.Cli;
using AgentJoules.Core.Tasks;
using AgentJoules.Specs.Support;
using AgentJoules.Workflow.Tasks;

using Reqnroll;

using Xunit;
using Xunit.Sdk;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-048: load the fixed task set from <c>tasks/</c>. Scoped to the feature because sentences such as "the exit code is 2" and "the output names ..."
/// are reused by other stories with other meanings. The state lives in <see cref="TaskSetContext"/>. The loader runs for real over the committed
/// folder or over an edited copy of it; "exit code" and "output" are the exit code and the message of the <see cref="ConfigurationException"/> the
/// loader throws, which the CLI turns into the process exit code and the stderr line (proved by AJ-054 and AJ-056).
/// </summary>
[Binding]
[Scope(Feature = "AJ-048 Load the fixed task set from tasks/")]
public sealed class TaskSetSteps(TaskSetContext context)
{
    private readonly TaskSetContext _context = context;

    private ConfigurationException Failure => _context.Failure ?? throw new XunitException("The loader did not fail.");

    private IReadOnlyList<TaskDefinition> Loaded => _context.Tasks ?? throw new XunitException("No tasks were loaded.");

    private void Run(Func<IReadOnlyList<TaskDefinition>> load)
    {
        try
        {
            _context.Tasks = load();
        }
        catch (ConfigurationException failure)
        {
            _context.Failure = failure;
        }
    }

    [Given(@"^the committed tasks folder$")]
    public void GivenTheCommittedTasksFolder() => _context.UseCommittedFolder();

    [Given(@"^a tasks folder where ""([^""]*)"" has no maxOutputTokens$")]
    public void GivenAFolderWhereAFileHasNoLimit(string fileName)
    {
        _context.UseEditedCopy();
        _context.EditFile(fileName, root => Assert.True(root.Remove("maxOutputTokens"), fileName + " has no maxOutputTokens to remove"));
    }

    [Given(@"^a tasks folder with two files whose name is ""([^""]*)""$")]
    public void GivenAFolderWithTwoFilesOfTheSameName(string name)
    {
        _context.UseEditedCopy();
        foreach (string fileName in _context.CopiedFileNames().Take(2))
        {
            _context.EditFile(fileName, root => root["name"] = name);
        }
    }

    [When(@"^the task set is loaded$")]
    public void WhenTheTaskSetIsLoaded() => Run(() => TaskSetLoader.Load(_context.RequiredDirectory));

    [When(@"^the task ""([^""]*)"" is requested$")]
    public void WhenATaskIsRequested(string name) => Run(() => TaskSetLoader.Load(_context.RequiredDirectory, name));

    [Then(@"^the names are ""([^""]*)"", ""([^""]*)"" and ""([^""]*)"" in that order$")]
    public void ThenTheNamesAreInOrder(string first, string second, string third)
    {
        Assert.Null(_context.Failure);
        Assert.Equal([first, second, third], Loaded.Select(task => task.Name));
    }

    [Then(@"^exactly 1 task with maxOutputTokens (\d+) is returned$")]
    public void ThenExactlyOneTaskIsReturned(int maxOutputTokens)
    {
        Assert.Null(_context.Failure);
        Assert.Equal(maxOutputTokens, Assert.Single(Loaded).MaxOutputTokens);
    }

    [Then(@"^the exit code is 2$")]
    public void ThenTheExitCodeIsTwo()
    {
        Assert.Equal(ExitCode.InvalidArguments, Failure.ExitCode);
        Assert.Equal(2, (int)Failure.ExitCode);
    }

    [Then(@"^the output names ""([^""]*)""$")]
    public void ThenTheOutputNamesTheFile(string fileName) => Assert.Contains(fileName, Failure.Message, StringComparison.Ordinal);

    [Then(@"^the output lists ""([^""]*)"", ""([^""]*)"" and ""([^""]*)""$")]
    public void ThenTheOutputListsTheNames(string first, string second, string third)
    {
        string message = Failure.Message;
        int firstAt = message.IndexOf(first, StringComparison.Ordinal);
        int secondAt = message.IndexOf(second, StringComparison.Ordinal);
        int thirdAt = message.IndexOf(third, StringComparison.Ordinal);

        Assert.True(firstAt >= 0 && firstAt < secondAt && secondAt < thirdAt, "The message does not list the three names in file order: " + message);
    }
}