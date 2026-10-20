using System.Globalization;
using System.Text.RegularExpressions;

using AgentJoules.Core.Configuration;
using AgentJoules.Core.Runs;

namespace AgentJoules.Reporting.Results;

/// <summary>
/// The name of a run file (REQ-017, design 5.x): <c>&lt;yyyyMMddTHHmmssZ&gt;-&lt;tier&gt;[-baseline].json</c>. The timestamp is the UTC start of the run, read from
/// a <see cref="TimeProvider"/> and never from the wall clock; the <c>-baseline</c> suffix marks a run in which any agent model is not a Nemotron model, so a
/// baseline comparison can never be mistaken for the Nemotron result.
/// </summary>
public static partial class RunFileName
{
    /// <summary>The pattern every run file name matches; the committed text of REQ-017.</summary>
    public const string Pattern = "^\\d{8}T\\d{6}Z-(local|cloudgpu|nvidia|nebius)(-baseline)?\\.json$";

    private const string TimestampFormat = "yyyyMMdd'T'HHmmss'Z'";
    private const string BaselineSuffix = "-baseline";
    private const string Extension = ".json";

    /// <summary>Creates the name of a run that starts now on <paramref name="time"/>.</summary>
    /// <param name="time">The clock; its UTC time is the timestamp of the name.</param>
    /// <param name="tier">The tier of the run.</param>
    /// <param name="modelIds">The model IDs of all agents of the run; at least one.</param>
    /// <returns>The file name, without a directory.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="time"/>, <paramref name="modelIds"/> or an element of it is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tier"/> is not a defined tier.</exception>
    /// <exception cref="ArgumentException"><paramref name="modelIds"/> is empty.</exception>
    public static string Create(TimeProvider time, Tier tier, IEnumerable<string> modelIds)
    {
        ArgumentNullException.ThrowIfNull(time);

        return Create(time.GetUtcNow(), tier, modelIds);
    }

    /// <summary>Creates the name of a run that starts at <paramref name="startUtc"/>.</summary>
    /// <param name="startUtc">The start of the run; any offset, converted to UTC.</param>
    /// <param name="tier">The tier of the run.</param>
    /// <param name="modelIds">The model IDs of all agents of the run; at least one.</param>
    /// <returns>The file name, without a directory.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="modelIds"/> or an element of it is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tier"/> is not a defined tier.</exception>
    /// <exception cref="ArgumentException"><paramref name="modelIds"/> is empty.</exception>
    public static string Create(DateTimeOffset startUtc, Tier tier, IEnumerable<string> modelIds)
    {
        ArgumentNullException.ThrowIfNull(modelIds);

        if (!Enum.IsDefined(tier))
        {
            throw new ArgumentOutOfRangeException(nameof(tier), (int)tier, "The tier is not a defined tier.");
        }

        string[] models = [.. modelIds];
        if (models.Length == 0)
        {
            throw new ArgumentException("A run has at least one agent model ID.", nameof(modelIds));
        }

        string timestamp = startUtc.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);
        string suffix = IsBaseline(models) ? BaselineSuffix : string.Empty;

        return string.Create(CultureInfo.InvariantCulture, $"{timestamp}-{TierOptions.NameOf(tier)}{suffix}{Extension}");
    }

    /// <summary>Whether a run over these models is a baseline run: any model ID that does not contain <c>nemotron</c> (case-insensitive) makes it one; the rule is <see cref="ModelIdRules.RequiresBaselineSuffix"/>.</summary>
    /// <param name="modelIds">The model IDs of the agents of the run.</param>
    /// <returns>True when at least one ID lacks <c>nemotron</c>; false for an empty list.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="modelIds"/> or an element of it is null.</exception>
    public static bool IsBaseline(IEnumerable<string> modelIds) => ModelIdRules.RequiresBaselineSuffix(modelIds);

    /// <summary>Whether <paramref name="name"/> is a run file name: the whole text matches <see cref="Pattern"/> (a trailing line feed does not).</summary>
    /// <param name="name">The file name to test; null is not a run file name.</param>
    /// <returns>True when the name is a run file name.</returns>
    public static bool IsValid(string? name)
    {
        if (name is null)
        {
            return false;
        }

        // `$` also matches before a final line feed, so the match must span the whole text.
        foreach (ValueMatch match in NamePattern().EnumerateMatches(name))
        {
            return match.Index == 0 && match.Length == name.Length;
        }

        return false;
    }

    // ECMAScript makes \d mean 0-9 only; without it .NET matches the digits of every script.
    [GeneratedRegex(Pattern, RegexOptions.CultureInvariant | RegexOptions.ECMAScript)]
    private static partial Regex NamePattern();
}