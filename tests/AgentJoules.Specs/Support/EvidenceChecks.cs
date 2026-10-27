using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The checks of the M7 evidence stories (AJ-150 the run files, AJ-151 the cloudgpu evidence, AJ-152 the generated outputs, AJ-153 the documentation and Devpost files), split by story over the partial files of this class.
/// Each rule takes the root of a repository-shaped folder (the repository itself for the live check of the committed tree, a <see cref="AgentJoules.TestSupport.TempWorkspace"/> for a negative scenario), only reads files there
/// and returns its violations as text, an empty list being a pass. The wording of a violation is the wording the negative scenarios assert. Nothing here uses the network, a GPU or a key.
/// </summary>
public static partial class EvidenceChecks
{
    /// <summary>The tiers of the published evidence: one completed run file each.</summary>
    public static IReadOnlyList<string> PublishedTiers { get; } = ["local", "nvidia", "nebius"];

    /// <summary>The fewest repeats a published run file may record.</summary>
    public const int MinimumRepeat = 3;

    /// <summary>The shortest idle baseline, in seconds.</summary>
    public const double MinimumBaselineSeconds = 30;

    /// <summary>The cost cap of the cloudgpu run, in USD.</summary>
    public const double CostCapUsd = 30;

    /// <summary>The longest shot list of the demo script, in seconds.</summary>
    public const int ShotListLimitSeconds = 170;

    /// <summary>The longest rendered post variant, in characters.</summary>
    public const int VariantCharacterLimit = 1300;

    /// <summary>The number of answers the user fills in the feedback file.</summary>
    public const int UserPlaceholderCount = 7;

    /// <summary>The earliest date a verification line may carry.</summary>
    public static readonly DateOnly EarliestVerification = new(2026, 10, 17);

    /// <summary>The statement the build log must hold: the published runs were recorded with no browser showing the dashboard.</summary>
    public const string NoBrowserStatement = "recorded without a browser";

    /// <summary>The path of the results folder under a root.</summary>
    /// <param name="root">The repository-shaped folder.</param>
    /// <returns>The path.</returns>
    public static string ResultsPath(string root) => Path.Combine(root, "results");

    /// <summary>The names of the files in <c>results/</c> in ordinal order, or none when the folder does not exist.</summary>
    /// <param name="root">The repository-shaped folder.</param>
    /// <returns>The file names without folder.</returns>
    public static IReadOnlyList<string> ResultFileNames(string root) =>
        Directory.Exists(ResultsPath(root))
            ? [.. Directory.EnumerateFiles(ResultsPath(root)).Select(static path => Path.GetFileName(path)).Order(StringComparer.Ordinal)]
            : [];

    /// <summary>The name of the newest non-baseline run file of a tier (the timestamp in the name sorts), or null.</summary>
    /// <param name="root">The repository-shaped folder.</param>
    /// <param name="tier">The tier name.</param>
    /// <returns>The file name, or null when the tier has none.</returns>
    public static string? NewestRunFileName(string root, string tier) =>
        ResultFileNames(root).LastOrDefault(name => RunFileOfTier().Match(name) is { Success: true } match && string.Equals(match.Groups["tier"].Value, tier, StringComparison.Ordinal));

    /// <summary>The name of the newest probe file: with no suffix, or with the <c>-cloudgpu</c> suffix.</summary>
    /// <param name="root">The repository-shaped folder.</param>
    /// <param name="cloudGpu">True for the cloudgpu probe.</param>
    /// <returns>The file name, or null.</returns>
    public static string? NewestProbeFileName(string root, bool cloudGpu) =>
        ResultFileNames(root).LastOrDefault(name => (cloudGpu ? CloudGpuProbeFile() : LocalProbeFile()).IsMatch(name));

