using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

using AgentJoules.Cli;
using AgentJoules.Core.Runs;
using AgentJoules.TestSupport;

using Xunit.Sdk;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The state of one AJ-086 scenario (the <c>report</c> command, REQ-016 and REQ-030), handed to the step class by Reqnroll context injection. The Given steps name the input (the committed fixture folder, which is read in
/// place and never copied, an empty folder, a folder with one corrupted run file, or a path that is not there) inside a <see cref="TempWorkspace"/> that is the working directory of the run; the When steps run the real
/// command line through the harness; the Then steps read the exit code, the two streams and the files in the output folder. There is no HTTP, no model and no GPU: <c>report</c> never builds a host. The repository is
/// never written: the output folder lives in the workspace.
/// </summary>
public sealed class ReportCommandContext : IDisposable
{
    /// <summary>The names of the three files the command writes, in the order it writes them.</summary>
    public static readonly IReadOnlyList<string> OutputFiles = ["summary.md", "joules-per-step.svg", "index.html"];

    private const string InputFolder = "runs";
    private const string OutputFolder = "out";

    private readonly List<string> _outputs = [];

    private TempWorkspace? _workspace;

    /// <summary>The name of the run file the scenario corrupted, or null.</summary>
    public string? FileName { get; private set; }

    /// <summary>The input path the run passes as <c>--in</c>.</summary>
    public string? Input { get; private set; }

    /// <summary>The result of the last run, or null before it ran.</summary>
    public CliRunResult? Result { get; private set; }

    /// <summary>The output folders of the runs so far, in order.</summary>
    public IReadOnlyList<string> Outputs => _outputs;

    /// <summary>The workspace root, the working directory of the run.</summary>
    public string Root => Workspace.Root;

    private TempWorkspace Workspace => _workspace ??= TempWorkspace.CreateEmpty();

    /// <summary>Names the committed fixture folder of the repository as the input; nothing is copied.</summary>
    public void UseFixtureFolder() => Input = Path.Combine(ConfigurationFixtures.RepositoryRoot(), RunDocumentFixtures.CommittedFolder);

    /// <summary>Creates an empty folder in the workspace and names it as the input.</summary>
    public void UseEmptyFolder()
    {
        Input = Path.Combine(Workspace.Root, InputFolder);
        _ = Directory.CreateDirectory(Input);
    }

    /// <summary>Names a path that is not there as the input.</summary>
    public void UseMissingPath() => Input = Path.Combine(Workspace.Root, "no-such-folder");

    /// <summary>Writes one run file whose first step has a cost with the given label and value, and names its folder as the input.</summary>
    /// <param name="label">The label to put in <c>cost.label</c>.</param>
    /// <param name="value">The number to put in <c>cost.value</c>.</param>
    public void WriteRunFileWithCost(string label, double value)
    {
        string text = RunDocumentFixtures.Edit(
            RunDocumentFixtures.CommittedText(Tier.Local),
            root =>
            {
                JsonObject cost = root["steps"]![0]!["cost"]!.AsObject();
                cost["label"] = label;
                cost["value"] = value;
            });
        FileName = RunDocumentFixtures.CommittedFileName(Tier.Local);
        _ = Workspace.WriteFile(Path.Combine(InputFolder, FileName), text);
        Input = Path.Combine(Workspace.Root, InputFolder);
    }

    /// <summary>Runs <c>report --in &lt;input&gt; --out &lt;a new folder of the workspace&gt;</c>; each call uses a folder of its own, so two runs can be compared.</summary>
    /// <returns>A task that completes when <see cref="Result"/> is set.</returns>
    public async Task RunAsync()
    {
        string output = Path.Combine(Workspace.Root, OutputFolder + (_outputs.Count + 1).ToString(CultureInfo.InvariantCulture));
        _outputs.Add(output);
        Result = await CliHarness.RunTokensAsync(
            ["report", "--in", Input ?? throw new XunitException("No input was named."), "--out", output],
            new CliHostOverrides(WorkingDirectory: Workspace.Root));
    }

    /// <summary>The SHA-256 sum of each of the three output files of a run, in file order.</summary>
    /// <param name="runIndex">The run, from 0.</param>
    /// <returns>The sums as hexadecimal text.</returns>
    public string[] HashesOf(int runIndex) =>
        [.. OutputFiles.Select(name => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(_outputs[runIndex], name)))))];

    /// <summary>The result of the last run.</summary>
    /// <exception cref="XunitException">The command has not run.</exception>
    public CliRunResult Required() => Result ?? throw new XunitException("The command has not run yet.");

    /// <inheritdoc />
    public void Dispose() => _workspace?.Dispose();
}