using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;

using AgentJoules.Core.Configuration;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;

namespace AgentJoules.Reporting.Results;

/// <summary>The two kinds of run file a tier can have (AJ-081, REQ-016): the file of the run itself and the file of a run in which some agent model is not a Nemotron model.</summary>
public enum RunVariant
{
    // Numbered from 1 so that default(RunVariant) is never a variant.

    /// <summary>A file name without the <c>-baseline</c> suffix.</summary>
    Primary = 1,

    /// <summary>A file name with the <c>-baseline</c> suffix; its rows are never combined with, or compared to, the rows of the primary file.</summary>
    Baseline = 2,
}

/// <summary>A run file that cannot be used and must not be silently dropped: the report ends with exit 2 and names it (AJ-081, P-2).</summary>
/// <param name="FileName">The file name, without a directory; for an unreadable folder, the bounded name of the folder.</param>
/// <param name="Reason">One token: a reason of <see cref="RunHeaderReader"/>, <c>tier-mismatch</c> (the header names another tier than the file name), <c>duplicate-file-name</c> (two inputs hold a file of that name) or <c>unreadable</c> (the file or folder cannot be opened).</param>
public sealed record RunFileProblem(string FileName, string Reason);

/// <summary>A run file that is readable but is not used, with the reason the report lists (AJ-081, P-3).</summary>
/// <param name="FileName">The file name, without a directory.</param>
/// <param name="Reason">One token: <c>status-running</c>, <c>status-failed</c>, <c>status-incomplete</c>, <c>non-publishable</c> or <c>partial-without-ok-step</c> (a partial file with no step whose outcome is ok).</param>
public sealed record RunFileSkip(string FileName, string Reason);

/// <summary>What <see cref="RunFileSelector.Select"/> made of its inputs.</summary>
/// <param name="Latest">The full path of the newest accepted primary file of each tier that has one; a tier without a selectable file is absent (the report draws <c>not run</c> from that absence, never from a zero).</param>
/// <param name="LatestBaseline">The full path of the newest accepted <c>-baseline</c> file of each tier that has one.</param>
/// <param name="Skipped">The readable files that are newer than the selected file of their tier and variant (or all of them when none was selected) and were not accepted, ordinal by file name.</param>
/// <param name="Problems">The files that cannot be used, ordinal by file name; any problem ends a report with exit 2.</param>
public sealed record RunFileSelection(
    IReadOnlyDictionary<Tier, string> Latest,
    IReadOnlyDictionary<Tier, string> LatestBaseline,
    IReadOnlyList<RunFileSkip> Skipped,
    IReadOnlyList<RunFileProblem> Problems);

/// <summary>
/// Chooses the run files a report may use (AJ-081, REQ-016). Per tier and per variant the newest file by name (the name starts with the UTC time of the run, so the order is the order of the runs) whose
/// header is accepted wins: the status is <c>completed</c> or <c>partial</c> and the flags lack <c>non-publishable</c>. A file that is not accepted is listed with its reason, never used for a number. Only names
/// that satisfy <see cref="RunFileName.IsValid"/> are candidates, so a <c>probe-*.json</c>, a <c>calibration-*.json</c>, a <c>*.partial</c> and everything else is filtered by its name and never opened. Only the
/// <c>run</c> header of a candidate is read (<see cref="RunHeaderReader"/>), and candidates older than the selected file are not opened at all. A header that cannot be read, or that names another tier than the
/// file name, is a problem and never a tier that was not run. The selector produces no number and no label.
/// </summary>
public static class RunFileSelector
{
    private const string BaselineSuffix = "-baseline";
    private const string Extension = ".json";
    private const string FolderLabel = "(folder)";

    private static readonly Tier[] Tiers = [Tier.Local, Tier.CloudGpu, Tier.Nvidia, Tier.Nebius];

    /// <summary>Selects from files and folders together.</summary>
    /// <param name="inputs">Fully qualified paths that exist (the command resolves its own relative paths first): a folder contributes the run files directly in it (no subfolder), a file contributes itself and its name must satisfy <see cref="RunFileName.IsValid"/>. A file reached twice counts once.</param>
    /// <returns>The selection; never null.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="inputs"/> is null.</exception>
    /// <exception cref="ArgumentException">An input is blank, is not fully qualified (a relative path or a drive-relative one such as <c>C:x</c> would depend on the process directory; the message echoes it, bounded), or is neither a folder nor a file whose name satisfies <see cref="RunFileName.IsValid"/> (the message echoes the name, bounded).</exception>
    public static RunFileSelection Select(IReadOnlyList<string> inputs) => SelectCore(inputs, only: null);

