using Xunit;

namespace AgentJoules.TestSupport;

/// <summary>
/// A fact that is skipped unless AGENTJOULES_INTEGRATION passes <see cref="IntegrationGate.SkipReason"/> (the gate of <see cref="IntegrationFactAttribute"/>) and the committed cloudgpu evidence of a
/// <see cref="CloudGpuEvidenceKind"/> exists (<see cref="CloudGpuEvidence.AbsentReason"/>). Without the file the skip reason names the story task that commits it, which a plain gate cannot say.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class CloudGpuEvidenceFactAttribute : FactAttribute
{
    /// <summary>Creates the attribute; <see cref="FactAttribute.Skip"/> is set when the gate is closed or the evidence is absent.</summary>
    /// <param name="kind">The kind of evidence the fact checks.</param>
    public CloudGpuEvidenceFactAttribute(CloudGpuEvidenceKind kind)
    {
        Skip = IntegrationGate.SkipReason(Environment.GetEnvironmentVariable(IntegrationGate.VariableName)) ?? AbsentOrNullForAProblemFile(kind);
    }

    // A committed run file that cannot be used is a problem, not an absence: the fact is not skipped, so it runs and fails on the file.
    private static string? AbsentOrNullForAProblemFile(CloudGpuEvidenceKind kind)
    {
        try
        {
            return CloudGpuEvidence.AbsentReason(kind);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }
}