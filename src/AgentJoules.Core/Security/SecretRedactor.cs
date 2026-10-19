using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentJoules.Core.Security;

/// <summary>
/// Masks configured key values and <c>nvapi-</c> keys in any text so that no key entropy reaches a log or a console:
/// every registered value becomes <c>****</c>; <c>nvapi-</c> keys keep only the public prefix.
/// </summary>
public sealed partial class SecretRedactor
{
    private const string Stars = "****";
    private const string NvidiaPrefix = "nvapi-";
    private const string MaskedNvidiaKey = "nvapi-****";
    private const int MinimumRegisteredLength = 5;

    private readonly Lock _gate = new();
    private readonly HashSet<string> _registered = new(StringComparer.Ordinal);
    private string[] _snapshot = [];

    /// <summary>
    /// Registers a configured secret value in up to two forms: the value with whitespace, Unicode format and control
    /// characters trimmed from both ends only (interior characters kept, so a decorated value echoed as configured is
    /// masked), and the bare value with every Unicode format and control character removed (byte order mark, zero-width
    /// space, tag characters, NUL, carriage return, line feed and similar) and then trimmed. Characters are handled as
    /// Unicode scalar values, so surrogate-pair characters are removed too. A form is registered, once, only when it is
    /// at least five characters long (short placeholders such as <c>x</c> or <c>0</c> would rewrite unrelated output),
    /// does not contain <c>*</c> (redaction would stop being idempotent) and is not a part of the <c>nvapi-****</c>
    /// mask (it would corrupt the mask).
    /// </summary>
    /// <param name="value">The secret value to mask wherever it occurs; null is ignored.</param>
    /// <returns>True when at least one form of the value is registered; otherwise false.</returns>
    public bool Register(string? value)
    {
        if (value is null)
        {
            return false;
        }

        string endsTrimmed = TrimEnds(value);
        string clean = RemoveInvisibleCharacters(value).Trim();
        bool endsTrimmedValid = IsRegistrable(endsTrimmed);
        bool cleanValid = IsRegistrable(clean);
        if (!endsTrimmedValid && !cleanValid)
        {
            return false;
        }

        lock (_gate)
        {
            bool added = endsTrimmedValid && _registered.Add(endsTrimmed);
            added |= cleanValid && _registered.Add(clean);
            if (added)
            {
                Volatile.Write(ref _snapshot, [.. _registered]);
            }
        }

        return true;
    }

    /// <summary>
    /// Replaces every <c>nvapi-</c> key and every registered value in <paramref name="text"/> with its mask in a single
    /// pass over the original text; overlapping or adjacent matches are merged and masked as one.
    /// </summary>
    /// <param name="text">The text to redact; null yields an empty string.</param>
    /// <returns>The redacted text.</returns>
    public string Redact(string? text)
    {
        if (text is null)
        {
            return string.Empty;
        }

        List<(int Start, int End)> spans = [];
        foreach (ValueMatch match in NvidiaKeyPattern().EnumerateMatches(text))
        {
            spans.Add((match.Index, match.Index + match.Length));
        }

        foreach (string value in Volatile.Read(ref _snapshot))
        {
            int from = 0;
            int hit;
            while ((hit = text.IndexOf(value, from, StringComparison.Ordinal)) >= 0)
            {
                spans.Add((hit, hit + value.Length));
                from = hit + 1;
            }
        }

        if (spans.Count == 0)
        {
            return text;
        }

        spans.Sort();

        var builder = new StringBuilder(text.Length);
        int copied = 0;
        int start = spans[0].Start;
        int end = spans[0].End;
        for (int i = 1; i <= spans.Count; i++)
        {
            if (i < spans.Count && spans[i].Start <= end)
            {
                end = Math.Max(end, spans[i].End);
                continue;
            }

            builder.Append(text, copied, start - copied);
            builder.Append(text.AsSpan(start).StartsWith(NvidiaPrefix, StringComparison.Ordinal) ? MaskedNvidiaKey : Stars);
            copied = end;
            if (i < spans.Count)
            {
                start = spans[i].Start;
                end = spans[i].End;
            }
        }

        builder.Append(text, copied, text.Length - copied);
        return builder.ToString();
    }

    private static bool IsRegistrable(string form) =>
        form.Length >= MinimumRegisteredLength
        && !form.Contains('*', StringComparison.Ordinal)
        && !MaskedNvidiaKey.Contains(form, StringComparison.Ordinal);

    private static bool IsInvisible(Rune rune) =>
        Rune.GetUnicodeCategory(rune) is UnicodeCategory.Format or UnicodeCategory.Control;

    private static string TrimEnds(string value)
    {
        Rune[] runes = [.. value.EnumerateRunes()];
        int start = 0;
        int end = runes.Length;
        while (start < end && (IsInvisible(runes[start]) || Rune.IsWhiteSpace(runes[start])))
        {
            start++;
        }

        while (end > start && (IsInvisible(runes[end - 1]) || Rune.IsWhiteSpace(runes[end - 1])))
        {
            end--;
        }

        var builder = new StringBuilder(value.Length);
        for (int i = start; i < end; i++)
        {
            builder.Append(runes[i].ToString());
        }

        return builder.ToString();
    }

    private static string RemoveInvisibleCharacters(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (Rune rune in value.EnumerateRunes())
        {
            if (!IsInvisible(rune))
            {
                builder.Append(rune.ToString());
            }
        }

        return builder.ToString();
    }

    [GeneratedRegex("nvapi-[A-Za-z0-9_-]{4,}", RegexOptions.CultureInvariant)]
    private static partial Regex NvidiaKeyPattern();
}