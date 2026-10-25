using Xunit;

namespace AgentJoules.TestSupport.Docs;

/// <summary>
/// A fact that is skipped unless AGENTJOULES_INTEGRATION passes <see cref="IntegrationGate.SkipReason"/> (the same gate as <see cref="IntegrationFactAttribute"/>) and the committed
/// <c>docs/nim-local-attempt.md</c> decides <c>Local runtime: nim</c>. A decision of <c>ollama</c>, or an absent document, is a skip with its own message, which a plain gate cannot express. <see cref="FactAttribute.Skip"/>
/// is set at runtime, so no analyzer exception is needed.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class NimDecisionIntegrationFactAttribute : FactAttribute
{
    /// <summary>Creates the attribute; <see cref="FactAttribute.Skip"/> is set when the gate is closed or the committed decision is not nim.</summary>
    public NimDecisionIntegrationFactAttribute()
        : this(Environment.GetEnvironmentVariable(IntegrationGate.VariableName), NimAttemptDocumentParser.CommittedPath())
    {
    }

    // Private so that no assembly can pass its own gate value or path; the unit tests reach the decision through SkipReasonFor.
    private NimDecisionIntegrationFactAttribute(string? gateValue, string path)
    {
        Skip = SkipReasonFor(gateValue, path);
    }

    /// <summary>The skip reason for a gate value and a document path.</summary>
    /// <param name="gateValue">The raw value of AGENTJOULES_INTEGRATION.</param>
    /// <param name="path">The full path of the document.</param>
    /// <returns>Null when the gate is open and the document decides nim; otherwise the reason, the gate first.</returns>
    internal static string? SkipReasonFor(string? gateValue, string path) => IntegrationGate.SkipReason(gateValue) ?? DecisionSkipReason(path);

    /// <summary>The skip reason of the committed decision alone.</summary>
    /// <param name="path">The full path of the document.</param>
    /// <returns>Null when the document exists and decides nim; otherwise the reason.</returns>
    internal static string? DecisionSkipReason(string path)
    {
        if (!File.Exists(path))
        {
            return NimAttemptDocumentFactAttribute.AbsentMessage;
        }

        string? runtime = NimAttemptDocumentParser.Parse(File.ReadAllText(path)).DecisionRuntime;
        return runtime == "nim" ? null : "docs/nim-local-attempt.md decides " + (runtime ?? "no runtime") + ", not nim; the NIM endpoint is not checked";
    }
}