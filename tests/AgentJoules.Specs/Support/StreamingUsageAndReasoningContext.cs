using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using AgentJoules.Core.Configuration;
using AgentJoules.TestSupport;
using AgentJoules.TestSupport.Docs;

using Xunit.Sdk;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The state of one AJ-108 scenario (verify the streaming usage and the reasoning toggle per tier, REQ-018, EDGE-007, handed to the step class by Reqnroll context injection). The scenarios about "the builder"
/// follow the AJ-023 and AJ-101 pattern: <see cref="Open"/> evaluates <see cref="IntegrationGate.SkipReason"/> for AGENTJOULES_INTEGRATION (and, for a hosted tier, reads, never sets and never prints, the named key variable);
/// when the gate is closed or the key is absent the remaining steps record the reason and pass. With them open the steps assert the content of the committed <c>docs/endpoints.md</c> against the shipped
/// <c>config/appsettings.json</c> and never perform the builder's action: no request is made here (the live calls are the gated <c>StreamingUsageTests</c>), and nothing is written.
/// </summary>
/// <remarks>
/// Record formats the open-gate scenarios read from a tier section of <c>docs/endpoints.md</c> (the first line that starts with the label is the record; nothing else is read):
/// <list type="bullet">
/// <item><c>Streaming usage: reported</c> or <c>Streaming usage: not-reported</c>: whether the final chunk of one streaming call made with <c>stream_options.include_usage</c> carried usage.</item>
/// <item><c>Streaming tokens: input &lt;n&gt; &lt;measured&gt;, output &lt;n&gt; &lt;measured&gt;</c> when the usage is reported: the counts of that call, copied from the measurement (the word <c>measured</c> and two whole numbers).</item>
/// <item><c>Reasoning toggle: {...}</c> (the exact JSON object merged into the request body, equal to <c>Tiers:&lt;tier&gt;:ExtraBodyJson</c>), or <c>Reasoning toggle: system message: &lt;text&gt;</c> (equal to <c>Tiers:&lt;tier&gt;:SystemPromptPrefix</c>), or <c>Reasoning toggle: unsupported</c>.</item>
/// <item><c>Reasoning result: off</c>, <c>on</c> or <c>unsupported</c>, equal to <c>Tiers:&lt;tier&gt;:ReasoningMode</c>.</item>
/// <item><c>Reasoning evidence: &lt;text&gt;</c>: the same short prompt without and with the toggle, compared for a reasoning member or a think block; for an endpoint that rejected the field, the error excerpt (for example <c>HTTP 400</c>), never a key.</item>
/// </list>
/// </remarks>
public sealed partial class StreamingUsageAndReasoningContext
{
    /// <summary>The label of the usage line.</summary>
    public const string UsageLabel = "Streaming usage:";

    /// <summary>The label of the token counts line.</summary>
    public const string TokensLabel = "Streaming tokens:";

    /// <summary>The label of the toggle line.</summary>
    public const string ToggleLabel = "Reasoning toggle:";

    /// <summary>The label of the result line.</summary>
    public const string ResultLabel = "Reasoning result:";

    /// <summary>The label of the evidence line.</summary>
    public const string EvidenceLabel = "Reasoning evidence:";

    /// <summary>The prefix of a toggle that is a system message.</summary>
    public const string SystemMessagePrefix = "system message:";

    private const string BuildLogRelativePath = "docs/build-log.md";

    private string? _skipReason;

    private string? RecordedNote { get; set; }

    /// <summary>The key variable the scenario named, or null.</summary>
    public string? KeyVariable { get; private set; }

    /// <summary>True when the gate is open and the named key variable (if any) has a value: the scenario asserts the committed record.</summary>
    public bool IsActive { get; private set; }

    /// <summary>The note a scenario recorded when it passed without asserting (gate closed, key absent, or the precondition of the scenario does not hold in the committed record), or null.</summary>
    public string? Note => _skipReason ?? RecordedNote;

