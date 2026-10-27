using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using AgentJoules.Core.Cli;
using AgentJoules.Core.Configuration;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Runs;

namespace AgentJoules.Reporting.Content;

/// <summary>The latest run file of one tier as the content renderer reads it (AJ-144).</summary>
/// <param name="FileName">The name of the run file, as printed on the <c>Sources:</c> line.</param>
/// <param name="Json">The text of the run file, already checked by the caller.</param>
public sealed record ContentSource(string FileName, string Json);

/// <summary>
/// The renderer of the post template (AJ-144, REQ-026, REQ-016): no number of a post is typed by hand. It resolves <c>{{results.&lt;tier&gt;.&lt;dot.path&gt;}}</c> against the run file of that tier, where a path
/// segment is a property name, optionally followed by <c>[index]</c> array segments, and a label object (<c>{ "value", "label" }</c>) is addressed as <c>.value</c>; an optional <c>|F0</c> or <c>|F1</c> suffix formats a
/// number with the invariant culture. A value that is null renders as the label text of its object (<c>N/A</c> or <c>not-reported</c>), never as <c>0</c>, <c>null</c> or an empty string (REQ-026 as amended 2026-10-18).
/// <c>{{repo.url}}</c> is left as it is; any other <c>{{</c> that is not a well-formed placeholder is refused. The text is split into variants on the lines that start with <c>## Variant:</c>. The length of a variant is the
/// body below its <c>## Variant:</c> heading, with the whitespace-only lines trimmed at both ends, rendered, and counted in UTF-16 code units (<see cref="string.Length"/>; the content is ASCII by rule); a variant of more
/// than <see cref="MaxVariantLength"/> units is refused. The output starts with the line <c>Sources: &lt;file names&gt;</c> that lists only the files a placeholder was resolved from, in ordinal order (<c>Sources: none</c>
/// when the template holds no placeholder), is LF only and ends with one line feed; it is a pure function of the template and the run files. The template is at most <see cref="MaxTemplateBytes"/> bytes, no resolved value and no
/// section (a variant, or the text before the first one) is longer than <see cref="MaxVariantLength"/> characters (checked while the section is built), and a string of a run file is plain: printable ASCII (0x20 to 0x7E)
/// without any of <c>&lt; &gt; [ ] ( ) ! { } ` |</c>. Every failure is a <see cref="ConfigurationException"/> (exit 2) that names the line of the template and its first 16 characters, or the tier, never a value of a file.
/// </summary>
public static partial class ContentTemplateRenderer
{
    /// <summary>The longest rendered variant in UTF-16 code units (a LinkedIn post; the content is ASCII by rule, so a unit is a character).</summary>
    public const int MaxVariantLength = 1300;

    private const string VariantHeading = "## Variant:";
    private const string RepositoryPlaceholder = "{{repo.url}}";
    private const string Opening = "{{";
    private const string Closing = "}}";
    private const int EchoLength = 16;

    /// <summary>The largest template in UTF-8 bytes; a larger one is refused before anything is rendered.</summary>
    public const int MaxTemplateBytes = 64 * 1024;

    // A character that could turn a value into a link, an image, a tag, a table cell, a code span or a placeholder; with every character outside 0x20 to 0x7E it is refused in a string of a run file.
    private static readonly SearchValues<char> MarkupCharacters = SearchValues.Create("<>[]()!{}`|");
    private const string ValueMember = "value";
    private const string LabelMember = "label";
    private const string NoSources = "none";

