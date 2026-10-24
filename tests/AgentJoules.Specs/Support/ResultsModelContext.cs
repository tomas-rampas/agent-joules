using System.Text.Json;

using AgentJoules.Core.Results;
using AgentJoules.TestSupport;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The state of one AJ-078 scenario (the results model and its serialisation), handed to the step class by Reqnroll context injection. The Given steps build a fixture document, a results text or an
/// informational version; the When steps serialise, read or create the run info with the real production code; the Then steps look at what came out. There is no HTTP, no file and no model.
/// </summary>
public sealed class ResultsModelContext
{
    /// <summary>The document of the scenario, or null before a Given step built it.</summary>
    public RunDocument? Document { get; set; }

    /// <summary>The text the document was serialised to, or null before the When step.</summary>
    public string? SerialisedText { get; set; }

    /// <summary>The document read back from the serialised text, or null before the When step.</summary>
    public RunDocument? ReadBack { get; set; }

    /// <summary>The results text of a negative scenario, or null.</summary>
    public string? ResultsText { get; set; }

    /// <summary>The property name a Given step added to a step of the results text, or null.</summary>
    public string? ExtraPropertyName { get; set; }

    /// <summary>The run flag a Given step wrote into the results text, or null.</summary>
    public string? FlagText { get; set; }

    /// <summary>The failure of reading the results text, or null when it read (or was not read yet).</summary>
    public Exception? ReadFailure { get; private set; }

    /// <summary>The informational version of a version scenario, or null.</summary>
    public string? InformationalVersion { get; set; }

    /// <summary>The run info created from the informational version, or null before the When step.</summary>
    public RunInfo? RunInfo { get; set; }

    /// <summary>Reads <see cref="ResultsText"/> with the production context. A failure of the two kinds the reader raises (a JSON error or a model invariant) is kept; any other exception is not caught.</summary>
    public void ReadResultsText()
    {
        ReadFailure = null;
        try
        {
            ReadBack = RunDocumentFixtures.Read(ResultsText ?? throw new InvalidOperationException("No results text was given."));
        }
        catch (JsonException failure)
        {
            ReadFailure = failure;
        }
        catch (ArgumentException failure)
        {
            ReadFailure = failure;
        }
    }
}