using System.Text.Json;

using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Specs.Support;
using AgentJoules.TestSupport;
using AgentJoules.Workflow.Runs;

using Reqnroll;

using Xunit;
using Xunit.Sdk;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-078: one complete, strictly typed results model with the exact label strings (REQ-015). Scoped to the feature because sentences such as "it is read" are reused by other stories with other
/// meanings. The state lives in <see cref="ResultsModelContext"/>; the real serialiser context, reader and <c>RunAssembler.ParseVersion</c> run over the fixture documents of
/// <see cref="RunDocumentFixtures"/>, with no HTTP and no file.
/// </summary>
[Binding]
[Scope(Feature = "AJ-078 Complete the results model and its serialisation")]
public sealed class ResultsModelSteps(ResultsModelContext context)
{
    private static readonly string[] LabelVocabulary = ["measured", "estimated", "model-judged", "N/A", "not-reported"];

    private readonly ResultsModelContext _context = context;

    private RunDocument Document => _context.Document ?? throw new XunitException("No document was built.");

    private string Serialised => _context.SerialisedText ?? throw new XunitException("The document was not serialised.");

    private Exception ReadFailure => _context.ReadFailure ?? throw new XunitException("Reading did not fail.");

    private RunInfo Run => _context.RunInfo ?? throw new XunitException("The run info was not created.");

    [Given(@"^a fixture document with measured, estimated, model-judged, N/A and not-reported metrics$")]
    [Given(@"^a fixture document$")]
    public void GivenAFixtureDocument() => _context.Document = RunDocumentFixtures.EveryLabelKind();

    [Given(@"^a fixture document whose run\.flags is \[""([^""]+)""\]$")]
    public void GivenAFixtureDocumentWithRunFlags(string flag) => _context.Document = RunDocumentFixtures.EveryLabelKind(runFlags: [flag]);

    [Given(@"^a results text where a metric has label ""(N/A|not-reported)"" and value (\d+)$")]
    public void GivenAResultsTextWithAValueOnAnAbsentMetric(string label, double value)
    {
        string text = RunDocumentFixtures.Serialise(RunDocumentFixtures.EveryLabelKind());
        _context.ResultsText = label == "N/A" ? RunDocumentFixtures.WithNaValue(text, value) : RunDocumentFixtures.WithNotReportedValue(text, value);
    }

    [Given(@"^a results text with an extra property ""([^""]+)"" in a step$")]
    public void GivenAResultsTextWithAnExtraStepProperty(string name)
    {
        _context.ExtraPropertyName = name;
        _context.ResultsText = RunDocumentFixtures.WithExtraStepProperty(RunDocumentFixtures.Serialise(RunDocumentFixtures.EveryLabelKind()), name);
    }

    [Given(@"^a results text whose run\.flags contains ""([^""]+)""$")]
    public void GivenAResultsTextWithARunFlag(string flag)
    {
        _context.FlagText = flag;
        _context.ResultsText = RunDocumentFixtures.WithRunFlagText(RunDocumentFixtures.Serialise(RunDocumentFixtures.EveryLabelKind()), flag);
    }

    [Given(@"^a step reading whose in-memory output text is ""([^""]+)""$")]
    public void GivenAStepReadingWithOutputText(string text) => _context.Document = RunDocumentFixtures.EveryLabelKind(outputText: text);

    [Given(@"^an informational version ""([^""]+)""$")]
    public void GivenAnInformationalVersion(string version) => _context.InformationalVersion = version;

    [When(@"^it is serialised and read back$")]
    public void WhenItIsSerialisedAndReadBack()
    {
        _context.SerialisedText = RunDocumentFixtures.Serialise(Document);
        _context.ReadBack = RunDocumentFixtures.Read(_context.SerialisedText);
    }

    [When(@"^(?:it|the document) is serialised$")]
    public void WhenItIsSerialised() => _context.SerialisedText = RunDocumentFixtures.Serialise(Document);

    [When(@"^it is read$")]
    public void WhenItIsRead() => _context.ReadResultsText();

