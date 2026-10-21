using AgentJoules.Core.Security;

namespace AgentJoules.Workflow.Providers;

/// <summary>
/// The model IDs one models endpoint listed (EDGE-008), in two forms that never mix. The IDs as the provider wrote them are kept in a private ordinal set and are only compared:
/// <see cref="Lists(string)"/> answers whether a configured ID is exactly one of them, so two IDs that share their first 64 characters, or differ only in a control character, are different
/// IDs. What can be shown or put in a message is <see cref="DisplayIds"/>: each ID masked by the redactor (a provider can echo a key) and then cut and scrubbed. No raw provider text leaves
/// this object.
/// </summary>
public sealed class ModelCatalog
{
    private readonly HashSet<string> _listed;

    internal ModelCatalog(IEnumerable<string> rawIds, SecretRedactor redactor)
    {
        string[] distinct = [.. rawIds.Distinct(StringComparer.Ordinal)];
        _listed = new HashSet<string>(distinct, StringComparer.Ordinal);
        DisplayIds = [.. distinct.Select(id => ModelIdListFormatter.Echo(redactor, id)).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>The number of distinct IDs the endpoint listed (as written, so two IDs that show the same are counted twice).</summary>
    public int Count => _listed.Count;

    /// <summary>
    /// The listed IDs in the order of the endpoint, each masked, cut at 64 characters and scrubbed (<see cref="ModelIdListFormatter.Echo(SecretRedactor, string)"/>), duplicates after that
    /// removed; at most <see cref="Count"/> entries.
    /// </summary>
    public IReadOnlyList<string> DisplayIds { get; }

    /// <summary>Whether the endpoint listed exactly <paramref name="configuredId"/>: an ordinal comparison of the two texts as they were written.</summary>
    /// <param name="configuredId">The configured model ID.</param>
    /// <returns>True when it is listed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configuredId"/> is null.</exception>
    public bool Lists(string configuredId)
    {
        ArgumentNullException.ThrowIfNull(configuredId);
        return _listed.Contains(configuredId);
    }
}