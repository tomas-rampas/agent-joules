using System.Globalization;
using System.Text.Json.Nodes;

using AgentJoules.Core.Results;
using AgentJoules.Reporting.Schema;
using AgentJoules.Specs.Support;
using AgentJoules.TestSupport;

using Reqnroll;

using Xunit;
using Xunit.Sdk;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-079: the JSON Schema is generated from the model, committed, embedded and guarded against drift (REQ-015). Scoped to the feature because sentences such as "it is validated" are reused by other stories
/// with other meanings. The real <see cref="SchemaValidator"/>, <see cref="SchemaGenerator"/> and <see cref="CommittedFileDrift"/> run over the fixture documents of <see cref="RunDocumentFixtures"/>; the state
/// lives in <see cref="ResultsSchemaContext"/>.
/// </summary>
[Binding]
[Scope(Feature = "AJ-079 Generate the JSON Schema and guard it against drift")]
public sealed class ResultsSchemaSteps(ResultsSchemaContext context)
{
    // The fixture members the scenarios mutate: the Writer step holds the N/A cost, the Planner step the model-judged quality.
    private const string WriterCost = "steps.1.cost";
    private const string PlannerQuality = "steps.0.quality";

    private readonly ResultsSchemaContext _context = context;

    [Given(@"^a generated fixture document$")]
    public void GivenAGeneratedFixtureDocument() => _context.Tree = FixtureTree(RunDocumentFixtures.EveryLabelKind());

    [Given(@"^a generated fixture whose run\.flags is \[""([^""]+)""\]$")]
    public void GivenAGeneratedFixtureWithRunFlags(string flag) =>
        _context.Tree = RunFlagNames.IsKnown(flag)
            ? FixtureTree(RunDocumentFixtures.EveryLabelKind(runFlags: [flag]))
            : JsonNode.Parse(RunDocumentFixtures.WithRunFlagText(RunDocumentFixtures.Serialise(RunDocumentFixtures.EveryLabelKind()), flag))!;

    [Given(@"^a fixture where (cost|quality) has label ""([^""]+)"" and value ([0-9.]+)$")]
    public void GivenAFixtureWithALabelAndAValue(string member, string label, string value)
    {
        JsonNode tree = FixtureTree(RunDocumentFixtures.EveryLabelKind());
        JsonObject metric = At(tree, member == "cost" ? WriterCost : PlannerQuality).AsObject();
        metric["label"] = label;
        metric["value"] = double.Parse(value, CultureInfo.InvariantCulture);
        _context.Tree = tree;
    }

    [Given(@"^the committed docs/results-schema\.json$")]
    public void GivenTheCommittedSchema() =>
        _context.CommittedText = File.ReadAllText(Path.Combine(ConfigurationFixtures.RepositoryRoot(), "docs", "results-schema.json"));

    [Given(@"^a committed schema that lacks a field the model now has$")]
    public void GivenAStaleCommittedSchema()
    {
        JsonNode stale = JsonNode.Parse(SchemaGenerator.GenerateText())!;
        if (!RemoveFirstProperty(stale, "selfJudged"))
        {
            throw new XunitException("The generated schema has no property selfJudged to leave out.");
        }

        _context.CommittedText = stale.ToJsonString();
    }

    [When(@"^it is validated(?: against the schema)?$")]
    public void WhenItIsValidated() => _context.Validate();

    [When(@"^the schema is regenerated from the model$")]
    public void WhenTheSchemaIsRegenerated() => _context.GeneratedText = SchemaGenerator.GenerateText();

    [When(@"^the drift test runs$")]
    public void WhenTheDriftTestRuns() => _context.RunDriftCheck();

    [Then(@"^the result is valid$")]
    public void ThenTheResultIsValid() =>
        Assert.True(_context.Failures is { Count: 0 }, $"The document was expected to be valid, but failed at: {_context.Locations()}.");

    [Then(@"^the result is invalid at the (cost|quality) location$")]
    public void ThenTheResultIsInvalidAtTheMetricLocation(string member) =>
        Assert.True(_context.HasFailureAt("/" + (member == "cost" ? WriterCost : PlannerQuality).Replace('.', '/')), $"No failure at the {member} location; the failures are at: {_context.Locations()}.");

    [Then(@"^the result is invalid at the run flags location$")]
    public void ThenTheResultIsInvalidAtTheRunFlagsLocation() =>
        Assert.True(_context.HasFailureAt("/run/flags"), $"No failure at the run flags location; the failures are at: {_context.Locations()}.");

    [Then(@"^the two JSON trees are equal$")]
    public void ThenTheTwoTreesAreEqual()
    {
        string committed = _context.CommittedText ?? throw new XunitException("The committed schema was not read.");
        string generated = _context.GeneratedText ?? throw new XunitException("The schema was not regenerated.");
        Assert.Null(CommittedFileDrift.Difference(committed, generated));
    }

    [Then(@"^the test fails and names the update variable$")]
    public void ThenTheTestFailsAndNamesTheUpdateVariable()
    {
        XunitException failure = _context.DriftFailure ?? throw new XunitException("The drift test did not fail.");
        Assert.Contains(CommittedFileDrift.SchemaUpdateVariable, failure.Message, StringComparison.Ordinal);
        Assert.EndsWith(CommittedFileDrift.SchemaUpdateHint, failure.Message, StringComparison.Ordinal);
    }

    private static JsonNode FixtureTree(RunDocument document) => JsonNode.Parse(RunDocumentFixtures.Serialise(document))!;

    private static JsonNode At(JsonNode root, string path)
    {
        JsonNode node = root;
        foreach (string part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            node = int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out int index) ? node[index]! : node[part]!;
        }

        return node;
    }

    // Removes the first member called name from any object called properties, depth first; the schema holds the member once per type, so the first is the one.
    private static bool RemoveFirstProperty(JsonNode? node, string name)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["properties"] is JsonObject properties && properties.Remove(name))
                {
                    return true;
                }

                foreach (KeyValuePair<string, JsonNode?> child in obj)
                {
                    if (RemoveFirstProperty(child.Value, name))
                    {
                        return true;
                    }
                }

                return false;
            case JsonArray array:
                foreach (JsonNode? item in array)
                {
                    if (RemoveFirstProperty(item, name))
                    {
                        return true;
                    }
                }

                return false;
            default:
                return false;
        }
    }
}