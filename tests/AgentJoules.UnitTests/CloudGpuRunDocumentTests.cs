using System.Security.Cryptography;

using AgentJoules.TestSupport;
using AgentJoules.TestSupport.Docs;

using Xunit;
using Xunit.Abstractions;

namespace AgentJoules.UnitTests;

/// <summary>
/// AJ-128 (REQ-031, EDGE-016, EDGE-017): the contract of <c>docs/cloudgpu-run.md</c>. The documents are built in memory (<see cref="CloudGpuRunDraft"/>) and read by <see cref="CloudGpuRunDocumentParser"/>; a pass is an
/// empty list of problems, and a problem names the section or the line and never shows an address, a key or a host. The last fact runs the contract on the committed document and passes with a note while the file is
/// not in the HEAD tree.
/// </summary>
[Trait("Spec", "REQ-031")]
[Trait("Spec", "EDGE-016")]
[Trait("Spec", "EDGE-017")]
public sealed class CloudGpuRunDocumentTests(ITestOutputHelper output)
{
    private const string NotCommittedNote = "not committed yet (AJ-125 to AJ-128)";
    private const string Alphanumerics = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    private static readonly Dictionary<string, string?> NoEnvironment = [];

    private readonly ITestOutputHelper _output = output;

    private static CloudGpuRunDocumentParser Read(CloudGpuRunDraft draft) => CloudGpuRunDocumentParser.Parse(draft.Render(), NoEnvironment);

    private static string Joined(CloudGpuRunDocumentParser parser) => string.Join('\n', parser.Problems);

    private static string RandomSuffix(int length) => RandomNumberGenerator.GetString(Alphanumerics, length);

    // ---- the scenarios of the story ----

    [Fact]
    public void A_complete_document_passes()
    {
        CloudGpuRunDocumentParser parser = Read(new CloudGpuRunDraft());

        Assert.True(parser.Passed, Joined(parser));
        Assert.Empty(parser.Problems);
    }

    [Fact]
    public void A_cost_of_exactly_30_USD_passes()
    {
        CloudGpuRunDocumentParser parser = Read(new CloudGpuRunDraft().Set("Cost", [.. CloudGpuRunDraft.CostLines("30.00")]));

        Assert.True(parser.Passed, Joined(parser));
    }

    [Fact]
    public void A_cost_with_the_currency_after_the_number_passes()
    {
        CloudGpuRunDocumentParser parser = Read(new CloudGpuRunDraft().Set("Cost", "Observed cost: 6.12 USD (billing page).", "Stop reason: cost-cap."));

        Assert.True(parser.Passed, Joined(parser));
    }

    [Fact]
    public void A_missing_Teardown_heading_fails_and_names_it()
    {
        CloudGpuRunDocumentParser parser = Read(new CloudGpuRunDraft().Remove("Teardown"));

        Assert.False(parser.Passed);
        Assert.Contains("Teardown", Assert.Single(parser.Problems), StringComparison.Ordinal);
        Assert.False(parser.Teardown.Present);
    }

