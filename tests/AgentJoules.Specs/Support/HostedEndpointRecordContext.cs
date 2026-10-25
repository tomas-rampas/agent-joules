using System.Globalization;
using System.Text.RegularExpressions;

using AgentJoules.Reporting.Endpoints;
using AgentJoules.TestSupport;

using Xunit.Sdk;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The state of one AJ-101 scenario (the nvidia and nebius endpoint verification is recorded in <c>docs/endpoints.md</c>, REQ-018), handed to the step class by Reqnroll context injection. The scenarios follow the AJ-023
/// pattern: <see cref="Open"/> evaluates <see cref="IntegrationGate.SkipReason"/> for AGENTJOULES_INTEGRATION and reads (never sets, never prints) the named key variable; when the gate is closed or the key is absent the
/// remaining steps record the reason and pass without reading anything. When it is open they assert the content of the committed <c>docs/endpoints.md</c>: the scenarios about "the builder" never perform the
/// builder's action (no call, no write), they check what the builder committed. The live comparison with <c>GET {BaseUrl}/models</c> is the gated test class <c>HostedEndpointListingTests</c>.
/// </summary>
/// <remarks>
/// Record formats the open-gate scenarios read from a tier section (lines of that section, nothing else is read):
/// <list type="bullet">
/// <item><c>Verified: YYYY-MM-DD</c>, <c>- model: &lt;id&gt;</c> and <c>Status: &lt;text&gt;</c> as the parser of AJ-100 reads them.</item>
/// <item>A command line that contains <c>Bearer ****</c> (the key is never shown).</item>
/// <item><c>Chosen: &lt;text naming the agent and the model ID&gt;</c> for each of Planner, Researcher, Writer and Reviewer; a line of this kind that names Judge is the judge choice and is not an agent choice.</item>
/// <item><c>BLOCKED: &lt;missing item&gt;</c> when no Nemotron model is listed; <c>Box exceeded: &lt;text&gt;</c> when the onboarding box ran out, with <c>Onboarding started: &lt;ISO time&gt;</c> and <c>Onboarding ended: &lt;ISO time&gt;</c>.</item>
/// <item>A line that contains the word <c>smallest</c> when the smallest listed Nemotron 3 model was chosen because the nvidia model is not listed.</item>
/// </list>
/// </remarks>
public sealed partial class HostedEndpointRecordContext
{
    /// <summary>The first UTC date a verification may carry (the build starts on 2026-10-17).</summary>
    public static readonly DateOnly EarliestVerification = new(2026, 10, 17);

    private const string DocumentRelativePath = "docs/endpoints.md";

    private string? _skipReason;

    private string? RecordedNote { get; set; }

    /// <summary>The key variable the scenario named, or null before <see cref="Open"/>.</summary>
    public string? KeyVariable { get; private set; }

    /// <summary>True when the gate is open and the named key variable has a value: the scenario asserts the committed record.</summary>
    public bool IsActive { get; private set; }

    /// <summary>The note a scenario recorded when it passed without asserting (gate closed, key absent, or the precondition of the scenario does not hold in the committed record), or null.</summary>
    public string? Note => _skipReason ?? RecordedNote;

