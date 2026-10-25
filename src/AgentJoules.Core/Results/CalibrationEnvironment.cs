using System.Text.Json.Serialization;

namespace AgentJoules.Core.Results;

/// <summary>
/// The environment facts of a calibration file (REQ-021, AJ-109): the local runtime that served the workflow of the window and the machine's power source, the same values a local run file carries in
/// <c>environment</c> (AJ-105, AJ-106). Every member may be null: a runtime that cannot describe itself, a GPU share nobody could read, or a machine whose power source is unknown leaves its member out of the
/// facts, and nothing is invented.
/// </summary>
/// <param name="LocalRuntime">The runtime that served the window, <c>nim</c> or <c>ollama</c>; null when the runtime did not name itself.</param>
/// <param name="RuntimeVersion">The version the runtime reported; null when it reported none.</param>
/// <param name="GpuOffloadPercent">The share of the model on the GPU, from 0 to 100; null when it was not read.</param>
/// <param name="Precision">The precision of the served model (the quantization level of Ollama, the configured value of a NIM container); null when nobody stated one.</param>
/// <param name="ModelLoadSeconds">The time of the unmetered warm-up call, in seconds; null when the runtime observed none.</param>
/// <param name="PowerSource">Where the machine took its power from: <c>ac</c>, <c>battery</c> or <c>unknown</c>; null when it was not asked.</param>
/// <exception cref="ArgumentOutOfRangeException"><paramref name="GpuOffloadPercent"/> is not a finite number from 0 to 100, or <paramref name="ModelLoadSeconds"/> is not finite.</exception>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CalibrationEnvironment(
    [property: JsonPropertyOrder(1)] string? LocalRuntime,
    [property: JsonPropertyOrder(2)] string? RuntimeVersion,
    double? GpuOffloadPercent,
    [property: JsonPropertyOrder(4)] string? Precision,
    double? ModelLoadSeconds,
    [property: JsonPropertyOrder(6)] string? PowerSource)
{
    private const string GpuOffloadPercentMember = "environment.gpuOffloadPercent";
    private const string ModelLoadSecondsMember = "environment.modelLoadSeconds";

    /// <summary>The share of the model on the GPU, from 0 to 100, or null.</summary>
    [JsonPropertyOrder(3)]
    public double? GpuOffloadPercent
    {
        get;
        init => field = ModelNumbers.InRangeOrNull(GpuOffloadPercentMember, value, 0.0, 100.0);
    } = ModelNumbers.InRangeOrNull(GpuOffloadPercentMember, GpuOffloadPercent, 0.0, 100.0);

    /// <summary>The time of the unmetered warm-up call in seconds, or null.</summary>
    [JsonPropertyOrder(5)]
    public double? ModelLoadSeconds
    {
        get;
        init => field = ModelNumbers.FiniteOrNull(ModelLoadSecondsMember, value);
    } = ModelNumbers.FiniteOrNull(ModelLoadSecondsMember, ModelLoadSeconds);
}