    [GeneratedRegex(@"\{\{results\.(?<tier>[^.{}|\r\n]+)\.(?<path>[^{}|\r\n]+)(?:\|(?<format>[^{}\r\n]*))?\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"[{}]results\.|results\.[^\s{}]*[{}]", RegexOptions.CultureInvariant)]
    private static partial Regex StrayResults();

    [GeneratedRegex(@"^(?<name>[A-Za-z_][A-Za-z0-9_]*)(?<indexes>(?:\[\d{1,9}\])*)\z", RegexOptions.CultureInvariant)]
    private static partial Regex Segment();

    [GeneratedRegex(@"\[(\d+)\]", RegexOptions.CultureInvariant)]
    private static partial Regex Index();

    /// <summary>Renders the template.</summary>
    /// <param name="template">The template text; CRLF is read as LF.</param>
    /// <param name="sources">The latest completed or partial run file of each tier that has one.</param>
    /// <returns>The rendered file: the <c>Sources:</c> line, a blank line and the variants.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="template"/> or <paramref name="sources"/> is null.</exception>
    /// <exception cref="ConfigurationException">A placeholder or a tier cannot be resolved, a format is unknown, a run file is not JSON or a variant is too long.</exception>
    public static string Render(string template, IReadOnlyDictionary<Tier, ContentSource> sources)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(sources);

        if (Encoding.UTF8.GetByteCount(template) > MaxTemplateBytes)
        {
            throw new ConfigurationException(string.Create(CultureInfo.InvariantCulture, $"the template is larger than {MaxTemplateBytes} bytes"));
        }

        var state = new State(sources);
        var output = new StringBuilder();
        foreach (Section section in Sections(template.Replace("\r\n", "\n", StringComparison.Ordinal)))
        {
            RefuseMalformedHeading(section);
            RefuseMalformed(section.Body, section.BodyLine);
            string owner = section.Name is null
                ? "the text before the first variant"
                : string.Create(CultureInfo.InvariantCulture, $"the variant at line {section.HeadingLine} ({Echo(section.Name)})");
            state.Begin(section.Body, section.BodyLine, owner);
            string rendered = Placeholder().Replace(section.Body, state.Resolve);
            if (rendered.Length > MaxVariantLength)
            {
                throw new ConfigurationException(string.Create(CultureInfo.InvariantCulture, $"{owner} is {rendered.Length} characters long; the limit is {MaxVariantLength}"));
            }

            if (section.Name is null)
            {
                if (rendered.Length > 0)
                {
                    output.Append(rendered).Append("\n\n");
                }

                continue;
            }

            output.Append(section.Heading).Append('\n').Append(rendered).Append("\n\n");
        }

        string sourceLine = "Sources: " + (state.Used.Count == 0 ? NoSources : string.Join(", ", state.Used));

        return sourceLine + "\n\n" + output.ToString().TrimEnd('\n') + "\n";
    }

    // A heading is copied to the output as typed, so a placeholder (or a stray brace) in it would reach the post unresolved: refused, naming its line and the first 16 characters from the brace.
    private static void RefuseMalformedHeading(Section section)
    {
        if (section.Name is null)
        {
            return;
        }

        int at = Earliest(section.Heading.IndexOf(Opening, StringComparison.Ordinal), section.Heading.IndexOf(Closing, StringComparison.Ordinal));
        if (at >= 0)
        {
            throw new ConfigurationException(string.Create(CultureInfo.InvariantCulture, $"the template holds a malformed placeholder at line {section.HeadingLine}: {Echo(section.Heading[at..])}; a variant heading holds no placeholder"));
        }
    }

    // A "{{" that is neither a well-formed placeholder nor {{repo.url}} would reach the post as typed, with the command exiting 0; it is refused. The message names the line and the first 16 characters of the token, never more of the template.
    private static void RefuseMalformed(string body, int bodyLine)
    {
        string rest = Placeholder().Replace(body, " ").Replace(RepositoryPlaceholder, " ", StringComparison.Ordinal);
        int at = Earliest(rest.IndexOf(Opening, StringComparison.Ordinal), rest.IndexOf(Closing, StringComparison.Ordinal), StrayResults().Match(rest) is { Success: true } stray ? stray.Index : -1);
        string text = rest;
        if (at < 0)
        {
            // A well-formed placeholder with a stray brace against it ("{{{...}}}") is a typo too.
            text = body;
            List<(int Index, int Length)> spans = [.. Placeholder().Matches(body).Select(static match => (match.Index, match.Length))];
            for (int found = body.IndexOf(RepositoryPlaceholder, StringComparison.Ordinal); found >= 0; found = body.IndexOf(RepositoryPlaceholder, found + RepositoryPlaceholder.Length, StringComparison.Ordinal))
            {
                spans.Add((found, RepositoryPlaceholder.Length));
            }

            foreach ((int index, int length) in spans.OrderBy(static span => span.Index))
            {
                int before = index - 1;
                int after = index + length;
                if (before >= 0 && body[before] == '{')
                {
                    at = before;
                }
                else if (after < body.Length && body[after] == '}')
                {
                    at = index;
                }

                if (at >= 0)
                {
                    break;
                }
            }
        }

        if (at < 0)
        {
            return;
        }

        int end = text.IndexOf('\n', at);
        string token = Echo(text[at..(end < 0 ? text.Length : end)]);

        throw new ConfigurationException($"the template holds a malformed placeholder at line {LineOf(text, at, bodyLine)}: {token}; use {{{{results.<tier>.<path>}}}} or {RepositoryPlaceholder}");
    }

    // The line of the template that holds the character at the index of the text, which starts at the given line.
    private static int LineOf(string text, int index, int startLine) => startLine + text.AsSpan(0, index).Count('\n');

    private static int Earliest(params int[] positions)
    {
        int best = -1;
        foreach (int position in positions)
        {
            if (position >= 0 && (best < 0 || position < best))
            {
                best = position;
            }
        }

        return best;
    }

    // Text echoed in a message: its first 16 characters, on one line (a control or format character becomes "?"), never a whole template line. A key typed into the template is refused by the caller before this runs, so the 16 never print one.
    private static string Echo(string text) => TextEcho.ForMessage(text.TrimEnd(), EchoLength);

    /// <summary>One part of the template: the name and line of its heading (null and 0 before the first one), the heading and the body with the whitespace-only lines at both ends removed, and the line of the body's first line.</summary>
    private sealed record Section(string? Name, int HeadingLine, string Heading, string Body, int BodyLine);

    // The body of a section without the whitespace-only lines at both ends, and the line of its first line (the line after the heading when the body is empty).
    private static Section Close(string? name, int headingLine, string heading, List<string> lines)
    {
        List<string> kept = [.. lines];
        while (kept.Count > 0 && string.IsNullOrWhiteSpace(kept[^1]))
        {
            kept.RemoveAt(kept.Count - 1);
        }

        int first = 0;
        while (first < kept.Count && string.IsNullOrWhiteSpace(kept[first]))
        {
            first++;
        }

        return new Section(name, headingLine, heading, string.Join('\n', kept.Skip(first)), headingLine + 1 + first);
    }

    // The text before the first heading (name null) and each variant: its name, its heading line and its body without the whitespace-only lines around it. Lines count from 1.
    private static List<Section> Sections(string template)
    {
        List<Section> sections = [];
        string? name = null;
        string heading = string.Empty;
        int headingLine = 0;
        int number = 0;
        List<string> body = [];
        foreach (string line in template.Split('\n'))
        {
            number++;
            if (line.StartsWith(VariantHeading, StringComparison.Ordinal))
            {
                sections.Add(Close(name, headingLine, heading, body));
                name = line[VariantHeading.Length..].Trim();
                heading = line.TrimEnd();
                headingLine = number;
                body = [];
            }
            else
            {
                body.Add(line);
            }
        }

        sections.Add(Close(name, headingLine, heading, body));

        return sections;
    }

    private sealed class State(IReadOnlyDictionary<Tier, ContentSource> sources)
    {
        private readonly Dictionary<Tier, JsonNode> _documents = [];
        private readonly SortedSet<string> _used = new(StringComparer.Ordinal);

        private string _body = string.Empty;
        private int _bodyLine;
        private string _owner = string.Empty;
        private int _matched;
        private int _values;

        public SortedSet<string> Used => _used;

        // The section the next Resolve calls belong to: its body, the line of its first line and how it is named in a message.
        public void Begin(string body, int bodyLine, string owner)
        {
            _body = body;
            _bodyLine = bodyLine;
            _owner = owner;
            _matched = 0;
            _values = 0;
        }

        public string Resolve(Match match)
        {
            string text = string.Create(CultureInfo.InvariantCulture, $"at line {LineOf(_body, match.Index, _bodyLine)}: {Echo(match.Value)}");
            Tier tier = TierOf(match.Groups["tier"].Value) ?? throw new ConfigurationException($"the placeholder {text} names no tier");
            if (!sources.TryGetValue(tier, out ContentSource? source))
            {
                throw new ConfigurationException($"the placeholder {text} needs a completed or partial run file of the tier \"{TierOptions.NameOf(tier)}\", and there is none");
            }

            string? format = match.Groups["format"].Success ? match.Groups["format"].Value : null;
            if (format is not null and not ("F0" or "F1"))
            {
                throw new ConfigurationException($"the placeholder {text} has an unknown format; use F0 or F1");
            }

            string value;
            try
            {
                value = Walk(Document(tier, source), match.Groups["path"].Value, format, text);
            }
            catch (ArgumentException)
            {
                // The tree is read lazily: a duplicate property name raises an ArgumentException when the property is first read.
                throw new ConfigurationException($"the run file {source.FileName} could not be read as JSON");
            }

            if (value.Length > MaxVariantLength)
            {
                throw new ConfigurationException(string.Create(CultureInfo.InvariantCulture, $"the value of the placeholder {text} is longer than {MaxVariantLength} characters"));
            }

            // The output of the section so far (the text up to this placeholder with the values put in) is a lower bound of its final length: the section is refused as soon as it is over the limit, before the rest is built.
            _matched += match.Length;
            _values += value.Length;
            int built = match.Index + match.Length - _matched + _values;
            if (built > MaxVariantLength)
            {
                throw new ConfigurationException(string.Create(CultureInfo.InvariantCulture, $"{_owner} is at least {built} characters long; the limit is {MaxVariantLength}"));
            }

            _ = _used.Add(source.FileName);

            return value;
        }

        private static Tier? TierOf(string name)
        {
            foreach (Tier tier in Enum.GetValues<Tier>())
            {
                if (string.Equals(TierOptions.NameOf(tier), name, StringComparison.Ordinal))
                {
                    return tier;
                }
            }

            return null;
        }

        private static string Walk(JsonNode root, string path, string? format, string text)
        {
            JsonNode? node = root;
            JsonObject? parent = null;
            string last = string.Empty;
            foreach (string part in path.Split('.'))
            {
                Match segment = Segment().Match(part);
                if (!segment.Success)
                {
                    throw new ConfigurationException($"the placeholder {text} has a path segment that is not a name with [index] parts");
                }

                if (node is not JsonObject properties || !properties.TryGetPropertyValue(segment.Groups["name"].Value, out JsonNode? child))
                {
                    throw new ConfigurationException($"the path of the placeholder {text} is not in the results file");
                }

                parent = properties;
                last = segment.Groups["name"].Value;
                node = child;
                foreach (Match index in Index().Matches(segment.Groups["indexes"].Value))
                {
                    if (node is not JsonArray array || !int.TryParse(index.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int position) || position >= array.Count)
                    {
                        throw new ConfigurationException($"the path of the placeholder {text} is not in the results file");
                    }

                    parent = null;
                    node = array[position];
                }
            }

            // A label object stands for its value.
            if (node is JsonObject labelled && labelled.ContainsKey(ValueMember) && labelled.ContainsKey(LabelMember))
            {
                parent = labelled;
                last = ValueMember;
                node = labelled[ValueMember];
            }

            if (node is null)
            {
                // A null value is its label text; a null with no label beside it is not a value to print.
                return parent is not null && last == ValueMember && parent[LabelMember] is JsonValue label && label.TryGetValue(out string? labelText) && !string.IsNullOrEmpty(labelText)
                    ? PlainText(labelText, text)
                    : throw new ConfigurationException($"the placeholder {text} is null and has no label to print");
            }

            return node is JsonValue scalar
                ? Scalar(scalar, format, text)
                : throw new ConfigurationException($"the placeholder {text} does not name a single value");
        }

        private static string Scalar(JsonValue value, string? format, string text) => value.GetValueKind() switch
        {
            JsonValueKind.Number => Number(value, format, text),
            JsonValueKind.String => PlainText(value.GetValue<string>(), text),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => throw new ConfigurationException($"the placeholder {text} does not name a single value"),
        };

        // A string of a run file goes into a post as it is, so it must be plain: printable ASCII and none of the characters that make a link, an image, a tag, a table cell, a code span or a placeholder.
        private static string PlainText(string value, string text)
        {
            ReadOnlySpan<char> span = value;
            if (span.Length > MaxVariantLength)
            {
                throw new ConfigurationException(string.Create(CultureInfo.InvariantCulture, $"the value of the placeholder {text} is longer than {MaxVariantLength} characters"));
            }

            return span.ContainsAnyExceptInRange((char)0x20, (char)0x7E) || span.ContainsAny(MarkupCharacters)
                ? throw new ConfigurationException($"the value of the placeholder {text} holds a character that is not allowed in a post; only plain printable ASCII without markup characters is")
                : value;
        }

        // A number that is not a finite double (1e400) is never printed as Infinity.
        private static string Number(JsonValue value, string? format, string text)
        {
            if (!value.TryGetValue(out double number) || !double.IsFinite(number))
            {
                throw new ConfigurationException($"the placeholder {text} is not a finite number");
            }

            return format is null ? value.ToJsonString() : number.ToString(format, CultureInfo.InvariantCulture);
        }

        private JsonNode Document(Tier tier, ContentSource source)
        {
            if (_documents.TryGetValue(tier, out JsonNode? cached))
            {
                return cached;
            }

            try
            {
                JsonNode parsed = JsonNode.Parse(source.Json) ?? throw new JsonException("The file holds null.");
                _documents[tier] = parsed;

                return parsed;
            }
            catch (Exception failure) when (failure is JsonException or ArgumentException)
            {
                // A duplicate property name raises an ArgumentException.
                throw new ConfigurationException($"the run file {source.FileName} could not be read as JSON");
            }
        }
    }
}