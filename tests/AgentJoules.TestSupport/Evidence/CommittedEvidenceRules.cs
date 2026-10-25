using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using AgentJoules.Reporting.Endpoints;
using AgentJoules.TestSupport.Docs;

namespace AgentJoules.TestSupport.Evidence;

/// <summary>
/// The rules that tie the committed laptop evidence to the README (AJ-115, REQ-020, REQ-021, EDGE-010). Every method takes parsed or in-memory input and returns the violations in the order of the rules; an empty
/// list is a pass. A violation starts with the field it is about (<c>environment.localRuntime</c>, <c>run.flags</c>, <c>baseline.seconds</c>, <c>README.md</c>, <c>EDGE-010</c>) and states the expected and the found value;
/// the values are short and bounded, and none is a key. The rules live here, not in <c>CommittedEvidenceTests</c>, so that the facts over the committed files and the hermetic scenarios of the feature run the same code.
/// </summary>
/// <remarks>
/// The tag rule of the README sentence: the tag is the model ID of the Planner entry of <c>environment.models</c>; when the entries of the other agents name other IDs, the sentence lists every distinct ID of the
/// non-baseline entries, the Planner's first and the others in the order of the run file, separated by a comma and a space (<see cref="ExpectedTagText"/>).
/// </remarks>
public static partial class CommittedEvidenceRules
{
    /// <summary>The runtimes the local tier may record.</summary>
    public static readonly IReadOnlyList<string> Runtimes = ["nim", "ollama"];

    /// <summary>The values of <c>environment.powerSource</c>.</summary>
    public static readonly IReadOnlyList<string> PowerSources = ["ac", "battery", "unknown"];

    /// <summary>The shortest idle baseline that may be published, in seconds.</summary>
    public const double MinimumBaselineSeconds = 30;

    /// <summary>The fewest repeats of a published local run.</summary>
    public const int MinimumRepeat = 3;

    /// <summary>The first marker of the generated results block of the README.</summary>
    public const string BlockBegin = "<!-- results:begin -->";

    /// <summary>The last marker of the generated results block of the README.</summary>
    public const string BlockEnd = "<!-- results:end -->";

    private const string PlannerAgent = "Planner";
    private const int ShownLength = 80;

    /// <summary>Reads the text of a run file.</summary>
    /// <param name="runJson">The JSON text.</param>
    /// <returns>The root object.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="runJson"/> is null.</exception>
    /// <exception cref="InvalidDataException">The text is not a JSON object.</exception>
    public static JsonNode ParseRun(string runJson)
    {
        ArgumentNullException.ThrowIfNull(runJson);

        try
        {
            return JsonNode.Parse(runJson) is JsonObject root ? root : throw new InvalidDataException("The run file is not a JSON object.");
        }
        catch (JsonException)
        {
            throw new InvalidDataException("The run file is not valid JSON.");
        }
    }

    /// <summary>The text that the README sentence must give as the tag.</summary>
    /// <param name="agents">The agent of each model entry, in file order.</param>
    /// <param name="modelIds">The model ID of each entry, in the same order; a baseline entry is left out by the caller.</param>
    /// <returns>The distinct IDs, the Planner's first and the others in file order, joined with a comma and a space; the one ID when all are equal.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The lists differ in length.</exception>
    public static string ExpectedTagText(IReadOnlyList<string> agents, IReadOnlyList<string> modelIds)
    {
        ArgumentNullException.ThrowIfNull(agents);
        ArgumentNullException.ThrowIfNull(modelIds);
        if (agents.Count != modelIds.Count)
        {
            throw new ArgumentException("There must be one model ID per agent.", nameof(modelIds));
        }

        IEnumerable<string> planner = modelIds.Where((_, index) => string.Equals(agents[index], PlannerAgent, StringComparison.Ordinal));
        return string.Join(", ", planner.Concat(modelIds).Distinct(StringComparer.Ordinal));
    }

    /// <summary>REQ-020: the runtime is <c>nim</c> or <c>ollama</c>, and the tag of every model entry is a <c>- model:</c> line of the <c>## local</c> section of <c>docs/endpoints.md</c>.</summary>
    /// <param name="run">The run file.</param>
    /// <param name="endpoints">The reading of <c>docs/endpoints.md</c>.</param>
    /// <returns>The violations.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static IReadOnlyList<string> CheckRuntime(JsonNode run, EndpointsDocument endpoints)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(endpoints);