    /// <summary>The newest accepted primary file of a tier in a folder.</summary>
    /// <param name="directory">The folder.</param>
    /// <param name="tier">The tier.</param>
    /// <returns>The full path of the file, or null when the tier has no selectable file.</returns>
    /// <exception cref="ArgumentException"><paramref name="directory"/> is blank or is not fully qualified.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tier"/> is not a defined tier.</exception>
    /// <exception cref="DirectoryNotFoundException">The folder does not exist.</exception>
    /// <exception cref="InvalidDataException">A file of that tier cannot be used; the message is <c>&lt;file&gt;: &lt;reason&gt;</c>.</exception>
    public static string? Latest(string directory, Tier tier) => Pick(directory, tier, RunVariant.Primary);

    /// <summary>The newest accepted <c>-baseline</c> file of a tier in a folder; the same status and <c>non-publishable</c> rule as <see cref="Latest"/>.</summary>
    /// <param name="directory">The folder.</param>
    /// <param name="tier">The tier.</param>
    /// <returns>The full path of the file, or null when the tier has no selectable baseline file.</returns>
    /// <exception cref="ArgumentException"><paramref name="directory"/> is blank or is not fully qualified.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tier"/> is not a defined tier.</exception>
    /// <exception cref="DirectoryNotFoundException">The folder does not exist.</exception>
    /// <exception cref="InvalidDataException">A file of that tier cannot be used; the message is <c>&lt;file&gt;: &lt;reason&gt;</c>.</exception>
    public static string? LatestBaseline(string directory, Tier tier) => Pick(directory, tier, RunVariant.Baseline);

    private static string? Pick(string directory, Tier tier, RunVariant variant)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        RequireFullyQualified(directory, nameof(directory));
        _ = TierOptions.NameOf(tier);

        string full = Path.GetFullPath(directory);
        if (!Directory.Exists(full))
        {
            throw new DirectoryNotFoundException($"The folder \"{TextEcho.ForMessage(Path.GetFileName(Path.TrimEndingDirectorySeparator(full)))}\" does not exist.");
        }

        RunFileSelection selection = SelectCore([full], tier);
        if (selection.Problems.Count > 0)
        {
            RunFileProblem problem = selection.Problems[0];
            throw new InvalidDataException($"{problem.FileName}: {problem.Reason}");
        }

        IReadOnlyDictionary<Tier, string> chosen = variant == RunVariant.Primary ? selection.Latest : selection.LatestBaseline;

