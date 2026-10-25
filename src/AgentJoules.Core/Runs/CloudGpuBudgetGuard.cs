using AgentJoules.Core.Configuration;

namespace AgentJoules.Core.Runs;

/// <summary>What <see cref="CloudGpuBudgetGuard.Check"/> decided.</summary>
/// <param name="Stop">True when no new (task, repeat) may start.</param>
/// <param name="Reason">The reason of a stop, <see cref="CloudGpuBudgetGuard.CostCap"/> or <see cref="CloudGpuBudgetGuard.TimeBox"/>; null when the run continues.</param>
/// <param name="Note">A fact about the check that the user should hear once, or null: <see cref="CloudGpuBudgetGuard.TierStartNotSetNote"/> while the tier start is not set.</param>
public readonly record struct BudgetDecision(bool Stop, string? Reason, string? Note = null);

/// <summary>
/// The 4-hour and USD 25 stop of the cloudgpu tier (EDGE-017, design 3.8): stop when <c>CloudGpu:ObservedCostUsd</c> is at least <c>CloudGpu:CostStopUsd</c> (<see cref="CostCap"/>) or when the time since <c>CloudGpu:TierStartUtc</c> is at least
/// <c>CloudGpu:TimeBoxHours</c> (<see cref="TimeBox"/>), whichever holds; when both hold the cost cap is the reason. A cost that was not reported counts as not reached, and a tier start that is not set means the box is not enforced (the decision says so in
/// <see cref="BudgetDecision.Note"/>). The clock is the injected <see cref="TimeProvider"/>, so a test decides without waiting.
/// </summary>
/// <param name="time">The clock of the run.</param>
/// <param name="options">The <c>CloudGpu</c> section; read on every check.</param>
public sealed class CloudGpuBudgetGuard(TimeProvider time, CloudGpuOptions options)
{
    /// <summary>The reason of a stop at the observed cost.</summary>
    public const string CostCap = "cost-cap";

    /// <summary>The reason of a stop at the time box.</summary>
    public const string TimeBox = "time-box";

    /// <summary>The note of a decision made without <c>CloudGpu:TierStartUtc</c>.</summary>
    public const string TierStartNotSetNote = "TierStartUtc not set: the 4-hour box is not enforced";

    private readonly TimeProvider _time = time ?? throw new ArgumentNullException(nameof(time));
    private readonly CloudGpuOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>Decides whether a new (task, repeat) may start now.</summary>
    /// <returns>Stop with <see cref="CostCap"/> or <see cref="TimeBox"/>, or continue.</returns>
    public BudgetDecision Check()
    {
        string? note = _options.TierStartUtc is null ? TierStartNotSetNote : null;
        return _options.ObservedCostUsd >= _options.CostStopUsd
            ? new BudgetDecision(true, CostCap, note)
            : _options.TierStartUtc is { } started && (_time.GetUtcNow() - started).TotalHours >= _options.TimeBoxHours
                ? new BudgetDecision(true, TimeBox)
                : new BudgetDecision(false, null, note);
    }
}