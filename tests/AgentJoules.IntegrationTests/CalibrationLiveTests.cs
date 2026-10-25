using System.Text.Json;

using AgentJoules.Reporting.Schema;
using AgentJoules.TestSupport;

using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace AgentJoules.IntegrationTests;

/// <summary>
/// The live check behind the calibration of AJ-111 (REQ-021, design 6.3): the newest committed <c>results/calibration-&lt;yyyyMMddTHHmmssZ&gt;.json</c> holds <c>counterJoules</c>, <c>samplingJoules</c> and
/// <c>relativeDifference</c> as label objects (a finite number labelled measured, or null labelled N/A with a non-empty reason), validates against <c>$defs/calibration</c> through the production
/// <see cref="SchemaValidator"/>, and, when both totals are measured, stores the relative difference that <c>(counter - sampling) / counter</c> gives within 1e-9. The tests are skipped unless AGENTJOULES_INTEGRATION
/// is 1 (<see cref="IntegrationFactAttribute"/>), with the message of the gate. An empty results folder fails with a message that names the story that commits the file. Only counts and joule values are written
/// to the output, never a key.
/// </summary>
[Trait("Spec", "REQ-021")]
public sealed class CalibrationLiveTests(ITestOutputHelper output)
{
    private const string Pattern = "results/calibration-<yyyyMMddTHHmmssZ>.json";

    private readonly ITestOutputHelper _output = output;

    private static string Newest() =>
        LaptopResults.NewestCalibration() ?? throw new XunitException(LaptopResults.NoFileMessage(Pattern, "AJ-111-T2"));

    [IntegrationFact]
    public async Task The_newest_committed_calibration_file_holds_three_label_objects_and_validates()
    {
        string path = Newest();
        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(path));

        IReadOnlyList<string> shape = LaptopResults.LabelObjectFailures(document.RootElement);
        IReadOnlyList<SchemaFailure> schema = SchemaValidator.Validate(document.RootElement, SchemaTarget.Calibration);
        _output.WriteLine($"{Path.GetFileName(path)}: {shape.Count} shape failures, {schema.Count} schema failures");
        Assert.True(shape.Count == 0, string.Join('\n', shape));
        Assert.True(schema.Count == 0, string.Join('\n', schema.Select(static failure => failure.InstanceLocation + ": " + failure.Message)));
    }

    [IntegrationFact]
    public async Task The_stored_difference_matches_the_recomputed_one()
    {
        string path = Newest();
        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        if (!LaptopResults.BothTotalsMeasured(document.RootElement))
        {
            _output.WriteLine("note: " + Path.GetFileName(path) + " has a method that is not measured, so there is no difference to recompute");
            return;
        }

        (double? recomputed, double? stored) = LaptopResults.Recompute(document.RootElement);
        _output.WriteLine($"{Path.GetFileName(path)}: recomputed {recomputed}; stored {stored}");
        if (recomputed is null)
        {
            // A counter of 0 J has no relative difference: the file must say N/A.
            Assert.Null(stored);
            return;
        }

        Assert.NotNull(stored);
        Assert.InRange(Math.Abs(recomputed.Value - stored.Value), 0.0, LaptopResults.RecomputeTolerance);
    }
}