    [When(@"^the run info is created$")]
    public void WhenTheRunInfoIsCreated()
    {
        (string toolVersion, string gitCommit) = RunAssembler.ParseVersion(_context.InformationalVersion, new Version(0, 0, 0));
        _context.RunInfo = new RunInfo("run-1", Tier.Nvidia, ["summarise"], RunMode.Serialized, Repeat: 1, RunCheckpointFixtures.RunStartUtc, toolVersion, gitCommit, RunStatus.Running);
    }

    [Then(@"^the read-back document equals the original$")]
    public void ThenTheReadBackDocumentEqualsTheOriginal() =>
        ResultsEquality.AssertEqual(Document, _context.ReadBack ?? throw new XunitException("The document was not read back."));

    [Then(@"^the label values used are only ""measured"", ""estimated"", ""model-judged"", ""N/A"" and ""not-reported""$")]
    public void ThenTheLabelValuesAreTheVocabulary()
    {
        var used = new SortedSet<string>(StringComparer.Ordinal);
        using JsonDocument parsed = JsonDocument.Parse(Serialised);
        CollectLabels(parsed.RootElement, used);

        Assert.NotEmpty(used);
        Assert.All(used, label => Assert.Contains(label, LabelVocabulary));

        // The fixture holds every kind, so the check cannot pass because a kind was never written.
        Assert.Equal(new SortedSet<string>(LabelVocabulary, StringComparer.Ordinal), used);
    }

    [Then(@"^reading fails$")]
    public void ThenReadingFails() => Assert.True(ReadFailure is JsonException or ArgumentException, $"Reading failed with {ReadFailure.GetType().Name}, which is neither a JsonException nor an ArgumentException.");

    [Then(@"^reading fails and names the property$")]
    public void ThenReadingFailsAndNamesTheProperty()
    {
        Exception failure = ReadFailure;
        _ = Assert.IsType<JsonException>(failure);
        Assert.Contains(_context.ExtraPropertyName ?? throw new XunitException("No property name was given."), failure.Message, StringComparison.Ordinal);
    }

    [Then(@"^reading fails and names the flag$")]
    public void ThenReadingFailsAndNamesTheFlag()
    {
        Exception failure = ReadFailure;
        Assert.True(failure is JsonException or ArgumentException, $"Reading failed with {failure.GetType().Name}.");
        Assert.Contains(_context.FlagText ?? throw new XunitException("No flag was given."), failure.Message, StringComparison.Ordinal);
    }

    [Then(@"^the text ""([^""]+)"" does not appear and outputSha256 and outputChars are present$")]
    public void ThenTheTextDoesNotAppear(string text)
    {
        Assert.DoesNotContain(text, Serialised, StringComparison.Ordinal);

        using JsonDocument parsed = JsonDocument.Parse(Serialised);
        JsonElement step = parsed.RootElement.GetProperty("steps")[0];
        Assert.Equal(RunDocumentFixtures.Sha256Hex(text), step.GetProperty("outputSha256").GetString());
        Assert.Equal(text.Length, step.GetProperty("outputChars").GetInt32());
        Assert.DoesNotContain(step.EnumerateObject(), property => property.Name is "outputText" or "window");
    }

    [Then(@"^the read-back run\.flags equals \[""([^""]+)""\]$")]
    public void ThenTheReadBackRunFlagsEqual(string flag) =>
        Assert.Equal([flag], (_context.ReadBack ?? throw new XunitException("The document was not read back.")).Run.Flags);

    [Then(@"^run\.toolVersion is ""([^""]+)"" and run\.gitCommit is ""([^""]+)""$")]
    public void ThenToolVersionAndCommit(string toolVersion, string gitCommit)
    {
        Assert.Equal(toolVersion, Run.ToolVersion);
        Assert.Equal(gitCommit, Run.GitCommit);
    }

    [Then(@"^run\.gitCommit is ""([^""]+)""$")]
    public void ThenCommitIs(string gitCommit) => Assert.Equal(gitCommit, Run.GitCommit);

    private static void CollectLabels(JsonElement element, SortedSet<string> used)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (property.Name == "label" && property.Value.ValueKind == JsonValueKind.String)
                    {
                        _ = used.Add(property.Value.GetString()!);
                    }
                    else
                    {
                        CollectLabels(property.Value, used);
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    CollectLabels(item, used);
                }

                break;
            default:
                break;
        }
    }
}