    /// <summary>Evaluates the gate and reads the key variable.</summary>
    /// <param name="keyVariable">The variable the scenario names, for example <c>NVIDIA_API_KEY</c>.</param>
    public void Open(string keyVariable)
    {
        KeyVariable = keyVariable;
        string? gate = IntegrationGate.SkipReason(Environment.GetEnvironmentVariable(IntegrationGate.VariableName));
        bool keySet = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(keyVariable));
        _skipReason = gate ?? (keySet ? null : keyVariable + " is not set");
        IsActive = _skipReason is null;
    }

    /// <summary>Records why an open-gate scenario passed without asserting.</summary>
    /// <param name="note">The reason.</param>
    public void Skip(string note) => RecordedNote = note;

    /// <summary>The value of <see cref="KeyVariable"/> in the process environment; never printed or written.</summary>
    /// <returns>The value, or null.</returns>
    public string? KeyValue() => KeyVariable is null ? null : Environment.GetEnvironmentVariable(KeyVariable);

    /// <summary>The text of the committed <c>docs/endpoints.md</c>.</summary>
    /// <returns>The file content.</returns>
    public static string ReadCommittedDocument() => File.ReadAllText(CommittedDocumentPath());

    /// <summary>The full path of the committed <c>docs/endpoints.md</c> of the repository.</summary>
    /// <returns>The path.</returns>
    public static string CommittedDocumentPath() => Path.Combine(ConfigurationFixtures.RepositoryRoot(), DocumentRelativePath);

    /// <summary>The parsed section of <paramref name="tier"/> of the committed document.</summary>
    /// <param name="tier">The lowercase tier name.</param>
    /// <returns>The section.</returns>
    /// <exception cref="XunitException">The document has no such heading.</exception>
    public static EndpointsSection SectionOf(string tier) =>
        EndpointsDocParser.Parse(ReadCommittedDocument()).TryGetSection(tier, out EndpointsSection? section)
            ? section
            : throw new XunitException($"docs/endpoints.md has no section ## {tier}.");

    /// <summary>The lines of the section of <paramref name="tier"/> in the committed document, from the heading to the next level-two heading, trimmed at the end.</summary>
    /// <param name="tier">The lowercase tier name.</param>
    /// <returns>The lines after the heading.</returns>
    /// <exception cref="XunitException">The document has no such heading.</exception>
    public static string[] LinesOf(string tier)
    {
        var lines = new List<string>();
        bool inside = false;
        foreach (string raw in ReadCommittedDocument().Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (inside)
                {
                    break;
                }

                inside = string.Equals(line, "## " + tier, StringComparison.Ordinal);
                continue;
            }

            if (inside)
            {
                lines.Add(line);
            }
        }

        return inside ? [.. lines] : throw new XunitException($"docs/endpoints.md has no section ## {tier}.");
    }

    /// <summary>The model IDs of <paramref name="section"/> that a <c>Chosen:</c> line of <paramref name="lines"/> names, for the agent lines only (a line that names Judge is the judge choice).</summary>
    /// <param name="section">The parsed section.</param>
    /// <param name="lines">The lines of the section.</param>
    /// <returns>The distinct chosen agent model IDs, ordinal order.</returns>
    public static string[] ChosenAgentModels(EndpointsSection section, string[] lines)
    {
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(lines);

        return
        [
            .. lines
                .Where(static line => line.StartsWith("Chosen:", StringComparison.Ordinal) && !line.Contains("Judge", StringComparison.Ordinal))
                .Select(line => section.ModelIds.Where(id => line.Contains(id, StringComparison.Ordinal)).OrderByDescending(static id => id.Length).FirstOrDefault())
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal),
        ];
    }

    /// <summary>The parameter count in billions that a model ID states (for example <c>30b</c> or <c>4B</c>), or null when it states none.</summary>
    /// <param name="modelId">The model ID.</param>
    /// <returns>The size, or null.</returns>
    public static double? SizeInBillions(string modelId)
    {
        ArgumentNullException.ThrowIfNull(modelId);

        Match match = SizePattern().Match(modelId);
        return match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double size) ? size : null;
    }

    /// <summary>The time on the line of <paramref name="lines"/> that starts with <paramref name="label"/>, or null when there is none or it is not an ISO time.</summary>
    /// <param name="lines">The lines of a section.</param>
    /// <param name="label">The line start, for example <c>Onboarding started:</c>.</param>
    /// <returns>The time, or null.</returns>
    public static DateTimeOffset? TimeOf(string[] lines, string label)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(label);

        string? line = Array.Find(lines, candidate => candidate.StartsWith(label, StringComparison.Ordinal));
        return line is not null && DateTimeOffset.TryParse(line[label.Length..].Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset time) ? time : null;
    }

    [GeneratedRegex(@"(?<![A-Za-z0-9.])(\d+(?:\.\d+)?)[bB](?![A-Za-z0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex SizePattern();
}