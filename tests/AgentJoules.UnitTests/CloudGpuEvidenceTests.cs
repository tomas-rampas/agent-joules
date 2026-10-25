using System.Text.Json.Nodes;

using AgentJoules.Reporting.Results;
using AgentJoules.TestSupport;

using Xunit;
using Xunit.Abstractions;

namespace AgentJoules.UnitTests;

/// <summary>
/// AJ-125, AJ-126 and AJ-127 (REQ-031): the committed cloudgpu evidence and the checks of <see cref="CloudGpuEvidence"/> that the gated classes <c>CloudGpuProbeTests</c>, <c>CloudGpuNimTests</c> and <c>CloudGpuRunTests</c> and the
/// open-gate steps apply to it. One fact runs on every build over every committed <c>results/probe-*-cloudgpu.json</c> and <c>results/*-cloudgpu.json</c> file of the HEAD tree (zero files until the VM stories commit them: the fact then
/// records "0 files" and passes, it is not a theory with an empty member data set). The other facts prove the checks over small documents built in memory, so a wrong check would show before the VM exists.
/// </summary>
[Trait("Spec", "REQ-031")]
public sealed class CloudGpuEvidenceTests(ITestOutputHelper output)
{
    private const string Digest = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private readonly ITestOutputHelper _output = output;

    private static JsonObject Probe(Action<JsonObject>? change = null)
    {
        var probe = new JsonObject
        {
            ["gpuPresent"] = true,
            ["name"] = "NVIDIA L40S",
            ["gpuUuid"] = "GPU-00000000-0000-0000-0000-000000000000",
            ["driverVersion"] = "580.95",
            ["computeCapability"] = "8.9",
            ["vbiosVersion"] = "95.02.5C.00.01",
            ["powerLimitW"] = 350.0,
            ["migMode"] = "disabled",
            ["energyCounterSupported"] = true,
            ["method"] = "counter",
            ["sampleRateHz"] = 10,
        };
        change?.Invoke(probe);
        return probe;
    }

    private static JsonObject Run(Action<JsonObject>? change = null)
    {
        JsonArray steps = [];
        for (int index = 0; index < CloudGpuEvidence.StepCount; index++)
        {
            steps.Add(new JsonObject { ["outcome"] = "ok" });
        }

        var run = new JsonObject
        {
            ["run"] = new JsonObject { ["status"] = "completed", ["tier"] = "cloudgpu", ["repeat"] = 3, ["tasks"] = new JsonArray("incident-brief", "api-migration", "energy-explainer") },
            ["steps"] = steps,
            ["baseline"] = new JsonObject { ["seconds"] = 30.003 },
            ["environment"] = new JsonObject
            {
                ["provider"] = "nebius-ai-cloud",
                ["precision"] = "FP8",
                ["instancePreset"] = "1gpu-16vcpu-64gb",
                ["region"] = "eu-north1",
                ["gpuUuid"] = "GPU-00000000-0000-0000-0000-000000000000",
                ["containerImage"] = "nvcr.io/nim/nvidia/nemotron-3-nano:latest",
                ["containerDigest"] = Digest,
            },
        };
        change?.Invoke(run);
        return run;
    }

    [Fact]
    public void Every_committed_cloudgpu_probe_and_run_file_validates_and_zero_files_is_recorded_as_such()
    {
        IReadOnlyList<string> probes = CloudGpuEvidence.CommittedProbeFiles();
        IReadOnlyList<string> runs = CloudGpuEvidence.CommittedRunFiles();

        List<string> failures = [];
        failures.AddRange(probes.SelectMany(static file => CloudGpuEvidence.ProbeFailures(Path.GetFileName(file), File.ReadAllText(file)).Select(failure => $"{Path.GetFileName(file)}: {failure}")));
        failures.AddRange(runs.SelectMany(static file => RunFileValidator.Check(Path.GetFileName(file), File.ReadAllText(file))));

        int count = probes.Count + runs.Count;
        string message = $"{count} files ({probes.Count} probe, {runs.Count} run) in the HEAD tree";
        _output.WriteLine(message);
        Assert.True(failures.Count == 0, message + ":\n" + string.Join('\n', failures));
    }

    [Fact]
    public void A_probe_of_an_L40S_with_a_disabled_MIG_and_a_boolean_counter_passes() =>
        Assert.Empty(CloudGpuEvidence.ProbeFailures("probe.json", Probe().ToJsonString()));

