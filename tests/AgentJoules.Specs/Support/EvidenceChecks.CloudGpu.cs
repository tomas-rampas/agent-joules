using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentJoules.Specs.Support;

/// <summary>The outcome of the cloudgpu run-file rule.</summary>
/// <param name="Violations">The violations; empty when the newest file counts as evidence.</param>
/// <param name="Preset">The <c>environment.instancePreset</c> of the newest file, or null.</param>
public sealed record CloudGpuRunEvidence(IReadOnlyList<string> Violations, string? Preset);

/// <summary>The cloudgpu rules of AJ-151: the run file, the probe, the six headings and the cost cap and teardown of the run record, the README block and the key scan.</summary>
public static partial class EvidenceChecks
{
    /// <summary>The headings the run record <c>docs/cloudgpu-run.md</c> holds.</summary>
    public static IReadOnlyList<string> CloudGpuRecordHeadings { get; } = ["Instance", "Commands", "Probe", "Runs", "Cost", "Teardown"];

    /// <summary>The message of a key found in the run record.</summary>
    public const string KeyInRecord = "key material in docs/cloudgpu-run.md";

    private const string CostLinePrefix = "- Whole-run cost:";

    /// <summary>
    /// The run-file rule: the newest cloudgpu run file has <c>run.status</c> <c>completed</c> or <c>partial</c> (any other status does not count as evidence), <c>environment.provider</c> <c>nebius-ai-cloud</c>,
    /// <c>environment.precision</c> <c>FP8</c> and a non-empty <c>environment.instancePreset</c>.
    /// </summary>
    /// <param name="root">The repository-shaped folder.</param>
    /// <returns>The violations and the preset.</returns>
    public static CloudGpuRunEvidence CloudGpuRunCheck(string root)
    {
        string? name = NewestRunFileName(root, "cloudgpu");
        JsonNode? document = name is null ? null : ReadResultFile(root, name);
        string? status = TextOf(At(document, "run", "status"));
        if (status is not ("completed" or "partial"))
        {
            return new CloudGpuRunEvidence(["no completed or partial cloudgpu run file"], null);
        }

        var violations = new List<string>();
        string? provider = TextOf(At(document, "environment", "provider"));
        if (!string.Equals(provider, "nebius-ai-cloud", StringComparison.Ordinal))
        {
            violations.Add($"environment.provider {provider ?? "missing"} is not nebius-ai-cloud");
        }

        string? precision = TextOf(At(document, "environment", "precision"));
        if (!string.Equals(precision, "FP8", StringComparison.Ordinal))
        {
            violations.Add($"environment.precision {precision ?? "missing"} is not FP8");
        }

        string? preset = TextOf(At(document, "environment", "instancePreset"));
        if (string.IsNullOrWhiteSpace(preset))
        {
            violations.Add("environment.instancePreset is empty");
        }

        return new CloudGpuRunEvidence(violations, preset);
    }

    /// <summary>The probe rule: a cloudgpu probe exists, records a boolean <c>energyCounterSupported</c> and does not have MIG enabled.</summary>
    /// <param name="root">The repository-shaped folder.</param>
    /// <returns>The violations.</returns>
    public static IReadOnlyList<string> CloudGpuProbeViolations(string root)
    {
        string? name = NewestProbeFileName(root, cloudGpu: true);
        if (name is null)
        {
            return ["no cloudgpu probe file"];
        }

        JsonNode? probe = ReadResultFile(root, name);
        var violations = new List<string>();
        string? mig = TextOf(At(probe, "migMode"));
        if (string.Equals(mig, "enabled", StringComparison.Ordinal))
        {
            violations.Add("MIG enabled: measurement invalid");
        }
        else if (mig is not ("disabled" or "not-supported"))
        {
            violations.Add($"migMode {mig ?? "missing"} is not disabled");
        }

        if (At(probe, "energyCounterSupported") is not JsonValue flag || !flag.TryGetValue(out bool _))
        {
            violations.Add("energyCounterSupported is missing or not a boolean");
        }

        return violations;
    }

