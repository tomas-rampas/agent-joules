using System.Text.Json;
using System.Text.Json.Serialization;

using AgentJoules.Core.Energy;
using AgentJoules.Core.Gpu;
using AgentJoules.Core.Runs;
using AgentJoules.Reporting.Json;

using Xunit;

namespace AgentJoules.UnitTests;

/// <summary>
/// The enum spellings of the results file are exact: the converters of <see cref="Tier"/> and <see cref="EnergyMethod"/> accept the
/// lowercase names and nothing else, and a document with a duplicated property is refused (REQ-008, security review of AJ-040).
/// </summary>
public sealed class StrictJsonTests
{
    [Theory]
    [Trait("Spec", "REQ-008")]
    [InlineData("\"local, cloudgpu\"")]
    [InlineData("\"nvidia,nebius\"")]
    [InlineData("\" local\"")]
    [InlineData("\"local \"")]
    [InlineData("\"Local\"")]
    [InlineData("\"CloudGpu\"")]
    [InlineData("\"NVIDIA\"")]
    [InlineData("\"hosted\"")]
    [InlineData("\"\"")]
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("[\"local\"]")]
    public void The_tier_converter_accepts_only_the_exact_lowercase_names(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Tier>(json));
    }

    [Theory]
    [Trait("Spec", "REQ-008")]
    [InlineData("\"counter, sampling\"")]
    [InlineData("\"sampling,unmetered\"")]
    [InlineData("\" counter\"")]
    [InlineData("\"sampling \"")]
    [InlineData("\"Counter\"")]
    [InlineData("\"Unmetered\"")]
    [InlineData("\"hosted\"")]
    [InlineData("\"\"")]
    [InlineData("1")]
    [InlineData("3")]
    [InlineData("false")]
    [InlineData("null")]
    [InlineData("{}")]
    public void The_method_converter_accepts_only_the_exact_lowercase_names(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<EnergyMethod>(json));
    }

    [Theory]
    [Trait("Spec", "REQ-008")]
    [InlineData(Tier.Local, "local")]
    [InlineData(Tier.CloudGpu, "cloudgpu")]
    [InlineData(Tier.Nvidia, "nvidia")]
    [InlineData(Tier.Nebius, "nebius")]
    public void The_four_tier_names_round_trip(Tier tier, string name)
    {
        string json = JsonSerializer.Serialize(tier);

        Assert.Equal($"\"{name}\"", json);
        Assert.Equal(tier, JsonSerializer.Deserialize<Tier>(json));
    }

    [Theory]
    [Trait("Spec", "REQ-008")]
    [InlineData(EnergyMethod.Counter, "counter")]
    [InlineData(EnergyMethod.Sampling, "sampling")]
    [InlineData(EnergyMethod.Unmetered, "unmetered")]
    public void The_three_method_names_round_trip(EnergyMethod method, string name)
    {
        string json = JsonSerializer.Serialize(method);

        Assert.Equal($"\"{name}\"", json);
        Assert.Equal(method, JsonSerializer.Deserialize<EnergyMethod>(json));
        Assert.Equal(method, JsonSerializer.Deserialize<EnergyMethod?>(json));
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    public void An_undefined_value_is_not_written()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize((Tier)0));
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize((Tier)9));
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize((EnergyMethod)0));
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize((EnergyMethod)9));
    }

    [Fact]
    [Trait("Spec", "REQ-008")]
    public void The_message_of_a_rejected_name_echoes_a_bounded_clean_text()
    {
        string hostile = new string('x', 500) + "\r\nFORGED";

        JsonException failure = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Tier>(JsonSerializer.Serialize(hostile)));

        Assert.True(failure.Message.Length < 300);
        Assert.DoesNotContain('\r', failure.Message);
        Assert.DoesNotContain('\n', failure.Message);
    }

    [Theory]
    [Trait("Spec", "REQ-008")]
    [InlineData("3")]
    [InlineData("\"nvidia, nebius\"")]
    [InlineData("\"Nvidia\"")]
    public void A_step_identity_rejects_a_lenient_tier_even_when_the_options_add_the_enum_converter(string tierToken)
    {
        JsonSerializerOptions lenient = new(JsonSerializerDefaults.Web);
        lenient.Converters.Add(new JsonStringEnumConverter());
        string json = "{\"agent\":\"Planner\",\"modelId\":\"m\",\"tier\":" + tierToken + ",\"task\":\"t\",\"repeat\":1,\"isJudge\":false}";

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<StepIdentity>(json, lenient));
        Assert.Equal(Tier.Nvidia, JsonSerializer.Deserialize<StepIdentity>(json.Replace(tierToken, "\"nvidia\"", StringComparison.Ordinal), lenient)!.Tier);
    }

    [Fact]
    [Trait("Spec", "REQ-006")]
    public void A_probe_document_with_a_duplicated_top_level_property_is_refused()
    {
        string json = JsonSerializer.Serialize(ProbeResult.NoGpu, AgentJoulesJsonContext.Default.ProbeResult);
        string duplicated = json.Insert(json.IndexOf('{', StringComparison.Ordinal) + 1, "\"gpuPresent\":true,");

        Assert.False(JsonSerializer.Deserialize(json, AgentJoulesJsonContext.Default.ProbeResult)!.GpuPresent);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(duplicated, AgentJoulesJsonContext.Default.ProbeResult));
    }
}