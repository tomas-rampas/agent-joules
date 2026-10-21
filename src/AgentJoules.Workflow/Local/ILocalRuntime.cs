namespace AgentJoules.Workflow.Local;

/// <summary>
/// The local inference runtime behind a measured tier (REQ-019, REQ-020, design 3.4 "Local runtimes"): what it is and which version runs (the <c>environment</c> fields of a run), and the
/// unmetered warm-up that loads the model before the idle baseline is measured, so the load is neither in the baseline nor in the first step. Ollama and NIM implement it in M4; M2 runs
/// with <see cref="NoOpLocalRuntime"/>, which describes nothing and warms nothing up.
/// </summary>
public interface ILocalRuntime
{
    /// <summary>The short name of the runtime, for example <c>ollama</c> or <c>nim</c>; <c>none</c> for the no-op runtime.</summary>
    string Name { get; }

    /// <summary>Describes the runtime for the <c>environment</c> of a run: name, version, GPU offload and precision; a value the runtime cannot state is null.</summary>
    /// <param name="cancellationToken">Cancels the request to the runtime.</param>
    /// <returns>The description; every member is null when nothing is known.</returns>
    Task<LocalRuntimeInfo> DescribeAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Loads <paramref name="modelId"/> without measuring it, before the idle baseline of a measured tier (design 3.4 "Run orchestration", step 1), and returns how long the load took:
    /// the <c>environment.modelLoadSeconds</c> of the run.
    /// </summary>
    /// <param name="modelId">The model the tier's agents call.</param>
    /// <param name="cancellationToken">Cancels the warm-up.</param>
    /// <returns>The wall time of the load, or null when no load was observed (the no-op runtime); a missing time is null, never 0.</returns>
    Task<TimeSpan?> WarmUpAsync(string modelId, CancellationToken cancellationToken);
}