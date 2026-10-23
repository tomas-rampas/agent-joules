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
/// <param name="PowerSource">Where the power figures come from, or null.</param>
/// <param name="ReasoningMode">The reasoning mode of the tier, or null.</param>
/// <param name="Temperature">The sampling temperature, or null.</param>
/// <param name="Provider">The provider of the tier (for example <c>nebius-ai-cloud</c> for cloudgpu), or null.</param>
/// <param name="InstancePreset">The VM preset of a cloudgpu run, or null.</param>
/// <param name="Region">The region of a cloudgpu run, or null.</param>
/// <param name="GpuUuid">The GPU UUID, or null.</param>
/// <param name="ContainerImage">The NIM container image of a cloudgpu run, or null.</param>
/// <param name="ContainerDigest">The digest of the container image, or null.</param>
/// <param name="Precision">The model precision (for example <c>FP8</c>), or null.</param>
/// <param name="CloudCostUsd">The observed cost of the VM in USD; N/A on a tier without a rented VM.</param>
/// <param name="EnergyScope">What the energy figures cover; <see cref="GpuBoardOnly"/>.</param>
/// <param name="Assumptions">The factors behind the cost and carbon figures.</param>
/// <param name="Models">The model each agent used; no element is null.</param>
/// <param name="CloudCostObservedUtc">When the cloud cost was read from the billing page, or null.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EnvironmentInfo(
    string? GpuName,
    string? DriverVersion,
    string Os,
    string DotnetVersion,
    string? LocalRuntime,
    string? RuntimeVersion,
    double? GpuOffloadPercent,
    double? ModelLoadSeconds,
    string? PowerSource,
    string? ReasoningMode,
    double? Temperature,
    string? Provider,
    string? InstancePreset,
    string? Region,
    string? GpuUuid,
    string? ContainerImage,
    string? ContainerDigest,
    string? Precision,
    Metric CloudCostUsd,
    string EnergyScope,
    EnvironmentAssumptions Assumptions,
    IReadOnlyList<EnvironmentModel> Models,
    [property: JsonPropertyOrder(2)] DateTimeOffset? CloudCostObservedUtc = null)
{
    /// <summary>The only value of <see cref="EnergyScope"/>: the energy figures cover the GPU board and nothing else.</summary>
    public const string GpuBoardOnly = "GPU-board only";

    private const string ModelsParameter = "Models";

    /// <summary>
    /// The model each agent used. No element is null; the list is a read-only copy, checked on construction, on <c>with</c> and on read. The order attribute keeps the skeleton order (models, then
    /// cloudCostObservedUtc) although this member is declared in the body.
    /// </summary>
    /// <exception cref="ArgumentNullException">The list or an element is null (the message names <c>environment.models[i]</c>, the index only).</exception>
    [JsonPropertyOrder(1)]
    public IReadOnlyList<EnvironmentModel> Models
    {
        get;
        init => field = CheckedModels(value);
    } = CheckedModels(Models);

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