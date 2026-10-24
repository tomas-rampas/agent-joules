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
}