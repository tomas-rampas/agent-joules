using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using AgentJoules.Core.Configuration;
using AgentJoules.Core.Runs;
using AgentJoules.Reporting.Results;
using AgentJoules.TestSupport.Evidence;

using Xunit.Sdk;

namespace AgentJoules.TestSupport;

/// <summary>
/// The committed run files of the laptop measurement campaign (AJ-114, REQ-018, REQ-020, REQ-021) and the checks that the gated tests, the open-gate steps and the ungated theory make on them: the newest accepted
/// run file of a tier (<see cref="RunFileSelector.Latest"/>), the shape of criterion 3 (status completed, exactly 3 tasks, <c>run.repeat</c> 3, 36 steps, a non-empty <c>judge</c> array, outcome <c>ok</c> on every
/// step), the local fields of criterion 4 (the rules of <see cref="CommittedEvidenceRules"/> that name them), the hosted energy values of criterion 4, the schema check and the file count of criterion 5. Every
/// check returns messages that name the field and both values; nothing here calls the network or writes a file, and no key is read.
/// </summary>
public static partial class LaptopCampaign
{
    /// <summary>The story that commits the files; it names the owner of a missing file in a failure message.</summary>
    public const string Story = "AJ-114-T4";

    /// <summary>The tasks of a campaign run (criterion 3).</summary>
    public const int TaskCount = 3;

    /// <summary>The <c>run.repeat</c> of a campaign run (criterion 3).</summary>
    public const int RepeatCount = 3;

    /// <summary>The steps of a campaign file: 3 tasks x 3 repeats x 4 agents (criterion 3).</summary>
    public const int StepCount = 36;

    private const int ShownFailures = 3;
    private const int ShownLength = 60;

    /// <summary>The tiers of the campaign.</summary>
    public static readonly IReadOnlyList<Tier> Tiers = [Tier.Local, Tier.Nvidia, Tier.Nebius];

    /// <summary>The hosted tiers of the campaign, whose steps carry no energy value.</summary>
    public static readonly IReadOnlyList<Tier> HostedTiers = [Tier.Nvidia, Tier.Nebius];

    /// <summary>
    /// The tiers whose file may still be missing. A missing file of such a tier is a note, never a failure, and an existing file is checked in full. The list is empty since the nvidia file was committed (it was pending while the free NVIDIA tier was saturated,
    /// AJ-114 criterion 6). A missing file of any tier fails. Add a tier here only for a tier that is re-run alone.
    /// </summary>
    public static readonly IReadOnlyList<Tier> PendingTiers = [];

    /// <summary>
    /// The newest accepted run file of a tier in the committed view: the HEAD names of <see cref="LaptopResults.CommittedResultsFiles"/> go through <see cref="RunFileSelector.Select"/>, as in
    /// <see cref="CommittedEvidenceReader.LatestLocalRunFile"/>, so an untracked, staged or unselectable working-tree file is never the pick. The content is read from the working-tree path of the committed name.
    /// </summary>
    /// <param name="tier">The tier.</param>
    /// <param name="repositoryRoot">The folder of the git repository; the repository of this test run when null.</param>
    /// <returns>The full path, or null when HEAD holds no completed or partial file of the tier.</returns>
    /// <exception cref="InvalidDataException">A committed run file of the tier cannot be used (the message is <c>&lt;file&gt;: &lt;reason&gt;</c>).</exception>
    /// <exception cref="InvalidOperationException">git is not installed, or the call failed.</exception>
    public static string? LatestCommittedRunFile(Tier tier, string? repositoryRoot = null)
    {
        string suffix = "-" + TierOptions.NameOf(tier);
        string[] candidates =
        [
            .. CommittedRunFiles(null, repositoryRoot)
                .Where(path => Path.GetFileNameWithoutExtension(path).EndsWith(suffix, StringComparison.Ordinal) || Path.GetFileNameWithoutExtension(path).EndsWith(suffix + "-baseline", StringComparison.Ordinal)),
        ];
        RunFileSelection selection = RunFileSelector.Select(candidates);

        return selection.Problems.Count > 0
            ? throw new InvalidDataException($"{selection.Problems[0].FileName}: {selection.Problems[0].Reason}")
            : selection.Latest.GetValueOrDefault(tier);
    }

