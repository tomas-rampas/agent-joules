using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

using AgentJoules.Core.Labels;

namespace AgentJoules.Core.Results;

/// <summary>
/// The <c>environment</c> object of the results file (REQ-015, REQ-020, REQ-031): what the run ran on. Every member is present in the file; a fact
/// that does not apply to the tier (a GPU name on a hosted tier) is null. The names are the camelCase names of the prompt skeleton in the same
/// order. M3 hardens the record without renaming a member.
/// </summary>
/// <param name="GpuName">The GPU name, or null.</param>
/// <param name="DriverVersion">The driver version, or null.</param>
/// <param name="Os">The operating system description.</param>
/// <param name="DotnetVersion">The .NET runtime version.</param>
/// <param name="LocalRuntime">The local model runtime (for example Ollama or NIM), or null on a hosted tier.</param>
/// <param name="RuntimeVersion">The version of the local runtime, or null.</param>
/// <param name="GpuOffloadPercent">The share of the model offloaded to the GPU, or null.</param>
/// <param name="ModelLoadSeconds">The seconds the unmetered warm-up took to load the model, or null.</param>
/// <param name="PowerSource">Where the power figures come from: ac, battery or unknown, or null when none was asked.</param>
/// <param name="ReasoningMode">The reasoning mode of the tier, or null.</param>
/// <param name="Temperature">The sampling temperature, or null.</param>
/// <param name="Provider">The provider of the tier (for example <c>nebius-ai-cloud</c> for cloudgpu), or null.</param>
/// <param name="InstancePreset">The VM preset of a cloudgpu run, or null.</param>
/// <param name="Region">The region of a cloudgpu run, or null.</param>
/// <param name="GpuUuid">The GPU UUID, or null.</param>
/// <param name="ContainerImage">The NIM container image of a cloudgpu run, or null.</param>
/// <param name="ContainerDigest">The digest of the container image, or null.</param>
/// <param name="Precision">The model precision (for example <c>FP8</c>), or null.</param>
/// <param name="CloudCostUsd">The billed cost of the VM in USD, not-reported billed-after-run on the cloudgpu tier (the bill is recorded in docs/cloudgpu-run.md); N/A not-cloudgpu elsewhere.</param>
/// <param name="EnergyScope">What the energy figures cover; <see cref="GpuBoardOnly"/>.</param>
/// <param name="Assumptions">The factors behind the cost and carbon figures.</param>
/// <param name="Models">The model each agent used; no element is null.</param>
/// <param name="CloudCostObservedUtc">When the cloud cost was read from the billing page, or null.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EnvironmentInfo(
    [property: JsonPropertyOrder(1)] string? GpuName,
    [property: JsonPropertyOrder(2)] string? DriverVersion,
    [property: JsonPropertyOrder(3)] string Os,
    [property: JsonPropertyOrder(4)] string DotnetVersion,
    [property: JsonPropertyOrder(5)] string? LocalRuntime,
    [property: JsonPropertyOrder(6)] string? RuntimeVersion,
    double? GpuOffloadPercent,
    double? ModelLoadSeconds,
    [property: JsonPropertyOrder(9)] string? PowerSource,
    [property: JsonPropertyOrder(10)] string? ReasoningMode,
    double? Temperature,
    [property: JsonPropertyOrder(12)] string? Provider,
    [property: JsonPropertyOrder(13)] string? InstancePreset,
    [property: JsonPropertyOrder(14)] string? Region,
    [property: JsonPropertyOrder(15)] string? GpuUuid,
    [property: JsonPropertyOrder(16)] string? ContainerImage,
    [property: JsonPropertyOrder(17)] string? ContainerDigest,
    [property: JsonPropertyOrder(18)] string? Precision,
    [property: JsonPropertyOrder(19)] Metric CloudCostUsd,
    [property: JsonPropertyOrder(20)] string EnergyScope,
    [property: JsonPropertyOrder(21)] EnvironmentAssumptions Assumptions,
    IReadOnlyList<EnvironmentModel> Models,
    [property: JsonPropertyOrder(23)] DateTimeOffset? CloudCostObservedUtc = null)
{
    /// <summary>The only value of <see cref="EnergyScope"/>: the energy figures cover the GPU board and nothing else.</summary>
    public const string GpuBoardOnly = "GPU-board only";

    private const string ModelsParameter = "Models";
    private const string GpuOffloadPercentMember = "environment.gpuOffloadPercent";
    private const string ModelLoadSecondsMember = "environment.modelLoadSeconds";
    private const string TemperatureMember = "environment.temperature";

    /// <summary>
    /// The model each agent used. No element is null; the list is a read-only copy, checked on construction, on <c>with</c> and on read. The order attribute keeps the skeleton order (models, then
    /// cloudCostObservedUtc) although this member is declared in the body.
    /// </summary>
    /// <exception cref="ArgumentNullException">The list or an element is null (the message names <c>environment.models[i]</c>, the index only).</exception>
    [JsonPropertyOrder(22)]
    public IReadOnlyList<EnvironmentModel> Models
    {
        get;
        init => field = CheckedModels(value);
    } = CheckedModels(Models);

    /// <summary>The share of the model offloaded to the GPU, or null; a finite number from 0 to 100, checked on construction, on <c>with</c> and on read.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a finite number from 0 to 100 (the message names <c>environment.gpuOffloadPercent</c>, never the value).</exception>
    [JsonPropertyOrder(7)]
    public double? GpuOffloadPercent
    {
        get;
        init => field = ModelNumbers.InRangeOrNull(GpuOffloadPercentMember, value, 0.0, 100.0);
    } = ModelNumbers.InRangeOrNull(GpuOffloadPercentMember, GpuOffloadPercent, 0.0, 100.0);

    /// <summary>The seconds the unmetered warm-up took to load the model, or null; a finite number, checked on construction, on <c>with</c> and on read.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is infinite or not a number (the message names <c>environment.modelLoadSeconds</c>, never the value).</exception>
    [JsonPropertyOrder(8)]
    public double? ModelLoadSeconds
    {
        get;
        init => field = ModelNumbers.FiniteOrNull(ModelLoadSecondsMember, value);
    } = ModelNumbers.FiniteOrNull(ModelLoadSecondsMember, ModelLoadSeconds);

    /// <summary>The sampling temperature, or null; a finite number, checked on construction, on <c>with</c> and on read.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is infinite or not a number (the message names <c>environment.temperature</c>, never the value).</exception>
    [JsonPropertyOrder(11)]
    public double? Temperature
    {
        get;
        init => field = ModelNumbers.FiniteOrNull(TemperatureMember, value);
    } = ModelNumbers.FiniteOrNull(TemperatureMember, Temperature);

    private static ReadOnlyCollection<EnvironmentModel> CheckedModels(IReadOnlyList<EnvironmentModel>? models)
    {
        if (models is null)
        {
            throw NullElement(ModelsParameter, "environment.models must not be null.");
        }

        var copy = new List<EnvironmentModel>(models.Count);
        for (int index = 0; index < models.Count; index++)
        {
            copy.Add(models[index] ?? throw NullElement(ModelsParameter, $"environment.models[{index}] must not be null."));
        }

        return new ReadOnlyCollection<EnvironmentModel>(copy);
    }

    private static ArgumentNullException NullElement(string parameterName, string message) => new(parameterName, message);
}