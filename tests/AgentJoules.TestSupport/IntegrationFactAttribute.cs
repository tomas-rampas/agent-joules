using Xunit;

namespace AgentJoules.TestSupport;

/// <summary>A fact that is skipped unless the environment variable AGENTJOULES_INTEGRATION is exactly 1.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class IntegrationFactAttribute : FactAttribute
{
    public IntegrationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("AGENTJOULES_INTEGRATION") != "1")
        {
            Skip = "Set AGENTJOULES_INTEGRATION=1";
        }
    }
}