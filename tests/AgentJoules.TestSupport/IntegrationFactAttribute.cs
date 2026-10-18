using Xunit;

namespace AgentJoules.TestSupport;

/// <summary>A fact that is skipped unless the environment variable AGENTJOULES_INTEGRATION is 1 after trimming surrounding whitespace, so a trailing space from a cmd.exe "set X=1 &amp;&amp;" still counts as set.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class IntegrationFactAttribute : FactAttribute
{
    public IntegrationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("AGENTJOULES_INTEGRATION")?.Trim() != "1")
        {
            Skip = "Set AGENTJOULES_INTEGRATION=1";
        }
    }
}