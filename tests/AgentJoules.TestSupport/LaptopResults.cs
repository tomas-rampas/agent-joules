using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentJoules.TestSupport;

/// <summary>
/// The committed laptop files of the measurement campaign (AJ-110, AJ-111, REQ-021) and the checks the gated tests and the open-gate steps make on them: the newest <c>results/probe-yyyyMMddTHHmmssZ.json</c> and
/// <c>results/calibration-yyyyMMddTHHmmssZ.json</c> (a probe saved with <c>--suffix</c> is not a laptop probe), the label-object shape of the three calibration members, the recomputation of the relative difference,
/// the driver version <c>nvidia-smi</c> prints, and the entry of a story in <c>docs/build-log.md</c>. Nothing here calls the network or writes a file; the one process it starts is <c>nvidia-smi</c> with a fixed
/// argument list, and it never reads or prints a key.
/// </summary>
public static partial class LaptopResults
{
    /// <summary>The members of a calibration file that are label objects.</summary>
    public static readonly string[] CalibrationMembers = ["counterJoules", "samplingJoules", "relativeDifference"];

    /// <summary>The tolerance of the recomputation of the relative difference (AJ-111 criterion 3).</summary>
    public const double RecomputeTolerance = 1e-9;

    private const string MeasuredLabel = "measured";
    private const string NotApplicableLabel = "N/A";

    /// <summary>The time limit of the <c>nvidia-smi</c> call.</summary>
    public static readonly TimeSpan SmiLimit = TimeSpan.FromSeconds(10);

    /// <summary>The <c>results</c> folder of the repository.</summary>
    /// <returns>The full path; the folder may not exist.</returns>
    public static string ResultsFolder() => Path.Combine(ConfigurationFixtures.RepositoryRoot(), "results");

    /// <summary>The newest laptop probe file.</summary>
    /// <param name="folder">The folder to look in; the <c>results</c> folder of the repository when null.</param>
    /// <returns>The full path, or null when the folder holds none.</returns>
    public static string? NewestProbe(string? folder = null) => Newest(ProbeName(), folder ?? ResultsFolder());

    /// <summary>The newest calibration file.</summary>
    /// <param name="folder">The folder to look in; the <c>results</c> folder of the repository when null.</param>
    /// <returns>The full path, or null when the folder holds none.</returns>
    public static string? NewestCalibration(string? folder = null) => Newest(CalibrationName(), folder ?? ResultsFolder());

    /// <summary>
    /// The files under <c>results/</c> in the HEAD commit (<c>git ls-tree -r --name-only HEAD -- results/</c>): the one committed view every evidence test uses. The view is HEAD, not the index: a file that is only staged,
    /// only untracked or only in the working tree is absent. Callers read the content of a listed name from its working-tree path, which equals HEAD when the tree is clean.
    /// </summary>
    /// <param name="repositoryRoot">The folder of the git repository; the repository of this test run when null.</param>
    /// <returns>The repository-relative paths with forward slashes, in git's order; empty when HEAD holds no file under <c>results/</c>.</returns>
    /// <exception cref="InvalidOperationException">git is not installed, or the call failed (a repository without a commit included).</exception>
    public static IReadOnlyList<string> CommittedResultsFiles(string? repositoryRoot = null)
    {
        GitResult result;
        try
        {
            result = GitRunner.Run(repositoryRoot ?? ConfigurationFixtures.RepositoryRoot(), "ls-tree", "-r", "--name-only", "HEAD", "--", "results/");
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidOperationException("the committed view of results/ cannot be read: " + exception.Message, exception);
        }

        return result.ExitCode == 0
            ? [.. result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(static line => line.Trim()).Where(static line => line.Length > 0)]
            : throw new InvalidOperationException("git ls-tree HEAD results/ exited with " + result.ExitCode.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>The message of a gated check that finds no committed file of a kind.</summary>
    /// <param name="pattern">The file name pattern, for example <c>results/probe-&lt;yyyyMMddTHHmmssZ&gt;.json</c>.</param>
    /// <param name="story">The story that commits it.</param>
    /// <returns>The message.</returns>
    public static string NoFileMessage(string pattern, string story) =>
        $"the results folder holds no {pattern}; {story} runs the command on the laptop and commits the file";

    /// <summary>Lists what is wrong with the three label objects of a calibration file.</summary>
    /// <param name="root">The root of the calibration file.</param>
    /// <returns>One message per problem; empty when the three members are label objects (a finite number labelled measured, or null labelled N/A with a non-empty reason).</returns>
    public static IReadOnlyList<string> LabelObjectFailures(JsonElement root)
    {
        List<string> failures = [];
        foreach (string member in CalibrationMembers)
        {
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(member, out JsonElement metric) || metric.ValueKind != JsonValueKind.Object)
            {
                failures.Add($"{member} is not a label object");
                continue;
            }

            string? label = metric.TryGetProperty("label", out JsonElement labelElement) && labelElement.ValueKind == JsonValueKind.String ? labelElement.GetString() : null;
            metric.TryGetProperty("value", out JsonElement value);
            if (string.Equals(label, MeasuredLabel, StringComparison.Ordinal))
            {
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double number) || !double.IsFinite(number))
                {
                    failures.Add($"{member} is labelled {MeasuredLabel} but its value is not a number");
                }
            }
            else if (string.Equals(label, NotApplicableLabel, StringComparison.Ordinal))
            {
                bool reason = metric.TryGetProperty("reason", out JsonElement reasonElement) && reasonElement.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(reasonElement.GetString());
                if (value.ValueKind != JsonValueKind.Null)
                {
                    failures.Add($"{member} is labelled {NotApplicableLabel} but its value is not null");
                }

                if (!reason)
                {
                    failures.Add($"{member} is labelled {NotApplicableLabel} without a reason");
                }
            }
            else
            {
                failures.Add($"{member} has the label \"{label}\"; only {MeasuredLabel} and {NotApplicableLabel} are allowed");
            }
        }

