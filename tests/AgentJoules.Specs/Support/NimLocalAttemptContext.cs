using System.Text.RegularExpressions;

using AgentJoules.TestSupport;
using AgentJoules.TestSupport.Docs;

using Xunit.Sdk;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The state of one AJ-103 scenario (the time-boxed NIM-local attempt, REQ-019 and EDGE-010), handed to the step class by Reqnroll context injection. The scenarios follow the AJ-101 pattern: <see cref="Open"/> evaluates
/// <see cref="IntegrationGate.SkipReason"/> for AGENTJOULES_INTEGRATION; when the gate is closed the remaining steps record the reason and pass without reading anything. When it is open they assert the content of the
/// committed <c>docs/nim-local-attempt.md</c> (read through <see cref="NimAttemptDocumentParser"/>) and of <c>README.md</c>. No scenario performs the builder's action: nothing is pulled, no login is made, no container is
/// started or stopped, no request is sent and nothing is written. A scenario whose precondition the committed record does not meet (the fallback scenarios when the decision is <c>nim</c>, or a login rejection that the
/// record does not mention) records that as its note and passes.
/// </summary>
/// <remarks>
/// The preconditions are read from the record, not assumed. The decision is <see cref="NimAttemptDocument.DecisionRuntime"/>. An exceeded box is a <c>Box exceeded:</c> line or a time between Started and Ended of more than
/// 90 minutes. An out-of-memory attempt is text in the <c>Outcome</c> section that matches <see cref="OutOfMemory"/>; a rejected login is text in the <c>Outcome</c> section that matches <see cref="LoginRejected"/>; a catalog
/// search without a supported image is text in the <c>Image</c> section that matches <see cref="NoImage"/>.
/// </remarks>
public sealed partial class NimLocalAttemptContext
{
    private const string ReadmeRelativePath = "README.md";

    private string? RecordedNote { get; set; }

    private string? ClosedReason { get; set; }

    private NimAttemptDocument? Loaded { get; set; }

    /// <summary>True when the gate is open and the committed document exists: the scenario asserts the committed record.</summary>
    public bool IsActive { get; private set; }

    /// <summary>The note a scenario recorded when it passed without asserting (gate closed, key absent, document absent, or the precondition does not hold in the committed record), or null.</summary>
    public string? Note => ClosedReason ?? RecordedNote;

    /// <summary>The reading of the committed document.</summary>
    /// <exception cref="XunitException">The scenario is not active or the document was not loaded.</exception>
    public NimAttemptDocument Document => Loaded ?? throw new XunitException("The committed document was not loaded.");

    /// <summary>Evaluates the gate, and the key variable when it is required, then loads the committed document.</summary>
    /// <param name="requiredKeyVariable">The variable that must have a value (read, never set, never printed), or null when the scenario needs none.</param>
    public void Open(string? requiredKeyVariable = null)
    {
        string? gate = IntegrationGate.SkipReason(Environment.GetEnvironmentVariable(IntegrationGate.VariableName));
        bool keyMissing = requiredKeyVariable is not null && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(requiredKeyVariable));
        string path = NimAttemptDocumentParser.CommittedPath();
        ClosedReason = gate ?? (keyMissing ? requiredKeyVariable + " is not set" : null) ?? (File.Exists(path) ? null : NimAttemptDocumentFactAttribute.AbsentMessage);
        IsActive = ClosedReason is null;
        Loaded = IsActive ? NimAttemptDocumentParser.Parse(File.ReadAllText(path)) : null;
    }

    /// <summary>Records why an open-gate scenario passed without asserting.</summary>
    /// <param name="note">The reason.</param>
    public void Skip(string note) => RecordedNote = note;

    /// <summary>The value of <c>NGC_API_KEY</c> in the process environment, or null; never printed or written.</summary>
    /// <returns>The value, or null.</returns>
    public static string? KeyValue() => Environment.GetEnvironmentVariable("NGC_API_KEY");

    /// <summary>The text of the committed <c>README.md</c>.</summary>
    /// <returns>The file content.</returns>
    public static string ReadReadme() => File.ReadAllText(Path.Combine(ConfigurationFixtures.RepositoryRoot(), ReadmeRelativePath));

    /// <summary>The body of a section of the committed document, or an empty string when it has none.</summary>
    /// <param name="heading">The heading name.</param>
    /// <returns>The body.</returns>
    public string SectionBody(string heading) => Document.Sections.GetValueOrDefault(heading) ?? string.Empty;

    /// <summary>True when the record shows that the box was exceeded: a <c>Box exceeded:</c> line, or Ended later than Started plus 90 minutes.</summary>
    public bool BoxWasExceeded => Document.BoxExceededReason is not null || (Document.Ended - Document.Started) > NimAttemptRules.Box;

    /// <summary>True when the <c>Outcome</c> section reports an out-of-memory exit.</summary>
    public bool OutcomeReportsOutOfMemory => OutOfMemory().IsMatch(SectionBody("Outcome"));

    /// <summary>True when the <c>Outcome</c> section reports a rejected NGC login.</summary>
    public bool OutcomeReportsRejectedLogin => LoginRejected().IsMatch(SectionBody("Outcome"));

    /// <summary>True when the <c>Image</c> section states that no supported image was found.</summary>
    public bool ImageStatesNoneFound => NoImage().IsMatch(SectionBody("Image"));

    [GeneratedRegex(@"out[- ]of[- ]memory|OOMKilled|\bOOM\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OutOfMemory();

    [GeneratedRegex(@"\bunauthorized\b|authorization (error|failed)|access denied|denied:|login (failed|rejected)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LoginRejected();

    [GeneratedRegex(@"none (was )?found|found none|no (supported )?(nemotron )?(nim|image)s? (was |were )?found", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NoImage();
}