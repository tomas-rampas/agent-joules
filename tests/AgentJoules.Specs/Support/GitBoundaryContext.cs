using AgentJoules.TestSupport;

using Xunit.Sdk;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The state of one AJ-156 scenario, handed to the step classes by Reqnroll context injection. A hermetic scenario lays a temporary repository out in a <see cref="TempWorkspace"/> (deleted with the scenario):
/// a work repository on <c>main</c> with a local identity and no hook, and, when the scenario needs a remote, a bare <c>origin.git</c> inside the same workspace that is cloned from the work repository and fetched,
/// so origin/main can be staged without a network and without sending anything. The @integration scenarios call <see cref="OpenGate"/>; with <c>AGENTJOULES_INTEGRATION</c> closed they keep the reason in <see cref="SkipNote"/>.
/// </summary>
public sealed class GitBoundaryContext : IDisposable
{
    private TempWorkspace? _workspace;

    /// <summary>The folder the rules read: the repository or the work repository of the fixture.</summary>
    public string Root { get; set; } = RepositoryRoot.Path;

    /// <summary>The recorded hash of the scenario: the build-start of the history check or the recorded base of the boundary check.</summary>
    public string Recorded { get; set; } = string.Empty;

    /// <summary>The text a rule works on: the checklist or the output of a captured command.</summary>
    public string? Text { get; set; }

    /// <summary>The rule the When step applies; set by the Given step that builds the subject.</summary>
    public Func<IReadOnlyList<string>>? Check { get; set; }

    /// <summary>The violations the last rule reported, or null before it ran.</summary>
    public IReadOnlyList<string>? Result { get; set; }

    /// <summary>The result of the last push-boundary check, or null.</summary>
    public BoundaryResult? Boundary { get; set; }

    /// <summary>The reason a gated scenario passes without asserting, or null when it asserts.</summary>
    public string? SkipNote { get; private set; }

    /// <summary>The violations of the last rule.</summary>
    /// <exception cref="XunitException">No rule has run.</exception>
    public IReadOnlyList<string> Violations => Result ?? throw new XunitException("The rule has not run yet.");

    /// <summary>Evaluates the integration gate; with it closed the reason is kept in <see cref="SkipNote"/>.</summary>
    public void OpenGate() => SkipNote = IntegrationGate.SkipReason(Environment.GetEnvironmentVariable(IntegrationGate.VariableName));

    /// <summary>Starts a fresh repository on <c>main</c> with a local identity and one base commit; the previous fixture is deleted.</summary>
    /// <param name="baseSubject">The subject of the base commit.</param>
    /// <returns>The hash of the base commit.</returns>
    public string NewRepository(string baseSubject)
    {
        _workspace?.Dispose();
        _workspace = TempWorkspace.CreateEmpty();
        Root = Path.Combine(_workspace.Root, "work");
        _ = Directory.CreateDirectory(Root);
        Git("init", "--quiet", "--initial-branch=main");
        Git("config", "user.name", "AJ-156 fixture");
        Git("config", "user.email", "fixture@example.invalid");
        Git("config", "commit.gpgsign", "false");
        Commit(baseSubject);
        Recorded = Head();
        return Recorded;
    }

    /// <summary>Commits an empty commit in the work repository without a hook.</summary>
    /// <param name="subject">The subject.</param>
    /// <exception cref="InvalidOperationException">No repository was created.</exception>
    public void Commit(string subject)
    {
        RequireWorkspace();
        GitResult result = EvidenceChecks.CommitWithoutHooks(Root, subject);
        if (result.ExitCode != 0)
        {
            throw new XunitException("git commit failed: " + result.Stderr.Trim());
        }
    }

    /// <summary>Creates the bare remote inside the workspace from the work repository as it is now and makes it <c>origin</c> with the tracking reference fetched.</summary>
    public void AttachOrigin()
    {
        string bare = Path.Combine(_workspace?.Root ?? throw new XunitException("No repository was created."), "origin.git");
        GitIn(Root, "-c", "core.hooksPath=" + Path.Combine(Root, "no-hooks"), "clone", "--quiet", "--bare", Root, bare);
        Git("remote", "add", "origin", bare);
        Git("-c", "core.hooksPath=" + Path.Combine(Root, "no-hooks"), "fetch", "--quiet", "origin");
    }

    /// <summary>The path of the bare remote.</summary>
    public string OriginPath => Path.Combine(_workspace?.Root ?? throw new XunitException("No repository was created."), "origin.git");

    /// <summary>The full hash of HEAD in the work repository.</summary>
    /// <returns>The hash.</returns>
    public string Head() => Git("rev-parse", "HEAD").Trim();

    /// <summary>Writes a file in the work repository.</summary>
    /// <param name="relativePath">The path under the work repository.</param>
    /// <param name="content">The text.</param>
    /// <exception cref="InvalidOperationException">No repository was created.</exception>
    public void Write(string relativePath, string content)
    {
        RequireWorkspace();
        File.WriteAllText(Path.Combine(Root, relativePath), content);
    }

    /// <summary>Runs git in the work repository and requires exit 0.</summary>
    /// <param name="args">The arguments.</param>
    /// <returns>Standard output.</returns>
    /// <exception cref="InvalidOperationException">No repository was created.</exception>
    public string Git(params string[] args)
    {
        RequireWorkspace();
        return GitIn(Root, args);
    }

    private void RequireWorkspace()
    {
        if (_workspace is null)
        {
            throw new InvalidOperationException("No repository was created: call NewRepository first.");
        }
    }

    private static string GitIn(string folder, params string[] args)
    {
        GitResult result = GitRunner.Run(folder, args);
        return result.ExitCode == 0 ? result.Stdout : throw new XunitException("git " + args[0] + " failed: " + result.Stderr.Trim());
    }

    /// <inheritdoc />
    public void Dispose() => _workspace?.Dispose();
}