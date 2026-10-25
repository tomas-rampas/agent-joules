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
    private const string CalibrationFilter = "calibration-*.json";

    private readonly string _root;

    /// <summary>Creates a reader of the repository that this test run belongs to.</summary>
    public CommittedEvidenceReader()
        : this(ConfigurationFixtures.RepositoryRoot())
    {
    }

    /// <summary>Creates a reader of the tree at <paramref name="root"/>.</summary>
    /// <param name="root">The folder that holds <c>results/</c>, <c>docs/</c> and <c>README.md</c>.</param>
    /// <exception cref="ArgumentException"><paramref name="root"/> is null, empty or white space.</exception>
    public CommittedEvidenceReader(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        _root = Path.GetFullPath(root);
    }

    /// <summary>The <c>results/</c> folder of the tree; it may not exist.</summary>
    public string ResultsDirectory => Path.Combine(_root, ResultsFolder);

    /// <summary>The newest accepted local run file, chosen by <see cref="RunFileSelector"/>: status completed or partial, no <c>non-publishable</c> flag.</summary>
    /// <returns>The full path, or null when <c>results/</c> is absent or holds no such file.</returns>
    /// <exception cref="InvalidDataException">A local run file cannot be used (the message is <c>&lt;file&gt;: &lt;reason&gt;</c>).</exception>
    public string? LatestLocalRunFile() =>
        Directory.Exists(ResultsDirectory) ? RunFileSelector.Latest(ResultsDirectory, Tier.Local) : null;

    /// <summary>The committed calibration files, <c>results/calibration-*.json</c>.</summary>
    /// <returns>The full paths in ordinal order; empty when there is none.</returns>
    public IReadOnlyList<string> CalibrationFiles() =>
        Directory.Exists(ResultsDirectory)
            ? [.. Directory.EnumerateFiles(ResultsDirectory, CalibrationFilter, SearchOption.TopDirectoryOnly)
                .Where(static path => CalibrationFile.IsValid(Path.GetFileName(path)))
                .Order(StringComparer.Ordinal)]
            : [];

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