    /// <summary>The newest accepted run file of a tier in the working tree (a fresh uncommitted file counts); <see cref="LatestCommittedRunFile"/> is the committed view.</summary>
    /// <param name="tier">The tier.</param>
    /// <param name="folder">The folder to look in; the <c>results</c> folder of the repository when null.</param>
    /// <returns>The full path, or null when the folder is absent or holds no completed or partial file of the tier.</returns>
    /// <exception cref="InvalidDataException">A run file of the tier cannot be used (the message is <c>&lt;file&gt;: &lt;reason&gt;</c>).</exception>
    public static string? LatestRunFile(Tier tier, string? folder = null)
    {
        string results = folder ?? LaptopResults.ResultsFolder();
        return Directory.Exists(results) ? RunFileSelector.Latest(results, tier) : null;
    }

    /// <summary>Finds the newest run file of a tier, or records why a pending tier has none.</summary>
    /// <param name="tier">The tier.</param>
    /// <param name="note">Receives the note for a pending tier without a file.</param>
    /// <param name="folder">The folder to look in; the <c>results</c> folder of the repository when null.</param>
    /// <returns>The full path, or null for a pending tier without a file.</returns>
    /// <exception cref="XunitException">The tier is not pending and has no file.</exception>
    public static string? FindOrNote(Tier tier, Action<string> note, string? folder = null)
    {
        ArgumentNullException.ThrowIfNull(note);

        if (LatestRunFile(tier, folder) is { } path)
        {
            return path;
        }

        string message = LaptopResults.NoFileMessage($"results/<yyyyMMddTHHmmssZ>-{TierOptions.NameOf(tier)}.json", Story);
        if (PendingTiers.Contains(tier))
        {
            note("note: " + message + "; the tier is pending, so its checks were not made");
            return null;
        }

        throw new XunitException(message);
    }

    /// <summary>The files of <c>results/</c> that count for <c>AJ validate results/</c> (criterion 5): <c>^[0-9]{8}T[0-9]{6}Z-(local|cloudgpu|nvidia|nebius)(-baseline)?\.json$</c>.</summary>
    /// <param name="folder">The folder whose files all count; when null, the committed view: the names of <see cref="LaptopResults.CommittedResultsFiles"/> (the HEAD tree), so an untracked or staged file in the working tree is not a row. The content of a committed name is read from its working-tree path, which equals HEAD when the tree is clean.</param>
    /// <param name="repositoryRoot">The folder of the git repository for the committed view; the repository of this test run when null.</param>
    /// <returns>The full paths in ordinal order; empty when the folder is absent or holds none.</returns>
    /// <exception cref="InvalidOperationException">The committed view is wanted and git is not installed, or the call failed.</exception>
    public static IReadOnlyList<string> CommittedRunFiles(string? folder = null, string? repositoryRoot = null)
    {
        if (folder is null)
        {
            string root = repositoryRoot ?? ConfigurationFixtures.RepositoryRoot();
            return
            [
                .. LaptopResults.CommittedResultsFiles(root)
                    .Where(static name => name.IndexOf('/', "results/".Length) < 0 && CountedName().IsMatch(name["results/".Length..]))
                    .Select(name => Path.GetFullPath(Path.Combine(root, name)))
                    .Where(File.Exists)
                    .Order(StringComparer.Ordinal),
            ];
        }

        string results = folder;
        return Directory.Exists(results)
            ? [.. Directory.EnumerateFiles(results).Where(static path => CountedName().IsMatch(Path.GetFileName(path))).Order(StringComparer.Ordinal)]
            : [];
    }

    /// <summary>The run files of one tier that carry <c>run.status</c> <c>failed</c> (the file of a failed tier is never committed, criterion 6).</summary>
    /// <param name="tier">The tier.</param>
    /// <param name="folder">The folder; the <c>results</c> folder of the repository when null.</param>
    /// <returns>The full paths in ordinal order.</returns>
    public static IReadOnlyList<string> FailedFiles(Tier tier, string? folder = null)
    {
        string suffix = "-" + TierOptions.NameOf(tier) + ".json";
        return
        [
            .. CommittedRunFiles(folder).Where(path => path.EndsWith(suffix, StringComparison.Ordinal) && string.Equals(StatusOf(Read(path)), "failed", StringComparison.Ordinal)),
        ];
    }

