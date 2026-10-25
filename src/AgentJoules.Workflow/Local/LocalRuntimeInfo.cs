namespace AgentJoules.Workflow.Local;

/// <summary>What an <see cref="ILocalRuntime"/> states about itself for the <c>environment</c> of a run; a value it cannot state is null, never a guess.</summary>
/// <param name="Name">The runtime name, <c>environment.localRuntime</c> (for example <c>ollama</c>).</param>
/// <param name="Version">The runtime version, <c>environment.runtimeVersion</c>.</param>
/// <param name="GpuOffloadPercent">The share of the model held in GPU memory in percent, <c>environment.gpuOffloadPercent</c>.</param>
/// <param name="Precision">The precision or quantization of the loaded model. For a runtime that names itself (<paramref name="Name"/> not null) it is <c>environment.precision</c> as it stands, null included, because a precision the runtime did not state must not be replaced by <c>Tiers:&lt;t&gt;:Precision</c>; a description without a name (<see cref="Unknown"/>) leaves <c>environment.precision</c> to that setting.</param>
public sealed record LocalRuntimeInfo(string? Name, string? Version, double? GpuOffloadPercent, string? Precision)
{
    /// <summary>The description of a runtime that states nothing: every member null.</summary>
    public static LocalRuntimeInfo Unknown { get; } = new(null, null, null, null);
}