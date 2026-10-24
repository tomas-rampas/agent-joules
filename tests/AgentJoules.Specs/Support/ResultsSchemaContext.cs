using System.Text.Json;
using System.Text.Json.Nodes;

using AgentJoules.Reporting.Schema;
using AgentJoules.TestSupport;

using Xunit.Sdk;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The state of one AJ-079 scenario (the generated results schema and its guard against drift), handed to the step class by Reqnroll context injection. The Given steps build a fixture document as a JSON tree
/// (mutated where the scenario says so) or pick the committed schema text; the When steps run the real <see cref="SchemaValidator"/>, <see cref="SchemaGenerator"/> and <see cref="CommittedFileDrift"/>; the Then
/// steps look at the failures. There is no HTTP, no model and no write to a file.
/// </summary>
public sealed class ResultsSchemaContext
{
    /// <summary>The repository-relative path of the committed schema.</summary>
    public const string SchemaFile = "docs/results-schema.json";

    /// <summary>The document of the scenario as a JSON tree, or null before a Given step built it.</summary>
    public JsonNode? Tree { get; set; }

    /// <summary>The failures of the last validation, or null before the When step.</summary>
    public IReadOnlyList<SchemaFailure>? Failures { get; private set; }

    /// <summary>The text of the committed schema (or of the stale one), or null before a Given step read it.</summary>
    public string? CommittedText { get; set; }

    /// <summary>The text the generator produced, or null before the When step.</summary>
    public string? GeneratedText { get; set; }

    /// <summary>The failure of the drift check, or null when it passed (or did not run yet).</summary>
    public XunitException? DriftFailure { get; private set; }

    /// <summary>Validates <see cref="Tree"/> against the embedded schema as a run file.</summary>
    public void Validate()
    {
        JsonNode tree = Tree ?? throw new XunitException("No document was built.");
        using JsonDocument document = JsonDocument.Parse(tree.ToJsonString());
        Failures = SchemaValidator.Validate(document.RootElement);
    }

    /// <summary>Tells whether some failure lies at <paramref name="location"/> or below it.</summary>
    /// <param name="location">The JSON location of the member.</param>
    /// <returns>True when a failure is reported at the member or inside it.</returns>
    public bool HasFailureAt(string location) =>
        (Failures ?? throw new XunitException("The document was not validated.")).Any(failure =>
            string.Equals(failure.InstanceLocation, location, StringComparison.Ordinal) || failure.InstanceLocation.StartsWith(location + "/", StringComparison.Ordinal));

    /// <summary>Lists the failure locations for a message.</summary>
    /// <returns>The locations, comma separated.</returns>
    public string Locations() => string.Join(", ", (Failures ?? []).Select(static failure => failure.InstanceLocation));

    /// <summary>Runs the drift check of the unit test: the committed text against the generated one.</summary>
    public void RunDriftCheck()
    {
        string committed = CommittedText ?? throw new XunitException("No committed schema text was given.");
        string generated = SchemaGenerator.GenerateText();
        GeneratedText = generated;
        DriftFailure = null;
        try
        {
            CommittedFileDrift.AssertNoDrift(committed, generated, SchemaFile, CommittedFileDrift.SchemaUpdateHint);
        }
        catch (XunitException failure)
        {
            DriftFailure = failure;
        }
    }
}