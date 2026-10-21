using AgentJoules.Core.Results;

namespace AgentJoules.Workflow.Judge;

/// <summary>
/// The <see cref="IJudgeService"/> of M2: it makes no call and returns the steps unchanged, so every quality stays N/A <c>not-scored</c>, the <c>judge</c> array of the run stays empty and
/// <c>totals.judge</c> stays N/A. M3 replaces it.
/// </summary>
public sealed class NoOpJudgeService : IJudgeService
{
    /// <inheritdoc />
    /// <returns><paramref name="okSteps"/> itself.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="okSteps"/> is null.</exception>
    public Task<IReadOnlyList<StepReading>> ScoreAsync(IReadOnlyList<StepReading> okSteps, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(okSteps);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(okSteps);
    }
}