using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using AgentJoules.Core.Configuration;
using AgentJoules.TestSupport;

using Xunit;

namespace AgentJoules.UnitTests;

/// <summary>
/// AJ-108 (REQ-018): the committed <c>docs/endpoints.md</c> records, per tier, the streaming usage behaviour and the reasoning toggle, and the record agrees with the shipped <c>config/appsettings.json</c>. The gated Specs
/// scenarios (AGENTJOULES_INTEGRATION and a key) assert the same lines; this class asserts them on every run, with no gate, no key and no network, so a record that drifts from the configuration fails the ordinary build.
/// The record formats are the ones listed on the Specs <c>StreamingUsageAndReasoningContext</c>; the first line that starts with a label is the record.
/// </summary>
[Trait("Spec", "REQ-018")]
public sealed partial class EndpointsRecordTests
{
    private const string UsageLabel = "Streaming usage:";
    private const string TokensLabel = "Streaming tokens:";
    private const string ToggleLabel = "Reasoning toggle:";
    private const string ResultLabel = "Reasoning result:";
    private const string SystemMessagePrefix = "system message:";

    // The lines of the section of a tier in the committed document: after its level-two heading, up to the next one.
    private static string[] LinesOf(string tier)
    {
        string path = Path.Combine(ConfigurationFixtures.RepositoryRoot(), "docs", "endpoints.md");
        var lines = new List<string>();
        bool inside = false;
        foreach (string raw in File.ReadAllText(path).Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (inside)
                {
                    break;
                }

                inside = string.Equals(line, "## " + tier, StringComparison.Ordinal);
                continue;
            }

            if (inside)
            {
                lines.Add(line);
            }
        }

        Assert.True(inside, $"docs/endpoints.md has no section ## {tier}.");
        return [.. lines];
    }

    private static string? ValueOf(string[] lines, string label) =>
        lines.FirstOrDefault(line => line.StartsWith(label, StringComparison.Ordinal)) is { } found ? found[label.Length..].Trim() : null;

    [Theory]
    [InlineData("local")]
    [InlineData("nvidia")]
    [InlineData("nebius")]
    public void The_committed_record_states_the_streaming_usage_of_each_tier(string tier)
    {
        string[] lines = LinesOf(tier);

        string? usage = ValueOf(lines, UsageLabel);
        Assert.True(usage is "reported" or "not-reported", $"The first \"{UsageLabel}\" line of ## {tier} must be reported or not-reported.");

        string? tokens = ValueOf(lines, TokensLabel);
        Assert.True(tokens is not null, $"The section ## {tier} has no \"{TokensLabel}\" line.");
        if (usage == "reported")
        {
            Assert.True(MeasuredCounts().IsMatch(tokens!), $"The \"{TokensLabel}\" line of ## {tier} must give the input and output counts as two measured whole numbers.");
        }
    }

    [Theory]
    [InlineData("local")]
    [InlineData("nvidia")]
    [InlineData("nebius")]
    public void The_committed_record_states_the_toggle_and_result_that_the_shipped_configuration_holds(string tier)
    {
        string[] lines = LinesOf(tier);
        TierOptions configured = ConfigurationFixtures.ShippedOptions().Tiers[tier];

        string toggle = ValueOf(lines, ToggleLabel) ?? throw new Xunit.Sdk.XunitException($"The section ## {tier} has no \"{ToggleLabel}\" line.");
        if (configured.ExtraBodyJson is not null)
        {
            // The mechanism of the tier is the body member: the record carries the same JSON object.
            Assert.True(toggle.StartsWith('{'), $"The toggle of ## {tier} must be the JSON object of Tiers:{tier}:ExtraBodyJson.");
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(toggle), JsonNode.Parse(configured.ExtraBodyJson)), $"The toggle of ## {tier} is not the JSON object of Tiers:{tier}:ExtraBodyJson.");
        }
        else
        {
            // The mechanism of the tier is the system message prefix.
            Assert.False(string.IsNullOrWhiteSpace(configured.SystemPromptPrefix), $"Tiers:{tier} holds neither an ExtraBodyJson nor a SystemPromptPrefix.");
            Assert.Equal(SystemMessagePrefix + " " + configured.SystemPromptPrefix!.Trim(), toggle);
        }

        Assert.Equal(configured.ReasoningMode, ValueOf(lines, ResultLabel));
    }

    [GeneratedRegex(@"^input\s+\d+\s+measured,\s*output\s+\d+\s+measured\b", RegexOptions.CultureInvariant)]
    private static partial Regex MeasuredCounts();
}