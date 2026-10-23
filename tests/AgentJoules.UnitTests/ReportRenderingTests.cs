using System.Text;

using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Reporting.Results;
using AgentJoules.TestSupport;

using Xunit;

namespace AgentJoules.UnitTests;

/// <summary>
/// AJ-081 (REQ-016): the run files a report may use. The header reader returns the tier, the status and the flags of the <c>run</c> object and one reason token for a header it cannot read; the selector
/// returns, per tier and per variant, the newest file by name whose header is accepted (completed or partial, not <c>non-publishable</c>), never opens a probe, calibration or non-matching file, reads only the
/// header, and reports an unreadable header or a tier that differs from the name as a problem, never as a tier that was not run.
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
}