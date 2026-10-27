using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentJoules.Specs.Support;

/// <summary>The rules of AJ-157 over the release notes, the three zip assets, the git index and the release command of the checklist.</summary>
public static partial class EvidenceChecks
{
    /// <summary>The release notes file.</summary>
    public const string ReleaseNotesPath = "docs/release-notes-v0.1.0.md";

    /// <summary>The sentence the release notes must state.</summary>
    public const string ScopeSentence = "Energy is GPU-board energy only.";

    /// <summary>The three asset paths, in the order of the release command.</summary>
    public static IReadOnlyList<string> ReleaseAssets { get; } =
        ["artifacts/agent-joules-v0.1.0-fdd.zip", "artifacts/agent-joules-v0.1.0-results.zip", "artifacts/agent-joules-v0.1.0-report.zip"];

    /// <summary>The headings of the release notes, in order.</summary>
    public static IReadOnlyList<string> ReleaseNotesHeadings { get; } =
        ["Features", "Fixes", "Breaking changes", "Migration", "Known limitations", "Honesty rules", "Assets", "Verification"];

    private static readonly string[] ReportZipEntries = ["index.html", "summary.md", "joules-per-step.svg"];

    private const string NoneFirstRelease = "None (first release)";
    private const int GitTimeoutSeconds = 30;

    /// <summary>The notes rule: the eight headings, the scope sentence, the first-release statements, a source or a label for every number, the asset names, the verification command and no cloudgpu claim without a run file.</summary>
    /// <param name="root">The repository-shaped folder.</param>
    /// <returns>The violations.</returns>
    public static IReadOnlyList<string> ReleaseNotesViolations(string root)
    {
        string? notes = ReadText(root, ReleaseNotesPath);
        if (notes is null)
        {
            return ["missing " + ReleaseNotesPath];
        }

        var violations = new List<string>();
        IReadOnlyList<string> headings = HeadingsOf(notes);
        violations.AddRange(ReleaseNotesHeadings.Where(heading => !headings.Contains(heading, StringComparer.Ordinal)).Select(static heading => "missing heading: " + heading));
        if (!notes.Contains(ScopeSentence, StringComparison.Ordinal))
        {
            violations.Add("missing sentence: " + ScopeSentence);
        }

        foreach (string heading in new[] { "Breaking changes", "Migration" })
        {
            if (SectionOf(notes, heading) is { } body && !body.Contains(NoneFirstRelease, StringComparison.Ordinal))
            {
                violations.Add(heading + " must read " + NoneFirstRelease);
            }
        }

        string assets = SectionOf(notes, "Assets") ?? string.Empty;
        violations.AddRange(ReleaseAssets.Select(static asset => Path.GetFileName(asset)).Where(name => !assets.Contains(name, StringComparison.Ordinal)).Select(static name => "Assets does not name: " + name));
        if (SectionOf(notes, "Verification") is { } verification && !verification.Contains("gh release view v0.1.0", StringComparison.Ordinal))
        {
            violations.Add("Verification lacks: gh release view v0.1.0");
        }

        violations.AddRange(NumberSourceViolations(notes));
        if (!HasCloudGpuRunFile(root) && DocumentChecks.Lines(notes).Any(static line => line.Contains("cloudgpu", StringComparison.OrdinalIgnoreCase) && !line.Contains("not run", StringComparison.OrdinalIgnoreCase)))
        {
            violations.Add("cloudgpu claim without run file");
        }

        return violations;
    }

    /// <summary>The number rule: every number with a unit sits in a sentence (or table row) that names a results file or <c>docs/cloudgpu-run.md</c> or carries a label.</summary>
    /// <param name="notes">The notes text.</param>
    /// <returns>The violations.</returns>
    public static IReadOnlyList<string> NumberSourceViolations(string notes)
    {
        ArgumentNullException.ThrowIfNull(notes);

        var violations = new List<string>();
        bool fenced = false;
        foreach (string line in DocumentChecks.Lines(notes))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                fenced = !fenced;
                continue;
            }

            if (fenced || line.StartsWith('#'))
            {
                continue;
            }

