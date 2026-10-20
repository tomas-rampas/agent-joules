using System.Globalization;
using System.Reflection;

using AgentJoules.Core.Labels;

using Microsoft.Extensions.Configuration;

namespace AgentJoules.Core.Configuration;

/// <summary>
/// Checks what the configuration binder would accept silently or explain badly: keys the options do not have, values that are set but
/// empty, a value where a section belongs, and values of the wrong type. No line ever contains a configured value; a key name comes from
/// the configuration sources, so it is passed through <see cref="TextEcho.ForMessage"/>.
/// </summary>
internal static class ConfigurationShape
{
    private enum Kind
    {
        Integer,
        Number,
        Boolean,
    }

    // The scalar keys that can fail to convert, in the order of the options, with the parse rules of the binder.
    private static readonly (string Key, Kind Kind)[] TypedKeys =
    [
        ("Gpu:Index", Kind.Integer),
        ("Meter:SampleRateHz", Kind.Number),
        ("Meter:AllowBelowMinRate", Kind.Boolean),
        ("Baseline:Seconds", Kind.Number),
        ("Baseline:MaxCoefficientOfVariation", Kind.Number),
    ];

    /// <summary>Lists, one line each, every key under <paramref name="root"/> that is unknown, empty, or the wrong shape (a value for a section or a section for a value).</summary>
    internal static List<string> ShapeProblems(IConfigurationSection root)
    {
        List<string> problems = [];

        // A value on the root key itself, with or without keys beside it, is a mistake the binder would ignore; the keys under it are still checked.
        if (root.Value is not null)
        {
            problems.Add($"{AgentJoulesOptions.SectionName} is a section and must hold keys, not a value; the value is not shown");
        }

        Walk(root, typeof(AgentJoulesOptions), string.Empty, problems);
        return problems;
    }

    /// <summary>The keys, relative to the root section, whose values <see cref="TypeProblems"/> re-checks; every non-string scalar option must be listed.</summary>
    internal static IEnumerable<string> TypedKeyNames => TypedKeys.Select(typed => typed.Key);

    /// <summary>Lists, one line each, every typed key under <paramref name="root"/> whose value the binder cannot convert; the value is not shown.</summary>
    internal static List<string> TypeProblems(IConfigurationSection root)
    {
        List<string> problems = [];
        foreach ((string key, Kind kind) in TypedKeys)
        {
            string? value = root[key];
            if (!string.IsNullOrEmpty(value) && !Converts(value, kind))
            {
                problems.Add($"{key} must be {Describe(kind)}; the value is not shown");
            }
        }

        return problems;
    }

    private static void Walk(IConfiguration node, Type type, string prefix, List<string> problems)
    {
        foreach (IConfigurationSection child in node.GetChildren())
        {
            string key = prefix + Segment(child.Key);
            PropertyInfo? property = type.GetProperty(child.Key, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            bool hasChildren = child.GetChildren().Any();

            if (property is null)
            {
                problems.Add($"{key} is not a known option; check the spelling");
            }
            else if (property.PropertyType.IsClass && property.PropertyType != typeof(string))
            {
                if (hasChildren)
                {
                    Walk(child, property.PropertyType, key + ConfigurationPath.KeyDelimiter, problems);
                }
                else if (child.Value is not null)
                {
                    problems.Add($"{key} is a section and must hold keys, not a value; the value is not shown");
                }
            }
            else if (hasChildren)
            {
                problems.Add($"{key} must be a single value, not a section");
            }
            else if (child.Value is { Length: 0 })
            {
                problems.Add($"{key} is set but empty");
            }
        }
    }

    // The longest valid segment (Baseline:MaxCoefficientOfVariation's last part) is 25 characters. A longer segment is not echoed, so a
    // misplaced value cannot ride into a message as a key name.
    private const int MaxEchoedSegmentLength = 32;

    private static string Segment(string segment) =>
        segment.Length == 0 ? "(empty key name)"
        : segment.Length > MaxEchoedSegmentLength ? $"a key segment of {segment.Length.ToString(CultureInfo.InvariantCulture)} characters"
        : TextEcho.ForMessage(segment);

    private static bool Converts(string value, Kind kind) => kind switch
    {
        Kind.Integer => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
        Kind.Number => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _),
        _ => bool.TryParse(value, out _),
    };

    private static string Describe(Kind kind) => kind switch
    {
        Kind.Integer => "an integer",
        Kind.Number => "a number",
        _ => "true or false",
    };
}