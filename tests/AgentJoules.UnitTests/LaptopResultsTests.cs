using System.Globalization;
using System.Text.Json;

using AgentJoules.TestSupport;

using Xunit;

namespace AgentJoules.UnitTests;

/// <summary>
/// The checks of the laptop campaign files (AJ-110, AJ-111, REQ-021), run without the gate over in-memory documents and a scratch folder: the choice of the newest file by name, the label-object rules (a number labelled
/// measured, a null labelled N/A with a reason, and the refusal of an N/A that carries a value), the sign and the tolerance of the recomputed relative difference, the first line of the driver text, and the entry reader of
/// <c>docs/build-log.md</c>. The last test reads every committed <c>results/calibration-*.json</c> (a file read only, REQ-023) and applies the same rules to it, so the rules hold on the real files with the gate closed.
/// </summary>
[Trait("Spec", "REQ-021")]
public sealed class LaptopResultsTests
{
    private static string Measured(double value, string unit = "J") =>
        "{\"value\":" + value.ToString("R", CultureInfo.InvariantCulture) + ",\"label\":\"measured\",\"unit\":\"" + unit + "\"}";

    private static string NotApplicable(string value = "null", string reason = "no-counter") =>
        "{\"value\":" + value + ",\"label\":\"N/A\"" + (reason.Length > 0 ? ",\"reason\":\"" + reason + "\"" : string.Empty) + "}";

    private static JsonDocument Calibration(string counter, string sampling, string difference) =>
        JsonDocument.Parse("{\"counterJoules\":" + counter + ",\"samplingJoules\":" + sampling + ",\"relativeDifference\":" + difference + "}");

    private static string ScratchFolder(params string[] names)
    {
        DirectoryInfo folder = Directory.CreateTempSubdirectory("aj-laptop-results-");
        foreach (string name in names)
        {
            File.WriteAllText(Path.Combine(folder.FullName, name), "{}");
        }

        return folder.FullName;
    }

    // ---- the newest file, by name ----

