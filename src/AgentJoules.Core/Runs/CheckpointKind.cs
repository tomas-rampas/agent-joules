namespace AgentJoules.Core.Runs;

/// <summary>
/// Why the orchestrator wrote a checkpoint that is not part of the normal flow (REQ-017, EDGE-006, EDGE-016): the listener words a failed write of it differently for each.
/// </summary>
public enum CheckpointKind
{
    // Numbered from 1 so that default(CheckpointKind) is never a kind.

    /// <summary>The <c>incomplete</c> checkpoint of a host shutdown (Ctrl+C, SIGTERM, <c>ApplicationStopping</c>).</summary>
    Shutdown = 1,

    /// <summary>The <c>failed</c> (or, for a drop after the first update, <c>incomplete</c>) checkpoint of a step that failed and stopped the tier.</summary>
    StepFailure = 2,
}