    [Theory]
    [InlineData("name", "NVIDIA GeForce RTX 3080 Ti Laptop GPU", "name")]
    [InlineData("migMode", "enabled", "migMode")]
    [InlineData("gpuUuid", "", "gpuUuid")]
    [InlineData("energyCounterSupported", "yes", "energyCounterSupported")]
    public void A_probe_that_breaks_a_field_rule_is_refused_with_the_field_named(string member, string value, string expected)
    {
        JsonObject probe = Probe(p => p[member] = value);

        IReadOnlyList<string> failures = CloudGpuEvidence.ProbeFailures("probe.json", probe.ToJsonString());

        Assert.Contains(failures, failure => failure.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void A_probe_that_is_not_JSON_is_refused() => Assert.Single(CloudGpuEvidence.ProbeFailures("probe.json", "not json"));

    [Fact]
    public void A_probe_with_a_schema_only_defect_is_refused_on_the_probe_definition()
    {
        // The field rules of the story do not name computeCapability; the schema requires it.
        JsonObject probe = Probe(p => p.Remove("computeCapability"));

        IReadOnlyList<string> failures = CloudGpuEvidence.ProbeFailures("probe.json", probe.ToJsonString());

        Assert.Contains(failures, static failure => failure.StartsWith("$defs/probe", StringComparison.Ordinal));
        Assert.DoesNotContain(failures, static failure => !failure.StartsWith("$defs/probe", StringComparison.Ordinal));
    }

    [Fact]
    public void A_run_file_the_validator_refuses_is_reported_by_RunFailures_apart_from_the_field_rules()
    {
        // The in-memory run has the cloudgpu shape (no field failure) but is not a full run file: the failures can only come from RunFileValidator.
        JsonObject run = Run();
        Assert.Empty(CloudGpuEvidence.RunFieldFailures(run));

        IReadOnlyList<string> failures = CloudGpuEvidence.RunFailures("20261026T090000Z-cloudgpu.json", run.ToJsonString());

        Assert.NotEmpty(failures);
        Assert.Equal(RunFileValidator.Check("20261026T090000Z-cloudgpu.json", run.ToJsonString()), failures);

        // A file with a field defect and the validator's complaints reports both halves.
        IReadOnlyList<string> both = CloudGpuEvidence.RunFailures("20261026T090000Z-cloudgpu.json", Run(r => r["environment"]!["region"] = "eu-west1").ToJsonString());
        Assert.Contains(both, static failure => failure.StartsWith("environment.region", StringComparison.Ordinal));
        Assert.True(both.Count > 1);
    }

    [GitFact]
    public void The_committed_probe_and_run_files_of_a_scratch_repository_are_found_and_the_untracked_and_other_tier_files_are_not()
    {
        using var workspace = TempWorkspace.CreateEmpty();
        string probePath = workspace.WriteFile(Path.Combine("results", "probe-20261026T090000Z-cloudgpu.json"), Probe().ToJsonString());
        string runPath = workspace.WriteFile(Path.Combine("results", "20261026T091000Z-cloudgpu.json"), Run().ToJsonString());
        _ = workspace.WriteFile(Path.Combine("results", "20261026T091000Z-local.json"), "{}");
        _ = workspace.WriteFile(Path.Combine("results", "probe-20261026T090000Z-local.json"), "{}");
        InitAndCommit(workspace.Root);
        _ = workspace.WriteFile(Path.Combine("results", "probe-20261027T090000Z-cloudgpu.json"), Probe().ToJsonString());

        IReadOnlyList<string> probes = CloudGpuEvidence.CommittedProbeFiles(workspace.Root);
        IReadOnlyList<string> runs = CloudGpuEvidence.CommittedRunFiles(workspace.Root);
        _output.WriteLine($"scratch repository: {probes.Count} probe file, {runs.Count} run file");

        _ = Assert.Single(probes);
        _ = Assert.Single(runs);
        Assert.Equal(Path.GetFullPath(probePath), probes[0]);
        Assert.Equal(Path.GetFullPath(runPath), runs[0]);
        Assert.Equal(Path.GetFullPath(probePath), CloudGpuEvidence.NewestProbe(workspace.Root));
        Assert.Equal(Path.GetFullPath(runPath), CloudGpuEvidence.NewestRun(workspace.Root));
    }

    [GitFact]
    public void A_committed_cloudgpu_run_file_that_cannot_be_used_is_a_problem_and_not_an_absence()
    {
        using var workspace = TempWorkspace.CreateEmpty();
        _ = workspace.WriteFile(Path.Combine("results", "20261026T091000Z-cloudgpu.json"), "not json");
        InitAndCommit(workspace.Root);

        Assert.Single(CloudGpuEvidence.CommittedRunFiles(workspace.Root));
        _ = Assert.Throws<InvalidDataException>(() => CloudGpuEvidence.NewestRun(workspace.Root));

        // The gated facts treat absence as a skip and a problem file as a failure: AbsentReason lets the problem through instead of turning it into a reason.
        _ = Assert.Throws<InvalidDataException>(() => CloudGpuEvidence.AbsentReason(CloudGpuEvidenceKind.Run, workspace.Root));
    }

    [GitFact]
    public void A_scratch_repository_without_cloudgpu_files_has_the_absent_reasons_of_the_probe_and_the_run()
    {
        using var workspace = TempWorkspace.CreateEmpty();
        _ = workspace.WriteFile("readme.txt", "nothing\n");
        InitAndCommit(workspace.Root);

        Assert.Equal(CloudGpuEvidence.NoProbeMessage, CloudGpuEvidence.AbsentReason(CloudGpuEvidenceKind.Probe, workspace.Root));
        Assert.Equal(CloudGpuEvidence.NoRunMessage, CloudGpuEvidence.AbsentReason(CloudGpuEvidenceKind.Run, workspace.Root));
    }

    [GitFact]
    public void An_ssh_value_is_found_as_a_host_token_or_as_user_at_and_a_user_name_in_a_longer_name_is_not()
    {
        using var workspace = TempWorkspace.CreateEmpty();
        string user = "u" + Guid.NewGuid().ToString("N")[..8];
        string host = "vm-" + Guid.NewGuid().ToString("N")[..8] + ".example.invalid";
        _ = workspace.WriteFile("longer.txt", $"runs-on: {user}-latest and image {user}24.04 and host {host}x\n");
        InitAndCommit(workspace.Root);

        Assert.False(CloudGpuEvidence.RepositoryHoldsSshValue(workspace.Root, "CLOUDGPU_SSH_USER", user));
        Assert.False(CloudGpuEvidence.RepositoryHoldsSshValue(workspace.Root, "CLOUDGPU_SSH_HOST", host));

        _ = workspace.WriteFile("target.txt", $"ssh {user}@elsewhere\n");
        Assert.True(CloudGpuEvidence.RepositoryHoldsSshValue(workspace.Root, "CLOUDGPU_SSH_USER", user));

        // The dots of the host are literal: the host with each dot replaced by a letter is another name.
        _ = workspace.WriteFile("dots.txt", $"ssh somebody@{host.Replace('.', 'x')}\n");
        Assert.False(CloudGpuEvidence.RepositoryHoldsSshValue(workspace.Root, "CLOUDGPU_SSH_HOST", host));

        _ = workspace.WriteFile("host.txt", $"ssh somebody@{host}\n");
        Assert.True(CloudGpuEvidence.RepositoryHoldsSshValue(workspace.Root, "CLOUDGPU_SSH_HOST", host));
    }

    [GitFact]
    public void The_host_is_matched_in_any_case_and_the_user_name_only_in_its_case()
    {
        using var workspace = TempWorkspace.CreateEmpty();
        string user = "u" + Guid.NewGuid().ToString("N")[..8];
        string host = "vm-" + Guid.NewGuid().ToString("N")[..8] + ".example.invalid";
        _ = workspace.WriteFile("upper.txt", $"ssh {user.ToUpperInvariant()}@elsewhere and host {host.ToUpperInvariant()}\n");
        InitAndCommit(workspace.Root);

        Assert.True(CloudGpuEvidence.RepositoryHoldsSshValue(workspace.Root, "CLOUDGPU_SSH_HOST", host));
        Assert.False(CloudGpuEvidence.RepositoryHoldsSshValue(workspace.Root, "CLOUDGPU_SSH_USER", user));
    }

    private static void InitAndCommit(string root)
    {
        Git(root, "init", "--quiet", root);
        Git(root, "add", "--all");
        Git(root, "-c", "user.name=k2", "-c", "user.email=k2@example.invalid", "-c", "commit.gpgsign=false", "-c", "core.hooksPath=" + Path.Combine(root, "no-hooks"), "commit", "--quiet", "--no-verify", "-m", "scratch");
    }

    private static void Git(string root, params string[] arguments)
    {
        GitResult result = GitRunner.Run(root, arguments);
        Assert.True(result.ExitCode == 0, $"git {arguments[0]} exited with {result.ExitCode}");
    }

    [Fact]
    public void A_run_with_the_cloudgpu_shape_passes_the_field_rules() => Assert.Empty(CloudGpuEvidence.RunFieldFailures(Run()));

    [Fact]
    public void A_partial_run_needs_a_stop_reason()
    {
        JsonObject partial = Run(r => r["run"]!["status"] = "partial");
        JsonObject stopped = Run(r =>
        {
            r["run"]!["status"] = "partial";
            r["run"]!["stopReason"] = "time-box";
        });

        Assert.Contains(CloudGpuEvidence.RunFieldFailures(partial), static failure => failure.StartsWith("run.stopReason", StringComparison.Ordinal));
        Assert.Empty(CloudGpuEvidence.RunFieldFailures(stopped));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(20, true)]
    [InlineData(36, true)]
    [InlineData(0, false)]
    [InlineData(37, false)]
    public void A_partial_run_with_a_stop_reason_holds_one_to_36_steps(int count, bool accepted)
    {
        JsonObject run = Run(r =>
        {
            r["run"]!["status"] = "partial";
            r["run"]!["stopReason"] = "cost-cap";
            r["steps"] = new JsonArray([.. Enumerable.Range(0, count).Select(static _ => (JsonNode?)new JsonObject { ["outcome"] = "ok" })]);
        });

        Assert.Equal(accepted, CloudGpuEvidence.RunFieldFailures(run).Count == 0);
    }

    [Fact]
    public void A_completed_run_and_a_partial_run_without_a_stop_reason_still_need_36_steps()
    {
        JsonObject completed = Run(r => r["steps"]!.AsArray().RemoveAt(0));
        JsonObject noReason = Run(r =>
        {
            r["run"]!["status"] = "partial";
            r["steps"]!.AsArray().RemoveAt(0);
        });

        Assert.Contains(CloudGpuEvidence.RunFieldFailures(completed), static failure => failure.StartsWith("steps", StringComparison.Ordinal));
        Assert.Contains(CloudGpuEvidence.RunFieldFailures(noReason), static failure => failure.StartsWith("steps", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("provider", "nebius", "environment.provider")]
    [InlineData("precision", "BF16", "environment.precision")]
    [InlineData("region", "eu-west1", "environment.region")]
    [InlineData("instancePreset", "", "environment.instancePreset")]
    [InlineData("containerDigest", "latest", "environment.containerDigest")]
    public void A_run_that_breaks_an_environment_rule_is_refused_with_the_field_named(string member, string value, string expected)
    {
        JsonObject run = Run(r => r["environment"]![member] = value);

        Assert.Contains(CloudGpuEvidence.RunFieldFailures(run), failure => failure.StartsWith(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void A_run_with_a_short_baseline_or_35_steps_is_refused()
    {
        JsonObject run = Run(r =>
        {
            r["baseline"]!["seconds"] = 29.9;
            r["steps"]!.AsArray().RemoveAt(0);
        });

        IReadOnlyList<string> failures = CloudGpuEvidence.RunFieldFailures(run);

        Assert.Contains(failures, static failure => failure.StartsWith("baseline.seconds", StringComparison.Ordinal));
        Assert.Contains(failures, static failure => failure.StartsWith("steps", StringComparison.Ordinal));
    }

    [Fact]
    public void A_section_runs_from_its_heading_to_the_next_heading()
    {
        const string document = "# Title\n\n## Instance\npreset a\n\n## Commands\ndocker logout nvcr.io\n\n## Runs\ntmux\n";

        Assert.Equal("docker logout nvcr.io\n", CloudGpuEvidence.Section(document, "Commands"));
        Assert.Null(CloudGpuEvidence.Section(document, "Cost"));
    }

    [Theory]
    [InlineData("docker login --password-stdin", false)]
    [InlineData("key nvapi-abc", true)]
    [InlineData("token Ab0123456789abcdefABCDEF0123456789abcdefABCDEF0123456789abcdef", true)]
    [InlineData("ContainerDigest sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", false)]
    [InlineData("profile 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", false)]
    [InlineData("the nvapi- prefix", false)]
    [InlineData("nvapi-x", true)]
    public void The_secret_shape_is_a_key_prefix_or_a_long_mixed_token_but_not_a_digest_or_a_profile_id(string text, bool expected) =>
        Assert.Equal(expected, CloudGpuEvidence.HasSecretShape(text));

    [Fact]
    public void The_pending_cloudgpu_section_is_not_verified_and_a_verified_one_needs_a_date_a_model_and_a_command()
    {
        const string pending = "## cloudgpu\n\nStatus: pending (verified in M5, AJ-126)\n";
        const string verified = "## cloudgpu\n\nVerified: 2026-10-26\nCommand: curl -sS models\n- model: nvidia/nemotron-3-nano\nStreaming usage: reported\nReasoning result: off\n\n## nvidia\n";
        const string old = "## cloudgpu\n\nVerified: 2026-10-16\n- model: nvidia/nemotron-3-nano\n";

        Assert.False(CloudGpuEvidence.EndpointsSectionVerified(pending));
        Assert.True(CloudGpuEvidence.EndpointsSectionVerified(verified));
        Assert.Empty(CloudGpuEvidence.EndpointsFailures(verified, withMeasurements: true));
        Assert.Contains(CloudGpuEvidence.EndpointsFailures(old), static failure => failure.StartsWith("Verified:", StringComparison.Ordinal));
        Assert.Contains(CloudGpuEvidence.EndpointsFailures(old), static failure => failure.StartsWith("Command:", StringComparison.Ordinal));
    }
}