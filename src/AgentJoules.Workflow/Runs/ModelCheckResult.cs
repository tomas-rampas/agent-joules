namespace AgentJoules.Workflow.Runs;

/// <summary>What the model check found for one tier: the endpoint it asked and the configured IDs that the endpoint lists.</summary>
/// <param name="Tier">The lowercase tier name.</param>
/// <param name="Endpoint">The base URL of the tier, the endpoint the models route was asked at.</param>
/// <param name="MatchedIds">The configured IDs of the tier that the endpoint listed (the tier model and every per-agent model, each once; for the judge tier also <c>Judge:ModelId</c>), masked and cut like the printed IDs.</param>
public sealed record ModelCheckEntry(string Tier, Uri Endpoint, IReadOnlyList<string> MatchedIds);

/// <summary>
/// What <see cref="PreflightService.CheckModelsAsync"/> found when every configured model ID was listed: one entry per tier that was asked, the selected tiers in order and then the judge
/// tier when it was not selected. Only the preflight service creates one (the constructor is internal), so a caller that holds a result knows the model check ran and passed; the dry run of
/// AJ-054 prints <c>&lt;tier&gt; &lt;endpoint&gt; &lt;matched ids&gt; OK</c> from it.
/// </summary>
public sealed record ModelCheckResult
{
    internal ModelCheckResult(IReadOnlyList<ModelCheckEntry> tiers)
    {
        Tiers = tiers;
    }

    /// <summary>The tiers that were asked, in the order they were asked; never empty.</summary>
    public IReadOnlyList<ModelCheckEntry> Tiers { get; }
}