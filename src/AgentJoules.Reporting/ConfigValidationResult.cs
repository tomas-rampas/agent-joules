namespace AgentJoules.Reporting;

/// <summary>The outcome of <see cref="ConfigModelValidator.Validate"/> (REQ-018).</summary>
/// <param name="ValidatedIds">How many (tier, model ID) pairs were checked, the judge pair included; a pair whose tier is disabled or has no section is not checked and not counted.</param>
/// <param name="Problems">The lines to print, one per problem, in the order of the rules; empty when every checked ID is recorded.</param>
public sealed record ConfigValidationResult(int ValidatedIds, IReadOnlyList<string> Problems)
{
    /// <summary>True when there is no problem.</summary>
    public bool IsValid => Problems.Count == 0;
}