using AgentJoules.Core.Cost;
using AgentJoules.Core.Runs;

using Microsoft.Extensions.AI;

namespace AgentJoules.TestSupport;

/// <summary>What one metered fake call of a <see cref="TelemetryRig"/> looks like: the tier, what the provider reported and what the meter read.</summary>
public sealed class TelemetryCall
{
    /// <summary>The tier of the call; a hosted tier gets the unmetered energy meter, so its energy is N/A.</summary>
    public Tier Tier { get; init; } = Tier.Local;

    /// <summary>The usage of the response; null for a response without usage, so both token counts are not-reported.</summary>
    public UsageDetails? Usage { get; init; } = new() { InputTokenCount = 120, OutputTokenCount = 45 };

    /// <summary>The cost calculator of the services; the default answers N/A, as in M2.</summary>
    public ICostCalculator Cost { get; init; } = NotConfiguredCostCalculator.Instance;

    /// <summary>The constant power of a metered tier in watts.</summary>
    public double Watts { get; init; } = 60.0;

    /// <summary>The idle baseline of a metered tier in watts; the net energy of the call is the gross minus this times the duration.</summary>
    public double BaselineWatts { get; init; } = 20.0;

    /// <summary>How long the call takes on the fake clock.</summary>
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>The failure the call ends with, or null for a call that succeeds.</summary>
    public Exception? Failure { get; init; }
}