        List<string> violations = [];
        JsonNode? runtime = At(run, "environment", "localRuntime");
        if (TextOf(runtime) is not { } text || !Runtimes.Contains(text, StringComparer.Ordinal))
        {
            violations.Add($"environment.localRuntime: expected {Quoted(Runtimes, " or ")} but found {Show(runtime)}");
        }

        if (At(run, "environment", "models") is not JsonArray { Count: > 0 } models)
        {
            violations.Add($"environment.models: expected a non-empty list of model entries but found {Show(At(run, "environment", "models"))}");
            return violations;
        }

        endpoints.TryGetSection("local", out EndpointsSection? local);
        HashSet<string> reported = new(StringComparer.Ordinal);
        for (int index = 0; index < models.Count; index++)
        {
            JsonNode? id = models[index]?["modelId"];
            if (TextOf(id) is not { Length: > 0 } tag)
            {
                violations.Add($"environment.models[{index.ToString(CultureInfo.InvariantCulture)}].modelId: expected a model tag but found {Show(id)}");
            }
            else if (reported.Add(tag) && (local is null || !local.Lists(tag)))
            {
                string listed = local is null ? "the document has no \"## local\" section" : "listed: " + (local.ModelIds.Count == 0 ? "(none)" : string.Join(", ", local.ModelIds));
                violations.Add($"environment.models[{index.ToString(CultureInfo.InvariantCulture)}].modelId: \"{Bounded(tag)}\" is not a \"- model:\" line of \"## local\" in docs/endpoints.md ({Bounded(listed)})");
            }
        }

