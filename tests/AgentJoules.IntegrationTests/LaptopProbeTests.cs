using System.Text.Json;

using AgentJoules.Reporting.Schema;
using AgentJoules.TestSupport;

using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace AgentJoules.IntegrationTests;

/// <summary>
/// The live check behind the laptop preflight of AJ-110 (REQ-021, REQ-017, design 6.3): the newest committed <c>results/probe-&lt;yyyyMMddTHHmmssZ&gt;.json</c> validates against <c>$defs/probe</c> through the
/// production <see cref="SchemaValidator"/> and reports a GPU, and its <c>driverVersion</c> is the one <c>nvidia-smi --query-gpu=driver_version --format=csv,noheader</c> prints on this machine. Both tests are skipped
/// unless AGENTJOULES_INTEGRATION is 1 (<see cref="IntegrationFactAttribute"/>), with the message of the gate. An empty results folder fails with a message that names the story that commits the file; an absent
/// <c>nvidia-smi</c> is a note, not a failure. Only counts and the driver version are written to the output, never a key.
/// </summary>
[Trait("Spec", "REQ-021")]
[Trait("Spec", "REQ-017")]
public sealed class LaptopProbeTests(ITestOutputHelper output)
{
    private const string Pattern = "results/probe-<yyyyMMddTHHmmssZ>.json";

    private readonly ITestOutputHelper _output = output;

    private static string Newest() =>
        LaptopResults.NewestProbe() ?? throw new XunitException(LaptopResults.NoFileMessage(Pattern, "AJ-110-T3"));

    [IntegrationFact]
    public async Task The_newest_committed_probe_validates_and_reports_a_gpu()
    {
        string path = Newest();
        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        JsonElement root = document.RootElement;

        IReadOnlyList<SchemaFailure> failures = SchemaValidator.Validate(root, SchemaTarget.Probe);
        _output.WriteLine($"{Path.GetFileName(path)}: {failures.Count} schema failures");
        Assert.True(failures.Count == 0, string.Join('\n', failures.Select(static failure => failure.InstanceLocation + ": " + failure.Message)));
        Assert.Equal(JsonValueKind.True, root.GetProperty("gpuPresent").ValueKind);
        JsonValueKind counter = root.GetProperty("energyCounterSupported").ValueKind;
        Assert.True(counter is JsonValueKind.True or JsonValueKind.False, "energyCounterSupported is not a boolean: " + counter);
    }

    [IntegrationFact]
    public async Task The_driver_version_of_the_newest_probe_is_the_one_nvidia_smi_prints()
    {
        string path = Newest();
        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        string? committed = document.RootElement.GetProperty("driverVersion").GetString();

        string? printed = await LaptopResults.NvidiaSmiDriverAsync();
        if (printed is null)
        {
            _output.WriteLine("note: nvidia-smi is not installed on this machine, so the driver version of " + Path.GetFileName(path) + " was not compared");
            return;
        }

        _output.WriteLine($"{Path.GetFileName(path)} driverVersion {committed}; nvidia-smi {printed}");
        Assert.Equal(printed, committed);
    }
}