    /// <summary>
    /// The run-record rules: the six headings, the whole-run cost in the <c>Cost</c> section at or below the cap (the first <c>USD &lt;number&gt;</c> of the line that starts <c>- Whole-run cost:</c>) and the word
    /// <c>deleted</c> in the <c>Teardown</c> section; a key value or an <c>nvapi-</c> token anywhere is a violation.
    /// </summary>
    /// <param name="root">The repository-shaped folder.</param>
    /// <returns>The violations.</returns>
    public static IReadOnlyList<string> CloudGpuRecordViolations(string root)
    {
        string? text = ReadText(root, "docs/cloudgpu-run.md");
        if (text is null)
        {
            return ["missing docs/cloudgpu-run.md"];
        }

        var violations = new List<string>();
        foreach (string heading in CloudGpuRecordHeadings)
        {
            if (SectionOf(text, heading) is null)
            {
                violations.Add("missing heading: " + heading);
            }
        }

        if (SectionOf(text, "Cost") is { } cost)
        {
            AddCostViolation(violations, cost);
        }

        if (SectionOf(text, "Teardown") is { } teardown && !teardown.Contains("deleted", StringComparison.OrdinalIgnoreCase))
        {
            violations.Add("teardown not confirmed");
        }

        violations.AddRange(KeyMaterialViolations(text, keyValue: null));
        return violations;
    }

    /// <summary>The key rule over the text of the run record: it holds neither the value of a key variable nor an <c>nvapi-</c> token. The violation never carries the value.</summary>
    /// <param name="text">The text of the run record.</param>
    /// <param name="keyValue">The value of <c>NGC_API_KEY</c>, or null for the token pattern only.</param>
    /// <returns>The violations.</returns>
    public static IReadOnlyList<string> KeyMaterialViolations(string text, string? keyValue)
    {
        bool value = !string.IsNullOrWhiteSpace(keyValue) && text.Contains(keyValue.Trim(), StringComparison.Ordinal);
        return value || NvidiaKeyToken().IsMatch(text) ? [KeyInRecord] : [];
    }

    /// <summary>The README rule: the text between the two result markers names <c>cloudgpu</c>.</summary>
    /// <param name="readme">The README text.</param>
    /// <returns>The violations.</returns>
    public static IReadOnlyList<string> ReadmeResultsViolations(string readme)
    {
        const string Begin = "<!-- results:begin -->";
        const string End = "<!-- results:end -->";
        int begin = readme.IndexOf(Begin, StringComparison.Ordinal);
        int end = readme.IndexOf(End, StringComparison.Ordinal);
        if (begin < 0 || end < begin)
        {
            return ["missing results block markers in README.md"];
        }

        return readme[begin..end].Contains("cloudgpu", StringComparison.Ordinal) ? [] : ["results block lacks cloudgpu"];
    }

    /// <summary>Every AJ-151 file rule over a folder: the run file, the probe and the run record.</summary>
    /// <param name="root">The repository-shaped folder.</param>
    /// <returns>The violations.</returns>
    public static IReadOnlyList<string> CloudGpuEvidence(string root) =>
        [.. CloudGpuRunCheck(root).Violations, .. CloudGpuProbeViolations(root), .. CloudGpuRecordViolations(root)];

    private static void AddCostViolation(List<string> violations, string cost)
    {
        string? line = DocumentChecks.Lines(cost).FirstOrDefault(static candidate => candidate.StartsWith(CostLinePrefix, StringComparison.Ordinal));
        if (line is null)
        {
            violations.Add("no whole-run cost line in the Cost section");
            return;
        }

        Match match = UsdAmount().Match(line);
        if (!match.Success)
        {
            violations.Add("no USD value in the whole-run cost line");
            return;
        }

        string token = match.Groups["amount"].Value;
        if (double.Parse(token, CultureInfo.InvariantCulture) > CostCapUsd)
        {
            violations.Add($"cost {token} exceeds {Show(CostCapUsd)}");
        }
    }

    [GeneratedRegex(@"USD (?<amount>[0-9]+(\.[0-9]+)?)", RegexOptions.CultureInvariant)]
    private static partial Regex UsdAmount();

    [GeneratedRegex(@"nvapi-[A-Za-z0-9_-]{8,}", RegexOptions.CultureInvariant)]
    private static partial Regex NvidiaKeyToken();
}