using Xunit;

namespace AgentJoules.TestSupport;

/// <summary>A fact that is skipped unless the environment variable AGENTJOULES_INTEGRATION passes <see cref="IntegrationGate.SkipReason"/>.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class IntegrationFactAttribute : FactAttribute
{
    public IntegrationFactAttribute()
        : this(Environment.GetEnvironmentVariable(IntegrationGate.VariableName))
    {
    }

    // Private so that [IntegrationFact("1")] cannot compile in any assembly; the unit tests reach it through ForValue.
    private IntegrationFactAttribute(string? rawValue)
    {
        Skip = IntegrationGate.SkipReason(rawValue);
    }

    internal static IntegrationFactAttribute ForValue(string? rawValue) => new(rawValue);
}