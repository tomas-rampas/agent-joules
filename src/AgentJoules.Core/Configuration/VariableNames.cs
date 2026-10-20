namespace AgentJoules.Core.Configuration;

/// <summary>
/// The one test for the name of an environment variable that holds a key (<c>Tiers:&lt;tier&gt;:ApiKeyVariable</c>), shared by the validator that rejects a
/// bad value at start and the provider client factory that decides whether a value may be echoed in a message. The accepted shape is the one the
/// committed file uses, <c>^[A-Z_][A-Z0-9_]{0,63}$</c>: an API key (lowercase letters, digits, often a hyphen) is not a name, so a key pasted into the
/// wrong setting is never taken for one and never printed.
/// </summary>
public static class VariableNames
{
    private const int MaxLength = 64;

    /// <summary>True when <paramref name="value"/> is 1 to 64 characters, starts with an uppercase ASCII letter or an underscore, and continues with uppercase ASCII letters, digits and underscores.</summary>
    /// <param name="value">The text to test.</param>
    /// <returns>Whether it is a variable name.</returns>
    public static bool IsValid(string? value) =>
        value is { Length: > 0 and <= MaxLength }
        && (char.IsAsciiLetterUpper(value[0]) || value[0] == '_')
        && value.All(static character => char.IsAsciiLetterUpper(character) || char.IsAsciiDigit(character) || character == '_');
}