    /// <summary>Reads a run file.</summary>
    /// <param name="path">The full path.</param>
    /// <returns>The root object.</returns>
    /// <exception cref="InvalidDataException">The file is not a JSON object.</exception>
    public static JsonNode Read(string path) => CommittedEvidenceRules.ParseRun(File.ReadAllText(path));

    /// <summary>Criterion 3: status completed, exactly 3 tasks, <c>run.repeat</c> 3, 36 steps, a non-empty <c>judge</c> array and outcome <c>ok</c> on every step.</summary>
    /// <param name="run">The run file.</param>
    /// <returns>The violations; empty when the file has the shape.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="run"/> is null.</exception>
    public static IReadOnlyList<string> ShapeFailures(JsonNode run)
    {
        ArgumentNullException.ThrowIfNull(run);

        List<string> failures = [];
        if (!string.Equals(StatusOf(run), "completed", StringComparison.Ordinal))
        {
            failures.Add($"run.status: expected \"completed\" but found {Show(At(run, "run", "status"))}");
        }

        if (At(run, "run", "tasks") is not JsonArray tasks || tasks.Count != TaskCount)
        {
            failures.Add($"run.tasks: expected {TaskCount.ToString(CultureInfo.InvariantCulture)} tasks but found {CountText(At(run, "run", "tasks"))}");
        }

        JsonNode? repeat = At(run, "run", "repeat");
        if (!(repeat is JsonValue value && value.GetValueKind() == JsonValueKind.Number && value.TryGetValue(out int repeats) && repeats == RepeatCount))
        {
            failures.Add($"run.repeat: expected {RepeatCount.ToString(CultureInfo.InvariantCulture)} but found {Show(repeat)}");
        }

        failures.AddRange(StepFailures(run));
        if (At(run, "judge") is not JsonArray { Count: > 0 })
        {
            failures.Add($"judge: expected a non-empty array but found {CountText(At(run, "judge"))}");
        }

        return failures;
    }

    /// <summary>Criterion 3, the steps only: exactly 36 and outcome <c>ok</c> on every one.</summary>
    /// <param name="run">The run file.</param>
    /// <returns>The violations; empty when all 36 steps are ok.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="run"/> is null.</exception>
    public static IReadOnlyList<string> StepFailures(JsonNode run)
    {
        ArgumentNullException.ThrowIfNull(run);

        if (At(run, "steps") is not JsonArray steps)
        {
            return [$"steps: expected an array of {StepCount.ToString(CultureInfo.InvariantCulture)} steps but found {Show(At(run, "steps"))}"];
        }

        List<string> failures = [];
        if (steps.Count != StepCount)
        {
            failures.Add($"steps: expected {StepCount.ToString(CultureInfo.InvariantCulture)} steps but found {steps.Count.ToString(CultureInfo.InvariantCulture)}");
        }

        failures.AddRange(Offenders(steps, static step => string.Equals(TextOf(step?["outcome"]), "ok", StringComparison.Ordinal), static index => $"steps[{index}].outcome: expected \"ok\""));
        return failures;
    }

    /// <summary>Criterion 4 for a hosted tier: every step has <c>grossJoules</c> and <c>netJoules</c> as <c>{value:null,label:"N/A"}</c> with a non-empty reason.</summary>
    /// <param name="run">The run file of the nvidia or nebius tier.</param>
    /// <returns>The violations; empty when no step carries an energy value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="run"/> is null.</exception>
    public static IReadOnlyList<string> HostedFailures(JsonNode run)
    {
        ArgumentNullException.ThrowIfNull(run);

        return At(run, "steps") is not JsonArray { Count: > 0 } steps
            ? [$"steps: expected a non-empty array but found {Show(At(run, "steps"))}"]
            :
            [
                .. Offenders(steps, static step => NotApplicable(step?["grossJoules"]), static index => $"steps[{index}].grossJoules: expected value null, label \"N/A\" and a reason"),
                .. Offenders(steps, static step => NotApplicable(step?["netJoules"]), static index => $"steps[{index}].netJoules: expected value null, label \"N/A\" and a reason"),
            ];
    }

