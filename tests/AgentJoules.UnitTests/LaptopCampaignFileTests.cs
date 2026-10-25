using System.Text.Json.Nodes;

using AgentJoules.Cli;
using AgentJoules.Core.Runs;
using AgentJoules.TestSupport;
using AgentJoules.TestSupport.Evidence;

using Xunit;
using Xunit.Sdk;

namespace AgentJoules.UnitTests;

/// <summary>
/// AJ-114 (REQ-021): the committed run files of the laptop campaign and the checks of <see cref="LaptopCampaign"/> that the gated class <c>LaptopCampaignTests</c> and the open-gate steps apply to them. The theory
/// runs on every build over every committed run file (one row per file, discovered when the tests are listed): the file validates against <c>$defs/run</c> and, when its status is completed, a primary file has the
/// 36 steps of 3 tasks x 3 repeats x 4 agents and every step has outcome <c>ok</c> (a <c>-baseline</c> file is one task with one repeat by criterion 7, so the step count is not asserted for it). The facts prove the
/// checks over small documents built in memory, so the gate is not the only place where a wrong check would show.
/// </summary>
public sealed class LaptopCampaignFileTests
{
    private static readonly string[] CampaignTierSuffixes = ["-local.json", "-nvidia.json", "-nebius.json"];

    /// <summary>
    /// One row per committed run file of the laptop campaign tiers: every file of the HEAD tree under <c>results/</c> that the name rule of criterion 5 counts and whose tier is local, nvidia or nebius (a committed
    /// <c>-cloudgpu</c> file is not a row of this class; M5 AJ-127 adds its own checks). A working-tree file that is not committed is not a row (K2, M4 peer review m-2).
    /// </summary>
    public static TheoryData<string> CommittedRunFiles()
    {
        TheoryData<string> rows = [];
        foreach (string path in LaptopCampaign.CommittedRunFiles())
        {
            string name = Path.GetFileName(path);
            if (CampaignTierSuffixes.Any(suffix => name.EndsWith(suffix, StringComparison.Ordinal) || name.EndsWith(suffix[..^".json".Length] + "-baseline.json", StringComparison.Ordinal)))
            {
                rows.Add(name);
            }
        }

        return rows;
    }

    [Theory]
    [MemberData(nameof(CommittedRunFiles))]
    [Trait("Spec", "REQ-021")]
    public void Every_committed_run_file_is_not_failed_validates_and_a_completed_one_has_36_ok_steps(string fileName)
    {
        string path = Path.Combine(LaptopResults.ResultsFolder(), fileName);
        JsonNode run = LaptopCampaign.Read(path);

        // AJ-114 criterion 6: the file of a failed tier is not committed; the tier is re-run alone and the incident is logged in docs/build-log.md.
        Assert.False(
            string.Equals(LaptopCampaign.StatusOf(run), "failed", StringComparison.Ordinal),
            $"results/{fileName} has run.status \"failed\": AJ-114 criterion 6 forbids committing the file of a failed tier; re-run the tier alone and log the incident in docs/build-log.md");

        IReadOnlyList<string> schema = LaptopCampaign.SchemaFailures(path);
        Assert.True(schema.Count == 0, $"results/{fileName} does not validate against $defs/run: {string.Join("; ", schema)}");

        if (!string.Equals(LaptopCampaign.StatusOf(run), "completed", StringComparison.Ordinal))
        {
            return;
        }

        if (fileName.EndsWith("-baseline.json", StringComparison.Ordinal))
        {
            Assert.DoesNotContain(LaptopCampaign.StepFailures(run), static failure => failure.StartsWith("steps[", StringComparison.Ordinal));
            return;
        }

        IReadOnlyList<string> steps = LaptopCampaign.StepFailures(run);
        Assert.True(steps.Count == 0, $"results/{fileName}: {string.Join("; ", steps)}");
    }

    // ---- the checks over documents built in memory ----

    private static JsonObject Metric(string label, double? value, string? reason = null)
    {
        var metric = new JsonObject { ["value"] = value, ["label"] = label };
        if (reason is not null)
        {
            metric["reason"] = reason;
        }

        return metric;
    }

    private static JsonObject Step(string outcome = "ok", bool hosted = true) =>
        new()
        {
            ["outcome"] = outcome,
            ["grossJoules"] = hosted ? Metric("N/A", null, "hosted-tier") : Metric("measured", 4.5),
            ["netJoules"] = hosted ? Metric("N/A", null, "hosted-tier") : Metric("measured", 3.5),
        };

