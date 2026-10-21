using AgentJoules.Core.Configuration;
using AgentJoules.Core.Energy;
using AgentJoules.Core.Gpu;
using AgentJoules.Core.Runs;

namespace AgentJoules.Workflow.Runs;

/// <summary>
/// The checks of <c>AJ run --dry-run</c> (REQ-017, design 3.7 "Preflight and dry-run"): everything a run would need, in the order of the preflight, and no completion call. The key check
/// with the judge key joining the message, the model check of every selected tier (one models request per tier, the judge model against the list of the judge tier), and the GPU check of
/// every selected measured tier. Nothing is written to disk. The <c>run</c> command prints one line per selected tier from the result (<see cref="FormatLine"/>).
/// </summary>
/// <param name="preflight">The preflight checks.</param>
/// <param name="gpus">The GPU port the measured tiers are checked through.</param>
public sealed class DryRunService(PreflightService preflight, IGpuTelemetrySourceFactory gpus)
{
    private readonly PreflightService _preflight = preflight ?? throw new ArgumentNullException(nameof(preflight));
    private readonly IGpuTelemetrySourceFactory _gpus = gpus ?? throw new ArgumentNullException(nameof(gpus));

    /// <summary>Runs the checks for <paramref name="tiers"/> and returns what passed.</summary>
    /// <param name="tiers">The selected tiers, from <see cref="PreflightService.SelectTiers(string)"/>.</param>
    /// <param name="cancellationToken">Cancels the models requests.</param>
    /// <returns>One entry per selected tier, in order, with the endpoint and the matched model IDs, and the meter capabilities of the measured tiers.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="tiers"/> is null.</exception>
    /// <exception cref="Core.Cli.MissingKeyException">A key of a selected tier, an SSH variable of <c>cloudgpu</c> or the judge key is missing (exit code 3).</exception>
    /// <exception cref="Core.Cli.ModelNotFoundException">A configured model ID is not listed (exit code 4).</exception>
    /// <exception cref="Core.Cli.EndpointUnreachableException">A models endpoint is unreachable or answers something that is not a model list (exit code 5).</exception>
    /// <exception cref="Core.Cli.MeasurementPreconditionException">A measured tier cannot be measured (exit code 6).</exception>
    /// <exception cref="Core.Cli.ConfigurationException">A tier or the judge is configured wrongly (exit code 2).</exception>
    public async Task<DryRunResult> RunAsync(IReadOnlyList<Tier> tiers, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tiers);

        KeyCheckResult keys = _preflight.CheckKeys(tiers, dryRun: true);
        ModelCheckResult models = await _preflight.CheckModelsAsync(tiers, keys, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<KeyValuePair<Tier, MeterCapabilities>> meters = _preflight.CheckMeasuredTiers(tiers, _gpus);

        // The model check also reports the judge tier when it was not selected; the dry run reports the selected tiers only.
        ModelCheckEntry[] selected = [.. tiers.Select(tier => models.Tiers.First(entry => entry.Tier == TierOptions.NameOf(tier)))];
        return new DryRunResult(selected, meters);
    }

    /// <summary>
    /// Formats the line of one tier: <c>&lt;tier&gt; &lt;endpoint&gt; &lt;matched ids&gt; OK</c>, the endpoint as an absolute URI and the matched IDs (already masked, cut and scrubbed by the
    /// model check) joined by a comma without spaces, for example <c>nvidia https://integrate.api.nvidia.com/v1 nvidia/nemotron-nano-3-30b-a3b OK</c>.
    /// </summary>
    /// <param name="entry">The model check entry of the tier.</param>
    /// <returns>The line without a terminator.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entry"/> is null.</exception>
    public static string FormatLine(ModelCheckEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return $"{entry.Tier} {entry.Endpoint.AbsoluteUri} {string.Join(',', entry.MatchedIds)} OK";
    }
}

/// <summary>What a dry run found when every check passed.</summary>
/// <param name="Tiers">One model check entry per selected tier, in the order of a run.</param>
/// <param name="Meters">The meter capabilities of each selected measured tier, in the order of a run; empty when only hosted tiers were selected.</param>
public sealed record DryRunResult(IReadOnlyList<ModelCheckEntry> Tiers, IReadOnlyList<KeyValuePair<Tier, MeterCapabilities>> Meters);