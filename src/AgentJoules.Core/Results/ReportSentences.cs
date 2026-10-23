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

    /// <summary>
    /// The scope of every joule the tool shows (prompt honesty rule 6): the readings are the energy of the GPU board only, not of the host, the network or a hosted tier's datacentre. The console prints it
    /// once per <c>run</c> command, before the first step line.
    /// </summary>
    public const string GpuBoardOnly = "Energy is GPU-board energy only.";

    /// <summary>
    /// The scope of the workflow figures (REQ-016, AJ-082): the judge calls are summed apart from the workflow, so no row and no workflow total includes them. Every report surface prints it.
    /// </summary>
    public const string JudgeExcluded = "Judge calls are excluded from workflow totals.";

    /// <summary>The disclosure of a self-judged run (REQ-016, AJ-082): the judge model is also one of the workflow models, so the quality scores are not independent of the model under test.</summary>
    public const string SelfJudged = "Self-judging: the judge model is also a workflow model.";

    /// <summary>
    /// The start of the disclosure that the tiers ran different models or precisions (REQ-016, AJ-082, P-15). The report appends one <c>tier=models (precision)</c> entry per primary tier; it states a fact and never
    /// compares the tiers.
    /// </summary>
    public const string ModelsDifferPrefix = "Models differ across tiers: ";
}