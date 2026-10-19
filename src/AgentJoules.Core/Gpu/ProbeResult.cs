using System.Text.Json.Serialization;

using AgentJoules.Core.Energy;

namespace AgentJoules.Core.Gpu;

/// <summary>
/// What <c>probe</c> reports: the GPU facts and the energy method that would measure it (REQ-006). Without NVML or a device the value is
/// <see cref="NoGpu"/>: <see cref="GpuPresent"/> is false and every other member is null. An unavailable fact is null, never an empty string or 0.
/// </summary>
/// <remarks>
/// The member order is the key order of the JSON object; it was chosen in AJ-035 to follow the text labels of criterion 1, not taken from design 3.6 (whose list
/// puts <c>gpuUuid</c> and <c>migMode</c> last). The source-generated <c>AgentJoulesJsonContext</c> writes it camelCase. The one
/// place that maps a GPU and the selector outcome to this record is <see cref="Create"/>.
/// </remarks>
/// <param name="GpuPresent">True when the GPU at <c>Gpu:Index</c> opened.</param>
/// <param name="Name">The product name.</param>
/// <param name="GpuUuid">The unique device identifier.</param>
/// <param name="DriverVersion">The driver version string.</param>
/// <param name="ComputeCapability">The CUDA compute capability as <c>major.minor</c>, or null when the query failed.</param>
/// <param name="VbiosVersion">The VBIOS version, or null when the query failed.</param>
/// <param name="PowerLimitW">The enforced power limit in watts.</param>
/// <param name="MigMode">One of <c>disabled</c>, <c>enabled</c> or <c>not-supported</c>.</param>
/// <param name="EnergyCounterSupported">True when the cumulative energy counter answered the probe read at open.</param>
/// <param name="Method">The method the selector chose, <c>counter</c> or <c>sampling</c>; null when <c>Meter:Method=counter</c> is forced on a GPU without the counter.</param>
/// <param name="SampleRateHz">The configured <c>Meter:SampleRateHz</c>.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProbeResult(
    bool GpuPresent,
    string? Name,
    string? GpuUuid,
    string? DriverVersion,
    string? ComputeCapability,
    string? VbiosVersion,
    double? PowerLimitW,
    string? MigMode,
    bool? EnergyCounterSupported,
    string? Method,
    double? SampleRateHz)
{
    /// <summary>The result when no GPU is reachable: <see cref="GpuPresent"/> false and every other member null.</summary>
    public static ProbeResult NoGpu { get; } = new(false, null, null, null, null, null, null, null, null, null, null);

    /// <summary>
    /// True when automatic selection fell back to sampling because the counter is unsupported, so the text form says <c>sampling (sampling-fallback)</c>.
    /// It is not part of the JSON object.
    /// </summary>
    [JsonIgnore]
    public bool IsFallback { get; init; }

    /// <summary>Builds the result of an opened GPU.</summary>
    /// <param name="identity">The static facts read when the GPU was opened.</param>
    /// <param name="energyCounterSupported">The counter state decided when the GPU was opened.</param>
    /// <param name="capabilities">The selector outcome, or null when <c>Meter:Method=counter</c> is forced and the counter is unsupported (probe reports, <c>run</c> fails with exit 6).</param>
    /// <param name="sampleRateHz">The configured <c>Meter:SampleRateHz</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="identity"/> is null.</exception>
    /// <exception cref="ArgumentException">The counter state contradicts <paramref name="capabilities"/>, or a null <paramref name="capabilities"/> comes with a supported counter.</exception>
    /// <remarks>
    /// An unavailable fact is null, never an empty or blank string, never a power limit that is not finite and positive, never a sample rate that is
    /// not finite and positive: the values come from a driver or a hand-written source, and a blank string or 0 would read as a fact in the JSON.
    /// </remarks>
    public static ProbeResult Create(GpuIdentity identity, bool energyCounterSupported, MeterCapabilities? capabilities, double sampleRateHz)
    {
        ArgumentNullException.ThrowIfNull(identity);

        RequireConsistent(energyCounterSupported, capabilities);

        return new ProbeResult(
            GpuPresent: true,
            Name: Fact(identity.Name),
            GpuUuid: Fact(identity.Uuid),
            DriverVersion: Fact(identity.DriverVersion),
            ComputeCapability: Fact(identity.ComputeCapability),
            VbiosVersion: Fact(identity.VbiosVersion),
            PowerLimitW: double.IsFinite(identity.PowerLimitWatts) && identity.PowerLimitWatts > 0.0 ? identity.PowerLimitWatts : null,
            MigMode: Fact(identity.MigMode),
            EnergyCounterSupported: energyCounterSupported,
            Method: capabilities is null ? null : MethodName(capabilities.Method),
            SampleRateHz: double.IsFinite(sampleRateHz) && sampleRateHz > 0.0 ? sampleRateHz : null)
        {
            IsFallback = capabilities?.IsFallback ?? false,
        };
    }

    // No capabilities means a forced counter on a GPU without one, so a supported counter cannot come with it.
    private static void RequireConsistent(bool energyCounterSupported, MeterCapabilities? capabilities)
    {
        if (capabilities is null ? energyCounterSupported : capabilities.CounterSupported != energyCounterSupported)
        {
            throw new ArgumentException("The counter state contradicts the selector outcome.", nameof(energyCounterSupported));
        }
    }

    private static string? Fact(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string MethodName(EnergyMethod method) => method switch
    {
        EnergyMethod.Counter => "counter",
        EnergyMethod.Sampling => "sampling",
        _ => throw new ArgumentOutOfRangeException(nameof(method)),
    };
}