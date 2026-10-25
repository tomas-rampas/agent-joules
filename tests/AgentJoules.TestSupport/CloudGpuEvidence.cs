using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using AgentJoules.Core.Runs;
using AgentJoules.Reporting.Endpoints;
using AgentJoules.Reporting.Results;
using AgentJoules.Reporting.Schema;
using AgentJoules.TestSupport.Docs;

namespace AgentJoules.TestSupport;

/// <summary>
/// The committed evidence of the cloudgpu VM stories (AJ-125 probe, AJ-126 NIM serving, AJ-127 run, REQ-031) and the checks that the gated classes, the open-gate steps and the ungated unit fact make on it: the committed
/// <c>results/probe-&lt;yyyyMMddTHHmmssZ&gt;-cloudgpu.json</c> and <c>results/&lt;yyyyMMddTHHmmssZ&gt;-cloudgpu.json</c> files of the HEAD tree, the sections of <c>docs/cloudgpu-run.md</c>, the <c>## cloudgpu</c> section of
/// <c>docs/endpoints.md</c> and the secret shape of a document. Every check returns messages that name the field and never hold a host, an address or a key; nothing here calls the network, starts a process other than git, or writes a file.
/// </summary>
public static partial class CloudGpuEvidence
{
    /// <summary>The probe <c>migMode</c> values that let the story go on (AJ-125 criterion 4); <c>enabled</c> stops it.</summary>
    public static readonly IReadOnlyList<string> MigModes = ["disabled", "not-supported"];

    /// <summary>The first date of a valid <c>Verified:</c> line of the <c>## cloudgpu</c> section (AJ-126 criterion 4).</summary>
    public static readonly DateOnly EndpointsVerifiedFrom = new(2026, 10, 17);

    /// <summary>The tasks of a cloudgpu run (AJ-127 criterion 2).</summary>
    public const int TaskCount = 3;

    /// <summary>The <c>run.repeat</c> of a cloudgpu run.</summary>
    public const int RepeatCount = 3;

    /// <summary>The steps of a cloudgpu run: 3 tasks x 3 repeats x 4 agents.</summary>
    public const int StepCount = 36;

    /// <summary>The least <c>baseline.seconds</c> of a cloudgpu run.</summary>
    public const double MinimumBaselineSeconds = 30;

    /// <summary>The path of the run document, relative to the repository root.</summary>
    public const string RunDocumentName = "docs/cloudgpu-run.md";

    /// <summary>The gate message of an open gate without a probe file.</summary>
    public const string NoProbeMessage = "the HEAD tree holds no results/probe-<yyyyMMddTHHmmssZ>-cloudgpu.json; AJ-125-T7 commits it after the probe on the VM";

    /// <summary>The gate message of an open gate without a run file.</summary>
    public const string NoRunMessage = "the HEAD tree holds no results/<yyyyMMddTHHmmssZ>-cloudgpu.json; AJ-127-T8 commits it after the run on the VM";

    /// <summary>The gate message of an open gate without a verified <c>## cloudgpu</c> section.</summary>
    public const string NoEndpointsMessage = "docs/endpoints.md has no Verified line in its ## cloudgpu section; AJ-126-T7 writes it after the models call on the VM";

    /// <summary>The committed cloudgpu probe files.</summary>
    /// <param name="repositoryRoot">The folder of the git repository; the repository of this test run when null.</param>
    /// <returns>The full paths in ordinal order; empty when HEAD holds none.</returns>
    /// <exception cref="InvalidOperationException">git is not installed, or the call failed.</exception>
    public static IReadOnlyList<string> CommittedProbeFiles(string? repositoryRoot = null) => Committed(ProbeName(), repositoryRoot);

    /// <summary>The committed cloudgpu run files (a <c>-baseline</c> file and a calibration file are not run files of this class).</summary>
    /// <param name="repositoryRoot">The folder of the git repository; the repository of this test run when null.</param>
    /// <returns>The full paths in ordinal order; empty when HEAD holds none.</returns>
    /// <exception cref="InvalidOperationException">git is not installed, or the call failed.</exception>
    public static IReadOnlyList<string> CommittedRunFiles(string? repositoryRoot = null) => Committed(RunName(), repositoryRoot);

