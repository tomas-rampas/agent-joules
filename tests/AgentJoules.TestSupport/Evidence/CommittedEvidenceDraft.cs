using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace AgentJoules.TestSupport.Evidence;

/// <summary>
/// A set of committed-evidence documents built in memory for the tests of AJ-115 (REQ-020, REQ-021, EDGE-010): a local run file, <c>docs/endpoints.md</c>, a calibration file, <c>README.md</c> and the text of
/// <c>docs/nim-local-attempt.md</c>. It starts as a set that satisfies every rule of <see cref="CommittedEvidenceRules"/> and each test changes the one part it is about. Nothing here is read from the repository;
/// <see cref="WriteTo"/> lays the set out in a temporary workspace so that <see cref="CommittedEvidenceReader"/> can read it.
/// </summary>
public sealed class CommittedEvidenceDraft
{
    /// <summary>The name of the run file of the set; it satisfies the run-file name pattern and sorts after nothing else in the workspace.</summary>
    public const string RunFileName = "20261025T100000Z-local.json";

    /// <summary>The name of the calibration file of the set.</summary>
    public const string CalibrationFileName = "calibration-20261025T090000Z.json";

    /// <summary>The agents of the run, in the order of the run file (the Planner first).</summary>
    public static readonly IReadOnlyList<string> Agents = ["Planner", "Researcher", "Writer", "Reviewer"];

    /// <summary>The default model tag of every agent.</summary>
    public const string DefaultTag = "nvidia/Llama-3.1-Nemotron-Nano-4B-v1.1";

    private const string BlockBegin = "<!-- results:begin -->";
    private const string BlockEnd = "<!-- results:end -->";

    /// <summary>The runtime of <c>environment.localRuntime</c>.</summary>
    public string? Runtime { get; set; } = "nim";

    /// <summary>The model tag of the Planner, the Researcher and the Writer.</summary>
    public string Tag { get; set; } = DefaultTag;

    /// <summary>True lists the Reviewer first and the Planner second in <c>environment.models</c>, so the Planner is not first in the file.</summary>
    public bool PlannerNotFirst { get; set; }

    /// <summary>The tag of an extra <c>baseline: true</c> model entry (a Qwen run), or null for none; it must not enter the README tag text.</summary>
    public string? BaselineTag { get; set; }

    /// <summary>True renders <c>environment.models</c> as an empty list.</summary>
    public bool EmptyModels { get; set; }

    /// <summary>False leaves the <c>## local</c> section out of the endpoints document.</summary>
    public bool EndpointsHaveLocalSection { get; set; } = true;

    /// <summary>False leaves the results block of the README without its end marker.</summary>
    public bool ReadmeBlockClosed { get; set; } = true;

    /// <summary>A string to write as <c>environment.modelLoadSeconds</c> instead of the number, or null.</summary>
    public string? ModelLoadSecondsText { get; set; }

    /// <summary>The model tag of the Reviewer, or null for <see cref="Tag"/>.</summary>
    public string? ReviewerTag { get; set; }

    /// <summary>The tags that the <c>## local</c> section of the endpoints document lists as <c>- model:</c> lines; null lists every tag of the run.</summary>
    public IReadOnlyList<string>? EndpointTags { get; set; }

    /// <summary>The value of <c>environment.gpuOffloadPercent</c>, or null for a JSON null.</summary>
    public double? GpuOffloadPercent { get; set; } = 100;

    /// <summary>The strings of <c>run.flags</c>.</summary>
    public List<string> Flags { get; } = [];

    /// <summary>The value of <c>baseline.seconds</c>.</summary>
    public double BaselineSeconds { get; set; } = 30;

    /// <summary>The value of <c>run.repeat</c>.</summary>
    public int Repeat { get; set; } = 3;

    /// <summary>The value of <c>environment.modelLoadSeconds</c>, or null for a JSON null.</summary>
    public double? ModelLoadSeconds { get; set; } = 4.5;

    /// <summary>The value of <c>environment.powerSource</c>, or null for a JSON null.</summary>
    public string? PowerSource { get; set; } = "ac";

    /// <summary>The runtime in the README sentence, or null for <see cref="Runtime"/>.</summary>
    public string? ReadmeRuntime { get; set; }

    /// <summary>The tag text in the README sentence, or null for the tags of the run file (the rule of <see cref="CommittedEvidenceRules.ExpectedTagText"/>).</summary>
    public string? ReadmeTag { get; set; }

    /// <summary>False leaves the README sentence out.</summary>
    public bool ReadmeHasSentence { get; set; } = true;

    /// <summary>True puts the README sentence inside the generated results block only.</summary>
    public bool ReadmeSentenceInsideBlock { get; set; }

    /// <summary>False leaves the link to <c>docs/nim-local-attempt.md</c> out of the README.</summary>
    public bool ReadmeHasLink { get; set; } = true;

    /// <summary>The value of the <c>Local runtime:</c> line of the Decision section, or null for no such line.</summary>
    public string? DecisionRuntime { get; set; } = "nim";

    /// <summary>The text of the calibration file; the default holds three measured label objects.</summary>
    public string CalibrationJson { get; set; } = ValidCalibrationJson;

    /// <summary>False leaves the calibration file out of <see cref="WriteTo"/>.</summary>
    public bool HasCalibrationFile { get; set; } = true;

