using Xunit;

namespace AgentJoules.TestSupport.Docs;

/// <summary>
/// A fact that is skipped, with a message that names the story that creates the file, while <c>docs/nim-local-attempt.md</c> does not exist (AJ-102 criterion: the contract test runs against the committed file
/// after AJ-103). <see cref="FactAttribute.Skip"/> is set at runtime, so no analyzer exception is needed.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class NimAttemptDocumentFactAttribute : FactAttribute
{
    /// <summary>The skip message while the document is absent.</summary>
    public const string AbsentMessage = "docs/nim-local-attempt.md does not exist yet (AJ-103)";

    /// <summary>Creates the attribute; <see cref="FactAttribute.Skip"/> is set when the committed document is absent.</summary>
    public NimAttemptDocumentFactAttribute()
        : this(NimAttemptDocumentParser.CommittedPath())
    {
    }

    // Private so that [NimAttemptDocumentFact("path")] cannot compile in any assembly; the unit tests reach the decision through SkipReasonFor.
    private NimAttemptDocumentFactAttribute(string path)
    {
        Skip = SkipReasonFor(path);
    }

    /// <summary>The skip reason for a document path.</summary>
    /// <param name="path">The full path of the document.</param>
    /// <returns>Null when the file exists; otherwise <see cref="AbsentMessage"/>.</returns>
    internal static string? SkipReasonFor(string path) => File.Exists(path) ? null : AbsentMessage;
}