    /// <summary>The newest committed cloudgpu probe file.</summary>
    /// <param name="repositoryRoot">The folder of the git repository; the repository of this test run when null.</param>
    /// <returns>The full path, or null when HEAD holds none.</returns>
    public static string? NewestProbe(string? repositoryRoot = null) => CommittedProbeFiles(repositoryRoot) is { Count: > 0 } files ? files[^1] : null;

    /// <summary>The newest committed cloudgpu run file that the selector accepts (completed or partial; an incomplete file never counts).</summary>
    /// <param name="repositoryRoot">The folder of the git repository; the repository of this test run when null.</param>
    /// <returns>The full path, or null when HEAD holds none.</returns>
    /// <exception cref="InvalidDataException">A committed run file cannot be used.</exception>
    public static string? NewestRun(string? repositoryRoot = null) => LaptopCampaign.LatestCommittedRunFile(Tier.CloudGpu, repositoryRoot);

    /// <summary>The reason the evidence of a kind is absent, with git trouble (no git, a failed call) counted as absent. A run file that exists and cannot be used is not absent: it is a problem.</summary>
    /// <param name="kind">The kind of evidence.</param>
    /// <param name="repositoryRoot">The folder of the git repository for the probe and run kinds; the repository of this test run when null.</param>
    /// <returns>Null when the evidence exists; otherwise the reason.</returns>
    /// <exception cref="InvalidDataException">A committed run file cannot be used (not JSON, or of another tier): a gated fact and a step must fail on it, not skip.</exception>
    public static string? AbsentReason(CloudGpuEvidenceKind kind, string? repositoryRoot = null)
    {
        try
        {
            return kind switch
            {
                CloudGpuEvidenceKind.Probe => NewestProbe(repositoryRoot) is null ? NoProbeMessage : null,
                CloudGpuEvidenceKind.Run => NewestRun(repositoryRoot) is null ? NoRunMessage : null,
                CloudGpuEvidenceKind.Endpoints => EndpointsText() is { } text && EndpointsSectionVerified(text) ? null : NoEndpointsMessage,
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            };
        }
        catch (InvalidOperationException exception)
        {
            return "the committed evidence cannot be read: " + exception.Message;
        }
    }

    /// <summary>The checks on the text of a probe file.</summary>
    /// <param name="fileName">The file name, used in no message.</param>
    /// <param name="text">The file content.</param>
    /// <returns>One message per problem; empty when the probe validates against <c>$defs/probe</c> and has an L40S name, a <c>gpuUuid</c>, <c>migMode</c> in {disabled, not-supported}, a boolean <c>energyCounterSupported</c>, a <c>driverVersion</c>, a <c>powerLimitW</c>, a <c>method</c> and a <c>sampleRateHz</c>.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static IReadOnlyList<string> ProbeFailures(string fileName, string text)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(text);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return ["the probe file is not JSON"];
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            List<string> failures = [.. SchemaValidator.Validate(root, SchemaTarget.Probe).Select(static failure => $"$defs/probe {failure.InstanceLocation}: {failure.Message}")];
            if (root.ValueKind != JsonValueKind.Object)
            {
                failures.Add("the probe is not an object");
                return failures;
            }

            if (TextOf(root, "name") is not { } name || !name.Contains("L40S", StringComparison.Ordinal))
            {
                failures.Add("name: expected an L40S GPU name");
            }

            AddIfEmpty(failures, root, "gpuUuid");
            AddIfEmpty(failures, root, "driverVersion");
            AddIfEmpty(failures, root, "method");
            if (TextOf(root, "migMode") is not { } mig || !MigModes.Contains(mig, StringComparer.Ordinal))
            {
                failures.Add("migMode: expected \"disabled\" or \"not-supported\"");
            }

            if (!root.TryGetProperty("energyCounterSupported", out JsonElement counter) || counter.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                failures.Add("energyCounterSupported: expected a boolean");
            }

            foreach (string member in new[] { "powerLimitW", "sampleRateHz" })
            {
                if (!root.TryGetProperty(member, out JsonElement number) || number.ValueKind != JsonValueKind.Number)
                {
                    failures.Add(member + ": expected a number");
                }
            }

