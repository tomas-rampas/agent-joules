using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json.Serialization;

using AgentJoules.Core.Results;

using Xunit.Sdk;

namespace AgentJoules.TestSupport;

/// <summary>
/// Compares results documents member by member (AJ-078, REQ-015). The records of the results model compare collection members by reference (the default record equality), and
/// they keep the in-memory <c>OutputText</c> and <c>Window</c> of a step, which are never written; this comparer is the equality a round trip is asserted with. It walks the public
/// instance properties by reflection, lists element by element, dictionaries by their ordinal key set and then by value, and every leaf exactly: a date-time offset with <see cref="DateTimeOffset.EqualsExact(DateTimeOffset)"/>, a floating-point number by its bits (so 0.0 and -0.0 differ), anything else by <see cref="object.Equals(object?)"/>.
/// A property that carries <c>[JsonIgnore]</c> with <see cref="JsonIgnoreCondition.Always"/> is skipped, because the file never holds it and a read-back document cannot; a property that is
/// written only when it is not null is compared.
/// </summary>
public static class ResultsEquality
{
    /// <summary>The path <see cref="FirstDifference"/> reports when the two roots themselves differ.</summary>
    public const string RootPath = "(root)";

    /// <summary>Finds the first difference between two objects of the results model.</summary>
    /// <param name="expected">The expected object, or null.</param>
    /// <param name="actual">The actual object, or null.</param>
    /// <returns>Null when they are equal; otherwise the member path of the first difference, camelCase, for example <c>steps[3].throttleReasons[0]</c> or <c>totals.costByCurrency[USD]</c>.</returns>
    public static string? FirstDifference(object? expected, object? actual) => Compare(expected, actual, string.Empty);

    /// <summary>Asserts that two documents are equal member by member.</summary>
    /// <param name="expected">The expected document.</param>
    /// <param name="actual">The actual document.</param>
    /// <exception cref="XunitException">The documents differ; the message names the member path.</exception>
    public static void AssertEqual(RunDocument expected, RunDocument actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);

        if (FirstDifference(expected, actual) is { } path)
        {
            throw new XunitException($"The documents differ at {path}.");
        }
    }

    private static string? Compare(object? expected, object? actual, string path)
    {
        if (expected is null || actual is null)
        {
            return expected is null && actual is null ? null : Here(path);
        }

        Type type = expected.GetType();

        // Collections are compared by content whatever their runtime types are: a list built in a test is an array, the one the reader builds is a List.
        if (IsDictionary(type) || IsDictionary(actual.GetType()))
        {
            return IsDictionary(type) && IsDictionary(actual.GetType()) ? CompareDictionaries(expected, actual, path) : Here(path);
        }

        if (expected is IEnumerable expectedItems && expected is not string)
        {
            return actual is IEnumerable actualItems and not string
                ? CompareLists([.. expectedItems.Cast<object?>()], [.. actualItems.Cast<object?>()], path)
                : Here(path);
        }

        if (type != actual.GetType())
        {
            return Here(path);
        }

        if (IsLeaf(type))
        {
            return LeafEquals(expected, actual) ? null : Here(path);
        }

        int compared = 0;
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.GetIndexParameters().Length > 0 || !property.CanRead || IsNeverWritten(property))
            {
                continue;
            }

            compared++;
            string name = char.ToLowerInvariant(property.Name[0]).ToString() + property.Name[1..];
            string memberPath = path.Length == 0 ? name : path + "." + name;
            if (Compare(property.GetValue(expected), property.GetValue(actual), memberPath) is { } difference)
            {
                return difference;
            }
        }

        // A type with nothing to look at would compare equal whatever it holds; a silent pass is the failure this comparer exists to prevent.
        return compared == 0 ? throw new InvalidOperationException($"The type {type} has no comparable public properties; teach the comparer how to compare it.") : null;
    }

    private static string? CompareLists(object?[] expected, object?[] actual, string path)
    {
        if (expected.Length != actual.Length)
        {
            return Here(path);
        }

        for (int index = 0; index < expected.Length; index++)
        {
            if (Compare(expected[index], actual[index], path + "[" + index.ToString(CultureInfo.InvariantCulture) + "]") is { } difference)
            {
                return difference;
            }
        }

        return null;
    }

    private static string? CompareDictionaries(object expected, object actual, string path)
    {
        Dictionary<string, object?> expectedMap = ToMap(expected);
        Dictionary<string, object?> actualMap = ToMap(actual);
        if (expectedMap.Count != actualMap.Count || expectedMap.Keys.Any(key => !actualMap.ContainsKey(key)))
        {
            return Here(path);
        }

        foreach ((string key, object? value) in expectedMap)
        {
            if (Compare(value, actualMap[key], path + "[" + key + "]") is { } difference)
            {
                return difference;
            }
        }

        return null;
    }

    private static Dictionary<string, object?> ToMap(object dictionary)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (object? pair in (IEnumerable)dictionary)
        {
            Type pairType = pair!.GetType();
            map[(string)pairType.GetProperty("Key")!.GetValue(pair)!] = pairType.GetProperty("Value")!.GetValue(pair);
        }

        return map;
    }

    // Exact comparison: a date-time offset is equal only with the same offset (the same instant in another offset is a different value), and a double only with the same bits (0.0 and -0.0 differ).
    private static bool LeafEquals(object expected, object actual) => expected switch
    {
        DateTimeOffset moment => moment.EqualsExact((DateTimeOffset)actual),
        double number => BitConverter.DoubleToInt64Bits(number) == BitConverter.DoubleToInt64Bits((double)actual),
        float number => BitConverter.SingleToInt32Bits(number) == BitConverter.SingleToInt32Bits((float)actual),
        _ => expected.Equals(actual),
    };

    private static string Here(string path) => path.Length == 0 ? RootPath : path;

    private static bool IsNeverWritten(PropertyInfo property) =>
        property.GetCustomAttribute<JsonIgnoreAttribute>() is { Condition: JsonIgnoreCondition.Always };

    private static bool IsDictionary(Type type) =>
        type.GetInterfaces().Any(static contract =>
            contract.IsGenericType && contract.GetGenericTypeDefinition() is var definition && (definition == typeof(IReadOnlyDictionary<,>) || definition == typeof(IDictionary<,>)));

    private static bool IsLeaf(Type type) =>
        type.IsPrimitive
        || type.IsEnum
        || type == typeof(string)
        || type == typeof(decimal)
        || type == typeof(DateTimeOffset)
        || type == typeof(DateTime)
        || type == typeof(TimeSpan)
        || type == typeof(Guid);
}