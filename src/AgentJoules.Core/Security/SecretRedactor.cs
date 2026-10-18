using System.Text.RegularExpressions;

namespace AgentJoules.Core.Security;

/// <summary>
/// Masks configured key values and <c>nvapi-</c> keys in any text so that no key entropy reaches a log or a console:
/// every registered value becomes <c>****</c>; <c>nvapi-</c> keys keep only the public prefix.
/// </summary>
public sealed partial class SecretRedactor
{
    private const string Stars = "****";
    private const string MaskedNvidiaKey = "nvapi-****";

    private readonly Lock _gate = new();
    private readonly HashSet<string> _registered = new(StringComparer.Ordinal);
    private string[] _longestFirst = [];

    /// <summary>
    /// Registers a configured secret value. Null, empty and whitespace values are ignored; a value is stored once.
    /// When the value has leading or trailing whitespace (for example a carriage return from a CRLF file), the trimmed
    /// value is registered as well so the bare key cannot leak.
    /// </summary>
    /// <param name="value">The secret value to mask wherever it occurs.</param>
    public void Register(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        string trimmed = value.Trim();

        lock (_gate)
        {
            bool added = _registered.Add(value);
            if (!string.Equals(trimmed, value, StringComparison.Ordinal))
            {
                added |= _registered.Add(trimmed);
            }

            if (!added)
            {
                return;
            }

            string[] snapshot = [.. _registered];
            Array.Sort(snapshot, CompareLongestFirst);
            Volatile.Write(ref _longestFirst, snapshot);
        }
    }

    /// <summary>
    /// Replaces every <c>nvapi-</c> key and every registered value in <paramref name="text"/> with its mask.
    /// </summary>
    /// <param name="text">The text to redact; null yields an empty string.</param>
    /// <returns>The redacted text.</returns>
    public string Redact(string? text)
    {
        if (text is null)
        {
            return string.Empty;
        }

        string result = NvidiaKeyPattern().Replace(text, MaskedNvidiaKey);
        foreach (string value in Volatile.Read(ref _longestFirst))
        {
            result = result.Replace(value, Stars, StringComparison.Ordinal);
        }

        return result;
    }

    private static int CompareLongestFirst(string left, string right)
    {
        int byLength = right.Length.CompareTo(left.Length);
        return byLength != 0 ? byLength : string.CompareOrdinal(left, right);
    }

    [GeneratedRegex("nvapi-[A-Za-z0-9_-]{4,}", RegexOptions.CultureInvariant)]
    private static partial Regex NvidiaKeyPattern();
}