    [Fact]
    public void The_newest_probe_is_the_last_by_name_whatever_the_order_of_creation()
    {
        // The newest name is written first; a file saved with a suffix, a backup copy and a calibration file are not laptop probes.
        string folder = ScratchFolder("probe-20261025T104744Z.json", "probe-20261019T205344Z.json", "probe-20270101T000000Z-test.json", "probe-20270101T000000Z.json.bak", "calibration-20280101T000000Z.json");
        try
        {
            Assert.Equal("probe-20261025T104744Z.json", Path.GetFileName(LaptopResults.NewestProbe(folder)));
            Assert.Equal("calibration-20280101T000000Z.json", Path.GetFileName(LaptopResults.NewestCalibration(folder)));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void The_newest_calibration_file_is_the_last_by_name()
    {
        string folder = ScratchFolder("calibration-20261025T105451Z.json", "calibration-20261024T235959Z.json", "probe-20301231T235959Z.json");
        try
        {
            Assert.Equal("calibration-20261025T105451Z.json", Path.GetFileName(LaptopResults.NewestCalibration(folder)));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_folder_without_a_file_or_without_a_folder_has_no_newest_file()
    {
        string folder = ScratchFolder();
        try
        {
            Assert.Null(LaptopResults.NewestProbe(folder));
            Assert.Null(LaptopResults.NewestCalibration(Path.Combine(folder, "missing")));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // ---- the label objects ----

    [Fact]
    public void A_measured_number_and_a_null_with_a_reason_are_label_objects()
    {
        using JsonDocument document = Calibration(Measured(100), Measured(90), Measured(0.1, "ratio"));
        using JsonDocument withNotApplicable = Calibration(Measured(100), NotApplicable(), NotApplicable());

        Assert.Empty(LaptopResults.LabelObjectFailures(document.RootElement));
        Assert.Empty(LaptopResults.LabelObjectFailures(withNotApplicable.RootElement));
    }

    [Fact]
    public void A_not_applicable_label_that_carries_a_value_is_refused()
    {
        using JsonDocument document = Calibration(Measured(100), NotApplicable(value: "5"), Measured(0.1, "ratio"));

        string failure = Assert.Single(LaptopResults.LabelObjectFailures(document.RootElement));
        Assert.Contains("samplingJoules", failure, StringComparison.Ordinal);
        Assert.Contains("not null", failure, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_not_applicable_label_without_a_reason_is_refused(string reason)
    {
        using JsonDocument document = Calibration(Measured(100), Measured(90), NotApplicable(reason: reason));

        string failure = Assert.Single(LaptopResults.LabelObjectFailures(document.RootElement));
        Assert.Contains("relativeDifference", failure, StringComparison.Ordinal);
        Assert.Contains("without a reason", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void A_measured_label_needs_a_number()
    {
        using JsonDocument document = Calibration("{\"value\":\"100\",\"label\":\"measured\"}", Measured(90), "{\"value\":null,\"label\":\"measured\"}");

        IReadOnlyList<string> failures = LaptopResults.LabelObjectFailures(document.RootElement);

        Assert.Equal(2, failures.Count);
        Assert.Contains(failures, static failure => failure.StartsWith("counterJoules", StringComparison.Ordinal));
        Assert.Contains(failures, static failure => failure.StartsWith("relativeDifference", StringComparison.Ordinal));
    }

    [Fact]
    public void Another_label_or_a_missing_member_or_a_document_that_is_not_an_object_fails()
    {
        using JsonDocument other = Calibration("{\"value\":1,\"label\":\"estimated\"}", Measured(90), Measured(0.1, "ratio"));
        using JsonDocument missing = JsonDocument.Parse("{\"counterJoules\":" + Measured(1) + ",\"samplingJoules\":5}");
        using JsonDocument array = JsonDocument.Parse("[]");

        Assert.Contains("estimated", Assert.Single(LaptopResults.LabelObjectFailures(other.RootElement)), StringComparison.Ordinal);
        Assert.Equal(2, LaptopResults.LabelObjectFailures(missing.RootElement).Count);
        Assert.Equal(3, LaptopResults.LabelObjectFailures(array.RootElement).Count);
    }

    // ---- the recomputed difference ----

    [Fact]
    public void The_recomputed_difference_is_counter_minus_sampling_over_counter()
    {
        using JsonDocument counterAbove = Calibration(Measured(100), Measured(90), Measured(0.1, "ratio"));
        using JsonDocument counterBelow = Calibration(Measured(90), Measured(100), Measured(-10.0 / 90.0, "ratio"));

        (double? above, double? storedAbove) = LaptopResults.Recompute(counterAbove.RootElement);
        (double? below, double? storedBelow) = LaptopResults.Recompute(counterBelow.RootElement);

        Assert.Equal(0.1, above!.Value, 12);
        Assert.Equal(0.1, storedAbove!.Value, 12);
        Assert.Equal(-10.0 / 90.0, below!.Value, 12);
        Assert.True(below.Value < 0, "sampling above the counter gives a negative difference");
        Assert.Equal(storedBelow!.Value, below.Value, 12);
        Assert.Null(RecomputeFailure(counterAbove));
        Assert.Null(RecomputeFailure(counterBelow));
    }

    [Fact]
    public void A_stored_difference_with_the_wrong_sign_is_refused()
    {
        using JsonDocument document = Calibration(Measured(100), Measured(90), Measured(-0.1, "ratio"));

        Assert.Contains("differs from the recomputed", RecomputeFailure(document), StringComparison.Ordinal);
    }

    [Fact]
    public void The_tolerance_is_one_in_a_billion()
    {
        using JsonDocument inside = Calibration(Measured(100), Measured(90), Measured(0.1 + 1e-10, "ratio"));
        using JsonDocument outside = Calibration(Measured(100), Measured(90), Measured(0.1 + 1e-8, "ratio"));

        Assert.Null(RecomputeFailure(inside));
        Assert.NotNull(RecomputeFailure(outside));
    }

    [Fact]
    public void A_counter_of_zero_needs_a_not_applicable_difference()
    {
        using JsonDocument number = Calibration(Measured(0), Measured(5), Measured(1, "ratio"));
        using JsonDocument notApplicable = Calibration(Measured(0), Measured(5), NotApplicable(reason: "counter-zero"));

        Assert.Null(LaptopResults.Recompute(number.RootElement).Recomputed);
        Assert.Contains("counterJoules is 0", RecomputeFailure(number), StringComparison.Ordinal);
        Assert.Null(RecomputeFailure(notApplicable));
    }

    [Fact]
    public void A_difference_that_is_not_a_number_is_refused_when_both_totals_are_measured_and_ignored_otherwise()
    {
        using JsonDocument measuredTotals = Calibration(Measured(100), Measured(90), NotApplicable());
        using JsonDocument noSampling = Calibration(Measured(100), NotApplicable(), NotApplicable());

        Assert.True(LaptopResults.BothTotalsMeasured(measuredTotals.RootElement));
        Assert.Contains("not a measured number", RecomputeFailure(measuredTotals), StringComparison.Ordinal);
        Assert.False(LaptopResults.BothTotalsMeasured(noSampling.RootElement));
        Assert.Null(RecomputeFailure(noSampling));
    }

    // ---- the driver line ----

    [Theory]
    [InlineData("616.92\n", "616.92")]
    [InlineData("  616.92 \r\n616.93\r\n", "616.92")]
    [InlineData("\n\r\n   \n616.92", "616.92")]
    [InlineData("", "")]
    [InlineData("  \r\n \n", "")]
    public void The_first_non_empty_line_of_the_smi_output_is_trimmed(string output, string expected) =>
        Assert.Equal(expected, LaptopResults.FirstLine(output));

    // ---- the entry of the build log ----

    private const string Log = "# Build log\r\n\r\n### AJ-109 calibrate\r\nline 109 a\r\nline 109 b\r\n\r\n### AJ-110 preflight\r\nline 110 a\r\n## not a story heading\r\n#### deeper heading\r\nline 110 b\r\n\r\n### AJ-111 calibration\r\nline 111\r\n";

    [Fact]
    public void An_entry_runs_from_its_heading_to_the_next_story_heading()
    {
        string entry = LaptopResults.EntryOf(Log, "AJ-110")!;

        Assert.Equal("line 110 a\n## not a story heading\n#### deeper heading\nline 110 b\n\n", entry);
        Assert.DoesNotContain("AJ-110", entry, StringComparison.Ordinal);
        Assert.DoesNotContain("line 109", entry, StringComparison.Ordinal);
        Assert.DoesNotContain("line 111", entry, StringComparison.Ordinal);
    }

    [Fact]
    public void The_last_entry_runs_to_the_end_of_the_log_and_a_missing_entry_is_null()
    {
        Assert.Equal("line 111\n\n", LaptopResults.EntryOf(Log, "AJ-111"));
        Assert.Null(LaptopResults.EntryOf(Log, "AJ-112"));
        Assert.Null(LaptopResults.EntryOf(string.Empty, "AJ-110"));
    }

    // ---- the committed calibration files ----

    public static TheoryData<string> CommittedCalibrationFiles()
    {
        // The HEAD names, so an untracked calibration file is not a row (K2).
        TheoryData<string> rows = [];
        foreach (string name in LaptopResults.CommittedResultsFiles().Where(static name => name.IndexOf('/', "results/".Length) < 0 && name.EndsWith(".json", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            string file = name["results/".Length..];
            if (file.StartsWith("calibration-", StringComparison.Ordinal) && File.Exists(Path.Combine(LaptopResults.ResultsFolder(), file)))
            {
                rows.Add(file);
            }
        }

        return rows;
    }

    [Theory]
    [MemberData(nameof(CommittedCalibrationFiles))]
    public void Every_committed_calibration_file_holds_label_objects_and_the_recomputed_difference(string fileName)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(LaptopResults.ResultsFolder(), fileName)));

        IReadOnlyList<string> shape = LaptopResults.LabelObjectFailures(document.RootElement);
        Assert.True(shape.Count == 0, $"results/{fileName}: {string.Join("; ", shape)}");
        string? recompute = LaptopResults.RecomputeFailure(document.RootElement);
        Assert.True(recompute is null, $"results/{fileName}: {recompute}");
    }

    private static string? RecomputeFailure(JsonDocument document) => LaptopResults.RecomputeFailure(document.RootElement);
}