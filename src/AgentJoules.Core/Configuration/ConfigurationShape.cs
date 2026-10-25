using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;

using Microsoft.Extensions.Configuration;

namespace AgentJoules.Core.Configuration;

/// <summary>
/// Checks what the configuration binder would accept silently or explain badly: keys the options do not have, values that are set but
/// empty, a value where a section belongs, and values of the wrong type. No line ever contains a configured value; a key name comes from
/// the configuration sources, so it is passed through <see cref="ConfigurationMessages.Segment"/>, which bounds it at 32 characters.
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
        ("Baseline:SettleSeconds", Kind.Number),
        ("Baseline:MaxCoefficientOfVariation", Kind.Number),
        ("Judge:MaxOutputTokens", Kind.Integer),
        ("Workflow:Seed", Kind.Integer),
        ("CloudGpu:RequireSshVariables", Kind.Boolean),
        ("CloudGpu:HourlyPriceUsd", Kind.Number),
        ("Cost:ElectricityPricePerKwh", Kind.Number),
        ("Cost:GCo2ePerKwh", Kind.Number),
        ("Dashboard:PowerIntervalMs", Kind.Integer),
    ];

    // The scalar keys of one tier that can fail to convert, relative to Tiers:<tier>; checked for every tier the configuration holds.
    private static readonly (string Key, Kind Kind)[] TierTypedKeys =
    [
        ("Enabled", Kind.Boolean),
        ("SupportsSeed", Kind.Boolean),
        ("TimeoutSeconds", Kind.Integer),
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

    /// <summary>The keys, relative to <c>Tiers:&lt;tier&gt;</c>, whose values <see cref="TypeProblems"/> re-checks for every tier; every non-string scalar option of a tier must be listed.</summary>
    internal static IEnumerable<string> TierTypedKeyNames => TierTypedKeys.Select(typed => typed.Key);

    /// <summary>Lists, one line each, every typed key under <paramref name="root"/> (those of <c>Tiers:&lt;tier&gt;</c> included) whose value the binder cannot convert; the value is not shown.</summary>
    internal static List<string> TypeProblems(IConfigurationSection root)
    {
        List<string> problems = [];
        foreach ((string key, Kind kind) in TypedKeys)
        {
            CheckConverts(root, key, kind, key, problems);
        }

        foreach (IConfigurationSection tier in root.GetSection(nameof(AgentJoulesOptions.Tiers)).GetChildren())
        {
            foreach ((string key, Kind kind) in TierTypedKeys)
            {
                CheckConverts(tier, key, kind, $"{nameof(AgentJoulesOptions.Tiers)}:{ConfigurationMessages.Segment(tier.Key)}:{key}", problems);
            }
        }

        return problems;
    }

    private static void CheckConverts(IConfiguration node, string key, Kind kind, string path, List<string> problems)
    {
        string? value = node[key];
        if (!string.IsNullOrEmpty(value) && !Converts(value, kind))
        {
            problems.Add($"{path} must be {Describe(kind)}; the value is not shown");
        }
    }

    private static void Walk(IConfiguration node, Type type, string prefix, List<string> problems)
    {
        foreach (IConfigurationSection child in node.GetChildren())
        {
            string key = prefix + ConfigurationMessages.Segment(child.Key);
            PropertyInfo? property = type.GetProperty(child.Key, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            bool hasChildren = child.GetChildren().Any();

            if (property is null)
            {
                problems.Add($"{key} is not a known option; check the spelling");
            }
            else if (IsStringKeyedDictionary(property.PropertyType, out Type? valueType))
            {
                WalkDictionary(child, valueType, property.Name, key, problems);
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

    private static bool IsStringKeyedDictionary(Type type, [NotNullWhen(true)] out Type? valueType)
    {
        bool match = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>) && type.GetGenericArguments()[0] == typeof(string);
        valueType = match ? type.GetGenericArguments()[1] : null;
        return match;
    }

    // A dictionary section holds one entry per child. The children of Tiers are tier names and must be known, and each is a section of TierOptions
    // keys; the children of an AgentModelIds section are agent names that hold one value each (an unknown agent name is reported here, before the validator).
    private static void WalkDictionary(IConfigurationSection node, Type valueType, string propertyName, string key, List<string> problems)
    {
        bool sectionValues = valueType.IsClass && valueType != typeof(string);
        bool any = false;

        foreach (IConfigurationSection child in node.GetChildren())
        {
            any = true;
            string childKey = key + ConfigurationPath.KeyDelimiter + ConfigurationMessages.Segment(child.Key);
            bool hasChildren = child.GetChildren().Any();

            if (valueType == typeof(TierOptions) && !TierOptions.KnownNames.Contains(child.Key, StringComparer.OrdinalIgnoreCase))
            {
                problems.Add(ConfigurationMessages.UnknownTier(childKey));
            }
            else if (propertyName == nameof(TierOptions.AgentModelIds) && !TierOptions.KnownAgentNames.Contains(child.Key, StringComparer.OrdinalIgnoreCase))
            {
                problems.Add(ConfigurationMessages.UnknownAgent(childKey));
            }
            else if (!sectionValues)
            {
                if (hasChildren)
                {
                    problems.Add($"{childKey} must be a single value, not a section");
                }
                else if (child.Value is { Length: 0 })
                {
                    problems.Add($"{childKey} is set but empty");
                }
            }
            else if (hasChildren)
            {
                Walk(child, valueType, childKey + ConfigurationPath.KeyDelimiter, problems);
            }
            else if (child.Value is not null)
            {
                problems.Add($"{childKey} is a section and must hold keys, not a value; the value is not shown");
            }
        }

        if (!any && node.Value is not null)
        {
            problems.Add($"{key} is a section and must hold keys, not a value; the value is not shown");
        }
    }

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