    /// <summary>Reads a JSON file of <c>results/</c> as a node tree, or null when the file is absent or not JSON.</summary>
    /// <param name="root">The repository-shaped folder.</param>
    /// <param name="fileName">The file name in <c>results/</c>.</param>
    /// <returns>The parsed document, or null.</returns>
    public static JsonNode? ReadResultFile(string root, string fileName)
    {
        string path = Path.Combine(ResultsPath(root), fileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(File.ReadAllText(path));
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>The text of a file under the root (either separator), or null when it does not exist.</summary>
    /// <param name="root">The repository-shaped folder.</param>
    /// <param name="relativePath">The path relative to the root.</param>
    /// <returns>The text, or null.</returns>
    public static string? ReadText(string root, string relativePath)
    {
        string path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    /// <summary>The node at a path of property names, or null when any step is missing.</summary>
    /// <param name="node">The start node.</param>
    /// <param name="keys">The property names, outermost first.</param>
    /// <returns>The node or null.</returns>
    public static JsonNode? At(JsonNode? node, params string[] keys)
    {
        JsonNode? current = node;
        foreach (string key in keys)
        {
            current = current is JsonObject obj && obj.TryGetPropertyValue(key, out JsonNode? next) ? next : null;
        }

        return current;
    }

    /// <summary>The number at a node, or null when the node is absent or not a number.</summary>
    /// <param name="node">The node.</param>
    /// <returns>The number or null.</returns>
    public static double? NumberOf(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out double number) ? number : null;

    /// <summary>The string at a node, or null when the node is absent or not a string.</summary>
    /// <param name="node">The node.</param>
    /// <returns>The string or null.</returns>
    public static string? TextOf(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    /// <summary>Formats a number the way a violation shows it: at most three decimals, no trailing zeros, invariant culture.</summary>
    /// <param name="number">The number.</param>
    /// <returns>The text.</returns>
    public static string Show(double number) => number.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>The body of a <c>## </c> section: the lines after the heading up to the next <c>## </c> heading, or null when the heading is absent.</summary>
    /// <param name="text">The document text.</param>
    /// <param name="heading">The heading text without the marker.</param>
    /// <returns>The section body, or null.</returns>
    public static string? SectionOf(string text, string heading)
    {
        string[] lines = DocumentChecks.Lines(text);
        int start = Array.FindIndex(lines, line => string.Equals(line.TrimEnd(), "## " + heading, StringComparison.Ordinal));
        if (start < 0)
        {
            return null;
        }

        IEnumerable<string> body = lines.Skip(start + 1).TakeWhile(static line => !line.StartsWith("## ", StringComparison.Ordinal));
        return string.Join('\n', body);
    }

    /// <summary>The headings (<c>## </c> lines) of a document, in order.</summary>
    /// <param name="text">The document text.</param>
    /// <returns>The heading texts without the marker.</returns>
    public static IReadOnlyList<string> HeadingsOf(string text) =>
        [.. DocumentChecks.Lines(text).Where(static line => line.StartsWith("## ", StringComparison.Ordinal)).Select(static line => line[3..].TrimEnd())];

    /// <summary>The number of lines that contain a fixed string, as <c>grep -cF</c> counts.</summary>
    /// <param name="text">The text.</param>
    /// <param name="needle">The fixed string.</param>
    /// <returns>The count.</returns>
    public static int LinesContaining(string text, string needle) =>
        DocumentChecks.Lines(text).Count(line => line.Contains(needle, StringComparison.Ordinal));

    [GeneratedRegex(@"^[0-9]{8}T[0-9]{6}Z-(?<tier>local|cloudgpu|nvidia|nebius)\.json$", RegexOptions.CultureInvariant)]
    private static partial Regex RunFileOfTier();

    [GeneratedRegex(@"^probe-[0-9]{8}T[0-9]{6}Z\.json$", RegexOptions.CultureInvariant)]
    private static partial Regex LocalProbeFile();

    [GeneratedRegex(@"^probe-[0-9]{8}T[0-9]{6}Z-cloudgpu\.json$", RegexOptions.CultureInvariant)]
    private static partial Regex CloudGpuProbeFile();
}