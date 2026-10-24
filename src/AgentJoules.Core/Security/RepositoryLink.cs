namespace AgentJoules.Core.Security;

/// <summary>
/// The one rule for the repository link that a page prints (REQ-030 and REQ-032): the static report page and the live page build their <c>Repository README</c> link from <c>Report:RepositoryUrl</c>
/// here. The link is published as it stands, so a URL that could carry a secret is refused, and an accepted one is rebuilt from its parts.
/// </summary>
internal static class RepositoryLink
{
    private const string ReadmeFragment = "#readme";

    // The prefix pattern only: the link is checked for an nvapi- key, the keys registered with a command are not available to a page.
    private static readonly SecretRedactor KeyMask = new();

    /// <summary>
    /// The address of the link: the scheme, the host in its ASCII (punycode) form, the port and the path of <paramref name="repositoryUrl"/> followed by <c>#readme</c>, so a fragment is dropped and
    /// the text is ASCII. The message of a refusal names the rule and never the URL.
    /// </summary>
    /// <param name="repositoryUrl">The URL of the repository.</param>
    /// <returns>The address, not yet encoded for HTML.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="repositoryUrl"/> is null.</exception>
    /// <exception cref="ArgumentException">The URL is not absolute and secure-scheme, has a user name, a password or a query, or holds an <c>nvapi-</c> key.</exception>
    internal static string ReadmeHref(Uri repositoryUrl)
    {
        ArgumentNullException.ThrowIfNull(repositoryUrl);

        // The scheme of a relative URI cannot be read, so the check for an absolute URI comes first in the chain.
        string? refusal = !repositoryUrl.IsAbsoluteUri || repositoryUrl.Scheme != Uri.UriSchemeHttps || repositoryUrl.UserInfo.Length > 0
            ? "The repository URL must be an absolute https URL without a user name or a password."
            : repositoryUrl.Query.Length > 0
                ? "The repository URL must have no query: the link is published as it stands."
                : !string.Equals(KeyMask.Redact(repositoryUrl.AbsoluteUri), repositoryUrl.AbsoluteUri, StringComparison.Ordinal)
                    ? "The repository URL must not contain an nvapi- key: the link is published as it stands."
                    : null;
        if (refusal is not null)
        {
            throw new ArgumentException(refusal, nameof(repositoryUrl));
        }

        var link = new UriBuilder(Uri.UriSchemeHttps, repositoryUrl.IdnHost, repositoryUrl.IsDefaultPort ? -1 : repositoryUrl.Port, repositoryUrl.AbsolutePath);

        return link.Uri.AbsoluteUri + ReadmeFragment;
    }
}