namespace AgentJoules.Core.Configuration;

/// <summary>The <c>CloudGpu</c> section of <c>AgentJoules</c>: how the rented-GPU tier is reached from the machine that runs the command.</summary>
/// <remarks>A mutable class with plain get and set accessors so that the configuration binding generator binds it; later stories add the VM and cost keys.</remarks>
public sealed class CloudGpuOptions
{
    /// <summary>
    /// Key <c>CloudGpu:RequireSshVariables</c>, default true: whether a run of the <c>cloudgpu</c> tier needs <c>CLOUDGPU_SSH_HOST</c> and <c>CLOUDGPU_SSH_USER</c>
    /// (the laptop copies the build and fetches the results over SSH); the builder sets it to false on the VM, where the run happens locally.
    /// </summary>
    public bool RequireSshVariables { get; set; } = true;
}