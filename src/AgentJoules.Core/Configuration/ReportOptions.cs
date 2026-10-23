using System.Diagnostics.CodeAnalysis;

using AgentJoules.Core.Security;

namespace AgentJoules.Core.Configuration;

/// <summary>
/// The <c>Report</c> section of <c>AgentJoules</c> (AJ-086, REQ-030, design 3.8): the settings of the <c>report</c> command. Today it holds the repository URL the HTML page links to.
/// </summary>
/// <remarks>A mutable class with plain get and set accessors so that the configuration binding generator binds it.</remarks>
public sealed class ReportOptions
{
    /// <summary>The configuration key of <see cref="RepositoryUrl"/>, relative to the root section; messages name the key and never the value.</summary>
    public const string RepositoryUrlKey = "Report:RepositoryUrl";

    /// <summary>The failure text for a <see cref="RepositoryUrl"/> that breaks the rule of <see cref="IsRepositoryUrl"/>; it names the key and never shows the value.</summary>
    public const string RepositoryUrlMessage = "Report:RepositoryUrl must be an absolute https URL without user info, query or fragment, secret or surrounding space; the value is not shown";

    private static readonly SecretRedactor KeyPatterns = new();

    /// <summary>
    /// Key <c>Report:RepositoryUrl</c>, default none: the address of the repository whose README the page links to. When set it is an absolute https URL without user information, query or fragment; when absent
    /// the page prints a sentence instead of a link.
    /// </summary>
    public string? RepositoryUrl { get; set; }

    /// <summary>
    /// The rule for a configured <see cref="RepositoryUrl"/>: an absolute <c>https</c> URL without user information, query or fragment, without leading or trailing white space, and that the secret redactor leaves
    /// as it is (an <c>nvapi-</c> key in the path is refused). A query or fragment marker with nothing after it (<c>?</c>, <c>#</c>) counts, so the text is checked as well as the parsed URI. The rule governs everywhere:
    /// the page renderer applies the same checks as its second line of defence.
    /// </summary>
    /// <param name="value">The configured text.</param>
    /// <param name="uri">The URL when the rule holds; otherwise null.</param>
    /// <returns>True when <paramref name="value"/> is an acceptable repository URL.</returns>
    public static bool IsRepositoryUrl(string? value, [NotNullWhen(true)] out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value.Contains('?', StringComparison.Ordinal)
            || value.Contains('#', StringComparison.Ordinal)
            || !Uri.TryCreate(value, UriKind.Absolute, out Uri? parsed)
            || parsed.Scheme != Uri.UriSchemeHttps
            || parsed.UserInfo.Length > 0
            || parsed.Host.Length == 0
            || !string.Equals(KeyPatterns.Redact(value), value, StringComparison.Ordinal)
            || !string.Equals(KeyPatterns.Redact(parsed.AbsoluteUri), parsed.AbsoluteUri, StringComparison.Ordinal))
        {
            return false;
        }

        uri = parsed;
        return true;
    }
}