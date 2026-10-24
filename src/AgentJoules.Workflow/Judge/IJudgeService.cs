using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Core.Tasks;
using AgentJoules.Metering;

namespace AgentJoules.Workflow.Judge;

/// <summary>
/// The judge of a run (REQ-010, design 3.4 "Judge"): after the last (task, repeat) of a tier the orchestrator hands it every workflow step of the tier run in a <see cref="JudgeRequest"/> and puts
/// the steps of the <see cref="JudgeResult"/> in their place. The service is stateless; the metering services of the tier run travel in the request, with the sink replaced by the judge sink, so the
/// judge readings are published there and never reach the workflow sink.
/// </summary>
public interface IJudgeService
{
    /// <summary>Scores the steps of one tier run and returns them with their quality filled in.</summary>
    /// <param name="request">The tier, the steps, the tasks and the metering services of the tier run.</param>
    /// <param name="cancellationToken">Cancels the judge calls.</param>
    /// <returns>
    /// One step per given step, in the same order: the given step with its quality members set (a score, or N/A with the reason), or the given step itself when it was not scored. Every other member is kept,
    /// the in-memory <see cref="StepReading.Window"/> included (the union totals and the <c>shared-window</c> flags of a concurrent run are computed from it, REQ-013); the orchestrator refuses a step
    /// whose window changed with an <see cref="InvalidOperationException"/>. The judge readings are not part of the result: they are published on the sink of the request.
    /// </returns>
    Task<JudgeResult> ScoreAsync(JudgeRequest request, CancellationToken cancellationToken);
}

/// <summary>What the judge is asked to score: one tier run.</summary>
/// <param name="Tier">The tier the workflow steps ran on.</param>
/// <param name="Steps">Every workflow step of the tier run, in run order, whatever its outcome.</param>
/// <param name="Tasks">The tasks of the plan; the prompt of a step is looked up by <see cref="StepReading.Task"/> (ordinal).</param>
/// <param name="Services">The metering services of the tier run with <see cref="MeteringServices.Sink"/> replaced by the judge sink.</param>
public sealed record JudgeRequest(Tier Tier, IReadOnlyList<StepReading> Steps, IReadOnlyList<TaskDefinition> Tasks, MeteringServices Services);

/// <summary>What the judge made of a <see cref="JudgeRequest"/>.</summary>
/// <param name="Steps">One step per requested step, in the same order, window unchanged.</param>
/// <param name="SelfJudged">True when the judge model id is the model id of any requested step, false when no step has it, and null when no step was judged (nothing to compare: no ok step with output, a blank judge model, the no-op judge).</param>
/// <param name="JudgedSteps">How many steps were sent to the judge: ok steps with output.</param>
/// <param name="FailedSteps">How many of those ended with a quality that is N/A (a failed call, a reply that did not parse, a refusal).</param>
/// <param name="FailureKinds">The distinct kinds of those failures, in ordinal order: exception type names, <c>parse-error</c> or a refusal token.</param>
public sealed record JudgeResult(IReadOnlyList<StepReading> Steps, bool? SelfJudged, int JudgedSteps, int FailedSteps, IReadOnlyList<string> FailureKinds);