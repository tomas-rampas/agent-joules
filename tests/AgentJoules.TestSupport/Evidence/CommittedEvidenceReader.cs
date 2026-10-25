using AgentJoules.Core.Runs;
using AgentJoules.Reporting.Endpoints;
using AgentJoules.Reporting.Results;
using AgentJoules.TestSupport.Docs;

namespace AgentJoules.TestSupport.Evidence;

/// <summary>
/// Reads the committed evidence of the laptop runs (AJ-115, REQ-020, REQ-021, EDGE-010) and judges nothing: the latest local run file, the calibration files, <c>docs/endpoints.md</c>, the Decision of
/// <c>docs/nim-local-attempt.md</c> and <c>README.md</c>. <see cref="CommittedEvidenceRules"/> turns what it returns into violations. The reader works on a tree that holds <c>results/</c>, <c>docs/</c> and
/// <c>README.md</c>: the repository for the facts of <c>CommittedEvidenceTests</c>, a temporary workspace for the scenarios.
/// </summary>
public sealed class CommittedEvidenceReader
{
    private const string ResultsFolder = "results";
    private const string DocsFolder = "docs";

    private readonly string _root;
    private readonly HashSet<string>? _committed;

    /// <summary>
    /// Creates a reader of the repository that this test run belongs to. Its <c>results/</c> files are the names of <see cref="LaptopResults.CommittedResultsFiles"/> (the HEAD tree), so a developer run file or a staged
    /// file in the working tree is not a row; the content of a committed name is read from its working-tree path, which equals HEAD when the tree is clean.
    /// </summary>
    /// <exception cref="InvalidOperationException">git is not installed, or the call failed.</exception>
    public CommittedEvidenceReader()
        : this(ConfigurationFixtures.RepositoryRoot(), LaptopResults.CommittedResultsFiles())
    {
    }

    /// <summary>Creates a reader of the tree at <paramref name="root"/>; every file of its <c>results/</c> folder counts (a temporary workspace has no git history).</summary>
    /// <param name="root">The folder that holds <c>results/</c>, <c>docs/</c> and <c>README.md</c>.</param>
    /// <exception cref="ArgumentException"><paramref name="root"/> is null, empty or white space.</exception>
    public CommittedEvidenceReader(string root)
        : this(root, null)
    {
    }

    /// <summary>Creates a reader of the tree at <paramref name="root"/> whose <c>results/</c> files are the listed names.</summary>
    /// <param name="root">The folder that holds <c>results/</c>, <c>docs/</c> and <c>README.md</c>.</param>
    /// <param name="committedResultsFiles">The repository-relative names (<c>results/&lt;file&gt;</c>) that count, or null for every file of the folder.</param>
    /// <exception cref="ArgumentException"><paramref name="root"/> is null, empty or white space.</exception>
    public CommittedEvidenceReader(string root, IEnumerable<string>? committedResultsFiles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        _root = Path.GetFullPath(root);
        _committed = committedResultsFiles is null ? null : new HashSet<string>(committedResultsFiles, StringComparer.Ordinal);
    }

    /// <summary>The <c>results/</c> folder of the tree; it may not exist.</summary>
    public string ResultsDirectory => Path.Combine(_root, ResultsFolder);

    /// <summary>The newest accepted local run file, chosen by <see cref="RunFileSelector"/>: status completed or partial, no <c>non-publishable</c> flag.</summary>
    /// <returns>The full path, or null when <c>results/</c> is absent or holds no such file.</returns>
    /// <exception cref="InvalidDataException">A local run file cannot be used (the message is <c>&lt;file&gt;: &lt;reason&gt;</c>).</exception>
    public string? LatestLocalRunFile()
    {
        if (!Directory.Exists(ResultsDirectory))
        {
            return null;
        }

        if (_committed is null)
        {
            return RunFileSelector.Latest(ResultsDirectory, Tier.Local);
        }

        string[] candidates = [.. ResultsFiles().Where(static path => IsLocalRunName(Path.GetFileName(path)))];
        RunFileSelection selection = RunFileSelector.Select(candidates);

        return selection.Problems.Count > 0
            ? throw new InvalidDataException($"{selection.Problems[0].FileName}: {selection.Problems[0].Reason}")
            : selection.Latest.GetValueOrDefault(Tier.Local);
    }

    /// <summary>The committed calibration files, <c>results/calibration-*.json</c>.</summary>
    /// <returns>The full paths in ordinal order; empty when there is none.</returns>
    public IReadOnlyList<string> CalibrationFiles() =>
        [.. ResultsFiles().Where(static path => CalibrationFile.IsValid(Path.GetFileName(path)))];

    // The files directly in results/ that count: every file of the folder, or the committed names that exist in the working tree, in ordinal order.
    private IEnumerable<string> ResultsFiles()
    {
        if (!Directory.Exists(ResultsDirectory))
        {
            return [];
        }

        IEnumerable<string> files = _committed is null
            ? Directory.EnumerateFiles(ResultsDirectory, "*", SearchOption.TopDirectoryOnly)
            : _committed
                .Where(static name => name.StartsWith(ResultsFolder + "/", StringComparison.Ordinal) && name.IndexOf('/', ResultsFolder.Length + 1) < 0)
                .Select(name => Path.GetFullPath(Path.Combine(_root, name)))
                .Where(File.Exists);
        return files.Order(StringComparer.Ordinal);
    }

    // The local names of RunFileName (the full-span ECMAScript pattern of the run-file selector), with or without -baseline.
    private static bool IsLocalRunName(string name) =>
        RunFileName.IsValid(name) && (name.EndsWith("-local.json", StringComparison.Ordinal) || name.EndsWith("-local-baseline.json", StringComparison.Ordinal));

    /// <summary>The calibration files read as text.</summary>
    /// <returns>The file name and the text of each file of <see cref="CalibrationFiles"/>.</returns>
    public IReadOnlyList<(string Name, string Json)> CalibrationTexts() =>
        [.. CalibrationFiles().Select(static path => (Path.GetFileName(path), File.ReadAllText(path)))];

    /// <summary>Reads <c>docs/endpoints.md</c>.</summary>
    /// <returns>The tier sections of the document.</returns>
    /// <exception cref="FileNotFoundException">The document does not exist.</exception>
    public AgentJoules.Reporting.Endpoints.EndpointsDocument EndpointsDocument() =>
        EndpointsDocParser.Parse(File.ReadAllText(Path.Combine(_root, DocsFolder, "endpoints.md")));

    /// <summary>Reads <c>docs/nim-local-attempt.md</c>, whose Decision section records the local runtime.</summary>
    /// <returns>The reading of the document.</returns>
    /// <exception cref="FileNotFoundException">The document does not exist.</exception>
    public NimAttemptDocument NimDecision() =>
        NimAttemptDocumentParser.Parse(File.ReadAllText(Path.Combine(_root, DocsFolder, "nim-local-attempt.md")));

    /// <summary>Reads <c>README.md</c>.</summary>
    /// <returns>The text.</returns>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    public string ReadmeText() => File.ReadAllText(Path.Combine(_root, "README.md"));
}