    /// <summary>
    /// Criterion 4 for the local file: <c>baseline.seconds</c> of at least 30, a numeric <c>environment.modelLoadSeconds</c>, <c>environment.powerSource</c> in {ac, battery, unknown}, a numeric
    /// <c>environment.gpuOffloadPercent</c> (with the <c>partial-offload</c> flag rule) and <c>environment.localRuntime</c> in {nim, ollama}. The measurement rules of AJ-115 are reused, so a short repeat count or
    /// a <c>non-publishable</c> flag is reported here too; each message starts with the field it is about.
    /// </summary>
    /// <param name="run">The local run file.</param>
    /// <returns>The violations; empty when the file carries the evidence.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="run"/> is null.</exception>
    public static IReadOnlyList<string> LocalFailures(JsonNode run)
    {
        ArgumentNullException.ThrowIfNull(run);

        List<string> failures = [.. CommittedEvidenceRules.CheckMeasurement(run), .. CommittedEvidenceRules.CheckOffload(run)];
        JsonNode? runtime = At(run, "environment", "localRuntime");
        if (TextOf(runtime) is not { } text || !CommittedEvidenceRules.Runtimes.Contains(text, StringComparer.Ordinal))
        {
            failures.Add($"environment.localRuntime: expected \"nim\" or \"ollama\" but found {Show(runtime)}");
        }

        return failures;
    }

    /// <summary>Checks a run file with the check of <c>AJ validate</c>, <see cref="RunFileValidator.Check"/>: the schema and the results-model read with its own rules.</summary>
    /// <param name="path">The full path of the file.</param>
    /// <returns>One line per failure as the command prints it; empty when the file is valid.</returns>
    public static IReadOnlyList<string> SchemaFailures(string path) => RunFileValidator.Check(Path.GetFileName(path), File.ReadAllText(path));

    /// <summary>The <c>run.status</c> of a run file.</summary>
    /// <param name="run">The run file.</param>
    /// <returns>The status, or null when it is missing or not a string.</returns>
    public static string? StatusOf(JsonNode run) => TextOf(At(run, "run", "status"));

    // The first few elements that fail the predicate, then a count of the rest.
    private static IEnumerable<string> Offenders(JsonArray array, Func<JsonNode?, bool> accepts, Func<string, string> describe)
    {
        List<string> bad = [];
        for (int index = 0; index < array.Count; index++)
        {
            if (!accepts(array[index]))
            {
                bad.Add(describe(index.ToString(CultureInfo.InvariantCulture)));
            }
        }

        foreach (string message in bad.Take(ShownFailures))
        {
            yield return message;
        }

        if (bad.Count > ShownFailures)
        {
            yield return $"and {(bad.Count - ShownFailures).ToString(CultureInfo.InvariantCulture)} more steps fail the same rule";
        }
    }

    // A label object of an energy value that a hosted tier cannot have: null value, label N/A, a reason.
    private static bool NotApplicable(JsonNode? node) =>
        node is JsonObject metric
        && metric.ContainsKey("value")
        && metric["value"] is null
        && string.Equals(TextOf(metric["label"]), "N/A", StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(TextOf(metric["reason"]));

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

    private static string CountText(JsonNode? node) => node is JsonArray array ? array.Count.ToString(CultureInfo.InvariantCulture) + " elements" : Show(node);

    private static string Show(JsonNode? node)
    {
        string text = node is null ? "null or missing" : node.ToJsonString();
        return text.Length <= ShownLength ? text : text[..ShownLength] + "...";
    }

    [GeneratedRegex(@"^[0-9]{8}T[0-9]{6}Z-(local|cloudgpu|nvidia|nebius)(-baseline)?\.json\z", RegexOptions.CultureInvariant)]
    private static partial Regex CountedName();
}