    [Fact]
    public void A_cost_above_the_cap_fails_and_names_the_30_USD_cap()
    {
        CloudGpuRunDocumentParser parser = Read(new CloudGpuRunDraft().Set("Cost", [.. CloudGpuRunDraft.CostLines("31.20")]));

        Assert.Contains("30 USD cap", Assert.Single(parser.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void A_pending_teardown_fails_and_says_it_is_not_confirmed()
    {
        CloudGpuRunDocumentParser parser = Read(new CloudGpuRunDraft().Set("Teardown", "pending user confirmation"));

        Assert.False(parser.Passed);
        Assert.Contains("the teardown is not confirmed", Assert.Single(parser.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void An_IPv4_literal_fails_without_echoing_the_address()
    {
        const string address = "203.0.113.77";
        CloudGpuRunDocumentParser parser = Read(new CloudGpuRunDraft().Add("Commands", "curl " + address));

        string problem = Assert.Single(parser.Problems);
        Assert.Contains("IPv4", problem, StringComparison.Ordinal);
        Assert.DoesNotContain(address, problem, StringComparison.Ordinal);
        Assert.DoesNotContain("203", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_five_part_version_is_not_an_IPv4_literal()
    {
        CloudGpuRunDocumentParser parser = Read(new CloudGpuRunDraft().Add("Probe", "vbios 95.02.66.00.01"));

        Assert.True(parser.Passed, string.Join("; ", parser.Problems));
    }

    [Fact]
    public void A_runtime_built_nvapi_key_fails_without_echoing_the_suffix()
    {
        string suffix = RandomSuffix(32);
        CloudGpuRunDocumentParser parser = Read(new CloudGpuRunDraft().Add("Commands", "export NVIDIA_API_KEY=nvapi-" + suffix));

        string problem = Assert.Single(parser.Problems);
        Assert.Contains("a key-like token at line", problem, StringComparison.Ordinal);
        Assert.DoesNotContain(suffix, problem, StringComparison.Ordinal);
    }

    // ---- the other rules ----

    [Fact]
    public void A_long_base64_like_token_fails_as_a_key()
    {
        string token = RandomSuffix(24) + "a1B2" + RandomSuffix(20);
        CloudGpuRunDocumentParser parser = Read(new CloudGpuRunDraft().Add("Probe", "token " + token));

        string problem = Assert.Single(parser.Problems);
        Assert.Contains("a key-like token at line", problem, StringComparison.Ordinal);
        Assert.DoesNotContain(token[..12], problem, StringComparison.Ordinal);
    }

    [Fact]
    public void An_image_digest_is_not_a_key()
    {
        CloudGpuRunDocumentParser parser = Read(new CloudGpuRunDraft().Add("Probe", "image digest sha256:" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32))));

        Assert.True(parser.Passed, Joined(parser));
    }

    [Fact]
    public void An_environment_key_value_fails_without_echoing_it()
    {
        string value = "k" + RandomSuffix(14);
        Dictionary<string, string?> environment = new() { ["NGC_API_KEY"] = value, ["NVIDIA_API_KEY"] = null, ["NEBIUS_API_KEY"] = string.Empty };
        string text = new CloudGpuRunDraft().Add("Probe", "see " + value + ".").Render();

        CloudGpuRunDocumentParser parser = CloudGpuRunDocumentParser.Parse(text, environment);

        string problem = Assert.Single(parser.Problems);
        Assert.Contains("a key-like token at line", problem, StringComparison.Ordinal);
        Assert.DoesNotContain(value, problem, StringComparison.Ordinal);
    }

    [Fact]
    public void An_environment_key_value_inside_a_longer_word_is_a_hit_because_a_key_is_matched_as_a_substring()
    {
        string value = "k" + RandomSuffix(14);
        Dictionary<string, string?> environment = new() { ["NEBIUS_API_KEY"] = value };
        string text = new CloudGpuRunDraft().Add("Probe", "prefix" + value + "suffix").Render();

        CloudGpuRunDocumentParser parser = CloudGpuRunDocumentParser.Parse(text, environment);

        string problem = Assert.Single(parser.Problems);
        Assert.Contains("a key-like token at line", problem, StringComparison.Ordinal);
        Assert.DoesNotContain(value, problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ubuntu", "runs-on: ubuntu-latest", false)]
    [InlineData("ubuntu", "image ubuntu24.04-cuda12.0", false)]
    [InlineData("ubuntu", "log in as ubuntu now", true)]
    [InlineData("ab", "log in as ab now", true)]
    public void An_ssh_value_matches_on_word_boundaries_with_no_length_floor(string value, string line, bool hit)
    {
        Dictionary<string, string?> environment = new() { ["CLOUDGPU_SSH_USER"] = value };
        string text = new CloudGpuRunDraft().Add("Probe", line).Render();

        CloudGpuRunDocumentParser parser = CloudGpuRunDocumentParser.Parse(text, environment);

        Assert.Equal(hit, !parser.Passed);
        Assert.DoesNotContain(value + " now", Joined(parser), StringComparison.Ordinal);
    }

    [Fact]
    public void An_image_reference_by_digest_is_not_an_ssh_target()
    {
        string digest = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        CloudGpuRunDocumentParser parser = Read(new CloudGpuRunDraft().Add("Commands", "nvcr.io/nim/nvidia/nemotron-3-nano@sha256:" + digest));

        Assert.True(parser.Passed, Joined(parser));
    }

    [Fact]
    public void A_name_at_sha256_without_a_hex_digest_is_still_an_ssh_target()
    {
        CloudGpuRunDocumentParser parser = Read(new CloudGpuRunDraft().Add("Commands", "ssh builder@sha256"));

        Assert.Contains("SSH target", Assert.Single(parser.Problems), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Observed cost: $31.20 (billing page); the old stop was USD 25.", true)]
    [InlineData("Observed cost: US$31.20 (billing page).", true)]
    [InlineData("Observed cost: $6.12 (billing page).", false)]
    [InlineData("Observed cost: 2026-10-31 USD 6.12 (billing page).", false)]
    [InlineData("Observed cost: 2026-10-31 USD 31.20 (billing page).", true)]
    [InlineData("Observed cost: USD 1,031.20 (billing page).", true)]
    [InlineData("Observed cost: 1,031.20 USD (billing page).", true)]
    [InlineData("Observed cost: USD 1,006.12 and USD 5.", true)]
    public void A_cost_is_read_in_any_USD_spelling_without_taking_a_date_or_a_time_for_an_amount(string line, bool aboveCap)
    {
        CloudGpuRunDocumentParser parser = Read(new CloudGpuRunDraft().Set("Cost", line, "Stop reason: no stop triggered."));

        Assert.Equal(aboveCap, parser.Problems.Any(static problem => problem.Contains("30 USD cap", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("The VM was not deleted. Final cost: USD 6.40 at 2026-11-01T11:05:00Z.", false)]
    [InlineData("The VM was NOT  deleted at 2026-11-01T11:05:00Z. USD 6.40", false)]
    [InlineData("The VM was not deleted at first; the user deleted it at 2026-11-01T11:05:00Z. Final cost: USD 6.40.", true)]
    public void A_not_deleted_teardown_is_not_confirmed(string line, bool confirmed)
    {
        CloudGpuRunDocumentParser parser = Read(new CloudGpuRunDraft().Set("Teardown", line));

        Assert.Equal(confirmed, parser.Passed);
        Assert.Equal(confirmed, !parser.Problems.Any(static problem => problem.Contains("the teardown is not confirmed", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("export NVIDIA_API_KEY=nvapi-a", true)]
    [InlineData("the nvapi- prefix is never written", false)]
    [InlineData("nvapi-", false)]
    public void An_nvapi_prefix_with_at_least_one_character_after_it_is_a_key(string line, bool hit)
    {
        CloudGpuRunDocumentParser parser = Read(new CloudGpuRunDraft().Add("Probe", line));

        Assert.Equal(hit, !parser.Passed);
    }

    [Theory]
    [InlineData("host 203.0.113.9 answered", true)]
    [InlineData("Docker Engine v1.2.3.4 is installed", false)]
    [InlineData("package version1.2.3.4", false)]
    [InlineData("kernel 6.8.0.1 booted", true)]
    public void A_four_part_version_after_a_letter_is_not_an_IPv4_literal(string line, bool hit)
    {
        CloudGpuRunDocumentParser parser = Read(new CloudGpuRunDraft().Add("Probe", line));

        Assert.Equal(hit, parser.Problems.Any(static problem => problem.Contains("IPv4", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("route 2001:db8::1 via the gateway", true)]
    [InlineData("route fe80:0:0:0:204:61ff:fe9d:f156 via the gateway", true)]
    [InlineData("ended 2026-11-01T10:40:00Z, started 2026-11-01T09:10:00Z", false)]
    public void An_IPv6_literal_fails_without_echoing_it(string line, bool hit)
    {
        CloudGpuRunDocumentParser parser = Read(new CloudGpuRunDraft().Add("Probe", line));

        Assert.Equal(hit, parser.Problems.Any(static problem => problem.Contains("IPv6", StringComparison.Ordinal)));
        Assert.DoesNotContain("db8", Joined(parser), StringComparison.Ordinal);
        Assert.DoesNotContain("204:61ff", Joined(parser), StringComparison.Ordinal);
    }

    [Fact]
    public void A_NIM_profile_id_of_64_hex_digits_is_not_a_key()
    {
        string profile = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        CloudGpuRunDocumentParser parser = Read(new CloudGpuRunDraft().Add("Commands", "NIM_MODEL_PROFILE=" + profile));

        Assert.True(parser.Passed, Joined(parser));
        Assert.False(CloudGpuEvidence.HasSecretShape("profile " + profile));
    }

    [Fact]
    public void An_incidents_none_line_is_no_incident_and_needs_no_retry_statement()
    {
        CloudGpuRunDocumentParser parser = Read(new CloudGpuRunDraft().Add("Runs", "- Incidents: none."));

        Assert.True(parser.Passed, Joined(parser));
    }

    [Theory]
    [InlineData("The guard has a cost-cap and a time-box.", false)]
    [InlineData("Stop reason: cost-cap.", true)]
    [InlineData("stop reason - time-box", true)]
    [InlineData("No stop triggered.", true)]
    public void The_stop_reason_is_the_one_stated_on_the_line_that_states_the_stop(string line, bool stated)
    {
        CloudGpuRunDocumentParser parser = Read(new CloudGpuRunDraft().Set("Cost", "Observed cost: USD 6.12 (billing page).", line));

        Assert.Equal(stated, parser.Passed);
    }

    [Fact]
    public void The_ssh_target_only_passes_as_the_two_variables()
    {
        CloudGpuRunDocumentParser parser = Read(new CloudGpuRunDraft().Add("Commands", "scp -r out $CLOUDGPU_SSH_USER@$CLOUDGPU_SSH_HOST:~/aj"));

        Assert.True(parser.Passed, Joined(parser));
    }

    [Fact]
    public void Another_ssh_target_fails_without_echoing_it()
    {
        CloudGpuRunDocumentParser parser = Read(new CloudGpuRunDraft().Add("Commands", "ssh builder@gpu-box-7"));

        string problem = Assert.Single(parser.Problems);
        Assert.Contains("$CLOUDGPU_SSH_USER@$CLOUDGPU_SSH_HOST", problem, StringComparison.Ordinal);
        Assert.DoesNotContain("builder", problem, StringComparison.Ordinal);
        Assert.DoesNotContain("gpu-box-7", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void An_environment_ssh_value_fails_without_echoing_it()
    {
        string host = "vm-" + RandomSuffix(10).ToLowerInvariant();
        Dictionary<string, string?> environment = new() { ["CLOUDGPU_SSH_HOST"] = host, ["CLOUDGPU_SSH_USER"] = null };
        string text = new CloudGpuRunDraft().Add("Probe", "the VM " + host + " answered").Render();

        CloudGpuRunDocumentParser parser = CloudGpuRunDocumentParser.Parse(text, environment);

        string problem = Assert.Single(parser.Problems);
        Assert.DoesNotContain(host, problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("platform")]
    [InlineData("preset")]
    [InlineData("region")]
    public void An_Instance_without_a_field_fails_and_names_it(string field)
    {
        var draft = new CloudGpuRunDraft();
        _ = draft.Lines("Instance").RemoveAll(line => line.Contains(field, StringComparison.OrdinalIgnoreCase));

        CloudGpuRunDocumentParser parser = Read(draft);

        Assert.Contains(field, Assert.Single(parser.Problems), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_Instance_without_the_preemptible_statement_fails()
    {
        var draft = new CloudGpuRunDraft();
        _ = draft.Lines("Instance").RemoveAll(line => line.Contains("preemptible", StringComparison.Ordinal));

        Assert.Contains("preemptible: off (user-confirmed)", Assert.Single(Read(draft).Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void An_Instance_without_an_hourly_price_in_USD_fails()
    {
        var draft = new CloudGpuRunDraft();
        _ = draft.Lines("Instance").RemoveAll(line => line.Contains("USD", StringComparison.Ordinal));

        Assert.Contains("hourly price", Assert.Single(Read(draft).Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void A_Cost_section_without_USD_fails()
    {
        CloudGpuRunDocumentParser parser = Read(new CloudGpuRunDraft().Set("Cost", "Observed cost: 6.12 dollars.", "No stop triggered."));

        Assert.Contains("USD", Assert.Single(parser.Problems), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Stop reason: cost-cap.")]
    [InlineData("Stop reason: time-box.")]
    [InlineData("No stop triggered.")]
    public void A_Cost_section_with_a_stop_reason_passes(string reason)
    {
        CloudGpuRunDocumentParser parser = Read(new CloudGpuRunDraft().Set("Cost", "Observed cost: USD 25.00 (billing page).", reason));

        Assert.True(parser.Passed, Joined(parser));
    }

    [Fact]
    public void A_Cost_section_without_a_stop_reason_fails()
    {
        CloudGpuRunDocumentParser parser = Read(new CloudGpuRunDraft().Set("Cost", "Observed cost: USD 6.12 (billing page)."));

        Assert.Contains("stop reason", Assert.Single(parser.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void A_confirmed_Teardown_needs_the_deletion_time_and_the_final_cost()
    {
        CloudGpuRunDocumentParser noTime = Read(new CloudGpuRunDraft().Set("Teardown", "The VM was deleted. Final cost: USD 6.40."));
        CloudGpuRunDocumentParser noCost = Read(new CloudGpuRunDraft().Set("Teardown", "The VM was deleted at 2026-11-01T11:05:00Z."));

        Assert.Contains("deletion time", Assert.Single(noTime.Problems), StringComparison.Ordinal);
        Assert.Contains("final cost", Assert.Single(noCost.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void A_Teardown_without_deleted_and_not_pending_is_also_not_confirmed()
    {
        CloudGpuRunDocumentParser parser = Read(new CloudGpuRunDraft().Set("Teardown", "The VM is gone."));

        Assert.Contains("the teardown is not confirmed", Assert.Single(parser.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void An_incident_without_the_no_retry_statement_fails()
    {
        CloudGpuRunDocumentParser parser = Read(new CloudGpuRunDraft().Add("Runs", "- Incident: the SSH session was broken at 09:50."));

        Assert.Contains("no retry", Assert.Single(parser.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void An_incident_with_the_no_retry_statement_passes()
    {
        CloudGpuRunDocumentParser parser = Read(new CloudGpuRunDraft().Add("Runs", "- Incident: the VM was lost; no retry was made past the box or the cap."));

        Assert.True(parser.Passed, Joined(parser));
    }

    [Fact]
    public void A_Runs_section_without_an_exit_code_fails()
    {
        CloudGpuRunDocumentParser parser = Read(new CloudGpuRunDraft().Set("Runs", "- Started: 2026-11-01T09:10:00Z, ended: 2026-11-01T10:40:00Z"));

        Assert.Contains("exit", Assert.Single(parser.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void Commands_need_docker_logout()
    {
        var draft = new CloudGpuRunDraft();
        _ = draft.Lines("Commands").RemoveAll(line => line.Contains("docker logout", StringComparison.Ordinal));

        Assert.Contains("docker logout", Assert.Single(Read(draft).Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void Commands_show_the_ngc_key_only_as_a_variable()
    {
        CloudGpuRunDocumentParser parser = Read(new CloudGpuRunDraft().Add("Commands", "export NGC_API_KEY=notavariable"));

        string problem = Assert.Single(parser.Problems);
        Assert.Contains("$NGC_API_KEY", problem, StringComparison.Ordinal);
        Assert.DoesNotContain("notavariable", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_level_3_heading_does_not_count_as_a_section()
    {
        string text = new CloudGpuRunDraft().Render().Replace("## Teardown", "### Teardown", StringComparison.Ordinal);

        Assert.Contains("Teardown", Assert.Single(CloudGpuRunDocumentParser.Parse(text, NoEnvironment).Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void A_heading_inside_a_code_fence_does_not_count()
    {
        string text = new CloudGpuRunDraft().Remove("Teardown").Add("Commands", "```").Add("Commands", "## Teardown").Add("Commands", "```").Render();

        Assert.Contains("Teardown", Assert.Single(CloudGpuRunDocumentParser.Parse(text, NoEnvironment).Problems), StringComparison.Ordinal);
    }

    // ---- the committed document ----

    [Fact]
    public void The_cloudgpu_run_document_passes_the_contract_whenever_it_exists()
    {
        // The working-tree file is judged as soon as the builder writes it, before its commit; the HEAD tree only decides the note.
        string path = CloudGpuRunDocumentParser.CommittedPath();
        if (!File.Exists(path))
        {
            GitResult tree = GitRunner.Run(ConfigurationFixtures.RepositoryRoot(), "ls-tree", "--name-only", "HEAD", "--", "docs/cloudgpu-run.md");
            Assert.Equal(0, tree.ExitCode);
            Assert.True(tree.Stdout.Trim().Length == 0, "docs/cloudgpu-run.md is in the HEAD tree but missing from the working tree.");
            _output.WriteLine(NotCommittedNote);
            return;
        }

        CloudGpuRunDocumentParser parser = CloudGpuRunDocumentParser.Parse(File.ReadAllText(path));

        Assert.True(parser.Passed, "docs/cloudgpu-run.md breaks the contract:\n" + Joined(parser));
    }
}