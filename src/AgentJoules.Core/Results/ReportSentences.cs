namespace AgentJoules.Core.Results;

/// <summary>
/// The fixed sentences that results and reports carry word for word (REQ-013, REQ-016, design 3.5 "Markdown"): one constant per sentence, so the renderers, the console and the tests
/// share one spelling and no surface paraphrases a disclosure.
/// </summary>
public static class ReportSentences
{
    /// <summary>
    /// The disclosure of a concurrent run (REQ-013, EDGE-004): the windows of parallel steps overlap, so their energies are not additive and the run total is the energy of the union
    /// of the windows, not the sum of the step readings.
    /// </summary>
    public const string ConcurrentOverlap = "Per-step energy overlaps in concurrent mode and does not sum to the run total.";
}