            return failures;
        }
    }

    /// <summary>The checks on the text of a cloudgpu run file: <see cref="RunFileValidator"/> (the check of <c>AJ validate</c>) and <see cref="RunFieldFailures"/>.</summary>
    /// <param name="fileName">The file name, used in the validator lines.</param>
    /// <param name="text">The file content.</param>
    /// <returns>One message per problem; empty when the file passes.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static IReadOnlyList<string> RunFailures(string fileName, string text)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(text);

        List<string> failures = [.. RunFileValidator.Check(fileName, text)];
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return failures;
        }

        if (root is JsonObject run)
        {
            failures.AddRange(RunFieldFailures(run));
        }

        return failures;
    }

    /// <summary>The field rules of AJ-127 criterion 2 (no schema check).</summary>
    /// <param name="root">The run file.</param>
    /// <returns>One message per problem, each starting with the field; empty when the file has the shape.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="root"/> is null.</exception>
    public static IReadOnlyList<string> RunFieldFailures(JsonObject root)
    {
        ArgumentNullException.ThrowIfNull(root);

        List<string> failures = [];
        string? status = Text(root, "run", "status");
        bool stopped = false;
        if (string.Equals(status, "partial", StringComparison.Ordinal))
        {
            stopped = !string.IsNullOrWhiteSpace(Text(root, "run", "stopReason"));
            if (!stopped)
            {
                failures.Add("run.stopReason: a partial file needs a stop reason");
            }
        }
        else if (!string.Equals(status, "completed", StringComparison.Ordinal))
        {
            failures.Add("run.status: expected \"completed\" (or \"partial\" with a stop reason)");
        }

        if (!string.Equals(Text(root, "run", "tier"), "cloudgpu", StringComparison.Ordinal))
        {
            failures.Add("run.tier: expected \"cloudgpu\"");
        }

        if (At(root, "run", "tasks") is not JsonArray tasks || tasks.Count != TaskCount)
        {
            failures.Add("run.tasks: expected 3 tasks");
        }

        if (!string.Equals(At(root, "run", "repeat")?.ToJsonString(), RepeatCount.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            failures.Add("run.repeat: expected 3");
        }

        // A completed run holds all 36 steps; a run the guard stopped (partial with a stop reason) holds the steps it reached, 1 to 36.
        if (At(root, "steps") is not JsonArray steps || (stopped ? steps.Count is < 1 or > StepCount : steps.Count != StepCount))
        {
            failures.Add(stopped ? "steps: expected 1 to 36 steps" : "steps: expected 36 steps");
        }

        if (!string.Equals(Text(root, "environment", "provider"), "nebius-ai-cloud", StringComparison.Ordinal))
        {
            failures.Add("environment.provider: expected \"nebius-ai-cloud\"");
        }

        if (!string.Equals(Text(root, "environment", "precision"), "FP8", StringComparison.Ordinal))
        {
            failures.Add("environment.precision: expected \"FP8\"");
        }

        if (!string.Equals(Text(root, "environment", "region"), "eu-north1", StringComparison.Ordinal))
        {
            failures.Add("environment.region: expected \"eu-north1\"");
        }

        foreach (string member in new[] { "instancePreset", "gpuUuid", "containerImage" })
        {
            if (string.IsNullOrWhiteSpace(Text(root, "environment", member)))
            {
                failures.Add($"environment.{member}: expected a non-empty string");
            }
        }

        if (Text(root, "environment", "containerDigest") is not { } digest || !digest.StartsWith("sha256:", StringComparison.Ordinal))
        {
            failures.Add("environment.containerDigest: expected a value starting \"sha256:\"");
        }

        double? seconds = At(root, "baseline", "seconds") is JsonValue value && value.TryGetValue(out double parsed) ? parsed : null;
        if (seconds is null or < MinimumBaselineSeconds)
        {
            failures.Add("baseline.seconds: expected at least 30");
        }

        return failures;
    }

    /// <summary>The text of <c>docs/cloudgpu-run.md</c> in the working tree, not at HEAD: the steps judge the document the builder has just written, before its commit.</summary>
    /// <returns>The text, or null when the document does not exist.</returns>
    public static string? RunDocumentText()
    {
        string path = Path.Combine(ConfigurationFixtures.RepositoryRoot(), "docs", "cloudgpu-run.md");
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    /// <summary>The text of <c>docs/endpoints.md</c> in the working tree, not at HEAD: the steps judge the section the builder has just written, before its commit.</summary>
    /// <returns>The text, or null when the document does not exist.</returns>
    public static string? EndpointsText()
    {
        string path = Path.Combine(ConfigurationFixtures.RepositoryRoot(), "docs", "endpoints.md");
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    /// <summary>The lines under a <c>## </c> heading up to the next <c>## </c> heading.</summary>
    /// <param name="document">The text of the document.</param>
    /// <param name="heading">The heading text without the <c>## </c>, for example <c>Commands</c>.</param>
    /// <returns>The lines joined by line feeds; null when the heading does not occur.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static string? Section(string document, string heading)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(heading);

        List<string> lines = [];
        bool inside = false;
        bool found = false;
        foreach (string line in document.Split('\n').Select(static raw => raw.TrimEnd()))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (inside)
                {
                    break;
                }

                inside = string.Equals(line["## ".Length..].Trim(), heading, StringComparison.Ordinal);
                found |= inside;
                continue;
            }

            if (inside)
            {
                lines.Add(line);
            }
        }

        return found ? string.Join('\n', lines) : null;
    }

    /// <summary>True when the text holds a key-like token by the one rule of <see cref="CloudGpuRunDocumentParser.HasKeyShape"/>: an <c>nvapi-</c> token or a token of 40 or more characters with upper case, lower case and a digit. A hex digest or a 64-digit NIM profile id does not count.</summary>
    /// <param name="text">The text of a document.</param>
    /// <returns>Whether the text has the shape of a secret.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    public static bool HasSecretShape(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return CloudGpuRunDocumentParser.HasKeyShape(text);
    }

    /// <summary>
    /// Whether a configured SSH value occurs in a tracked or untracked (not ignored) file of a repository, in the shape that identifies it: the host as a whole token in any letter case (host names are case-insensitive), the
    /// user only as <c>&lt;user&gt;@</c> in its own case. The pattern goes to <c>git grep -E -f -</c> on standard input, so no value is in an argument list; <c>git grep -w -F</c> would treat <c>-</c> as a word boundary and make
    /// a user name such as <c>ubuntu</c> hit <c>ubuntu-latest</c>.
    /// </summary>
    /// <param name="repositoryRoot">The folder of the git repository.</param>
    /// <param name="variable">The variable: <c>CLOUDGPU_SSH_USER</c> or <c>CLOUDGPU_SSH_HOST</c>.</param>
    /// <param name="value">The value; never echoed.</param>
    /// <returns>True when a file holds it.</returns>
    /// <exception cref="InvalidOperationException">git is not installed, or the search failed (an exit code other than 0 and 1).</exception>
    public static bool RepositoryHoldsSshValue(string repositoryRoot, string variable, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(variable);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        string escaped = string.Concat(value.Select(static character => ".[]{}()\\*+?|^$".Contains(character, StringComparison.Ordinal) ? "\\" + character : character.ToString()));
        bool isUser = string.Equals(variable, "CLOUDGPU_SSH_USER", StringComparison.Ordinal);
        string pattern = isUser
            ? "(^|[^A-Za-z0-9_.-])" + escaped + "@"
            : "(^|[^A-Za-z0-9_.-])" + escaped + "($|[^A-Za-z0-9_-])";
        string[] arguments = isUser
            ? ["grep", "--untracked", "-q", "-E", "-f", "-", "--", "."]
            : ["grep", "--untracked", "-q", "-i", "-E", "-f", "-", "--", "."];

        GitResult grep = GitRunner.RunWithInput(repositoryRoot, pattern + "\n", arguments);

        return grep.ExitCode switch
        {
            0 => true,
            1 => false,
            _ => throw new InvalidOperationException("git grep exited with " + grep.ExitCode.ToString(CultureInfo.InvariantCulture)),
        };
    }

    /// <summary>True when the <c>## cloudgpu</c> section of an endpoints document has a <c>Verified:</c> line.</summary>
    /// <param name="markdown">The text of <c>docs/endpoints.md</c>.</param>
    /// <returns>Whether the section exists with a verified date (well formed or not).</returns>
    public static bool EndpointsSectionVerified(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        return EndpointsDocParser.Parse(markdown).TryGetSection("cloudgpu", out EndpointsSection? section) && (section.Verified is not null || section.MalformedVerified is not null);
    }

    /// <summary>The checks on the <c>## cloudgpu</c> section of AJ-126 criterion 4.</summary>
    /// <param name="markdown">The text of <c>docs/endpoints.md</c>.</param>
    /// <param name="withMeasurements">True to also require the <c>Streaming usage:</c> and <c>Reasoning result:</c> lines.</param>
    /// <returns>One message per problem; empty when the section has a Verified date of 2026-10-17 or later, a <c>- model:</c> line, a <c>Command:</c> line (and the measurement lines when asked).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="markdown"/> is null.</exception>
    public static IReadOnlyList<string> EndpointsFailures(string markdown, bool withMeasurements = false)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        if (!EndpointsDocParser.Parse(markdown).TryGetSection("cloudgpu", out EndpointsSection? section))
        {
            return ["the ## cloudgpu section is absent"];
        }

        List<string> failures = [];
        if (section.Verified is not { } verified || verified < EndpointsVerifiedFrom)
        {
            failures.Add("Verified: expected a date of 2026-10-17 or later");
        }

        if (section.ModelIds.Count == 0 || section.ModelIds.Any(string.IsNullOrWhiteSpace))
        {
            failures.Add("- model: expected at least one model ID");
        }

        string body = Section(markdown, "cloudgpu") ?? string.Empty;
        string[] lines = [.. body.Split('\n')];
        if (!lines.Any(static line => line.StartsWith("Command:", StringComparison.Ordinal)))
        {
            failures.Add("Command: expected the command of the models call");
        }

        if (withMeasurements)
        {
            foreach (string prefix in new[] { "Streaming usage:", "Reasoning result:" })
            {
                if (!lines.Any(line => line.StartsWith(prefix, StringComparison.Ordinal)))
                {
                    failures.Add(prefix + " expected a measured line");
                }
            }
        }

        return failures;
    }

    private static IReadOnlyList<string> Committed(Regex name, string? repositoryRoot)
    {
        string root = repositoryRoot ?? ConfigurationFixtures.RepositoryRoot();
        return
        [
            .. LaptopResults.CommittedResultsFiles(root)
                .Where(static path => path.IndexOf('/', "results/".Length) < 0)
                .Where(path => name.IsMatch(path))
                .Select(path => Path.GetFullPath(Path.Combine(root, path)))
                .Where(File.Exists)
                .Order(StringComparer.Ordinal),
        ];
    }

    private static void AddIfEmpty(List<string> failures, JsonElement root, string member)
    {
        if (string.IsNullOrWhiteSpace(TextOf(root, member)))
        {
            failures.Add(member + ": expected a non-empty string");
        }
    }

    private static string? TextOf(JsonElement root, string member) =>
        root.TryGetProperty(member, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static JsonNode? At(JsonNode root, params string[] path)
    {
        JsonNode? node = root;
        foreach (string name in path)
        {
            node = node is JsonObject obj ? obj[name] : null;
        }

        return node;
    }

    private static string? Text(JsonNode root, params string[] path) =>
        At(root, path) is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    [GeneratedRegex(@"^results/probe-\d{8}T\d{6}Z-cloudgpu\.json\z", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ProbeName();

    [GeneratedRegex(@"^results/\d{8}T\d{6}Z-cloudgpu\.json\z", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex RunName();
}

/// <summary>The three kinds of cloudgpu evidence a gated class waits for.</summary>
public enum CloudGpuEvidenceKind
{
    /// <summary>The committed <c>results/probe-&lt;stamp&gt;-cloudgpu.json</c> (AJ-125).</summary>
    Probe = 1,

    /// <summary>The <c>## cloudgpu</c> section of <c>docs/endpoints.md</c> with a Verified line (AJ-126).</summary>
    Endpoints = 2,

    /// <summary>The committed <c>results/&lt;stamp&gt;-cloudgpu.json</c> (AJ-127).</summary>
    Run = 3,
}