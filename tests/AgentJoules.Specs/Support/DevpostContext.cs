using System.Net;

using AgentJoules.TestSupport;
using AgentJoules.TestSupport.Docs;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The state of one Devpost scenario (AJ-138 to AJ-142, REQ-028), handed to the step classes by Reqnroll context injection: the text under test (the committed file or a mutated copy), the second texts a check compares it
/// with, the failures of the last check and the cloudgpu state the scenario models. The committed files are only read, through <see cref="DevpostCommitted"/>; the git reads of the pre-fill versions go through
/// <see cref="PreFillFiles"/>. The scenario of the live rules page follows the AJ-111 pattern: <see cref="Open"/> evaluates <see cref="IntegrationGate.SkipReason"/>, and a request over the real handler is made only with the
/// gate open; a scenario that needs a fake handler gets one that answers a fixed status and never touches the network.
/// </summary>
public sealed class DevpostContext : IDisposable
{
    private readonly List<HttpClient> _clients = [];

    /// <summary>The document under test: the committed text or a mutated copy.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>The checklist, when the scenario also needs it next to another document.</summary>
    public string Checklist { get; set; } = string.Empty;

    /// <summary>The pre-fill version of the document, or null when git could not read it.</summary>
    public string? PreFill { get; set; }

    /// <summary>The text of <c>docs/build-log.md</c> the scenario checks against.</summary>
    public string BuildLog { get; set; } = string.Empty;

    /// <summary>The pitch file text the scenario checks against.</summary>
    public string Pitch { get; set; } = string.Empty;

    /// <summary>The failures of the last check.</summary>
    public IReadOnlyList<string> Failures { get; set; } = [];

    /// <summary>The lines of a section the When step read.</summary>
    public IReadOnlyList<string> Section { get; set; } = [];

    /// <summary>The fenced blocks the When step extracted.</summary>
    public IReadOnlyList<LabelledBlock> Blocks { get; set; } = [];

    /// <summary>Whether the scenario models a completed or partial cloudgpu run file.</summary>
    public bool CloudGpuRan { get; set; } = true;

    /// <summary>The cloudgpu state a pitch scenario evaluates.</summary>
    public CloudGpuTierState State { get; set; } = new(CloudGpuTaglineRule.Absent, null, null, null);

    /// <summary>The pitch the rule selected.</summary>
    public string Chosen { get; set; } = string.Empty;

    /// <summary>The HTTP status the rules page answered.</summary>
    public int Status { get; set; }

    /// <summary>True when the gate is open and the scenario makes the real request.</summary>
    public bool IsActive { get; private set; }

    /// <summary>Why a scenario passed without asserting (the closed integration gate), or null.</summary>
    public string? Note { get; set; }

    /// <summary>Evaluates the integration gate.</summary>
    public void Open()
    {
        Note = IntegrationGate.SkipReason(Environment.GetEnvironmentVariable(IntegrationGate.VariableName));
        IsActive = Note is null;
    }

    /// <summary>A client over the real handler, for the open gate only.</summary>
    /// <returns>The client, disposed with the context.</returns>
    public HttpClient RealClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("agent-joules-rules-check");
        _clients.Add(client);

        return client;
    }

    /// <summary>A client whose handler answers every request with the given status and never touches the network.</summary>
    /// <param name="status">The HTTP status.</param>
    /// <returns>The client, disposed with the context.</returns>
    public HttpClient FakeClient(HttpStatusCode status)
    {
        var client = new HttpClient(new StatusHandler(status));
        _clients.Add(client);

        return client;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (HttpClient client in _clients)
        {
            client.Dispose();
        }
    }

    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { RequestMessage = request });
    }
}

/// <summary>Read-only access to the committed texts and facts the Devpost scenarios start from.</summary>
public static class DevpostCommitted
{
    /// <summary>The committed checklist.</summary>
    /// <returns>The text.</returns>
    public static string Checklist() => RepositoryRoot.ReadAllText(DocumentChecks.ChecklistPath);

    /// <summary>The committed story.</summary>
    /// <returns>The text.</returns>
    public static string Story() => RepositoryRoot.ReadAllText(DocumentChecks.StoryPath);

    /// <summary>The committed feedback file.</summary>
    /// <returns>The text.</returns>
    public static string Feedback() => RepositoryRoot.ReadAllText(DocumentChecks.FeedbackPath);

    /// <summary>The committed pitch file.</summary>
    /// <returns>The text.</returns>
    public static string Pitch() => RepositoryRoot.ReadAllText("docs/devpost-pitch.md");

    /// <summary>The committed build log.</summary>
    /// <returns>The text.</returns>
    public static string BuildLog() => RepositoryRoot.ReadAllText("docs/build-log.md");

    /// <summary>The committed README.</summary>
    /// <returns>The text.</returns>
    public static string Readme() => RepositoryRoot.ReadAllText("README.md");

    /// <summary>The distinct <c>steps.agent</c> values of the newest local run file.</summary>
    /// <returns>The names, empty when no local run file exists.</returns>
    public static IReadOnlyList<string> LatestLocalAgents()
    {
        string? text = RepositoryFiles.LatestRunText("local");
        if (text is null)
        {
            return [];
        }

        using var document = System.Text.Json.JsonDocument.Parse(text);

        return [.. document.RootElement.GetProperty("steps").EnumerateArray().Select(static step => step.GetProperty("agent").GetString() ?? string.Empty).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>Whether a completed or partial cloudgpu run file is committed.</summary>
    /// <returns>True when the tagline rule finds one.</returns>
    public static bool CloudGpuRan() => DocumentChecks.CloudGpuRan(RepositoryFiles.ResultsFileNames(), static name => RepositoryRoot.ReadAllText("results/" + name));

    /// <summary>Whether a baseline run file (<c>-baseline.json</c>) is committed.</summary>
    /// <returns>True when one exists.</returns>
    public static bool BaselineRunExists() => RepositoryFiles.ResultsFileNames().Any(static name => name.EndsWith("-baseline.json", StringComparison.Ordinal));

    /// <summary>The checks of the checklist on a text, against the committed README and files.</summary>
    /// <param name="checklist">The checklist text.</param>
    /// <param name="cloudGpuRan">Whether the scenario models a cloudgpu run file.</param>
    /// <returns>The failures.</returns>
    public static IReadOnlyList<string> ChecklistFailures(string checklist, bool cloudGpuRan) =>
        DocumentChecks.ChecklistChecks(checklist, Readme(), RepositoryFiles.Exists, cloudGpuRan);
}