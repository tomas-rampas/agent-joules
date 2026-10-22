namespace AgentJoules.Workflow.Judge;

/// <summary>
/// An <see cref="IJudgeService"/> that makes no call and returns the steps unchanged, so every quality stays N/A <c>not-scored</c>, the <c>judge</c> array of the run stays empty and
/// <c>totals.judge</c> stays N/A. The production registration is <see cref="JudgeService"/>; this type is the double the test harness registers when a test does not exercise the judge.
/// </summary>
public sealed class NoOpJudgeService : IJudgeService
{
    /// <inheritdoc />
    /// <returns>The steps of <paramref name="request"/> itself, nothing judged, nothing failed and not self-judged.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    public Task<JudgeResult> ScoreAsync(JudgeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new JudgeResult(request.Steps, SelfJudged: false, JudgedSteps: 0, FailedSteps: 0, FailureKinds: []));
    }
}