    private static JsonNode Run(Action<JsonObject>? change = null, bool hosted = true)
    {
        JsonArray steps = [];
        for (int index = 0; index < LaptopCampaign.StepCount; index++)
        {
            steps.Add(Step(hosted: hosted));
        }

        var run = new JsonObject
        {
            ["run"] = new JsonObject { ["status"] = "completed", ["repeat"] = 3, ["tasks"] = new JsonArray("incident-brief", "api-migration", "energy-explainer"), ["flags"] = new JsonArray() },
            ["steps"] = steps,
            ["judge"] = new JsonArray(new JsonObject { ["score"] = 4 }),
            ["baseline"] = new JsonObject { ["seconds"] = 30.003 },
            ["environment"] = new JsonObject { ["localRuntime"] = "nim", ["gpuOffloadPercent"] = 100, ["modelLoadSeconds"] = 0.49, ["powerSource"] = "ac" },
        };
        change?.Invoke(run);
        // Round-tripped through text, as a run file is read: a number built in memory is not a JSON number to the helpers until it is parsed.
        return CommittedEvidenceRules.ParseRun(run.ToJsonString());
    }

    [Fact]
    [Trait("Spec", "REQ-021")]
    public void A_campaign_file_with_3_tasks_3_repeats_36_ok_steps_and_a_judge_array_has_the_shape() => Assert.Empty(LaptopCampaign.ShapeFailures(Run()));