            foreach (string sentence in SentenceBreak().Split(line))
            {
                bool sourced = ResultsFileName().IsMatch(sentence) || sentence.Contains("docs/cloudgpu-run.md", StringComparison.Ordinal);
                bool labelled = NoteLabel().IsMatch(sentence);
                if (!sourced && !labelled)
                {
                    violations.AddRange(NoteQuantity().Matches(sentence).Cast<Match>().Select(static quantity => "number without source: " + quantity.Value));
                }
            }
        }

        return violations;
    }

    /// <summary>The zip rule: the three assets exist with the expected entries, and no entry is a raw file, a private key name or holds key material.</summary>
    /// <param name="root">The repository-shaped folder.</param>
    /// <returns>The violations.</returns>
    public static IReadOnlyList<string> ReleaseZipViolations(string root)
    {
        var violations = new List<string>();
        foreach (string asset in ReleaseAssets)
        {
            string path = Path.Combine(root, asset.Replace('/', Path.DirectorySeparatorChar));
            string zipName = Path.GetFileName(asset);
            if (!File.Exists(path))
            {
                violations.Add("missing asset: " + asset);
                continue;
            }

            try
            {
                using ZipArchive archive = ZipFile.OpenRead(path);
                ZipArchiveEntry[] files = [.. archive.Entries.Where(static entry => entry.Name.Length > 0)];
                violations.AddRange(files.Where(static entry => ForbiddenEntry().IsMatch(entry.Name)).Select(entry => $"forbidden entry in {zipName}: {entry.FullName}"));
                if (files.Any(static entry => HoldsKeyMaterial(entry)))
                {
                    violations.Add("key material in " + zipName);
                }

                violations.AddRange(ContentViolations(root, asset, zipName, files));
            }
            catch (InvalidDataException)
            {
                violations.Add("not a zip archive: " + zipName);
            }
        }

        return violations;
    }

    /// <summary>Whether at least one of the three zip assets exists below the folder (the <c>artifacts/</c> folder is git-ignored, so a clean checkout holds none).</summary>
    /// <param name="root">The repository-shaped folder.</param>
    /// <returns>True when any asset file exists.</returns>
    public static bool AnyReleaseAssetExists(string root) =>
        ReleaseAssets.Any(asset => File.Exists(Path.Combine(root, asset.Replace('/', Path.DirectorySeparatorChar))));

    /// <summary>
    /// The files below <c>artifacts/</c> that the git index of the folder tracks; empty when the folder is not a repository, and null when the folder is a repository (it holds <c>.git</c>) and git
    /// could not run, timed out or exited non-zero.
    /// </summary>
    /// <param name="root">The repository-shaped folder.</param>
    /// <returns>The tracked paths, or null when the listing failed.</returns>
    public static IReadOnlyList<string>? TrackedArtifacts(string root)
    {
        string dotGit = Path.Combine(root, ".git");
        IReadOnlyList<string>? failed = Directory.Exists(dotGit) || File.Exists(dotGit) ? null : [];

        var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in new[] { "-C", root, "ls-files", "--", "artifacts" })
        {
            start.ArgumentList.Add(argument);
        }

        foreach (string variable in new[] { "GIT_DIR", "GIT_INDEX_FILE", "GIT_WORK_TREE" })
        {
            _ = start.Environment.Remove(variable);
        }

        try
        {
            using Process? process = Process.Start(start);
            if (process is null)
            {
                return failed;
            }

            // The pipes are drained asynchronously so that the timed wait below is reachable when git hangs.
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromSeconds(GitTimeoutSeconds)))
            {
                process.Kill(entireProcessTree: true);
                return failed;
            }

            string output = outputTask.GetAwaiter().GetResult();
            _ = errorTask.GetAwaiter().GetResult();
            return process.ExitCode == 0 ? [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)] : failed;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return failed;
        }
    }

    /// <summary>The tracked-artifacts rule: <c>git ls-files artifacts</c> prints nothing, and git ran when the folder is a repository.</summary>
    /// <param name="root">The repository-shaped folder.</param>
    /// <returns>The violations.</returns>
    public static IReadOnlyList<string> TrackedArtifactViolations(string root) =>
        TrackedArtifacts(root) switch
        {
            null => ["artifacts check could not run git"],
            { Count: 0 } => [],
            _ => ["artifacts must not be tracked"],
        };

    /// <summary>The release-command rule: the <c>gh release create</c> block of the checklist is labelled as a user action, names the notes file and the three assets, and each of them exists.</summary>
    /// <param name="root">The repository-shaped folder.</param>
    /// <param name="requireAssets">False leaves out the existence check of the three zips (the name, notes-path and label checks stay), for a checkout where the git-ignored <c>artifacts/</c> folder is absent.</param>
    /// <returns>The violations.</returns>
    public static IReadOnlyList<string> ReleaseCommandViolations(string root, bool requireAssets = true)
    {
        string? checklist = ReadText(root, ChecklistPath);
        if (checklist is null)
        {
            return ["missing docs/devpost-checklist.md"];
        }

        string[] lines = DocumentChecks.Lines(checklist);
        int open = -1;
        int close = -1;
        for (int index = 0; index < lines.Length && close < 0; index++)
        {
            if (!lines[index].StartsWith("```", StringComparison.Ordinal))
            {
                continue;
            }

            if (open < 0)
            {
                open = index;
            }
            else if (lines.Skip(open + 1).Take(index - open - 1).Any(static line => line.Contains("gh release create", StringComparison.Ordinal)))
            {
                close = index;
            }
            else
            {
                open = -1;
            }
        }

        if (close < 0)
        {
            return ["release command not found: gh release create"];
        }

        var violations = new List<string>();
        string previous = lines.Take(open).LastOrDefault(static line => !string.IsNullOrWhiteSpace(line))?.Trim() ?? string.Empty;
        if (!string.Equals(previous, DocumentChecks.UserActionLabel, StringComparison.Ordinal))
        {
            violations.Add("release command not marked as user action");
        }

        string command = string.Join(' ', lines.Skip(open + 1).Take(close - open - 1)).Replace("\\ ", " ", StringComparison.Ordinal);
        string[] tokens = [.. ShellWords().Matches(command).Cast<Match>().Select(static match => match.Value.Trim('"'))];
        if (!command.Contains("gh release create v0.1.0", StringComparison.Ordinal))
        {
            violations.Add("release command does not create v0.1.0");
        }

        int flag = Array.IndexOf(tokens, "--notes-file");
        string notes = flag >= 0 && flag + 1 < tokens.Length ? tokens[flag + 1] : string.Empty;
        if (!string.Equals(notes, ReleaseNotesPath, StringComparison.Ordinal))
        {
            violations.Add("notes file must be " + ReleaseNotesPath);
        }
        else if (!File.Exists(Path.Combine(root, ReleaseNotesPath.Replace('/', Path.DirectorySeparatorChar))))
        {
            violations.Add("notes file not found: " + ReleaseNotesPath);
        }

        string[] named = [.. tokens.Where(static token => token.StartsWith("artifacts/", StringComparison.Ordinal))];
        violations.AddRange(ReleaseAssets.Where(asset => !named.Contains(asset, StringComparer.Ordinal)).Select(static asset => "asset not named: " + asset));
        violations.AddRange(named.Where(asset => !ReleaseAssets.Contains(asset, StringComparer.Ordinal)).Select(static asset => "unexpected asset: " + asset));
        if (requireAssets)
        {
            violations.AddRange(ReleaseAssets.Union(named, StringComparer.Ordinal).Where(asset => !File.Exists(Path.Combine(root, asset.Replace('/', Path.DirectorySeparatorChar)))).Select(static asset => "asset not found: " + asset));
        }

        if (!checklist.Contains("gh release view v0.1.0", StringComparison.Ordinal))
        {
            violations.Add("checklist lacks the verification command: gh release view v0.1.0");
        }

        return violations;
    }

    /// <summary>Every AJ-157 rule over a folder.</summary>
    /// <param name="root">The repository-shaped folder.</param>
    /// <returns>The violations.</returns>
    public static IReadOnlyList<string> ReleasePackageViolations(string root) =>
        [.. ReleaseNotesViolations(root), .. ReleaseZipViolations(root), .. TrackedArtifactViolations(root), .. ReleaseCommandViolations(root)];

    // The entries a named zip must hold: the entry count of the results zip is the count of results/*.json.
    private static IEnumerable<string> ContentViolations(string root, string asset, string zipName, ZipArchiveEntry[] files)
    {
        if (asset.EndsWith("-fdd.zip", StringComparison.Ordinal) && !files.Any(static entry => string.Equals(entry.Name, "AgentJoules.Cli.dll", StringComparison.Ordinal)))
        {
            yield return "AgentJoules.Cli.dll not listed in " + zipName;
        }

        if (asset.EndsWith("-results.zip", StringComparison.Ordinal))
        {
            int expected = Directory.Exists(ResultsPath(root)) ? Directory.EnumerateFiles(ResultsPath(root), "*.json").Count() : 0;
            if (files.Length != expected)
            {
                yield return $"{zipName} has {files.Length} files, results/ has {expected} json files";
            }
        }

        if (asset.EndsWith("-report.zip", StringComparison.Ordinal))
        {
            foreach (string required in ReportZipEntries.Where(required => !files.Any(entry => string.Equals(entry.Name, required, StringComparison.Ordinal))))
            {
                yield return required + " not listed in " + zipName;
            }
        }
    }

    // The entry is read as Latin-1 text (ASCII and UTF-8 strings) and as UTF-16LE text at both byte alignments (the user-string heap of a .NET DLL); only the verdict leaves this method, never a match.
    private static bool HoldsKeyMaterial(ZipArchiveEntry entry)
    {
        using var buffer = new MemoryStream();
        using (Stream stream = entry.Open())
        {
            stream.CopyTo(buffer);
        }

        byte[] bytes = buffer.ToArray();
        return KeyToken().IsMatch(Encoding.Latin1.GetString(bytes))
            || KeyToken().IsMatch(Encoding.Unicode.GetString(bytes))
            || (bytes.Length > 1 && KeyToken().IsMatch(Encoding.Unicode.GetString(bytes, 1, bytes.Length - 1)));
    }

    private static bool HasCloudGpuRunFile(string root) =>
        ResultFileNames(root).Any(static name => name.EndsWith("Z-cloudgpu.json", StringComparison.Ordinal) && !name.StartsWith("probe-", StringComparison.Ordinal) && !name.StartsWith("calibration-", StringComparison.Ordinal));

    [GeneratedRegex(@"(?<=[.!?])\s+(?=[A-Z\[""(`*])", RegexOptions.CultureInvariant)]
    private static partial Regex SentenceBreak();

    [GeneratedRegex(@"[0-9]+(\.[0-9]+)? ?(J|W|Wh|kJ|kWh|ms|tokens|%|EUR|USD|(?i:joules?|watts?))(?![A-Za-z])", RegexOptions.CultureInvariant)]
    private static partial Regex NoteQuantity();

    [GeneratedRegex(@"(?<![\w-])(?<!(?i:not) )(?:measured|estimated|inferred|model-judged|N/A|not-reported)(?![\w-])", RegexOptions.CultureInvariant)]
    private static partial Regex NoteLabel();

    [GeneratedRegex(@"(?:results/[A-Za-z0-9._-]+|[0-9]{8}T[0-9]{6}Z(?:-[a-z]+)+)\.json", RegexOptions.CultureInvariant)]
    private static partial Regex ResultsFileName();

    [GeneratedRegex(@"^(?:.*\.raw|id_ed25519.*|id_rsa.*|.*\.pem)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ForbiddenEntry();

    [GeneratedRegex(@"nvapi-[A-Za-z0-9_-]{8,}", RegexOptions.CultureInvariant)]
    private static partial Regex KeyToken();

    [GeneratedRegex(@"""[^""]*""|\S+", RegexOptions.CultureInvariant)]
    private static partial Regex ShellWords();
}