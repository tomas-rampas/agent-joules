using AgentJoules.TestSupport;

using Xunit.Sdk;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The state of one AJ-154 or AJ-155 scenario, handed to the step classes by Reqnroll context injection. A scenario about the committed tree reads the repository through <see cref="RepositoryRoot"/> and changes nothing;
/// a negative scenario lays a fixture out in a <see cref="TempWorkspace"/> (deleted with the scenario) and applies the same <see cref="EvidenceChecks"/> rule to it. The @integration scenarios call <see cref="OpenGate"/>
/// first: with <c>AGENTJOULES_INTEGRATION</c> closed they record the reason in <see cref="SkipNote"/> and the remaining steps pass without asserting. A key value is held only to compare and is never printed.
/// </summary>
public sealed class HardeningContext : IDisposable
{
    private TempWorkspace? _workspace;

    /// <summary>The folder the rules read: the repository or the fixture.</summary>
    public string Root { get; set; } = RepositoryRoot.Path;

    /// <summary>The text a rule works on: the build log, the workflow or a captured output.</summary>
    public string? Text { get; set; }

    /// <summary>The exit code of a captured command.</summary>
    public int ExitCode { get; set; }

    /// <summary>The number of Definition of Done boxes still unchecked.</summary>
    public int UncheckedBoxes { get; set; }

    /// <summary>The rule the When step applies; set by the Given step that builds the subject.</summary>
    public Func<IReadOnlyList<string>>? Check { get; set; }

    /// <summary>The violations the last rule reported, or null before it ran.</summary>
    public IReadOnlyList<string>? Result { get; set; }

    /// <summary>The line the summary formatter produced, or null.</summary>
    public string? Line { get; set; }

    /// <summary>A value that must never appear in a violation or a message (a runtime-built token or a key); held only to compare.</summary>
    public string? Secret { get; set; }

    /// <summary>The name of the tracked file of a key-scan fixture.</summary>
    public string TrackedName { get; set; } = string.Empty;

    /// <summary>The reason a gated scenario passes without asserting, or null when it asserts.</summary>
    public string? SkipNote { get; private set; }

    /// <summary>The value of the key variable of a gated scenario; held only to compare, never printed.</summary>
    public string? KeyValue { get; private set; }

    /// <summary>The result of each scan of a gated scenario, by variable name.</summary>
    public List<(string Variable, GitResult Result)> Scans { get; } = [];

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

    /// <summary>Evaluates the integration gate; with it closed the reason is kept in <see cref="SkipNote"/>.</summary>
    /// <param name="keyVariable">A variable that must be set when the gate is open (its value is read, never copied into a message), or null.</param>
    public void OpenGate(string? keyVariable = null)
    {
        SkipNote = IntegrationGate.SkipReason(Environment.GetEnvironmentVariable(IntegrationGate.VariableName));
        if (SkipNote is null && keyVariable is not null)
        {
            KeyValue = Environment.GetEnvironmentVariable(keyVariable);
            if (string.IsNullOrWhiteSpace(KeyValue))
            {
                throw new XunitException(keyVariable + " is not set");
            }
        }
    }

    /// <inheritdoc />
    public void Dispose() => _workspace?.Dispose();
}