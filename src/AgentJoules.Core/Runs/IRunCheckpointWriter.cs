using AgentJoules.Core.Results;

namespace AgentJoules.Core.Runs;

/// <summary>
/// Writes the results document of one run after every (task, repeat) so that a crash, a rate-limit failure or a lost VM never loses a finished step
/// and never leaves a truncated file (REQ-017, ADR-17). The run orchestrator depends on this interface; <c>ResultsFileWriter</c> in the reporting
/// project is the implementation.
/// </summary>
public interface IRunCheckpointWriter
{
    /// <summary>The path of the complete run file this writer replaces on every checkpoint.</summary>
    string FilePath { get; }

    /// <summary>
    /// Replaces the run file with <paramref name="document"/>, as a whole: after a successful call the file holds this document, and after a failed call
    /// it still holds the one before.
    /// </summary>
    /// <param name="document">The document to write.</param>
    /// <param name="cancellationToken">Cancels the write; the previous complete file is then left as it was.</param>
    /// <returns>A task that completes when the file holds <paramref name="document"/>.</returns>
    Task WriteAsync(RunDocument document, CancellationToken cancellationToken);
}