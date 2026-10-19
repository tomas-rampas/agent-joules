using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentJoules.Core.Labels;

/// <summary>
/// Reads and writes <see cref="Metric"/> with a fixed shape: <c>value</c> (always, null included), <c>label</c>,
/// then <c>reason</c>, <c>unit</c> and <c>currency</c> only when non-null. The names and label strings are fixed here,
/// so the result does not depend on the caller's naming policy or enum converter. Reading validates through the
/// <see cref="Metric"/> constructor, so an invariant violation surfaces as an unwrapped <see cref="ArgumentException"/>
/// and a malformed document as a <see cref="JsonException"/>.
/// </summary>
public sealed class MetricJsonConverter : JsonConverter<Metric>
{
    // One ordered table; the strings equal the [JsonStringEnumMemberName] attributes on MetricLabel.
    private static readonly (MetricLabel Label, string Name)[] Labels =
    [
        (MetricLabel.Measured, "measured"),
        (MetricLabel.Estimated, "estimated"),
        (MetricLabel.ModelJudged, "model-judged"),
        (MetricLabel.NotApplicable, "N/A"),
        (MetricLabel.NotReported, "not-reported"),
    ];

    /// <inheritdoc />
    public override Metric Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("A metric must be a JSON object.");
        }

        double? value = null;
        MetricLabel? label = null;
        string? reason = null;
        string? unit = null;
        string? currency = null;
        bool sawValue = false;
        bool sawReason = false;
        bool sawUnit = false;
        bool sawCurrency = false;

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            string name = reader.GetString()!;
            if (!reader.Read())
            {
                throw new JsonException("The metric object ended unexpectedly.");
            }

            switch (name)
            {
                case "value":
                    RejectDuplicate(sawValue, name);
                    sawValue = true;
                    value = ReadValue(ref reader);
                    break;
                case "label":
                    RejectDuplicate(label is not null, name);
                    label = ReadLabel(ref reader);
                    break;
                case "reason":
                    RejectDuplicate(sawReason, name);
                    sawReason = true;
                    reason = ReadOptionalString(ref reader, name);
                    break;
                case "unit":
                    RejectDuplicate(sawUnit, name);
                    sawUnit = true;
                    unit = ReadOptionalString(ref reader, name);
                    break;
                case "currency":
                    RejectDuplicate(sawCurrency, name);
                    sawCurrency = true;
                    currency = ReadOptionalString(ref reader, name);
                    break;
                default:
                    throw new JsonException($"Unknown metric property \"{TextEcho.ForMessage(name)}\".");
            }
        }

        if (reader.TokenType != JsonTokenType.EndObject)
        {
            throw new JsonException("The metric object ended unexpectedly.");
        }

        RequirePresent(sawValue, "A metric requires a \"value\" property (null for N/A and not-reported).");
        RequirePresent(label is not null, "A metric requires a \"label\" property.");

        return Metric.FromParts(value, label.GetValueOrDefault(), reason, unit, currency);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Metric value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        writer.WriteStartObject();

        if (value.Value is double number)
        {
            writer.WriteNumber("value", number);
        }
        else
        {
            writer.WriteNull("value");
        }

        writer.WriteString("label", NameOf(value.Label));

        if (value.Reason is not null)
        {
            writer.WriteString("reason", value.Reason);
        }

        if (value.Unit is not null)
        {
            writer.WriteString("unit", value.Unit);
        }

        if (value.Currency is not null)
        {
            writer.WriteString("currency", value.Currency);
        }

        writer.WriteEndObject();
    }

    private static void RequirePresent(bool present, string message)
    {
        if (!present)
        {
            throw new JsonException(message);
        }
    }

    private static void RejectDuplicate(bool seen, string name)
    {
        if (seen)
        {
            throw new JsonException($"Duplicate metric property \"{name}\".");
        }
    }

    private static string NameOf(MetricLabel label)
    {
        foreach ((MetricLabel candidate, string name) in Labels)
        {
            if (candidate == label)
            {
                return name;
            }
        }

        throw new JsonException($"Label {(int)label} is not a defined metric label.");
    }

    private static double? ReadValue(ref Utf8JsonReader reader)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Null => null,
            // On .NET 10 an overflowing literal such as 1e400 arrives here as infinity; the Metric constructor owns the finite check.
            // The fallback only covers a reader that cannot convert the token at all.
            JsonTokenType.Number => reader.TryGetDouble(out double number)
                ? number
                : throw new JsonException("The metric value is not a number the reader can convert to a double."),
            _ => throw new JsonException($"The metric value must be a number or null, not {reader.TokenType}."),
        };
    }

    private static MetricLabel ReadLabel(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"The metric label must be a string, not {reader.TokenType}.");
        }

        string text = reader.GetString()!;
        foreach ((MetricLabel candidate, string name) in Labels)
        {
            if (string.Equals(name, text, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        throw new JsonException($"Unknown metric label \"{TextEcho.ForMessage(text)}\".");
    }

    private static string? ReadOptionalString(ref Utf8JsonReader reader, string property)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.String => reader.GetString(),
            _ => throw new JsonException($"The metric property \"{property}\" must be a string or null, not {reader.TokenType}."),
        };
    }
}