        return failures;
    }

    /// <summary>True when the counter total and the sampling total are both measured numbers.</summary>
    /// <param name="root">The root of the calibration file.</param>
    /// <returns>Whether both are measured.</returns>
    public static bool BothTotalsMeasured(JsonElement root) => MeasuredValue(root, CalibrationMembers[0]) is not null && MeasuredValue(root, CalibrationMembers[1]) is not null;

    /// <summary>Recomputes <c>(counter - sampling) / counter</c> and compares it with the stored <c>relativeDifference</c>.</summary>
    /// <param name="root">The root of the calibration file; both totals must be measured.</param>
    /// <returns>The recomputed value and the stored one (null when the file stores N/A); the recomputed value is null when the counter is 0.</returns>
    public static (double? Recomputed, double? Stored) Recompute(JsonElement root)
    {
        double counter = MeasuredValue(root, CalibrationMembers[0]) ?? throw new InvalidOperationException("counterJoules is not measured");
        double sampling = MeasuredValue(root, CalibrationMembers[1]) ?? throw new InvalidOperationException("samplingJoules is not measured");
        return (counter == 0.0 ? null : (counter - sampling) / counter, MeasuredValue(root, CalibrationMembers[2]));
    }

    /// <summary>Compares the stored <c>relativeDifference</c> of a calibration file with <c>(counter - sampling) / counter</c>.</summary>
    /// <param name="root">The root of the calibration file.</param>
    /// <returns>The problem, or null when the file passes or has no difference to recompute (a method that is not measured). A counter of 0 J must store N/A; otherwise the stored number must equal the recomputed one within <see cref="RecomputeTolerance"/>.</returns>
    public static string? RecomputeFailure(JsonElement root)
    {
        if (!BothTotalsMeasured(root))
        {
            return null;
        }

        (double? recomputed, double? stored) = Recompute(root);
        if (recomputed is null)
        {
            return stored is null ? null : "counterJoules is 0, so relativeDifference must be N/A, but the file stores a number";
        }

        if (stored is null)
        {
            return "both totals are measured, but relativeDifference is not a measured number";
        }

        double difference = Math.Abs(recomputed.Value - stored.Value);
        return difference <= RecomputeTolerance
            ? null
            : $"the stored relativeDifference {stored.Value.ToString("R", CultureInfo.InvariantCulture)} differs from the recomputed {recomputed.Value.ToString("R", CultureInfo.InvariantCulture)} by {difference.ToString("R", CultureInfo.InvariantCulture)}, over the tolerance {RecomputeTolerance.ToString("R", CultureInfo.InvariantCulture)}";
    }

    /// <summary>The text of the entry of a story in <c>docs/build-log.md</c>: the lines from its <c>### AJ-nnn</c> heading to the next <c>### </c> heading.</summary>
    /// <param name="story">The story id, for example <c>AJ-110</c>.</param>
    /// <returns>The entry text, or null when the log or the entry does not exist.</returns>
    public static string? BuildLogEntry(string story)
    {
        string path = Path.Combine(ConfigurationFixtures.RepositoryRoot(), "docs", "build-log.md");
        return File.Exists(path) ? EntryOf(File.ReadAllText(path), story) : null;
    }

    /// <summary>The entry of a story in the text of a build log.</summary>
    /// <param name="log">The text of the log.</param>
    /// <param name="story">The story id, for example <c>AJ-110</c>.</param>
    /// <returns>The lines after the <c>### AJ-nnn</c> heading up to, not including, the next <c>### </c> heading, each ended by a line feed; null when the log has no such heading.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static string? EntryOf(string log, string story)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(story);

        StringBuilder entry = new();
        bool inside = false;
        foreach (string line in log.Split('\n').Select(static line => line.TrimEnd('\r')))
        {
            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                if (inside)
                {
                    break;
                }

                inside = line.StartsWith("### " + story, StringComparison.Ordinal);
                continue;
            }

            if (inside)
            {
                entry.Append(line).Append('\n');
            }
        }

        return inside ? entry.ToString() : null;
    }

    /// <summary>The driver version <c>nvidia-smi --query-gpu=driver_version --format=csv,noheader</c> prints.</summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The first output line, trimmed; null when <c>nvidia-smi</c> is not installed.</returns>
    /// <exception cref="InvalidOperationException">The call failed or printed nothing.</exception>
    public static async Task<string?> NvidiaSmiDriverAsync(CancellationToken cancellationToken = default)
    {
        string? smi = FindNvidiaSmi();
        if (smi is null)
        {
            return null;
        }

        var info = new ProcessStartInfo(smi)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add("--query-gpu=driver_version");
        info.ArgumentList.Add("--format=csv,noheader");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SmiLimit);
        using var process = new Process { StartInfo = info };
        if (!process.Start())
        {
            throw new InvalidOperationException("nvidia-smi did not start");
        }

        process.StandardInput.Close();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("nvidia-smi did not exit within " + SmiLimit.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture) + " s");
        }

        // The error stream is drained so a chatty nvidia-smi cannot fill its pipe and stall; its text is never shown.
        string first = FirstLine(await stdout);
        _ = await stderr;
        return process.ExitCode == 0 && first.Length > 0
            ? first
            : throw new InvalidOperationException("nvidia-smi exited with " + process.ExitCode.ToString(CultureInfo.InvariantCulture) + " and printed " + (first.Length > 0 ? "a driver line" : "no driver line"));
    }

    /// <summary>The first non-empty line of what <c>nvidia-smi</c> printed, trimmed.</summary>
    /// <param name="output">The standard output text.</param>
    /// <returns>The line; empty when there is none.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="output"/> is null.</exception>
    public static string FirstLine(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(static line => line.Trim()).FirstOrDefault(static line => line.Length > 0) ?? string.Empty;
    }

    private static double? MeasuredValue(JsonElement root, string member) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(member, out JsonElement metric)
        && metric.ValueKind == JsonValueKind.Object
        && metric.TryGetProperty("label", out JsonElement label)
        && label.ValueKind == JsonValueKind.String
        && string.Equals(label.GetString(), MeasuredLabel, StringComparison.Ordinal)
        && metric.TryGetProperty("value", out JsonElement value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out double number)
            ? number
            : null;

    // The last file in ordinal order: the names carry the UTC timestamp, so the last is the newest.
    private static string? Newest(Regex name, string folder) =>
        Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder).Where(path => name.IsMatch(Path.GetFileName(path))).Order(StringComparer.Ordinal).LastOrDefault()
            : null;

    // The full path of nvidia-smi: the system directory, the legacy NVSMI folder, then every PATH entry.
    private static string? FindNvidiaSmi()
    {
        List<string> candidates =
        [
            Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe"),
        ];
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            candidates.Add(Path.Combine(directory, "nvidia-smi.exe"));
            candidates.Add(Path.Combine(directory, "nvidia-smi"));
        }

        // A relative candidate would be resolved against the working directory (Environment.SystemDirectory is empty on Linux, a relative PATH entry is a planted-file risk): only full paths count.
        return candidates.FirstOrDefault(static path => Path.IsPathFullyQualified(path) && File.Exists(path));
    }

    [GeneratedRegex(@"^probe-\d{8}T\d{6}Z\.json\z", RegexOptions.CultureInvariant)]
    private static partial Regex ProbeName();

    [GeneratedRegex(@"^calibration-\d{8}T\d{6}Z\.json\z", RegexOptions.CultureInvariant)]
    private static partial Regex CalibrationName();
}