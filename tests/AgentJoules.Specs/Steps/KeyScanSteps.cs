using System.Globalization;

using AgentJoules.TestSupport;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-143: the key scan with the real keys (REQ-025 to REQ-028). The scan of the repository is gated by <see cref="IntegrationGate"/> like the other @integration features; it passes each key to git on standard input
/// (never in the argument list), asks for file names only and prints neither a value nor a matching line. The negative scenario is hermetic: a scratch repository in a temporary folder holds a tracked file with a
/// synthetic value built at run time, so no real key is written to disk and the repository is never touched.
/// </summary>
[Binding]
[Scope(Feature = "AJ-143 Key scan with the real keys")]
public sealed class KeyScanSteps(IReqnrollOutputHelper output) : IDisposable
{
    private readonly IReqnrollOutputHelper _output = output;

    private static readonly string[] KeyVariables = ["NVIDIA_API_KEY", "NEBIUS_API_KEY", "NGC_API_KEY"];

    private readonly List<(string Variable, GitResult Result)> _scans = [];
    private TempWorkspace? _workspace;
    private string? _gateNote;
    private string _syntheticValue = string.Empty;
    private string _trackedName = string.Empty;

    // The scan: file names of the tracked files that contain the value as a fixed string; exit code 1 means none.
    private static GitResult Scan(string workingDirectory, string value) =>
        GitRunner.RunWithInput(workingDirectory, value + "\n", "grep", "--name-only", "-F", "-f", "-");

    [Given(@"^NVIDIA_API_KEY, NEBIUS_API_KEY and NGC_API_KEY are set$")]
    public void GivenTheKeysAreSet()
    {
        _gateNote = IntegrationGate.SkipReason(Environment.GetEnvironmentVariable(IntegrationGate.VariableName));
        if (_gateNote is null)
        {
            foreach (string variable in KeyVariables)
            {
                Assert.False(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable)), variable + " is not set");
            }
        }
    }

    [When(@"^""git grep -nF"" runs for each value$")]
    public void WhenGitGrepRunsForEachValue()
    {
        if (_gateNote is not null)
        {
            return;
        }

        foreach (string variable in KeyVariables)
        {
            _scans.Add((variable, Scan(RepositoryRoot.Path, Environment.GetEnvironmentVariable(variable)!.Trim())));
        }
    }

    [Then(@"^each returns exit code 1 and prints no line$")]
    public void ThenEachReturnsExitCodeOne()
    {
        if (_gateNote is not null)
        {
            _output.WriteLine("passed without asserting: " + _gateNote);
            return;
        }

        foreach ((string variable, GitResult result) in _scans)
        {
            Assert.True(result.ExitCode == 1 && result.Stdout.Length == 0, variable + ": git grep exited " + result.ExitCode.ToString(CultureInfo.InvariantCulture) + " and printed " + result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length.ToString(CultureInfo.InvariantCulture) + " file name(s)");
        }
    }

    [Given(@"^a temporary tracked file containing the value of NGC_API_KEY$")]
    public void GivenATemporaryTrackedFile()
    {
        Assert.True(GitRunner.Available(), "git is not on PATH");
        _workspace = TempWorkspace.CreateEmpty();
        _syntheticValue = string.Concat("ngc", "-", Guid.NewGuid().ToString("N"), "key");
        _trackedName = "tracked-note.txt";
        _ = _workspace.WriteFile(_trackedName, "token=" + _syntheticValue + "\n");
        Assert.Equal(0, GitRunner.Run(_workspace.Root, "init", "--quiet", _workspace.Root).ExitCode);
        Assert.Equal(0, GitRunner.Run(_workspace.Root, "add", _trackedName).ExitCode);
    }

    [When(@"^""git grep -nF"" runs for that value$")]
    public void WhenGitGrepRunsForThatValue() =>
        _scans.Add(("scratch", Scan(_workspace?.Root ?? throw new InvalidOperationException("The scratch repository was not created."), _syntheticValue)));

    [Then(@"^it prints the file name without the value and the scan fails$")]
    public void ThenItPrintsTheFileName()
    {
        GitResult result = Assert.Single(_scans).Result;

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(_trackedName, result.Stdout.Trim());
        Assert.DoesNotContain(_syntheticValue, result.Stdout, StringComparison.Ordinal);
    }

    public void Dispose() => _workspace?.Dispose();
}