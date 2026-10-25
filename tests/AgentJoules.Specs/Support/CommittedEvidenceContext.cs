using AgentJoules.TestSupport;
using AgentJoules.TestSupport.Evidence;

using Xunit.Sdk;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The state of one AJ-115 scenario (the committed local evidence and the README runtime statements, REQ-020, REQ-021 and EDGE-010), handed to the step class by Reqnroll context injection. The Given steps change a
/// <see cref="CommittedEvidenceDraft"/> that starts as a set satisfying every rule; the When step lays it out in a temporary workspace and applies <see cref="CommittedEvidenceRules"/> through a
/// <see cref="CommittedEvidenceReader"/> over that workspace, so the scenarios are hermetic and the unit facts of <c>CommittedEvidenceTests</c> are the live check of the committed files. There is no network, no
/// model, no GPU and no key; the workspace is deleted when the scenario ends.
/// </summary>
public sealed class CommittedEvidenceContext : IDisposable
{
    private TempWorkspace? _workspace;

    private IReadOnlyList<string>? Result { get; set; }

    /// <summary>The set the scenario builds.</summary>
    public CommittedEvidenceDraft Draft { get; } = new();

    /// <summary>The violations of the rules, or throws when the evidence tests have not run.</summary>
    /// <exception cref="XunitException">The When step has not run.</exception>
    public IReadOnlyList<string> Violations => Result ?? throw new XunitException("The evidence tests have not run yet.");

    /// <summary>Writes the draft into a new workspace and applies the rules to what a reader finds there.</summary>
    public void Run()
    {
        _workspace?.Dispose();
        _workspace = TempWorkspace.CreateEmpty();
        Draft.WriteTo(_workspace);
        Result = CommittedEvidenceRules.CheckAll(new CommittedEvidenceReader(_workspace.Root));
    }

    /// <inheritdoc />
    public void Dispose() => _workspace?.Dispose();
}