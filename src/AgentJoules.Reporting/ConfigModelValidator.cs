using AgentJoules.Core.Configuration;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Security;
using AgentJoules.Reporting.Endpoints;

namespace AgentJoules.Reporting;

/// <summary>
/// Checks every model ID of the configuration against the endpoints document (REQ-018, design 3.5): <c>Tiers:&lt;t&gt;:ModelId</c>, every <c>Tiers:&lt;t&gt;:AgentModelIds</c> value and <c>Judge:ModelId</c>
/// must appear as a <c>- model: &lt;id&gt;</c> line in the section of their tier, the judge ID in the section of <c>Judge:Tier</c>. The check is a comparison of two texts: no file, no HTTP and no clock.
/// </summary>
/// <remarks>
/// <para>
/// The rules run per tier in the order of <see cref="TierOptions.KnownNames"/>, for the tiers the configuration holds (a tier it does not hold needs no section), then for the judge. A tier whose section is
/// missing gives <c>Section ## &lt;tier&gt; missing</c>. A tier with <c>Enabled=false</c> needs only its section: its IDs are neither checked nor counted, which is why <c>## cloudgpu</c> may carry
/// <c>Status: pending</c> until the VM exists. An enabled tier without a valid <c>Verified:</c> date gives <c>Verified date missing in section ## &lt;tier&gt;</c> (followed by <c>Verified date malformed in section ## &lt;tier&gt;: &lt;text&gt;</c> when the section holds a <c>Verified:</c> line that does not parse), and every distinct ID it names (the model, then
/// the agent models in the order of the agent names) that its section does not list gives <c>Missing in section ## &lt;tier&gt;: &lt;id&gt;</c>. IDs are compared with <see cref="StringComparison.Ordinal"/>, so
/// <c>nemotron-3-nano:30b</c> is not <c>nemotron-3-nano:30B</c>.
/// </para>
/// <para>
/// The judge ID is looked up in the section of <c>Judge:Tier</c> only; when that tier is disabled or has no section the ID is reported missing there, because a disabled tier is not verified. The judge pair is
/// counted beside the tier pairs even when it repeats a tier ID. A (tier, ID) pair is reported once, whatever the number of places that configure it; the check is on the raw pair, before the echo. An ID is echoed through <see cref="TextEcho.ForMessage(string, int)"/> cut
/// at 256 characters, so a long or hostile ID cannot break the output and two long IDs that differ late stay two lines.
/// </para>
/// </remarks>
public static class ConfigModelValidator
{
    private const int IdEchoLength = 256;

    /// <summary>Validates the configured model IDs.</summary>
    /// <param name="options">The bound and validated options.</param>
    /// <param name="document">The parsed endpoints document.</param>
    /// <param name="redactor">The redactor of the host: every echoed text is redacted before it is cut, so a registered key inside an ID is masked whole and never shown in part.</param>
    /// <returns>The number of pairs checked and the problem lines.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/>, <paramref name="document"/> or <paramref name="redactor"/> is null.</exception>
    public static ConfigValidationResult Validate(AgentJoulesOptions options, EndpointsDocument document, SecretRedactor redactor)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(redactor);

        var problems = new List<string>();
        var seen = new HashSet<(string, string)>();
        int validated = 0;

        foreach (string name in TierOptions.KnownNames)
        {
            if (options.Tiers.TryGetValue(name, out TierOptions? tier))
            {
                validated += CheckTier(name, tier, document, redactor, problems, seen);
            }
        }

        validated += CheckJudge(options, document, redactor, problems, seen);
        return new ConfigValidationResult(validated, problems);
    }

    private static int CheckTier(string name, TierOptions tier, EndpointsDocument document, SecretRedactor redactor, List<string> problems, HashSet<(string, string)> seen)
    {
        if (!document.TryGetSection(name, out EndpointsSection? section))
        {
            Add(problems, seen, $"Section ## {name} missing");
            return 0;
        }

        if (!tier.Enabled)
        {
            return 0;
        }

        if (section.Verified is null)
        {
            Add(problems, seen, $"Verified date missing in section ## {name}");
            if (section.MalformedVerified is not null)
            {
                Add(problems, seen, $"Verified date malformed in section ## {name}: {TextEcho.ForMessage(redactor.Redact(section.MalformedVerified))}");
            }
        }

        string[] ids = IdsOf(tier);
        foreach (string id in ids.Where(id => !section.Lists(id)))
        {
            AddMissing(problems, seen, redactor, name, id);
        }

        return ids.Length;
    }

    private static int CheckJudge(AgentJoulesOptions options, EndpointsDocument document, SecretRedactor redactor, List<string> problems, HashSet<(string, string)> seen)
    {
        string? id = options.Judge.ModelId;
        if (string.IsNullOrWhiteSpace(id))
        {
            return 0;
        }

        string tierName = options.Judge.Tier ?? string.Empty;
        bool disabled = options.Tiers.TryGetValue(tierName, out TierOptions? tier) && !tier.Enabled;
        if (disabled || !document.TryGetSection(tierName, out EndpointsSection? section) || !section.Lists(id))
        {
            AddMissing(problems, seen, redactor, tierName, id);
        }

        return 1;
    }

    // The model of the tier, then the agent models by agent name; blank entries are not IDs, and an ID the tier names twice is one pair.
    private static string[] IdsOf(TierOptions tier) =>
    [
        .. new[] { tier.ModelId }
            .Concat(tier.AgentModelIds.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(static pair => pair.Value))
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Select(static id => id!)
            .Distinct(StringComparer.Ordinal),
    ];

    // One problem per raw (tier, ID) pair, decided before the echo: two IDs that differ only after the cut of the echo are two problems. The ID is redacted first and then cut to the length of a path echo, so a key that the cut would split is already masked whole.
    private static void AddMissing(List<string> problems, HashSet<(string, string)> seen, SecretRedactor redactor, string tierName, string id)
    {
        if (seen.Add((tierName, id)))
        {
            problems.Add($"Missing in section ## {TextEcho.ForMessage(redactor.Redact(tierName))}: {TextEcho.ForMessage(redactor.Redact(id), IdEchoLength)}");
        }
    }

    private static void Add(List<string> problems, HashSet<(string, string)> seen, string line)
    {
        if (seen.Add((line, string.Empty)))
        {
            problems.Add(line);
        }
    }
}