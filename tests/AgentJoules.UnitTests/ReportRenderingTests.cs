using System.Globalization;
using System.Reflection;
using System.Text;

using AgentJoules.Core.Configuration;
using AgentJoules.Core.Energy;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Reporting.Render;
using AgentJoules.Reporting.Results;
using AgentJoules.TestSupport;

using Xunit;

namespace AgentJoules.UnitTests;

/// <summary>
/// AJ-081 (REQ-016): the run files a report may use. The header reader returns the tier, the status and the flags of the <c>run</c> object and one reason token for a header it cannot read; the selector
/// returns, per tier and per variant, the newest file by name whose header is accepted (completed or partial, not <c>non-publishable</c>), never opens a probe, calibration or non-matching file, reads only the
/// header, and reports an unreadable header or a tier that differs from the name as a problem, never as a tier that was not run.
/// AJ-082 (REQ-016): the aggregation of repeats into report rows. A row is (tier, variant, task, agent) over the <c>ok</c> steps of one run file; each value cell holds the median of the repeats that have a value
/// with the range and <c>n</c>, keeps their label, unit and currency, and is N/A or not-reported with a null value when no repeat has one; rows of different tiers or variants are never merged, ranked or compared.
/// </summary>
[Trait("Spec", "REQ-016")]
public sealed class ReportRenderingTests
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

    private const string Stamp = "20261023T093000Z";
    private const string TaskName = "summarise";
    private const string Planner = "Planner";

    private static readonly string[] CrossTierWords = ["ratio", "rank", "best", "winner", "combined", "overall", "versus", "leader"];

    private static string NameOf(Tier tier, RunVariant variant, string stamp = Stamp) =>
        $"{stamp}-{TierOptions.NameOf(tier)}{(variant == RunVariant.Baseline ? "-baseline" : string.Empty)}.json";

    private static StepReading Step(
        Tier tier = Tier.Local,
        string task = TaskName,
        int repeat = 1,
        string agent = Planner,
        string modelId = "model-a",
        string outcome = StepOutcomes.Ok,
        Metric? net = null,
        Metric? inputTokens = null,
        Metric? latency = null,
        Metric? cost = null,
        Metric? quality = null,
        ReadingFlags flags = ReadingFlags.None,
        EnergyMethod? method = null,
        string? judgedAgent = null,
        Metric? gross = null,
        Metric? avgW = null,
        Metric? peakW = null,
        Metric? outputTokens = null,
        Metric? ttft = null)
    {
        bool hosted = TierKinds.IsHosted(tier);
        Metric hostedNa = Metric.NotApplicable("hosted-tier");
        Metric netJoules = net ?? (hosted ? hostedNa : Metric.Measured(60.0, "J"));
        DateTimeOffset start = StepReadingFixtures.StartUtc.AddSeconds(2 * repeat);

        return new StepReading(
            task: task,
            repeat: repeat,
            agent: agent,
            modelId: modelId,
            tier: tier,
            startUtc: start,
            endUtc: start.AddSeconds(1),
            outcome: outcome,
            latencyMs: latency ?? Metric.Measured(1000.0, "ms"),
            ttftMs: ttft ?? Metric.Measured(250.0, "ms"),
            inputTokens: inputTokens ?? Metric.Measured(120.0, "tokens"),
            outputTokens: outputTokens ?? Metric.Measured(80.0, "tokens"),
            grossJoules: gross ?? (hosted ? hostedNa : Metric.Measured((netJoules.Value ?? 0.0) + 30.0, "J")),
            netJoules: netJoules,
            avgW: avgW ?? (hosted ? hostedNa : Metric.Measured(90.0, "W")),
            peakW: peakW ?? (hosted ? hostedNa : Metric.Measured(110.0, "W")),
            cost: cost ?? Metric.NotApplicable("cost-not-configured"),
            retries: 0,
            waitMs: 0,
            flags: flags,
            method: hosted ? null : method ?? EnergyMethod.Counter,
            quality: quality,
            judgedAgent: judgedAgent);
    }

    // On a hosted tier the energy is N/A whatever the caller asks for, so the values only count the repeats there.
    private static StepReading[] NetSteps(Tier tier, params double[] values) =>
        [.. values.Select((value, index) => Step(tier, repeat: index + 1, net: TierKinds.IsHosted(tier) ? null : Metric.Measured(value, "J")))];

    private static SelectedRun RunOf(
        Tier tier,
        IReadOnlyList<StepReading> steps,
        RunVariant variant = RunVariant.Primary,
        IReadOnlyList<StepReading>? judge = null,
        IReadOnlyList<string>? tasks = null,
        Func<RunDocument, RunDocument>? edit = null,
        string stamp = Stamp)
    {
        RunDocument document = RunDocumentFixtures.Document(steps, judge ?? []);

        // The name of a run file starts with the start of the run formatted to the second, and Build checks that.
        DateTimeOffset startUtc = DateTimeOffset.ParseExact(stamp, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        document = document with { Run = document.Run with { Tier = tier, StartUtc = startUtc, Tasks = tasks ?? [.. steps.Select(step => step.Task).Distinct(StringComparer.Ordinal)] } };
        if (edit is not null)
        {
            document = edit(document);
        }

        return new SelectedRun(variant, NameOf(tier, variant, stamp), document);
    }

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
}