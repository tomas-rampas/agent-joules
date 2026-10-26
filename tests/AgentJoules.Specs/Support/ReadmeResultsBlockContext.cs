using System.Text;

using AgentJoules.Cli;
using AgentJoules.Core.Runs;
using AgentJoules.TestSupport;

using Xunit.Sdk;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The state of one AJ-133 scenario (generate and verify the README results block, REQ-025), handed to the step class by Reqnroll context injection. The Given steps lay out a <see cref="TempWorkspace"/> that is
/// the working directory of the run: fixture run files in <c>results</c> and a README with the two marker lines; the When steps run the real command line through the harness; the Then steps read the exit codes,
/// the streams and the files. There is no HTTP, no model and no GPU, and the repository is never written. The workspace paths are the defaults of the command (<c>results</c>, <c>docs/report</c>), which resolve
/// against the working directory.
/// </summary>
public sealed class ReadmeResultsBlockContext : IDisposable
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly List<CliRunResult> _results = [];

    private TempWorkspace? _workspace;

    /// <summary>The README bytes saved by <see cref="SnapshotReadme"/>, or null.</summary>
    public byte[]? Snapshot { get; private set; }

    private TempWorkspace Workspace => _workspace ??= TempWorkspace.CreateEmpty();

    /// <summary>Writes the fixture run files of local, nvidia and nebius.</summary>
    public void AddThreeTiers() => ReadmeFixtures.AddCommitted(Workspace, Tier.Local, Tier.Nvidia, Tier.Nebius);

    /// <summary>Writes the fixture run files of the given tiers.</summary>
    /// <param name="tiers">The local, nvidia or nebius tiers.</param>
    public void AddTiers(params Tier[] tiers) => ReadmeFixtures.AddCommitted(Workspace, tiers);

    /// <summary>Writes the cloudgpu fixture run file.</summary>
    /// <param name="partial">True for a partial file with <c>stopReason</c> <c>cost-cap</c>.</param>
    public void AddCloudGpu(bool partial) => ReadmeFixtures.AddCloudGpu(Workspace, partial);

    /// <summary>Writes a results file.</summary>
    /// <param name="name">The file name.</param>
    /// <param name="text">The text.</param>
    public void WriteResult(string name, string text) => _ = Workspace.WriteFile(Path.Combine("results", name), text);

    /// <summary>Writes the README with the two marker lines around a stale block.</summary>
    public void WriteReadme() => WriteReadme(ReportFlagsContext.ReadmeTemplate);

    /// <summary>Writes the README.</summary>
    /// <param name="text">The text of the file.</param>
    public void WriteReadme(string text) => _ = Workspace.WriteFile("README.md", text);

    /// <summary>The README as it is on disk.</summary>
    /// <returns>The bytes.</returns>
    public byte[] ReadmeBytes() => File.ReadAllBytes(Path.Combine(Workspace.Root, "README.md"));

    /// <summary>The README as it is on disk.</summary>
    /// <returns>The text.</returns>
    public string ReadmeText() => Utf8WithoutBom.GetString(ReadmeBytes());

    /// <summary>The text between the markers of the README on disk.</summary>
    /// <returns>The block text.</returns>
    public string Block() => ReadmeFixtures.BlockOf(ReadmeText());

    /// <summary>Whether a path of the workspace exists.</summary>
    /// <param name="relative">The path relative to the workspace.</param>
    /// <returns>True when the file or folder exists.</returns>
    public bool Exists(string relative) => File.Exists(Path.Combine(Workspace.Root, relative));

    /// <summary>The text of a file of the workspace.</summary>
    /// <param name="relative">The path relative to the workspace.</param>
    /// <returns>The text.</returns>
    public string ReadFile(string relative) => File.ReadAllText(Path.Combine(Workspace.Root, relative), Utf8WithoutBom);

    /// <summary>Remembers the README bytes now.</summary>
    public void SnapshotReadme() => Snapshot = ReadmeBytes();

    /// <summary>Replaces the first digit of the net J cell of the first data row of the results table with another digit, the edit of a hand: a figure of the block, not a digit of a file name.</summary>
    /// <exception cref="XunitException">The block has no table with a net J column and a data row.</exception>
    public void EditBlockByHand()
    {
        string[] lines = ReadmeText().Split('\n');
        int header = Array.FindIndex(lines, static line => line.StartsWith("| tier |", StringComparison.Ordinal));
        if (header < 0 || header + 2 >= lines.Length)
        {
            throw new XunitException("The results block has no table with a data row.");
        }

        string[] headerCells = lines[header].Split('|');
        int column = Array.FindIndex(headerCells, static cell => cell.Trim().StartsWith("net J", StringComparison.Ordinal));
        string[] cells = lines[header + 2].Split('|');
        int digit = column < 0 || column >= cells.Length ? -1 : cells[column].AsSpan().IndexOfAnyInRange('0', '9');
        if (digit < 0)
        {
            throw new XunitException("The first data row has no digit in the net J cell.");
        }

        string cell = cells[column];
        cells[column] = string.Concat(cell.AsSpan(0, digit), cell[digit] == '7' ? "3" : "7", cell.AsSpan(digit + 1));
        lines[header + 2] = string.Join('|', cells);
        WriteReadme(string.Join('\n', lines));
    }

    /// <summary>Runs the real command line in the workspace; <paramref name="arguments"/> are split on spaces.</summary>
    /// <param name="arguments">The arguments after the program name, for example <c>report --update-readme</c>.</param>
    /// <returns>A task that completes with the result, which is also kept for <see cref="Last"/>.</returns>
    public async Task<CliRunResult> RunAsync(string arguments)
    {
        CliRunResult result = await CliHarness.RunTokensAsync(arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries), new CliHostOverrides(WorkingDirectory: Workspace.Root));
        _results.Add(result);

        return result;
    }

    /// <summary>The runs of the scenario in order.</summary>
    public IReadOnlyList<CliRunResult> Runs => _results;

    /// <summary>The last run.</summary>
    /// <returns>The result.</returns>
    /// <exception cref="XunitException">No command has run.</exception>
    public CliRunResult Last() => _results.Count > 0 ? _results[^1] : throw new XunitException("The command has not run yet.");

    /// <inheritdoc />
    public void Dispose() => _workspace?.Dispose();
}