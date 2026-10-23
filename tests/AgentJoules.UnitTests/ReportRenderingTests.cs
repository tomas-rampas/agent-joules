using System.Globalization;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

using AgentJoules.Cli;
using AgentJoules.Core.Cost;
using AgentJoules.Core.Energy;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Reporting.Render;
using AgentJoules.Reporting.Results;
using AgentJoules.TestSupport;
using AgentJoules.Workflow.Agents;

using Xunit;

using static AgentJoules.TestSupport.ReportRunFixtures;

namespace AgentJoules.UnitTests;

/// <summary>
/// AJ-081 (REQ-016): the run files a report may use. The header reader returns the tier, the status and the flags of the <c>run</c> object and one reason token for a header it cannot read; the selector
/// returns, per tier and per variant, the newest file by name whose header is accepted (completed or partial, not <c>non-publishable</c>), never opens a probe, calibration or non-matching file, reads only the
/// header, and reports an unreadable header or a tier that differs from the name as a problem, never as a tier that was not run.
/// AJ-082 (REQ-016): the aggregation of repeats into report rows. A row is (tier, variant, task, agent) over the <c>ok</c> steps of one run file; each value cell holds the median of the repeats that have a value
/// with the range and <c>n</c>, keeps their label, unit and currency, and is N/A or not-reported with a null value when no repeat has one; rows of different tiers or variants are never merged, ranked or compared.
/// </summary>
[Trait("Spec", "REQ-016")]
public sealed partial class ReportRenderingTests
{
    private const string Folder = "runs";
    private const string LocalOld = "20261022T093000Z-local.json";
    private const string LocalNew = "20261023T093000Z-local.json";
    private const string NvidiaOld = "20261022T093000Z-nvidia.json";
    private const string NvidiaNew = "20261023T093000Z-nvidia.json";
    private const string NebiusOld = "20261022T093000Z-nebius.json";
    private const string NebiusBaseline = "20261023T093000Z-nebius-baseline.json";

    /// <summary>A header text; the body after it is whatever <paramref name="tail"/> says.</summary>
    private static string Text(string tier = "local", string status = "completed", string flagsJson = "[]", string tail = "") =>
        $"{{\"schemaVersion\":1,\"run\":{{\"id\":\"r\",\"tier\":\"{tier}\",\"status\":\"{status}\",\"flags\":{flagsJson}}}{tail}}}";

    private static string Put(TempWorkspace workspace, string name, string text, string folder = Folder) => workspace.WriteFile(Path.Combine(folder, name), text);

    private static string FolderOf(TempWorkspace workspace, string folder = Folder) => Path.Combine(workspace.Root, folder);

    private static RunFileSelection SelectFolder(TempWorkspace workspace) => RunFileSelector.Select([FolderOf(workspace)]);

    private static string ReadReason(string text)
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string path = Put(workspace, "header.json", text);

