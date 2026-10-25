using AgentJoules.Core.Configuration;
using AgentJoules.Core.Runs;
using AgentJoules.TestSupport;

using Xunit;
using Xunit.Abstractions;

namespace AgentJoules.IntegrationTests;

/// <summary>
/// The gated check behind the laptop measurement campaign of AJ-114 (REQ-018, REQ-020, REQ-021, design 6.3): the newest committed run file of each tier has the shape of criterion 3 (status completed, 3 tasks,
/// <c>run.repeat</c> 3, 36 steps, a non-empty <c>judge</c> array, outcome <c>ok</c> on every step), the local file carries the evidence of criterion 4 (baseline of at least 30 s, a numeric load time and offload, a
/// known power source and runtime), the hosted files carry no energy value, every counted file validates against <c>$defs/run</c> through the production <c>SchemaValidator</c>, and the count of the counted
/// files is what <c>AJ validate results/</c> would print. The tests read committed files only: they never run the campaign, never call a model, read no key and write nothing. They are skipped unless
/// AGENTJOULES_INTEGRATION is 1 (<see cref="IntegrationFactAttribute"/>), with the message of the gate. A missing file fails with a message that names the story that commits it, except for a tier in
/// <see cref="LaptopCampaign.PendingTiers"/>, whose missing file is a note in the output. Only counts and file names are written to the output, never a key.
/// </summary>
[Trait("Spec", "REQ-018")]
[Trait("Spec", "REQ-020")]
[Trait("Spec", "REQ-021")]
public sealed class LaptopCampaignTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    private void AssertShape(Tier tier)
    {
        if (LaptopCampaign.FindOrNote(tier, _output.WriteLine) is not { } path)
        {
            return;
        }

        IReadOnlyList<string> failures = LaptopCampaign.ShapeFailures(LaptopCampaign.Read(path));
        _output.WriteLine($"{Path.GetFileName(path)}: {failures.Count} shape failures");
        Assert.True(failures.Count == 0, string.Join('\n', failures));
    }

    [IntegrationFact]
    public void The_newest_local_file_is_completed_with_3_tasks_3_repeats_and_36_ok_steps() => AssertShape(Tier.Local);

    [IntegrationFact]
    public void The_newest_nvidia_file_is_completed_with_3_tasks_3_repeats_and_36_ok_steps() => AssertShape(Tier.Nvidia);

    [IntegrationFact]
    public void The_newest_nebius_file_is_completed_with_3_tasks_3_repeats_and_36_ok_steps() => AssertShape(Tier.Nebius);

    [IntegrationFact]
    public void The_newest_local_file_carries_the_baseline_load_time_offload_power_source_and_runtime()
    {
        string path = LaptopCampaign.FindOrNote(Tier.Local, _output.WriteLine)!;
        IReadOnlyList<string> failures = LaptopCampaign.LocalFailures(LaptopCampaign.Read(path));
        _output.WriteLine($"{Path.GetFileName(path)}: {failures.Count} local-evidence failures");
        Assert.True(failures.Count == 0, string.Join('\n', failures));
    }

    [IntegrationFact]
    public void Every_step_of_the_hosted_files_has_gross_and_net_joules_as_not_applicable_with_a_reason()
    {
        foreach (Tier tier in LaptopCampaign.HostedTiers)
        {
            if (LaptopCampaign.FindOrNote(tier, _output.WriteLine) is not { } path)
            {
                continue;
            }

            IReadOnlyList<string> failures = LaptopCampaign.HostedFailures(LaptopCampaign.Read(path));
            _output.WriteLine($"{Path.GetFileName(path)}: {failures.Count} energy-value failures");
            Assert.True(failures.Count == 0, TierOptions.NameOf(tier) + ":\n" + string.Join('\n', failures));
        }
    }

    [IntegrationFact]
    public void The_newest_file_of_each_tier_validates_against_the_run_definition()
    {
        foreach (Tier tier in LaptopCampaign.Tiers)
        {
            if (LaptopCampaign.FindOrNote(tier, _output.WriteLine) is not { } path)
            {
                continue;
            }

            IReadOnlyList<string> failures = LaptopCampaign.SchemaFailures(path);
            _output.WriteLine($"{Path.GetFileName(path)}: {failures.Count} schema failures");
            Assert.True(failures.Count == 0, Path.GetFileName(path) + ":\n" + string.Join('\n', failures));
        }
    }

    [IntegrationFact]
    public void The_validator_accepts_every_counted_file_so_that_validate_prints_the_file_count()
    {
        IReadOnlyList<string> files = LaptopCampaign.CommittedRunFiles();
        Assert.NotEmpty(files);

        // "AJ validate results/" prints "Validated <n> files" with n the count of the name rule, and exits 0 only when every counted file is valid.
        string[] failing = [.. files.Where(static path => LaptopCampaign.SchemaFailures(path).Count > 0).Select(static path => Path.GetFileName(path))];
        _output.WriteLine($"{files.Count} counted files; {failing.Length} fail the validator");
        Assert.True(failing.Length == 0, "the validator rejects: " + string.Join(", ", failing));
    }
}