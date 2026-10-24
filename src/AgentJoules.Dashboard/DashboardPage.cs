using System.Net;
using System.Text;

using AgentJoules.Core.Security;

namespace AgentJoules.Dashboard;

/// <summary>
/// The live page (REQ-032, design 3.10): one embedded file with its own style and script, no external resource, served only by the command line and never written to <c>docs/report/</c>, whose
/// static page has no script. <see cref="Render"/> is the exact text <c>GET /</c> serves: the source with its one placeholder, the repository link, replaced. The page is ASCII, with LF newlines and
/// one final line feed, and is the same text in every culture.
/// </summary>
public static class DashboardPage
{
    /// <summary>The logical name of the embedded resource.</summary>
    public const string ResourceName = "AgentJoules.Dashboard.index.html";

    /// <summary>The sentence the footer shows instead of the link while <c>Report:RepositoryUrl</c> is not configured.</summary>
    public const string RepositoryNotConfigured = "Repository link not configured (Report:RepositoryUrl).";

    private const string Anchor = "<a id=\"repo-link\" href=\"{{repo.url}}\">Repository README</a>";
    private const string Placeholder = "{{repo.url}}";

    private static readonly Lazy<string> Source = new(ReadSource);

    /// <summary>Renders the page for the footer link.</summary>
    /// <param name="repositoryUrl">
    /// The URL of the repository (<c>Report:RepositoryUrl</c>), or null when none is configured. With a URL the footer has one link, <c>Repository README</c>, to the URL without its fragment
    /// followed by <c>#readme</c>, built from the scheme, the host in its ASCII (punycode) form, the port and the path (the rule of the static report page), and HTML-encoded; without one the anchor is
    /// replaced by <see cref="RepositoryNotConfigured"/> and the page has no link.
    /// </param>
    /// <returns>The page: ASCII, LF newlines, ending with one line feed.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="repositoryUrl"/> is not null and is not an absolute URL of the secure web scheme without a user name, a password or a query, or holds an <c>nvapi-</c> key. The message names the rule and never the URL.
    /// </exception>
    public static string Render(Uri? repositoryUrl)
    {
        string source = Source.Value;
        return repositoryUrl is null
            ? source.Replace(Anchor, RepositoryNotConfigured, StringComparison.Ordinal)
            : source.Replace(Placeholder, WebUtility.HtmlEncode(RepositoryLink.ReadmeHref(repositoryUrl)), StringComparison.Ordinal);
    }

    private static string ReadSource()
    {
        using Stream stream = typeof(DashboardPage).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The embedded resource {ResourceName} is missing.");
        using var reader = new StreamReader(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        string source = reader.ReadToEnd();

        // The source holds the anchor exactly once, and the placeholder only inside it.
        return CountOf(source, Anchor) == 1 && CountOf(source, Placeholder) == 1
            ? source
            : throw new InvalidOperationException($"The embedded resource {ResourceName} must hold the repository anchor exactly once.");
    }

    private static int CountOf(string text, string value)
    {
        int count = 0;
        for (int at = text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}