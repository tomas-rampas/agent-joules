using Microsoft.Extensions.Options;

namespace AgentJoules.Core.Configuration;

/// <summary>The root section <c>AgentJoules</c> of <c>config/appsettings.json</c>, overridable by <c>AGENTJOULES__*</c> environment variables.</summary>
/// <remarks>
/// A mutable class with plain get and set accessors so that the configuration binding generator binds it; a key that is absent keeps
/// the default of its property, which is also what <c>config/appsettings.json</c> ships.
/// </remarks>
public sealed class AgentJoulesOptions
{
    /// <summary>The name of the root configuration section.</summary>
    public const string SectionName = "AgentJoules";

    /// <summary>Section <c>Gpu</c>: which GPU is measured.</summary>
    [ValidateObjectMembers]
    public GpuOptions Gpu { get; set; } = new();

    /// <summary>Section <c>Meter</c>: how energy is measured.</summary>
    [ValidateObjectMembers]
    public MeterOptions Meter { get; set; } = new();

    /// <summary>Section <c>Baseline</c>: how the idle baseline is measured and judged.</summary>
    [ValidateObjectMembers]
    public BaselineOptions Baseline { get; set; } = new();

    /// <summary>
    /// Section <c>Tiers</c>: one entry per tier name (<c>local</c>, <c>cloudgpu</c>, <c>nvidia</c>, <c>nebius</c>), matched without regard to case.
    /// Checked by <see cref="CrossFieldValidator"/>: the generated validator does not enumerate a dictionary.
    /// </summary>
    public Dictionary<string, TierOptions> Tiers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Section <c>Judge</c>: which tier and model score the answers; checked by <see cref="CrossFieldValidator"/>.</summary>
    public JudgeOptions Judge { get; set; } = new();

    /// <summary>Section <c>Workflow</c>: settings of the agent workflow that every tier shares.</summary>
    public WorkflowOptions Workflow { get; set; } = new();

    /// <summary>Section <c>CloudGpu</c>: how the rented-GPU tier is reached; <c>RequireSshVariables</c> is false on the VM.</summary>
    [ValidateObjectMembers]
    public CloudGpuOptions CloudGpu { get; set; } = new();

    /// <summary>Section <c>Cost</c>: the electricity price, its currency and the optional carbon factor.</summary>
    [ValidateObjectMembers]
    public CostOptions Cost { get; set; } = new();

    /// <summary>Section <c>Paths</c>: the files a run reads, relative to the content root; every key has a default.</summary>
    public PathsOptions Paths { get; set; } = new();

    /// <summary>Section <c>Report</c>: the settings of the <c>report</c> command; checked by <see cref="CrossFieldValidator"/>.</summary>
    public ReportOptions Report { get; set; } = new();

    /// <summary>Section <c>Dashboard</c>: how the live dashboard is fed (the power forwarding interval).</summary>
    [ValidateObjectMembers]
    public DashboardOptions Dashboard { get; set; } = new();
}