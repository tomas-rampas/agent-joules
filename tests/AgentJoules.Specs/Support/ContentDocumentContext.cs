namespace AgentJoules.Specs.Support;

/// <summary>
/// The state of one content scenario (AJ-136 script checks, AJ-137 article checks), handed to the step class by Reqnroll context injection: the text of the document under test, which may be a mutated copy of the
/// committed one, a second text when a check compares two documents, and the failures of the last check. The texts are in memory; the committed files are only read.
/// </summary>
public sealed class ContentDocumentContext
{
    /// <summary>The document under test.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>A second document: the story file, the README or the build log, according to the scenario.</summary>
    public string Other { get; set; } = string.Empty;

    /// <summary>The failures of the last check.</summary>
    public IReadOnlyList<string> Failures { get; set; } = [];

    /// <summary>The links of the document, for the gated link checks.</summary>
    public IReadOnlyList<string> Links { get; set; } = [];

    /// <summary>Whether the scenario models a cloudgpu run file.</summary>
    public bool CloudGpuRan { get; set; } = true;
}