using AgentJoules.TestSupport.Docs;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The state of one AJ-112 scenario (write the methodology document with a contract test, REQ-022), handed to the step class by Reqnroll context injection. The Given steps only choose how the in-memory document
/// (<see cref="MethodologyDocumentFixtures.Document"/>) differs from a complete one; the When step runs <see cref="MethodologyDocumentParser.Check"/> over it with an in-memory reader of the results folder. Nothing
/// here reads a file, the environment or the network.
/// </summary>
public sealed class MethodologyContractContext
{
    /// <summary>A required heading to leave out of the document, or null.</summary>
    public string? OmitHeading { get; set; }

    /// <summary>A Limitations string to leave out, or null.</summary>
    public string? OmitString { get; set; }

    /// <summary>True to list the Limitations strings in reverse order.</summary>
    public bool ReverseStrings { get; set; }

    /// <summary>The text the document quotes as the relative difference.</summary>
    public string Quoted { get; set; } = MethodologyDocumentFixtures.StoredDifference;

    /// <summary>The <c>relativeDifference</c> the calibration file stores (JSON number text).</summary>
    public string Stored { get; set; } = MethodologyDocumentFixtures.StoredDifference;

    /// <summary>False when the document names no calibration file.</summary>
    public bool NameFile { get; set; } = true;

    /// <summary>A REQ-032 sentence to leave out, or null.</summary>
    public string? OmitSentence { get; set; }

    /// <summary>The sentence an earlier milestone wrote under no required heading, or null.</summary>
    public string? Existing { get; set; }

    /// <summary>The failures the contract found; null before the When step.</summary>
    public IReadOnlyList<string>? Failures { get; private set; }

    /// <summary>The document the scenario checks.</summary>
    /// <returns>The markdown text.</returns>
    public string Render() =>
        MethodologyDocumentFixtures.Document(OmitHeading, OmitString, ReverseStrings, Quoted, nameFile: NameFile, omitSentence: OmitSentence, existing: Existing);

    /// <summary>Runs the contract over the document of the scenario and keeps the failures.</summary>
    public void Run() =>
        Failures = MethodologyDocumentParser.Check(Render(), MethodologyDocumentFixtures.Reader(MethodologyDocumentFixtures.CalibrationFile(Stored)));
}