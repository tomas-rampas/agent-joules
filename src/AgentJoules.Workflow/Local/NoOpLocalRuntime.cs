namespace AgentJoules.Workflow.Local;

/// <summary>
/// The <see cref="ILocalRuntime"/> of M2: it talks to no runtime, so it describes nothing (every <c>environment</c> field it feeds stays null) and its warm-up observes no load
/// (<c>environment.modelLoadSeconds</c> stays null, never 0). M4 replaces it with the Ollama and NIM runtimes.
/// </summary>
public sealed class NoOpLocalRuntime : ILocalRuntime
{
    /// <summary>The name of the no-op runtime.</summary>
    public const string NoneName = "none";

    /// <inheritdoc />
    /// <value>Always <see cref="NoneName"/>.</value>
    public string Name => NoneName;

    /// <inheritdoc />
    /// <returns>A completed task: the no-op runtime has nothing to check.</returns>
    public Task CheckAvailableAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <returns><see cref="LocalRuntimeInfo.Unknown"/>, every member null.</returns>
    public Task<LocalRuntimeInfo> DescribeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(LocalRuntimeInfo.Unknown);
    }

    /// <inheritdoc />
    /// <returns>Null: no load was observed.</returns>
    /// <exception cref="ArgumentException"><paramref name="modelId"/> is null or blank.</exception>
    public Task<TimeSpan?> WarmUpAsync(string modelId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<TimeSpan?>(null);
    }
}