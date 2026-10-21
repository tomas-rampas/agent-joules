namespace AgentJoules.Workflow.Runs;

/// <summary>
/// Runs the measured part of <c>AJ run</c> (REQ-017, REQ-013, design 3.4 "Run orchestration"): every selected task for the requested number of repeats in the requested mode, one
/// results file per tier. The <c>run</c> command calls it only after the preflight passed outside a dry run, so the <see cref="RunPlan"/> it receives proves that the key check and the
/// model check ran, and the writers in it proved that their directory can be written; a dry run never reaches it.
/// </summary>
public interface IRunOrchestrator
{
    /// <summary>Runs <paramref name="plan"/> and returns the exit code of the command.</summary>
    /// <param name="plan">What to run, built by the <c>run</c> command after the preflight.</param>
    /// <param name="cancellationToken">Cancelled by Ctrl+C, SIGTERM or host shutdown.</param>
    /// <returns>The exit code: 0 when every step completed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is null.</exception>
    /// <exception cref="Core.Cli.StepFailedException">A provider call of a step failed (exit code 5); the message carries no provider text. The partial file of the tier is written first with status <c>failed</c> (or <c>incomplete</c>), and the exception names it (<see cref="Core.Cli.StepFailedException.PartialFile"/>) unless that write failed.</exception>
    Task<int> RunAsync(RunPlan plan, CancellationToken cancellationToken);
}