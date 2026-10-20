namespace AgentJoules.Core.Results;

/// <summary>The four outcomes a step can end with; the strings are fixed by the results schema.</summary>
public static class StepOutcomes
{
    /// <summary>The call finished and returned an answer.</summary>
    public const string Ok = "ok";

    /// <summary>The caller cancelled the call.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>The call failed.</summary>
    public const string Error = "error";

    /// <summary>The call was cut off by a shutdown or a stopped run before it could finish.</summary>
    public const string Incomplete = "incomplete";

    /// <summary>Whether <paramref name="outcome"/> is one of the four outcomes (an exact, case-sensitive match).</summary>
    /// <param name="outcome">The text to test; null is not an outcome.</param>
    public static bool IsKnown(string? outcome) =>
        outcome is Ok or Cancelled or Error or Incomplete;
}