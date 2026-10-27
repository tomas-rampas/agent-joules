using System.Text.Json.Nodes;

namespace AgentJoules.Specs.Support;

/// <summary>The run-file rules of AJ-150: a completed file per tier with enough repeats, the local baseline and runtime fields, stray files and the no-browser statement.</summary>
public static partial class EvidenceChecks
{
    private static readonly string[] PowerSources = ["ac", "battery", "unknown"];

    private static readonly string[] LocalRuntimes = ["nim", "ollama"];

    /// <summary>The message of a build log that lacks the no-browser statement.</summary>
    public const string MissingNoBrowserStatement = "missing statement: published runs recorded without a browser";

    /// <summary>The message of a changed driver.</summary>
    public const string DriverChanged = "driver changed since the committed probe";

    /// <summary>
    /// The tier rules: the newest non-baseline file of <c>local</c>, <c>nvidia</c> and <c>nebius</c> exists, has <c>run.status</c> <c>completed</c> and <c>run.repeat</c> of three or more; the newest <c>cloudgpu</c>
    /// file, when there is one, has three repeats or more (its status is judged by the cloudgpu rules).
    /// </summary>
    /// <param name="root">The repository-shaped folder.</param>
    /// <returns>The violations.</returns>
    public static IReadOnlyList<string> TierViolations(string root)
    {
        var violations = new List<string>();
        foreach (string tier in PublishedTiers)
        {
            string? name = NewestRunFileName(root, tier);
            if (name is null)
            {
                violations.Add("no run file for tier: " + tier);
                continue;
            }

            JsonNode? document = ReadResultFile(root, name);
            string? status = TextOf(At(document, "run", "status"));
            if (!string.Equals(status, "completed", StringComparison.Ordinal))
            {
                violations.Add($"run.status {status ?? "missing"} is not completed: {tier}");
            }

            AddRepeatViolation(violations, document, tier);
        }

        if (NewestRunFileName(root, "cloudgpu") is { } cloudGpuName)
        {
            AddRepeatViolation(violations, ReadResultFile(root, cloudGpuName), "cloudgpu");
        }

        return violations;
    }

    /// <summary>The local-file rules: <c>baseline.seconds</c> of 30 or more, a numeric <c>modelLoadSeconds</c> and <c>gpuOffloadPercent</c>, a <c>powerSource</c> of ac, battery or unknown and a <c>localRuntime</c> of nim or ollama. No file is no violation here; <see cref="TierViolations"/> reports it.</summary>
    /// <param name="root">The repository-shaped folder.</param>
    /// <returns>The violations.</returns>
    public static IReadOnlyList<string> LocalFileViolations(string root)
    {
        string? name = NewestRunFileName(root, "local");
        if (name is null)
        {
            return [];
        }

        JsonNode? document = ReadResultFile(root, name);
        var violations = new List<string>();
        if (NumberOf(At(document, "baseline", "seconds")) is not { } seconds)
        {
            violations.Add("baseline.seconds is missing or not a number");
        }
        else if (seconds < MinimumBaselineSeconds)
        {
            violations.Add($"baseline.seconds {Show(seconds)} is below {Show(MinimumBaselineSeconds)}");
        }

        if (NumberOf(At(document, "environment", "modelLoadSeconds")) is null)
        {
            violations.Add("environment.modelLoadSeconds is missing or not a number");
        }

        if (NumberOf(At(document, "environment", "gpuOffloadPercent")) is null)
        {
            violations.Add("environment.gpuOffloadPercent is missing or not a number");
        }

        AddOneOfViolation(violations, "environment.powerSource", TextOf(At(document, "environment", "powerSource")), PowerSources);
        AddOneOfViolation(violations, "environment.localRuntime", TextOf(At(document, "environment", "localRuntime")), LocalRuntimes);
        return violations;
    }

    /// <summary>The files of <c>results/</c> that must not be committed: a name ending in <c>.partial</c> or <c>.raw</c>.</summary>
    /// <param name="root">The repository-shaped folder.</param>
    /// <returns>One <c>stray file: &lt;name&gt;</c> violation per file.</returns>
    public static IReadOnlyList<string> StrayFileViolations(string root) =>
        [.. ResultFileNames(root)
            .Where(static name => name.EndsWith(".partial", StringComparison.Ordinal) || name.EndsWith(".raw", StringComparison.Ordinal))
            .Select(static name => "stray file: " + name)];

    /// <summary>The build-log rule: <c>docs/build-log.md</c> states that the published runs were recorded without a browser.</summary>
    /// <param name="root">The repository-shaped folder.</param>
    /// <returns>The violations.</returns>
    public static IReadOnlyList<string> BuildLogViolations(string root)
    {
        string? log = ReadText(root, "docs/build-log.md");
        if (log is null)
        {
            return ["missing docs/build-log.md"];
        }

        return log.Contains(NoBrowserStatement, StringComparison.Ordinal) ? [] : [MissingNoBrowserStatement];
    }

    /// <summary>Every AJ-150 rule over a folder: the tiers, the local file, stray files and the build log.</summary>
    /// <param name="root">The repository-shaped folder.</param>
    /// <returns>The violations.</returns>
    public static IReadOnlyList<string> RunEvidence(string root) =>
        [.. TierViolations(root), .. LocalFileViolations(root), .. StrayFileViolations(root), .. BuildLogViolations(root)];

    /// <summary>The driver rule of the probe: the driver installed now equals the driver of the committed probe.</summary>
    /// <param name="committedDriver">The <c>driverVersion</c> of the committed probe.</param>
    /// <param name="installedDriver">The driver the laptop reports now.</param>
    /// <returns>The violations.</returns>
    public static IReadOnlyList<string> DriverViolations(string? committedDriver, string? installedDriver) =>
        string.Equals(committedDriver?.Trim(), installedDriver?.Trim(), StringComparison.Ordinal) ? [] : [DriverChanged];

    /// <summary>The <c>driverVersion</c> of the newest committed probe without a suffix, or null.</summary>
    /// <param name="root">The repository-shaped folder.</param>
    /// <returns>The driver version, or null when there is no probe or it names none.</returns>
    public static string? CommittedProbeDriver(string root)
    {
        string? name = NewestProbeFileName(root, cloudGpu: false);
        return name is null ? null : TextOf(At(ReadResultFile(root, name), "driverVersion"));
    }

    private static void AddRepeatViolation(List<string> violations, JsonNode? document, string tier)
    {
        double? repeat = NumberOf(At(document, "run", "repeat"));
        if (repeat is null or < MinimumRepeat)
        {
            violations.Add($"run.repeat {(repeat is { } value ? Show(value) : "missing")} is below {MinimumRepeat}: {tier}");
        }
    }

    private static void AddOneOfViolation(List<string> violations, string field, string? value, string[] allowed)
    {
        if (value is null || !allowed.Contains(value, StringComparer.Ordinal))
        {
            violations.Add($"{field} {value ?? "missing"} is not one of {string.Join(", ", allowed)}");
        }
    }
}