    /// <summary>Evaluates the gate and, when <paramref name="keyVariable"/> is given, reads (never sets, never prints) the key variable.</summary>
    /// <param name="keyVariable">The variable the scenario names, for example <c>NVIDIA_API_KEY</c>, or null for a scenario that needs no key.</param>
    public void Open(string? keyVariable)
    {
        KeyVariable = keyVariable;
        string? gate = IntegrationGate.SkipReason(Environment.GetEnvironmentVariable(IntegrationGate.VariableName));
        bool keySet = keyVariable is null || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(keyVariable));
        _skipReason = gate ?? (keySet ? null : keyVariable + " is not set");
        IsActive = _skipReason is null;
    }

    /// <summary>Opens the scenario of the Ollama branch: the gate must be open and the committed decision of <c>docs/nim-local-attempt.md</c> must be <c>ollama</c>; otherwise the note says why and the scenario passes without asserting.</summary>
    public void OpenOllamaBranch()
    {
        Open(keyVariable: null);
        if (!IsActive)
        {
            return;
        }

        string attempt = NimAttemptDocumentParser.CommittedPath();
        string? runtime = File.Exists(attempt) ? NimAttemptDocumentParser.Parse(File.ReadAllText(attempt)).DecisionRuntime : null;
        if (!string.Equals(runtime, "ollama", StringComparison.Ordinal))
        {
            Skip("the committed decision of docs/nim-local-attempt.md is " + (runtime ?? "none") + ", not ollama, so the Ollama branch has nothing to assert");
        }
    }

    /// <summary>Records why an open-gate scenario passed without asserting.</summary>
    /// <param name="note">The reason.</param>
    public void Skip(string note)
    {
        RecordedNote = note;
        IsActive = false;
    }

    /// <summary>The value of <see cref="KeyVariable"/> in the process environment; never printed or written.</summary>
    /// <returns>The value, or null.</returns>
    public string? KeyValue() => KeyVariable is null ? null : Environment.GetEnvironmentVariable(KeyVariable);

    /// <summary>The text of the committed <c>docs/build-log.md</c>, or null when the file does not exist.</summary>
    /// <returns>The text, or null.</returns>
    public static string? ReadBuildLog()
    {
        string path = Path.Combine(ConfigurationFixtures.RepositoryRoot(), BuildLogRelativePath);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    /// <summary>The lines of the section of <paramref name="tier"/> in the committed <c>docs/endpoints.md</c>, or null when the document has no such section.</summary>
    /// <param name="tier">The lowercase tier name.</param>
    /// <returns>The lines, or null.</returns>
    public static string[]? TryLinesOf(string tier)
    {
        try
        {
            return HostedEndpointRecordContext.LinesOf(tier);
        }
        catch (XunitException)
        {
            return null;
        }
    }

    /// <summary>The text after <paramref name="label"/> on the first line of <paramref name="lines"/> that starts with it, trimmed, or null when there is none.</summary>
    /// <param name="lines">The lines of a section.</param>
    /// <param name="label">The label, for example <see cref="UsageLabel"/>.</param>
    /// <returns>The value, or null.</returns>
    public static string? ValueOf(IReadOnlyList<string> lines, string label)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(label);

        string? line = lines.FirstOrDefault(candidate => candidate.StartsWith(label, StringComparison.Ordinal));
        return line?[label.Length..].Trim();
    }

    /// <summary>Checks the usage record of a section: a well-formed usage line and, when the usage is reported, a tokens line with two whole numbers marked measured.</summary>
    /// <param name="tier">The tier name, for the failure message.</param>
    /// <param name="lines">The lines of the section.</param>
    /// <returns>The usage word, <c>reported</c> or <c>not-reported</c>.</returns>
    /// <exception cref="XunitException">A line is absent or malformed.</exception>
    public static string AssertUsageRecord(string tier, IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        string usage = ValueOf(lines, UsageLabel) ?? throw new XunitException($"The section ## {tier} has no \"{UsageLabel}\" line.");
        if (usage is not ("reported" or "not-reported"))
        {
            throw new XunitException($"The line \"{UsageLabel}\" of ## {tier} must be reported or not-reported.");
        }

        if (usage == "reported")
        {
            string tokens = ValueOf(lines, TokensLabel) ?? throw new XunitException($"The section ## {tier} reports the usage but has no \"{TokensLabel}\" line.");
            if (!TokensPattern().IsMatch(tokens) || !tokens.Contains("measured", StringComparison.Ordinal))
            {
                throw new XunitException($"The line \"{TokensLabel}\" of ## {tier} must give the input and output counts as measured whole numbers.");
            }
        }

        return usage;
    }

    /// <summary>
    /// Checks the reasoning record of a section against the configuration of the tier: a toggle line equal to the configured <c>ExtraBodyJson</c> (or <c>SystemPromptPrefix</c>, or <c>unsupported</c>), a result line equal to
    /// the configured <c>ReasoningMode</c>, and an evidence line.
    /// </summary>
    /// <param name="tier">The tier name.</param>
    /// <param name="lines">The lines of the section.</param>
    /// <param name="configured">The options of the tier in the shipped configuration.</param>
    /// <returns>The result word of the record: <c>off</c>, <c>on</c> or <c>unsupported</c>.</returns>
    /// <exception cref="XunitException">A line is absent, malformed, or disagrees with the configuration.</exception>
    public static string AssertReasoningRecord(string tier, IReadOnlyList<string> lines, TierOptions configured)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(configured);

        string toggle = ValueOf(lines, ToggleLabel) ?? throw new XunitException($"The section ## {tier} has no \"{ToggleLabel}\" line.");
        string result = ValueOf(lines, ResultLabel) ?? throw new XunitException($"The section ## {tier} has no \"{ResultLabel}\" line.");
        if (!TierOptions.ReasoningModes.Contains(result, StringComparer.Ordinal))
        {
            throw new XunitException($"The line \"{ResultLabel}\" of ## {tier} must be off, on or unsupported.");
        }

        if (string.IsNullOrWhiteSpace(ValueOf(lines, EvidenceLabel)))
        {
            throw new XunitException($"The section ## {tier} has no \"{EvidenceLabel}\" line with text.");
        }

        if (!string.Equals(result, configured.ReasoningMode, StringComparison.Ordinal))
        {
            throw new XunitException($"The record of ## {tier} says {ResultLabel} {result} but Tiers:{tier}:ReasoningMode is {configured.ReasoningMode}.");
        }

        if (toggle.StartsWith('{'))
        {
            bool recorded = ConfigurationJson.TryParseObject(toggle, out JsonObject? recordedObject);
            bool set = configured.ExtraBodyJson is not null && ConfigurationJson.TryParseObject(configured.ExtraBodyJson, out _);
            if (!recorded || !set || !JsonNode.DeepEquals(recordedObject, JsonNode.Parse(configured.ExtraBodyJson!)))
            {
                throw new XunitException($"The toggle of ## {tier} is not the JSON object of Tiers:{tier}:ExtraBodyJson.");
            }
        }
        else if (toggle.StartsWith(SystemMessagePrefix, StringComparison.Ordinal))
        {
            string text = toggle[SystemMessagePrefix.Length..].Trim();
            if (text.Length == 0 || !string.Equals(text, configured.SystemPromptPrefix?.Trim(), StringComparison.Ordinal))
            {
                throw new XunitException($"The toggle of ## {tier} is not the text of Tiers:{tier}:SystemPromptPrefix.");
            }
        }
        else if (!string.Equals(toggle, "unsupported", StringComparison.Ordinal) || !string.Equals(result, "unsupported", StringComparison.Ordinal))
        {
            throw new XunitException($"The toggle of ## {tier} must be a JSON object, \"{SystemMessagePrefix} <text>\" or unsupported (and unsupported only with the result unsupported).");
        }

        return result;
    }

    [GeneratedRegex(@"^input\D*\d+\D+output\D*\d+", RegexOptions.CultureInvariant)]
    private static partial Regex TokensPattern();
}