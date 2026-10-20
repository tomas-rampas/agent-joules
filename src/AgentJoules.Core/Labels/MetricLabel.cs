using System.Text.Json.Serialization;

namespace AgentJoules.Core.Labels;

/// <summary>The honesty label every metric value carries; the serialized strings are fixed by the results schema.</summary>
public enum MetricLabel
{
    // Numbered from 1 so that default(MetricLabel) is never a valid label.

    /// <summary>Read from the hardware or a provider response during the run.</summary>
    [JsonStringEnumMemberName("measured")]
    Measured = 1,

    /// <summary>Computed from measured inputs and a price table.</summary>
    [JsonStringEnumMemberName("estimated")]
    Estimated = 2,

    /// <summary>Scored by a judge model.</summary>
    [JsonStringEnumMemberName("model-judged")]
    ModelJudged = 3,

    /// <summary>The value does not exist for this step, for example energy of a hosted call.</summary>
    [JsonStringEnumMemberName("N/A")]
    NotApplicable = 4,

    /// <summary>The value should exist but the source did not supply it.</summary>
    [JsonStringEnumMemberName("not-reported")]
    NotReported = 5,
}