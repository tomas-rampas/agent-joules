namespace AgentJoules.Workflow.Runs;

/// <summary>
/// What <see cref="PreflightService.CheckKeys"/> found when no required variable was missing. Only the preflight service creates one (the constructor is internal), so a caller that
/// holds a result knows the key check ran: AJ-054 makes the run orchestrator require it.
/// </summary>
public sealed record KeyCheckResult
{
    internal KeyCheckResult(string? missingJudgeVariable)
    {
        MissingJudgeVariable = missingJudgeVariable;
    }

    /// <summary>
    /// The name of the judge key variable when it is not set and the run is not a dry run (the run continues and warns); null when the judge key is set,
    /// when the judge tier has no key variable, and in a dry run (where a missing judge key is part of the failure instead).
    /// </summary>
    public string? MissingJudgeVariable { get; }
}