        return violations;
    }

    /// <summary>REQ-020: <c>gpuOffloadPercent</c> is a number from 0 to 100, <c>partial-offload</c> is in <c>run.flags</c> when it is below 100 and absent when it is 100.</summary>
    /// <param name="run">The run file.</param>
    /// <returns>The violations.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="run"/> is null.</exception>
    public static IReadOnlyList<string> CheckOffload(JsonNode run)
    {
        ArgumentNullException.ThrowIfNull(run);

        JsonNode? offload = At(run, "environment", "gpuOffloadPercent");
        if (NumberOf(offload) is not { } percent || percent < 0 || percent > 100)
        {
            return [$"environment.gpuOffloadPercent: expected a number from 0 to 100 but found {Show(offload)}"];
        }

        IReadOnlyList<string> flags = FlagsOf(run);
        bool partial = flags.Contains("partial-offload", StringComparer.Ordinal);
        return percent < 100 && !partial
            ? [$"run.flags: environment.gpuOffloadPercent is {Number(percent)} (below 100) but run.flags lacks \"partial-offload\" (found {FlagsText(flags)})"]
            : percent >= 100 && partial
                ? [$"run.flags: \"partial-offload\" is set but environment.gpuOffloadPercent is {Number(percent)} (the flag belongs to a value below 100)"]
                : [];
    }

    /// <summary>REQ-021: <c>baseline.seconds</c> of at least 30, <c>run.repeat</c> of at least 3, a numeric <c>environment.modelLoadSeconds</c>, a <c>powerSource</c> of the vocabulary and no <c>non-publishable</c> flag.</summary>
    /// <param name="run">The run file.</param>
    /// <returns>The violations.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="run"/> is null.</exception>
    public static IReadOnlyList<string> CheckMeasurement(JsonNode run)
    {
        ArgumentNullException.ThrowIfNull(run);

        List<string> violations = [];
        JsonNode? seconds = At(run, "baseline", "seconds");
        if (NumberOf(seconds) is not { } baseline || baseline < MinimumBaselineSeconds)
        {
            violations.Add($"baseline.seconds: expected at least {Number(MinimumBaselineSeconds)} but found {Show(seconds)}");
        }

        JsonNode? repeat = At(run, "run", "repeat");
        if (NumberOf(repeat) is not { } repeats || repeats < MinimumRepeat)
        {
            violations.Add($"run.repeat: expected at least {MinimumRepeat.ToString(CultureInfo.InvariantCulture)} but found {Show(repeat)}");
        }

        JsonNode? load = At(run, "environment", "modelLoadSeconds");
        if (NumberOf(load) is null)
        {
            violations.Add($"environment.modelLoadSeconds: expected a number but found {Show(load)}");
        }

        JsonNode? source = At(run, "environment", "powerSource");
        if (TextOf(source) is not { } power || !PowerSources.Contains(power, StringComparer.Ordinal))
        {
            violations.Add($"environment.powerSource: expected {Quoted(PowerSources, ", ")} but found {Show(source)}");
        }

        IReadOnlyList<string> flags = FlagsOf(run);
        if (flags.Contains("non-publishable", StringComparer.Ordinal))
        {
            violations.Add($"run.flags: contains \"non-publishable\" (the run is not fit to publish; found {FlagsText(flags)})");
        }

        return violations;
    }

    /// <summary>REQ-021: a calibration file exists and its three fields are label objects.</summary>
    /// <param name="files">The file name and the text of each committed calibration file.</param>
    /// <returns>The violations.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="files"/> is null.</exception>
    public static IReadOnlyList<string> CheckCalibration(IReadOnlyList<(string Name, string Json)> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        if (files.Count == 0)
        {
            return ["results/calibration-*.json: no committed calibration file"];
        }

        List<string> violations = [];
        foreach ((string name, string json) in files)
        {
            try
            {
                // The label-object rule is the one of AJ-111 (LaptopResults): a finite number labelled measured, or null labelled N/A with a non-empty reason.
                using JsonDocument document = JsonDocument.Parse(json);
                violations.AddRange(LaptopResults.LabelObjectFailures(document.RootElement).Select(failure => $"results/{name}: {failure}"));
            }
            catch (JsonException)
            {
                violations.Add($"results/{name}: the file is not valid JSON");
            }
        }

        return violations;
    }

    /// <summary>REQ-020: the README holds the sentence <c>Local numbers were produced with &lt;runtime&gt; running &lt;tag&gt;.</c> outside the generated results block, with the runtime and tag of the run file.</summary>
    /// <param name="readme">The text of <c>README.md</c>.</param>
    /// <param name="run">The run file.</param>
    /// <returns>The violations.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static IReadOnlyList<string> CheckReadmeSentence(string readme, JsonNode run)
    {
        ArgumentNullException.ThrowIfNull(readme);
        ArgumentNullException.ThrowIfNull(run);

        string runtime = TextOf(At(run, "environment", "localRuntime")) ?? string.Empty;
        string tags = TagsOf(run);
        MatchCollection sentences = SentencePattern().Matches(WithoutResultsBlock(readme));
        return sentences.Count == 0
            ? [$"README.md: no sentence \"Local numbers were produced with <runtime> running <tag>.\" outside the generated results block (expected \"Local numbers were produced with {Bounded(runtime)} running {Bounded(tags)}.\")"]
            :
            [
                .. sentences
                    .Where(sentence => sentence.Groups["runtime"].Value != runtime || sentence.Groups["tag"].Value != tags)
                    .Select(sentence => $"README.md: the sentence names runtime \"{Bounded(sentence.Groups["runtime"].Value)}\" and tag \"{Bounded(sentence.Groups["tag"].Value)}\" but the run file says environment.localRuntime \"{Bounded(runtime)}\" and model tag \"{Bounded(tags)}\""),
            ];
    }

    /// <summary>
    /// EDGE-010: the Decision of <c>docs/nim-local-attempt.md</c> records <c>Local runtime: nim</c> or <c>Local runtime: ollama</c>, the latest local run file records the same runtime, and the README links the attempt
    /// document. For a fallback decision (<c>ollama</c>) the link is the requirement; for <c>nim</c> it is the honesty record of the attempt and is asserted as well. That the configured <c>Local:Runtime</c> equals the Decision is
    /// <c>Shipped_Local_Runtime_equals_the_committed_decision</c> and is not repeated.
    /// </summary>
    /// <param name="decision">The reading of the attempt document.</param>
    /// <param name="readme">The text of <c>README.md</c>.</param>
    /// <param name="run">The run file.</param>
    /// <returns>The violations; each starts with <c>EDGE-010</c>.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static IReadOnlyList<string> CheckDecision(NimAttemptDocument decision, string readme, JsonNode run)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(readme);
        ArgumentNullException.ThrowIfNull(run);

        List<string> violations = [];
        if (decision.DecisionRuntime is not { } recorded)
        {
            violations.Add("EDGE-010: docs/nim-local-attempt.md has no \"Local runtime: nim\" or \"Local runtime: ollama\" line in its Decision section");
        }
        else
        {
            JsonNode? runtime = At(run, "environment", "localRuntime");
            if (!string.Equals(TextOf(runtime), recorded, StringComparison.Ordinal))
            {
                violations.Add($"EDGE-010: the Decision records \"Local runtime: {recorded}\" but environment.localRuntime of the latest local run file is {Show(runtime)}");
            }
        }

        if (!AttemptLinkPattern().IsMatch(readme))
        {
            violations.Add($"EDGE-010: README.md has no link to docs/nim-local-attempt.md (the Decision records \"Local runtime: {decision.DecisionRuntime ?? "(none)"}\"; expected a Markdown link to docs/nim-local-attempt.md)");
        }

        return violations;
    }

    /// <summary>Applies every rule to evidence held in memory.</summary>
    /// <param name="runJson">The text of the latest local run file.</param>
    /// <param name="calibrations">The committed calibration files.</param>
    /// <param name="endpoints">The reading of <c>docs/endpoints.md</c>.</param>
    /// <param name="decision">The reading of <c>docs/nim-local-attempt.md</c>.</param>
    /// <param name="readme">The text of <c>README.md</c>.</param>
    /// <returns>The violations in the order REQ-020, REQ-021, EDGE-010.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static IReadOnlyList<string> CheckAll(string runJson, IReadOnlyList<(string Name, string Json)> calibrations, EndpointsDocument endpoints, NimAttemptDocument decision, string readme)
    {
        ArgumentNullException.ThrowIfNull(runJson);

        JsonNode run;
        try
        {
            run = ParseRun(runJson);
        }
        catch (InvalidDataException ex)
        {
            return [$"run file: {ex.Message}"];
        }

        return
        [
            .. CheckRuntime(run, endpoints),
            .. CheckOffload(run),
            .. CheckReadmeSentence(readme, run),
            .. CheckMeasurement(run),
            .. CheckCalibration(calibrations),
            .. CheckDecision(decision, readme, run),
        ];
    }

    /// <summary>Reads the evidence through <paramref name="reader"/> and applies every rule.</summary>
    /// <param name="reader">The reader of a tree.</param>
    /// <returns>The violations; a tree without a local run file gives one.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="reader"/> is null.</exception>
    public static IReadOnlyList<string> CheckAll(CommittedEvidenceReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        return reader.LatestLocalRunFile() is { } path
            ? CheckAll(File.ReadAllText(path), reader.CalibrationTexts(), reader.EndpointsDocument(), reader.NimDecision(), reader.ReadmeText())
            : ["results/: no completed or partial local run file"];
    }

    // The README without the generated block: from the begin marker to the end marker, or to the end of the text when the block is not closed.
    private static string WithoutResultsBlock(string readme)
    {
        int begin = readme.IndexOf(BlockBegin, StringComparison.Ordinal);
        if (begin < 0)
        {
            return readme;
        }

        int end = readme.IndexOf(BlockEnd, begin + BlockBegin.Length, StringComparison.Ordinal);
        return end < 0 ? readme[..begin] : readme[..begin] + readme[(end + BlockEnd.Length)..];
    }

    // The tag text of the run file: the IDs of the non-baseline entries by ExpectedTagText.
    private static string TagsOf(JsonNode run)
    {
        List<string> agents = [];
        List<string> ids = [];
        if (At(run, "environment", "models") is JsonArray models)
        {
            foreach (JsonNode? entry in models)
            {
                if (entry is JsonObject model && TextOf(model["modelId"]) is { } id && model["baseline"]?.GetValueKind() != JsonValueKind.True)
                {
                    agents.Add(TextOf(model["agent"]) ?? string.Empty);
                    ids.Add(id);
                }
            }
        }

        return ExpectedTagText(agents, ids);
    }

    // The node at a path of member names; null when a step is missing or a JSON null (JsonNode has no separate null node).
    private static JsonNode? At(JsonNode root, params string[] path)
    {
        JsonNode? node = root;
        foreach (string name in path)
        {
            node = node is JsonObject obj ? obj[name] : null;
        }

        return node;
    }

    private static string? TextOf(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    private static double? NumberOf(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.Number && value.TryGetValue(out double number) ? number : null;

    private static List<string> FlagsOf(JsonNode run) =>
        At(run, "run", "flags") is JsonArray array ? [.. array.Select(TextOf).OfType<string>()] : [];

    private static string FlagsText(IReadOnlyList<string> flags) => flags.Count == 0 ? "(no flags)" : string.Join(", ", flags);

    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static string Quoted(IEnumerable<string> values, string separator) => string.Join(separator, values.Select(static value => "\"" + value + "\""));

    private static string Show(JsonNode? node) => node is null ? "null or missing" : Bounded(node.ToJsonString());

    private static string Bounded(string text) => text.Length <= ShownLength ? text : text[..ShownLength] + "...";

    [GeneratedRegex(@"Local numbers were produced with (?<runtime>\S+) running (?<tag>\S[^\r\n]*?)\.(?=\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex SentencePattern();

    [GeneratedRegex(@"\]\(docs/nim-local-attempt\.md[)#]", RegexOptions.CultureInvariant)]
    private static partial Regex AttemptLinkPattern();
}