        return Assert.Throws<InvalidDataException>(() => RunHeaderReader.Read(path)).Message;
    }

    // ---- the header reader (AJ-081-T2) ----

    [Fact]
    public void Header_reader_returns_tier_status_and_flags()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string path = Put(workspace, "header.json", Text("nebius", "partial", "[\"partial-offload\",\"non-publishable\"]"));

        RunHeader header = RunHeaderReader.Read(path);

        Assert.Equal(Tier.Nebius, header.Tier);
        Assert.Equal(RunStatus.Partial, header.Status);
        Assert.Equal([RunFlagNames.PartialOffload, RunFlagNames.NonPublishable], header.Flags);
    }

    [Theory]
    [InlineData("local", Tier.Local)]
    [InlineData("cloudgpu", Tier.CloudGpu)]
    [InlineData("nvidia", Tier.Nvidia)]
    [InlineData("nebius", Tier.Nebius)]
    public void Header_reader_knows_the_four_tiers(string name, Tier expected)
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();

        Assert.Equal(expected, RunHeaderReader.Read(Put(workspace, "header.json", Text(name))).Tier);
    }

    [Theory]
    [InlineData("running", RunStatus.Running)]
    [InlineData("completed", RunStatus.Completed)]
    [InlineData("failed", RunStatus.Failed)]
    [InlineData("partial", RunStatus.Partial)]
    [InlineData("incomplete", RunStatus.Incomplete)]
    public void Header_reader_knows_the_five_statuses(string name, RunStatus expected)
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();

        Assert.Equal(expected, RunHeaderReader.Read(Put(workspace, "header.json", Text(status: name))).Status);
    }

    [Fact]
    public void Header_reader_reads_flags_absent_or_null_as_empty_and_does_not_check_the_names()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string absent = Put(workspace, "absent.json", "{\"run\":{\"tier\":\"local\",\"status\":\"completed\"}}");
        string nothing = Put(workspace, "null.json", "{\"run\":{\"tier\":\"local\",\"status\":\"completed\",\"flags\":null}}");
        string unknown = Put(workspace, "unknown.json", Text(flagsJson: "[\"not-a-flag\"]"));

        Assert.Empty(RunHeaderReader.Read(absent).Flags);
        Assert.Empty(RunHeaderReader.Read(nothing).Flags);
        Assert.Equal(["not-a-flag"], RunHeaderReader.Read(unknown).Flags);
    }

    [Fact]
    public void Header_reader_skips_members_before_and_inside_run()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        const string text = "{\"schemaVersion\":1,\"note\":{\"a\":[1,2,{\"b\":null}],\"run\":3},\"list\":[1,[2,3]],\"run\":{\"id\":\"r\",\"tasks\":[\"a\",\"b\"],\"extra\":{\"tier\":\"nebius\"},\"tier\":\"local\",\"status\":\"failed\"},\"steps\":[]}";

        RunHeader header = RunHeaderReader.Read(Put(workspace, "header.json", text));

        Assert.Equal(Tier.Local, header.Tier);
        Assert.Equal(RunStatus.Failed, header.Status);
    }

    [Fact]
    public void Header_with_bom_is_read()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string path = Put(workspace, "header.json", string.Empty);
        File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(Text("nvidia", "completed"))]);

        RunHeader header = RunHeaderReader.Read(path);

        Assert.Equal(Tier.Nvidia, header.Tier);
        Assert.Equal(RunStatus.Completed, header.Status);
    }

    [Fact]
    public void Invalid_body_after_run_is_still_selected()
    {
        // The body after the run header is a megabyte of invalid JSON: Select must neither read it nor care about it (criterion 5).
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string body = ",\"steps\":[ this is not json " + new string('x', 1024 * 1024);
        string path = Put(workspace, LocalNew, Text(tail: body));

        RunFileSelection selection = RunFileSelector.Select([path]);

        Assert.Equal(path, selection.Latest[Tier.Local]);
        Assert.Empty(selection.Problems);
    }

    [Fact]
    public void Header_reader_refuses_text_that_is_not_json_with_header_not_json()
    {
        Assert.Equal("header-not-json", ReadReason("this is not json"));
        Assert.Equal("header-not-json", ReadReason(string.Empty));
        Assert.Equal("header-not-json", ReadReason("[1,2]"));
        Assert.Equal("header-not-json", ReadReason("{\"run\":{\"tier\":\"local\",\"status\":"));
        Assert.Equal("header-not-json", ReadReason("{\"run\":{\"tier\":\"local\" \"status\":\"completed\"}}"));
    }

    [Fact]
    public void Header_reader_refuses_a_file_without_run_with_run_missing()
    {
        Assert.Equal("run-missing", ReadReason("{\"schemaVersion\":1,\"steps\":[]}"));
        Assert.Equal("run-missing", ReadReason("{\"run\":null}"));
        Assert.Equal("run-missing", ReadReason("{\"run\":\"local\"}"));
    }

    [Fact]
    public void Header_reader_refuses_a_run_that_is_not_closed_in_64_KiB_with_header_too_long()
    {
        string text = "{\"run\":{\"tier\":\"local\",\"status\":\"completed\",\"pad\":\"" + new string('a', RunHeaderReader.MaxHeaderBytes + 10) + "\"}}";

        Assert.Equal("header-too-long", ReadReason(text));
    }

    [Fact]
    public void Header_reader_refuses_a_missing_tier_with_tier_missing() => Assert.Equal("tier-missing", ReadReason("{\"run\":{\"status\":\"completed\"}}"));

    [Theory]
    [InlineData("Local")]
    [InlineData("gpu")]
    [InlineData("")]
    public void Header_reader_refuses_an_unknown_tier_with_tier_unknown(string name) =>
        Assert.Equal("tier-unknown", ReadReason($"{{\"run\":{{\"tier\":\"{name}\",\"status\":\"completed\"}}}}"));

    [Fact]
    public void Header_reader_refuses_a_tier_that_is_not_a_string_with_tier_unknown() =>
        Assert.Equal("tier-unknown", ReadReason("{\"run\":{\"tier\":3,\"status\":\"completed\"}}"));

    [Fact]
    public void Header_reader_refuses_a_missing_status_with_status_missing() => Assert.Equal("status-missing", ReadReason("{\"run\":{\"tier\":\"local\"}}"));

    [Theory]
    [InlineData("Completed")]
    [InlineData("done")]
    [InlineData("")]
    public void Header_reader_refuses_an_unknown_status_with_status_unknown(string name) =>
        Assert.Equal("status-unknown", ReadReason($"{{\"run\":{{\"tier\":\"local\",\"status\":\"{name}\"}}}}"));

    [Theory]
    [InlineData("\"non-publishable\"")]
    [InlineData("[1]")]
    [InlineData("[null]")]
    [InlineData("[[\"a\"]]")]
    [InlineData("{}")]
    [InlineData("true")]
    public void Header_reader_refuses_flags_that_are_not_strings_with_flags_not_strings(string flagsJson) =>
        Assert.Equal("flags-not-strings", ReadReason(Text(flagsJson: flagsJson)));

    [Fact]
    public void Header_reader_refuses_a_blank_path_and_a_missing_file()
    {
        _ = Assert.ThrowsAny<ArgumentException>(() => RunHeaderReader.Read(" "));
        _ = Assert.ThrowsAny<IOException>(() => RunHeaderReader.Read(Path.Combine(Path.GetTempPath(), "agentjoules-no-such-" + Guid.NewGuid().ToString("N") + ".json")));
    }

    // ---- the selector (AJ-081-T3): the criteria and the scenarios ----

    [Fact]
    public void Newest_file_by_name_wins_whatever_the_write_time()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string newest = Put(workspace, NvidiaNew, Text("nvidia"));
        string older = Put(workspace, NvidiaOld, Text("nvidia"));
        File.SetLastWriteTimeUtc(newest, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(older, new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(newest, RunFileSelector.Latest(FolderOf(workspace), Tier.Nvidia));
    }

    [Fact]
    public void Partial_runs_are_accepted_and_baseline_runs_are_returned_separately()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string partial = Put(workspace, NebiusOld, Text("nebius", "partial"));
        string baseline = Put(workspace, NebiusBaseline, Text("nebius"));

        Assert.Equal(partial, RunFileSelector.Latest(FolderOf(workspace), Tier.Nebius));
        Assert.Equal(baseline, RunFileSelector.LatestBaseline(FolderOf(workspace), Tier.Nebius));
    }

    [Fact]
    public void Failed_and_running_files_are_skipped_and_the_older_completed_file_is_returned()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string older = Put(workspace, NvidiaOld, Text("nvidia"));
        _ = Put(workspace, NvidiaNew, Text("nvidia", "failed"));

        Assert.Equal(older, RunFileSelector.Latest(FolderOf(workspace), Tier.Nvidia));
    }

    [Fact]
    public void Running_and_incomplete_are_skipped()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string completed = Put(workspace, "20261021T093000Z-nvidia.json", Text("nvidia"));
        _ = Put(workspace, "20261022T093000Z-nvidia.json", Text("nvidia", "incomplete"));
        _ = Put(workspace, "20261023T093000Z-nvidia.json", Text("nvidia", "running"));
        _ = Put(workspace, "20261023T093000Z-nebius.json", Text("nebius", "running"));
        _ = Put(workspace, "20261022T093000Z-nebius.json", Text("nebius", "incomplete"));

        RunFileSelection selection = SelectFolder(workspace);

        Assert.Equal(completed, selection.Latest[Tier.Nvidia]);
        Assert.DoesNotContain(Tier.Nebius, selection.Latest.Keys);
        Assert.Empty(selection.Problems);
    }

    [Fact]
    public void Probe_and_calibration_files_are_never_selected()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = Put(workspace, "probe-20261019T205344Z.json", Text());
        _ = Put(workspace, "calibration-20261019T205344Z.json", Text());

        RunFileSelection selection = SelectFolder(workspace);

        Assert.Null(RunFileSelector.Latest(FolderOf(workspace), Tier.Local));
        Assert.Empty(selection.Latest);
        Assert.Empty(selection.Skipped);
        Assert.Empty(selection.Problems);
    }

    [Fact]
    public void Probe_and_calibration_with_valid_header_never_returned()
    {
        // Their headers would be accepted, and the unreadable ones among the other names would be problems: the name alone must keep every one of them out, unopened.
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string[] names =
        [
            "probe-20261023T093000Z.json",
            "probe-20261023T093000Z-nvidia.json",
            "calibration-20261023T093000Z.json",
            "calibration-20261023T093000Z-local.json",
            "20261023T093000Z-local.json.partial",
            "20261023T093000Z-local.txt",
            "20261023T093000Z-gpu.json",
            "20261023T093000Z-local-baseline.json.bak",
            "results.json",
            "20261023T09300Z-local.json",
        ];
        foreach (string name in names)
        {
            _ = Put(workspace, name, name.StartsWith("probe-", StringComparison.Ordinal) ? Text() : "not json at all");
        }

        RunFileSelection selection = SelectFolder(workspace);

        Assert.Empty(selection.Latest);
        Assert.Empty(selection.LatestBaseline);
        Assert.Empty(selection.Skipped);
        Assert.Empty(selection.Problems);
        Assert.Null(RunFileSelector.Latest(FolderOf(workspace), Tier.Local));
        Assert.Null(RunFileSelector.LatestBaseline(FolderOf(workspace), Tier.Local));
    }

    [Fact]
    public void A_non_publishable_newest_file_is_skipped_by_Latest()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string older = Put(workspace, LocalOld, Text());
        _ = Put(workspace, LocalNew, Text(flagsJson: "[\"non-publishable\"]"));

        Assert.Equal(older, RunFileSelector.Latest(FolderOf(workspace), Tier.Local));
    }

    [Fact]
    public void A_run_with_an_empty_flag_list_is_selected()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string newest = Put(workspace, LocalNew, Text(flagsJson: "[]"));
        _ = Put(workspace, LocalOld, Text());

        Assert.Equal(newest, RunFileSelector.Latest(FolderOf(workspace), Tier.Local));
    }

    [Fact]
    public void A_partial_offload_flag_does_not_keep_a_file_out()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string newest = Put(workspace, LocalNew, Text(flagsJson: "[\"partial-offload\"]"));

        Assert.Equal(newest, RunFileSelector.Latest(FolderOf(workspace), Tier.Local));
    }

    [Fact]
    public void Baseline_file_never_returned_by_Latest()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string primary = Put(workspace, LocalOld, Text());
        string baseline = Put(workspace, "20261023T093000Z-local-baseline.json", Text());
        string onlyBaseline = Put(workspace, NebiusBaseline, Text("nebius"));

        RunFileSelection selection = SelectFolder(workspace);

        Assert.Equal(primary, RunFileSelector.Latest(FolderOf(workspace), Tier.Local));
        Assert.Equal(baseline, RunFileSelector.LatestBaseline(FolderOf(workspace), Tier.Local));
        Assert.Equal(primary, selection.Latest[Tier.Local]);
        Assert.Equal(baseline, selection.LatestBaseline[Tier.Local]);
        Assert.Null(RunFileSelector.Latest(FolderOf(workspace), Tier.Nebius));
        Assert.DoesNotContain(Tier.Nebius, selection.Latest.Keys);
        Assert.Equal(onlyBaseline, selection.LatestBaseline[Tier.Nebius]);
        Assert.Null(RunFileSelector.LatestBaseline(FolderOf(workspace), Tier.Nvidia));
    }

    [Fact]
    public void Failed_baseline_is_skipped()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string older = Put(workspace, "20261022T093000Z-local-baseline.json", Text());
        _ = Put(workspace, "20261023T093000Z-local-baseline.json", Text(status: "failed"));
        _ = Put(workspace, "20261024T093000Z-local-baseline.json", Text(status: "running"));

        RunFileSelection selection = SelectFolder(workspace);

        Assert.Equal(older, selection.LatestBaseline[Tier.Local]);
        Assert.Equal(["status-failed", "status-running"], [.. selection.Skipped.Select(static skip => skip.Reason)]);
        Assert.Empty(selection.Problems);
    }

    [Fact]
    public void Non_publishable_baseline_is_skipped()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = Put(workspace, NebiusBaseline, Text("nebius", flagsJson: "[\"non-publishable\"]"));

        RunFileSelection selection = SelectFolder(workspace);

        Assert.Empty(selection.LatestBaseline);
        Assert.Null(RunFileSelector.LatestBaseline(FolderOf(workspace), Tier.Nebius));
        RunFileSkip skip = Assert.Single(selection.Skipped);
        Assert.Equal(new RunFileSkip(NebiusBaseline, "non-publishable"), skip);
    }

    [Fact]
    public void Skipped_lists_reason_per_file()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string winner = Put(workspace, "20261020T093000Z-local.json", Text());
        _ = Put(workspace, "20261021T093000Z-local.json", Text(flagsJson: "[\"non-publishable\"]"));
        _ = Put(workspace, "20261022T093000Z-local.json", Text(status: "incomplete"));
        _ = Put(workspace, "20261023T093000Z-local.json", Text(status: "failed"));
        _ = Put(workspace, "20261024T093000Z-local.json", Text(status: "running"));

        RunFileSelection selection = SelectFolder(workspace);

        Assert.Equal(winner, selection.Latest[Tier.Local]);
        Assert.Equal(
            [
                new RunFileSkip("20261021T093000Z-local.json", "non-publishable"),
                new RunFileSkip("20261022T093000Z-local.json", "status-incomplete"),
                new RunFileSkip("20261023T093000Z-local.json", "status-failed"),
                new RunFileSkip("20261024T093000Z-local.json", "status-running"),
            ],
            selection.Skipped);
    }

    [Fact]
    public void Tier_mismatch_is_a_problem()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string older = Put(workspace, LocalOld, Text());
        _ = Put(workspace, LocalNew, Text("nvidia"));

        RunFileSelection selection = SelectFolder(workspace);

        Assert.Equal(new RunFileProblem(LocalNew, "tier-mismatch"), Assert.Single(selection.Problems));
        Assert.Equal(older, selection.Latest[Tier.Local]);
        Assert.DoesNotContain(Tier.Nvidia, selection.Latest.Keys);
    }

    [Fact]
    public void An_unreadable_header_is_a_problem_and_never_a_tier_that_was_not_run()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = Put(workspace, LocalNew, "{ this is not json");
        _ = Put(workspace, NvidiaNew, "{\"schemaVersion\":1}");
        _ = Put(workspace, NebiusOld, Text("nebius", "Completed"));

        RunFileSelection selection = SelectFolder(workspace);

        Assert.Empty(selection.Latest);
        Assert.Equal(
            [
                new RunFileProblem(NebiusOld, "status-unknown"),
                new RunFileProblem(LocalNew, "header-not-json"),
                new RunFileProblem(NvidiaNew, "run-missing"),
            ],
            selection.Problems);
    }

    [Fact]
    public void A_file_that_cannot_be_opened_is_the_problem_unreadable()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string missing = Path.Combine(FolderOf(workspace), LocalNew);

        RunFileSelection selection = RunFileSelector.Select([missing]);

        Assert.Equal(new RunFileProblem(LocalNew, "unreadable"), Assert.Single(selection.Problems));
        Assert.Empty(selection.Latest);
    }

    [Fact]
    public void Files_older_than_the_selected_one_are_not_opened()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string newest = Put(workspace, LocalNew, Text());
        _ = Put(workspace, LocalOld, "not json");

        RunFileSelection selection = SelectFolder(workspace);

        Assert.Equal(newest, selection.Latest[Tier.Local]);
        Assert.Empty(selection.Problems);
    }

    [Fact]
    public void Latest_throws_on_a_problem_file_of_its_tier_and_ignores_the_other_tiers()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string nvidia = Put(workspace, NvidiaNew, Text("nvidia"));
        _ = Put(workspace, LocalNew, "not json");

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => RunFileSelector.Latest(FolderOf(workspace), Tier.Local));

        Assert.Equal(LocalNew + ": header-not-json", error.Message);
        Assert.Equal(nvidia, RunFileSelector.Latest(FolderOf(workspace), Tier.Nvidia));
    }

    [Fact]
    public void Latest_of_a_missing_folder_throws()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();

        _ = Assert.Throws<DirectoryNotFoundException>(() => RunFileSelector.Latest(FolderOf(workspace), Tier.Local));
        _ = Assert.Throws<DirectoryNotFoundException>(() => RunFileSelector.LatestBaseline(FolderOf(workspace), Tier.Local));
    }

    [Fact]
    public void Named_file_and_directory_combine()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string localInFolder = Put(workspace, LocalOld, Text());
        _ = Put(workspace, "20261025T093000Z-local.json", Text(), Path.Combine(Folder, "nested"));
        string nvidia = Put(workspace, NvidiaOld, Text("nvidia"), "other");
        string newerLocal = Put(workspace, LocalNew, Text(), "third");

        RunFileSelection folderOnly = RunFileSelector.Select([FolderOf(workspace)]);
        RunFileSelection combined = RunFileSelector.Select([FolderOf(workspace), nvidia, newerLocal]);

        Assert.Equal(localInFolder, folderOnly.Latest[Tier.Local]);
        Assert.Equal(newerLocal, combined.Latest[Tier.Local]);
        Assert.Equal(nvidia, combined.Latest[Tier.Nvidia]);
        Assert.Empty(combined.Problems);
        Assert.Empty(combined.Skipped);
    }

    [Fact]
    public void The_same_file_named_twice_or_through_its_folder_counts_once()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string path = Put(workspace, LocalNew, Text());

        RunFileSelection selection = RunFileSelector.Select([FolderOf(workspace), path, path]);

        Assert.Equal(path, selection.Latest[Tier.Local]);
        Assert.Empty(selection.Problems);
    }

    [Fact]
    public void Same_name_in_two_inputs_is_a_problem()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string one = Put(workspace, LocalNew, Text(), "one");
        string two = Put(workspace, LocalNew, Text(), "two");
        string other = Put(workspace, NvidiaNew, Text("nvidia"), "two");

        RunFileSelection selection = RunFileSelector.Select([one, two, other]);

        Assert.Equal(new RunFileProblem(LocalNew, "duplicate-file-name"), Assert.Single(selection.Problems));
        Assert.DoesNotContain(Tier.Local, selection.Latest.Keys);
        Assert.Equal(other, selection.Latest[Tier.Nvidia]);
    }

    [Fact]
    public void A_named_file_that_is_not_a_run_file_name_is_refused()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string probe = Put(workspace, "probe-20261019T205344Z.json", Text());

        _ = Assert.Throws<ArgumentException>(() => RunFileSelector.Select([probe]));
        _ = Assert.Throws<ArgumentNullException>(() => RunFileSelector.Select(null!));
    }

    [Fact]
    public void An_empty_input_list_selects_nothing()
    {
        RunFileSelection selection = RunFileSelector.Select([]);

        Assert.Empty(selection.Latest);
        Assert.Empty(selection.LatestBaseline);
        Assert.Empty(selection.Skipped);
        Assert.Empty(selection.Problems);
    }

    [Fact]
    public void The_committed_fixture_run_files_are_all_selected()
    {
        string folder = Path.GetFullPath(Path.Combine(ConfigurationFixtures.RepositoryRoot(), RunDocumentFixtures.CommittedFolder));

        RunFileSelection selection = RunFileSelector.Select([folder]);

        Assert.Equal([Tier.Local, Tier.Nvidia, Tier.Nebius], selection.Latest.Keys.OrderBy(static tier => (int)tier).ToArray());
        Assert.Equal(Path.Combine(folder, RunDocumentFixtures.CommittedFileName(Tier.Nvidia)), selection.Latest[Tier.Nvidia]);
        Assert.Empty(selection.LatestBaseline);
        Assert.Empty(selection.Skipped);
        Assert.Empty(selection.Problems);
    }

    // ---- the fix round: the 64 KiB cap, the order of the rules, the files that are not files, the relative paths ----

    [Fact]
    public void The_header_cap_is_64_KiB_and_the_boundary_bytes_are_exact()
    {
        Assert.Equal(65536, RunHeaderReader.MaxHeaderBytes);
        const string prefix = "{\"run\":{\"tier\":\"local\",\"status\":\"completed\",\"pad\":\"";
        static string Closing(int closesAt, int extra) => prefix + new string('a', closesAt - prefix.Length - 2) + "\"}" + new string(' ', extra);
        static string Unclosed(int length) => prefix + new string('a', length - prefix.Length);
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();

        Assert.Equal(Tier.Local, RunHeaderReader.Read(Put(workspace, "closes-at-the-cap-and-ends.json", Closing(65536, 0))).Tier);
        Assert.Equal(Tier.Local, RunHeaderReader.Read(Put(workspace, "closes-at-the-cap-and-goes-on.json", Closing(65536, 100))).Tier);
        Assert.Equal("header-too-long", ReadReason(Closing(65537, 0)));
        Assert.Equal("header-too-long", ReadReason(Closing(65537, 100)));
        Assert.Equal("header-not-json", ReadReason(Unclosed(65536)));
        Assert.Equal("header-too-long", ReadReason(Unclosed(65537)));
    }

    [Fact]
    public void Header_reader_requests_no_more_than_the_cap_and_one_probe_byte()
    {
        const string prefix = "{\"run\":{\"tier\":\"local\",\"status\":\"completed\",";
        byte[] closed = Encoding.ASCII.GetBytes(Text() + new string(' ', 1024 * 1024));
        byte[] unclosed = Encoding.ASCII.GetBytes(prefix + "\"pad\":\"" + new string('a', 1024 * 1024));

        using var closedStream = new CappedStream(closed);
        Assert.Equal(Tier.Local, RunHeaderReader.Read(closedStream).Tier);
        Assert.Equal(RunHeaderReader.MaxHeaderBytes + 1, closedStream.Furthest);

        using var unclosedStream = new CappedStream(unclosed);
        Assert.Equal("header-too-long", Assert.Throws<InvalidDataException>(() => RunHeaderReader.Read(unclosedStream)).Message);
        Assert.Equal(RunHeaderReader.MaxHeaderBytes + 1, unclosedStream.Furthest);
    }

    [Fact]
    public void Selector_reads_header_only()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string path = Put(workspace, LocalNew, RunDocumentFixtures.CommittedText(Tier.Local));
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write))
        {
            stream.SetLength(64L * 1024 * 1024);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        RunFileSelection selection = RunFileSelector.Select([path]);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(path, selection.Latest[Tier.Local]);
        Assert.InRange(allocated, 0, 4L * RunHeaderReader.MaxHeaderBytes);
    }

    [Fact]
    public void Many_unreadable_files_share_one_buffer()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        for (int day = 1; day <= 40; day++)
        {
            _ = Put(workspace, $"202610{day:D2}T093000Z-local.json", "not json");
        }

        _ = SelectFolder(workspace);
        long before = GC.GetAllocatedBytesForCurrentThread();
        RunFileSelection selection = SelectFolder(workspace);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(40, selection.Problems.Count);
        Assert.InRange(allocated, 0, 16L * RunHeaderReader.MaxHeaderBytes);
    }

    [Fact]
    public void Tier_mismatch_wins_over_the_status_rule()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = Put(workspace, LocalNew, Text("nvidia", "failed"));

        RunFileSelection selection = SelectFolder(workspace);

        Assert.Equal(new RunFileProblem(LocalNew, "tier-mismatch"), Assert.Single(selection.Problems));
        Assert.Empty(selection.Skipped);
    }

    [DeniedFolderFact]
    public void A_folder_that_cannot_be_listed_is_unreadable_with_its_name_bounded()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string name = new('f', 80);
        string folder = Path.Combine(workspace.Root, name);
        _ = Put(workspace, LocalNew, Text(), name);

        RunFileSelection selection;
        using (FolderAccess.DenyListing(folder))
        {
            selection = RunFileSelector.Select([folder]);
        }

        Assert.Equal(new RunFileProblem(new string('f', 64) + "...", "unreadable"), Assert.Single(selection.Problems));
        Assert.Empty(selection.Latest);
    }

    [WindowsFact]
    public void The_same_folder_in_two_letter_cases_counts_once_on_Windows()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string path = Put(workspace, LocalNew, Text());
        string folder = FolderOf(workspace);

        RunFileSelection selection = RunFileSelector.Select([folder, folder.ToUpperInvariant()]);

        Assert.Equal(path, selection.Latest[Tier.Local]);
        Assert.Empty(selection.Problems);
    }

    [Fact]
    public void The_same_folder_spelled_with_a_parent_step_counts_once()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string path = Put(workspace, LocalNew, Text());
        string folder = FolderOf(workspace);

        RunFileSelection selection = RunFileSelector.Select([folder, Path.Combine(folder, "..", Folder)]);

        Assert.Equal(path, selection.Latest[Tier.Local]);
        Assert.Empty(selection.Problems);
    }

    [Fact]
    public void A_header_problem_does_not_stop_the_older_files()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string older = Put(workspace, LocalOld, Text());
        _ = Put(workspace, LocalNew, "not json");

        RunFileSelection selection = SelectFolder(workspace);

        Assert.Equal(older, selection.Latest[Tier.Local]);
        Assert.Equal(new RunFileProblem(LocalNew, "header-not-json"), Assert.Single(selection.Problems));
    }

    [Fact]
    public void Header_errors_carry_no_inner_exception()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string[] texts =
        [
            "{\"run\":{\"tier\":\"local\" \"status\":\"completed\"}}",
            "{\"schemaVersion\":1}",
            "{\"run\":{\"status\":\"completed\"}}",
            "{\"run\":{\"tier\":\"local\",\"status\":\"completed\",\"flags\":[1]}}",
            string.Empty,
        ];
        foreach (string text in texts)
        {
            string path = Put(workspace, "header.json", text);

            InvalidDataException error = Assert.Throws<InvalidDataException>(() => RunHeaderReader.Read(path));

            Assert.Null(error.InnerException);
        }

        IOException notRegular = Assert.Throws<IOException>(() => RunHeaderReader.Read(new NonSeekableStream()));
        Assert.Null(notRegular.InnerException);
        ArgumentException relative = Assert.Throws<ArgumentException>(() => RunFileSelector.Select(["runs"]));
        Assert.Null(relative.InnerException);
    }

    [Fact]
    public void A_file_open_for_writing_elsewhere_is_still_read()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string path = Put(workspace, LocalNew, Text());

        RunFileSelection selection;
        using (new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            selection = RunFileSelector.Select([path]);
        }

        Assert.Equal(path, selection.Latest[Tier.Local]);
        Assert.Empty(selection.Problems);
    }

    [Fact]
    public void Relative_inputs_are_refused_and_echoed_bounded()
    {
        string longName = new string('r', 100) + ".json";

        foreach (string input in new[] { "runs", "C:x", Path.Combine("runs", LocalNew), longName })
        {
            Assert.Equal("inputs", Assert.Throws<ArgumentException>(() => RunFileSelector.Select([input])).ParamName);
            Assert.Equal("directory", Assert.Throws<ArgumentException>(() => RunFileSelector.Latest(input, Tier.Local)).ParamName);
            Assert.Equal("directory", Assert.Throws<ArgumentException>(() => RunFileSelector.LatestBaseline(input, Tier.Local)).ParamName);
        }

        ArgumentException failure = Assert.Throws<ArgumentException>(() => RunFileSelector.Select([longName]));
        Assert.Contains(new string('r', 64) + "...", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('r', 65), failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_folder_is_echoed_by_its_name_only()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string missing = Path.Combine(workspace.Root, "no-such-folder");

        DirectoryNotFoundException failure = Assert.Throws<DirectoryNotFoundException>(() => RunFileSelector.Latest(missing, Tier.Local));

        Assert.Contains("no-such-folder", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(workspace.Root, failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Every_run_status_has_a_selection_rule()
    {
        Dictionary<RunStatus, string?> expected = new()
        {
            [RunStatus.Running] = "status-running",
            [RunStatus.Completed] = null,
            [RunStatus.Failed] = "status-failed",
            [RunStatus.Partial] = null,
            [RunStatus.Incomplete] = "status-incomplete",
        };
        Assert.Equal(Enum.GetValues<RunStatus>().Length, expected.Count);

        foreach (RunStatus status in Enum.GetValues<RunStatus>())
        {
            using TempWorkspace workspace = TempWorkspace.CreateEmpty();
            string path = Put(workspace, LocalNew, Text(status: status.ToString().ToLowerInvariant()));

            RunFileSelection selection = RunFileSelector.Select([path]);

            Assert.Equal(expected[status] is null, selection.Latest.ContainsKey(Tier.Local));
            Assert.Equal(expected[status], selection.Skipped.SingleOrDefault()?.Reason);
        }
    }

    [Fact]
    public void An_empty_file_is_header_not_json()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = Put(workspace, LocalNew, string.Empty);

        RunFileSelection selection = SelectFolder(workspace);

        Assert.Equal(new RunFileProblem(LocalNew, "header-not-json"), Assert.Single(selection.Problems));
    }

    [Fact]
    public void A_stream_that_cannot_seek_is_refused_without_a_read()
    {
        _ = Assert.Throws<IOException>(() => RunHeaderReader.Read(new NonSeekableStream()));
    }

    // Windows only: opening a pipe does not block, so the seek check is what refuses it. The pipe is named as an input, never found by listing the global pipe namespace, which other test processes share.
    [WindowsFact]
    public async Task A_pipe_named_like_a_run_file_is_unreadable_without_blocking_and_the_scan_continues()
    {
        string name = $"2099{Random.Shared.Next(0, 10000):D4}T{Random.Shared.Next(0, 1000000):D6}Z-local.json";
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string nvidia = Put(workspace, NvidiaNew, Text("nvidia"));
        using var server = new System.IO.Pipes.NamedPipeServerStream(name, System.IO.Pipes.PipeDirection.InOut, 2, System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);

        (RunFileSelection selection, long elapsedMs) = await Task.Run(() =>
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            RunFileSelection result = RunFileSelector.Select([@"\\.\pipe\" + name, nvidia]);
            return (result, timer.ElapsedMilliseconds);
        }).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(elapsedMs < 1000, $"The scan took {elapsedMs} ms.");
        Assert.Equal(new RunFileProblem(name, "unreadable"), Assert.Single(selection.Problems));
        Assert.Equal(nvidia, selection.Latest[Tier.Nvidia]);
        Assert.DoesNotContain(Tier.Local, selection.Latest.Keys);
    }

    // Unix only: opening a FIFO for reading blocks until a writer appears, so the size zero keeps it from being opened.
    [UnixFact]
    public async Task A_fifo_named_like_a_run_file_is_a_problem_without_blocking()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = Directory.CreateDirectory(FolderOf(workspace));
        var start = new System.Diagnostics.ProcessStartInfo("mkfifo") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(Path.Combine(FolderOf(workspace), LocalNew));
        using (System.Diagnostics.Process mkfifo = System.Diagnostics.Process.Start(start)!)
        {
            await mkfifo.WaitForExitAsync();
            Assert.Equal(0, mkfifo.ExitCode);
        }

        string nvidia = Put(workspace, NvidiaNew, Text("nvidia"));
        RunFileSelection selection = await Task.Run(() => SelectFolder(workspace)).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(new RunFileProblem(LocalNew, "header-not-json"), Assert.Single(selection.Problems));
        Assert.Equal(nvidia, selection.Latest[Tier.Nvidia]);
    }

    private sealed class UnixFactAttribute : FactAttribute
    {
        public UnixFactAttribute()
        {
            Skip = OperatingSystem.IsWindows() ? "this behaviour is specific to Unix file systems" : null;
        }
    }

    // Serves the bytes of a file and fails the test if a read would reach past the cap and the probe byte.
    private sealed class CappedStream(byte[] data) : Stream
    {
        private long _position;

        public long Furthest { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => data.Length;

        public override long Position
        {
            get => _position;
            set => _position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position + count > RunHeaderReader.MaxHeaderBytes + 1)
            {
                throw new InvalidOperationException("A read reached past the cap and the probe byte.");
            }

            int served = (int)Math.Min(count, data.Length - _position);
            Array.Copy(data, _position, buffer, offset, served);
            _position += served;
            Furthest = Math.Max(Furthest, _position);
            return served;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // A pipe: readable, cannot seek; a read would block, so the test fails if one is made.
    private sealed class NonSeekableStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("A read was made of a stream that cannot seek.");

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // ---- the aggregation of repeats into report rows (AJ-082) ----

    private static SelectedRun RunOfNet(Tier tier, params double[] values) => RunOf(tier, NetSteps(tier, values));

    private static ReportRow OnlyRow(params SelectedRun[] runs) => Assert.Single(ReportAggregator.Build(runs).Rows);

    private static ReportCell[] ValueCells(ReportRow row) =>
        [row.GrossJoules, row.NetJoules, row.AvgW, row.PeakW, row.InputTokens, row.OutputTokens, row.LatencyMs, row.TtftMs, row.Cost, row.Quality];

    private static RunDocument WithModels(RunDocument document, string? precision, params string[] modelIds) =>
        document with { Environment = RunDocumentFixtures.Environment([.. modelIds.Select(id => new EnvironmentModel(Planner, id, Baseline: false))]) with { Precision = precision } };

    [Fact]
    public void Median_min_max_n()
    {
        ReportRow row = OnlyRow(RunOfNet(Tier.Local, 10.0, 12.0, 14.0));

        Assert.Equal(Tier.Local, row.Tier);
        Assert.Equal(RunVariant.Primary, row.Variant);
        Assert.Equal(TaskName, row.Task);
        Assert.Equal(Planner, row.Agent);
        Assert.Equal(3, row.Repeats);
        ReportCell cell = row.NetJoules;
        Assert.Equal(12.0, cell.Median);
        Assert.Equal(10.0, cell.Min);
        Assert.Equal(14.0, cell.Max);
        Assert.Equal(3, cell.N);
        Assert.Equal(3, cell.Total);
        Assert.Equal(MetricLabel.Measured, cell.Label);
        Assert.Equal("J", cell.Unit);
        Assert.Null(cell.Currency);
        Assert.Empty(cell.Reasons);
    }

    [Fact]
    public void Median_sorts_the_values()
    {
        ReportCell cell = OnlyRow(RunOfNet(Tier.Local, 14.0, 10.0, 12.0)).NetJoules;

        Assert.Equal(12.0, cell.Median);
        Assert.Equal(10.0, cell.Min);
        Assert.Equal(14.0, cell.Max);
    }

    [Theory]
    [InlineData(11.0, 10.0, 12.0)]
    [InlineData(2.5, 1.0, 2.0, 3.0, 10.0)]
    [InlineData(5.0, 5.0, 5.0)]
    public void Even_n_median_is_mean_of_middles(double expected, params double[] values)
    {
        ReportCell cell = OnlyRow(RunOfNet(Tier.Local, values)).NetJoules;

        Assert.Equal(expected, cell.Median);
        Assert.Equal(values.Length, cell.N);
    }

    [Fact]
    public void Zero_is_a_value_not_a_missing_value()
    {
        ReportCell cell = OnlyRow(RunOfNet(Tier.Local, 0.0)).NetJoules;

        Assert.Equal(0.0, cell.Median);
        Assert.Equal(1, cell.N);
        Assert.Equal(MetricLabel.Measured, cell.Label);
    }

    [Fact]
    public void Mixed_cell_keeps_valued_label_and_total()
    {
        StepReading[] steps =
        [
            Step(repeat: 1, net: Metric.Measured(10.0, "J")),
            Step(repeat: 2, net: Metric.NotReported("sampler-unavailable")),
            Step(repeat: 3, net: Metric.Measured(14.0, "J")),
        ];

        ReportCell cell = OnlyRow(RunOf(Tier.Local, steps)).NetJoules;

        Assert.Equal(12.0, cell.Median);
        Assert.Equal(10.0, cell.Min);
        Assert.Equal(14.0, cell.Max);
        Assert.Equal(2, cell.N);
        Assert.Equal(3, cell.Total);
        Assert.Equal(MetricLabel.Measured, cell.Label);
        Assert.Empty(cell.Reasons);
    }

    [Fact]
    public void All_NA_cell_stays_NA()
    {
        StepReading[] steps = [.. Enumerable.Range(1, 3).Select(repeat => Step(Tier.Nvidia, repeat: repeat))];

        ReportRow row = OnlyRow(RunOf(Tier.Nvidia, steps));

        foreach (ReportCell cell in new[] { row.GrossJoules, row.NetJoules, row.AvgW, row.PeakW })
        {
            Assert.Equal(MetricLabel.NotApplicable, cell.Label);
            Assert.Null(cell.Median);
            Assert.Null(cell.Min);
            Assert.Null(cell.Max);
            Assert.Equal(0, cell.N);
            Assert.Equal(3, cell.Total);
            Assert.Equal(["hosted-tier"], cell.Reasons);
            Assert.Null(cell.Unit);
        }

        Assert.Equal(MetricLabel.Measured, row.InputTokens.Label);
        Assert.Equal(120.0, row.InputTokens.Median);
    }

    [Fact]
    public void All_not_reported_cell_keeps_not_reported()
    {
        StepReading[] steps = [.. Enumerable.Range(1, 3).Select(repeat => Step(repeat: repeat, net: Metric.NotReported("sampler-unavailable")))];

        ReportCell cell = OnlyRow(RunOf(Tier.Local, steps)).NetJoules;

        Assert.Equal(MetricLabel.NotReported, cell.Label);
        Assert.Null(cell.Median);
        Assert.Equal(0, cell.N);
        Assert.Equal(["sampler-unavailable"], cell.Reasons);
    }

    [Fact]
    public void NA_wins_over_not_reported_when_no_repeat_has_a_value_and_reasons_are_distinct_and_ordinal()
    {
        StepReading[] steps =
        [
            Step(repeat: 1, cost: Metric.NotReported("not-priced")),
            Step(repeat: 2, cost: Metric.NotApplicable("cost-not-configured")),
            Step(repeat: 3, cost: Metric.NotReported("not-priced")),
        ];

        ReportCell cell = OnlyRow(RunOf(Tier.Local, steps)).Cost;

        Assert.Equal(MetricLabel.NotApplicable, cell.Label);
        Assert.Null(cell.Median);
        Assert.Equal(["cost-not-configured", "not-priced"], cell.Reasons);
    }

    [Fact]
    public void Mixed_currency_cost_is_NA()
    {
        StepReading[] steps =
        [
            Step(repeat: 1, cost: Metric.Estimated(0.5, "EUR")),
            Step(repeat: 2, cost: Metric.Estimated(0.7, "USD")),
        ];

        ReportCell cell = OnlyRow(RunOf(Tier.Local, steps)).Cost;

        Assert.Equal(MetricLabel.NotApplicable, cell.Label);
        Assert.Null(cell.Median);
        Assert.Null(cell.Min);
        Assert.Null(cell.Max);
        Assert.Equal(0, cell.N);
        Assert.Equal(2, cell.Total);
        Assert.Equal([ReportAggregator.MixedCurrencyReason], cell.Reasons);
        Assert.Null(cell.Currency);
    }

    [Fact]
    public void Cost_cell_keeps_currency_and_estimated_label()
    {
        StepReading[] steps =
        [
            Step(repeat: 1, cost: Metric.Estimated(0.2, "EUR")),
            Step(repeat: 2, cost: Metric.Estimated(0.6, "EUR")),
            Step(repeat: 3, cost: Metric.Estimated(0.4, "EUR")),
        ];

        ReportCell cell = OnlyRow(RunOf(Tier.Local, steps)).Cost;

        Assert.Equal(0.4, cell.Median);
        Assert.Equal("EUR", cell.Currency);
        Assert.Equal(MetricLabel.Estimated, cell.Label);
        Assert.Null(cell.Unit);
    }

    [Fact]
    public void Quality_cell_keeps_model_judged_label_over_the_scored_repeats()
    {
        StepReading[] steps =
        [
            Step(repeat: 1, quality: Metric.ModelJudged(3.0)),
            Step(repeat: 2, quality: Metric.NotApplicable("judge-failed: HttpRequestException")),
            Step(repeat: 3, quality: Metric.ModelJudged(5.0)),
        ];

        ReportCell cell = OnlyRow(RunOf(Tier.Local, steps)).Quality;

        Assert.Equal(4.0, cell.Median);
        Assert.Equal(2, cell.N);
        Assert.Equal(3, cell.Total);
        Assert.Equal(MetricLabel.ModelJudged, cell.Label);
    }

    // The bad run is the third input but sorts second (local, cloudgpu, nebius), so the message must carry the input index; its second row starts at steps[2]. The task and agent names must never reach the message.
    private static ArgumentException BuildWithBadSecondRow(Func<int, StepReading> bad)
    {
        StepReading[] badSteps =
        [
            Step(Tier.CloudGpu, task: "first-task", repeat: 1),
            Step(Tier.CloudGpu, task: "first-task", repeat: 2),
            bad(3),
            bad(4),
        ];
        SelectedRun badRun = RunOf(Tier.CloudGpu, badSteps);
        SelectedRun nebiusRun = RunOfNet(Tier.Nebius, 1.0);
        SelectedRun localRun = RunOfNet(Tier.Local, 1.0);

        return Assert.Throws<ArgumentException>(() => ReportAggregator.Build([nebiusRun, localRun, badRun]));
    }

    [Fact]
    public void Valued_repeats_with_two_labels_throw_naming_the_run_step_and_member()
    {
        ArgumentException failure = BuildWithBadSecondRow(repeat => Step(
            Tier.CloudGpu,
            task: "secret-task",
            agent: "secret-agent",
            repeat: repeat,
            inputTokens: repeat == 3 ? Metric.Measured(100.0, "tokens") : Metric.EstimatedQuantity(100.0, "tokens")));

        Assert.Equal("runs", failure.ParamName);
        Assert.StartsWith("runs[2].Document.Steps[2].inputTokens: the valued repeats of its row hold more than one label.", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Valued_repeats_with_two_units_throw_naming_the_run_step_and_member()
    {
        ArgumentException failure = BuildWithBadSecondRow(repeat => Step(
            Tier.CloudGpu,
            task: "secret-task",
            agent: "secret-agent",
            repeat: repeat,
            latency: Metric.Measured(1.0, repeat == 3 ? "ms" : "s")));

        Assert.Equal("runs", failure.ParamName);
        Assert.StartsWith("runs[2].Document.Steps[2].latencyMs: the valued repeats of its row hold more than one unit.", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_cell_member_holds_its_own_field_of_the_step()
    {
        StepReading step = Step(
            gross: Metric.Measured(101.0, "J"),
            net: Metric.Measured(61.0, "J"),
            avgW: Metric.Measured(92.0, "W"),
            peakW: Metric.Measured(113.0, "W"),
            inputTokens: Metric.Measured(124.0, "tokens"),
            outputTokens: Metric.Measured(85.0, "tokens"),
            latency: Metric.Measured(1006.0, "ms"),
            ttft: Metric.Measured(257.0, "ms"),
            cost: Metric.Estimated(0.8, "EUR"),
            quality: Metric.ModelJudged(4.0));

        ReportRow row = OnlyRow(RunOf(Tier.Local, [step]));

        Assert.Equal(101.0, row.GrossJoules.Median);
        Assert.Equal(61.0, row.NetJoules.Median);
        Assert.Equal(92.0, row.AvgW.Median);
        Assert.Equal(113.0, row.PeakW.Median);
        Assert.Equal(124.0, row.InputTokens.Median);
        Assert.Equal(85.0, row.OutputTokens.Median);
        Assert.Equal(1006.0, row.LatencyMs.Median);
        Assert.Equal(257.0, row.TtftMs.Median);
        Assert.Equal(0.8, row.Cost.Median);
        Assert.Equal(4.0, row.Quality.Median);
        Assert.Equal("W", row.AvgW.Unit);
        Assert.Equal("tokens", row.OutputTokens.Unit);
    }

    [Fact]
    public void Median_of_two_huge_values_does_not_overflow()
    {
        const double huge = 1.7e308;
        StepReading[] steps = [Step(repeat: 1, latency: Metric.Measured(huge, "ms")), Step(repeat: 2, latency: Metric.Measured(huge, "ms"))];

        ReportCell cell = OnlyRow(RunOf(Tier.Local, steps)).LatencyMs;

        Assert.Equal(huge, cell.Median);
        Assert.Equal(2, cell.N);
    }

    [Fact]
    public void A_large_run_with_a_distinct_task_and_agent_per_step_builds_in_linear_time()
    {
        const int count = 2000;
        StepReading[] steps = [.. Enumerable.Range(0, count).Select(index => Step(task: $"task-{index:D5}", agent: $"agent-{index:D5}"))];
        SelectedRun run = RunOf(Tier.Local, steps);
        var clock = System.Diagnostics.Stopwatch.StartNew();

        ReportModel model = ReportAggregator.Build([run]);

        clock.Stop();
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"Build took {clock.Elapsed.TotalSeconds:F1} s for {count} steps.");
        Assert.Equal(count, model.Rows.Count);
        Assert.Equal(steps.Select(step => (step.Task, step.Agent)), model.Rows.Select(row => (row.Task, row.Agent)));
    }

    [Fact]
    public void Error_repeat_is_excluded_and_counted()
    {
        StepReading[] steps =
        [
            Step(repeat: 1, net: Metric.Measured(10.0, "J")),
            Step(repeat: 2, outcome: StepOutcomes.Error, net: Metric.Measured(1000.0, "J")),
            Step(repeat: 3, net: Metric.Measured(14.0, "J")),
        ];

        ReportRow row = OnlyRow(RunOf(Tier.Local, steps));

        Assert.Equal(3, row.Repeats);
        Assert.Equal(1, row.Errors);
        Assert.Equal(0, row.Cancelled);
        Assert.Equal(0, row.Incomplete);
        Assert.Equal(12.0, row.NetJoules.Median);
        Assert.Equal(14.0, row.NetJoules.Max);
        Assert.Equal(2, row.NetJoules.N);
        Assert.Equal(2, row.NetJoules.Total);
    }

    [Fact]
    public void Cancelled_and_incomplete_repeats_are_counted_apart()
    {
        StepReading[] steps =
        [
            Step(repeat: 1),
            Step(repeat: 2, outcome: StepOutcomes.Cancelled),
            Step(repeat: 3, outcome: StepOutcomes.Incomplete),
            Step(repeat: 4, outcome: StepOutcomes.Incomplete),
            Step(repeat: 5, outcome: StepOutcomes.Error),
        ];

        ReportRow row = OnlyRow(RunOf(Tier.Local, steps));

        Assert.Equal(5, row.Repeats);
        Assert.Equal(1, row.Cancelled);
        Assert.Equal(2, row.Incomplete);
        Assert.Equal(1, row.Errors);
        Assert.Equal(1, row.NetJoules.N);
        Assert.Equal(1, row.NetJoules.Total);
    }

    [Fact]
    public void All_non_ok_row_is_NA_no_ok_step()
    {
        StepReading[] steps =
        [
            Step(repeat: 1, outcome: StepOutcomes.Error),
            Step(repeat: 2, outcome: StepOutcomes.Cancelled),
        ];

        ReportRow row = OnlyRow(RunOf(Tier.Local, steps));

        Assert.Equal(2, row.Repeats);
        Assert.Equal(1, row.Errors);
        Assert.Equal(1, row.Cancelled);
        Assert.Equal(ReadingFlags.None, row.Flags);
        foreach (ReportCell cell in ValueCells(row))
        {
            Assert.Equal(MetricLabel.NotApplicable, cell.Label);
            Assert.Null(cell.Median);
            Assert.Null(cell.Min);
            Assert.Null(cell.Max);
            Assert.Equal(0, cell.N);
            Assert.Equal(0, cell.Total);
            Assert.Equal([ReportAggregator.NoOkStepReason], cell.Reasons);
        }
    }

    [Fact]
    public void Judge_readings_never_enter_a_row()
    {
        StepReading[] steps = NetSteps(Tier.Local, 10.0, 12.0, 14.0);
        StepReading[] judge = [.. steps.Select(step => Step(Tier.Nvidia, repeat: step.Repeat, agent: "Judge", latency: Metric.Measured(999999.0, "ms"), judgedAgent: Planner))];

        ReportModel model = ReportAggregator.Build([RunOf(Tier.Local, steps, judge: judge)]);

        ReportRow row = Assert.Single(model.Rows);
        Assert.Equal(Planner, row.Agent);
        Assert.Equal(3, row.Repeats);
        Assert.Equal(1000.0, row.LatencyMs.Max);
        Assert.Equal(["model-a"], row.ModelIds);
    }

    [Fact]
    public void Flag_on_one_repeat_reaches_row()
    {
        StepReading[] steps =
        [
            Step(repeat: 1),
            Step(repeat: 2, flags: ReadingFlags.LowResolution),
            Step(repeat: 3),
        ];

        ReportRow row = OnlyRow(RunOf(Tier.Local, steps));

        Assert.Equal(ReadingFlags.LowResolution, row.Flags);
        Assert.Contains("low-resolution", ReadingFlagNames.ToStrings(row.Flags));
        Assert.Equal(MetricLabel.Measured, row.NetJoules.Label);
    }

    [Fact]
    public void Flags_of_the_ok_repeats_are_united_and_those_of_other_repeats_are_not()
    {
        StepReading[] steps =
        [
            Step(repeat: 1, flags: ReadingFlags.LowResolution),
            Step(repeat: 2, flags: ReadingFlags.SharedWindow),
            Step(repeat: 3, outcome: StepOutcomes.Error, flags: ReadingFlags.MaxTokensExceeded),
        ];

        ReportRow row = OnlyRow(RunOf(Tier.Local, steps));

        Assert.Equal(ReadingFlags.LowResolution | ReadingFlags.SharedWindow, row.Flags);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Baseline_unstable_reaches_every_row(bool unstable)
    {
        StepReading[] steps = [Step(agent: "Planner"), Step(agent: "Writer")];
        BaselineResult baseline = unstable
            ? new BaselineResult(Metric.Measured(30.0, "W"), Metric.Measured(6.0, "W"), seconds: 5.0, sampleCount: 50, coefficientOfVariation: 0.2, maxCoefficientOfVariation: 0.1, ReadingFlags.BaselineUnstable)
            : new BaselineResult(Metric.Measured(30.0, "W"), Metric.Measured(1.5, "W"), seconds: 5.0, sampleCount: 50, coefficientOfVariation: 0.05, maxCoefficientOfVariation: 0.1, ReadingFlags.None);

        ReportModel model = ReportAggregator.Build([RunOf(Tier.Local, steps, edit: document => document with { Baseline = baseline })]);

        Assert.Equal(2, model.Rows.Count);
        Assert.All(model.Rows, row => Assert.Equal(unstable, row.Flags.HasFlag(ReadingFlags.BaselineUnstable)));
    }

    [Fact]
    public void Rows_of_different_tiers_are_not_merged()
    {
        ReportModel model = ReportAggregator.Build(
        [
            RunOf(Tier.CloudGpu, [Step(Tier.CloudGpu, modelId: "shared-model", net: Metric.Measured(20.0, "J"))]),
            RunOf(Tier.Local, [Step(Tier.Local, modelId: "shared-model", net: Metric.Measured(10.0, "J"))]),
        ]);

        Assert.Equal(2, model.Rows.Count);
        Assert.Equal([Tier.Local, Tier.CloudGpu], model.Rows.Select(row => row.Tier));
        Assert.Equal([10.0, 20.0], model.Rows.Select(row => row.NetJoules.Median));
        Assert.All(model.Rows, row => Assert.Equal(1, row.Repeats));
        Assert.All(model.Rows, row => Assert.Equal(["shared-model"], row.ModelIds));
    }

    [Fact]
    public void Model_has_no_cross_tier_member()
    {
        Type[] types = [typeof(SelectedRun), typeof(ReportCell), typeof(ReportRow), typeof(TierReport), typeof(ReportModel), typeof(ReportAggregator)];

        string[] offending =
        [
            .. types
                .SelectMany(type => type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                .Select(member => $"{member.DeclaringType!.Name}.{member.Name}")
                .Where(name => CrossTierWords.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase))),
        ];

        Assert.Empty(offending);
    }

    [Fact]
    public void Primary_and_baseline_rows_of_a_tier_are_separate_rows()
    {
        ReportModel model = ReportAggregator.Build(
        [
            RunOf(Tier.Local, NetSteps(Tier.Local, 40.0), RunVariant.Baseline),
            RunOf(Tier.Local, NetSteps(Tier.Local, 10.0, 12.0)),
        ]);

        Assert.Equal(2, model.Rows.Count);
        Assert.Equal([RunVariant.Primary, RunVariant.Baseline], model.Rows.Select(row => row.Variant));
        Assert.Equal([11.0, 40.0], model.Rows.Select(row => row.NetJoules.Median));
        Assert.Equal([2, 1], model.Rows.Select(row => row.Repeats));
        Assert.Equal([RunVariant.Primary, RunVariant.Baseline], model.Tiers.Select(report => report.Variant));
        Assert.Equal([NameOf(Tier.Local, RunVariant.Primary), NameOf(Tier.Local, RunVariant.Baseline)], model.Tiers.Select(report => report.FileName));
    }

    [Fact]
    public void Tiers_and_rows_are_ordered_by_tier_with_the_baseline_right_after_its_primary()
    {
        ReportModel model = ReportAggregator.Build(
        [
            RunOf(Tier.Nebius, NetSteps(Tier.Nebius, 1.0), RunVariant.Baseline),
            RunOf(Tier.Local, NetSteps(Tier.Local, 1.0), RunVariant.Baseline),
            RunOf(Tier.Nebius, NetSteps(Tier.Nebius, 1.0)),
            RunOf(Tier.Nvidia, NetSteps(Tier.Nvidia, 1.0)),
            RunOf(Tier.Local, NetSteps(Tier.Local, 1.0)),
        ]);

        (Tier, RunVariant)[] expected =
        [
            (Tier.Local, RunVariant.Primary),
            (Tier.Local, RunVariant.Baseline),
            (Tier.Nvidia, RunVariant.Primary),
            (Tier.Nebius, RunVariant.Primary),
            (Tier.Nebius, RunVariant.Baseline),
        ];
        Assert.Equal(expected, model.Rows.Select(row => (row.Tier, row.Variant)));
        Assert.Equal(expected, model.Tiers.Select(report => (report.Tier, report.Variant)));
    }

    [Fact]
    public void Tiers_not_run_lists_the_known_tiers_without_a_primary_file()
    {
        ReportModel model = ReportAggregator.Build(
        [
            RunOfNet(Tier.Local, 1.0),
            RunOf(Tier.Nebius, NetSteps(Tier.Nebius, 1.0), RunVariant.Baseline),
        ]);

        Assert.Equal([Tier.CloudGpu, Tier.Nvidia, Tier.Nebius], model.TiersNotRun);
        Assert.Equal(2, model.Rows.Count);
    }

    [Fact]
    public void No_files_give_an_empty_report_with_all_tiers_not_run()
    {
        ReportModel model = ReportAggregator.Build([]);

        Assert.Empty(model.Tiers);
        Assert.Empty(model.Rows);
        Assert.Equal([Tier.Local, Tier.CloudGpu, Tier.Nvidia, Tier.Nebius], model.TiersNotRun);
        Assert.Equal([ReportSentences.GpuBoardOnly, ReportSentences.JudgeExcluded], model.Disclosures);
    }

    [Fact]
    public void Tasks_follow_the_run_order_then_ordinal_and_agents_follow_first_appearance()
    {
        StepReading[] steps =
        [
            Step(task: "a", agent: "Writer"),
            Step(task: "b", agent: "Writer"),
            Step(task: "z", agent: "Writer"),
            Step(task: "c", agent: "Writer"),
            Step(task: "b", agent: "Planner"),
            Step(task: "a", agent: "Planner"),
        ];

        ReportModel model = ReportAggregator.Build([RunOf(Tier.Local, steps, tasks: ["b", "a"])]);

        Assert.Equal(
            [("b", "Writer"), ("b", "Planner"), ("a", "Writer"), ("a", "Planner"), ("c", "Writer"), ("z", "Writer")],
            model.Rows.Select(row => (row.Task, row.Agent)));
    }

    [Fact]
    public void Agents_follow_their_first_appearance_across_all_tasks()
    {
        StepReading[] steps =
        [
            Step(task: "a", agent: "Planner"),
            Step(task: "a", agent: "Writer"),
            Step(task: "b", agent: "Writer"),
            Step(task: "b", agent: "Planner"),
        ];

        ReportModel model = ReportAggregator.Build([RunOf(Tier.Local, steps)]);

        Assert.Equal([("a", "Planner"), ("a", "Writer"), ("b", "Planner"), ("b", "Writer")], model.Rows.Select(row => (row.Task, row.Agent)));
    }

    [Fact]
    public void A_listed_task_without_steps_gives_no_row_in_a_partial_run()
    {
        StepReading[] steps = [Step(task: "first", agent: "Planner"), Step(task: "first", agent: "Writer")];
        SelectedRun run = RunOf(
            Tier.Local,
            steps,
            tasks: ["first", "second", "third"],
            edit: document => document with { Run = document.Run with { Status = RunStatus.Partial } });

        ReportModel model = ReportAggregator.Build([run]);

        Assert.Equal([("first", "Planner"), ("first", "Writer")], model.Rows.Select(row => (row.Task, row.Agent)));
        Assert.Equal(RunStatus.Partial, Assert.Single(model.Tiers).Status);
    }

    [Fact]
    public void Row_model_ids_are_distinct_and_ordinal()
    {
        StepReading[] steps = [Step(repeat: 1, modelId: "z-model"), Step(repeat: 2, modelId: "a-model"), Step(repeat: 3, modelId: "z-model")];

        Assert.Equal(["a-model", "z-model"], OnlyRow(RunOf(Tier.Local, steps)).ModelIds);
    }

    [Fact]
    public void Build_refuses_a_null_list_and_a_null_element()
    {
        Assert.Throws<ArgumentNullException>(() => ReportAggregator.Build(null!));
        Assert.Throws<ArgumentNullException>(() => ReportAggregator.Build([null!]));
    }

    [Fact]
    public void Build_refuses_two_runs_with_the_same_tier_and_variant()
    {
        SelectedRun first = RunOfNet(Tier.Local, 1.0);
        SelectedRun second = RunOf(Tier.Local, NetSteps(Tier.Local, 2.0), stamp: "20261024T093000Z");

        Assert.Throws<ArgumentException>(() => ReportAggregator.Build([first, second]));
    }

    [Theory]
    [InlineData("report.json")]
    [InlineData("probe-20261019T205344Z.json")]
    [InlineData("20261023T093000Z-local.json.partial")]
    [InlineData("runs\\20261023T093000Z-local.json")]
    [InlineData("")]
    public void Build_refuses_a_file_name_that_is_not_a_run_file_name(string name)
    {
        SelectedRun run = RunOfNet(Tier.Local, 1.0) with { FileName = name };

        Assert.Throws<ArgumentException>(() => ReportAggregator.Build([run]));
    }

    [Fact]
    public void Build_refuses_a_name_whose_tier_differs_from_the_document()
    {
        SelectedRun run = RunOfNet(Tier.Local, 1.0) with { FileName = NameOf(Tier.Nebius, RunVariant.Primary) };

        Assert.Throws<ArgumentException>(() => ReportAggregator.Build([run]));
    }

    [Theory]
    [InlineData(RunVariant.Primary, "20261023T093000Z-local-baseline.json")]
    [InlineData(RunVariant.Baseline, "20261023T093000Z-local.json")]
    public void Build_refuses_a_name_whose_suffix_differs_from_the_variant(RunVariant variant, string name)
    {
        SelectedRun run = RunOfNet(Tier.Local, 1.0) with { Variant = variant, FileName = name };

        Assert.Throws<ArgumentException>(() => ReportAggregator.Build([run]));
    }

    [Fact]
    public void Build_refuses_an_undefined_variant_and_a_null_document()
    {
        SelectedRun undefinedVariant = RunOfNet(Tier.Local, 1.0) with { Variant = (RunVariant)0 };
        SelectedRun nullDocument = RunOfNet(Tier.Local, 1.0) with { Document = null! };

        Assert.Throws<ArgumentException>(() => ReportAggregator.Build([undefinedVariant]));
        Assert.Throws<ArgumentNullException>(() => ReportAggregator.Build([nullDocument]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Build_refuses_a_step_of_another_tier_than_the_run(int mismatched)
    {
        StepReading[] steps = [.. Enumerable.Range(0, 3).Select(index => Step(index == mismatched ? Tier.Nvidia : Tier.Local, repeat: index + 1))];

        ArgumentException failure = Assert.Throws<ArgumentException>(() => ReportAggregator.Build([RunOf(Tier.Local, steps)]));

        Assert.Equal("runs", failure.ParamName);
        Assert.StartsWith($"runs[0].Document.Steps[{mismatched}].Tier differs from the run tier.", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(RunStatus.Running)]
    [InlineData(RunStatus.Failed)]
    [InlineData(RunStatus.Incomplete)]
    public void Build_refuses_a_run_that_is_not_completed_or_partial(RunStatus status)
    {
        SelectedRun run = RunOf(Tier.Local, NetSteps(Tier.Local, 1.0), edit: document => document with { Run = document.Run with { Status = status } });

        ArgumentException failure = Assert.Throws<ArgumentException>(() => ReportAggregator.Build([run]));

        Assert.Equal("runs", failure.ParamName);
        Assert.StartsWith("runs[0].Document.Run.Status is not completed or partial", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(RunStatus.Completed)]
    [InlineData(RunStatus.Partial)]
    public void Build_accepts_a_completed_or_partial_run(RunStatus status)
    {
        SelectedRun run = RunOf(Tier.Local, NetSteps(Tier.Local, 1.0), edit: document => document with { Run = document.Run with { Status = status } });

        Assert.Equal(status, Assert.Single(ReportAggregator.Build([run]).Tiers).Status);
    }

    [Fact]
    public void Build_refuses_a_non_publishable_run()
    {
        SelectedRun run = RunOf(Tier.Local, NetSteps(Tier.Local, 1.0), edit: document => document with { Run = document.Run with { Flags = [RunFlagNames.PartialOffload, RunFlagNames.NonPublishable] } });

        ArgumentException failure = Assert.Throws<ArgumentException>(() => ReportAggregator.Build([run]));

        Assert.Equal("runs", failure.ParamName);
        Assert.StartsWith("runs[0].Document.Run.Flags holds non-publishable", failure.Message, StringComparison.Ordinal);
    }

    // The run starts at 2026-10-23 09:30:00 UTC: another day and one second later are both another timestamp.
    [Theory]
    [InlineData("20261024T093000Z")]
    [InlineData("20261023T093001Z")]
    public void Build_refuses_a_name_whose_timestamp_is_not_the_start_of_the_run(string stamp)
    {
        SelectedRun run = RunOfNet(Tier.Local, 1.0) with { FileName = NameOf(Tier.Local, RunVariant.Primary, stamp) };

        ArgumentException failure = Assert.Throws<ArgumentException>(() => ReportAggregator.Build([run]));

        Assert.Equal("runs", failure.ParamName);
        Assert.StartsWith("runs[0].FileName has another timestamp than run.startUtc", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_takes_the_timestamp_of_the_name_from_the_start_of_the_run_to_the_second_in_utc()
    {
        // 09:30:00.9 at +02:00 is 07:30:00 UTC: the name carries the UTC second, the fraction is dropped.
        SelectedRun run = RunOf(Tier.Local, NetSteps(Tier.Local, 1.0), edit: document => document with { Run = document.Run with { StartUtc = new DateTimeOffset(2026, 10, 23, 9, 30, 0, 900, TimeSpan.FromHours(2)) } }, stamp: "20261023T073000Z");

        Assert.Single(ReportAggregator.Build([run]).Tiers);
    }

    [Fact]
    public void Totals_are_the_totals_of_each_file_and_never_summed()
    {
        SelectedRun local = RunOfNet(Tier.Local, 1.0);
        SelectedRun nvidia = RunOfNet(Tier.Nvidia, 1.0);

        ReportModel model = ReportAggregator.Build([local, nvidia]);

        Assert.Same(local.Document.Totals, model.Tiers[0].Totals);
        Assert.Same(nvidia.Document.Totals, model.Tiers[1].Totals);
    }

    [Fact]
    public void Tier_report_carries_the_facts_of_the_file()
    {
        SelectedRun run = RunOf(
            Tier.Local,
            NetSteps(Tier.Local, 1.0),
            edit: document => document with { Run = document.Run with { Status = RunStatus.Partial, Mode = RunMode.Concurrent, Flags = [RunFlagNames.PartialOffload], SelfJudged = null } });

        TierReport report = Assert.Single(ReportAggregator.Build([run]).Tiers);

        Assert.Equal(Tier.Local, report.Tier);
        Assert.Equal(RunVariant.Primary, report.Variant);
        Assert.Equal(NameOf(Tier.Local, RunVariant.Primary), report.FileName);
        Assert.Equal(run.Document.Run.StartUtc, report.StartUtc);
        Assert.Equal(RunStatus.Partial, report.Status);
        Assert.Equal(RunMode.Concurrent, report.Mode);
        Assert.Equal("Test GPU", report.GpuName);
        Assert.Equal("555.00", report.DriverVersion);
        Assert.Equal("ollama", report.LocalRuntime);
        Assert.Equal("0.1.0", report.RuntimeVersion);
        Assert.Equal("Q4_K_M", report.Precision);
        Assert.Equal("off", report.ReasoningMode);
        Assert.Null(report.PowerSource);
        Assert.Equal(run.Document.Environment.Models, report.Models);
        Assert.Equal([RunFlagNames.PartialOffload], report.RunFlags);
        Assert.Null(report.SelfJudged);
    }

    [Fact]
    public void Tier_report_lists_judge_models_energy_methods_and_the_sampling_fallback()
    {
        StepReading[] steps =
        [
            Step(repeat: 1, method: EnergyMethod.Sampling),
            Step(repeat: 2, method: EnergyMethod.Counter),
            Step(repeat: 3, method: EnergyMethod.Sampling),
        ];
        StepReading[] judge = [.. steps.Select(step => Step(Tier.Nvidia, repeat: step.Repeat, agent: "Judge", modelId: step.Repeat == 2 ? "b-judge" : "a-judge", judgedAgent: Planner))];

        TierReport report = Assert.Single(ReportAggregator.Build([RunOf(Tier.Local, steps, judge: judge)]).Tiers);

        Assert.Equal(["a-judge", "b-judge"], report.JudgeModelIds);
        Assert.Equal([EnergyMethod.Counter, EnergyMethod.Sampling], report.EnergyMethods);
        Assert.False(report.SamplingFallback);
    }

    [Theory]
    [InlineData(StepOutcomes.Ok, true)]
    [InlineData(StepOutcomes.Error, false)]
    public void Sampling_fallback_counts_only_ok_steps(string outcome, bool expected)
    {
        StepReading[] steps = [Step(repeat: 1), Step(repeat: 2, outcome: outcome, flags: ReadingFlags.SamplingFallback, method: EnergyMethod.Sampling)];

        TierReport report = Assert.Single(ReportAggregator.Build([RunOf(Tier.Local, steps)]).Tiers);

        Assert.Equal(expected, report.SamplingFallback);
    }

    [Fact]
    public void Disclosure_sentences_are_verbatim()
    {
        Assert.Equal("Judge calls are excluded from workflow totals.", ReportSentences.JudgeExcluded);
        Assert.Equal("Self-judging: the judge model is also a workflow model.", ReportSentences.SelfJudged);
        Assert.Equal("Models differ across tiers: ", ReportSentences.ModelsDifferPrefix);
    }

    [Fact]
    public void Disclosures_hold_the_two_fixed_sentences_for_a_plain_run()
    {
        ReportModel model = ReportAggregator.Build([RunOfNet(Tier.Local, 1.0)]);

        Assert.Equal([ReportSentences.GpuBoardOnly, ReportSentences.JudgeExcluded], model.Disclosures);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(null, false)]
    public void Self_judged_sentence_follows_the_run_flag(bool? selfJudged, bool expected)
    {
        SelectedRun run = RunOf(Tier.Local, NetSteps(Tier.Local, 1.0), edit: document => document with { Run = document.Run with { SelfJudged = selfJudged } });

        ReportModel model = ReportAggregator.Build([run]);

        Assert.Equal(expected, model.Disclosures.Contains(ReportSentences.SelfJudged));
        Assert.Equal(selfJudged, Assert.Single(model.Tiers).SelfJudged);
    }

    [Fact]
    public void Concurrent_sentence_follows_the_mode_of_any_tier()
    {
        SelectedRun serialized = RunOfNet(Tier.Local, 1.0);
        SelectedRun concurrent = RunOf(Tier.Nebius, NetSteps(Tier.Nebius, 1.0), edit: document => document with { Run = document.Run with { Mode = RunMode.Concurrent } });

        Assert.DoesNotContain(ReportSentences.ConcurrentOverlap, ReportAggregator.Build([serialized]).Disclosures);
        Assert.Contains(ReportSentences.ConcurrentOverlap, ReportAggregator.Build([serialized, concurrent]).Disclosures);
    }

    [Fact]
    public void Models_differ_line_lists_models_and_precision_per_primary_tier_in_tier_order()
    {
        SelectedRun nvidia = RunOf(Tier.Nvidia, NetSteps(Tier.Nvidia, 1.0), edit: document => WithModels(document, null, "nv-model"));
        SelectedRun local = RunOf(Tier.Local, NetSteps(Tier.Local, 1.0), edit: document => WithModels(document, "Q4_K_M", "z-model", "a-model"));

        ReportModel model = ReportAggregator.Build([nvidia, local]);

        Assert.Equal(
            [ReportSentences.GpuBoardOnly, ReportSentences.JudgeExcluded, "Models differ across tiers: local=a-model+z-model (Q4_K_M), nvidia=nv-model (N/A)"],
            model.Disclosures);
    }

    [Fact]
    public void Disclosures_keep_their_order_and_appear_once()
    {
        SelectedRun local = RunOf(
            Tier.Local,
            NetSteps(Tier.Local, 1.0),
            edit: document => WithModels(document, "Q4_K_M", "m1") with { Run = document.Run with { SelfJudged = true, Mode = RunMode.Concurrent } });
        SelectedRun nebius = RunOf(
            Tier.Nebius,
            NetSteps(Tier.Nebius, 1.0),
            edit: document => WithModels(document, "FP8", "m2") with { Run = document.Run with { SelfJudged = true, Mode = RunMode.Concurrent } });

        IReadOnlyList<string> disclosures = ReportAggregator.Build([nebius, local]).Disclosures;

        Assert.Equal(
            [
                ReportSentences.GpuBoardOnly,
                ReportSentences.JudgeExcluded,
                ReportSentences.SelfJudged,
                ReportSentences.ConcurrentOverlap,
                "Models differ across tiers: local=m1 (Q4_K_M), nebius=m2 (FP8)",
            ],
            disclosures);
        Assert.Equal(disclosures.Count, disclosures.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Models_differ_line_is_absent_for_one_primary_tier_equal_tiers_and_baseline_files()
    {
        SelectedRun local = RunOf(Tier.Local, NetSteps(Tier.Local, 1.0), edit: document => WithModels(document, "Q4_K_M", "m1"));
        SelectedRun localBaseline = RunOf(Tier.Local, NetSteps(Tier.Local, 1.0), RunVariant.Baseline, edit: document => WithModels(document, "FP8", "other"));
        SelectedRun same = RunOf(Tier.Nebius, NetSteps(Tier.Nebius, 1.0), edit: document => WithModels(document, "Q4_K_M", "m1"));

        Assert.DoesNotContain(ReportAggregator.Build([local]).Disclosures, line => line.StartsWith(ReportSentences.ModelsDifferPrefix, StringComparison.Ordinal));
        Assert.DoesNotContain(ReportAggregator.Build([local, localBaseline]).Disclosures, line => line.StartsWith(ReportSentences.ModelsDifferPrefix, StringComparison.Ordinal));
        Assert.DoesNotContain(ReportAggregator.Build([local, same]).Disclosures, line => line.StartsWith(ReportSentences.ModelsDifferPrefix, StringComparison.Ordinal));
    }

    [Fact]
    public void Models_differ_line_appears_when_only_the_precision_differs()
    {
        SelectedRun local = RunOf(Tier.Local, NetSteps(Tier.Local, 1.0), edit: document => WithModels(document, "Q4_K_M", "m1"));
        SelectedRun cloud = RunOf(Tier.CloudGpu, NetSteps(Tier.CloudGpu, 1.0), edit: document => WithModels(document, "FP8", "m1"));

        Assert.Contains("Models differ across tiers: local=m1 (Q4_K_M), cloudgpu=m1 (FP8)", ReportAggregator.Build([local, cloud]).Disclosures);
    }

    [Fact]
    public void Models_differ_line_prints_NA_for_a_tier_without_models_or_precision()
    {
        SelectedRun local = RunOf(Tier.Local, NetSteps(Tier.Local, 1.0), edit: document => WithModels(document, null));
        SelectedRun nebius = RunOf(Tier.Nebius, NetSteps(Tier.Nebius, 1.0), edit: document => WithModels(document, "FP8", "m2"));

        Assert.Contains("Models differ across tiers: local=N/A (N/A), nebius=m2 (FP8)", ReportAggregator.Build([local, nebius]).Disclosures);
    }

    [Fact]
    public void A_non_ok_repeat_counts_for_model_ids_and_energy_methods_but_not_for_the_sampling_fallback()
    {
        StepReading[] steps =
        [
            Step(repeat: 1, modelId: "model-a", method: EnergyMethod.Counter),
            Step(repeat: 2, outcome: StepOutcomes.Error, modelId: "model-z", method: EnergyMethod.Sampling, flags: ReadingFlags.SamplingFallback),
        ];

        ReportModel model = ReportAggregator.Build([RunOf(Tier.Local, steps)]);

        Assert.Equal(["model-a", "model-z"], Assert.Single(model.Rows).ModelIds);
        TierReport report = Assert.Single(model.Tiers);
        Assert.Equal([EnergyMethod.Counter, EnergyMethod.Sampling], report.EnergyMethods);
        Assert.False(report.SamplingFallback);
    }

    // ---- the Markdown summary with honest labels (AJ-083) ----

    private const int ColumnCount = 15;
    private const int TierColumn = 0;
    private const int TaskColumn = 1;
    private const int AgentColumn = 2;
    private const int GrossColumn = 3;
    private const int NetColumn = 4;
    private const int AvgWColumn = 5;
    private const int PeakWColumn = 6;
    private const int InputTokensColumn = 7;
    private const int OutputTokensColumn = 8;
    private const int LatencyColumn = 9;
    private const int TtftColumn = 10;
    private const int CostColumn = 11;
    private const int QualityColumn = 12;
    private const int LabelsColumn = 13;
    private const int FlagsColumn = 14;

    private const string TableHeaderRow = "| tier | task | agent | gross J | net J | avg W | peak W | input tokens | output tokens | latency ms | TTFT ms | cost | quality | labels | flags |";
    private const string Hostile = "|<b>[x](y)![i](j)";
    private const string HostileEscaped = "\\|\\<b\\>\\[x\\]\\(y\\)\\!\\[i\\]\\(j\\)";

    [GeneratedRegex(@"(?<!\\)\|", RegexOptions.CultureInvariant)]
    private static partial Regex UnescapedPipe();

    private static string Render(params SelectedRun[] runs) => SummaryMarkdownRenderer.Render(ReportAggregator.Build(runs));

    private static string[] LinesOf(string text) => text.Split('\n');

    private static string[] TableLines(string text) => [.. LinesOf(text).Where(line => line.StartsWith('|'))];

    private static string[] DataRows(string text) => [.. TableLines(text).Skip(2)];

    private static string[] CellsOf(string row) => [.. UnescapedPipe().Split(row).Skip(1).SkipLast(1).Select(cell => cell.Trim())];

    private static string[] OnlyRowCells(string text) => CellsOf(Assert.Single(DataRows(text)));

    private static string NotRunRow(string tier) => $"| {tier} | not run |" + string.Concat(Enumerable.Repeat(" |", ColumnCount - 2));

    private static string[] TierCells(string text) => [.. DataRows(text).Select(row => CellsOf(row)[TierColumn])];

    private static string[] BulletLines(string text) => [.. LinesOf(text).Skip(1).TakeWhile(line => line.Length > 0)];

    private static string[] DisclosureLines(string text) => [.. LinesOf(text).SkipWhile(line => line != string.Empty).Skip(1).TakeWhile(line => line.Length > 0)];

    private static IEnumerable<RunDocument> EveryReportFixture() =>
    [
        RunDocumentFixtures.HostedNoUsage(),
        RunDocumentFixtures.Concurrent(Tier.Local),
        RunDocumentFixtures.Concurrent(Tier.Nebius),
        RunDocumentFixtures.LowResolution(),
        RunDocumentFixtures.SamplingFallback(),
        RunDocumentFixtures.Partial(Tier.Local),
        RunDocumentFixtures.Partial(Tier.Nvidia),
    ];

    // The CultureInfo of the thread is set for the call only.
    private static string RenderUnder(string culture, params SelectedRun[] runs)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);

            return Render(runs);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private static ReportCell Missing(MetricLabel label, params string[] reasons) => new(null, null, null, 0, 1, label, reasons, null, null);

    private static ReportCell Valued(double median, double min, double max, int n, int total, MetricLabel label = MetricLabel.Measured, string? currency = null) =>
        new(median, min, max, n, total, label, [], null, currency);

    private static string Lone(char value) => $"a{value}b";

    // ---- the formats (AJ-083-T3) ----

    [Theory]
    [InlineData(12.34, "12.3")]
    [InlineData(90.0, "90.0")]
    [InlineData(0.0, "0.0")]
    [InlineData(1234.56, "1234.6")]
    [InlineData(-5.04, "-5.0")]
    public void Joules_and_watts_have_one_decimal(double value, string expected)
    {
        Assert.Equal(expected, InvariantFormat.Joules(value));
        Assert.Equal(expected, InvariantFormat.Watts(value));
    }

    [Theory]
    [InlineData(1000.0, "1000")]
    [InlineData(1000.4, "1000")]
    [InlineData(0.0, "0")]
    public void Milliseconds_have_no_decimal(double value, string expected) => Assert.Equal(expected, InvariantFormat.Milliseconds(value));

    [Theory]
    [InlineData(0.00123, "0.0012")]
    [InlineData(0.0, "0.0000")]
    [InlineData(12.0, "12.0000")]
    public void Cost_has_four_decimals(double value, string expected) => Assert.Equal(expected, InvariantFormat.Cost(value));

    [Theory]
    [InlineData(4.0, "4")]
    [InlineData(120.0, "120")]
    [InlineData(3.5, "3.5")]
    [InlineData(2.27, "2.3")]
    public void Count_is_an_integer_unless_the_value_is_not_integral(double value, string expected) => Assert.Equal(expected, InvariantFormat.Count(value));

    [Fact]
    public void Formats_use_the_invariant_culture()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");

            Assert.Equal("1234567.8", InvariantFormat.Joules(1234567.8));
            Assert.Equal("0.0012", InvariantFormat.Cost(0.00123));
            Assert.Equal("3.5", InvariantFormat.Count(3.5));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void A_negative_zero_is_written_as_zero()
    {
        double negativeZero = double.NegativeZero;

        Assert.Equal("0.0", InvariantFormat.Joules(negativeZero));
        Assert.Equal("0.0", InvariantFormat.Watts(negativeZero));
        Assert.Equal("0", InvariantFormat.Milliseconds(negativeZero));
        Assert.Equal("0.0000", InvariantFormat.Cost(negativeZero));
        Assert.Equal("0", InvariantFormat.Count(negativeZero));
        Assert.Equal("0.0", InvariantFormat.Joules(-0.04));
        Assert.Equal("-0.5", InvariantFormat.Joules(-0.5));
    }

    [Fact]
    public void Cell_prints_median_range_and_count_with_the_value_first()
    {
        string text = InvariantFormat.Cell(Valued(12.0, 10.0, 14.0, 3, 3), CellKind.Joules, Escaped);

        Assert.Equal("12.0 [10.0 to 14.0] n=3", text);
        Assert.StartsWith("12.0", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Cell_of_one_repeat_has_no_range()
    {
        Assert.Equal("12.0 n=1", InvariantFormat.Cell(Valued(12.0, 12.0, 12.0, 1, 1), CellKind.Joules, Escaped));
        Assert.Equal("90.0 n=1", InvariantFormat.Cell(Valued(90.0, 90.0, 90.0, 1, 1), CellKind.Watts, Escaped));
        Assert.Equal("1000 n=1", InvariantFormat.Cell(Valued(1000.0, 1000.0, 1000.0, 1, 1), CellKind.Milliseconds, Escaped));
        Assert.Equal("120 n=1", InvariantFormat.Cell(Valued(120.0, 120.0, 120.0, 1, 1), CellKind.Count, Escaped));
    }

    [Fact]
    public void Cell_says_of_total_only_when_a_repeat_has_no_value()
    {
        Assert.Equal("15.0 [10.0 to 20.0] n=2 of 3", InvariantFormat.Cell(Valued(15.0, 10.0, 20.0, 2, 3), CellKind.Joules, Escaped));
        Assert.Equal("15.0 [10.0 to 20.0] n=2", InvariantFormat.Cell(Valued(15.0, 10.0, 20.0, 2, 2), CellKind.Joules, Escaped));
    }

    [Fact]
    public void Cell_prints_the_currency_after_the_value_and_a_label_other_than_measured_last()
    {
        Assert.Equal("0.0012 EUR n=1 estimated", InvariantFormat.Cell(Valued(0.0012, 0.0012, 0.0012, 1, 1, MetricLabel.Estimated, "EUR"), CellKind.Cost, Escaped));
        Assert.Equal("0.0012 EUR [0.0010 to 0.0014] n=3 estimated", InvariantFormat.Cell(Valued(0.0012, 0.001, 0.0014, 3, 3, MetricLabel.Estimated, "EUR"), CellKind.Cost, Escaped));
        Assert.Equal("0.0012 USD n=1", InvariantFormat.Cell(Valued(0.0012, 0.0012, 0.0012, 1, 1, MetricLabel.Measured, "USD"), CellKind.Cost, Escaped));
    }

    [Fact]
    public void Cell_prints_quality_as_an_integer_or_one_decimal_for_an_even_median_between_integers()
    {
        Assert.Equal("4 n=1 model-judged", InvariantFormat.Cell(Valued(4.0, 4.0, 4.0, 1, 1, MetricLabel.ModelJudged), CellKind.Quality, Escaped));
        Assert.Equal("3.5 [3 to 4] n=2 model-judged", InvariantFormat.Cell(Valued(3.5, 3.0, 4.0, 2, 2, MetricLabel.ModelJudged), CellKind.Quality, Escaped));
        Assert.Equal("120.5 [120 to 121] n=2", InvariantFormat.Cell(Valued(120.5, 120.0, 121.0, 2, 2), CellKind.Count, Escaped));
    }

    [Fact]
    public void Cell_without_a_value_prints_its_label_and_reasons_never_a_number()
    {
        Assert.Equal("N/A (hosted-tier)", InvariantFormat.Cell(Missing(MetricLabel.NotApplicable, "hosted-tier"), CellKind.Joules, Escaped));
        Assert.Equal("not-reported (no-usage)", InvariantFormat.Cell(Missing(MetricLabel.NotReported, "no-usage"), CellKind.Count, Escaped));
        Assert.Equal("N/A (a; b)", InvariantFormat.Cell(Missing(MetricLabel.NotApplicable, "a", "b"), CellKind.Cost, Escaped));
        Assert.Equal("N/A", InvariantFormat.Cell(Missing(MetricLabel.NotApplicable), CellKind.Cost, Escaped));
    }

    [Fact]
    public void Cell_prints_at_most_three_reasons_then_a_count()
    {
        Assert.Equal("N/A (a; b; c)", InvariantFormat.Cell(Missing(MetricLabel.NotApplicable, "a", "b", "c"), CellKind.Joules, Escaped));
        Assert.Equal("N/A (a; b; c; +1 more)", InvariantFormat.Cell(Missing(MetricLabel.NotApplicable, "a", "b", "c", "d"), CellKind.Joules, Escaped));
        Assert.Equal("not-reported (a; b; c; +7 more)", InvariantFormat.Cell(Missing(MetricLabel.NotReported, [.. Enumerable.Range(0, 10).Select(index => ((char)('a' + index)).ToString())]), CellKind.Joules, Escaped));
    }

    [Fact]
    public void Cell_escapes_the_reasons_and_the_currency_but_not_the_numbers_or_the_label()
    {
        static string Angle(string text) => $"<{text}>";

        Assert.Equal("N/A (<r1>; <r2>)", InvariantFormat.Cell(Missing(MetricLabel.NotApplicable, "r1", "r2"), CellKind.Joules, Angle));
        Assert.Equal("0.0012 <EUR> n=1 estimated", InvariantFormat.Cell(Valued(0.0012, 0.0012, 0.0012, 1, 1, MetricLabel.Estimated, "EUR"), CellKind.Cost, Angle));
        Assert.Equal("12.0 [10.0 to 14.0] n=3", InvariantFormat.Cell(Valued(12.0, 10.0, 14.0, 3, 3), CellKind.Joules, Angle));
    }

    [Fact]
    public void Cell_minimum_of_negative_zero_prints_zero()
    {
        string text = InvariantFormat.Cell(Valued(0.5, double.NegativeZero, 1.0, 2, 2), CellKind.Joules, Escaped);

        Assert.Equal("0.5 [0.0 to 1.0] n=2", text);
        Assert.DoesNotContain("-", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Cell_refuses_null_arguments_and_an_undefined_kind()
    {
        ReportCell cell = Valued(1.0, 1.0, 1.0, 1, 1);

        _ = Assert.Throws<ArgumentNullException>(() => InvariantFormat.Cell(null!, CellKind.Joules, Escaped));
        _ = Assert.Throws<ArgumentNullException>(() => InvariantFormat.Cell(cell, CellKind.Joules, null!));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => InvariantFormat.Cell(cell, (CellKind)99, Escaped));
    }

    [Fact]
    public void Metric_formats_one_totals_value_with_its_label_and_reason()
    {
        Assert.Equal("360.0", InvariantFormat.Metric(Metric.Measured(360.0, "J"), CellKind.Joules, Escaped));
        Assert.Equal("160", InvariantFormat.Metric(Metric.Measured(160.0, "tokens"), CellKind.Count, Escaped));
        Assert.Equal("0.0012 USD estimated", InvariantFormat.Metric(Metric.Estimated(0.0012, "USD"), CellKind.Cost, Escaped));
        Assert.Equal("N/A (missing-step-costs)", InvariantFormat.Metric(Metric.NotApplicable("missing-step-costs"), CellKind.Cost, Escaped));
        Assert.Equal("not-reported (provider-omitted-usage)", InvariantFormat.Metric(Metric.NotReported("provider-omitted-usage"), CellKind.Count, Escaped));
        Assert.Equal("N/A (<a|b>)", InvariantFormat.Metric(Metric.NotApplicable("a|b"), CellKind.Count, text => $"<{text}>"));
    }

    private static string Escaped(string text) => text;

    // ---- the escaping (AJ-083, P-18) ----

    [Fact]
    public void ReportText_replaces_lone_surrogate()
    {
        Assert.Equal("a?b", ReportText.Clean(Lone((char)0xD800), ReportText.NameMaxLength));
        Assert.Equal("a?b", ReportText.Clean(Lone((char)0xDC00), ReportText.NameMaxLength));
        Assert.Equal("a?b", ReportText.Clean(Lone((char)0xFFFE), ReportText.NameMaxLength));
        Assert.Equal("a?b", ReportText.Clean(Lone((char)0xFFFF), ReportText.NameMaxLength));
        Assert.Equal("a??b", ReportText.Clean("a" + (char)0xDC00 + (char)0xD800 + "b", ReportText.NameMaxLength));
    }

    [Fact]
    public void ReportText_keeps_a_valid_surrogate_pair_and_replaces_controls_and_bidi_marks()
    {
        string pair = char.ConvertFromUtf32(0x1F600);

        Assert.Equal("a" + pair + "b", ReportText.Clean("a" + pair + "b", ReportText.NameMaxLength));
        Assert.Equal("a?b?c", ReportText.Clean("a\nb" + (char)0x202E + "c", ReportText.NameMaxLength));
    }

    [Fact]
    public void ReportText_cuts_to_the_limit_and_marks_the_cut()
    {
        string cut = ReportText.Clean(new string('a', 500), ReportText.ReasonMaxLength);

        Assert.Equal(new string('a', ReportText.ReasonMaxLength) + "...", cut);
        Assert.Equal(128, ReportText.NameMaxLength);
        Assert.Equal(80, ReportText.ReasonMaxLength);
        _ = Assert.Throws<ArgumentNullException>(() => ReportText.Clean(null!, 10));
    }

    [Theory]
    [InlineData('\\')]
    [InlineData('`')]
    [InlineData('*')]
    [InlineData('_')]
    [InlineData('~')]
    [InlineData('[')]
    [InlineData(']')]
    [InlineData('(')]
    [InlineData(')')]
    [InlineData('<')]
    [InlineData('>')]
    [InlineData('!')]
    [InlineData('|')]
    [InlineData('&')]
    [InlineData('#')]
    [InlineData('$')]
    public void MarkdownText_puts_a_backslash_before_each_special_character(char special) =>
        Assert.Equal($"a\\{special}b", MarkdownText.Escape($"a{special}b"));

    [Fact]
    public void MarkdownText_leaves_plain_text_and_cuts_at_the_given_length()
    {
        Assert.Equal("model-a 1.5: x=y, z/w + 4 @ 5%", MarkdownText.Escape("model-a 1.5: x=y, z/w + 4 @ 5%"));
        Assert.Equal(new string('a', ReportText.ReasonMaxLength) + "...", MarkdownText.Escape(new string('a', 200), ReportText.ReasonMaxLength));
        Assert.Equal("a?b", MarkdownText.Escape(Lone((char)0xD800)));
    }

    // ---- the fixtures (AJ-083-T2) ----

    [Fact]
    public void Report_fixtures_pass_the_run_file_validator()
    {
        foreach (RunDocument document in EveryReportFixture())
        {
            string text = RunDocumentFixtures.Serialise(document);

            Assert.Empty(RunFileValidator.Check(NameOf(document.Run.Tier, RunVariant.Primary), text));
        }
    }

    [Fact]
    public void Report_fixtures_describe_the_case_they_are_named_for()
    {
        RunDocument noUsage = RunDocumentFixtures.HostedNoUsage();
        StepReading hostedStep = Assert.Single(noUsage.Steps);
        Assert.Equal(Tier.Nvidia, noUsage.Run.Tier);
        Assert.Equal(MetricLabel.NotReported, hostedStep.InputTokens.Label);
        Assert.Equal(MetricLabel.NotReported, hostedStep.OutputTokens.Label);
        Assert.Equal(MetricLabel.NotApplicable, hostedStep.Cost.Label);
        Assert.Equal(MetricLabel.NotApplicable, hostedStep.GrossJoules.Label);

        Assert.Equal(RunMode.Concurrent, RunDocumentFixtures.Concurrent(Tier.Local).Run.Mode);
        Assert.Equal(Tier.Nebius, RunDocumentFixtures.Concurrent(Tier.Nebius).Run.Tier);
        Assert.All(RunDocumentFixtures.LowResolution().Steps, step => Assert.True(step.Flags.HasFlag(ReadingFlags.LowResolution)));

        RunDocument fallback = RunDocumentFixtures.SamplingFallback();
        Assert.All(fallback.Steps, step => Assert.True(step.Flags.HasFlag(ReadingFlags.SamplingFallback) && step.Method == EnergyMethod.Sampling));
        Assert.False(fallback.Probe!.EnergyCounterSupported);

        RunDocument partial = RunDocumentFixtures.Partial(Tier.Nvidia);
        Assert.Equal(RunStatus.Partial, partial.Run.Status);
        Assert.False(string.IsNullOrWhiteSpace(partial.Run.StopReason));
        Assert.Equal(Tier.Nvidia, partial.Run.Tier);
    }

    [Fact]
    public void Report_fixtures_build_a_model_the_aggregator_accepts()
    {
        foreach (RunDocument document in EveryReportFixture())
        {
            ReportModel model = ReportAggregator.Build([Wrap(document)]);

            Assert.NotEmpty(model.Rows);
            Assert.All(model.Rows, row => Assert.Equal(document.Run.Tier, row.Tier));
        }
    }

    // ---- the header (AJ-083-T5) ----

    [Fact]
    public void Header_and_honesty_sentence()
    {
        string models = string.Join(", ", AgentCatalog.Names.Select(agent => $"{agent}=model-a"));

        string text = Render(RunOf(Tier.Local, NetSteps(Tier.Local, 10.0)), Wrap(RunDocumentFixtures.HostedNoUsage()));
        string[] lines = LinesOf(text);

        Assert.Equal("# Agent Joules results", lines[0]);
        Assert.Equal(
            "- local: file 20261023T093000Z-local.json; run date 2026-10-23T09:30:00Z; status completed; mode serialized; GPU Test GPU; driver 555.00; local runtime ollama 0.1.0; precision Q4\\_K\\_M; reasoning mode off; power source N/A; models Planner=model-a, Writer=model-b; judge none; self-judging no; energy method counter",
            lines[1]);
        Assert.Equal("- cloudgpu: not run", lines[2]);
        Assert.Equal(
            $"- nvidia: file 20261023T093000Z-nvidia.json; run date 2026-10-23T09:30:00Z; status completed; mode serialized; GPU N/A; driver N/A; local runtime N/A; precision N/A; reasoning mode off; power source N/A; models {models}; judge none; self-judging no; energy method N/A",
            lines[3]);
        Assert.Equal("- nebius: not run", lines[4]);
        Assert.Equal(string.Empty, lines[5]);
        Assert.Equal(ReportSentences.GpuBoardOnly, lines[6]);
        Assert.Equal("Energy is GPU-board energy only.", ReportSentences.GpuBoardOnly);
    }

    [Fact]
    public void The_honesty_sentence_follows_the_header_facts_and_precedes_the_table()
    {
        string[] lines = LinesOf(Render(RunOf(Tier.Local, NetSteps(Tier.Local, 10.0))));

        int lastBullet = Array.FindLastIndex(lines, line => line.StartsWith("- ", StringComparison.Ordinal));
        int sentence = Array.IndexOf(lines, ReportSentences.GpuBoardOnly);
        int table = Array.IndexOf(lines, TableHeaderRow);

        Assert.True(lastBullet > 0);
        Assert.True(sentence > lastBullet);
        Assert.True(table > sentence);
        Assert.Equal(string.Empty, lines[table - 1]);
    }

    [Fact]
    public void Header_shows_a_missing_fact_as_NA_and_a_runtime_without_a_version_alone()
    {
        SelectedRun run = RunOf(
            Tier.Local,
            NetSteps(Tier.Local, 1.0),
            edit: document => document with { Environment = document.Environment with { GpuName = null, DriverVersion = null, RuntimeVersion = null, Precision = null, ReasoningMode = null, PowerSource = "nvml" } });

        string bullet = Assert.Single(BulletLines(Render(run)), line => line.StartsWith("- local:", StringComparison.Ordinal));

        Assert.Contains("; GPU N/A; driver N/A; local runtime ollama; precision N/A; reasoning mode N/A; power source nvml;", bullet, StringComparison.Ordinal);
    }

    [Fact]
    public void Header_formats_the_run_date_in_utc_to_the_second()
    {
        SelectedRun run = RunOf(Tier.Local, NetSteps(Tier.Local, 1.0), edit: document => document with { Run = document.Run with { StartUtc = new DateTimeOffset(2026, 10, 23, 9, 30, 0, 900, TimeSpan.Zero) } });

        Assert.Contains("; run date 2026-10-23T09:30:00Z;", Render(run), StringComparison.Ordinal);
    }

    [Fact]
    public void A_tier_without_a_run_file_is_shown_as_not_run()
    {
        string text = Render(Wrap(RunDocumentFixtures.HostedNoUsage()));

        string[] rows = DataRows(text);
        Assert.Equal(NotRunRow("local"), rows[0]);
        Assert.Equal(NotRunRow("cloudgpu"), rows[1]);
        Assert.Equal(NotRunRow("nebius"), rows[3]);
        Assert.Equal(4, rows.Length);
        Assert.Equal(["local", "cloudgpu", "nvidia", "nebius"], TierCells(text));
        Assert.StartsWith("| local | not run | |", rows[0], StringComparison.Ordinal);
        Assert.Equal(ColumnCount + 1, rows[0].Count(character => character == '|'));
        Assert.Equal(ReportSentences.NotRun, "not run");
        Assert.Contains("- local: not run", BulletLines(text));
        Assert.Contains("- nebius: not run", BulletLines(text));
    }

    [Fact]
    public void Not_run_rows_and_bullets_take_the_place_of_their_tier()
    {
        string text = Render(RunOf(Tier.Local, NetSteps(Tier.Local, 1.0)), RunOf(Tier.Nebius, NetSteps(Tier.Nebius, 1.0)));

        Assert.Equal(["local", "cloudgpu", "nvidia", "nebius"], TierCells(text));
        string[] bullets = BulletLines(text);
        Assert.Equal(4, bullets.Length);
        Assert.StartsWith("- local: file", bullets[0], StringComparison.Ordinal);
        Assert.Equal("- cloudgpu: not run", bullets[1]);
        Assert.Equal("- nvidia: not run", bullets[2]);
        Assert.StartsWith("- nebius: file", bullets[3], StringComparison.Ordinal);
    }

    [Fact]
    public void A_baseline_only_tier_lists_its_not_run_line_before_its_baseline_rows()
    {
        string text = Render(RunOf(Tier.Nebius, NetSteps(Tier.Nebius, 1.0), RunVariant.Baseline));

        Assert.Equal(["local", "cloudgpu", "nvidia", "nebius", "nebius baseline"], TierCells(text));
        string[] rows = DataRows(text);
        Assert.Equal(NotRunRow("nebius"), rows[3]);
        string[] bullets = BulletLines(text);
        Assert.Equal("- nebius: not run", bullets[3]);
        Assert.StartsWith("- nebius baseline: file 20261023T093000Z-nebius-baseline.json; ", bullets[4], StringComparison.Ordinal);
    }

    [Fact]
    public void Baseline_rows_say_tier_baseline_and_follow_the_rows_of_their_tier()
    {
        string text = Render(
            RunOf(Tier.Local, NetSteps(Tier.Local, 40.0), RunVariant.Baseline),
            RunOf(Tier.Nvidia, NetSteps(Tier.Nvidia, 1.0)),
            RunOf(Tier.Local, NetSteps(Tier.Local, 10.0, 12.0)));

        Assert.Equal(["local", "local baseline", "cloudgpu", "nvidia", "nebius"], TierCells(text));
        string[] rows = DataRows(text);
        Assert.Equal("11.0 [10.0 to 12.0] n=2", CellsOf(rows[0])[NetColumn]);
        Assert.Equal("40.0 n=1", CellsOf(rows[1])[NetColumn]);
        string[] bullets = BulletLines(text);
        Assert.StartsWith("- local: file 20261023T093000Z-local.json; ", bullets[0], StringComparison.Ordinal);
        Assert.StartsWith("- local baseline: file 20261023T093000Z-local-baseline.json; ", bullets[1], StringComparison.Ordinal);
    }

    // ---- the table (AJ-083-T4) ----

    [Fact]
    public void The_table_has_fifteen_columns_in_the_pinned_order()
    {
        string text = Render(RunOf(Tier.Local, NetSteps(Tier.Local, 10.0)), Wrap(RunDocumentFixtures.HostedNoUsage()));
        string[] table = TableLines(text);

        Assert.Equal(TableHeaderRow, table[0]);
        Assert.Equal("|" + string.Concat(Enumerable.Repeat("---|", ColumnCount)), table[1]);
        Assert.All(table, row => Assert.Equal(ColumnCount, CellsOf(row).Length));
        Assert.Equal(ColumnCount, CellsOf(TableHeaderRow).Length);
    }

    [Fact]
    public void A_row_prints_every_cell_in_the_grammar_with_one_decimal_for_energy_and_none_for_latency()
    {
        string text = Render(RunOf(Tier.Local, NetSteps(Tier.Local, 60.0)));
        string[] row = RowOfTask(text);

        Assert.Equal("local", row[TierColumn]);
        Assert.Equal(TaskName, row[TaskColumn]);
        Assert.Equal(Planner, row[AgentColumn]);
        Assert.Equal("90.0 n=1", row[GrossColumn]);
        Assert.Equal("60.0 n=1", row[NetColumn]);
        Assert.Equal("90.0 n=1", row[AvgWColumn]);
        Assert.Equal("110.0 n=1", row[PeakWColumn]);
        Assert.Equal("120 n=1", row[InputTokensColumn]);
        Assert.Equal("80 n=1", row[OutputTokensColumn]);
        Assert.Equal("1000 n=1", row[LatencyColumn]);
        Assert.Equal("250 n=1", row[TtftColumn]);
        Assert.Equal("N/A (cost-not-configured)", row[CostColumn]);
        Assert.Equal("N/A (not-scored)", row[QualityColumn]);
        Assert.Equal("measured, N/A", row[LabelsColumn]);
        Assert.Equal(string.Empty, row[FlagsColumn]);
    }

    [Fact]
    public void A_row_with_repeats_shows_median_range_and_n()
    {
        string[] row = RowOfTask(Render(RunOfNet(Tier.Local, 10.0, 12.0, 14.0)));

        Assert.Equal("12.0 [10.0 to 14.0] n=3", row[NetColumn]);
        Assert.Equal("42.0 [40.0 to 44.0] n=3", row[GrossColumn]);
        Assert.Equal("120 [120 to 120] n=3", row[InputTokensColumn]);
    }

    [Fact]
    public void A_row_with_a_repeat_that_has_no_value_says_n_of_total_and_prints_no_reason()
    {
        StepReading[] steps =
        [
            Step(repeat: 1, net: Metric.Measured(10.0, "J")),
            Step(repeat: 2, net: Metric.Measured(20.0, "J")),
            Step(repeat: 3, net: Metric.NotReported("sampler-unavailable")),
        ];

        string[] row = RowOfTask(Render(RunOf(Tier.Local, steps)));

        Assert.Equal("15.0 [10.0 to 20.0] n=2 of 3", row[NetColumn]);
        Assert.DoesNotContain("sampler-unavailable", string.Join('|', row), StringComparison.Ordinal);
    }

    [Fact]
    public void Estimated_cost_and_model_judged_quality_keep_their_label_and_not_reported_keeps_its_reason()
    {
        StepReading step = Step(
            cost: Metric.Estimated(0.0012, "EUR"),
            quality: Metric.ModelJudged(4.0),
            inputTokens: Metric.NotReported(TokenCounts.OmittedReason),
            ttft: Metric.NotApplicable("buffered-call"));

        string[] row = RowOfTask(Render(RunOf(Tier.Local, [step])));

        Assert.Equal("0.0012 EUR n=1 estimated", row[CostColumn]);
        Assert.Equal("4 n=1 model-judged", row[QualityColumn]);
        Assert.Equal("not-reported (provider-omitted-usage)", row[InputTokensColumn]);
        Assert.Equal("N/A (buffered-call)", row[TtftColumn]);
        Assert.Equal("measured, estimated, model-judged, N/A, not-reported", row[LabelsColumn]);
    }

    [Fact]
    public void Quality_of_an_even_count_between_two_integers_prints_one_decimal()
    {
        StepReading[] steps = [Step(repeat: 1, quality: Metric.ModelJudged(3.0)), Step(repeat: 2, quality: Metric.ModelJudged(4.0))];

        string[] row = RowOfTask(Render(RunOf(Tier.Local, steps)));

        Assert.Equal("3.5 [3 to 4] n=2 model-judged", row[QualityColumn]);
    }

    [Fact]
    public void The_flags_column_lists_row_flags_run_flags_partial_and_the_failed_repeats()
    {
        StepReading[] steps =
        [
            Step(repeat: 1, flags: ReadingFlags.LowResolution),
            Step(repeat: 2, outcome: StepOutcomes.Error),
            Step(repeat: 3, outcome: StepOutcomes.Cancelled),
            Step(repeat: 4, outcome: StepOutcomes.Incomplete),
            Step(repeat: 5, outcome: StepOutcomes.Incomplete),
        ];
        SelectedRun run = RunOf(
            Tier.Local,
            steps,
            edit: document => document with { Run = document.Run with { Status = RunStatus.Partial, Flags = [RunFlagNames.PartialOffload] } });

        string[] row = RowOfTask(Render(run));

        Assert.Equal("low-resolution, partial-offload, partial, 1 cancelled, 1 error, 2 incomplete", row[FlagsColumn]);
    }

    [Fact]
    public void Partial_run_says_partial()
    {
        string text = Render(Wrap(RunDocumentFixtures.Partial(Tier.Local)));

        string bullet = Assert.Single(BulletLines(text), line => line.StartsWith("- local:", StringComparison.Ordinal));
        Assert.Contains("; status partial;", bullet, StringComparison.Ordinal);
        Assert.Contains("partial", RowOfTask(text)[FlagsColumn].Split(", "));
    }

    [Fact]
    [Trait("Spec", "EDGE-003")]
    public void Low_resolution_flag_stays_visible_in_the_row_of_the_value()
    {
        string text = Render(Wrap(RunDocumentFixtures.LowResolution()));

        string row = Assert.Single(DataRows(text), line => line.Contains("low-resolution", StringComparison.Ordinal));
        string[] cells = CellsOf(row);
        Assert.Equal("low-resolution", cells[FlagsColumn]);
        Assert.Equal("60.0 n=1", cells[NetColumn]);
        Assert.Equal("local", cells[TierColumn]);
        Assert.True(row.IndexOf("60.0", StringComparison.Ordinal) < row.IndexOf("low-resolution", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Spec", "EDGE-002")]
    public void Sampling_fallback_header()
    {
        string text = Render(Wrap(RunDocumentFixtures.SamplingFallback()));

        string[] lines = LinesOf(text);
        int bullet = Array.FindIndex(lines, line => line.StartsWith("- local:", StringComparison.Ordinal));
        Assert.EndsWith("; energy method sampling", lines[bullet], StringComparison.Ordinal);
        Assert.Equal("  - Energy method: sampling (counter unsupported)", lines[bullet + 1]);
        Assert.Equal("Energy method: sampling (counter unsupported)", ReportSentences.SamplingFallback);
        Assert.Equal(1, text.Split(ReportSentences.SamplingFallback).Length - 1);
        Assert.Contains("sampling-fallback", RowOfTask(text)[FlagsColumn], StringComparison.Ordinal);
    }

    [Fact]
    public void The_sampling_fallback_line_follows_the_fallback_of_ok_steps_and_not_the_energy_methods()
    {
        ReportModel model = ReportAggregator.Build([RunOf(Tier.Local, NetSteps(Tier.Local, 1.0))]);
        TierReport report = Assert.Single(model.Tiers);

        string forced = SummaryMarkdownRenderer.Render(model with { Tiers = [report with { EnergyMethods = [EnergyMethod.Sampling], SamplingFallback = false }] });
        string fallback = SummaryMarkdownRenderer.Render(model with { Tiers = [report with { EnergyMethods = [EnergyMethod.Counter], SamplingFallback = true }] });

        Assert.Contains("; energy method sampling", forced, StringComparison.Ordinal);
        Assert.DoesNotContain(ReportSentences.SamplingFallback, forced, StringComparison.Ordinal);
        Assert.Contains("; energy method counter", fallback, StringComparison.Ordinal);
        Assert.Contains(ReportSentences.SamplingFallback, fallback, StringComparison.Ordinal);
    }

    [Fact]
    public void Both_energy_methods_are_listed_in_order()
    {
        ReportModel model = ReportAggregator.Build([RunOf(Tier.Local, NetSteps(Tier.Local, 1.0))]);
        TierReport report = Assert.Single(model.Tiers);

        string text = SummaryMarkdownRenderer.Render(model with { Tiers = [report with { EnergyMethods = [EnergyMethod.Counter, EnergyMethod.Sampling] }] });

        Assert.Contains("; energy method counter, sampling", text, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Spec", "EDGE-004")]
    public void Concurrent_sentence()
    {
        string concurrent = Render(Wrap(RunDocumentFixtures.Concurrent(Tier.Local)));
        string serialized = Render(RunOf(Tier.Local, NetSteps(Tier.Local, 1.0)));

        Assert.Equal("Per-step energy overlaps in concurrent mode and does not sum to the run total.", ReportSentences.ConcurrentOverlap);
        Assert.Equal(1, concurrent.Split(ReportSentences.ConcurrentOverlap).Length - 1);
        Assert.Contains(ReportSentences.ConcurrentOverlap, DisclosureLines(concurrent));
        Assert.Contains("; mode concurrent;", concurrent, StringComparison.Ordinal);
        Assert.DoesNotContain(ReportSentences.ConcurrentOverlap, serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void The_new_sentences_are_the_pinned_text()
    {
        Assert.Equal("In concurrent mode the cost total counts overlapping step windows once per step.", ReportSentences.ConcurrentCostOverlap);
        Assert.Equal("not run", ReportSentences.NotRun);
        Assert.Equal("Energy method: sampling (counter unsupported)", ReportSentences.SamplingFallback);
    }

    [Fact]
    [Trait("Spec", "EDGE-007")]
    public void Usage_free_row_shows_not_reported_and_NA()
    {
        string[] row = RowOfTask(Render(Wrap(RunDocumentFixtures.HostedNoUsage())));

        Assert.StartsWith("not-reported", row[InputTokensColumn], StringComparison.Ordinal);
        Assert.StartsWith("not-reported", row[OutputTokensColumn], StringComparison.Ordinal);
        Assert.Equal("not-reported (provider-omitted-usage)", row[InputTokensColumn]);
        Assert.StartsWith("N/A", row[CostColumn], StringComparison.Ordinal);
        Assert.Equal("N/A (tokens-not-reported)", row[CostColumn]);
        Assert.Equal("measured, N/A, not-reported", row[LabelsColumn]);
    }

    [Fact]
    public void Hosted_energy_is_NA_and_never_0()
    {
        string[] row = RowOfTask(Render(Wrap(RunDocumentFixtures.HostedNoUsage())));

        string[] energy = [row[GrossColumn], row[NetColumn], row[AvgWColumn], row[PeakWColumn]];
        Assert.All(energy, cell => Assert.Equal("N/A (hosted-tier)", cell));
        Assert.All(energy, cell => Assert.DoesNotContain("0.0", cell, StringComparison.Ordinal));
        Assert.Equal("nvidia", row[TierColumn]);
    }

    [Fact]
    public void Every_cost_shows_its_currency_code()
    {
        StepReading step = Step(Tier.Nebius, cost: Metric.Estimated(0.00004, "USD"));

        string[] row = RowOfTask(Render(RunOf(Tier.Nebius, [step])));

        Assert.Equal("0.000040 USD n=1 estimated", row[CostColumn]);
    }

    [Fact]
    public void Self_judged_disclosed()
    {
        SelectedRun run = RunOf(Tier.Local, NetSteps(Tier.Local, 1.0), edit: document => document with { Run = document.Run with { SelfJudged = true } });
        string text = Render(run);

        Assert.Contains("; self-judging yes;", text, StringComparison.Ordinal);
        Assert.Equal(1, text.Split(ReportSentences.SelfJudged).Length - 1);
        Assert.Contains(ReportSentences.SelfJudged, DisclosureLines(text));
    }

    [Fact]
    public void Self_judging_is_no_when_false_and_not_judged_when_unknown()
    {
        string no = Render(RunOf(Tier.Local, NetSteps(Tier.Local, 1.0)));
        string unknown = Render(RunOf(Tier.Local, NetSteps(Tier.Local, 1.0), edit: document => document with { Run = document.Run with { SelfJudged = null } }));

        Assert.Contains("; self-judging no;", no, StringComparison.Ordinal);
        Assert.DoesNotContain(ReportSentences.SelfJudged, no, StringComparison.Ordinal);
        Assert.Contains("; self-judging not judged;", unknown, StringComparison.Ordinal);
    }

    [Fact]
    public void No_judge_reading_prints_judge_none()
    {
        string text = Render(RunOf(Tier.Local, NetSteps(Tier.Local, 1.0)));

        Assert.Contains("; judge none;", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Judge_models_are_listed_by_id()
    {
        StepReading[] steps = [Step(repeat: 1), Step(repeat: 1, agent: "Writer")];
        StepReading[] oneJudge = [RunDocumentFixtures.JudgeReading("Planner")];
        StepReading[] twoJudges = [RunDocumentFixtures.JudgeReading("Planner"), Step(Tier.Nvidia, agent: "Judge", modelId: "judge-b", judgedAgent: "Writer")];

        string one = Render(RunOf(Tier.Local, steps, judge: oneJudge));
        string two = Render(RunOf(Tier.Local, steps, judge: twoJudges));

        Assert.Contains("; judge judge-model;", one, StringComparison.Ordinal);
        Assert.Contains("; judge judge-b, judge-model;", two, StringComparison.Ordinal);
    }

    [Fact]
    public void Disclosures_are_printed_in_model_order_one_per_line_with_the_constants_unchanged()
    {
        SelectedRun local = RunOf(Tier.Local, NetSteps(Tier.Local, 1.0), edit: document => WithModels(document, "Q4_K_M", "m1") with { Run = document.Run with { SelfJudged = true, Mode = RunMode.Concurrent } });
        SelectedRun nebius = RunOf(Tier.Nebius, NetSteps(Tier.Nebius, 1.0), edit: document => WithModels(document, "FP8", "m2"));
        ReportModel model = ReportAggregator.Build([local, nebius]);

        string[] lines = DisclosureLines(SummaryMarkdownRenderer.Render(model));

        Assert.Equal(model.Disclosures.Count, lines.Length);
        Assert.Equal(ReportSentences.GpuBoardOnly, lines[0]);
        Assert.Equal(ReportSentences.JudgeExcluded, lines[1]);
        Assert.Equal(ReportSentences.SelfJudged, lines[2]);
        Assert.Equal(ReportSentences.ConcurrentOverlap, lines[3]);
        Assert.StartsWith(ReportSentences.ModelsDifferPrefix, lines[4], StringComparison.Ordinal);
    }

    [Fact]
    public void Models_differ_disclosed_without_ratio()
    {
        SelectedRun local = RunOf(Tier.Local, NetSteps(Tier.Local, 1.0), edit: document => WithModels(document, "Q4_K_M", "m1"));
        SelectedRun nebius = RunOf(Tier.Nebius, NetSteps(Tier.Nebius, 1.0), edit: document => WithModels(document, "FP8", "m2"));

        string text = Render(local, nebius);

        Assert.Contains("Models differ across tiers", text, StringComparison.Ordinal);
        Assert.Contains("Models differ across tiers: local=m1 \\(Q4\\_K\\_M\\), nebius=m2 \\(FP8\\)", DisclosureLines(text));
        Assert.DoesNotMatch(@"\d\s*x\b|x-fold|fold\b", text);
        foreach (string word in new[] { "%", "ratio", "better", "greener", "than", "faster", "cheaper" })
        {
            Assert.DoesNotContain(word, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Same_models_at_the_same_precision_print_no_models_differ_line()
    {
        SelectedRun local = RunOf(Tier.Local, NetSteps(Tier.Local, 1.0), edit: document => WithModels(document, "Q4_K_M", "m1"));
        SelectedRun same = RunOf(Tier.Nebius, NetSteps(Tier.Nebius, 1.0), edit: document => WithModels(document, "Q4_K_M", "m1"));

        Assert.DoesNotContain("Models differ", Render(local, same), StringComparison.Ordinal);
    }

    [Fact]
    public void Rendered_text_has_no_cross_tier_figure_or_ranking_word()
    {
        string text = Render(
            RunOf(Tier.Local, NetSteps(Tier.Local, 10.0, 12.0), edit: document => WithModels(document, "Q4_K_M", "m1")),
            RunOf(Tier.CloudGpu, NetSteps(Tier.CloudGpu, 20.0)),
            RunOf(Tier.Nvidia, NetSteps(Tier.Nvidia, 1.0)),
            RunOf(Tier.Nebius, NetSteps(Tier.Nebius, 1.0), edit: document => WithModels(document, "FP8", "m2")),
            RunOf(Tier.Local, NetSteps(Tier.Local, 40.0), RunVariant.Baseline));

        foreach (string word in CrossTierWords)
        {
            Assert.DoesNotContain(word, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Summary_prints_no_run_total_judge_total_or_cost_map()
    {
        string text = Render(RunOf(Tier.Local, NetSteps(Tier.Local, 10.0)));

        // The hand-built totals of the fixture document hold 360.0 J gross, 160 tokens, 4000 ms and a USD cost; none of them reaches the summary.
        Assert.DoesNotContain("360.0", text, StringComparison.Ordinal);
        Assert.DoesNotContain("4000", text, StringComparison.Ordinal);
        Assert.DoesNotContain("USD", text, StringComparison.Ordinal);
        Assert.DoesNotContain("missing-step-costs", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Output_is_deterministic_with_lf_newlines_and_no_clock()
    {
        SelectedRun[] runs = [RunOf(Tier.Local, NetSteps(Tier.Local, 10.0, 12.0, 14.0)), Wrap(RunDocumentFixtures.HostedNoUsage())];

        string first = Render(runs);
        string second = Render(runs);
        string german = RenderUnder("de-DE", runs);

        Assert.Equal(first, second);
        Assert.Equal(first, german);
        Assert.DoesNotContain('\r', first);
        Assert.EndsWith("|\n", first, StringComparison.Ordinal);
        Assert.False(first.EndsWith("\n\n", StringComparison.Ordinal));
        Assert.All(first, character => Assert.True(character < 128));
        Assert.Matches(@"^# Agent Joules results\n", first);
    }

    [Fact]
    public void Empty_model_renders_four_not_run_rows_and_the_two_standing_sentences()
    {
        string text = Render();

        Assert.Equal([NotRunRow("local"), NotRunRow("cloudgpu"), NotRunRow("nvidia"), NotRunRow("nebius")], DataRows(text));
        Assert.Equal([ReportSentences.GpuBoardOnly, ReportSentences.JudgeExcluded], DisclosureLines(text));
        Assert.Equal(["- local: not run", "- cloudgpu: not run", "- nvidia: not run", "- nebius: not run"], BulletLines(text));
    }

    [Fact]
    public void Render_refuses_null()
    {
        _ = Assert.Throws<ArgumentNullException>(() => SummaryMarkdownRenderer.Render(null!));
        _ = Assert.Throws<ArgumentNullException>(() => ReportHeader.Lines(null!, Escaped));
        _ = Assert.Throws<ArgumentNullException>(() => ReportHeader.Lines(ReportAggregator.Build([]), null!));
    }

    [Fact]
    public void Header_lines_are_the_bullets_one_empty_line_and_the_disclosures()
    {
        ReportModel model = ReportAggregator.Build([RunOf(Tier.Local, NetSteps(Tier.Local, 1.0))]);

        IReadOnlyList<string> lines = ReportHeader.Lines(model, Escaped);

        Assert.StartsWith("- local: file ", lines[0], StringComparison.Ordinal);
        Assert.Equal("- cloudgpu: not run", lines[1]);
        Assert.Equal("- nebius: not run", lines[3]);
        Assert.Equal(string.Empty, lines[4]);
        Assert.Equal(model.Disclosures, [.. lines.Skip(5)]);
    }

    // ---- hostile text (AJ-083, P-18, AJ-082 L-3) ----

    [Fact]
    public void Hostile_model_id_keeps_fifteen_cells()
    {
        const string hostileId = "a|b [x](y) ![i](j) <b>";
        StepReading step = Step(agent: hostileId, task: hostileId, modelId: hostileId);
        SelectedRun run = RunOf(
            Tier.Local,
            [step],
            edit: document => document with { Environment = document.Environment with { Models = [new EnvironmentModel(hostileId, hostileId, Baseline: false)] } });

        string text = Render(run);

        Assert.All(TableLines(text), row => Assert.Equal(ColumnCount, CellsOf(row).Length));
        Assert.DoesNotContain("<b>", text, StringComparison.Ordinal);
        Assert.DoesNotContain("[x](y)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("![i]", text, StringComparison.Ordinal);
        string[] row = RowOfTask(text, "a\\|b");
        Assert.Equal("a\\|b \\[x\\]\\(y\\) \\!\\[i\\]\\(j\\) \\<b\\>", row[AgentColumn]);
        Assert.All(BulletLines(text).Concat(DisclosureLines(text)), line => Assert.DoesNotMatch(@"(?<!\\)\|", line));
    }

    [Fact]
    public void Quality_reason_with_pipe_is_escaped()
    {
        StepReading step = Step(quality: Metric.NotApplicable("judge-failed: a|b"));

        string text = Render(RunOf(Tier.Local, [step]));

        string[] row = RowOfTask(text);
        Assert.Equal("N/A (judge-failed: a\\|b)", row[QualityColumn]);
        Assert.All(TableLines(text), line => Assert.Equal(ColumnCount, CellsOf(line).Length));
    }

    [Fact]
    public void Reasons_are_cut_to_eighty_characters_and_capped_per_cell()
    {
        string longReason = new('r', 300);
        ReportModel model = ReportAggregator.Build([RunOf(Tier.Local, NetSteps(Tier.Local, 1.0))]);
        ReportRow row = Assert.Single(model.Rows);
        ReportCell cell = Missing(MetricLabel.NotApplicable, longReason, "b" + (char)0x202E, "c", "d", "e", "f");

        string text = SummaryMarkdownRenderer.Render(model with { Rows = [row with { NetJoules = cell }] });

        string net = RowOfTask(text)[NetColumn];
        Assert.Equal($"N/A ({new string('r', ReportText.ReasonMaxLength)}...; b?; c; +3 more)", net);
    }

    [Fact]
    public void A_minimum_of_negative_zero_prints_as_zero_in_the_row()
    {
        ReportModel model = ReportAggregator.Build([RunOf(Tier.Local, NetSteps(Tier.Local, 1.0, 2.0))]);
        ReportRow row = Assert.Single(model.Rows);

        string text = SummaryMarkdownRenderer.Render(model with { Rows = [row with { NetJoules = Valued(0.5, double.NegativeZero, 1.0, 2, 2) }] });

        Assert.Equal("0.5 [0.0 to 1.0] n=2", RowOfTask(text)[NetColumn]);
    }

    [Fact]
    public void Every_string_member_of_the_model_and_every_disclosure_is_escaped()
    {
        ReportModel built = ReportAggregator.Build([RunOf(Tier.Local, NetSteps(Tier.Local, 1.0), judge: [RunDocumentFixtures.JudgeReading("Planner")])]);
        TierReport tier = Assert.Single(built.Tiers);
        ReportRow row = Assert.Single(built.Rows);
        static ReportCell WithHostile(ReportCell cell) => cell with { Reasons = [Hostile], Unit = Hostile, Currency = Hostile };
        ReportModel model = built with
        {
            Tiers =
            [
                tier with
                {
                    GpuName = Hostile,
                    DriverVersion = Hostile,
                    LocalRuntime = Hostile,
                    RuntimeVersion = Hostile,
                    Precision = Hostile,
                    ReasoningMode = Hostile,
                    PowerSource = Hostile,
                    Models = [new EnvironmentModel(Hostile, Hostile, Baseline: false)],
                    JudgeModelIds = [Hostile, Hostile + "2"],
                },
            ],
            Rows =
            [
                row with
                {
                    Task = Hostile,
                    Agent = Hostile,
                    ModelIds = [Hostile],
                    GrossJoules = WithHostile(row.GrossJoules),
                    NetJoules = Valued(1.0, 1.0, 1.0, 1, 1, MetricLabel.Estimated, Hostile),
                    Cost = WithHostile(row.Cost),
                    Quality = Missing(MetricLabel.NotReported, Hostile),
                },
            ],
            Disclosures = [.. built.Disclosures, Hostile, ReportSentences.ModelsDifferPrefix + "local=" + Hostile + " (" + Hostile + "), nvidia=" + Hostile],
        };

        string text = SummaryMarkdownRenderer.Render(model);

        Assert.DoesNotContain("<b>", text, StringComparison.Ordinal);
        Assert.DoesNotContain("[x](y)", text, StringComparison.Ordinal);
        Assert.Contains(HostileEscaped, text, StringComparison.Ordinal);
        Assert.All(TableLines(text), line => Assert.Equal(ColumnCount, CellsOf(line).Length));
        Assert.All(BulletLines(text).Concat(DisclosureLines(text)), line => Assert.DoesNotMatch(@"(?<!\\)\|", line));
        Assert.Contains(HostileEscaped, DisclosureLines(text));
        Assert.Contains(DisclosureLines(text), line => line.StartsWith(ReportSentences.ModelsDifferPrefix, StringComparison.Ordinal) && line.Contains("local=" + HostileEscaped + " \\(" + HostileEscaped + "\\)", StringComparison.Ordinal));
    }

    [Fact]
    public void A_forged_models_differ_text_is_not_parsed_or_printed()
    {
        ReportModel built = ReportAggregator.Build([RunOf(Tier.Local, NetSteps(Tier.Local, 1.0))]);
        string forged = ReportSentences.ModelsDifferPrefix + "local=m1\n# heading, nvidia=" + (char)0x202E + "<script>";

        string text = SummaryMarkdownRenderer.Render(built with { Disclosures = [forged] });

        string line = Assert.Single(DisclosureLines(text));
        Assert.Equal(ReportSentences.ModelsDifferPrefix + "local=model-a+model-b \\(Q4\\_K\\_M\\)", line);
        Assert.DoesNotContain("script", text, StringComparison.Ordinal);
        Assert.DoesNotContain("heading", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_does_not_mutate_the_model_it_is_given()
    {
        ReportModel model = ReportAggregator.Build([RunOf(Tier.Local, NetSteps(Tier.Local, 10.0, 12.0))]);
        int rows = model.Rows.Count;
        string[] disclosures = [.. model.Disclosures];

        _ = SummaryMarkdownRenderer.Render(model);

        Assert.Equal(rows, model.Rows.Count);
        Assert.Equal(disclosures, model.Disclosures);
    }

    // The data row of the one task of a fixture (the not-run rows have no task); the first row whose task starts with the given text.
    private static string[] RowOfTask(string text, string taskStart = TaskName) =>
        DataRows(text).Select(CellsOf).First(cells => cells[TaskColumn].StartsWith(taskStart, StringComparison.Ordinal));

    // ---- the fix round of AJ-083 (gates: spec, security, code review) ----

    // A key built at run time, with underscores: the escape of Markdown would put a backslash inside it and hide it from a secret scan.
    private static string FakeKey() => string.Concat("nvapi", "-", "Zq9_xK3", "_mP7w");

    private static string NoCompletedStepsRow(string tier, string flags) =>
        $"| {tier} | no completed steps |" + string.Concat(Enumerable.Repeat(" |", ColumnCount - 3)) + (flags.Length == 0 ? " |" : $" {flags} |");

    private static ReportModel ModelWithDisclosures(params string[] disclosures) =>
        ReportAggregator.Build([RunOf(Tier.Local, NetSteps(Tier.Local, 1.0))]) with { Disclosures = disclosures };

    [Fact]
    public void ReportText_masks_a_key_before_the_cut_and_the_replacement()
    {
        string key = FakeKey();

        Assert.Equal("x nvapi-**** y", ReportText.Clean($"x {key} y", ReportText.NameMaxLength));

        // Cut first would leave "nvapi-Zq" of the key in front of the cut marker; mask first leaves only the mask.
        string cut = ReportText.Clean(new string('a', 72) + key, ReportText.ReasonMaxLength);

        Assert.Equal(new string('a', 72) + "nvapi-**...", cut);
        Assert.DoesNotContain("Zq", cut, StringComparison.Ordinal);
    }

    [Fact]
    public void A_key_in_a_quality_reason_is_masked_in_the_whole_summary()
    {
        string key = FakeKey();
        StepReading step = Step(quality: Metric.NotApplicable("judge-failed: " + key));

        string text = Render(RunOf(Tier.Local, [step], edit: document => document with { Environment = document.Environment with { GpuName = "gpu " + key } }));

        Assert.Equal("N/A (judge-failed: nvapi-\\*\\*\\*\\*)", RowOfTask(text)[QualityColumn]);
        Assert.Contains("; GPU gpu nvapi-\\*\\*\\*\\*;", text, StringComparison.Ordinal);
        Assert.DoesNotContain(key, text, StringComparison.Ordinal);
        Assert.DoesNotContain("Zq9", text, StringComparison.Ordinal);
        Assert.DoesNotContain("xK3", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportText_replaces_supplementary_format_characters()
    {
        string tag = char.ConvertFromUtf32(0xE0041);
        string musical = char.ConvertFromUtf32(0x1D173);
        string nonCharacter = char.ConvertFromUtf32(0x1FFFE);

        Assert.Equal("a?b", ReportText.Clean("a" + tag + "b", ReportText.NameMaxLength));
        Assert.Equal("a?b", ReportText.Clean("a" + musical + "b", ReportText.NameMaxLength));
        Assert.Equal("a?b", ReportText.Clean("a" + nonCharacter + "b", ReportText.NameMaxLength));
        Assert.Equal("a" + char.ConvertFromUtf32(0x1F600) + "b", ReportText.Clean("a" + char.ConvertFromUtf32(0x1F600) + "b", ReportText.NameMaxLength));
    }

    [Fact]
    public void MarkdownText_escapes_the_dollar_sign()
    {
        Assert.Equal("\\$x\\$ costs \\$\\$", MarkdownText.Escape("$x$ costs $$"));
    }

    [Fact]
    public void The_models_differ_split_bounds_the_line_whatever_the_model_id_holds()
    {
        string hostile = string.Concat(Enumerable.Repeat("m, ", 33_333));
        SelectedRun local = RunOf(Tier.Local, NetSteps(Tier.Local, 1.0), edit: document => WithModels(document, "Q4_K_M", hostile));
        SelectedRun nebius = RunOf(Tier.Nebius, NetSteps(Tier.Nebius, 1.0), edit: document => WithModels(document, "FP8", "m2"));

        string line = Assert.Single(DisclosureLines(Render(local, nebius)), candidate => candidate.StartsWith(ReportSentences.ModelsDifferPrefix, StringComparison.Ordinal));

        // At most one entry per known tier, each cut at the name limit plus the cut marker, plus the prefix and the separators.
        Assert.True(line.Length <= ReportSentences.ModelsDifferPrefix.Length + (4 * (ReportText.NameMaxLength + 3 + 2)) + (3 * 2), $"The line has {line.Length} characters.");
    }

    [Fact]
    public void A_real_four_tier_models_differ_line_is_not_cut()
    {
        string text = Render(
            RunOf(Tier.Local, NetSteps(Tier.Local, 1.0), edit: document => WithModels(document, "Q4_K_M", "nemotron-3-nano:30b")),
            RunOf(Tier.CloudGpu, NetSteps(Tier.CloudGpu, 1.0), edit: document => WithModels(document, "FP8", "nvidia/NVIDIA-Nemotron-3-Nano-30B-A3B-FP8")),
            RunOf(Tier.Nvidia, NetSteps(Tier.Nvidia, 1.0), edit: document => WithModels(document, null, "nvidia/nemotron-nano-3-30b-a3b")),
            RunOf(Tier.Nebius, NetSteps(Tier.Nebius, 1.0), edit: document => WithModels(document, null, "nvidia/NVIDIA-Nemotron-3-Nano-30B-A3B")));

        string line = Assert.Single(DisclosureLines(text), candidate => candidate.StartsWith(ReportSentences.ModelsDifferPrefix, StringComparison.Ordinal));

        Assert.EndsWith(", nebius=nvidia/NVIDIA-Nemotron-3-Nano-30B-A3B \\(N/A\\)", line, StringComparison.Ordinal);
        Assert.Equal(
            ReportSentences.ModelsDifferPrefix
            + "local=nemotron-3-nano:30b \\(Q4\\_K\\_M\\), cloudgpu=nvidia/NVIDIA-Nemotron-3-Nano-30B-A3B-FP8 \\(FP8\\), nvidia=nvidia/nemotron-nano-3-30b-a3b \\(N/A\\), nebius=nvidia/NVIDIA-Nemotron-3-Nano-30B-A3B \\(N/A\\)",
            line);
    }

    [Theory]
    [InlineData("- item", "\\- item")]
    [InlineData("+ item", "\\+ item")]
    [InlineData("= item", "\\= item")]
    [InlineData("* item", "\\* item")]
    [InlineData("1. item", "1\\. item")]
    [InlineData("12) item", "12\\) item")]
    [InlineData("--- item", "\\--- item")]
    [InlineData("=== item", "\\=== item")]
    [InlineData("   - item", "\\- item")]
    [InlineData("# item", "\\# item")]
    [InlineData("$x$ item", "\\$x\\$ item")]
    public void A_disclosure_that_is_not_a_sentence_cannot_start_a_list_or_a_heading(string line, string expected)
    {
        string text = SummaryMarkdownRenderer.Render(ModelWithDisclosures(line));

        Assert.Equal(expected, Assert.Single(DisclosureLines(text)));
    }

    [Theory]
    [InlineData("- item", "\\- item")]
    [InlineData("+ item", "\\+ item")]
    [InlineData("* item", "\\* item")]
    [InlineData("7. item", "7\\. item")]
    [InlineData("7) item", "7\\) item")]
    [InlineData("77x item", "77x item")]
    [InlineData("item - item", "item - item")]
    public void The_leading_marker_is_escaped_whatever_the_escape_of_the_surface_does(string line, string expected)
    {
        IReadOnlyList<string> lines = ReportHeader.Lines(ModelWithDisclosures(line), Escaped);

        Assert.Equal(expected, lines[^1]);
    }

    [Fact]
    public void A_disclosure_equal_to_a_sentence_is_printed_unchanged_and_any_other_text_goes_through_the_escape()
    {
        string[] sentences =
        [
            ReportSentences.GpuBoardOnly,
            ReportSentences.JudgeExcluded,
            ReportSentences.SelfJudged,
            ReportSentences.ConcurrentOverlap,
            ReportSentences.ConcurrentCostOverlap,
            ReportSentences.SamplingFallback,
        ];

        IReadOnlyList<string> lines = ReportHeader.Lines(ModelWithDisclosures([.. sentences, "other text"]), text => "X" + text);

        Assert.Equal([.. sentences, "Xother text"], [.. lines.Skip(lines.Count - sentences.Length - 1)]);
    }

    [Fact]
    public void A_long_task_and_a_long_gpu_name_are_cut_at_the_name_limit()
    {
        string task = new('t', 300);
        string gpu = new('g', 300);

        string text = Render(RunOf(Tier.Local, [Step(task: task)], edit: document => document with { Environment = document.Environment with { GpuName = gpu } }));

        Assert.Equal(new string('t', 128) + "...", RowOfTask(text, "ttt")[TaskColumn]);
        Assert.Contains("; GPU " + new string('g', 128) + "...; driver", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Latency_has_no_decimal_and_tokens_have_one_when_the_median_is_between_integers()
    {
        StepReading[] steps =
        [
            Step(repeat: 1, latency: Metric.Measured(1000.6, "ms"), inputTokens: Metric.Measured(120.0, "tokens")),
            Step(repeat: 2, latency: Metric.Measured(1000.6, "ms"), inputTokens: Metric.Measured(121.0, "tokens")),
        ];

        string[] row = RowOfTask(Render(RunOf(Tier.Local, steps)));

        Assert.Equal("1001 [1001 to 1001] n=2", row[LatencyColumn]);
        Assert.Equal("120.5 [120 to 121] n=2", row[InputTokensColumn]);
    }

    [Fact]
    public void An_empty_fact_prints_NA()
    {
        string text = Render(RunOf(Tier.Local, NetSteps(Tier.Local, 1.0), edit: document => document with { Environment = document.Environment with { GpuName = string.Empty } }));

        Assert.Contains("; GPU N/A; driver", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_without_rows_gets_a_no_completed_steps_row_and_the_not_run_row_is_unchanged()
    {
        static RunDocument Stopped(RunDocument document) => document with { Run = document.Run with { Status = RunStatus.Partial, StopReason = "budget-reached" } };
        SelectedRun local = RunOf(Tier.Local, [], tasks: [TaskName], edit: Stopped);
        SelectedRun nebiusBaseline = RunOf(Tier.Nebius, [], RunVariant.Baseline, tasks: [TaskName], edit: Stopped);

        string text = Render(local, nebiusBaseline);

        Assert.Equal(["local", "cloudgpu", "nvidia", "nebius", "nebius baseline"], TierCells(text));
        string[] rows = DataRows(text);
        Assert.Equal(NoCompletedStepsRow("local", "partial"), rows[0]);
        Assert.Equal(NotRunRow("cloudgpu"), rows[1]);
        Assert.Equal(NotRunRow("nvidia"), rows[2]);
        Assert.Equal(NotRunRow("nebius"), rows[3]);
        Assert.Equal(NoCompletedStepsRow("nebius baseline", "partial"), rows[4]);
        Assert.All(TableLines(text), row => Assert.Equal(ColumnCount, CellsOf(row).Length));
        Assert.Contains(BulletLines(text), line => line.StartsWith("- local: file ", StringComparison.Ordinal) && line.Contains("; status partial;", StringComparison.Ordinal));
        Assert.Equal("no completed steps", ReportSentences.NoCompletedSteps);

        // An empty primary file is judged by its own variant (the baseline file of its tier has rows) and ranks as a primary file.
        string primaryEmpty = Render(local, RunOf(Tier.Local, NetSteps(Tier.Local, 5.0), RunVariant.Baseline));
        Assert.Equal(["local", "local baseline", "cloudgpu", "nvidia", "nebius"], TierCells(primaryEmpty));
        Assert.Equal(NoCompletedStepsRow("local", "partial"), DataRows(primaryEmpty)[0]);
        Assert.Equal("5.0 n=1", CellsOf(DataRows(primaryEmpty)[1])[NetColumn]);

        // An empty baseline file is judged by its own variant (the primary file has rows) and ranks as a baseline file, after the rows of its primary file.
        SelectedRun baselineEmpty = RunOf(Tier.Local, [], RunVariant.Baseline, tasks: [TaskName], edit: Stopped);
        string baselineOnly = Render(RunOf(Tier.Local, NetSteps(Tier.Local, 5.0)), baselineEmpty);
        Assert.Equal(["local", "local baseline", "cloudgpu", "nvidia", "nebius"], TierCells(baselineOnly));
        Assert.Equal("5.0 n=1", CellsOf(DataRows(baselineOnly)[0])[NetColumn]);
        Assert.Equal(NoCompletedStepsRow("local baseline", "partial"), DataRows(baselineOnly)[1]);
    }

    [Fact]
    public void A_file_with_rows_gets_no_no_completed_steps_row()
    {
        Assert.DoesNotContain("no completed steps", Render(RunOf(Tier.Local, NetSteps(Tier.Local, 1.0))), StringComparison.Ordinal);
    }

    // ---- the cost format (AJ-083 fix round: criterion 2 amended) ----

    [Theory]
    [InlineData(0.0, "0.0000")]
    [InlineData(0.00004, "0.000040")]
    [InlineData(0.000019, "0.000019")]
    [InlineData(0.0000058, "0.0000058")]
    [InlineData(0.00009996, "0.0001")]
    [InlineData(0.0001, "0.0001")]
    [InlineData(0.0012, "0.0012")]
    [InlineData(0.25, "0.2500")]
    [InlineData(-0.000019, "-0.000019")]
    [InlineData(9.9996e-6, "0.000010")]
    [InlineData(9.99999e-7, "0.0000010")]
    [InlineData(9.9996e-16, "0.0000000000000010")]
    public void Cost_keeps_four_decimals_from_a_ten_thousandth_and_two_significant_digits_below(double value, string expected) =>
        Assert.Equal(expected, InvariantFormat.Cost(value));

    [Fact]
    public void Cost_below_a_ten_thousandth_never_uses_an_exponent_and_a_range_applies_the_rule_to_each_bound()
    {
        Assert.Equal("0.0000000012", InvariantFormat.Cost(0.0000000012));
        Assert.DoesNotContain("E", InvariantFormat.Cost(1.2e-12), StringComparison.Ordinal);
        Assert.Equal(
            "0.000019 EUR [0.000012 to 0.0006] n=3 estimated",
            InvariantFormat.Cell(Valued(0.000019, 0.000012, 0.00058, 3, 3, MetricLabel.Estimated, "EUR"), CellKind.Cost, Escaped));
    }

    [Fact]
    public void No_cost_cell_of_the_committed_local_or_nebius_fixture_prints_a_zero()
    {
        foreach (Tier tier in new[] { Tier.Local, Tier.Nebius })
        {
            string text = Render(Wrap(RunDocumentFixtures.Committed(tier)));
            string[] costs = [.. DataRows(text).Select(CellsOf).Where(cells => cells[TaskColumn] != ReportSentences.NotRun).Select(cells => cells[CostColumn])];

            Assert.NotEmpty(costs);
            Assert.All(costs, cost => Assert.DoesNotMatch(@"^0\.0000 ", cost));
            Assert.All(costs, cost => Assert.Matches(@"^0\.0000\d+ (EUR|USD) n=1 estimated$", cost));
        }
    }

    [Fact]
    public void The_no_completed_steps_row_shows_the_run_flags_of_its_file_like_every_other_row()
    {
        SelectedRun flagged = RunOf(
            Tier.Local,
            [],
            tasks: [TaskName],
            edit: document => document with { Run = document.Run with { Status = RunStatus.Partial, StopReason = "budget-reached", Flags = [RunFlagNames.PartialOffload] } });
        SelectedRun completed = RunOf(Tier.Nebius, [], tasks: [TaskName]);

        string text = Render(flagged, completed);

        string[] rows = DataRows(text);
        Assert.Equal(NoCompletedStepsRow("local", "partial-offload, partial"), rows[0]);
        Assert.Equal(NoCompletedStepsRow("nebius", string.Empty), rows[3]);
        Assert.Equal("partial-offload, partial", CellsOf(rows[0])[FlagsColumn]);
    }

    [Theory]
    [InlineData(9e-15)]
    [InlineData(1e-15)]
    [InlineData(1e-20)]
    [InlineData(1e-300)]
    [InlineData(double.Epsilon)]
    public void Cost_of_any_positive_double_prints_two_significant_digits_in_fixed_notation_and_never_throws(double value)
    {
        string text = InvariantFormat.Cost(value);

        Assert.DoesNotContain("E", text, StringComparison.Ordinal);
        Assert.Matches(@"^0\.0+[1-9][0-9]$", text);
        Assert.Equal(text, InvariantFormat.Cost(-value).TrimStart('-'));
    }

    [Fact]
    public void A_cost_below_ten_to_the_minus_fourteen_that_the_validator_accepts_renders_in_the_summary()
    {
        foreach (double value in new[] { 9e-15, 1e-15, 1e-20, 1e-14 })
        {
            string[] row = RowOfTask(Render(RunOf(Tier.Local, [Step(cost: Metric.Estimated(value, "EUR"))])));

            Assert.Matches(@"^0\.0+[1-9][0-9] EUR n=1 estimated$", row[CostColumn]);
        }

        Assert.Equal("0.000000000000010 EUR n=1 estimated", RowOfTask(Render(RunOf(Tier.Local, [Step(cost: Metric.Estimated(1e-14, "EUR"))])))[CostColumn]);
        Assert.True(InvariantFormat.Cost(double.Epsilon).Length < 400);
    }

    private static string ModelsDifferLineOf(params SelectedRun[] runs) =>
        Assert.Single(DisclosureLines(Render(runs)), candidate => candidate.StartsWith(ReportSentences.ModelsDifferPrefix, StringComparison.Ordinal));

    [Fact]
    public void A_model_id_with_forged_separators_cannot_hide_a_real_models_differ_entry()
    {
        string forged = "a, b, c, " + new string('z', 120);
        SelectedRun local = RunOf(Tier.Local, NetSteps(Tier.Local, 1.0), edit: document => WithModels(document, "Q4_K_M", forged));
        SelectedRun nvidia = RunOf(Tier.Nvidia, NetSteps(Tier.Nvidia, 1.0), edit: document => WithModels(document, null, "nv-model"));
        SelectedRun nebius = RunOf(Tier.Nebius, NetSteps(Tier.Nebius, 1.0), edit: document => WithModels(document, "FP8", "nb-model"));

        string line = ModelsDifferLineOf(local, nvidia, nebius);

        Assert.StartsWith(ReportSentences.ModelsDifferPrefix + "local=a, b, c, zzz", line, StringComparison.Ordinal);
        Assert.EndsWith("..." + " \\(Q4\\_K\\_M\\), nvidia=nv-model \\(N/A\\), nebius=nb-model \\(FP8\\)", line, StringComparison.Ordinal);
    }

    [Fact]
    public void A_model_id_that_looks_like_an_entry_stays_inside_its_own_entry()
    {
        SelectedRun local = RunOf(Tier.Local, NetSteps(Tier.Local, 1.0), edit: document => WithModels(document, "Q4_K_M", "x, nvidia=fake (FP16)"));
        SelectedRun nvidia = RunOf(Tier.Nvidia, NetSteps(Tier.Nvidia, 1.0), edit: document => WithModels(document, null, "real-model"));

        string line = ModelsDifferLineOf(local, nvidia);

        Assert.Equal(ReportSentences.ModelsDifferPrefix + "local=x, nvidia=fake \\(FP16\\) \\(Q4\\_K\\_M\\), nvidia=real-model \\(N/A\\)", line);
    }

    [Fact]
    public void The_models_differ_line_prints_NA_for_a_tier_without_models_or_precision_and_joins_duplicates_once_in_order()
    {
        SelectedRun local = RunOf(Tier.Local, NetSteps(Tier.Local, 1.0), edit: document => WithModels(document, null));
        SelectedRun nebius = RunOf(Tier.Nebius, NetSteps(Tier.Nebius, 1.0), edit: document => WithModels(document, "FP8", "m1", "z1", "m1", "b1"));

        Assert.Equal(ReportSentences.ModelsDifferPrefix + "local=N/A \\(N/A\\), nebius=b1+m1+z1 \\(FP8\\)", ModelsDifferLineOf(local, nebius));
    }

    [Fact]
    public void The_models_differ_line_lists_primary_tiers_only()
    {
        SelectedRun local = RunOf(Tier.Local, NetSteps(Tier.Local, 1.0), edit: document => WithModels(document, "Q4_K_M", "m1"));
        SelectedRun localBaseline = RunOf(Tier.Local, NetSteps(Tier.Local, 1.0), RunVariant.Baseline, edit: document => WithModels(document, "FP8", "base-model"));
        SelectedRun nebius = RunOf(Tier.Nebius, NetSteps(Tier.Nebius, 1.0), edit: document => WithModels(document, "FP8", "m2"));

        string line = ModelsDifferLineOf(local, localBaseline, nebius);

        Assert.DoesNotContain("base-model", line, StringComparison.Ordinal);
        Assert.Equal(ReportSentences.ModelsDifferPrefix + "local=m1 \\(Q4\\_K\\_M\\), nebius=m2 \\(FP8\\)", line);
    }

    // ---- the SVG chart with N/A as a text marker (AJ-084) ----

    private const double PlotTopPixels = 102.0;
    private const double PlotHeightPixels = 240.0;
    private const double RowPitchPixels = 430.0;
    private const double FirstRowTopPixels = 62.0;

    private static readonly XNamespace SvgNs = "http://www.w3.org/2000/svg";
    private static readonly string[] SvgTierOrder = ["local", "cloudgpu", "nvidia", "nebius"];
    private static readonly string[] SvgElementNames = ["svg", "text", "line", "g", "rect"];

    [GeneratedRegex("<text[^>]*>N/A</text>", RegexOptions.CultureInvariant)]
    private static partial Regex NaMarkerText();

    private static string RenderSvg(params SelectedRun[] runs) => JoulesSvgRenderer.Render(ReportAggregator.Build(runs));

    private static string RenderSvgUnder(string culture, params SelectedRun[] runs)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);

            return RenderSvg(runs);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private static string? ClassOf(XElement element) => (string?)element.Attribute("class");

    private static XElement[] SvgGroups(XDocument document) => [.. document.Descendants(SvgNs + "g").Where(element => ClassOf(element) == "group")];

    private static XElement[] SlotsOfGroup(XElement group) => [.. group.Elements(SvgNs + "g").Where(element => ClassOf(element) == "slot")];

    private static XElement SlotOfGroup(XElement group, string tier, string variant = "primary") =>
        Assert.Single(SlotsOfGroup(group), slot => (string?)slot.Attribute("data-tier") == tier && (string?)slot.Attribute("data-variant") == variant);

    private static string[] SvgTexts(XElement parent, string cssClass) =>
        [.. parent.Elements(SvgNs + "text").Where(element => ClassOf(element) == cssClass).Select(element => element.Value)];

    private static XElement[] SvgBars(XDocument document) => [.. document.Descendants(SvgNs + "rect")];

    private static XElement SvgAxis(XDocument document)
    {
        Assert.Single(document.Descendants(SvgNs + "line"));

        return Assert.Single(document.Descendants(SvgNs + "line"), element => ClassOf(element) == "axis");
    }

    private static XElement[] SvgAxes(XDocument document) => [.. document.Descendants(SvgNs + "line").Where(element => ClassOf(element) == "axis")];

    private static XElement[] SvgRows(XDocument document) => [.. document.Descendants(SvgNs + "g").Where(element => ClassOf(element) == "row")];

    private static double Pixels(XElement element, string name) =>
        double.Parse((string?)element.Attribute(name) ?? throw new InvalidOperationException($"No {name} attribute."), NumberStyles.Float, CultureInfo.InvariantCulture);

    private static SelectedRun LocalSteps(params (string Task, double Net)[] steps) =>
        RunOf(Tier.Local, [.. steps.Select(step => Step(Tier.Local, task: step.Task, net: Metric.Measured(step.Net, "J"), flags: step.Net < 0.0 ? ReadingFlags.BelowBaseline : ReadingFlags.None))]);

    private static string AttributeDump(XDocument document) =>
        string.Join('\n', document.Descendants().SelectMany(element => element.Attributes().Select(attribute => $"{element.Name.LocalName}.{attribute.Name.LocalName}={attribute.Value}")));

    [Fact]
    public void The_chart_is_well_formed_svg_with_the_svg_namespace_on_the_root()
    {
        string svg = RenderSvg(LocalSteps((TaskName, 12.3)));

        XDocument document = XDocument.Parse(svg);

        Assert.Equal(SvgNs + "svg", document.Root!.Name);
        Assert.StartsWith("<svg xmlns=\"http://www.w3.org/2000/svg\"", svg, StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty((string?)document.Root.Attribute("viewBox")));
        Assert.True(Pixels(document.Root, "width") > 0.0);
        Assert.True(Pixels(document.Root, "height") > 0.0);
        Assert.DoesNotContain("<?xml", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void The_chart_text_is_lf_only_ascii_with_one_final_line_feed()
    {
        string svg = RenderSvg(LocalSteps((TaskName, 12.3)), RunOf(Tier.Nvidia, NetSteps(Tier.Nvidia, 1.0)));

        Assert.DoesNotContain('\r', svg);
        Assert.All(svg, character => Assert.True(character < 128, "The chart holds a character outside ASCII."));
        Assert.EndsWith("</svg>\n", svg, StringComparison.Ordinal);
        Assert.False(svg.EndsWith("\n\n", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_refuses_a_null_model()
    {
        _ = Assert.Throws<ArgumentNullException>(() => JoulesSvgRenderer.Render(null!));
    }

    [Fact]
    public void A_measured_median_is_one_rect_with_its_label()
    {
        string svg = RenderSvg(LocalSteps((TaskName, 12.3)));
        XDocument document = XDocument.Parse(svg);

        XElement group = Assert.Single(SvgGroups(document));
        XElement slot = SlotOfGroup(group, "local");
        XElement bar = Assert.Single(SvgBars(document));

        Assert.Same(slot, bar.Parent);
        Assert.Equal("bar", ClassOf(bar));
        Assert.Equal(["12.3"], SvgTexts(slot, "value"));
        Assert.Empty(SvgTexts(slot, "marker"));
        Assert.Equal(PlotHeightPixels, Pixels(bar, "height"), precision: 2);
    }

    [Fact]
    public void The_value_label_is_the_invariant_joules_text_of_the_median_under_any_culture()
    {
        SelectedRun run = LocalSteps((TaskName, 1234.56));

        foreach (string culture in new[] { "de-DE", "tr-TR", "fr-FR" })
        {
            XDocument document = XDocument.Parse(RenderSvgUnder(culture, run));

            Assert.Equal([InvariantFormat.Joules(1234.56)], SvgTexts(SlotOfGroup(Assert.Single(SvgGroups(document)), "local"), "value"));
        }

        Assert.Equal("1234.6", InvariantFormat.Joules(1234.56));
    }

    [Fact]
    public void The_bar_of_a_row_is_the_median_of_its_repeats_and_there_is_one_bar_per_row()
    {
        SelectedRun run = RunOf(Tier.Local, NetSteps(Tier.Local, 10.0, 12.0, 100.0));

        XDocument document = XDocument.Parse(RenderSvg(run));

        XElement slot = SlotOfGroup(Assert.Single(SvgGroups(document)), "local");
        Assert.Single(SvgBars(document));
        Assert.Equal(["12.0"], SvgTexts(slot, "value"));
    }

    [Fact]
    public void Bars_share_one_scale_the_largest_value_fills_the_plot_height()
    {
        XDocument document = XDocument.Parse(RenderSvg(LocalSteps(("alpha", 10.0), ("beta", 20.0))));

        XElement[] groups = SvgGroups(document);
        XElement small = Assert.Single(SlotOfGroup(groups[0], "local").Elements(SvgNs + "rect"));
        XElement large = Assert.Single(SlotOfGroup(groups[1], "local").Elements(SvgNs + "rect"));
        XElement[] axes = SvgAxes(document);

        // Two rows, one axis each, one scale for the chart: 20 J fills the plot, 10 J fills half of it, in rows one pitch apart.
        Assert.Equal(2, axes.Length);
        Assert.Equal(PlotHeightPixels, Pixels(large, "height"), precision: 2);
        Assert.Equal(PlotHeightPixels / 2.0, Pixels(small, "height"), precision: 2);
        Assert.Equal(PlotTopPixels + PlotHeightPixels, Pixels(axes[0], "y1"), precision: 2);
        Assert.Equal(PlotTopPixels + PlotHeightPixels + RowPitchPixels, Pixels(axes[1], "y1"), precision: 2);
        Assert.Equal(Pixels(axes[0], "y1"), Pixels(small, "y") + Pixels(small, "height"), precision: 2);
        Assert.Equal(Pixels(axes[1], "y1"), Pixels(large, "y") + Pixels(large, "height"), precision: 2);
    }

    [Fact]
    public void Negative_bar_below_axis()
    {
        XDocument document = XDocument.Parse(RenderSvg(LocalSteps(("alpha", 10.0), ("beta", -2.0))));

        XElement[] groups = SvgGroups(document);
        XElement positive = Assert.Single(SlotOfGroup(groups[0], "local").Elements(SvgNs + "rect"));
        XElement negativeSlot = SlotOfGroup(groups[1], "local");
        XElement negative = Assert.Single(negativeSlot.Elements(SvgNs + "rect"));
        XElement[] axes = SvgAxes(document);

        // scale = 240 / (10 + 2) = 20 px per joule; in every row the axis is 10 * 20 = 200 px below the top of the plot, and the rows are one pitch apart.
        Assert.Equal(2, axes.Length);
        double axisY = PlotTopPixels + 200.0;
        double secondAxisY = axisY + RowPitchPixels;
        Assert.Equal(axisY, Pixels(axes[0], "y1"), precision: 2);
        Assert.Equal(secondAxisY, Pixels(axes[1], "y1"), precision: 2);
        Assert.All(axes, axis => Assert.Equal(Pixels(axis, "y1"), Pixels(axis, "y2")));
        Assert.Equal(PlotTopPixels, Pixels(positive, "y"), precision: 2);
        Assert.Equal(200.0, Pixels(positive, "height"), precision: 2);
        Assert.Equal(secondAxisY, Pixels(negative, "y"), precision: 2);
        Assert.Equal(40.0, Pixels(negative, "height"), precision: 2);
        Assert.Equal(["-2.0"], SvgTexts(negativeSlot, "value"));
        XElement label = Assert.Single(negativeSlot.Elements(SvgNs + "text"), element => ClassOf(element) == "value");
        Assert.True(Pixels(label, "y") > Pixels(negative, "y") + Pixels(negative, "height"));
    }

    [Fact]
    public void A_negative_value_flagged_below_baseline_is_drawn_below_the_axis_and_the_flag_is_not_drawn()
    {
        SelectedRun run = RunOf(Tier.Local, [Step(Tier.Local, net: Metric.Measured(-2.0, "J"), flags: ReadingFlags.BelowBaseline)]);

        string svg = RenderSvg(run);
        XDocument document = XDocument.Parse(svg);

        XElement bar = Assert.Single(SvgBars(document));
        XElement axis = SvgAxis(document);
        Assert.Equal(Pixels(axis, "y1"), Pixels(bar, "y"), precision: 2);
        Assert.Equal(PlotTopPixels, Pixels(axis, "y1"), precision: 2);
        Assert.Equal(PlotHeightPixels, Pixels(bar, "height"), precision: 2);
        Assert.DoesNotContain("below-baseline", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void A_measured_zero_is_a_rect_of_height_zero_with_its_label_and_not_a_marker()
    {
        XDocument document = XDocument.Parse(RenderSvg(LocalSteps(("alpha", 0.0), ("beta", 10.0))));

        XElement zeroSlot = SlotOfGroup(SvgGroups(document)[0], "local");
        XElement bar = Assert.Single(zeroSlot.Elements(SvgNs + "rect"));

        Assert.Equal(0.0, Pixels(bar, "height"));
        Assert.Equal(Pixels(SvgAxes(document)[0], "y1"), Pixels(bar, "y"), precision: 2);
        Assert.Equal(["0.0"], SvgTexts(zeroSlot, "value"));
        Assert.Empty(SvgTexts(zeroSlot, "marker"));
    }

    [Fact]
    public void Only_zero_values_draw_height_zero_bars_on_an_axis_at_the_bottom_of_the_plot()
    {
        XDocument document = XDocument.Parse(RenderSvg(LocalSteps((TaskName, 0.0))));

        XElement bar = Assert.Single(SvgBars(document));

        Assert.Equal(0.0, Pixels(bar, "height"));
        Assert.Equal(PlotTopPixels + PlotHeightPixels, Pixels(SvgAxis(document), "y1"), precision: 2);
        Assert.Equal(Pixels(SvgAxis(document), "y1"), Pixels(bar, "y"), precision: 2);
        Assert.Equal(["0.0"], SvgTexts(SlotOfGroup(Assert.Single(SvgGroups(document)), "local"), "value"));
    }

    [Fact]
    public void A_negative_zero_reading_prints_without_a_sign()
    {
        XDocument document = XDocument.Parse(RenderSvg(LocalSteps((TaskName, -0.0))));

        Assert.Equal(["0.0"], SvgTexts(SlotOfGroup(Assert.Single(SvgGroups(document)), "local"), "value"));
        Assert.Equal(0.0, Pixels(Assert.Single(SvgBars(document)), "height"));
    }

    [Fact]
    public void NA_step_has_no_rect()
    {
        string svg = RenderSvg(RunOf(Tier.Nvidia, NetSteps(Tier.Nvidia, 1.0)));
        XDocument document = XDocument.Parse(svg);

        XElement slot = SlotOfGroup(Assert.Single(SvgGroups(document)), "nvidia");

        Assert.Equal(["N/A"], SvgTexts(slot, "marker"));
        Assert.Empty(SvgBars(document));
        Assert.Empty(SvgTexts(slot, "value"));
        Assert.Equal(1, NaMarkerText().Count(svg));
    }

    [Fact]
    public void The_hosted_marker_appears_once_per_row_never_once_per_repeat()
    {
        string svg = RenderSvg(RunOf(Tier.Nvidia, NetSteps(Tier.Nvidia, 1.0, 2.0, 3.0)));

        Assert.Equal(1, NaMarkerText().Count(svg));
        Assert.Empty(XDocument.Parse(svg).Descendants(SvgNs + "rect"));
    }

    [Fact]
    public void Not_reported_marker_text()
    {
        SelectedRun run = RunOf(Tier.Local, [Step(Tier.Local, net: Metric.NotReported("sensor-missing"))]);

        string svg = RenderSvg(run);
        XDocument document = XDocument.Parse(svg);
        XElement slot = SlotOfGroup(Assert.Single(SvgGroups(document)), "local");

        Assert.Equal(["not-reported"], SvgTexts(slot, "marker"));
        Assert.Empty(SvgBars(document));
        Assert.Equal(0, NaMarkerText().Count(svg));
        Assert.DoesNotContain("sensor-missing", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cell_without_a_value_follows_its_label_na_when_any_repeat_is_na()
    {
        SelectedRun run = RunOf(
            Tier.Local,
            [
                Step(Tier.Local, repeat: 1, net: Metric.NotReported("sensor-missing")),
                Step(Tier.Local, repeat: 2, net: Metric.NotApplicable("no-counter")),
            ]);

        XDocument document = XDocument.Parse(RenderSvg(run));

        Assert.Equal(["N/A"], SvgTexts(SlotOfGroup(Assert.Single(SvgGroups(document)), "local"), "marker"));
        Assert.Empty(SvgBars(document));
    }

    [Fact]
    public void A_hosted_marker_and_a_measured_bar_stand_in_the_same_group_without_a_rect_for_the_marker()
    {
        XDocument document = XDocument.Parse(RenderSvg(LocalSteps((TaskName, 40.0)), RunOf(Tier.Nvidia, NetSteps(Tier.Nvidia, 1.0))));

        XElement group = Assert.Single(SvgGroups(document));

        Assert.Single(SlotOfGroup(group, "local").Elements(SvgNs + "rect"));
        Assert.Empty(SlotOfGroup(group, "nvidia").Elements(SvgNs + "rect"));
        Assert.Equal(["N/A"], SvgTexts(SlotOfGroup(group, "nvidia"), "marker"));
        Assert.Single(SvgBars(document));
    }

    [Fact]
    public void Every_value_label_has_its_rect_and_every_marker_has_none()
    {
        XDocument document = XDocument.Parse(RenderSvg(LocalSteps(("alpha", 5.0), ("beta", -1.0)), RunOf(Tier.Nvidia, NetSteps(Tier.Nvidia, 1.0))));

        foreach (XElement slot in SvgGroups(document).SelectMany(SlotsOfGroup))
        {
            int rects = slot.Elements(SvgNs + "rect").Count();
            int values = SvgTexts(slot, "value").Length;
            int markers = SvgTexts(slot, "marker").Length;

            Assert.Equal(rects, values);
            Assert.Equal(1, rects + markers);
            Assert.Single(SvgTexts(slot, "slot-label"));
        }
    }

    [Fact]
    public void Tier_without_a_run_file_is_labelled_not_run()
    {
        SelectedRun[] runs = [RunOf(Tier.Local, NetSteps(Tier.Local, 10.0)), RunOf(Tier.Nvidia, NetSteps(Tier.Nvidia, 1.0)), RunOf(Tier.Nebius, NetSteps(Tier.Nebius, 1.0))];

        XDocument document = XDocument.Parse(RenderSvg(runs));

        XElement slot = SlotOfGroup(Assert.Single(SvgGroups(document)), "cloudgpu");
        Assert.Equal(["not run"], SvgTexts(slot, "marker"));
        Assert.Empty(slot.Elements(SvgNs + "rect"));
        Assert.Equal(["cloudgpu"], SvgTexts(slot, "slot-label"));
    }

    [Fact]
    public void Empty_model_draws_four_not_run_slots()
    {
        string svg = JoulesSvgRenderer.Render(ReportAggregator.Build([]));
        XDocument document = XDocument.Parse(svg);

        XElement group = Assert.Single(SvgGroups(document));
        XElement[] slots = SlotsOfGroup(group);

        Assert.Equal(SvgTierOrder, slots.Select(slot => (string?)slot.Attribute("data-tier")));
        Assert.All(slots, slot => Assert.Equal(["not run"], SvgTexts(slot, "marker")));
        Assert.Empty(SvgBars(document));
        Assert.Equal([string.Empty], SvgTexts(group, "group-label"));
        Assert.Equal([string.Empty], SvgTexts(Assert.Single(SvgRows(document)), "row-label"));
        Assert.Equal(0, NaMarkerText().Count(svg));
        Assert.Equal(ReportSentences.GpuBoardOnly, Assert.Single(document.Descendants(SvgNs + "text"), element => ClassOf(element) == "scope").Value);
        Assert.Equal(PlotTopPixels + PlotHeightPixels, Pixels(SvgAxis(document), "y1"), precision: 2);
        Assert.All(slots.Select(slot => Assert.Single(slot.Elements(SvgNs + "text"), element => ClassOf(element) == "marker")), marker => Assert.Equal(PlotTopPixels + PlotHeightPixels - 8.0, Pixels(marker, "y"), precision: 2));
    }

    [Fact]
    public void A_file_without_rows_prints_no_completed_steps_in_every_group_and_a_tier_with_rows_keeps_its_bar()
    {
        SelectedRun empty = RunOf(Tier.Local, [], tasks: [TaskName]);
        SelectedRun cloud = RunOf(Tier.CloudGpu, NetSteps(Tier.CloudGpu, 30.0));

        XDocument document = XDocument.Parse(RenderSvg(empty, cloud));

        XElement group = Assert.Single(SvgGroups(document));
        Assert.Equal([ReportSentences.NoCompletedSteps], SvgTexts(SlotOfGroup(group, "local"), "marker"));
        Assert.Equal(["not run"], SvgTexts(SlotOfGroup(group, "nvidia"), "marker"));
        Assert.Single(SlotOfGroup(group, "cloudgpu").Elements(SvgNs + "rect"));
        Assert.DoesNotContain(SvgBars(document), bar => bar.Parent == SlotOfGroup(group, "local"));
    }

    [Fact]
    public void A_tier_with_rows_but_none_for_a_group_prints_na_for_that_group_and_nothing_else()
    {
        SelectedRun local = LocalSteps(("alpha", 10.0), ("beta", 20.0));
        SelectedRun cloud = RunOf(Tier.CloudGpu, [Step(Tier.CloudGpu, task: "alpha", net: Metric.Measured(5.0, "J"))]);

        XDocument document = XDocument.Parse(RenderSvg(local, cloud));

        XElement[] groups = SvgGroups(document);
        Assert.Single(SlotOfGroup(groups[0], "cloudgpu").Elements(SvgNs + "rect"));
        Assert.Equal(["N/A"], SvgTexts(SlotOfGroup(groups[1], "cloudgpu"), "marker"));
        Assert.Empty(SlotOfGroup(groups[1], "cloudgpu").Elements(SvgNs + "rect"));
    }

    [Fact]
    public void Slots_in_tier_order_with_baseline_after_its_tier()
    {
        SelectedRun local = RunOf(Tier.Local, NetSteps(Tier.Local, 10.0));
        SelectedRun localBaseline = RunOf(Tier.Local, NetSteps(Tier.Local, 7.0), RunVariant.Baseline);
        SelectedRun nebius = RunOf(Tier.Nebius, NetSteps(Tier.Nebius, 1.0));
        SelectedRun nebiusBaseline = RunOf(Tier.Nebius, NetSteps(Tier.Nebius, 1.0), RunVariant.Baseline);

        XDocument document = XDocument.Parse(RenderSvg(nebiusBaseline, nebius, localBaseline, local));

        XElement group = Assert.Single(SvgGroups(document));
        XElement[] slots = SlotsOfGroup(group);
        Assert.Equal(
            [("local", "primary"), ("local", "baseline"), ("cloudgpu", "primary"), ("nvidia", "primary"), ("nebius", "primary"), ("nebius", "baseline")],
            slots.Select(slot => ((string)slot.Attribute("data-tier")!, (string)slot.Attribute("data-variant")!)));
        Assert.Equal(
            ["local", "local baseline", "cloudgpu", "nvidia", "nebius", "nebius baseline"],
            slots.Select(slot => Assert.Single(SvgTexts(slot, "slot-label"))));
        Assert.Equal(["7.0"], SvgTexts(SlotOfGroup(group, "local", "baseline"), "value"));
        Assert.Equal(["10.0"], SvgTexts(SlotOfGroup(group, "local"), "value"));
    }

    [Fact]
    public void The_four_tiers_stand_in_the_fixed_order_in_every_group()
    {
        SelectedRun[] runs = [.. new[] { Tier.Nebius, Tier.Nvidia, Tier.CloudGpu, Tier.Local }.Select(tier => RunOf(tier, NetSteps(tier, 10.0)))];

        XDocument document = XDocument.Parse(RenderSvg(runs));

        foreach (XElement group in SvgGroups(document))
        {
            Assert.Equal(SvgTierOrder, SlotsOfGroup(group).Select(slot => (string?)slot.Attribute("data-tier")));
        }
    }

    [Fact]
    public void A_partial_file_marks_its_slot_label_and_no_other_flag_is_drawn()
    {
        SelectedRun partial = Wrap(RunDocumentFixtures.Partial(Tier.Local));
        SelectedRun flagged = RunOf(Tier.CloudGpu, [Step(Tier.CloudGpu, flags: ReadingFlags.SharedWindow | ReadingFlags.LowResolution)]);

        string svg = RenderSvg(partial, flagged);
        XElement group = Assert.Single(SvgGroups(XDocument.Parse(svg)));

        Assert.Equal(["local (partial)"], SvgTexts(SlotOfGroup(group, "local"), "slot-label"));
        Assert.Equal(["cloudgpu"], SvgTexts(SlotOfGroup(group, "cloudgpu"), "slot-label"));
        Assert.DoesNotContain("shared-window", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("low-resolution", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("sampling-fallback", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void The_chart_carries_the_gpu_board_sentence_once_as_a_text_element()
    {
        string svg = RenderSvg(LocalSteps((TaskName, 12.3)));

        XElement scope = Assert.Single(XDocument.Parse(svg).Descendants(SvgNs + "text"), element => ClassOf(element) == "scope");

        Assert.Equal(ReportSentences.GpuBoardOnly, scope.Value);
        Assert.Equal("Energy is GPU-board energy only.", scope.Value);
        Assert.Equal(1, svg.Split(ReportSentences.GpuBoardOnly).Length - 1);
    }

    [Fact]
    public void The_chart_names_no_figure_across_tiers_and_makes_no_comparison()
    {
        string svg = RenderSvg(LocalSteps(("alpha", 10.0), ("beta", -2.0)), RunOf(Tier.Nvidia, NetSteps(Tier.Nvidia, 1.0)), RunOf(Tier.Nebius, NetSteps(Tier.Nebius, 1.0)));

        foreach (string word in CrossTierWords)
        {
            Assert.DoesNotContain(word, svg, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotMatch(@"\d\s*x\b|x-fold|%", XDocument.Parse(svg).Root!.Value);
        Assert.DoesNotContain("cost", svg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("quality", svg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_chart_uses_only_the_elements_of_a_plain_drawing_and_no_script_style_image_or_link()
    {
        string svg = RenderSvg(LocalSteps(("alpha", 10.0)), RunOf(Tier.Nvidia, NetSteps(Tier.Nvidia, 1.0)));
        XDocument document = XDocument.Parse(svg);

        Assert.All(document.Descendants(), element => Assert.Contains(element.Name.LocalName, SvgElementNames));
        Assert.All(document.Descendants(), element => Assert.Equal(SvgNs, element.Name.Namespace));
        Assert.DoesNotContain("href", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", svg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<style", svg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<image", svg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("url(", svg, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, svg.Split("http").Length - 1);
        Assert.All(document.Descendants(SvgNs + "text"), element => Assert.False(element.HasElements));
        Assert.All(document.Descendants(SvgNs + "rect"), element => Assert.True(element.IsEmpty));
        Assert.All(document.Descendants(SvgNs + "line"), element => Assert.True(element.IsEmpty));
    }

    [Fact]
    public void Two_renders_identical()
    {
        SelectedRun[] runs = [LocalSteps(("alpha", 10.0), ("beta", -2.0)), RunOf(Tier.Nvidia, NetSteps(Tier.Nvidia, 1.0)), RunOf(Tier.Nebius, NetSteps(Tier.Nebius, 3.0), RunVariant.Baseline)];

        string first = RenderSvg(runs);

        Assert.Equal(first, RenderSvg(runs));
        Assert.Equal(first, RenderSvgUnder("de-DE", runs));
        Assert.Equal(first, RenderSvgUnder("tr-TR", runs));
        Assert.Equal(first, JoulesSvgRenderer.Render(ReportAggregator.Build(runs)));
    }

    [Fact]
    public void The_order_of_the_input_files_does_not_change_the_chart()
    {
        SelectedRun local = RunOf(Tier.Local, NetSteps(Tier.Local, 10.0));
        SelectedRun nvidia = RunOf(Tier.Nvidia, NetSteps(Tier.Nvidia, 1.0));

        Assert.Equal(RenderSvg(local, nvidia), RenderSvg(nvidia, local));
    }

    [Fact]
    public void The_chart_of_the_fixture_documents_renders_and_every_na_is_a_text_marker()
    {
        foreach (RunDocument document in EveryReportFixture())
        {
            string svg = RenderSvg(Wrap(document));
            XDocument parsed = XDocument.Parse(svg);

            Assert.All(SvgBars(parsed), bar => Assert.Single(bar.Parent!.Elements(SvgNs + "text"), element => ClassOf(element) == "value"));
            Assert.Equal(parsed.Descendants(SvgNs + "rect").Count(), parsed.Descendants(SvgNs + "text").Count(element => ClassOf(element) == "value"));
        }

        Assert.True(NaMarkerText().Count(RenderSvg(Wrap(RunDocumentFixtures.HostedNoUsage()))) >= 1);
    }

    // The payloads are passed by index: xUnit cannot tell two lone surrogates apart as theory data.
    private static readonly string[] HostilePayloads =
    [
        "a&b",
        "a<b",
        "a>b",
        "a\"b",
        "a'b",
        "&amp;",
        "&#x0;",
        "&lt;script&gt;",
        "<script>alert(1)</script>",
        "\"/><script>alert(1)</script>",
        "</text><rect class=\"bar\"/>",
        "' onload='x",
        "]]>",
        "<![CDATA[x]]>",
        "<!-- x -->",
        "<?pi x?>",
        "<!DOCTYPE x [<!ENTITY e \"y\">]>&e;",
        "<g class=\"slot\"/>",
        "xmlns=\"x\"",
        "javascript:alert(1)",
        "a\tb\nc\rd",
        "a" + (char)0x01 + "b",
        "a" + (char)0x1F + "b",
        "a" + (char)0xD800 + "b",
        "a" + (char)0xDC00 + "b",
        "a" + (char)0xFFFE + "b",
        "a" + (char)0xFFFF + "b",
        "a" + (char)0x202E + "b",
        "a" + (char)0x2028 + "b",
        "a" + (char)0x85 + "b",
        "a" + char.ConvertFromUtf32(0xE0041) + "b",
        "a" + char.ConvertFromUtf32(0x1F600) + "b",
        "a" + (char)0xE9 + "b",
        "nvapi-" + new string('K', 30),
        new string('x', 400),
    ];

    public static TheoryData<int> InjectionPayloads()
    {
        var indexes = new TheoryData<int>();
        for (int index = 0; index < HostilePayloads.Length; index++)
        {
            indexes.Add(index);
        }

        return indexes;
    }

    [Theory]
    [MemberData(nameof(InjectionPayloads))]
    public void Hostile_ids_parse(int index)
    {
        string payload = HostilePayloads[index];
        SelectedRun hostile = RunOf(
            Tier.Local,
            [Step(Tier.Local, task: payload, agent: payload, modelId: payload, net: Metric.Measured(5.0, "J"))],
            edit: document => document with { Environment = document.Environment with { Models = [new EnvironmentModel(payload, payload, Baseline: false)] } });
        SelectedRun plain = RunOf(Tier.Local, [Step(Tier.Local, task: "task", agent: "agent", net: Metric.Measured(5.0, "J"))]);

        string svg = RenderSvg(hostile, RunOf(Tier.Nvidia, [Step(Tier.Nvidia, task: payload, agent: payload, modelId: payload)]));
        XDocument document = XDocument.Parse(svg);

        // The names reach the chart as text nodes, cleaned and then escaped by the writer, so the parsed text is the cleaned text.
        XElement row = Assert.Single(SvgRows(document));
        XElement group = Assert.Single(SvgGroups(document));
        string cleaned = ReportText.Clean(payload, ReportText.NameMaxLength);
        Assert.Equal(cleaned, Assert.Single(SvgTexts(row, "row-label")));
        Assert.Equal(cleaned, Assert.Single(SvgTexts(group, "group-label")));

        // Nothing that came from the document is an element or an attribute: the structure and every attribute equal those of a plain render.
        Assert.Single(SvgBars(document));
        Assert.All(document.Descendants(), element => Assert.Contains(element.Name.LocalName, SvgElementNames));
        string plainSvg = RenderSvg(plain, RunOf(Tier.Nvidia, [Step(Tier.Nvidia, task: "task", agent: "agent")]));
        Assert.Equal(AttributeDump(XDocument.Parse(plainSvg)), AttributeDump(document));
        Assert.Equal(XDocument.Parse(plainSvg).Descendants().Count(), document.Descendants().Count());
        Assert.DoesNotContain("nvapi-" + new string('K', 30), svg, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', svg);
        Assert.All(svg, character => Assert.True(character < 128));
    }

    [Fact]
    public void Characters_outside_ascii_are_written_as_character_references_and_round_trip()
    {
        string name = "caf" + (char)0xE9 + char.ConvertFromUtf32(0x1F600);
        SelectedRun run = RunOf(Tier.Local, [Step(Tier.Local, task: name, agent: name, net: Metric.Measured(5.0, "J"))]);

        string svg = RenderSvg(run);

        Assert.All(svg, character => Assert.True(character < 128));
        XDocument document = XDocument.Parse(svg);
        Assert.Equal(name, Assert.Single(SvgTexts(Assert.Single(SvgRows(document)), "row-label")));
        Assert.Equal(name, Assert.Single(SvgTexts(Assert.Single(SvgGroups(document)), "group-label")));
    }

    [Fact]
    public void The_chart_does_not_grow_a_rect_for_a_long_task_name_and_cuts_the_name()
    {
        string name = new('n', 500);

        XDocument document = XDocument.Parse(RenderSvg(RunOf(Tier.Local, [Step(Tier.Local, task: name, agent: "Planner")])));

        string label = Assert.Single(SvgTexts(Assert.Single(SvgRows(document)), "row-label"));
        Assert.Equal(ReportText.Clean(name, ReportText.NameMaxLength), label);
        Assert.EndsWith("...", label, StringComparison.Ordinal);
        Assert.Equal(["Planner"], SvgTexts(Assert.Single(SvgGroups(document)), "group-label"));
    }

    [Fact]
    public void The_drawing_width_grows_with_the_groups_and_the_slots_stay_inside_it()
    {
        SelectedRun many = RunOf(Tier.Local, [.. Enumerable.Range(1, 12).Select(index => Step(Tier.Local, task: $"task-{index:D2}", net: Metric.Measured(index, "J")))]);

        XDocument document = XDocument.Parse(RenderSvg(many, RunOf(Tier.Local, NetSteps(Tier.Local, 1.0), RunVariant.Baseline)));

        double width = Pixels(document.Root!, "width");
        Assert.Equal(13, SvgGroups(document).Length);
        Assert.Equal(13, SvgRows(document).Length);
        Assert.Equal(FirstRowTopPixels + (13 * RowPitchPixels), Pixels(document.Root!, "height"), precision: 2);
        Assert.Equal($"0 0 {width.ToString("0.##", CultureInfo.InvariantCulture)} {Pixels(document.Root!, "height").ToString("0.##", CultureInfo.InvariantCulture)}", (string?)document.Root!.Attribute("viewBox"));
        Assert.All(SvgBars(document), bar => Assert.InRange(Pixels(bar, "x") + Pixels(bar, "width"), 0.0, width));
        Assert.All(SvgBars(document), bar => Assert.True(Pixels(bar, "x") >= 0.0));
    }

    // ---- the SVG chart: one row per task (AJ-084 fix round) ----

    private const string ChartTitle = "Measured net energy per step in joules (J), median of the repeats of each bar";

    private static SelectedRun LocalAgents(string task, int count, Func<int, double>? net = null) =>
        RunOf(Tier.Local, [.. Enumerable.Range(0, count).Select(index => Step(Tier.Local, task: task, agent: $"a{index:D4}", net: Metric.Measured(net?.Invoke(index) ?? 1.0, "J")))]);

    [Fact]
    public void The_chart_has_one_row_per_task_in_the_order_of_the_rows_of_the_model_each_with_its_label_and_its_axis()
    {
        SelectedRun run = RunOf(
            Tier.Local,
            [
                Step(Tier.Local, task: "gamma", agent: "Planner"),
                Step(Tier.Local, task: "alpha", agent: "Writer"),
                Step(Tier.Local, task: "alpha", agent: "Planner"),
                Step(Tier.Local, task: "beta", agent: "Planner"),
            ]);
        SelectedRun nebius = RunOf(Tier.Nebius, [Step(Tier.Nebius, task: "gamma", agent: "Planner")]);
        ReportModel model = ReportAggregator.Build([run, nebius]);

        XDocument document = XDocument.Parse(JoulesSvgRenderer.Render(model));

        // One row per distinct task in the order the tasks first appear in the rows of the model; the groups of a row are the agents of its task in the same order.
        string[] tasks = [.. model.Rows.Select(row => row.Task).Distinct()];
        XElement[] rows = SvgRows(document);
        Assert.Equal(3, tasks.Length);
        Assert.Equal("gamma", tasks[0]);
        Assert.NotEqual([.. tasks.Order(StringComparer.Ordinal)], tasks);
        Assert.Equal(tasks, rows.Select(row => Assert.Single(SvgTexts(row, "row-label"))));
        for (int index = 0; index < rows.Length; index++)
        {
            string[] agents = [.. model.Rows.Where(row => row.Task == tasks[index]).Select(row => row.Agent).Distinct()];
            Assert.Equal(agents, rows[index].Elements(SvgNs + "g").Where(element => ClassOf(element) == "group").Select(group => Assert.Single(SvgTexts(group, "group-label"))));
            XElement axis = Assert.Single(rows[index].Elements(SvgNs + "line"));
            Assert.Equal("axis", ClassOf(axis));
            Assert.Equal(PlotTopPixels + PlotHeightPixels + (index * RowPitchPixels), Pixels(axis, "y1"), precision: 2);
            Assert.Equal(24.0, Pixels(Assert.Single(rows[index].Elements(SvgNs + "text"), element => ClassOf(element) == "row-label"), "x"));
        }

        Assert.Equal(FirstRowTopPixels + (3 * RowPitchPixels), Pixels(document.Root!, "height"), precision: 2);
        Assert.Equal(3, SvgAxes(document).Length);
    }

    [Fact]
    public void The_row_label_is_the_cleaned_task_name_as_a_text_node_and_the_group_label_is_the_agent_name_only()
    {
        const string task = "task-name-one";
        const string agent = "agent-name-one";

        XDocument document = XDocument.Parse(RenderSvg(RunOf(Tier.Local, [Step(Tier.Local, task: task, agent: agent)])));

        XElement row = Assert.Single(SvgRows(document));
        XElement group = Assert.Single(SvgGroups(document));
        Assert.Equal([task], SvgTexts(row, "row-label"));
        Assert.Equal([agent], SvgTexts(group, "group-label"));
        Assert.DoesNotContain(" / ", document.Root!.Value, StringComparison.Ordinal);
        Assert.All(document.Descendants().SelectMany(element => element.Attributes()), attribute => Assert.DoesNotContain("name-one", attribute.Value, StringComparison.Ordinal));
        Assert.DoesNotContain(row.Elements(SvgNs + "text"), element => ClassOf(element) == "group-label");
    }

    [Fact]
    public void Pairs_that_would_join_to_the_same_text_render_as_different_rows_and_groups()
    {
        SelectedRun run = RunOf(
            Tier.Local,
            [
                Step(Tier.Local, task: "a / b", agent: "c"),
                Step(Tier.Local, task: "a", agent: "b / c"),
            ]);

        XDocument document = XDocument.Parse(RenderSvg(run));

        XElement[] rows = SvgRows(document);
        Assert.Equal(2, rows.Length);
        Assert.Equal(["a / b", "a"], rows.Select(row => Assert.Single(SvgTexts(row, "row-label"))));
        Assert.Equal(["c", "b / c"], SvgGroups(document).Select(group => Assert.Single(SvgTexts(group, "group-label"))));
        Assert.Equal(["c"], SvgTexts(Assert.Single(rows[0].Elements(SvgNs + "g"), element => ClassOf(element) == "group"), "group-label"));
        Assert.Equal(["b / c"], SvgTexts(Assert.Single(rows[1].Elements(SvgNs + "g"), element => ClassOf(element) == "group"), "group-label"));
    }

    [Fact]
    public void A_group_that_exists_only_in_a_baseline_file_sits_in_the_row_of_its_task()
    {
        SelectedRun primary = RunOf(Tier.Local, [Step(Tier.Local, task: "alpha", agent: "Planner"), Step(Tier.Local, task: "beta", agent: "Planner")]);
        SelectedRun baseline = RunOf(Tier.Local, [Step(Tier.Local, task: "alpha", agent: "Writer", net: Metric.Measured(7.0, "J"))], RunVariant.Baseline);

        XDocument document = XDocument.Parse(RenderSvg(primary, baseline));

        XElement[] rows = SvgRows(document);
        Assert.Equal(["alpha", "beta"], rows.Select(row => Assert.Single(SvgTexts(row, "row-label"))));
        XElement[] alpha = [.. rows[0].Elements(SvgNs + "g").Where(element => ClassOf(element) == "group")];
        Assert.Equal(["Planner", "Writer"], alpha.Select(group => Assert.Single(SvgTexts(group, "group-label"))));
        Assert.Equal(["7.0"], SvgTexts(SlotOfGroup(alpha[1], "local", "baseline"), "value"));
        Assert.Single(rows[1].Elements(SvgNs + "g"), element => ClassOf(element) == "group");
    }

    [Fact]
    public void The_drawing_is_as_wide_as_its_widest_row_and_as_high_as_its_rows_stacked()
    {
        SelectedRun run = RunOf(
            Tier.Local,
            [
                Step(Tier.Local, task: "beta", agent: "B1"),
                Step(Tier.Local, task: "alpha", agent: "A1"),
                Step(Tier.Local, task: "alpha", agent: "A2"),
                Step(Tier.Local, task: "alpha", agent: "A3"),
            ]);

        XDocument document = XDocument.Parse(RenderSvg(run));

        // Four slots of 80 px per group, 32 px between groups, 24 px on the left and 130 px on the right: the widest row has three groups.
        double widest = 24.0 + (3 * 320.0) + (2 * 32.0) + 130.0;
        Assert.Equal(widest, Pixels(document.Root!, "width"), precision: 2);
        Assert.Equal(FirstRowTopPixels + (2 * RowPitchPixels), Pixels(document.Root!, "height"), precision: 2);
        XElement[] axes = SvgAxes(document);
        Assert.Equal(24.0 + 320.0, Pixels(axes[0], "x2"), precision: 2);
        Assert.Equal(24.0 + (3 * 320.0) + (2 * 32.0), Pixels(axes[1], "x2"), precision: 2);
        Assert.All(axes, axis => Assert.Equal(24.0, Pixels(axis, "x1"), precision: 2));
    }

    [Fact]
    public void The_twelve_committed_groups_fit_in_three_rows_of_four_groups()
    {
        SelectedRun[] runs = [.. new[] { Tier.Local, Tier.Nvidia, Tier.Nebius }.Select(tier => Wrap(RunDocumentFixtures.Committed(tier)))];

        XDocument document = XDocument.Parse(RenderSvg(runs));

        XElement[] rows = SvgRows(document);
        Assert.Equal(3, rows.Length);
        Assert.All(rows, row => Assert.Equal(4, row.Elements(SvgNs + "g").Count(element => ClassOf(element) == "group")));
        Assert.Equal(12, SvgGroups(document).Length);
        Assert.InRange(Pixels(document.Root!, "width"), 1200.0, 1700.0);
        Assert.Equal(3, SvgAxes(document).Length);
    }

    [Fact]
    public void The_title_says_the_values_are_measured_net_joules_and_the_chart_has_one_title()
    {
        XElement title = Assert.Single(XDocument.Parse(RenderSvg(LocalSteps((TaskName, 12.3)))).Descendants(SvgNs + "text"), element => ClassOf(element) == "title");

        Assert.Equal(ChartTitle, title.Value);
        Assert.Equal("Measured net energy per step in joules (J), median of the repeats of each bar", title.Value);
    }

    [Fact]
    public void The_title_the_scope_sentence_and_the_first_row_label_do_not_overlap_vertically_and_leave_a_visible_gap()
    {
        // A conservative line box of a text: an ascent of nine tenths of an em above the baseline and a descent of a quarter em below it, from the emitted y and font-size attributes.
        static (double Top, double Bottom) Box(XElement text) => (Pixels(text, "y") - (0.9 * Pixels(text, "font-size")), Pixels(text, "y") + (0.25 * Pixels(text, "font-size")));

        XDocument document = XDocument.Parse(RenderSvg(LocalSteps((TaskName, 12.3))));

        (_, double titleBottom) = Box(Assert.Single(document.Descendants(SvgNs + "text"), element => ClassOf(element) == "title"));
        (double scopeTop, double scopeBottom) = Box(Assert.Single(document.Descendants(SvgNs + "text"), element => ClassOf(element) == "scope"));
        (double labelTop, _) = Box(Assert.Single(document.Descendants(SvgNs + "text"), element => ClassOf(element) == "row-label"));

        Assert.True(titleBottom + 4.0 <= scopeTop, $"title bottom {titleBottom} and scope top {scopeTop} leave less than 4 px");
        Assert.True(scopeBottom + 8.0 <= labelTop, $"scope bottom {scopeBottom} and row label top {labelTop} leave less than 8 px");
        Assert.Equal(FirstRowTopPixels, Pixels(Assert.Single(SvgRows(document)).Elements(SvgNs + "text").Single(element => ClassOf(element) == "row-label"), "y") - 14.0, precision: 2);
    }

    [Fact]
    public void A_chart_of_markers_only_puts_its_axis_at_the_bottom_of_the_plot_and_the_markers_just_above_it()
    {
        XDocument document = XDocument.Parse(RenderSvg(RunOf(Tier.Nvidia, NetSteps(Tier.Nvidia, 1.0))));

        double axisY = Pixels(SvgAxis(document), "y1");

        Assert.Equal(PlotTopPixels + PlotHeightPixels, axisY, precision: 2);
        Assert.All(document.Descendants(SvgNs + "text").Where(element => ClassOf(element) == "marker"), marker => Assert.Equal(axisY - 8.0, Pixels(marker, "y"), precision: 2));
    }

    [Fact]
    public void Huge_medians_keep_their_heights_because_the_scale_does_not_overflow()
    {
        XDocument document = XDocument.Parse(RenderSvg(LocalSteps(("a", double.MaxValue), ("b", -double.MaxValue), ("c", 1e308))));

        XElement[] bars = SvgBars(document);
        XElement[] axes = SvgAxes(document);

        // maxPos = maxNeg = MaxValue: the scale is 240 / (2 * MaxValue) = 120 px per MaxValue, the axis is 120 px below the top of each plot, and 1e308 is 0.5563 of MaxValue.
        Assert.Equal(3, bars.Length);
        Assert.Equal(120.0, Pixels(bars[0], "height"), precision: 2);
        Assert.Equal(120.0, Pixels(bars[1], "height"), precision: 2);
        Assert.Equal(66.75, Pixels(bars[2], "height"), precision: 2);
        for (int index = 0; index < 3; index++)
        {
            Assert.Equal(PlotTopPixels + 120.0 + (index * RowPitchPixels), Pixels(axes[index], "y1"), precision: 2);
        }

        Assert.Equal(PlotTopPixels, Pixels(bars[0], "y"), precision: 2);
        Assert.Equal(Pixels(axes[1], "y1"), Pixels(bars[1], "y"), precision: 2);
        Assert.Equal(Pixels(axes[2], "y1") - 66.75, Pixels(bars[2], "y"), precision: 2);
    }

    [Fact]
    public void The_cap_is_one_thousand_groups_and_the_notice_sentence_names_it()
    {
        Assert.Equal(1000, JoulesSvgRenderer.MaxGroups);
        Assert.Equal("The chart shows the first 1000 task and agent groups; summary.md lists every row.", ReportSentences.ChartTruncated);
        Assert.Contains(JoulesSvgRenderer.MaxGroups.ToString(CultureInfo.InvariantCulture), ReportSentences.ChartTruncated, StringComparison.Ordinal);
        Assert.All(ReportSentences.ChartTruncated, character => Assert.True(character < 128));
    }

    [Fact]
    public void Exactly_one_thousand_groups_are_drawn_without_a_notice()
    {
        string svg = RenderSvg(LocalAgents("alpha", 1000));
        XDocument document = XDocument.Parse(svg);

        Assert.Equal(1000, SvgGroups(document).Length);
        Assert.DoesNotContain(document.Descendants(SvgNs + "g"), element => ClassOf(element) == "notice");
        Assert.DoesNotContain(ReportSentences.ChartTruncated, svg, StringComparison.Ordinal);
        Assert.Equal(FirstRowTopPixels + RowPitchPixels, Pixels(document.Root!, "height"), precision: 2);
    }

    [Fact]
    public void The_one_thousand_and_first_group_is_cut_and_the_notice_is_drawn_once_in_a_final_row()
    {
        string svg = RenderSvg(LocalAgents("alpha", 1001, index => index == 1000 ? 1000.0 : 1.0));
        XDocument document = XDocument.Parse(svg);

        XElement[] groups = SvgGroups(document);
        XElement notice = Assert.Single(document.Descendants(SvgNs + "g"), element => ClassOf(element) == "notice");
        XElement marker = Assert.Single(notice.Elements(SvgNs + "text"));
        Assert.Equal(1000, groups.Length);
        Assert.Equal("a0999", SvgTexts(groups[^1], "group-label").Single());
        Assert.DoesNotContain("a1000", svg, StringComparison.Ordinal);
        Assert.Equal("notice", ClassOf(marker));
        Assert.Equal(ReportSentences.ChartTruncated, marker.Value);
        Assert.DoesNotContain(document.Descendants(SvgNs + "text"), element => ClassOf(element) == "marker" && element.Value == ReportSentences.ChartTruncated);
        Assert.Equal(3000, document.Descendants(SvgNs + "text").Count(element => ClassOf(element) == "marker"));
        Assert.Equal(1, svg.Split(ReportSentences.ChartTruncated).Length - 1);
        Assert.Empty(notice.Descendants(SvgNs + "rect"));
        Assert.True(Pixels(marker, "y") > FirstRowTopPixels + RowPitchPixels);
        Assert.Equal(FirstRowTopPixels + RowPitchPixels + 40.0, Pixels(document.Root!, "height"), precision: 2);

        // The scale is that of the groups drawn: the 1000 J of the cut group does not shrink the bars that are drawn.
        Assert.All(SvgBars(document), bar => Assert.Equal(PlotHeightPixels, Pixels(bar, "height"), precision: 2));
        Assert.Equal(1000, SvgBars(document).Length);
    }

    [Fact]
    public void A_chart_below_the_cap_has_no_notice_element_and_no_notice_text()
    {
        string svg = RenderSvg(LocalSteps((TaskName, 12.3)));

        Assert.DoesNotContain(XDocument.Parse(svg).Descendants(SvgNs + "g"), element => ClassOf(element) == "notice");
        Assert.DoesNotContain("first 1000", svg, StringComparison.Ordinal);
    }

    // ---- the static HTML page (AJ-085) ----

    private const string ChartFile = "joules-per-step.svg";
    private const string PageDoctype = "<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n";
    private static readonly string[] GenericChartClasses = ["bar", "marker", "value", "title", "scope", "axis", "row", "row-label", "group-label", "notice"];

    [GeneratedRegex("<[^<>]*>")]
    private static partial Regex HtmlTag();

    [GeneratedRegex("<tr>(.*?)</tr>", RegexOptions.Singleline)]
    private static partial Regex HtmlRow();

    [GeneratedRegex("<td(?: colspan=\"14\")?>(.*?)</td>", RegexOptions.Singleline)]
    private static partial Regex HtmlCell();

    [GeneratedRegex("<th>(.*?)</th>", RegexOptions.Singleline)]
    private static partial Regex HtmlHeading();

    [GeneratedRegex("<tbody(?: class=\"not-run\")?>(.*?)</tbody>", RegexOptions.Singleline)]
    private static partial Regex HtmlBody();

    [GeneratedRegex(@"\ssrc\s*=\s*(""[^""]*""|'[^']*'|[^\s>]+)", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlSrc();

    [GeneratedRegex("<style>(.*?)</style>", RegexOptions.Singleline)]
    private static partial Regex HtmlStyle();

    [GeneratedRegex("<li>(.*?)</li>", RegexOptions.Singleline)]
    private static partial Regex HtmlItem();

    [GeneratedRegex("<p>(.*?)</p>", RegexOptions.Singleline)]
    private static partial Regex HtmlParagraph();

    [GeneratedRegex("<tbody.*?</tbody>", RegexOptions.Singleline)]
    private static partial Regex HtmlAnyBody();

    private static Uri RepositoryUri(string path = "owner/repo") => new(string.Concat(Uri.UriSchemeHttps, "://", "example.test/", path));

    private static string RenderHtml(params SelectedRun[] runs) => ReportHtmlRenderer.Render(ReportAggregator.Build(runs), repositoryUrl: null);

    private static int Occurrences(string text, string needle) => text.Split(needle).Length - 1;

    private static string[] BodiesOf(string html) => [.. HtmlBody().Matches(html).Select(match => match.Groups[1].Value)];

    private static string[] RowsOfBody(string body) => [.. HtmlRow().Matches(body).Select(match => match.Groups[1].Value)];

    // The decoded text of the cells of one row; the colspan cell of a not-run row counts as one cell.
    private static string[] CellsOfHtmlRow(string row) => [.. HtmlCell().Matches(row).Select(match => WebUtility.HtmlDecode(match.Groups[1].Value))];

    private static string[] HeadingsOf(string html) => [.. HtmlHeading().Matches(html).Select(match => match.Groups[1].Value)];

    private static string[] TagsOf(string html) => [.. HtmlTag().Matches(html).Select(match => match.Value)];

    private static string[][] AggregateCells(string html) => [.. RowsOfBody(BodiesOf(html)[0]).Select(CellsOfHtmlRow)];

    private static string[] BulletsOf(string html) =>
        [.. HtmlItem().Matches(html).Select(match => match.Groups[1].Value)];

    private static string[] ParagraphsOf(string html) =>
        [.. HtmlParagraph().Matches(html).Select(match => match.Groups[1].Value)];

    private static SelectedRun[] CommittedRuns() => [Wrap(RunDocumentFixtures.Committed(Tier.Local)), Wrap(RunDocumentFixtures.Committed(Tier.Nvidia)), Wrap(RunDocumentFixtures.Committed(Tier.Nebius))];

    // Every text member of a document that the page prints carries the value.
    private static SelectedRun[] RunsWithEveryTextMember(string value) =>
    [
        RunOf(
            Tier.Local,
            [Step(Tier.Local, task: value, agent: value, modelId: value, net: Metric.Measured(5.0, "J"), quality: Metric.NotApplicable(value))],
            judge: [Step(Tier.Nvidia, task: value, agent: "Judge", modelId: value, judgedAgent: value)],
            edit: document => document with
            {
                Environment = document.Environment with
                {
                    GpuName = value,
                    DriverVersion = value,
                    LocalRuntime = value,
                    RuntimeVersion = value,
                    Precision = value,
                    ReasoningMode = value,
                    PowerSource = value,
                    Models = [new EnvironmentModel(value, value, Baseline: false)],
                },
            }),
        RunOf(Tier.Nvidia, [Step(Tier.Nvidia, task: value, agent: value, modelId: value)]),
    ];

    [Fact]
    [Trait("Spec", "REQ-030")]
    public void Page_has_the_table_the_chart_reference_and_the_sentence()
    {
        SelectedRun local = RunOf(Tier.Local, NetSteps(Tier.Local, 10.0));
        SelectedRun nvidia = RunOf(Tier.Nvidia, NetSteps(Tier.Nvidia, 10.0));

        string html = RenderHtml(local, nvidia);

        Assert.StartsWith(PageDoctype + "<meta charset=\"utf-8\">\n<meta http-equiv=\"Content-Security-Policy\" content=\"", html, StringComparison.Ordinal);
        Assert.Contains("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n<title>Agent Joules results</title>\n<style>\n", html, StringComparison.Ordinal);
        Assert.Contains("<body>\n<h1>Agent Joules results</h1>\n", html, StringComparison.Ordinal);
        Assert.Equal(1, Occurrences(html, "<table>"));
        Assert.Equal(1, Occurrences(html, "</table>"));
        Assert.Equal(1, Occurrences(html, "<img "));
        Assert.Equal(1, Occurrences(html, ChartFile));
        Assert.Contains(ReportSentences.GpuBoardOnly, html, StringComparison.Ordinal);
        Assert.Contains("GPU-board energy only", html, StringComparison.Ordinal);
        Assert.Equal(0, Occurrences(html, "<script"));
        Assert.EndsWith("</body>\n</html>\n", html, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Spec", "REQ-030")]
    public void The_page_prints_the_header_fields_of_every_run_file_and_the_tiers_that_were_not_run()
    {
        SelectedRun local = RunOf(Tier.Local, NetSteps(Tier.Local, 10.0), edit: document => WithModels(document, "Q4_K_M", "model-a", "model-b"));
        SelectedRun nebius = RunOf(Tier.Nebius, NetSteps(Tier.Nebius, 10.0));
        EnvironmentInfo environment = local.Document.Environment;

        string[] bullets = BulletsOf(RenderHtml(local, nebius));

        string bullet = Assert.Single(bullets, line => line.StartsWith("local: file ", StringComparison.Ordinal));
        Assert.Contains($"run date {local.Document.Run.StartUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)};", bullet, StringComparison.Ordinal);
        Assert.Contains($"GPU {WebUtility.HtmlEncode(environment.GpuName!)};", bullet, StringComparison.Ordinal);
        Assert.Contains($"driver {WebUtility.HtmlEncode(environment.DriverVersion!)};", bullet, StringComparison.Ordinal);
        Assert.Contains($"local runtime {WebUtility.HtmlEncode(environment.LocalRuntime!)} {WebUtility.HtmlEncode(environment.RuntimeVersion!)};", bullet, StringComparison.Ordinal);
        Assert.Contains("models Planner=model-a, Planner=model-b;", bullet, StringComparison.Ordinal);
        Assert.Single(bullets, line => line.StartsWith("nebius: file ", StringComparison.Ordinal));
        Assert.Contains("cloudgpu: not run", bullets);
        Assert.Contains("nvidia: not run", bullets);
    }

    [Fact]
    [Trait("Spec", "REQ-030")]
    public void Html_has_one_row_per_aggregate()
    {
        string[] tasks = ["alpha", "beta", "gamma"];
        string[] agents = ["Planner", "Researcher", "Writer", "Reviewer"];
        SelectedRun run = RunOf(Tier.Local, [.. tasks.SelectMany(task => agents.Select(agent => Step(Tier.Local, task: task, agent: agent)))]);

        string html = RenderHtml(run);

        string[] bodies = BodiesOf(html);
        Assert.Equal(2, bodies.Length);
        string[] rows = RowsOfBody(bodies[0]);
        Assert.Equal(12, rows.Length);
        Assert.All(rows, row => Assert.Equal(ColumnCount, CellsOfHtmlRow(row).Length));
        Assert.Equal(tasks.SelectMany(task => agents.Select(agent => $"{task}/{agent}")), rows.Select(row => CellsOfHtmlRow(row)).Select(cells => $"{cells[TaskColumn]}/{cells[AgentColumn]}"));
        Assert.All(rows, row => Assert.Equal("local", CellsOfHtmlRow(row)[TierColumn]));
        Assert.Equal(3, RowsOfBody(bodies[1]).Length);
    }

    [Fact]
    [Trait("Spec", "REQ-030")]
    public void Repeats_collapse_into_one_row_per_task_and_agent_per_tier()
    {
        SelectedRun local = RunOf(Tier.Local, NetSteps(Tier.Local, 10.0, 12.0, 14.0));
        SelectedRun nebius = RunOf(Tier.Nebius, NetSteps(Tier.Nebius, 1.0, 2.0));

        string[][] rows = AggregateCells(RenderHtml(local, nebius));

        Assert.Equal(2, rows.Length);
        Assert.Equal(["local", "nebius"], rows.Select(cells => cells[TierColumn]));
        Assert.Equal("12.0 [10.0 to 14.0] n=3", rows[0][NetColumn]);
    }

    [Fact]
    [Trait("Spec", "REQ-030")]
    public void Rows_carry_the_cells_of_the_summary_in_the_same_order()
    {
        SelectedRun[] runs = CommittedRuns();
        ReportModel model = ReportAggregator.Build(runs);

        string[][] markdown = [.. DataRows(SummaryMarkdownRenderer.Render(model)).Select(CellsOf).Where(cells => cells[TaskColumn] != ReportSentences.NotRun)];
        string[][] html = AggregateCells(ReportHtmlRenderer.Render(model, repositoryUrl: null));

        Assert.Equal(36, html.Length);
        Assert.Equal(markdown.Length, html.Length);
        for (int index = 0; index < markdown.Length; index++)
        {
            Assert.Equal(markdown[index], html[index]);
        }
    }

    [Fact]
    [Trait("Spec", "REQ-030")]
    public void The_headings_are_the_headings_of_the_summary()
    {
        ReportModel model = ReportAggregator.Build(CommittedRuns());

        string[] headings = HeadingsOf(ReportHtmlRenderer.Render(model, repositoryUrl: null));

        Assert.Equal(ColumnCount, headings.Length);
        Assert.Equal(CellsOf(TableLines(SummaryMarkdownRenderer.Render(model))[0]), headings);
        Assert.Contains("<thead>\n<tr><th>tier</th>", ReportHtmlRenderer.Render(model, repositoryUrl: null), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Spec", "REQ-030")]
    public void A_tier_without_a_run_file_is_a_not_run_row_in_a_second_table_body()
    {
        string html = RenderHtml(RunOf(Tier.Local, NetSteps(Tier.Local, 10.0)));

        string[] bodies = BodiesOf(html);
        Assert.Equal(2, bodies.Length);
        Assert.Contains("<tbody class=\"not-run\">\n", html, StringComparison.Ordinal);
        Assert.Single(RowsOfBody(bodies[0]));
        Assert.Equal(
            [
                "<td>cloudgpu</td><td colspan=\"14\">not run</td>",
                "<td>nvidia</td><td colspan=\"14\">not run</td>",
                "<td>nebius</td><td colspan=\"14\">not run</td>",
            ],
            RowsOfBody(bodies[1]));
        Assert.True(html.IndexOf("<tbody>", StringComparison.Ordinal) < html.IndexOf("<tbody class=\"not-run\">", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Spec", "REQ-030")]
    public void Without_a_missing_tier_there_is_no_second_table_body()
    {
        string html = RenderHtml(
            RunOf(Tier.Local, NetSteps(Tier.Local, 10.0)),
            RunOf(Tier.CloudGpu, NetSteps(Tier.CloudGpu, 10.0)),
            RunOf(Tier.Nvidia, NetSteps(Tier.Nvidia, 10.0)),
            RunOf(Tier.Nebius, NetSteps(Tier.Nebius, 10.0)));

        Assert.Single(BodiesOf(html));
        Assert.DoesNotContain("<tbody class=\"not-run\">", html, StringComparison.Ordinal);
        Assert.Equal(4, RowsOfBody(BodiesOf(html)[0]).Length);
    }

    [Fact]
    [Trait("Spec", "REQ-030")]
    public void An_empty_input_prints_four_not_run_bullets_the_disclosures_an_empty_body_and_four_not_run_rows()
    {
        ReportModel model = ReportAggregator.Build([]);

        string html = ReportHtmlRenderer.Render(model, repositoryUrl: null);

        Assert.Equal(["local: not run", "cloudgpu: not run", "nvidia: not run", "nebius: not run"], BulletsOf(html));
        Assert.Equal(model.Disclosures.Select(WebUtility.HtmlEncode), ParagraphsOf(html).Take(model.Disclosures.Count));
        string[] bodies = BodiesOf(html);
        Assert.Equal(2, bodies.Length);
        Assert.Empty(RowsOfBody(bodies[0]));
        Assert.Equal(4, RowsOfBody(bodies[1]).Length);
        Assert.Equal(1, Occurrences(html, "<table>"));
        Assert.Equal(1, Occurrences(html, "<img "));
    }

    [Fact]
    [Trait("Spec", "REQ-030")]
    public void A_baseline_file_follows_its_tier_and_a_file_without_rows_has_a_no_completed_steps_row()
    {
        static RunDocument Stopped(RunDocument document) => document with { Run = document.Run with { Status = RunStatus.Partial, StopReason = "budget-reached", Flags = [RunFlagNames.PartialOffload] } };
        SelectedRun empty = RunOf(Tier.Local, [], tasks: [TaskName], edit: Stopped);
        SelectedRun baseline = RunOf(Tier.Local, NetSteps(Tier.Local, 5.0), RunVariant.Baseline);
        SelectedRun nebius = RunOf(Tier.Nebius, NetSteps(Tier.Nebius, 5.0));
        ReportModel model = ReportAggregator.Build([empty, baseline, nebius]);

        string html = ReportHtmlRenderer.Render(model, repositoryUrl: null);

        string[][] rows = AggregateCells(html);
        Assert.Equal(["local", "local baseline", "nebius"], rows.Select(cells => cells[TierColumn]));
        Assert.All(rows, cells => Assert.Equal(ColumnCount, cells.Length));
        Assert.Equal(ReportSentences.NoCompletedSteps, rows[0][TaskColumn]);
        Assert.Equal("partial-offload, partial", rows[0][FlagsColumn]);
        Assert.All(rows[0].Skip(2).Take(12), cell => Assert.Empty(cell));
        string[][] markdown = [.. DataRows(SummaryMarkdownRenderer.Render(model)).Select(CellsOf).Where(cells => cells[TaskColumn] != ReportSentences.NotRun)];
        Assert.Equal(markdown, rows);
    }

    [Fact]
    [Trait("Spec", "REQ-030")]
    public void Page_links_repository_readme()
    {
        Uri url = RepositoryUri();

        string html = ReportHtmlRenderer.Render(ReportAggregator.Build([RunOf(Tier.Local, NetSteps(Tier.Local, 10.0))]), url);

        Assert.Equal(1, Occurrences(html, "<a "));
        Assert.Contains($"<p><a href=\"{url.AbsoluteUri}#readme\">Repository README</a></p>\n", html, StringComparison.Ordinal);
        Assert.DoesNotContain("not configured", html, StringComparison.Ordinal);
        Assert.Equal(1, Occurrences(html, "href="));
        Assert.EndsWith("</p>\n</body>\n</html>\n", html, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Spec", "REQ-030")]
    public void Page_without_url_has_no_link()
    {
        string html = RenderHtml(RunOf(Tier.Local, NetSteps(Tier.Local, 10.0)));

        Assert.Equal(0, Occurrences(html, "<a "));
        Assert.Equal(0, Occurrences(html, "href"));
        Assert.Equal(0, Occurrences(html, "http:"));
        Assert.Equal(0, Occurrences(html, "https:"));
        Assert.Contains("<p>Repository link not configured (Report:RepositoryUrl).</p>\n", html, StringComparison.Ordinal);
        Assert.Equal("Repository link not configured (Report:RepositoryUrl).", ReportHtmlRenderer.RepositoryNotConfigured);
    }

    [Theory]
    [Trait("Spec", "REQ-030")]
    [InlineData("http", "example.test/owner/repo")]
    [InlineData("ftp", "example.test/owner/repo")]
    [InlineData("file", "/owner/repo")]
    [InlineData("https", "user:secret@example.test/owner/repo")]
    public void Non_https_url_is_rejected(string scheme, string rest)
    {
        ReportModel model = ReportAggregator.Build([]);

        ArgumentException error = Assert.Throws<ArgumentException>(() => ReportHtmlRenderer.Render(model, new Uri(scheme + "://" + rest)));

        Assert.Equal("repositoryUrl", error.ParamName);
        Assert.DoesNotContain("secret", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Spec", "REQ-030")]
    public void A_relative_url_is_rejected_and_a_null_model_is_refused()
    {
        ReportModel model = ReportAggregator.Build([]);

        _ = Assert.Throws<ArgumentException>(() => ReportHtmlRenderer.Render(model, new Uri("/owner/repo", UriKind.Relative)));
        _ = Assert.Throws<ArgumentNullException>(() => ReportHtmlRenderer.Render(null!, repositoryUrl: null));
    }

    [Fact]
    [Trait("Spec", "REQ-030")]
    public void The_chart_is_an_image_of_the_sibling_file_shown_at_its_own_size_in_a_scrolling_box()
    {
        string html = RenderHtml(RunOf(Tier.Local, NetSteps(Tier.Local, 10.0)));

        Assert.Contains($"<div class=\"chart\">\n<img src=\"{ChartFile}\" alt=\"Net joules per step and tier; N/A where no GPU-board energy was measured\">\n</div>\n", html, StringComparison.Ordinal);
        Assert.Equal(ChartFile, ReportHtmlRenderer.ChartFileName);
        Assert.Equal("Net joules per step and tier; N/A where no GPU-board energy was measured", ReportHtmlRenderer.ChartAlt);
        Assert.Equal(0, Occurrences(html, "<object"));
        Assert.Equal(0, Occurrences(html, "<embed"));
        Assert.Equal(0, Occurrences(html, "<iframe"));
        Assert.Equal(0, Occurrences(html, "<svg"));
        Assert.Equal(1, Occurrences(html, "<style>"));
        string style = HtmlStyle().Match(html).Groups[1].Value;
        Assert.Contains("overflow-x: auto", style, StringComparison.Ordinal);
        Assert.DoesNotContain("max-width", style, StringComparison.Ordinal);
        Assert.DoesNotContain("width: 100%", style, StringComparison.Ordinal);
        Assert.DoesNotContain("@import", style, StringComparison.Ordinal);
        Assert.DoesNotContain("url(", style, StringComparison.Ordinal);
        Assert.All(GenericChartClasses, name => Assert.DoesNotMatch($@"\.{Regex.Escape(name)}(?![\w-])", style));
    }

    [Fact]
    [Trait("Spec", "REQ-030")]
    public void The_page_has_the_parts_in_order()
    {
        string html = ReportHtmlRenderer.Render(ReportAggregator.Build(CommittedRuns()), RepositoryUri());

        int[] positions =
        [
            html.IndexOf("<h1>", StringComparison.Ordinal),
            html.IndexOf("<ul>", StringComparison.Ordinal),
            html.IndexOf("<p>" + ReportSentences.GpuBoardOnly + "</p>", StringComparison.Ordinal),
            html.IndexOf("<table>", StringComparison.Ordinal),
            html.IndexOf("<img ", StringComparison.Ordinal),
            html.IndexOf("<a href=", StringComparison.Ordinal),
        ];
        Assert.All(positions, position => Assert.True(position > 0));
        Assert.Equal(positions.Order(), positions);
    }

    [Fact]
    [Trait("Spec", "REQ-030")]
    public void Page_has_no_script_no_link_and_no_resource_but_the_sibling_svg()
    {
        IEnumerable<SelectedRun> fixtures = EveryReportFixture().Select(document => Wrap(document));
        foreach (SelectedRun fixture in fixtures.Concat(CommittedRuns()))
        {
            string html = RenderHtml(fixture);

            Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("<link", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("http:", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("https:", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("href", html, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, Occurrences(html.ToLowerInvariant(), "http-equiv"));
            Assert.Contains("default-src 'none'; img-src 'self'; style-src 'sha256-", html, StringComparison.Ordinal);
            Assert.Equal([ChartFile], HtmlSrc().Matches(html).Select(match => match.Groups[1].Value.Trim('"')));
            Assert.Equal(1, Occurrences(html, "<table>"));
            Assert.Equal(1, Occurrences(html, "<img "));
            Assert.Contains("GPU-board energy only", html, StringComparison.Ordinal);
        }
    }

    [Theory]
    [Trait("Spec", "REQ-030")]
    [MemberData(nameof(InjectionPayloads))]
    public void Hostile_strings_cannot_add_markup_to_the_page(int index)
    {
        string payload = HostilePayloads[index];
        string plain = ReportHtmlRenderer.Render(ReportAggregator.Build(RunsWithEveryTextMember("p")), repositoryUrl: null);

        string html = ReportHtmlRenderer.Render(ReportAggregator.Build(RunsWithEveryTextMember(payload)), repositoryUrl: null);

        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);

        // Every angle bracket is one of the page's own tags, and the tags (names and attributes) are those of a page with plain names: nothing from a document became markup.
        string[] tags = TagsOf(html);
        Assert.Equal(TagsOf(plain), tags);
        Assert.Equal(tags.Length, html.Count(character => character == '<'));
        Assert.Equal(tags.Length, html.Count(character => character == '>'));

        // The decoded cells hold the cleaned text, once.
        string cleaned = ReportText.Clean(payload, ReportText.NameMaxLength);
        string[] first = AggregateCells(html)[0];
        Assert.Equal(cleaned, first[TaskColumn]);
        Assert.Equal(cleaned, first[AgentColumn]);
        Assert.Contains($"GPU {WebUtility.HtmlEncode(cleaned)};", html, StringComparison.Ordinal);
        Assert.DoesNotContain("nvapi-" + new string('K', 30), html, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', html);
        Assert.DoesNotContain('\\', html);
        Assert.All(html, character => Assert.True(character < 128));
    }

    [Fact]
    [Trait("Spec", "REQ-030")]
    public void Text_from_a_document_is_encoded_once()
    {
        SelectedRun run = RunOf(
            Tier.Local,
            [Step(Tier.Local, task: "t&u", agent: "a<b", modelId: "m&n", quality: Metric.NotApplicable("x&y<z>\"'"))],
            edit: document => document with { Environment = document.Environment with { GpuName = "G&T <x>" } });

        string html = RenderHtml(run);

        Assert.Contains("<td>t&amp;u</td><td>a&lt;b</td>", html, StringComparison.Ordinal);
        Assert.Contains("N/A (x&amp;y&lt;z&gt;&quot;&#39;)", html, StringComparison.Ordinal);
        Assert.Contains("GPU G&amp;T &lt;x&gt;;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("&amp;amp;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("&amp;lt;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("&amp;#", html, StringComparison.Ordinal);
        Assert.DoesNotContain("&amp;quot;", html, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Spec", "REQ-030")]
    public void Disclosures_are_paragraphs_in_the_order_of_the_model_and_a_leading_marker_is_not_escaped_with_a_backslash()
    {
        string[] disclosures =
        [
            ReportSentences.GpuBoardOnly,
            ReportSentences.JudgeExcluded,
            ReportSentences.SelfJudged,
            ReportSentences.ConcurrentOverlap,
            ReportSentences.ConcurrentCostOverlap,
            "- item <b>&",
            "1. item",
            "# head",
            "= setext",
        ];

        string html = ReportHtmlRenderer.Render(ModelWithDisclosures(disclosures), repositoryUrl: null);

        string[] paragraphs = ParagraphsOf(html);
        Assert.Equal(
            [.. disclosures.Take(5), "- item &lt;b&gt;&amp;", "1. item", "# head", "= setext"],
            paragraphs.Take(disclosures.Length));
        Assert.DoesNotContain('\\', html);
    }

    [Fact]
    [Trait("Spec", "REQ-030")]
    public void The_models_differ_line_is_built_per_tier_and_each_text_is_encoded_on_its_own()
    {
        SelectedRun local = RunOf(Tier.Local, NetSteps(Tier.Local, 1.0), edit: document => WithModels(document, "Q4_K_M", "m2", "m1"));
        SelectedRun nvidia = RunOf(Tier.Nvidia, NetSteps(Tier.Nvidia, 1.0), edit: document => WithModels(document, null, "nv<1>&"));
        ReportModel model = ReportAggregator.Build([local, nvidia]);

        string html = ReportHtmlRenderer.Render(model, repositoryUrl: null);

        string line = Assert.Single(ParagraphsOf(html), paragraph => paragraph.StartsWith(ReportSentences.ModelsDifferPrefix, StringComparison.Ordinal));
        Assert.Equal(ReportSentences.ModelsDifferPrefix + "local=m1+m2 (Q4_K_M), nvidia=nv&lt;1&gt;&amp; (N/A)", line);
        Assert.DoesNotContain('\\', html);

        // The line is the one of the summary, up to the escape of the surface.
        string markdownLine = ModelsDifferLineOf(local, nvidia).Replace("\\", string.Empty, StringComparison.Ordinal);
        Assert.Equal(WebUtility.HtmlDecode(line), markdownLine);
    }

    [Fact]
    [Trait("Spec", "REQ-030")]
    public void The_sampling_fallback_sentence_is_nested_under_the_bullet_of_its_tier()
    {
        string html = RenderHtml(Wrap(RunDocumentFixtures.SamplingFallback()));

        Assert.Equal(1, Occurrences(html, "<ul><li>" + ReportSentences.SamplingFallback + "</li></ul></li>"));
        Assert.Matches("<li>local: file [^\n]*<ul><li>", html);
        Assert.Equal(2, Occurrences(html, "<ul>"));
    }

    [Fact]
    [Trait("Spec", "REQ-030")]
    public void The_page_is_deterministic_ascii_with_lf_and_one_final_line_feed_whatever_the_culture()
    {
        SelectedRun[] runs = CommittedRuns();
        Uri url = RepositoryUri();

        string first = ReportHtmlRenderer.Render(ReportAggregator.Build(runs), url);
        string second = ReportHtmlRenderer.Render(ReportAggregator.Build(runs), url);
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            string german = ReportHtmlRenderer.Render(ReportAggregator.Build(runs), url);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            string turkish = ReportHtmlRenderer.Render(ReportAggregator.Build(runs), url);

            Assert.Equal(first, german);
            Assert.Equal(first, turkish);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }

        Assert.Equal(first, second);
        Assert.All(first, character => Assert.True(character < 128));
        Assert.DoesNotContain('\r', first);
        Assert.EndsWith("</html>\n", first, StringComparison.Ordinal);
        Assert.False(first.EndsWith("\n\n", StringComparison.Ordinal));
        Assert.StartsWith("<!DOCTYPE html>\n", first, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Spec", "REQ-030")]
    public void The_page_ranks_nothing_and_compares_no_tiers()
    {
        string html = RenderHtml(CommittedRuns());

        // The words that name a figure across tiers appear nowhere outside the names of the rows (a task such as api-migration contains "ratio"): not in the style, the header, the disclosures, the headings or the link text.
        string outside = HtmlStyle().Replace(html, string.Empty);
        outside = HtmlAnyBody().Replace(outside, string.Empty);
        string text = WebUtility.HtmlDecode(HtmlTag().Replace(outside, " "));
        Assert.All(CrossTierWords, word => Assert.DoesNotContain(word, text, StringComparison.OrdinalIgnoreCase));
        Assert.All(CrossTierWords, word => Assert.DoesNotContain(HeadingsOf(html), heading => heading.Contains(word, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    [Trait("Spec", "REQ-030")]
    public void NoJekyll_is_created_empty_when_absent_and_left_alone_afterwards()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string docs = Path.Combine(workspace.Root, "docs");
        _ = Directory.CreateDirectory(docs);
        string marker = Path.Combine(docs, ".nojekyll");

        bool created = NoJekyllWriter.EnsureExists(docs);

        Assert.True(created);
        Assert.Equal(0, new FileInfo(marker).Length);
        Assert.Equal(".nojekyll", NoJekyllWriter.FileName);
        Assert.False(NoJekyllWriter.EnsureExists(docs));
        Assert.Equal(0, new FileInfo(marker).Length);
        Assert.Equal([marker], Directory.GetFileSystemEntries(docs));
    }

    [Fact]
    [Trait("Spec", "REQ-030")]
    public void An_existing_NoJekyll_keeps_its_bytes()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string marker = workspace.WriteFile(Path.Combine("docs", ".nojekyll"), "known content\n");
        DateTime written = new FileInfo(marker).LastWriteTimeUtc;

        bool created = NoJekyllWriter.EnsureExists(Path.Combine(workspace.Root, "docs"));

        Assert.False(created);
        Assert.Equal("known content\n", File.ReadAllText(marker));
        Assert.Equal(written, new FileInfo(marker).LastWriteTimeUtc);
    }

    [Fact]
    [Trait("Spec", "REQ-030")]
    public void An_existing_NoJekyll_that_is_read_only_is_not_opened_for_writing()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string marker = workspace.WriteFile(Path.Combine("docs", ".nojekyll"), "known content\n");
        File.SetAttributes(marker, FileAttributes.ReadOnly);
        try
        {
            Assert.False(NoJekyllWriter.EnsureExists(Path.Combine(workspace.Root, "docs")));
            Assert.Equal("known content\n", File.ReadAllText(marker));
        }
        finally
        {
            File.SetAttributes(marker, FileAttributes.Normal);
        }
    }

    [Fact]
    [Trait("Spec", "REQ-030")]
    public void No_docs_folder_creates_nothing()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string docs = Path.Combine(workspace.Root, "docs");

        bool created = NoJekyllWriter.EnsureExists(docs);

        Assert.False(created);
        Assert.False(Directory.Exists(docs));
        Assert.Empty(Directory.GetFileSystemEntries(workspace.Root));
    }

    [Theory]
    [Trait("Spec", "REQ-030")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void NoJekyll_refuses_a_blank_folder(string? folder) => _ = Assert.ThrowsAny<ArgumentException>(() => NoJekyllWriter.EnsureExists(folder!));

    [Fact]
    [Trait("Spec", "REQ-030")]
    public void The_committed_NoJekyll_is_an_empty_file_beside_the_report_folder()
    {
        string marker = Path.Combine(ConfigurationFixtures.RepositoryRoot(), "docs", ".nojekyll");

        Assert.True(File.Exists(marker));
        Assert.Equal(0, new FileInfo(marker).Length);
    }

    // ---- the fix round of AJ-085: drift against the summary, ASCII by construction, the link, the policy ----

    // The Markdown escape is a backslash before a character; the summary cell of a name or a reason is compared with the page cell after it is taken off.
    private static string UnescapeMarkdown(string cell) => MarkdownBackslash().Replace(cell, "$1");

    [GeneratedRegex(@"\\(.)", RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownBackslash();

    // The decoded cells of the page, row by row, equal the unescaped cells of summary.md for the same model (the not-run rows of the summary are the rows of the second body of the page).
    private static void AssertPageRowsEqualSummaryRows(params SelectedRun[] runs)
    {
        ReportModel model = ReportAggregator.Build(runs);
        string[][] markdown =
        [
            .. DataRows(SummaryMarkdownRenderer.Render(model))
                .Select(CellsOf)
                .Where(cells => cells[TaskColumn] != ReportSentences.NotRun)
                .Select(cells => cells.Select(UnescapeMarkdown).ToArray()),
        ];

        string[][] html = AggregateCells(ReportHtmlRenderer.Render(model, repositoryUrl: null));

        Assert.Equal(markdown.Length, html.Length);
        for (int index = 0; index < markdown.Length; index++)
        {
            Assert.Equal(markdown[index], html[index]);
        }
    }

    [Fact]
    [Trait("Spec", "REQ-030")]
    public void Every_report_fixture_has_the_cells_of_the_summary_row_by_row()
    {
        foreach (RunDocument document in EveryReportFixture())
        {
            AssertPageRowsEqualSummaryRows(Wrap(document));
        }
    }

    [Fact]
    [Trait("Spec", "REQ-030")]
    public void Rich_rows_have_the_cells_of_the_summary_flags_labels_counts_and_all()
    {
        StepReading[] steps =
        [
            Step(Tier.Local, repeat: 1, flags: ReadingFlags.SharedWindow | ReadingFlags.LowResolution, latency: Metric.Measured(1000.25, "ms"), ttft: Metric.Measured(250.25, "ms"), inputTokens: Metric.Measured(121.0, "tokens")),
            Step(Tier.Local, repeat: 2, flags: ReadingFlags.SharedWindow, latency: Metric.Measured(1003.25, "ms"), inputTokens: Metric.Measured(124.0, "tokens"), quality: Metric.ModelJudged(3.0)),
            Step(Tier.Local, repeat: 3, outcome: StepOutcomes.Cancelled),
            Step(Tier.Local, repeat: 4, outcome: StepOutcomes.Incomplete),
            Step(Tier.Local, repeat: 5, outcome: StepOutcomes.Error),
            Step(Tier.Local, repeat: 1, agent: "Writer", inputTokens: Metric.NotReported("provider-omitted-usage"), cost: Metric.NotApplicable("cost-not-configured")),
            Step(Tier.Local, repeat: 1, agent: "Reviewer", quality: Metric.NotApplicable(new string('q', 200)), inputTokens: Metric.NotApplicable("a"), outputTokens: Metric.NotReported("b")),
        ];
        SelectedRun local = RunOf(Tier.Local, steps, edit: document => document with { Run = document.Run with { Status = RunStatus.Partial, StopReason = "budget-reached", Flags = [RunFlagNames.PartialOffload] } });
        SelectedRun nebius = RunOf(Tier.Nebius, [Step(Tier.Nebius, inputTokens: Metric.NotReported("provider-omitted-usage"))]);

        AssertPageRowsEqualSummaryRows(local, nebius);

        // The row of the first agent shows what the comparison is meant to guard: the flags, the counts of the repeats that did not finish and the non-integral latency.
        string[] planner = AggregateCells(RenderHtml(local, nebius)).First(cells => cells[AgentColumn] == Planner && cells[TierColumn] == "local");
        Assert.NotEmpty(planner[FlagsColumn]);
        Assert.Contains("partial-offload, partial, 1 cancelled, 1 error, 1 incomplete", planner[FlagsColumn], StringComparison.Ordinal);
        Assert.StartsWith("1002 [1000 to 1003] n=2", planner[LatencyColumn], StringComparison.Ordinal);
        Assert.Contains("N/A, not-reported", AggregateCells(RenderHtml(local, nebius)).First(cells => cells[AgentColumn] == "Reviewer")[LabelsColumn], StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Spec", "REQ-030")]
    public void An_empty_later_tier_file_sorts_after_the_rows_of_the_earlier_tiers_as_in_the_summary()
    {
        static RunDocument Stopped(RunDocument document) => document with { Run = document.Run with { Status = RunStatus.Partial, StopReason = "budget-reached" } };
        SelectedRun emptyNebius = RunOf(Tier.Nebius, [], tasks: [TaskName], edit: Stopped);
        SelectedRun local = RunOf(Tier.Local, NetSteps(Tier.Local, 5.0));
        SelectedRun localBaseline = RunOf(Tier.Local, NetSteps(Tier.Local, 6.0), RunVariant.Baseline);

        AssertPageRowsEqualSummaryRows(emptyNebius, localBaseline, local);

        string[][] rows = AggregateCells(RenderHtml(emptyNebius, localBaseline, local));
        Assert.Equal(["local", "local baseline", "nebius"], rows.Select(cells => cells[TierColumn]));
        Assert.Equal(ReportSentences.NoCompletedSteps, rows[2][TaskColumn]);
    }

    [Fact]
    [Trait("Spec", "REQ-030")]
    public void A_long_reason_is_cut_at_eighty_characters_in_the_cells()
    {
        string reason = new('r', 200);
        SelectedRun run = RunOf(Tier.Local, [Step(Tier.Local, quality: Metric.NotApplicable(reason))]);

        string[] cells = AggregateCells(RenderHtml(run))[0];

        Assert.Equal("N/A (" + ReportText.Clean(reason, ReportText.ReasonMaxLength) + ")", cells[QualityColumn]);
        Assert.Equal(5 + 80 + 3 + 1, cells[QualityColumn].Length);
    }

    [Fact]
    [Trait("Spec", "REQ-030")]
    public void The_committed_models_differ_paragraph_is_the_exact_per_tier_line_and_is_not_cut()
    {
        const string expected = "Models differ across tiers: local=nemotron-3-nano:30b (Q4_K_M), nvidia=nvidia/nemotron-nano-3-30b-a3b (N/A), nebius=nvidia/NVIDIA-Nemotron-3-Nano-30B-A3B (N/A)";

        string html = RenderHtml(CommittedRuns());

        string line = Assert.Single(ParagraphsOf(html), paragraph => paragraph.StartsWith(ReportSentences.ModelsDifferPrefix, StringComparison.Ordinal));
        Assert.Equal(159, line.Length);
        Assert.Equal(expected, line);
    }

    [Fact]
    [Trait("Spec", "REQ-030")]
    public void A_models_differ_line_longer_than_the_name_limit_is_not_cut()
    {
        string longId = "org/" + new string('m', 100);
        SelectedRun local = RunOf(Tier.Local, NetSteps(Tier.Local, 1.0), edit: document => WithModels(document, "Q4_K_M", longId));
        SelectedRun nvidia = RunOf(Tier.Nvidia, NetSteps(Tier.Nvidia, 1.0), edit: document => WithModels(document, null, "nvidia/other-model"));

        string html = ReportHtmlRenderer.Render(ReportAggregator.Build([local, nvidia]), repositoryUrl: null);

        string line = Assert.Single(ParagraphsOf(html), paragraph => paragraph.StartsWith(ReportSentences.ModelsDifferPrefix, StringComparison.Ordinal));
        Assert.True(line.Length > ReportText.NameMaxLength);
        Assert.Equal(ReportSentences.ModelsDifferPrefix + "local=" + longId + " (Q4_K_M), nvidia=nvidia/other-model (N/A)", line);
    }

    [Fact]
    [Trait("Spec", "REQ-030")]
    public void Characters_outside_ascii_are_written_as_numeric_references_and_the_page_stays_ascii()
    {
        string name = "price " + (char)0x20AC + " " + (char)0x65E5 + (char)0x672C + " " + (char)0x416 + " e" + (char)0x301 + " " + char.ConvertFromUtf32(0x1F600);
        SelectedRun run = RunOf(
            Tier.Local,
            [Step(Tier.Local, task: name, agent: name, modelId: name, quality: Metric.NotApplicable(name))],
            edit: document => document with { Environment = document.Environment with { GpuName = name, Models = [new EnvironmentModel(name, name, Baseline: false)] } });
        Uri url = new(string.Concat(Uri.UriSchemeHttps, "://", "b" + (char)0xFC + "cher.example/owner/repo"));

        string html = ReportHtmlRenderer.Render(ReportAggregator.Build([run]), url);

        Assert.All(html, character => Assert.True(character < 128));
        string cleaned = ReportText.Clean(name, ReportText.NameMaxLength);
        string[] cells = AggregateCells(html)[0];
        Assert.Equal(cleaned, cells[TaskColumn]);
        Assert.Equal(cleaned, cells[AgentColumn]);
        Assert.Equal("N/A (" + ReportText.Clean(name, ReportText.ReasonMaxLength) + ")", cells[QualityColumn]);
        Assert.Contains("GPU " + cleaned + ";", WebUtility.HtmlDecode(BulletsOf(html)[0]), StringComparison.Ordinal);
        Assert.Contains("&#8364;", html, StringComparison.Ordinal);
        Assert.Contains("&#128512;", html, StringComparison.Ordinal);
        Assert.Contains("<a href=\"" + Uri.UriSchemeHttps + "://xn--bcher-kva.example/owner/repo#readme\">", html, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Spec", "REQ-030")]
    public void The_link_drops_a_fragment_keeps_a_port_and_is_encoded_as_an_attribute_value()
    {
        ReportModel model = ReportAggregator.Build([RunOf(Tier.Local, NetSteps(Tier.Local, 10.0))]);

        string withFragment = ReportHtmlRenderer.Render(model, RepositoryUri("owner/repo#top"));
        string withPort = ReportHtmlRenderer.Render(model, new Uri(string.Concat(Uri.UriSchemeHttps, "://", "example.test:8443/owner/repo")));
        string quoted = ReportHtmlRenderer.Render(model, RepositoryUri("o'wner/re\"po"));

        Assert.Contains("href=\"" + RepositoryUri("owner/repo").AbsoluteUri + "#readme\"", withFragment, StringComparison.Ordinal);
        Assert.DoesNotContain("#top", withFragment, StringComparison.Ordinal);
        Assert.Contains(":8443/owner/repo#readme\"", withPort, StringComparison.Ordinal);
        Assert.Equal(1, Occurrences(quoted, "href="));
        Assert.Equal(1, Occurrences(quoted, "<a "));
        Assert.Equal(0, Occurrences(quoted, "\"po"));
        Assert.Contains("o&#39;wner", quoted, StringComparison.Ordinal);
        Assert.Contains("#readme\">Repository README</a>", quoted, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Spec", "REQ-030")]
    public void A_url_with_a_query_or_a_key_is_refused_without_echoing_it()
    {
        ReportModel model = ReportAggregator.Build([]);
        string key = FakeKey();

        foreach (string rest in new[] { "owner/repo?a=1", "owner/repo?token=" + key, "owner/" + key, "owner/repo#" + key })
        {
            Uri url = RepositoryUri(rest);

            ArgumentException error = Assert.Throws<ArgumentException>(() => ReportHtmlRenderer.Render(model, url));

            Assert.Equal("repositoryUrl", error.ParamName);
            Assert.DoesNotContain(key, error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("example.test", error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    [Trait("Spec", "REQ-030")]
    public void The_page_carries_a_content_security_policy_whose_style_hash_is_that_of_the_emitted_style()
    {
        string html = RenderHtml(RunOf(Tier.Local, NetSteps(Tier.Local, 10.0)));

        string style = HtmlStyle().Match(html).Groups[1].Value;
        string hash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(style)));
        string policy = $"<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; img-src 'self'; style-src 'sha256-{hash}'; base-uri 'none'; form-action 'none'\">\n";
        Assert.StartsWith(PageDoctype + "<meta charset=\"utf-8\">\n" + policy, html, StringComparison.Ordinal);
        Assert.Equal(1, Occurrences(html, "http-equiv"));
        Assert.StartsWith("\nbody {", style, StringComparison.Ordinal);
        Assert.EndsWith("}\n", style, StringComparison.Ordinal);
        Assert.Equal(1, Occurrences(html, "<style>"));
    }

    // ---- AJ-086 (REQ-016, REQ-030): the report command ----
    // Every test runs the real command line through the harness with an empty TempWorkspace as the working directory; the committed fixture folder is read in place by its absolute path and is never copied
    // into the repository's output. Nothing is written under docs/report or results of the repository.

    private static ReadOnlySpan<byte> ByteOrderMark => [0xEF, 0xBB, 0xBF];

    private static readonly string[] ReportOutputNames = ["summary.md", "joules-per-step.svg", "index.html"];

    private static string FixtureFolder() => Path.Combine(ConfigurationFixtures.RepositoryRoot(), RunDocumentFixtures.CommittedFolder);

    private static Task<CliRunResult> ReportAsync(TempWorkspace workspace, params string[] arguments) =>
        CliHarness.RunTokensAsync(["report", .. arguments], new CliHostOverrides(WorkingDirectory: workspace.Root));

    private static string[] OutputLines(string text) => [.. text.Split('\n').Select(static line => line.TrimEnd('\r')).Where(static line => line.Length > 0)];

    private static string[] ReportSums(string folder) =>
        [.. ReportOutputNames.Select(name => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(folder, name)))))];

    // The committed text of a tier, written into a folder of the workspace under its committed file name.
    private static string PutCommitted(TempWorkspace workspace, string folder, Tier tier) =>
        Put(workspace, RunDocumentFixtures.CommittedFileName(tier), RunDocumentFixtures.CommittedText(tier), folder);

    private static string RepositoryText(string path = "owner/repo") => string.Concat(Uri.UriSchemeHttps, "://", "example.test/", path);

    private static string ReportConfigJson(string url) =>
        new JsonObject { ["AgentJoules"] = new JsonObject { ["Report"] = new JsonObject { ["RepositoryUrl"] = url } } }.ToJsonString();

    private static void AssertNothingWritten(TempWorkspace workspace, string outFolder)
    {
        Assert.False(Directory.Exists(Path.Combine(workspace.Root, outFolder)), "the output folder was created");
        Assert.False(Directory.Exists(Path.Combine(workspace.Root, "docs")), "the docs folder was created");
    }

    private static void AssertNoPathShown(TempWorkspace workspace, CliRunResult result)
    {
        string shown = result.Stdout + result.Stderr;

        Assert.DoesNotContain(workspace.Root, shown, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ConfigurationFixtures.RepositoryRoot(), shown, StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex("<text[^>]*>N/A</text>")]
    private static partial Regex NotAvailableMarker();

    [GeneratedRegex("<text[^>]*>not run</text>")]
    private static partial Regex NotRunMarker();

    [GeneratedRegex(@"\S{8,}")]
    private static partial Regex NonSpaceRun();

    [GeneratedRegex("[A-Za-z][A-Za-z0-9-]{9,}")]
    private static partial Regex WordRun();

    [GeneratedRegex(@"^\s{2}(--[a-z-]+)", RegexOptions.Multiline)]
    private static partial Regex HelpOption();

    [Fact]
    [Trait("Spec", "REQ-016")]
    public async Task The_report_command_writes_the_three_files_from_the_fixtures()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();

        CliRunResult result = await ReportAsync(workspace, "--in", FixtureFolder(), "--out", "out");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Stderr);
        Assert.Equal(["Report written to out: summary.md, joules-per-step.svg, index.html"], OutputLines(result.Stdout));
        Assert.Equal(ReportOutputNames.Order(StringComparer.Ordinal), Directory.GetFiles(Path.Combine(workspace.Root, "out")).Select(static file => Path.GetFileName(file)).Order(StringComparer.Ordinal));
        Assert.All(ReportOutputNames, name => Assert.True(new FileInfo(Path.Combine(workspace.Root, "out", name)).Length > 0, name));
        AssertNoPathShown(workspace, result);

        // docs does not exist, so no marker is created and no docs folder appears.
        Assert.False(Directory.Exists(Path.Combine(workspace.Root, "docs")));
    }

    [Fact]
    [Trait("Spec", "REQ-016")]
    public async Task Fixture_report_has_24_NA_markers()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        Assert.Equal(0, (await ReportAsync(workspace, "--in", FixtureFolder(), "--out", "out")).ExitCode);

        string svg = File.ReadAllText(Path.Combine(workspace.Root, "out", "joules-per-step.svg"));
        string html = File.ReadAllText(Path.Combine(workspace.Root, "out", "index.html"));

        // Twelve groups (three tasks, four agents); the nvidia and nebius slots of each carry one N/A marker per metric-less cell, the cloudgpu slot is not run.
        Assert.Equal(12, SvgGroups(XDocument.Parse(svg)).Length);
        Assert.Equal(24, NotAvailableMarker().Count(svg));
        Assert.Equal(12, NotRunMarker().Count(svg));
        Assert.Equal(0, Occurrences(svg, "<script"));
        Assert.Equal(0, Occurrences(html, "<script"));
        Assert.True(Occurrences(html, ReportHtmlRenderer.ChartFileName) >= 1);
        Assert.True(Occurrences(html, ReportSentences.GpuBoardOnly) >= 1);
    }

    [Fact]
    [Trait("Spec", "REQ-016")]
    public async Task Hosted_slots_have_no_rect()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        Assert.Equal(0, (await ReportAsync(workspace, "--in", FixtureFolder(), "--out", "out")).ExitCode);

        XElement[] groups = SvgGroups(XDocument.Load(Path.Combine(workspace.Root, "out", "joules-per-step.svg")));

        Assert.Equal(12, groups.Length);
        foreach (XElement group in groups)
        {
            Assert.Single(SlotOfGroup(group, "local").Elements(SvgNs + "rect"));
            foreach (string hosted in new[] { "nvidia", "nebius" })
            {
                XElement slot = SlotOfGroup(group, hosted);

                Assert.Empty(slot.Elements(SvgNs + "rect"));
                Assert.Equal(["N/A"], SvgTexts(slot, "marker"));
            }

            XElement cloud = SlotOfGroup(group, "cloudgpu");
            Assert.Empty(cloud.Elements(SvgNs + "rect"));
            Assert.Equal(["not run"], SvgTexts(cloud, "marker"));
        }
    }

    [Fact]
    [Trait("Spec", "REQ-016")]
    public async Task Two_runs_identical_sha256()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();

        Assert.Equal(0, (await ReportAsync(workspace, "--in", FixtureFolder(), "--out", "first")).ExitCode);
        Assert.Equal(0, (await ReportAsync(workspace, "--in", FixtureFolder(), "--out", "second")).ExitCode);
        string[] first = ReportSums(Path.Combine(workspace.Root, "first"));
        string[] second = ReportSums(Path.Combine(workspace.Root, "second"));

        // A third run over the same folder replaces the files and leaves the same bytes.
        Assert.Equal(0, (await ReportAsync(workspace, "--in", FixtureFolder(), "--out", "first")).ExitCode);

        Assert.Equal(3, first.Length);
        Assert.Equal(first, second);
        Assert.Equal(first, ReportSums(Path.Combine(workspace.Root, "first")));
        Assert.Equal(3, Directory.GetFiles(Path.Combine(workspace.Root, "first")).Length);
    }

    [Fact]
    [Trait("Spec", "REQ-016")]
    public async Task Help_lists_in_out_config_only()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();

        CliRunResult help = await ReportAsync(workspace, "--help");

        Assert.Equal(0, help.ExitCode);
        Assert.DoesNotContain("(default results)", help.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("(default docs/report)", help.Stdout, StringComparison.Ordinal);
        Assert.Equal(["--config", "--in", "--out"], HelpOption().Matches(help.Stdout).Select(match => match.Groups[1].Value).Order(StringComparer.Ordinal));
        foreach (string later in new[] { "--update-readme", "--verify-readme", "--render-content" })
        {
            Assert.DoesNotContain(later, help.Stdout, StringComparison.Ordinal);

            CliRunResult refused = await ReportAsync(workspace, later);

            Assert.Equal(2, refused.ExitCode);
            Assert.Contains("(ParseError)", refused.Stderr, StringComparison.Ordinal);
        }

        AssertNothingWritten(workspace, "docs");
    }

    [Fact]
    [Trait("Spec", "REQ-016")]
    public async Task In_takes_one_value_per_occurrence()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();

        string[][] refused = [["--in", FixtureFolder(), FixtureFolder()], ["--in"]];
        foreach (string[] arguments in refused)
        {
            CliRunResult result = await ReportAsync(workspace, arguments);

            Assert.Equal(2, result.ExitCode);
            Assert.Contains("(ParseError)", result.Stderr, StringComparison.Ordinal);
        }

        AssertNothingWritten(workspace, "docs");
    }

    [Fact]
    [Trait("Spec", "REQ-016")]
    public async Task Missing_in_exits_2()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();

        CliRunResult named = await ReportAsync(workspace, "--in", "no-such-folder", "--out", "out");
        CliRunResult byDefault = await ReportAsync(workspace, "--out", "out");

        Assert.Equal(2, named.ExitCode);
        Assert.Single(named.ErrorLines);
        Assert.Contains("\"no-such-folder\"", named.ErrorLines[0], StringComparison.Ordinal);
        Assert.Equal(2, byDefault.ExitCode);
        Assert.Contains("\"results\"", byDefault.ErrorLines[0], StringComparison.Ordinal);
        AssertNothingWritten(workspace, "out");
        AssertNoPathShown(workspace, named);
    }

    [Fact]
    [Trait("Spec", "REQ-016")]
    public async Task Schema_invalid_file_exits_2_naming_it_and_writes_nothing()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string name = RunDocumentFixtures.CommittedFileName(Tier.Local);
        string text = RunDocumentFixtures.Edit(RunDocumentFixtures.CommittedText(Tier.Local), root =>
        {
            JsonObject cost = root["steps"]![0]!["cost"]!.AsObject();
            cost["label"] = "N/A";
            cost["value"] = 0.5;
        });
        _ = Put(workspace, name, text);

        CliRunResult result = await ReportAsync(workspace, "--in", Folder, "--out", "out");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains(OutputLines(result.Stdout), line => line.StartsWith(name + ": /steps/0/cost", StringComparison.Ordinal));
        Assert.Single(result.ErrorLines);
        Assert.Contains("report: 1 run file failed validation", result.ErrorLines[0], StringComparison.Ordinal);
        Assert.DoesNotContain("(ParseError)", result.Stderr, StringComparison.Ordinal);
        AssertNothingWritten(workspace, "out");
        AssertNoPathShown(workspace, result);
    }

    [Fact]
    [Trait("Spec", "REQ-016")]
    public async Task Only_the_selected_files_are_validated()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = PutCommitted(workspace, Folder, Tier.Local);

        // An older file of the tier is never opened, so a broken one cannot fail the report.
        _ = Put(workspace, "20251231T000000Z-local.json", Text(tail: ",\"steps\":[{\"bogus\":1}]"));
        CliRunResult result = await ReportAsync(workspace, "--in", Folder, "--out", "out");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Stderr);
        Assert.Equal(["Report written to out: summary.md, joules-per-step.svg, index.html"], OutputLines(result.Stdout));
    }

    [Fact]
    [Trait("Spec", "REQ-016")]
    public async Task Problem_header_exits_2()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string name = RunDocumentFixtures.CommittedFileName(Tier.Nvidia);
        _ = PutCommitted(workspace, Folder, Tier.Local);
        _ = Put(workspace, name, "not json");

        CliRunResult result = await ReportAsync(workspace, "--in", Folder, "--out", "out");

        Assert.Equal(2, result.ExitCode);
        Assert.Single(OutputLines(result.Stdout));
        Assert.StartsWith(name + ": ", OutputLines(result.Stdout)[0], StringComparison.Ordinal);
        Assert.Equal("error: report: 1 run file cannot be used (ConfigurationException)", Assert.Single(result.ErrorLines));
        AssertNothingWritten(workspace, "out");
        AssertNoPathShown(workspace, result);
    }

    [Fact]
    [Trait("Spec", "REQ-016")]
    public async Task The_same_file_name_in_two_inputs_is_a_problem()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = PutCommitted(workspace, "a", Tier.Local);
        _ = PutCommitted(workspace, "b", Tier.Local);
        string name = RunDocumentFixtures.CommittedFileName(Tier.Local);

        CliRunResult result = await ReportAsync(workspace, "--in", "a", "--in", "b", "--out", "out");

        Assert.Equal(2, result.ExitCode);
        Assert.Equal([name + ": duplicate-file-name"], OutputLines(result.Stdout));
        AssertNothingWritten(workspace, "out");
    }

    [Fact]
    [Trait("Spec", "REQ-016")]
    public async Task Repeated_in_combines()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = PutCommitted(workspace, "a", Tier.Local);
        _ = PutCommitted(workspace, "b", Tier.Nvidia);
        _ = PutCommitted(workspace, "b", Tier.Nebius);

        CliRunResult combined = await ReportAsync(workspace, "--in", "a", "--in", "b", "--out", "combined");
        CliRunResult once = await ReportAsync(workspace, "--in", FixtureFolder(), "--out", "once");
        CliRunResult twice = await ReportAsync(workspace, "--in", FixtureFolder(), "--in", FixtureFolder(), "--out", "twice");
        string summary = File.ReadAllText(Path.Combine(workspace.Root, "combined", "summary.md"));

        int[] exitCodes = [combined.ExitCode, once.ExitCode, twice.ExitCode];
        Assert.Equal([0, 0, 0], exitCodes);
        Assert.Contains("| cloudgpu | not run |", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("| local | not run |", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("| nvidia | not run |", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("| nebius | not run |", summary, StringComparison.Ordinal);
        Assert.Equal(ReportSums(Path.Combine(workspace.Root, "once")), ReportSums(Path.Combine(workspace.Root, "combined")));
        Assert.Equal(ReportSums(Path.Combine(workspace.Root, "once")), ReportSums(Path.Combine(workspace.Root, "twice")));
    }

    [Fact]
    [Trait("Spec", "REQ-016")]
    public async Task A_named_run_file_is_read_and_a_file_that_is_not_a_run_file_exits_2()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string local = Path.Combine(FixtureFolder(), RunDocumentFixtures.CommittedFileName(Tier.Local));
        _ = Put(workspace, "notes.json", "{}");

        CliRunResult single = await ReportAsync(workspace, "--in", local, "--out", "out");
        CliRunResult notRun = await ReportAsync(workspace, "--in", "runs/notes.json", "--out", "other");
        CliRunResult stray = await ReportAsync(workspace, "--in", Path.Combine(workspace.Root, "runs", "notes.json"), "--out", "other");
        string summary = File.ReadAllText(Path.Combine(workspace.Root, "out", "summary.md"));

        Assert.Equal(0, single.ExitCode);
        Assert.DoesNotContain("| local | not run |", summary, StringComparison.Ordinal);
        Assert.Contains("| nvidia | not run |", summary, StringComparison.Ordinal);
        Assert.Equal(2, notRun.ExitCode);
        Assert.Equal(2, stray.ExitCode);
        Assert.Contains("notes.json", notRun.ErrorLines[0], StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(workspace.Root, "other")));
    }

    [Fact]
    [Trait("Spec", "REQ-016")]
    public async Task Empty_folder_renders_four_not_run()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = Directory.CreateDirectory(FolderOf(workspace));

        CliRunResult result = await ReportAsync(workspace, "--in", Folder, "--out", "out");
        string summary = File.ReadAllText(Path.Combine(workspace.Root, "out", "summary.md"));
        string svg = File.ReadAllText(Path.Combine(workspace.Root, "out", "joules-per-step.svg"));
        string html = File.ReadAllText(Path.Combine(workspace.Root, "out", "index.html"));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["Report written to out: summary.md, joules-per-step.svg, index.html"], OutputLines(result.Stdout));
        foreach (string tier in new[] { "local", "cloudgpu", "nvidia", "nebius" })
        {
            Assert.Contains($"| {tier} | not run |", summary, StringComparison.Ordinal);
        }

        Assert.Equal(4, NotRunMarker().Count(svg));
        Assert.True(Occurrences(html, ReportSentences.NotRun) >= 4);
        Assert.Equal(0, NotAvailableMarker().Count(svg));
    }

    [Fact]
    [Trait("Spec", "REQ-016")]
    public async Task Skipped_files_are_listed_before_the_written_line_and_capped_at_20()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = PutCommitted(workspace, Folder, Tier.Local);
        string[] names = [.. Enumerable.Range(1, 25).Select(day => $"202602{day:D2}T000000Z-local.json")];
        foreach (string name in names)
        {
            _ = Put(workspace, name, Text(status: "failed"));
        }

        CliRunResult result = await ReportAsync(workspace, "--in", Folder, "--out", "out");
        string[] lines = OutputLines(result.Stdout);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(22, lines.Length);
        Assert.Equal([.. names.Take(20).Select(name => $"Skipped {name}: status-failed")], lines[..20]);
        Assert.Equal("Skipped 5 more files", lines[20]);
        Assert.Equal("Report written to out: summary.md, joules-per-step.svg, index.html", lines[21]);
    }

    [Fact]
    [Trait("Spec", "REQ-016")]
    public async Task A_run_file_that_disagrees_with_its_name_exits_2_and_writes_nothing()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();

        // The document starts at 2026-01-01T00:00:00Z; the name says another second.
        _ = Put(workspace, "20260101T000001Z-local.json", RunDocumentFixtures.CommittedText(Tier.Local));

        CliRunResult result = await ReportAsync(workspace, "--in", Folder, "--out", "out");

        Assert.Equal(2, result.ExitCode);
        Assert.Equal("error: report: 1 run file cannot be used (ConfigurationException)", Assert.Single(result.ErrorLines));
        string line = Assert.Single(OutputLines(result.Stdout));
        Assert.StartsWith("20260101T000001Z-local.json: ", line, StringComparison.Ordinal);
        Assert.DoesNotContain("(Parameter", line + result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("runs[", line, StringComparison.Ordinal);
        AssertNothingWritten(workspace, "out");
    }

    [Fact]
    [Trait("Spec", "REQ-016")]
    public async Task Explicit_missing_config_exits_2()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = Directory.CreateDirectory(Path.Combine(workspace.Root, "config-folder"));

        CliRunResult missing = await ReportAsync(workspace, "--in", FixtureFolder(), "--out", "out", "--config", "nope.json");
        CliRunResult folder = await ReportAsync(workspace, "--in", FixtureFolder(), "--out", "out", "--config", "config-folder");

        Assert.Equal(2, missing.ExitCode);
        Assert.Single(missing.ErrorLines);
        Assert.Contains("nope.json", missing.ErrorLines[0], StringComparison.Ordinal);
        Assert.Equal(2, folder.ExitCode);
        Assert.Contains("config-folder", folder.ErrorLines[0], StringComparison.Ordinal);
        AssertNothingWritten(workspace, "out");
        AssertNoPathShown(workspace, missing);
        AssertNoPathShown(workspace, folder);
    }

    [Fact]
    [Trait("Spec", "REQ-016")]
    public async Task Out_is_a_file_exits_2()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string file = workspace.WriteFile("out", "keep");

        CliRunResult result = await ReportAsync(workspace, "--in", FixtureFolder(), "--out", "out");

        Assert.Equal(2, result.ExitCode);
        Assert.Single(result.ErrorLines);
        Assert.Contains("\"out\"", result.ErrorLines[0], StringComparison.Ordinal);
        Assert.Equal("keep", File.ReadAllText(file));
        Assert.False(Directory.Exists(Path.Combine(workspace.Root, "docs")));
        AssertNoPathShown(workspace, result);
    }

    [Fact]
    [Trait("Spec", "REQ-016")]
    public async Task The_written_files_are_utf8_without_bom_with_lf_and_other_files_are_left_alone()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string keep = workspace.WriteFile(Path.Combine("out", "keep.txt"), "keep");
        _ = workspace.WriteFile(Path.Combine("out", "summary.md"), "old");

        CliRunResult result = await ReportAsync(workspace, "--in", FixtureFolder(), "--out", "out");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("keep", File.ReadAllText(keep));
        Assert.Equal(["index.html", "joules-per-step.svg", "keep.txt", "summary.md"], Directory.GetFiles(Path.Combine(workspace.Root, "out")).Select(static file => Path.GetFileName(file)).Order(StringComparer.Ordinal));
        foreach (string name in ReportOutputNames)
        {
            byte[] bytes = File.ReadAllBytes(Path.Combine(workspace.Root, "out", name));

            Assert.False(bytes.AsSpan().StartsWith(ByteOrderMark), name + " starts with a byte order mark");
            Assert.DoesNotContain((byte)'\r', bytes);
            Assert.Equal((byte)'\n', bytes[^1]);
            Assert.NotEqual((byte)'\n', bytes[^2]);
            _ = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }

        // The page and the chart are ASCII by construction.
        foreach (string name in ReportOutputNames.Skip(1))
        {
            Assert.All(File.ReadAllBytes(Path.Combine(workspace.Root, "out", name)), value => Assert.True(value < 0x80, name));
        }
    }

    [Fact]
    [Trait("Spec", "REQ-016")]
    public async Task A_write_failure_exits_1_naming_the_file_only()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();

        // A folder where the summary belongs: the move onto it fails.
        _ = Directory.CreateDirectory(Path.Combine(workspace.Root, "out", "summary.md"));

        CliRunResult result = await ReportAsync(workspace, "--in", FixtureFolder(), "--out", "out");

        Assert.Equal(1, result.ExitCode);
        Assert.Single(result.ErrorLines);
        Assert.Contains("summary.md", result.ErrorLines[0], StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(Path.Combine(workspace.Root, "out")));
        AssertNoPathShown(workspace, result);
    }

    [Fact]
    [Trait("Spec", "REQ-030")]
    public async Task Default_out_writes_docs_report_and_the_nojekyll_marker()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        foreach (Tier tier in new[] { Tier.Local, Tier.Nvidia, Tier.Nebius })
        {
            _ = PutCommitted(workspace, "results", tier);
        }

        CliRunResult result = await ReportAsync(workspace);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["Report written to docs/report: summary.md, joules-per-step.svg, index.html"], OutputLines(result.Stdout));
        Assert.All(ReportOutputNames, name => Assert.True(File.Exists(Path.Combine(workspace.Root, "docs", "report", name)), name));
        Assert.Equal(0, new FileInfo(Path.Combine(workspace.Root, "docs", NoJekyllWriter.FileName)).Length);
    }

    [Fact]
    [Trait("Spec", "REQ-030")]
    public async Task The_nojekyll_marker_follows_the_docs_folder_whatever_out_is()
    {
        using TempWorkspace without = TempWorkspace.CreateEmpty();
        using TempWorkspace existing = TempWorkspace.CreateEmpty();
        _ = Directory.CreateDirectory(Path.Combine(existing.Root, "docs"));
        string kept = existing.WriteFile(Path.Combine("docs", ".nojekyll"), string.Empty);
        File.SetLastWriteTimeUtc(kept, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        using TempWorkspace created = TempWorkspace.CreateEmpty();
        _ = Directory.CreateDirectory(Path.Combine(created.Root, "docs"));

        Assert.Equal(0, (await ReportAsync(without, "--in", FixtureFolder(), "--out", "elsewhere")).ExitCode);
        Assert.Equal(0, (await ReportAsync(existing, "--in", FixtureFolder(), "--out", "elsewhere")).ExitCode);
        Assert.Equal(0, (await ReportAsync(created, "--in", FixtureFolder(), "--out", "elsewhere")).ExitCode);

        Assert.False(Directory.Exists(Path.Combine(without.Root, "docs")));
        Assert.Equal(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), File.GetLastWriteTimeUtc(kept));
        Assert.Equal(0, new FileInfo(Path.Combine(created.Root, "docs", NoJekyllWriter.FileName)).Length);
    }

    [Fact]
    [Trait("Spec", "REQ-030")]
    public async Task A_nojekyll_marker_that_cannot_be_created_exits_2_after_the_files_are_written()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = Directory.CreateDirectory(Path.Combine(workspace.Root, "docs", NoJekyllWriter.FileName));

        CliRunResult result = await ReportAsync(workspace, "--in", FixtureFolder(), "--out", "out");

        Assert.Equal(2, result.ExitCode);
        Assert.Single(result.ErrorLines);
        Assert.Contains(NoJekyllWriter.FileName, result.ErrorLines[0], StringComparison.Ordinal);
        Assert.All(ReportOutputNames, name => Assert.True(File.Exists(Path.Combine(workspace.Root, "out", name)), name));
        AssertNoPathShown(workspace, result);
    }

    [Fact]
    [Trait("Spec", "REQ-030")]
    public async Task The_repository_link_comes_from_the_default_or_the_named_configuration_file()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string link = "href=\"" + RepositoryText() + "#readme\">" + ReportHtmlRenderer.RepositoryLinkText + "</a>";

        // No configuration file at all: no link and the sentence that says so.
        Assert.Equal(0, (await ReportAsync(workspace, "--in", FixtureFolder(), "--out", "none")).ExitCode);
        string none = File.ReadAllText(Path.Combine(workspace.Root, "none", "index.html"));

        _ = workspace.WriteFile(Path.Combine("config", "appsettings.json"), ReportConfigJson(RepositoryText()));
        Assert.Equal(0, (await ReportAsync(workspace, "--in", FixtureFolder(), "--out", "default")).ExitCode);
        string byDefault = File.ReadAllText(Path.Combine(workspace.Root, "default", "index.html"));

        _ = workspace.WriteFile(Path.Combine("elsewhere", "settings.json"), ReportConfigJson(RepositoryText("other/repo")));
        Assert.Equal(0, (await ReportAsync(workspace, "--in", FixtureFolder(), "--out", "named", "--config", Path.Combine("elsewhere", "settings.json"))).ExitCode);
        string named = File.ReadAllText(Path.Combine(workspace.Root, "named", "index.html"));

        Assert.Contains(ReportHtmlRenderer.RepositoryNotConfigured, none, StringComparison.Ordinal);
        Assert.DoesNotContain("<a href", none, StringComparison.Ordinal);
        Assert.Contains(link, byDefault, StringComparison.Ordinal);
        Assert.DoesNotContain(ReportHtmlRenderer.RepositoryNotConfigured, byDefault, StringComparison.Ordinal);
        Assert.Contains("other/repo#readme", named, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Spec", "REQ-030")]
    public async Task An_environment_variable_sets_the_link_only_when_the_overrides_include_the_process_environment()
    {
        const string variableName = "AGENTJOULES__Report__RepositoryUrl";
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string? before = Environment.GetEnvironmentVariable(variableName);
        try
        {
            Environment.SetEnvironmentVariable(variableName, RepositoryText("from/env"));

            CliRunResult isolated = await ReportAsync(workspace, "--in", FixtureFolder(), "--out", "isolated");
            CliRunResult included = await CliHarness.RunTokensAsync(
                ["report", "--in", FixtureFolder(), "--out", "included"],
                new CliHostOverrides(IncludeProcessEnvironment: true, WorkingDirectory: workspace.Root));

            Assert.Equal(0, isolated.ExitCode);
            Assert.Equal(0, included.ExitCode);
            Assert.DoesNotContain("from/env", File.ReadAllText(Path.Combine(workspace.Root, "isolated", "index.html")), StringComparison.Ordinal);
            Assert.Contains("from/env#readme", File.ReadAllText(Path.Combine(workspace.Root, "included", "index.html")), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, before);
        }
    }

    [Fact]
    [Trait("Spec", "REQ-030")]
    public async Task An_invalid_repository_url_exits_2_naming_the_key_never_the_value()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string key = FakeKey();
        string[] values =
        [
            string.Concat(Uri.UriSchemeHttp, "://", "example.test/owner/repo"),
            string.Concat(Uri.UriSchemeHttps, "://", "user:", key, "@example.test/owner/repo"),
            RepositoryText("owner/repo?token=" + key),
            RepositoryText("owner/repo?"),
            RepositoryText("owner/repo#" + key),
            "owner/" + key,
            string.Concat(Uri.UriSchemeFtp, "://", "example.test/", key),
        ];

        foreach (string value in values)
        {
            _ = workspace.WriteFile(Path.Combine("config", "appsettings.json"), ReportConfigJson(value));

            CliRunResult result = await ReportAsync(workspace, "--in", FixtureFolder(), "--out", "out");

            Assert.Equal(2, result.ExitCode);
            Assert.Single(result.ErrorLines);
            Assert.Contains("Report:RepositoryUrl", result.ErrorLines[0], StringComparison.Ordinal);
            Assert.DoesNotContain(key, result.Stdout + result.Stderr, StringComparison.Ordinal);
            Assert.DoesNotContain("example.test", result.Stdout + result.Stderr, StringComparison.Ordinal);
            AssertNothingWritten(workspace, "out");
        }
    }

    [Fact]
    [Trait("Spec", "REQ-030")]
    public async Task The_configuration_file_is_read_for_the_report_section_only()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();

        // A rate below 10 Hz and a judge tier that Tiers does not hold fail the run command's checks; report does not make them.
        _ = workspace.WriteFile(
            Path.Combine("config", "appsettings.json"),
            new JsonObject { ["AgentJoules"] = new JsonObject { ["Meter"] = new JsonObject { ["SampleRateHz"] = 1 }, ["Judge"] = new JsonObject { ["Tier"] = "bogus" } } }.ToJsonString());
        CliRunResult accepted = await ReportAsync(workspace, "--in", FixtureFolder(), "--out", "out");

        // A key the options do not have is still refused by the shape check.
        _ = workspace.WriteFile(
            Path.Combine("config", "appsettings.json"),
            new JsonObject { ["AgentJoules"] = new JsonObject { ["Report"] = new JsonObject { ["Bogus"] = "x" } } }.ToJsonString());
        CliRunResult unknown = await ReportAsync(workspace, "--in", FixtureFolder(), "--out", "other");

        // A file that is not JSON is refused naming the file, never the path.
        _ = workspace.WriteFile(Path.Combine("config", "appsettings.json"), "{ not json");
        CliRunResult broken = await ReportAsync(workspace, "--in", FixtureFolder(), "--out", "third");

        Assert.Equal(0, accepted.ExitCode);
        Assert.Equal(2, unknown.ExitCode);
        Assert.Contains("Report:Bogus", unknown.ErrorLines[0], StringComparison.Ordinal);
        Assert.Equal(2, broken.ExitCode);
        Assert.Contains("appsettings.json", broken.ErrorLines[0], StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(workspace.Root, "other")));
        Assert.False(Directory.Exists(Path.Combine(workspace.Root, "third")));
        AssertNoPathShown(workspace, broken);
    }

    // ---- AJ-086 fix round: keys, staging, echoes, configuration bounds, counts ----

    private const string KeyVariable = "NEBIUS_API_KEY";
    private const string KeyAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789";

    // A runtime-built value the process knows as a key: registered through the overrides, the way the keys of the environment are.
    private static Task<CliRunResult> ReportWithKeyAsync(TempWorkspace workspace, string key, params string[] arguments) =>
        CliHarness.RunTokensAsync(
            ["report", .. arguments],
            new CliHostOverrides(Configuration: new Dictionary<string, string?> { [KeyVariable] = key }, WorkingDirectory: workspace.Root));

    private static string LongKey(int length) => string.Concat(Enumerable.Range(0, length).Select(index => KeyAlphabet[((index * index) + (7 * index) + (index / 5)) % KeyAlphabet.Length]));

    private static void AssertNoKeyRun(string shown, string key)
    {
        for (int start = 0; start + 20 <= key.Length; start++)
        {
            Assert.DoesNotContain(key.Substring(start, 20), shown, StringComparison.Ordinal);
        }
    }

    private static string RunFileWithGpuName(Tier tier, string gpuName) =>
        RunDocumentFixtures.Edit(RunDocumentFixtures.CommittedText(tier), root => root["environment"]!["gpuName"] = gpuName);

    private static string CorruptedCostText(Tier tier) =>
        RunDocumentFixtures.Edit(RunDocumentFixtures.CommittedText(tier), root =>
        {
            JsonObject cost = root["steps"]![0]!["cost"]!.AsObject();
            cost["label"] = "N/A";
            cost["value"] = 0.5;
        });

    private sealed class CancellingWriter(CancellationTokenSource source) : StringWriter
    {
        public override void Write(char value)
        {
            base.Write(value);
            source.Cancel();
        }

        public override void Write(string? value)
        {
            base.Write(value);
            source.Cancel();
        }
    }

    [Fact]
    [Trait("Spec", "REQ-016")]
    public async Task A_run_file_that_holds_a_registered_key_exits_2_naming_it_and_writes_nothing()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string key = ProviderClientFixtures.FakeKey();
        string name = RunDocumentFixtures.CommittedFileName(Tier.Local);
        _ = PutCommitted(workspace, Folder, Tier.Nvidia);
        _ = Put(workspace, name, RunFileWithGpuName(Tier.Local, "GPU " + key));

        CliRunResult result = await ReportWithKeyAsync(workspace, key, "--in", Folder, "--out", "out");

        Assert.Equal(2, result.ExitCode);
        Assert.Equal([name + ": (root) holds a registered key"], OutputLines(result.Stdout));
        Assert.Equal("error: report: 1 run file failed validation (ConfigurationException)", Assert.Single(result.ErrorLines));
        Assert.DoesNotContain(key, result.Stdout + result.Stderr, StringComparison.Ordinal);
        AssertNothingWritten(workspace, "out");
    }

    [Fact]
    [Trait("Spec", "REQ-016")]
    public async Task A_registered_key_with_an_underscore_is_refused_before_markdown_could_escape_it()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string key = string.Concat("zk7w", "_2qm9", "xv4t", "j8hd");
        string name = RunDocumentFixtures.CommittedFileName(Tier.Local);
        _ = Put(workspace, name, RunFileWithGpuName(Tier.Local, "GPU " + key));

        CliRunResult result = await ReportWithKeyAsync(workspace, key, "--in", Folder, "--out", "out");

        Assert.Equal(2, result.ExitCode);
        Assert.Equal([name + ": (root) holds a registered key"], OutputLines(result.Stdout));
        Assert.DoesNotContain(key, result.Stdout + result.Stderr, StringComparison.Ordinal);
        AssertNothingWritten(workspace, "out");
    }

    [Fact]
    [Trait("Spec", "REQ-016")]
    public async Task A_run_file_without_a_registered_key_is_rendered_byte_for_byte_whatever_keys_are_registered()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();

        CliRunResult plain = await ReportAsync(workspace, "--in", FixtureFolder(), "--out", "plain");
        CliRunResult keyed = await ReportWithKeyAsync(workspace, string.Concat("qh4m", "x9tz", "p2vc"), "--in", FixtureFolder(), "--out", "keyed");

        int[] exits = [plain.ExitCode, keyed.ExitCode];
        Assert.Equal([0, 0], exits);
        Assert.Equal(ReportSums(Path.Combine(workspace.Root, "plain")), ReportSums(Path.Combine(workspace.Root, "keyed")));
    }

    [Fact]
    [Trait("Spec", "REQ-030")]
    public async Task A_page_that_would_carry_a_registered_value_is_refused_not_masked()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        Assert.Equal(0, (await ReportAsync(workspace, "--in", FixtureFolder(), "--out", "plain")).ExitCode);
        string page = File.ReadAllText(Path.Combine(workspace.Root, "plain", "index.html"));

        // A value of the constant style of the page: no run file holds it, and the rendered page does.
        string style = page[(page.IndexOf("<style>", StringComparison.Ordinal) + "<style>".Length)..page.IndexOf("</style>", StringComparison.Ordinal)];
        string value = NonSpaceRun().Matches(style).Select(match => match.Value).OrderByDescending(text => text.Length).First();

        CliRunResult result = await ReportWithKeyAsync(workspace, value, "--in", FixtureFolder(), "--out", "keyed");

        Assert.Equal(2, result.ExitCode);
        string error = Assert.Single(result.ErrorLines);
        Assert.Matches(@"^error: report: (summary\.md|joules-per-step\.svg|index\.html) holds a registered key; nothing written \(ConfigurationException\)$", error);
        Assert.Equal(string.Empty, result.Stdout);
        Assert.False(Directory.Exists(Path.Combine(workspace.Root, "keyed")));
    }

    [Fact]
    [Trait("Spec", "REQ-016")]
    public async Task A_blocked_third_partial_leaves_the_old_files_intact_and_exits_1()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        foreach (string name in ReportOutputNames)
        {
            _ = workspace.WriteFile(Path.Combine("out", name), "old " + name);
        }

        _ = workspace.WriteFile(Path.Combine("out", "index.html.partial", "keep.txt"), "x");

        CliRunResult result = await ReportAsync(workspace, "--in", FixtureFolder(), "--out", "out");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("index.html", Assert.Single(result.ErrorLines), StringComparison.Ordinal);
        Assert.All(ReportOutputNames, name => Assert.Equal("old " + name, File.ReadAllText(Path.Combine(workspace.Root, "out", name))));
        Assert.False(File.Exists(Path.Combine(workspace.Root, "out", "summary.md.partial")));
        Assert.False(File.Exists(Path.Combine(workspace.Root, "out", "joules-per-step.svg.partial")));
        AssertNoPathShown(workspace, result);
    }

    [Fact]
    [Trait("Spec", "REQ-016")]
    public async Task A_stale_partial_file_is_replaced()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        Assert.Equal(0, (await ReportAsync(workspace, "--in", FixtureFolder(), "--out", "fresh")).ExitCode);
        foreach (string name in ReportOutputNames)
        {
            _ = workspace.WriteFile(Path.Combine("out", name + ".partial"), "junk");
        }

        CliRunResult result = await ReportAsync(workspace, "--in", FixtureFolder(), "--out", "out");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(ReportOutputNames.Order(StringComparer.Ordinal), Directory.GetFiles(Path.Combine(workspace.Root, "out")).Select(static file => Path.GetFileName(file)).Order(StringComparer.Ordinal));
        Assert.Equal(ReportSums(Path.Combine(workspace.Root, "fresh")), ReportSums(Path.Combine(workspace.Root, "out")));
    }

    [Fact]
    [Trait("Spec", "REQ-016")]
    public async Task Out_under_a_file_exits_1_without_a_path()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = workspace.WriteFile("plainfile", "x");

        CliRunResult result = await ReportAsync(workspace, "--in", FixtureFolder(), "--out", Path.Combine("plainfile", "sub"));

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("error: report: the output folder could not be created (AgentJoulesException)", Assert.Single(result.ErrorLines));
        AssertNoPathShown(workspace, result);
    }

    [Fact]
    [Trait("Spec", "REQ-016")]
    public async Task A_registered_key_in_a_path_is_masked_before_the_echo_is_cut()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string key = LongKey(300);
        string shortKey = LongKey(100);
        string prefix = new('d', 30);

        // --in and --config name a path that is not there; the echo of 256 characters would cut a key of 300 characters at offset 30.
        CliRunResult input = await ReportWithKeyAsync(workspace, key, "--in", prefix + key, "--out", "out");
        CliRunResult config = await ReportWithKeyAsync(workspace, key, "--in", FixtureFolder(), "--out", "out", "--config", prefix + key);

        // --out is echoed on success.
        CliRunResult output = await ReportWithKeyAsync(workspace, shortKey, "--in", FixtureFolder(), "--out", prefix + shortKey);

        // validate echoes its path argument in the same way.
        CliRunResult validate = await CliHarness.RunTokensAsync(
            ["validate", prefix + key],
            new CliHostOverrides(Configuration: new Dictionary<string, string?> { [KeyVariable] = key }, WorkingDirectory: workspace.Root));

        int[] exits = [input.ExitCode, config.ExitCode, output.ExitCode, validate.ExitCode];
        Assert.Equal([2, 2, 0, 2], exits);
        AssertNoKeyRun(input.Stdout + input.Stderr, key);
        AssertNoKeyRun(config.Stdout + config.Stderr, key);
        AssertNoKeyRun(output.Stdout + output.Stderr, shortKey);
        AssertNoKeyRun(validate.Stdout + validate.Stderr, key);
        Assert.Contains("does not exist", input.ErrorLines[0], StringComparison.Ordinal);
        Assert.Contains("does not exist", config.ErrorLines[0], StringComparison.Ordinal);
        Assert.Contains("Report written to " + prefix + "****: ", output.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Spec", "REQ-016")]
    public async Task The_refusal_of_the_builder_names_the_refused_file_not_the_first()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = PutCommitted(workspace, Folder, Tier.Local);

        // The second file in file order is the one the builder refuses: its name says another second than run.startUtc.
        _ = Put(workspace, "20260101T000001Z-nvidia.json", RunDocumentFixtures.CommittedText(Tier.Nvidia));

        CliRunResult result = await ReportAsync(workspace, "--in", Folder, "--out", "out");

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(["20260101T000001Z-nvidia.json: FileName has another timestamp than run.startUtc."], OutputLines(result.Stdout));
        Assert.Equal("error: report: 1 run file cannot be used (ConfigurationException)", Assert.Single(result.ErrorLines));
        AssertNothingWritten(workspace, "out");
    }

    [Fact]
    [Trait("Spec", "REQ-030")]
    public async Task A_repository_url_that_holds_a_registered_key_is_refused_before_any_run_file_is_read()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string key = ProviderClientFixtures.FakeKey();
        _ = workspace.WriteFile(Path.Combine("config", "appsettings.json"), ReportConfigJson(RepositoryText("owner/" + key)));

        // The input is a path that is not there: its own line would be the first failure if the run files were looked at before the URL.
        CliRunResult result = await ReportWithKeyAsync(workspace, key, "--in", "no-such-folder", "--out", "out");

        Assert.Equal(2, result.ExitCode);
        Assert.Equal($"error: {AgentJoules.Core.Configuration.ReportOptions.RepositoryUrlMessage} (ConfigurationException)", Assert.Single(result.ErrorLines));
        Assert.DoesNotContain("no-such-folder", result.Stdout + result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(key, result.Stdout + result.Stderr, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.Stdout);
        AssertNothingWritten(workspace, "out");

        // Without the key registered the same URL is a plain path and the report goes on to the input.
        CliRunResult unregistered = await ReportAsync(workspace, "--in", "no-such-folder", "--out", "out");

        Assert.Contains("no-such-folder", Assert.Single(unregistered.ErrorLines), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Spec", "REQ-030")]
    public async Task A_configuration_file_with_a_lone_surrogate_is_exit_2_naming_the_file()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = workspace.WriteFile(Path.Combine("config", "appsettings.json"), "{\"AgentJoules\":{\"Report\":{\"RepositoryUrl\":\"\\ud800\"}}}");

        CliRunResult result = await ReportAsync(workspace, "--in", FixtureFolder(), "--out", "out");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("appsettings.json", Assert.Single(result.ErrorLines), StringComparison.Ordinal);
        AssertNothingWritten(workspace, "out");
        AssertNoPathShown(workspace, result);
    }

    [Fact]
    [Trait("Spec", "REQ-030")]
    public async Task A_configuration_file_of_exactly_one_mebibyte_is_read_and_one_byte_more_is_refused()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        const string body = "{\"AgentJoules\":{}}";
        const int cap = 1024 * 1024;
        _ = workspace.WriteFile("exact.json", body + new string(' ', cap - body.Length));
        _ = workspace.WriteFile("over.json", body + new string(' ', cap - body.Length + 1));

        CliRunResult exact = await ReportAsync(workspace, "--in", FixtureFolder(), "--out", "exact", "--config", "exact.json");
        CliRunResult over = await ReportAsync(workspace, "--in", FixtureFolder(), "--out", "over", "--config", "over.json");

        Assert.Equal(0, exact.ExitCode);
        Assert.Equal(2, over.ExitCode);
        Assert.Equal("error: the configuration file \"over.json\" is larger than 1048576 bytes (ConfigurationException)", Assert.Single(over.ErrorLines));
        Assert.False(Directory.Exists(Path.Combine(workspace.Root, "over")));
    }

    [Fact]
    [Trait("Spec", "REQ-030")]
    public async Task A_configuration_file_that_cannot_be_opened_is_exit_2_naming_the_file_only()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string config = workspace.WriteFile(Path.Combine("config", "appsettings.json"), ReportConfigJson(RepositoryText()));
        CliRunResult result;
        using (new FileStream(config, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            result = await ReportAsync(workspace, "--in", FixtureFolder(), "--out", "out");
        }

        Assert.Equal(2, result.ExitCode);
        Assert.Equal("error: the configuration file \"appsettings.json\" could not be read (ConfigurationException)", Assert.Single(result.ErrorLines));
        AssertNothingWritten(workspace, "out");
        AssertNoPathShown(workspace, result);
    }

    [Fact]
    [Trait("Spec", "REQ-030")]
    public async Task The_environment_overrides_the_configuration_file()
    {
        const string variableName = "AGENTJOULES__Report__RepositoryUrl";
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = workspace.WriteFile(Path.Combine("config", "appsettings.json"), ReportConfigJson(RepositoryText("from/file")));
        string? before = Environment.GetEnvironmentVariable(variableName);
        try
        {
            Environment.SetEnvironmentVariable(variableName, RepositoryText("from/env"));

            CliRunResult result = await CliHarness.RunTokensAsync(
                ["report", "--in", FixtureFolder(), "--out", "out"],
                new CliHostOverrides(IncludeProcessEnvironment: true, WorkingDirectory: workspace.Root));
            string page = File.ReadAllText(Path.Combine(workspace.Root, "out", "index.html"));

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("from/env#readme", page, StringComparison.Ordinal);
            Assert.DoesNotContain("from/file", page, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, before);
        }
    }

    [Fact]
    [Trait("Spec", "REQ-030")]
    public void Every_repository_url_the_rule_accepts_is_accepted_by_the_page_renderer()
    {
        ReportModel model = ReportAggregator.Build([]);
        string[] candidates =
        [
            RepositoryText(),
            RepositoryText("owner/repo/"),
            RepositoryText("owner/repo.git"),
            RepositoryText("o%20w/r%2Fx"),
            RepositoryText("\u00e4/\u00fc"),
            string.Concat(Uri.UriSchemeHttps, "://", "EXAMPLE.test:8443/Owner/Repo"),
            string.Concat(Uri.UriSchemeHttps, "://", "b\u00fccher.test/owner/repo"),
            string.Concat(Uri.UriSchemeHttps, "://", "[::1]/owner/repo"),
            string.Concat(Uri.UriSchemeHttps, "://", "127.0.0.1/owner/repo"),
            string.Concat(Uri.UriSchemeHttps, "://", "localhost/owner/repo"),
            string.Concat(Uri.UriSchemeHttps, "://", "ex_ample.test/owner/repo"),
        ];

        int accepted = 0;
        foreach (string candidate in candidates)
        {
            if (AgentJoules.Core.Configuration.ReportOptions.IsRepositoryUrl(candidate, out Uri? uri))
            {
                accepted++;
                string html = ReportHtmlRenderer.Render(model, uri);

                Assert.Contains("#readme", html, StringComparison.Ordinal);
            }
        }

        // The renderer's own refusal is therefore not reachable through the command: the configuration rule is the same check, and it runs first.
        Assert.True(accepted >= 5, "the rule accepts the plain forms");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(20)]
    [InlineData(21)]
    [InlineData(25)]
    [Trait("Spec", "REQ-016")]
    public async Task Problem_lines_are_capped_at_20_and_the_tail_counts_the_rest(int count)
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string[] names = [.. Enumerable.Range(1, count).Select(day => $"202602{day:D2}T000000Z-local.json")];
        foreach (string name in names)
        {
            _ = Put(workspace, name, "not json");
        }

        CliRunResult result = await ReportAsync(workspace, "--in", Folder, "--out", "out");
        string[] lines = OutputLines(result.Stdout);
        int rest = count - 20;

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(Math.Min(count, 20) + (rest > 0 ? 1 : 0), lines.Length);
        Assert.Equal([.. names.Order(StringComparer.Ordinal).Take(20).Select(name => name + ": header-not-json")], lines[..Math.Min(count, 20)]);
        if (rest > 0)
        {
            Assert.Equal(rest == 1 ? "1 more file cannot be used" : $"{rest} more files cannot be used", lines[^1]);
        }

        Assert.Equal($"error: report: {count} run {(count == 1 ? "file" : "files")} cannot be used (ConfigurationException)", Assert.Single(result.ErrorLines));
        AssertNothingWritten(workspace, "out");
    }

    [Theory]
    [InlineData(20)]
    [InlineData(21)]
    [InlineData(22)]
    [Trait("Spec", "REQ-016")]
    public async Task Skipped_lines_are_capped_at_20_and_the_tail_counts_the_rest(int count)
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = PutCommitted(workspace, Folder, Tier.Local);
        string[] names = [.. Enumerable.Range(1, count).Select(day => $"202602{day:D2}T000000Z-local.json")];
        foreach (string name in names)
        {
            _ = Put(workspace, name, Text(status: "failed"));
        }

        CliRunResult result = await ReportAsync(workspace, "--in", Folder, "--out", "out");
        string[] lines = OutputLines(result.Stdout);
        int rest = count - 20;

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Math.Min(count, 20) + (rest > 0 ? 1 : 0) + 1, lines.Length);
        Assert.Equal([.. names.Take(20).Select(name => $"Skipped {name}: status-failed")], lines[..Math.Min(count, 20)]);
        if (rest > 0)
        {
            Assert.Equal(rest == 1 ? "Skipped 1 more file" : $"Skipped {rest} more files", lines[^2]);
        }

        Assert.Equal("Report written to out: summary.md, joules-per-step.svg, index.html", lines[^1]);
    }

    [Fact]
    [Trait("Spec", "REQ-016")]
    public async Task Every_failing_file_is_listed_in_ordinal_order_of_the_file_name()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        foreach (Tier tier in new[] { Tier.Nvidia, Tier.Local })
        {
            _ = Put(workspace, RunDocumentFixtures.CommittedFileName(tier), CorruptedCostText(tier));
        }

        _ = PutCommitted(workspace, Folder, Tier.Nebius);

        CliRunResult result = await ReportAsync(workspace, "--in", Folder, "--out", "out");
        string[] lines = OutputLines(result.Stdout);

        Assert.Equal(2, result.ExitCode);
        string local = RunDocumentFixtures.CommittedFileName(Tier.Local) + ": ";
        string nvidia = RunDocumentFixtures.CommittedFileName(Tier.Nvidia) + ": ";
        int lastLocal = Array.FindLastIndex(lines, line => line.StartsWith(local, StringComparison.Ordinal));
        int firstNvidia = Array.FindIndex(lines, line => line.StartsWith(nvidia, StringComparison.Ordinal));
        Assert.True(lastLocal >= 0 && firstNvidia > lastLocal, "the lines of the files are in ordinal order of the file name");
        Assert.All(lines, line => Assert.True(line.StartsWith(local, StringComparison.Ordinal) || line.StartsWith(nvidia, StringComparison.Ordinal), line));
        Assert.Contains(lines, line => line.StartsWith(local + "/steps/0/cost", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.StartsWith(nvidia + "/steps/0/cost", StringComparison.Ordinal));
        Assert.Equal("error: report: 2 run files failed validation (ConfigurationException)", Assert.Single(result.ErrorLines));
        AssertNothingWritten(workspace, "out");
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 2)]
    [Trait("Spec", "REQ-016")]
    public async Task A_run_file_is_read_up_to_16_MiB_and_one_byte_more_is_refused(int extra, int expectedExit)
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string name = RunDocumentFixtures.CommittedFileName(Tier.Local);
        byte[] text = Encoding.UTF8.GetBytes(RunDocumentFixtures.CommittedText(Tier.Local));
        byte[] padded = new byte[RunFileValidator.MaxBytes + extra];
        Array.Fill(padded, (byte)' ');
        text.CopyTo(padded, 0);
        string path = Path.Combine(workspace.Root, Folder, name);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, padded);

        CliRunResult result = await ReportAsync(workspace, "--in", Folder, "--out", "out");

        Assert.Equal(16777216, RunFileValidator.MaxBytes);
        Assert.Equal(expectedExit, result.ExitCode);
        if (extra > 0)
        {
            Assert.Equal([name + ": (root) is larger than 16777216 bytes"], OutputLines(result.Stdout));
            AssertNothingWritten(workspace, "out");
        }
    }

    [Fact]
    [Trait("Spec", "REQ-016")]
    public async Task A_run_file_that_is_not_valid_utf8_is_refused_naming_it()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string name = RunDocumentFixtures.CommittedFileName(Tier.Local);
        string text = RunFileWithGpuName(Tier.Local, "GPU XYZ");
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        bytes[text.IndexOf("GPU XYZ", StringComparison.Ordinal) + 4] = 0xFF;
        string path = Path.Combine(workspace.Root, Folder, name);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);

        CliRunResult result = await ReportAsync(workspace, "--in", Folder, "--out", "out");

        Assert.Equal(2, result.ExitCode);
        Assert.Equal([name + ": (root) is not valid UTF-8"], OutputLines(result.Stdout));
        AssertNothingWritten(workspace, "out");
    }

    [Fact]
    [Trait("Spec", "REQ-016")]
    public async Task Cancellation_while_the_files_are_read_exits_5_and_the_next_file_is_never_read()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        foreach (Tier tier in new[] { Tier.Nvidia, Tier.Local })
        {
            _ = Put(workspace, RunDocumentFixtures.CommittedFileName(tier), CorruptedCostText(tier));
        }

        using var caller = new CancellationTokenSource();
        using var writer = new CancellingWriter(caller);

        // The writer cancels the token on the first line the command prints, which is a line of the first file in file order.
        CliRunResult result = await CliHarness.RunTokensAsync(
            ["report", "--in", Folder, "--out", "out"],
            new CliHostOverrides(WorkingDirectory: workspace.Root),
            stdout: writer,
            cancellationToken: caller.Token);

        Assert.Equal(5, result.ExitCode);
        Assert.Equal("error: interrupted by shutdown (OperationCanceledException)", Assert.Single(result.ErrorLines));
        Assert.All(OutputLines(writer.ToString()), line => Assert.StartsWith(RunDocumentFixtures.CommittedFileName(Tier.Local) + ": ", line, StringComparison.Ordinal));
        AssertNothingWritten(workspace, "out");
    }

    [Theory]
    [InlineData("summary.md")]
    [InlineData("joules-per-step.svg")]
    [InlineData("index.html")]
    [Trait("Spec", "REQ-030")]
    public async Task Each_rendered_file_that_would_carry_a_registered_value_is_refused_not_masked(string target)
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        Assert.Equal(0, (await ReportAsync(workspace, "--in", FixtureFolder(), "--out", "plain")).ExitCode);
        string Read(string name) => File.ReadAllText(Path.Combine(workspace.Root, "plain", name));
        string others = string.Concat(ReportOutputNames.Where(name => name != target).Select(Read))
            + string.Concat(Directory.GetFiles(FixtureFolder()).Select(file => File.ReadAllText(file)));

        // A word or a line that only this rendered file holds: no run file and none of the other two texts contains it.
        string content = Read(target);
        string value = WordRun().Matches(content)
            .Select(match => match.Value)
            .Concat(content.Split('\n').Select(static line => line.Trim()))
            .Where(piece => piece.Length >= 10 && !piece.Contains('*', StringComparison.Ordinal) && !others.Contains(piece, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .First();

        CliRunResult result = await ReportWithKeyAsync(workspace, value, "--in", FixtureFolder(), "--out", "keyed");

        Assert.Equal(2, result.ExitCode);
        Assert.Equal($"error: report: {target} holds a registered key; nothing written (ConfigurationException)", Assert.Single(result.ErrorLines));
        Assert.False(Directory.Exists(Path.Combine(workspace.Root, "keyed")));
    }
}