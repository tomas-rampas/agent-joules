namespace AgentJoules.Core.Configuration;

/// <summary>The <c>CloudGpu</c> section of <c>AgentJoules</c>: how the rented-GPU tier is reached from the machine that runs the command.</summary>
/// <remarks>A mutable class with plain get and set accessors so that the configuration binding generator binds it; the cost key is checked by <see cref="FlatSectionsValidator"/>.</remarks>
public sealed class CloudGpuOptions
{
    /// <summary>
    /// Key <c>CloudGpu:RequireSshVariables</c>, default true: whether a run of the <c>cloudgpu</c> tier needs <c>CLOUDGPU_SSH_HOST</c> and <c>CLOUDGPU_SSH_USER</c>
    /// (the laptop copies the build and fetches the results over SSH); the builder sets it to false on the VM, where the run happens locally.
    /// </summary>
    public bool RequireSshVariables { get; set; } = true;

    /// <summary>
    /// Key <c>CloudGpu:HourlyPriceUsd</c>, default null: the price of the rented GPU instance in USD per hour (ADR-15); finite and greater than 0 when set. A cloudgpu step costs this price times its wall seconds over 3600,
    /// <c>estimated</c> in USD; while it is null the cost is N/A <c>hourly-price-not-configured</c>. The committed file does not set it: the user supplies it on the VM.
    /// </summary>
    [MinimumValue(0.0, IsExclusive = true, ErrorMessage = "CloudGpu:HourlyPriceUsd must be a finite number greater than 0 when it is set")]
    public double? HourlyPriceUsd { get; set; }

    /// <summary>The largest <c>CloudGpu:CostStopUsd</c>: the USD 30 cap of the cloudgpu budget (REQ-031, EDGE-017); a larger threshold fails the configuration check.</summary>
    public const double MaximumCostStopUsd = 30.0;

    /// <summary>Key <c>CloudGpu:Provider</c>, default null: the provider named in the run file; <c>nebius-ai-cloud</c> is written while it is null (the shipped file sets it).</summary>
    public string? Provider { get; set; }

    /// <summary>Key <c>CloudGpu:InstancePreset</c>, default null: the VM preset of the run (for example the GPU and vCPU shape), written as <c>environment.instancePreset</c>; null stays null.</summary>
    public string? InstancePreset { get; set; }

    /// <summary>Key <c>CloudGpu:Region</c>, default null: the region of the VM, written as <c>environment.region</c>; null stays null.</summary>
    public string? Region { get; set; }

    /// <summary>Key <c>CloudGpu:ContainerImage</c>, default null: the NIM container image on the VM, written as <c>environment.containerImage</c>; null stays null.</summary>
    public string? ContainerImage { get; set; }

    /// <summary>Key <c>CloudGpu:ContainerDigest</c>, default null: the digest of the container image, written as <c>environment.containerDigest</c>; null stays null.</summary>
    public string? ContainerDigest { get; set; }

    /// <summary>Key <c>CloudGpu:HourlyPriceSource</c>, default null: where the hourly price was read (for example the pricing page and its date); documentation of <see cref="HourlyPriceUsd"/> only. It is bound and validated but not written to the run file (the environment block has no field for it); AJ-128 records it in <c>docs/cloudgpu-run.md</c>.</summary>
    public string? HourlyPriceSource { get; set; }

    /// <summary>
    /// Key <c>CloudGpu:TierStartUtc</c>, default null: the instant the VM started billing, as an ISO 8601 time with an explicit offset (<c>Z</c> or <c>+hh:mm</c>); a value without an offset fails the configuration check, and so does one more than 60 s in the future (the command checks it against its clock). While it is null the 4-hour box is not enforced and the command says so once (AJ-122).
    /// </summary>
    public DateTimeOffset? TierStartUtc { get; set; }

    /// <summary>
    /// Key <c>CloudGpu:ObservedCostUsd</c>, default null: the cost the billing page shows, in USD, reported by the user through <c>AGENTJOULES__CloudGpu__ObservedCostUsd</c> before each invocation; zero or more when set.
    /// While it is null the cost cap is not reached and <c>environment.cloudCostUsd</c> is not-reported.
    /// </summary>
    [MinimumValue(0.0, ErrorMessage = "CloudGpu:ObservedCostUsd must be a finite number of zero or more when it is set")]
    public double? ObservedCostUsd { get; set; }

    /// <summary>Key <c>CloudGpu:CostStopUsd</c>, default 25: no new (task, repeat) starts once <see cref="ObservedCostUsd"/> reaches it; greater than 0, and at most 30 (<see cref="MaximumCostStopUsd"/>, checked by <see cref="CrossFieldValidator"/>).</summary>
    [MinimumValue(0.0, IsExclusive = true, ErrorMessage = "CloudGpu:CostStopUsd must be a finite number greater than 0")]
    public double CostStopUsd { get; set; } = 25.0;

    /// <summary>The largest <c>CloudGpu:TimeBoxHours</c>: the 4-hour box of the cloudgpu budget (REQ-031, EDGE-017).</summary>
    public const double MaximumTimeBoxHours = 4.0;

    /// <summary>Key <c>CloudGpu:TimeBoxHours</c>, default 4: no new (task, repeat) starts once this many hours passed since <see cref="TierStartUtc"/>; greater than 0 and at most 4.</summary>
    [MinimumValue(0.0, IsExclusive = true, Maximum = MaximumTimeBoxHours, ErrorMessage = "CloudGpu:TimeBoxHours must be a finite number greater than 0 and at most 4")]
    public double TimeBoxHours { get; set; } = 4.0;
}