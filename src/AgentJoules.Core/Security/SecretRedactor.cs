using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using AgentJoules.Core.Labels;

namespace AgentJoules.Core.Security;

/// <summary>
/// Masks configured key values and <c>nvapi-</c> keys in any text so that no key entropy reaches a log or a console:
/// every registered value becomes <c>****</c>; <c>nvapi-</c> keys keep only the public prefix.
/// </summary>
/// <remarks>
/// Limits: a cut echo that leaves fewer than 8 characters of a key (57 or more characters of other text in front of it) can leave up to 7 characters of it in the output; only the exact
/// text of a registered value, its JSON-encoded form, its lines and its trimmed forms are masked, so a URL-encoded or base64 copy of a key is not registered. A later feature that puts a key in
/// a URL must register the <c>Uri.EscapeDataString</c> form of the key itself.
/// </remarks>
public sealed partial class SecretRedactor
{
    private const string Stars = "****";
    private const string NvidiaPrefix = "nvapi-";
    private const string MaskedNvidiaKey = "nvapi-****";
    private const int MinimumRegisteredLength = 5;
    private const int EchoLength = TextEcho.DefaultMaxLength;
    private const int MinimumCutFragmentLength = 8;

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
    /// Every form longer than 64 characters is also registered as its 64-character prefix (and, when the 64th character is the first half of a surrogate pair, as its 63-character prefix),
    /// because <see cref="TextEcho.ForMessage(string, int)"/> cuts an echoed text at 64 characters and a masking that matches only whole values would let the cut text through; the
    /// prefixes are registered before the JSON forms, so the encoded prefix is registered too. For an echo where other text came first (<c>Authorization: Bearer</c> and then the key),
    /// the form is also registered as its first <c>m</c> characters followed by the three-character cut marker <c>...</c> for every <c>m</c> from 8 to 64; those forms match only where
    /// <see cref="TextEcho.ForMessage(string, int)"/> really cut, so no other text is masked.
    /// Every registered form is also registered in the form the JSON encoder writes when it differs (a key with <c>+</c>, <c>&lt;</c>, <c>&gt;</c>, <c>&amp;</c>, an apostrophe
    /// or a non-ASCII character appears escaped in a JSON text), under the same rules.
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

        AddEchoPrefixForms(forms);
        AddJsonEncodedForms(forms);

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

    // TextEcho.ForMessage keeps the first 64 characters of an echoed text (63 when the 64th is the first half of a surrogate pair) and appends the cut marker "...", while Redact
    // masks only exact registered values, so the cut text of a registered form longer than 64 characters would pass unmasked. Two registrations cover it:
    //  - the 64- and 63-character prefix of the form, for an echo that starts with the key;
    //  - F[..m] + marker for every m from 8 to 64, for an echo where other text came first and the cut left only the first m characters of the key. These forms match only where
    //    TextEcho really cut (the marker follows), so no other text is masked; a cut that leaves fewer than 8 characters leaves at most 7 of them in the output.
    // None has a star, and each is at least 8 characters, so the registrable rules hold; a form of 64 characters or fewer is its own echo and needs none.
    private static void AddEchoPrefixForms(HashSet<string> forms)
    {
        foreach (string form in forms.ToArray())
        {
            if (form.Length <= EchoLength)
            {
                continue;
            }

            AddIfRegistrable(forms, form[..EchoLength]);
            if (char.IsHighSurrogate(form[EchoLength - 1]))
            {
                AddIfRegistrable(forms, form[..(EchoLength - 1)]);
            }

            for (int kept = MinimumCutFragmentLength; kept <= EchoLength; kept++)
            {
                AddIfRegistrable(forms, form[..kept] + TextEcho.CutMarker);
            }
        }
    }

    // The JSON encoder writes + < > & ' and every non-ASCII character as \uXXXX, so a key with one of them never matches JSON text (a results file, probe --json)
    // as it is. Each registered form is therefore registered in its encoded form too, under the same rules (length, no star, not a part of the mask).
    private static void AddJsonEncodedForms(HashSet<string> forms)
    {
        foreach (string form in forms.ToArray())
        {
            try
            {
                string encoded = JsonEncodedText.Encode(form).ToString();
                if (!string.Equals(encoded, form, StringComparison.Ordinal))
                {
                    AddIfRegistrable(forms, encoded);
                }
            }
            catch (ArgumentException)
            {
                // A form with a lone surrogate (kept for the UTF-8 round trip of a malformed value) cannot be encoded; its other forms still are.
            }
        }
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