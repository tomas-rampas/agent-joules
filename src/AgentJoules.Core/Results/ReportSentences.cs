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

    /// <summary>
    /// The disclosure that a tier measured its energy by sampling because the GPU has no energy counter (REQ-016, EDGE-002, AJ-083): the Markdown summary prints it under the bullet of every tier whose <c>ok</c> steps carry
    /// the <c>sampling-fallback</c> flag. A tier that was forced to sampling shows <c>energy method sampling</c> without it.
    /// </summary>
    public const string SamplingFallback = "Energy method: sampling (counter unsupported)";

    /// <summary>The text of a tier without a selectable run file (REQ-016, AJ-083): the summary row reads <c>&lt;tier&gt; | not run</c> and the header bullet <c>&lt;tier&gt;: not run</c>.</summary>
    public const string NotRun = "not run";

    /// <summary>
    /// The text of a run file that holds no row (REQ-016, AJ-083): a <c>partial</c> file that stopped before its first step finished still has its header bullet, so the table says in a row of its own that the
    /// file has nothing to show, instead of omitting the tier. The SVG and HTML renderers reuse it.
    /// </summary>
    public const string NoCompletedSteps = "no completed steps";

    /// <summary>
    /// The disclosure that the cost total of a concurrent run counts overlapping step windows once per step (REQ-013, REQ-016, AJ-087): the cost is summed over steps, not over the union of their windows. The README
    /// results block of AJ-087 prints it next to <see cref="ConcurrentOverlap"/>; it is defined here with the other report sentences.
    /// </summary>
    public const string ConcurrentCostOverlap = "In concurrent mode the cost total counts overlapping step windows once per step.";
}