    /// <summary>A calibration file whose three fields are label objects.</summary>
    public static string ValidCalibrationJson { get; } =
        """
        {
          "counterJoules": { "value": 120.5, "label": "measured", "unit": "J" },
          "samplingJoules": { "value": 118.0, "label": "measured", "unit": "J" },
          "relativeDifference": { "value": 0.0207, "label": "measured", "unit": "ratio" }
        }
        """;

    /// <summary>The tags of the run file's models in the order of <see cref="Agents"/>.</summary>
    public IReadOnlyList<string> RunTags => [Tag, Tag, Tag, ReviewerTag ?? Tag];

    /// <summary>Renders the run file.</summary>
    /// <returns>The JSON text.</returns>
    public string RunJson()
    {
        JsonArray flags = [.. Flags.Select(static flag => JsonValue.Create(flag))];
        JsonArray models = [];
        int[] order = PlannerNotFirst ? [3, 0, 1, 2] : [0, 1, 2, 3];
        foreach (int index in EmptyModels ? [] : order)
        {
            models.Add(new JsonObject { ["agent"] = Agents[index], ["modelId"] = RunTags[index], ["baseline"] = false });
        }

        if (BaselineTag is not null && !EmptyModels)
        {
            models.Add(new JsonObject { ["agent"] = "Planner", ["modelId"] = BaselineTag, ["baseline"] = true });
        }

        var root = new JsonObject
        {
            ["schemaVersion"] = "1",
            ["run"] = new JsonObject { ["tier"] = "local", ["status"] = "completed", ["repeat"] = Repeat, ["flags"] = flags },
            ["environment"] = new JsonObject
            {
                ["localRuntime"] = Runtime,
                ["gpuOffloadPercent"] = GpuOffloadPercent,
                ["modelLoadSeconds"] = ModelLoadSecondsText is null ? ModelLoadSeconds : JsonValue.Create(ModelLoadSecondsText),
                ["powerSource"] = PowerSource,
                ["models"] = models,
            },
            ["baseline"] = new JsonObject { ["seconds"] = BaselineSeconds },
        };
        return root.ToJsonString();
    }

    /// <summary>Renders <c>docs/endpoints.md</c>: a <c>## local</c> section that lists the tags, and a <c>## nvidia</c> section whose model line must not count for the local tier.</summary>
    /// <returns>The Markdown text.</returns>
    public string EndpointsText()
    {
        IEnumerable<string> tags = EndpointTags ?? RunTags.Distinct(StringComparer.Ordinal);
        var text = new StringBuilder();
        _ = text.Append("# Endpoints\n\n");
        if (EndpointsHaveLocalSection)
        {
            _ = text.Append("## local\n\nVerified: 2026-10-24\n");
        }

        foreach (string tag in EndpointsHaveLocalSection ? tags : [])
        {
            _ = text.Append("- model: ").Append(tag).Append('\n');
        }

        _ = text.Append("\n## nvidia\n\nVerified: 2026-10-24\n- model: nvidia/hosted-only-model\n");
        return text.ToString();
    }

    /// <summary>Renders <c>README.md</c>.</summary>
    /// <returns>The Markdown text.</returns>
    public string ReadmeText()
    {
        string sentence = string.Create(
            CultureInfo.InvariantCulture,
            $"Local numbers were produced with {ReadmeRuntime ?? Runtime} running {ReadmeTag ?? CommittedEvidenceRules.ExpectedTagText(Agents, RunTags)}.");
        var text = new StringBuilder("# Agent Joules\n\n## Local runtime: NIM attempt\n\n");
        _ = text.Append(ReadmeHasLink ? "The attempt is recorded in [docs/nim-local-attempt.md](docs/nim-local-attempt.md).\n\n" : "The attempt is recorded elsewhere.\n\n");
        if (ReadmeHasSentence && !ReadmeSentenceInsideBlock)
        {
            _ = text.Append(sentence).Append("\n\n");
        }

        _ = text.Append("## Results\n\n").Append(BlockBegin).Append('\n');
        _ = text.Append(ReadmeHasSentence && ReadmeSentenceInsideBlock ? sentence : "No results yet.").Append('\n');
        if (ReadmeBlockClosed)
        {
            _ = text.Append(BlockEnd).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>Renders <c>docs/nim-local-attempt.md</c>: only the Decision section is part of the set.</summary>
    /// <returns>The Markdown text.</returns>
    public string DecisionText() =>
        "# NIM-local attempt\n\n## Decision\n\nThe container was judged.\n\n" + (DecisionRuntime is null ? string.Empty : "Local runtime: " + DecisionRuntime + "\n");

    /// <summary>Writes the set into a workspace: <c>results/</c>, <c>docs/endpoints.md</c>, <c>docs/nim-local-attempt.md</c> and <c>README.md</c>.</summary>
    /// <param name="workspace">The workspace; it need not hold anything yet.</param>
    /// <exception cref="ArgumentNullException"><paramref name="workspace"/> is null.</exception>
    public void WriteTo(TempWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        _ = workspace.WriteFile(Path.Combine("results", RunFileName), RunJson());
        if (HasCalibrationFile)
        {
            _ = workspace.WriteFile(Path.Combine("results", CalibrationFileName), CalibrationJson);
        }

        _ = workspace.WriteFile(Path.Combine("docs", "endpoints.md"), EndpointsText());
        _ = workspace.WriteFile(Path.Combine("docs", "nim-local-attempt.md"), DecisionText());
        _ = workspace.WriteFile("README.md", ReadmeText());
    }
}