    [Theory]
    [InlineData("llamafile")]
    [InlineData("NIM")]
    [InlineData(null)]
    [Trait("Spec", "REQ-021")]
    public void An_unknown_runtime_fails_and_names_the_field(string? runtime)
    {
        IReadOnlyList<string> failures = LaptopCampaign.LocalFailures(Run(run => run["environment"]!["localRuntime"] = runtime));

        Assert.StartsWith("environment.localRuntime: expected \"nim\" or \"ollama\" but found", Assert.Single(failures), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Spec", "REQ-021")]
    public void The_baseline_boundary_is_30_seconds()
    {
        _ = Assert.Single(LaptopCampaign.LocalFailures(Run(run => run["baseline"]!["seconds"] = 29.999)));
        Assert.Empty(LaptopCampaign.LocalFailures(Run(run => run["baseline"]!["seconds"] = 30)));
    }

    [Fact]
    [Trait("Spec", "REQ-021")]
    public void Four_tasks_fail_and_name_the_field() =>
        Assert.Contains("run.tasks: expected 3 tasks but found 4 elements", LaptopCampaign.ShapeFailures(Run(run => run["run"]!["tasks"] = new JsonArray("a", "b", "c", "d"))));

    [Fact]
    [Trait("Spec", "REQ-021")]
    public void A_missing_file_of_a_tier_that_is_not_pending_fails_and_names_the_story()
    {
        using var workspace = TempWorkspace.CreateEmpty();
        string empty = Path.Combine(workspace.Root, "results");
        List<string> notes = [];

        foreach (Tier tier in LaptopCampaign.Tiers)
        {
            XunitException failure = Assert.Throws<XunitException>(() => LaptopCampaign.FindOrNote(tier, notes.Add, empty));
            Assert.Contains(LaptopCampaign.Story, failure.Message, StringComparison.Ordinal);
        }

        Assert.Empty(notes);
    }

    [Fact]
    [Trait("Spec", "REQ-021")]
    public void FailedFiles_lists_only_files_of_the_tier_whose_status_is_failed()
    {
        using var workspace = TempWorkspace.CreateEmpty();
        static string Doc(string status) => "{\"run\":{\"status\":\"" + status + "\"}}";
        _ = workspace.WriteFile(Path.Combine("results", "20261025T100000Z-nvidia.json"), Doc("failed"));
        _ = workspace.WriteFile(Path.Combine("results", "20261025T100100Z-nvidia.json"), Doc("completed"));
        _ = workspace.WriteFile(Path.Combine("results", "20261025T100200Z-nebius.json"), Doc("failed"));
        string folder = Path.Combine(workspace.Root, "results");

        Assert.Equal(["20261025T100000Z-nvidia.json"], LaptopCampaign.FailedFiles(Tier.Nvidia, folder).Select(static path => Path.GetFileName(path)));
        Assert.Equal(["20261025T100200Z-nebius.json"], LaptopCampaign.FailedFiles(Tier.Nebius, folder).Select(static path => Path.GetFileName(path)));
        Assert.Empty(LaptopCampaign.FailedFiles(Tier.Local, folder));
    }

    [Fact]
    [Trait("Spec", "REQ-021")]
    public void Every_tier_has_a_committed_file_with_the_shape_and_the_hosted_files_carry_no_energy()
    {
        foreach (Tier tier in LaptopCampaign.Tiers)
        {
            string path = LaptopCampaign.LatestCommittedRunFile(tier) ?? throw new XunitException($"HEAD holds no completed or partial {tier} run file in results/ (AJ-114)");
            JsonNode run = LaptopCampaign.Read(path);
            IReadOnlyList<string> shape = LaptopCampaign.ShapeFailures(run);

            Assert.True(shape.Count == 0, $"{Path.GetFileName(path)}: {string.Join("; ", shape)}");
            if (LaptopCampaign.HostedTiers.Contains(tier))
            {
                IReadOnlyList<string> hosted = LaptopCampaign.HostedFailures(run);
                Assert.True(hosted.Count == 0, $"{Path.GetFileName(path)}: {string.Join("; ", hosted)}");
            }
        }
    }

    [Fact]
    [Trait("Spec", "REQ-021")]
    public async Task Validate_on_the_committed_results_folder_exits_0_and_counts_the_run_files()
    {
        // `validate results/` reads the working-tree folder, so the expected count is the working-tree one.
        int count = LaptopCampaign.CommittedRunFiles(LaptopResults.ResultsFolder()).Count;

        CliRunResult result = await CliHarness.RunTokensAsync(["validate", "results/"], new CliHostOverrides(WorkingDirectory: ConfigurationFixtures.RepositoryRoot()));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal([$"Validated {count} files"], result.Stdout.Split('\n').Select(static line => line.TrimEnd('\r')).Where(static line => line.Length > 0));
        Assert.Empty(result.Stderr);
    }

    [Theory]
    [InlineData("failed", "run.status")]
    [InlineData("running", "run.status")]
    [InlineData("partial", "run.status")]
    [Trait("Spec", "REQ-021")]
    public void A_status_other_than_completed_fails_and_names_the_field(string status, string field)
    {
        IReadOnlyList<string> failures = LaptopCampaign.ShapeFailures(Run(run => run["run"]!["status"] = status));

        Assert.Contains(failures, failure => failure.StartsWith(field, StringComparison.Ordinal) && failure.Contains($"\"{status}\"", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Spec", "REQ-021")]
    public void A_task_count_a_repeat_or_a_step_count_that_is_off_fails_and_names_the_field()
    {
        Assert.Contains(LaptopCampaign.ShapeFailures(Run(run => run["run"]!["tasks"] = new JsonArray("incident-brief"))), static failure => failure.StartsWith("run.tasks", StringComparison.Ordinal));
        Assert.Contains(LaptopCampaign.ShapeFailures(Run(run => run["run"]!["repeat"] = 2)), static failure => failure.StartsWith("run.repeat", StringComparison.Ordinal) && failure.Contains('2', StringComparison.Ordinal));
        Assert.Contains(LaptopCampaign.ShapeFailures(Run(run => run["steps"]!.AsArray().RemoveAt(0))), static failure => failure.StartsWith("steps:", StringComparison.Ordinal) && failure.Contains("35", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Spec", "REQ-021")]
    public void An_empty_or_missing_judge_array_fails_and_names_the_field()
    {
        Assert.Contains(LaptopCampaign.ShapeFailures(Run(run => run["judge"] = new JsonArray())), static failure => failure.StartsWith("judge", StringComparison.Ordinal));
        Assert.Contains(LaptopCampaign.ShapeFailures(Run(run => run.Remove("judge"))), static failure => failure.StartsWith("judge", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Spec", "REQ-021")]
    public void A_step_that_is_not_ok_fails_with_its_index_and_a_long_tail_is_counted()
    {
        IReadOnlyList<string> one = LaptopCampaign.StepFailures(Run(run => run["steps"]![7]!["outcome"] = "failed"));
        Assert.Single(one);
        Assert.StartsWith("steps[7].outcome", one[0], StringComparison.Ordinal);

        IReadOnlyList<string> many = LaptopCampaign.StepFailures(Run(run =>
        {
            foreach (JsonNode? step in run["steps"]!.AsArray())
            {
                step!["outcome"] = "failed";
            }
        }));
        Assert.Equal(4, many.Count);
        Assert.Contains("33 more steps", many[^1], StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Spec", "REQ-018")]
    public void Hosted_steps_with_not_applicable_energy_and_a_reason_pass() => Assert.Empty(LaptopCampaign.HostedFailures(Run()));

    [Fact]
    [Trait("Spec", "REQ-018")]
    public void A_hosted_step_with_a_measured_energy_value_or_no_reason_fails_and_names_the_step()
    {
        IReadOnlyList<string> measured = LaptopCampaign.HostedFailures(Run(run => run["steps"]![2]!["grossJoules"] = Metric("measured", 4.5)));
        Assert.Single(measured);
        Assert.StartsWith("steps[2].grossJoules", measured[0], StringComparison.Ordinal);

        IReadOnlyList<string> noReason = LaptopCampaign.HostedFailures(Run(run => run["steps"]![5]!["netJoules"] = Metric("N/A", null)));
        Assert.Single(noReason);
        Assert.StartsWith("steps[5].netJoules", noReason[0], StringComparison.Ordinal);

        Assert.NotEmpty(LaptopCampaign.HostedFailures(Run(hosted: false)));
    }

    [Fact]
    [Trait("Spec", "REQ-021")]
    public void A_local_file_with_a_baseline_a_load_time_an_offload_a_power_source_and_a_runtime_passes() => Assert.Empty(LaptopCampaign.LocalFailures(Run()));

    [Theory]
    [InlineData("baseline.seconds")]
    [InlineData("environment.modelLoadSeconds")]
    [InlineData("environment.gpuOffloadPercent")]
    [InlineData("environment.powerSource")]
    [InlineData("environment.localRuntime")]
    [Trait("Spec", "REQ-021")]
    public void A_local_file_that_lacks_a_field_of_criterion_4_fails_and_names_it(string field)
    {
        IReadOnlyList<string> failures = LaptopCampaign.LocalFailures(Run(run =>
        {
            string[] path = field.Split('.');
            run[path[0]]!.AsObject().Remove(path[1]);
        }));

        Assert.Contains(failures, failure => failure.StartsWith(field, StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Spec", "REQ-021")]
    public void A_baseline_of_20_seconds_and_a_power_source_of_wall_fail_with_both_values()
    {
        IReadOnlyList<string> failures = LaptopCampaign.LocalFailures(Run(run =>
        {
            run["baseline"]!["seconds"] = 20;
            run["environment"]!["powerSource"] = "wall";
        }));

        Assert.Contains(failures, static failure => failure.StartsWith("baseline.seconds", StringComparison.Ordinal) && failure.Contains("30", StringComparison.Ordinal) && failure.Contains("20", StringComparison.Ordinal));
        Assert.Contains(failures, static failure => failure.StartsWith("environment.powerSource", StringComparison.Ordinal) && failure.Contains("wall", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Spec", "REQ-021")]
    public void The_counted_names_are_the_run_file_names_and_nothing_else()
    {
        using var workspace = TempWorkspace.CreateEmpty();
        string[] counted = ["20261025T131930Z-local.json", "20261025T133440Z-nebius.json", "20261025T133441Z-nvidia.json", "20261025T133442Z-cloudgpu.json", "20261025T133443Z-local-baseline.json"];
        string[] ignored = ["probe-20261025T131800Z.json", "calibration-20261025T105451Z.json", "20261025T131930Z-local.json.partial", "20261025T131930Z-other.json", "2026102T131930Z-local.json", "x20261025T131930Z-local.json"];
        foreach (string name in counted.Concat(ignored))
        {
            _ = workspace.WriteFile(Path.Combine("results", name), "{}");
        }

        Assert.Equal(counted.Order(StringComparer.Ordinal), LaptopCampaign.CommittedRunFiles(Path.Combine(workspace.Root, "results")).Select(static path => Path.GetFileName(path)));
        Assert.Empty(LaptopCampaign.CommittedRunFiles(Path.Combine(workspace.Root, "missing")));
    }

    [Fact]
    [Trait("Spec", "REQ-021")]
    public void No_tier_is_pending_and_a_folder_without_a_file_gives_no_path()
    {
        using var workspace = TempWorkspace.CreateEmpty();

        Assert.Empty(LaptopCampaign.PendingTiers);
        Assert.All(LaptopCampaign.Tiers, tier => Assert.Null(LaptopCampaign.LatestRunFile(tier, Path.Combine(workspace.Root, "results"))));
    }
}