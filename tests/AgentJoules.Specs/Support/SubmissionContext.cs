using AgentJoules.Cli;
using AgentJoules.TestSupport;

using Xunit.Sdk;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The state of one AJ-157, AJ-158 or AJ-159 scenario, handed to the step classes by Reqnroll context injection. A scenario about the committed tree reads the repository through <see cref="Root"/> and changes
/// nothing; a negative scenario lays a fixture out in a <see cref="TempWorkspace"/> (built by <see cref="SubmissionFixtures"/> with one defect) and applies the same <see cref="EvidenceChecks"/> rule to it. The report command of the
/// cloudgpu-absent scenario runs in process through <see cref="CliHarness"/> with the fixture as the working directory. No scenario writes into the repository and none prints a key value.
/// </summary>
public sealed class SubmissionContext : IDisposable
{
    private TempWorkspace? _workspace;

    /// <summary>The folder the rules read: the repository or the fixture.</summary>
    public string Root { get; set; } = RepositoryRoot.Path;

    /// <summary>The rule the When step applies; set by the Given step that builds the subject.</summary>
    public Func<IReadOnlyList<string>>? Check { get; set; }

    /// <summary>The violations the last rule reported, or null before it ran.</summary>
    public IReadOnlyList<string>? Result { get; set; }

    /// <summary>The result of the last command, or null.</summary>
    public CliRunResult? Command { get; private set; }

    /// <summary>The violations of the last rule.</summary>
    /// <exception cref="XunitException">No rule has run.</exception>
    public IReadOnlyList<string> Violations => Result ?? throw new XunitException("The rule has not run yet.");

    /// <summary>Reads the committed repository.</summary>
    public void UseRepository() => Root = RepositoryRoot.Path;

    /// <summary>Starts a fresh fixture folder (the previous one is deleted) and makes it the folder the rules read.</summary>
    /// <returns>The workspace.</returns>
    public TempWorkspace NewFixture()
    {
        _workspace?.Dispose();
        _workspace = TempWorkspace.CreateEmpty();
        Root = _workspace.Root;
        return _workspace;
    }

    /// <summary>Applies the rule the Given step chose.</summary>
    /// <exception cref="XunitException">No Given step chose a rule.</exception>
    public void RunCheck() => Result = (Check ?? throw new XunitException("No rule was chosen by a Given step."))();

    /// <summary>Runs a command line in process with the folder as the working directory.</summary>
    /// <param name="tokens">The arguments without the program name.</param>
    /// <returns>A task that completes when the command ended.</returns>
    public async Task RunCommandAsync(params string[] tokens) =>
        Command = await CliHarness.RunTokensAsync(tokens, new CliHostOverrides(WorkingDirectory: Root)).WaitAsync(TimeSpan.FromMinutes(2));

    /// <inheritdoc />
    public void Dispose() => _workspace?.Dispose();
}