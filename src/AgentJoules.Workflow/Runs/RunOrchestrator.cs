namespace AgentJoules.Workflow.Runs;

/// <summary>
/// The production <see cref="IRunOrchestrator"/>. AJ-054 delivers the <c>run</c> command, its dry run and the preflight of a measured run; the measured run itself (the loops over tasks,
/// repeats and agents, the GPU meter and the idle baseline of a measured tier, the checkpoints and the step lines) is built by AJ-056, which replaces this placeholder. Until then a run
/// outside <c>--dry-run</c> passes every check and then ends with exit code 1 and the line <c>error: measured runs are built by AJ-056 (NotSupportedException)</c>.
/// </summary>
public sealed class RunOrchestrator : IRunOrchestrator
{
    /// <summary>The message of the placeholder failure.</summary>
    public const string NotBuiltMessage = "measured runs are built by AJ-056";

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always: the measured run is AJ-056's (exit code 1).</exception>
    public Task<int> RunAsync(RunPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        throw new NotSupportedException(NotBuiltMessage);
    }
}