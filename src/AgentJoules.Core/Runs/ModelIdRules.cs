namespace AgentJoules.Core.Runs;

/// <summary>
/// The rules about model IDs that more than one project needs (REQ-017): which IDs are Nemotron models, and when a run must be marked as a baseline run.
/// They live in Core so the run orchestrator and the run assembler (Workflow) and the run file name (Reporting) ask the same predicate.
/// </summary>
public static class ModelIdRules
{
    private const string NemotronMarker = "nemotron";

    /// <summary>Whether <paramref name="modelId"/> names a Nemotron model: it contains <c>nemotron</c>, whatever the case.</summary>
    /// <param name="modelId">The model ID; not null.</param>
    /// <returns>True for a Nemotron model; false for any other ID, the empty string included.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="modelId"/> is null.</exception>
    public static bool IsNemotron(string modelId)
    {
        ArgumentNullException.ThrowIfNull(modelId);

        return modelId.Contains(NemotronMarker, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether a run over these models is a baseline run (the <c>-baseline</c> file name suffix, <c>models[].baseline</c>): any ID that is not a Nemotron model makes it one.</summary>
    /// <param name="modelIds">The model IDs of the agents of the run.</param>
    /// <returns>True when at least one ID is not a Nemotron model; false for an empty list.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="modelIds"/> or an element of it is null; every element is looked at, so a null after the first baseline ID is still refused.</exception>
    public static bool RequiresBaselineSuffix(IEnumerable<string> modelIds)
    {
        ArgumentNullException.ThrowIfNull(modelIds);

        bool baseline = false;
        foreach (string modelId in modelIds)
        {
            ArgumentNullException.ThrowIfNull(modelId, nameof(modelIds));
            baseline |= !IsNemotron(modelId);
        }

        return baseline;
    }
}