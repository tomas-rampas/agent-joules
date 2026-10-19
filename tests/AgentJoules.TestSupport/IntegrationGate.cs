namespace AgentJoules.TestSupport;

/// <summary>The one place that decides whether the integration tests run: only when AGENTJOULES_INTEGRATION is 1 after trimming surrounding whitespace.</summary>
public static class IntegrationGate
{
    /// <summary>The name of the environment variable that opens the gate.</summary>
    public const string VariableName = "AGENTJOULES_INTEGRATION";

    private const string ClosedReason = "Set " + VariableName + "=1";

    /// <summary>
    /// Null when <paramref name="value"/> is 1 once surrounding whitespace is trimmed, so a trailing space from a
    /// cmd.exe <c>set X=1 &amp;&amp;</c> still counts as set; otherwise the skip reason. Unset, empty, 0 and true all skip.
    /// </summary>
    public static string? SkipReason(string? value) => value?.Trim() == "1" ? null : ClosedReason;
}