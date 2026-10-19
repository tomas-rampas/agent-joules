using System.Buffers;
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

    private static readonly char[] LineTerminators =
        ['\r', '\n', '\v', '\f', (char)0x0085, (char)0x2028, (char)0x2029];

    private readonly Lock _gate = new();
    private readonly HashSet<string> _registered = new(StringComparer.Ordinal);
    private string[] _snapshot = [];

    /// <summary>
    /// Registers a configured secret value as a whole and line by line: the whole value is registered, and the value
    /// is also split on CR, LF and the other Unicode line separators (empty segments dropped) with every segment
    /// registered on its own, because a key may sit on one line of a multi-line value and the line-buffered writer
    /// may see one line at a time, while a single write of several lines is redacted as one string. Every line of
    /// five or more characters of a multi-line value is therefore masked as well, which errs on the safe side (for
    /// example a JSON <c>"type"</c> line of a service-account file). Each text (the whole value, each segment) is
    /// registered in up to three forms: the text with whitespace, Unicode format and control characters trimmed from
    /// both ends only (a slice of the original string; interior characters kept, so a decorated value echoed as
    /// configured is masked); the bare text with every Unicode format, control and line-separator character removed
    /// (byte order mark, zero-width space, tag characters, line breaks, NUL and similar) and then trimmed; and the
    /// ends-trimmed text with every lone
    /// surrogate replaced by U+FFFD (what a UTF-8 round trip of a malformed value yields). Characters are handled as
    /// Unicode scalar values, so surrogate-pair characters are removed too; a lone surrogate counts as a visible
    /// character in the first form and is dropped from the second. A form is registered, once, only when it is at
    /// least five characters long (short placeholders such as <c>x</c> or <c>0</c> would rewrite unrelated output),
    /// does not contain <c>*</c> (redaction would stop being idempotent) and is not a part of the <c>nvapi-****</c>
    /// mask (it would corrupt the mask).
    /// </summary>
    /// <param name="value">The secret value to mask wherever it occurs; null is ignored.</param>
    /// <returns>True when at least one form of the value or of one of its lines is registrable.</returns>
    public bool Register(string? value)
    {
        if (value is null)
        {
            return false;
        }

        HashSet<string> forms = new(StringComparer.Ordinal);
        AddForms(forms, value);
        foreach (string segment in value.Split(LineTerminators, StringSplitOptions.RemoveEmptyEntries))
        {
            AddForms(forms, segment);
        }

        if (forms.Count == 0)
        {
            return false;
        }

        lock (_gate)
        {
            if (_registered.IsSupersetOf(forms))
            {
                return true;
            }

            _registered.UnionWith(forms);
            Volatile.Write(ref _snapshot, [.. _registered]);
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
            bool isNvidiaKey = text.AsSpan(start).StartsWith(NvidiaPrefix, StringComparison.Ordinal);
            builder.Append(isNvidiaKey ? MaskedNvidiaKey : Stars);
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

    private static void AddForms(HashSet<string> forms, string text)
    {
        string endsTrimmed = TrimEnds(text);
        AddIfRegistrable(forms, endsTrimmed);
        AddIfRegistrable(forms, RemoveInvisibleCharacters(text).Trim());
        AddIfRegistrable(forms, string.Concat(endsTrimmed.EnumerateRunes()));
    }

    private static void AddIfRegistrable(HashSet<string> forms, string form)
    {
        if (IsRegistrable(form))
        {
            forms.Add(form);
        }
    }

    private static bool IsRegistrable(string form) =>
        form.Length >= MinimumRegisteredLength
        && !form.Contains('*', StringComparison.Ordinal)
        && !MaskedNvidiaKey.Contains(form, StringComparison.Ordinal);

    private static bool IsLineTerminator(Rune rune) =>
        rune.IsBmp && Array.IndexOf(LineTerminators, (char)rune.Value) >= 0;

    private static bool IsInvisible(Rune rune) =>
        Rune.GetUnicodeCategory(rune) is UnicodeCategory.Format or UnicodeCategory.Control;

    private static string TrimEnds(string value)
    {
        int start = 0;
        while (start < value.Length && IsTrimmable(value.AsSpan(start), out int consumed))
        {
            start += consumed;
        }

        int end = value.Length;
        while (end > start && IsTrimmableLast(value.AsSpan(start, end - start), out int consumed))
        {
            end -= consumed;
        }

        return value[start..end];
    }

    private static bool IsTrimmable(ReadOnlySpan<char> text, out int consumed)
    {
        if (Rune.DecodeFromUtf16(text, out Rune rune, out consumed) != OperationStatus.Done)
        {
            consumed = 0;
            return false;
        }

        return IsInvisible(rune) || Rune.IsWhiteSpace(rune);
    }

    private static bool IsTrimmableLast(ReadOnlySpan<char> text, out int consumed)
    {
        if (Rune.DecodeLastFromUtf16(text, out Rune rune, out consumed) != OperationStatus.Done)
        {
            consumed = 0;
            return false;
        }

        return IsInvisible(rune) || Rune.IsWhiteSpace(rune);
    }

    private static string RemoveInvisibleCharacters(string value)
    {
        var builder = new StringBuilder(value.Length);
        int index = 0;
        while (index < value.Length)
        {
            if (Rune.DecodeFromUtf16(value.AsSpan(index), out Rune rune, out int consumed) != OperationStatus.Done)
            {
                index++;
                continue;
            }

            if (!IsInvisible(rune) && !IsLineTerminator(rune))
            {
                builder.Append(value, index, consumed);
            }

            index += consumed;
        }

        return builder.ToString();
    }

    [GeneratedRegex("nvapi-[A-Za-z0-9_-]{4,}", RegexOptions.CultureInvariant)]
    private static partial Regex NvidiaKeyPattern();
}