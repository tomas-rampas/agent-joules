using AgentJoules.TestSupport;
using AgentJoules.TestSupport.Docs;

using Xunit.Sdk;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The state of one AJ-107 scenario (select and record the local model, REQ-018, REQ-020, EDGE-009), handed to the step class by Reqnroll context injection. The scenarios about the record follow the AJ-101 and AJ-103
/// pattern: <see cref="Open"/> evaluates the gate AGENTJOULES_INTEGRATION and the committed decision of <c>docs/nim-local-attempt.md</c>; the record scenarios (the Nemotron tag, the Qwen baseline, the processor split, the
/// library check) describe the Ollama record, so they assert the <c>## local</c> section of the committed <c>docs/endpoints.md</c> only when the gate is open and the committed decision is <c>ollama</c>; otherwise
/// <see cref="Note"/> says why and they pass without asserting. They never perform the builder's action: no pull, no show, no request and no write happen here (the live check is the gated
/// <c>LocalModelListingTests</c>). The two failure scenarios (Ollama stopped, an unpulled tag) are not gated: they run the real <c>run --tier local --dry-run</c> command through the harness against a loopback fake
/// that stands where Ollama stands, so the exit codes and the fix commands are checked on every run; nothing leaves the process.
/// </summary>
public sealed class LocalModelRecordContext
{
    private const string LocalHeading = "## local";
    private const string EndpointsRelativePath = "docs/endpoints.md";

    private string? ClosedReason { get; set; }

    private string? RecordedNote { get; set; }

    /// <summary>True when the gate is open, the committed decision is <c>ollama</c> and the <c>## local</c> section exists: the scenario asserts the committed record.</summary>
    public bool IsActive { get; private set; }

    /// <summary>The note of a scenario that passed without asserting (gate closed, record absent, or a decision other than <c>ollama</c>), or null.</summary>
    public string? Note => ClosedReason ?? RecordedNote;

    /// <summary>The lines of the committed <c>## local</c> section after its heading, or an empty list when the scenario is not active.</summary>
    public IReadOnlyList<string> LocalLines { get; private set; } = [];

    /// <summary>The fake that stands where Ollama stands in a failure scenario, or null before the Given step.</summary>
    public FakeHttpMessageHandler? Endpoint { get; set; }

    /// <summary>The tag the failure scenario configures as <c>Tiers:local:ModelId</c>; <see cref="OllamaFixtures.StoryTag"/> when null.</summary>
    public string? ConfiguredTag { get; set; }

    /// <summary>The result of the last <c>run</c> command, or null before it ran.</summary>
    public CliRunResult? Result { get; private set; }

    /// <summary>The text of standard output and standard error of the last run.</summary>
    /// <exception cref="XunitException">The command has not run.</exception>
    public string Output => (Result ?? throw new XunitException("The command has not run yet.")).Stdout + "\n" + Result.Stderr;

    /// <summary>
    /// Evaluates the gate and the committed decision, then loads the <c>## local</c> section of the committed <c>docs/endpoints.md</c>. The record scenarios describe the Ollama record: a decision other than
    /// <c>ollama</c> (the committed one is <c>nim</c>) leaves them nothing to assert, and the note says so.
    /// </summary>
    public void Open()
    {
        ClosedReason = IntegrationGate.SkipReason(Environment.GetEnvironmentVariable(IntegrationGate.VariableName));
        IsActive = false;
        LocalLines = [];
        if (ClosedReason is not null)
        {
            return;
        }

        string attempt = NimAttemptDocumentParser.CommittedPath();
        if (!File.Exists(attempt))
        {
            ClosedReason = NimAttemptDocumentFactAttribute.AbsentMessage;
            return;
        }

        string? runtime = NimAttemptDocumentParser.Parse(File.ReadAllText(attempt)).DecisionRuntime;
        if (!string.Equals(runtime, "ollama", StringComparison.Ordinal))
        {
            RecordedNote = "the committed decision of docs/nim-local-attempt.md is " + (runtime ?? "none") + ", not ollama, so the Ollama record has nothing to assert";
            return;
        }

        string endpoints = Path.Combine(ConfigurationFixtures.RepositoryRoot(), EndpointsRelativePath);
        if (!File.Exists(endpoints))
        {
            ClosedReason = EndpointsRelativePath + " does not exist";
            return;
        }

        string[] lines = File.ReadAllLines(endpoints);
        int heading = Array.FindIndex(lines, static line => string.Equals(line.TrimEnd(), LocalHeading, StringComparison.Ordinal));
        if (heading < 0)
        {
            ClosedReason = EndpointsRelativePath + " has no \"## local\" section";
            return;
        }

        LocalLines = [.. lines.Skip(heading + 1).TakeWhile(static line => !line.StartsWith("## ", StringComparison.Ordinal))];
        IsActive = true;
    }

    /// <summary>Runs <paramref name="arguments"/> through the real command against <see cref="Endpoint"/> with <c>Local:Runtime</c> <c>ollama</c> and <see cref="ConfiguredTag"/>.</summary>
    /// <param name="arguments">The command line.</param>
    /// <returns>A task that completes when the command ended.</returns>
    /// <exception cref="XunitException">No Given step prepared the endpoint.</exception>
    public async Task RunAsync(string arguments)
    {
        FakeHttpMessageHandler endpoint = Endpoint ?? throw new XunitException("No Given step prepared the Ollama fake.");
        RunCommandSetup setup = OllamaFixtures.Setup(endpoint, ConfiguredTag, runtime: "ollama");
        Result = await RunCommandHarness.RunAsync(arguments, setup).WaitAsync(TimeSpan.FromSeconds(30));
    }
}