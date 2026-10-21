using AgentJoules.Core.Labels;
using AgentJoules.Core.Security;

namespace AgentJoules.Workflow.Providers;

/// <summary>The model IDs a failure shows the user (EDGE-008): at most <see cref="ModelIdListFormatter.MaxListed"/> of them, and how many there were before the cap.</summary>
/// <param name="Listed">The IDs to print, in the order to print them: Nemotron and Qwen entries first, then the others in the order the endpoint listed them.</param>
/// <param name="Total">The number of distinct IDs the endpoint listed, before the cap.</param>
public sealed record ModelIdList(IReadOnlyList<string> Listed, int Total);

/// <summary>
/// Orders and caps the model IDs a models endpoint listed (EDGE-008, design 3.7): at most 20 are shown, every ID whose text contains <c>nemotron</c> or <c>qwen</c> (ordinal, ignoring
/// case) comes first because those are the models the project measures, and everything else keeps the order of the endpoint, so the same answer always prints the same list. Pure:
/// it holds no state and makes no request.
/// </summary>
public static class ModelIdListFormatter
{
    /// <summary>The most IDs a failure prints.</summary>
    public const int MaxListed = 20;

    private static readonly string[] PreferredFragments = ["nemotron", "qwen"];

    /// <summary>
    /// Removes duplicate IDs (ordinal, the first occurrence stays), puts the preferred IDs first (their own order kept), then the others (their order kept), and keeps the first
    /// <see cref="MaxListed"/>. The IDs are expected to be echoed already (<see cref="Echo(SecretRedactor, string)"/>); the formatter does not change a character of any of them.
    /// </summary>
    /// <param name="ids">The IDs in the order the endpoint listed them.</param>
    /// <returns>The IDs to print and the number of distinct IDs.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="ids"/> is null.</exception>
    public static ModelIdList Format(IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);

        string[] distinct = [.. ids.Distinct(StringComparer.Ordinal)];
        string[] ordered = [.. distinct.Where(IsPreferred), .. distinct.Where(static id => !IsPreferred(id))];

        return new ModelIdList([.. ordered.Take(MaxListed)], distinct.Length);
    }

    /// <summary>
    /// The form of provider or configuration text that may be compared, stored in an exception and printed: every registered key is masked first (a provider can echo a key it was sent),
    /// then the text is cut at 64 characters and every control character becomes <c>?</c> (<see cref="TextEcho.ForMessage(string, int)"/>). Both sides of the comparison of a configured ID with a
    /// listed ID go through this method, so an ID that is long or holds a control character still matches itself.
    /// </summary>
    /// <param name="redactor">The redactor that holds the key values.</param>
    /// <param name="text">The ID as the provider or the configuration wrote it.</param>
    /// <returns>The masked, cut and scrubbed text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="redactor"/> or <paramref name="text"/> is null.</exception>
    public static string Echo(SecretRedactor redactor, string text)
    {
        ArgumentNullException.ThrowIfNull(redactor);
        ArgumentNullException.ThrowIfNull(text);

        return TextEcho.ForMessage(redactor.Redact(text));
    }

    private static bool IsPreferred(string id) => PreferredFragments.Any(fragment => id.Contains(fragment, StringComparison.OrdinalIgnoreCase));
}