        return chosen.TryGetValue(tier, out string? path) ? path : null;
    }

    private static RunFileSelection SelectCore(IReadOnlyList<string> inputs, Tier? only)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        List<RunFileProblem> problems = [];
        List<RunFileSkip> skipped = [];
        Dictionary<Tier, string> latest = [];
        Dictionary<Tier, string> latestBaseline = [];

        // The same name from two different paths is ambiguous: neither is a candidate and the name is a problem.
        List<Candidate> usable = [];
        foreach (IGrouping<string, Candidate> sameName in Candidates(inputs, only, problems).GroupBy(static candidate => candidate.Name, StringComparer.Ordinal))
        {
            if (sameName.Skip(1).Any())
            {
                problems.Add(new RunFileProblem(sameName.Key, "duplicate-file-name"));
            }
            else
            {
                usable.Add(sameName.First());
            }
        }

        // One buffer serves every header of the call; it is never shared with another thread.
        byte[] buffer = RunHeaderReader.RentBuffer();
        try
        {
            foreach (IGrouping<(Tier Tier, RunVariant Variant), Candidate> group in usable.GroupBy(static candidate => (candidate.Tier, candidate.Variant)))
            {
                string? chosen = Choose(group.OrderByDescending(static candidate => candidate.Name, StringComparer.Ordinal), buffer, skipped, problems);
                if (chosen is not null)
                {
                    (group.Key.Variant == RunVariant.Primary ? latest : latestBaseline)[group.Key.Tier] = chosen;
                }
            }
        }
        finally
        {
            RunHeaderReader.ReturnBuffer(buffer);
        }

        return new RunFileSelection(
            new ReadOnlyDictionary<Tier, string>(latest),
            new ReadOnlyDictionary<Tier, string>(latestBaseline),
            skipped.OrderBy(static skip => skip.FileName, StringComparer.Ordinal).ThenBy(static skip => skip.Reason, StringComparer.Ordinal).ToList().AsReadOnly(),
            problems.OrderBy(static problem => problem.FileName, StringComparer.Ordinal).ThenBy(static problem => problem.Reason, StringComparer.Ordinal).ToList().AsReadOnly());
    }

    // The newest candidate whose header is accepted; the candidates newer than it that are not are listed. Older candidates are never opened.
    private static string? Choose(IEnumerable<Candidate> newestFirst, byte[] buffer, List<RunFileSkip> skipped, List<RunFileProblem> problems)
    {
        foreach (Candidate candidate in newestFirst)
        {
            RunHeader header;
            try
            {
                header = RunHeaderReader.Read(candidate.Path, buffer);
            }
            catch (InvalidDataException ex)
            {
                problems.Add(new RunFileProblem(candidate.Name, ex.Message));
                continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add(new RunFileProblem(candidate.Name, "unreadable"));
                continue;
            }

            if (header.Tier != candidate.Tier)
            {
                problems.Add(new RunFileProblem(candidate.Name, "tier-mismatch"));
                continue;
            }

            string? reason = SkipReason(header);
            if (reason is null && header.Status == RunStatus.Partial)
            {
                try
                {
                    reason = HasOkStep(candidate.Path) ? null : "partial-without-ok-step";
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    problems.Add(new RunFileProblem(candidate.Name, "unreadable"));
                    continue;
                }
            }

            if (reason is null)
            {
                return candidate.Path;
            }

            skipped.Add(new RunFileSkip(candidate.Name, reason));
        }

        return null;
    }

    // Every status is named: a sixth status must be given its rule here, not accepted by default.
    private static string? SkipReason(RunHeader header) =>
        header.Status switch
        {
            RunStatus.Running => "status-running",
            RunStatus.Failed => "status-failed",
            RunStatus.Incomplete => "status-incomplete",
            RunStatus.Completed or RunStatus.Partial => header.Flags.Contains(RunFlagNames.NonPublishable, StringComparer.Ordinal) ? "non-publishable" : null,
            _ => throw new UnreachableException(),
        };

    // A partial file that holds no step with the outcome ok (the box expired before the first step finished) holds no measurement: it is skipped so that an older valid file is not hidden by it (peer review m-2).
    // Only a partial file is opened beyond its header. A file that cannot be parsed, an oversized file (more than RunFileValidator.MaxBytes), a non-object root and a file without a steps array
    // are not skipped here: the full read of the selected file names the defect (peer review m-2).
    private static bool HasOkStep(string path)
    {
        if (new FileInfo(path).Length > RunFileValidator.MaxBytes)
        {
            return true;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, FileOptions.SequentialScan);
        try
        {
            using JsonDocument document = JsonDocument.Parse(stream);

            return document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("steps", out JsonElement steps)
                || steps.ValueKind != JsonValueKind.Array
                || steps.EnumerateArray().Any(static step => step.ValueKind == JsonValueKind.Object
                    && step.TryGetProperty("outcome", out JsonElement outcome)
                    && outcome.ValueKind == JsonValueKind.String
                    && outcome.ValueEquals("ok"));
        }
        catch (JsonException)
        {
            return true;
        }
    }

    // A relative input would be resolved against the process directory, which a command must never rely on (it resolves against its own working directory first).
    private static void RequireFullyQualified(string path, string parameter)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException($"\"{TextEcho.ForMessage(path)}\" is not a fully qualified path.", parameter);
        }
    }

    // Every candidate of the inputs, a file reached twice once; an unreadable folder is added to `problems`.
    private static List<Candidate> Candidates(IReadOnlyList<string> inputs, Tier? only, List<RunFileProblem> problems)
    {
        StringComparer paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        HashSet<string> seen = new(paths);
        List<Candidate> candidates = [];

        foreach (string input in inputs)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(input);
            RequireFullyQualified(input, nameof(inputs));

            string full = Path.GetFullPath(input);
            if (Directory.Exists(full))
            {
                foreach (string file in FilesOf(full, problems))
                {
                    Add(Path.GetFullPath(file));
                }
            }
            else if (RunFileName.IsValid(Path.GetFileName(full)))
            {
                Add(full);
            }
            else
            {
                throw new ArgumentException($"\"{TextEcho.ForMessage(Path.GetFileName(full))}\" is neither a folder nor a run file name.", nameof(inputs));
            }
        }

        return candidates;

        void Add(string path)
        {
            if (!seen.Add(path))
            {
                return;
            }

            Candidate candidate = Candidate.Of(path);
            if (only is null || candidate.Tier == only)
            {
                candidates.Add(candidate);
            }
        }
    }

    private static List<string> FilesOf(string directory, List<RunFileProblem> problems)
    {
        try
        {
            return [.. Directory.EnumerateFiles(directory, "*" + Extension, SearchOption.TopDirectoryOnly).Where(static file => RunFileName.IsValid(Path.GetFileName(file)))];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));
            problems.Add(new RunFileProblem(name.Length == 0 ? FolderLabel : TextEcho.ForMessage(name), "unreadable"));

            return [];
        }
    }

    private sealed record Candidate(string Path, string Name, Tier Tier, RunVariant Variant)
    {
        // The name satisfies RunFileName.IsValid: <timestamp>-<tier>[-baseline].json.
        public static Candidate Of(string path)
        {
            string name = System.IO.Path.GetFileName(path);
            string stem = name[..^Extension.Length];
            bool baseline = stem.EndsWith(BaselineSuffix, StringComparison.Ordinal);
            string rest = stem[(stem.IndexOf('-', StringComparison.Ordinal) + 1)..];
            string tierName = baseline ? rest[..^BaselineSuffix.Length] : rest;
            Tier tier = Tiers.First(candidate => TierOptions.NameOf(candidate) == tierName);

            return new Candidate(path, name, tier, baseline ? RunVariant.Baseline : RunVariant.Primary);
        }
    }
}