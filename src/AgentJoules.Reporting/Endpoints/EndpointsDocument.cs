using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

namespace AgentJoules.Reporting.Endpoints;

/// <summary>
/// One tier section of <c>docs/endpoints.md</c> (REQ-018): what <see cref="EndpointsDocParser"/> captured between the heading <c>## &lt;tier&gt;</c> and the next <c>## </c> heading. Only three line formats are
/// read: <c>Verified: YYYY-MM-DD</c>, <c>- model: &lt;id&gt;</c> and <c>Status: &lt;text&gt;</c>; every other line is prose and is ignored.
/// </summary>
/// <param name="Name">The tier name of the heading: <c>local</c>, <c>cloudgpu</c>, <c>nvidia</c> or <c>nebius</c>.</param>
/// <param name="Verified">The date of the first well-formed <c>Verified:</c> line, or null when the section has none.</param>
/// <param name="MalformedVerified">The text after <c>Verified: </c> of the first malformed <c>Verified:</c> line when the section has no well-formed one, otherwise null; a malformed date counts as missing.</param>
/// <param name="ModelIds">The ID of every <c>- model:</c> line in file order, case preserved and trailing white space trimmed.</param>
/// <param name="Status">The text of the first <c>Status:</c> line, or null.</param>
public sealed record EndpointsSection(string Name, DateOnly? Verified, string? MalformedVerified, IReadOnlyList<string> ModelIds, string? Status)
{
    /// <summary>True when <paramref name="modelId"/> equals the ID of a model line exactly (ordinal, no normalisation).</summary>
    /// <param name="modelId">The ID to look for.</param>
    /// <returns>Whether the section lists it.</returns>
    public bool Lists(string modelId) => ModelIds.Contains(modelId, StringComparer.Ordinal);
}

/// <summary>The tier sections of <c>docs/endpoints.md</c> by tier name (REQ-018); a tier without a heading has no section.</summary>
public sealed class EndpointsDocument
{
    private readonly ReadOnlyDictionary<string, EndpointsSection> _sections;

    /// <summary>Creates the document from its sections.</summary>
    /// <param name="sections">The sections; the names must be distinct (ordinal).</param>
    /// <exception cref="ArgumentNullException"><paramref name="sections"/> is null.</exception>
    /// <exception cref="ArgumentException">Two sections have the same name.</exception>
    public EndpointsDocument(IEnumerable<EndpointsSection> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);

        var map = new Dictionary<string, EndpointsSection>(StringComparer.Ordinal);
        foreach (EndpointsSection section in sections)
        {
            if (!map.TryAdd(section.Name, section))
            {
                throw new ArgumentException("Two sections have the same name.", nameof(sections));
            }
        }

        _sections = new ReadOnlyDictionary<string, EndpointsSection>(map);
    }

    /// <summary>The sections by tier name.</summary>
    public IReadOnlyDictionary<string, EndpointsSection> Sections => _sections;

    /// <summary>Looks up the section of <paramref name="tier"/> (ordinal).</summary>
    /// <param name="tier">The tier name, for example <c>nvidia</c>.</param>
    /// <param name="section">The section when found.</param>
    /// <returns>Whether the document has a heading for the tier.</returns>
    public bool TryGetSection(string tier, [MaybeNullWhen(false)] out EndpointsSection section)
    {
        ArgumentNullException.ThrowIfNull(tier);

        if (_sections.TryGetValue(tier, out EndpointsSection? found))
        {
            section = found;
            return true;
        }

        section = null;
        return false;
    }
}