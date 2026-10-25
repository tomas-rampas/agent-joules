using System.Globalization;

using AgentJoules.Core.Cli;
using AgentJoules.Core.Configuration;

namespace AgentJoules.Reporting.Endpoints;

/// <summary>
/// Reads <c>docs/endpoints.md</c>, the record of every endpoint and model ID the configuration may use (REQ-018, design 3.5). The file is Markdown for people; the parser reads three line formats and nothing
/// else. A section starts at a line that is exactly <c>## &lt;tier&gt;</c> with the tier one of <see cref="TierOptions.KnownNames"/> (ordinal, so <c>## Local</c> is not one); any other line that starts with
/// <c>## </c> starts a section that is ignored, so the prose headings of the file do not leak their lines into a tier. Inside a tier section these lines are read, at the start of the line and with white space at its end
/// removed: <c>Verified: YYYY-MM-DD</c> (exactly that format, invariant; a malformed date counts as missing and its text is kept), <c>- model: &lt;id&gt;</c> (the ID is the text after <c>- model: </c>, case
/// preserved and not normalised in any other way) and <c>Status: &lt;text&gt;</c>. Every other line is prose. Line ends may be LF or CRLF. The parser reads text and never a path, a network or a clock.
/// </summary>
public static class EndpointsDocParser
{
    private const string HeadingPrefix = "## ";
    private const string VerifiedPrefix = "Verified: ";
    private const string ModelPrefix = "- model: ";
    private const string StatusPrefix = "Status: ";
    private const string DateFormat = "yyyy-MM-dd";

    /// <summary>Parses the text of <c>docs/endpoints.md</c>.</summary>
    /// <param name="markdown">The file content; empty text has no sections.</param>
    /// <returns>The tier sections found, one per heading.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="markdown"/> is null.</exception>
    /// <exception cref="ConfigurationException">A tier heading occurs more than once (the message names the heading; exit code 2).</exception>
    public static EndpointsDocument Parse(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        var sections = new List<EndpointsSection>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        SectionReader? current = null;

        foreach (string raw in markdown.Split('\n'))
        {
            // TrimEnd removes the carriage return of a CRLF file and the trailing white space of an ID.
            string line = raw.TrimEnd();
            if (line.StartsWith(HeadingPrefix, StringComparison.Ordinal))
            {
                if (current is not null)
                {
                    sections.Add(current.Build());
                }

                string name = line[HeadingPrefix.Length..];
                current = TierOptions.KnownNames.Contains(name, StringComparer.Ordinal) ? Start(name, seen) : null;
                continue;
            }

            current?.Read(line);
        }

        if (current is not null)
        {
            sections.Add(current.Build());
        }

        return new EndpointsDocument(sections);
    }

    private static SectionReader Start(string name, HashSet<string> seen) =>
        seen.Add(name)
            ? new SectionReader(name)
            : throw new ConfigurationException($"the endpoints document holds the heading \"{HeadingPrefix}{name}\" more than once");

    // The lines of one tier section, read one at a time.
    private sealed class SectionReader(string name)
    {
        private readonly List<string> _modelIds = [];
        private DateOnly? _verified;
        private string? _malformedVerified;
        private string? _status;

        internal void Read(string line)
        {
            if (line.StartsWith(VerifiedPrefix, StringComparison.Ordinal))
            {
                ReadVerified(line[VerifiedPrefix.Length..]);
            }
            else if (line.StartsWith(ModelPrefix, StringComparison.Ordinal))
            {
                _modelIds.Add(line[ModelPrefix.Length..]);
            }
            else if (line.StartsWith(StatusPrefix, StringComparison.Ordinal))
            {
                _status ??= line[StatusPrefix.Length..];
            }
        }

        internal EndpointsSection Build() => new(name, _verified, _verified is null ? _malformedVerified : null, _modelIds, _status);

        // The first well-formed date wins; the first malformed text is kept only to be shown when there is no well-formed date.
        private void ReadVerified(string text)
        {
            if (_verified is not null)
            {
                return;
            }

            if (DateOnly.TryParseExact(text, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date))
            {
                _verified = date;
            }
            else
            {
                _malformedVerified ??= text;
            }
        }
    }
}