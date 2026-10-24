using System.Security.Cryptography;

using AgentJoules.TestSupport.Docs;

using Xunit.Sdk;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The state of one AJ-102 scenario (the contract of <c>docs/nim-local-attempt.md</c>, REQ-019 and EDGE-010), handed to the step class by Reqnroll context injection. The Given steps change a
/// <see cref="NimAttemptDraft"/> that starts as a document satisfying every rule; the When step reads the rendered text with <see cref="NimAttemptDocumentParser"/> and checks it with
/// <see cref="NimAttemptRules"/>. Nothing here uses the command line, a file, the environment or the network.
/// </summary>
public sealed class NimAttemptContractContext
{
    private IReadOnlyList<string>? Result { get; set; }

    /// <summary>The document the scenario builds.</summary>
    public NimAttemptDraft Draft { get; } = new();

    /// <summary>The random suffix of the key the scenario put into the text, or null.</summary>
    public string? KeySuffix { get; private set; }

    /// <summary>The violations of the rules, or throws when the contract test has not run.</summary>
    /// <exception cref="XunitException">The When step has not run.</exception>
    public IReadOnlyList<string> Violations => Result ?? throw new XunitException("The contract test has not run yet.");

    /// <summary>Puts a key built at runtime ("nvapi-" plus 40 random base64url characters) into the Outcome section and remembers the suffix.</summary>
    public void AddRuntimeBuiltKey()
    {
        KeySuffix = Convert.ToBase64String(RandomNumberGenerator.GetBytes(33)).Replace('+', '-').Replace('/', '_')[..40];
        Draft.OutcomeLines.Add("export NGC_API_KEY=nvapi-" + KeySuffix);
    }

    /// <summary>Parses the document and applies the rules.</summary>
    public void Run() => Result = NimAttemptRules.Check(NimAttemptDocumentParser.Parse(Draft.Render()), null);
}