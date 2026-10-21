using AgentJoules.Core.Results;

namespace AgentJoules.Workflow.Judge;

/// <summary>
/// The judge seam of a run (REQ-010, design 3.4 "Judge"): after the last (task, repeat) of a tier the orchestrator hands it the steps whose outcome is <c>ok</c> and puts the steps it returns
/// in their place. M3 fills it with the judge that scores each step on its own sink; M2 runs with <see cref="NoOpJudgeService"/>, which scores nothing.
/// </summary>
public interface IJudgeService
{
    /// <summary>Scores <paramref name="okSteps"/> and returns them with their quality filled in.</summary>
    /// <param name="okSteps">The steps of the tier whose outcome is <c>ok</c>, in the order of the run.</param>
    /// <param name="cancellationToken">Cancels the judge calls.</param>
    /// <returns>One step per given step, in the same order: the given step with its quality members set, or the given step itself when it was not scored.</returns>
    Task<IReadOnlyList<StepReading>> ScoreAsync(IReadOnlyList<StepReading> okSteps, CancellationToken cancellationToken);
}