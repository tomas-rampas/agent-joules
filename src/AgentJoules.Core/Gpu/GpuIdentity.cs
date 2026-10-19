namespace AgentJoules.Core.Gpu;

/// <summary>The static facts of one GPU, read once when it is opened.</summary>
/// <param name="Name">The product name.</param>
/// <param name="Uuid">The unique device identifier.</param>
/// <param name="DriverVersion">The driver version string.</param>
/// <param name="VbiosVersion">The VBIOS version, or null when the query failed.</param>
/// <param name="ComputeCapability">The CUDA compute capability as <c>major.minor</c> (for example <c>8.6</c>), or null when the query failed.</param>
/// <param name="PowerLimitWatts">The enforced power limit in watts.</param>
/// <param name="MigMode">One of <c>disabled</c>, <c>enabled</c> or <c>not-supported</c>.</param>
public sealed record GpuIdentity(
    string Name,
    string Uuid,
    string DriverVersion,
    string? VbiosVersion,
    string? ComputeCapability,
    double PowerLimitWatts,
    string MigMode);