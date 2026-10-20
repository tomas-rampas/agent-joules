using System.Text;
using System.Text.Json.Nodes;

using AgentJoules.Core.Cli;
using AgentJoules.Core.Tasks;
using AgentJoules.TestSupport;
using AgentJoules.Workflow.Tasks;

using Xunit;

namespace AgentJoules.UnitTests;

/// <summary>AJ-048 (REQ-012): the three committed task files load in file-name order, a name selects one, and a bad file or an unknown name exits 2 naming the file or listing the names.</summary>
[Trait("Spec", "REQ-012")]
public sealed class TaskSetTests
{
    private static readonly string[] CommittedFileNames = ["01-incident-brief.json", "02-api-migration.json", "03-energy-explainer.json"];
    private static readonly string[] CommittedNames = ["incident-brief", "api-migration", "energy-explainer"];
    private static readonly int[] CommittedLimits = [512, 768, 384];
    private static readonly string ByteOrderMark = Encoding.UTF8.GetString(Encoding.UTF8.Preamble);

    // The JSON text of a string with a lone surrogate escape (a, backslash, u, D800, b), built from pieces so that no escape is written in this source.
    private static readonly string LoneSurrogate = "a" + new string('\\', 1) + "uD800" + "b";

    private static string CommittedFolder() => Path.Combine(ConfigurationFixtures.RepositoryRoot(), "tasks");

    private static string Valid(string name, int maxOutputTokens = 100) =>
        new JsonObject { ["name"] = name, ["prompt"] = "Write one sentence about " + name + ".", ["maxOutputTokens"] = maxOutputTokens }.ToJsonString();

    // A folder of the given files (name, content), written as they are so that a test can put malformed text in.
    private static TempWorkspace Folder(params (string FileName, string Content)[] files)
    {
        TempWorkspace workspace = TempWorkspace.CreateEmpty();
        Directory.CreateDirectory(Path.Combine(workspace.Root, "tasks"));
        foreach ((string fileName, string content) in files)
        {
            workspace.WriteFile(Path.Combine("tasks", fileName), content);
        }

        return workspace;
    }

    private static string TasksPath(TempWorkspace workspace) => Path.Combine(workspace.Root, "tasks");

    private static ConfigurationException Rejected(string directory) => Assert.Throws<ConfigurationException>(() => TaskSetLoader.Load(directory));

    private static void AssertExitCodeTwo(ConfigurationException failure)
    {
        Assert.Equal(ExitCode.InvalidArguments, failure.ExitCode);
        Assert.Equal(2, (int)failure.ExitCode);
    }

    // ---- criterion 1: the committed files ----

    [Fact]
    public void Committed_tasks_parse()
    {
        string[] files = [.. Directory.GetFiles(CommittedFolder(), "*.json").Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal)];
        Assert.Equal(CommittedFileNames, files);

        foreach (string file in files)
        {
            JsonObject raw = JsonNode.Parse(File.ReadAllText(Path.Combine(CommittedFolder(), file)))!.AsObject();
            Assert.Equal(["name", "prompt", "maxOutputTokens"], raw.Select(member => member.Key));
        }

        IReadOnlyList<TaskDefinition> tasks = TaskSetLoader.Load(CommittedFolder());

        Assert.Equal(CommittedNames, tasks.Select(task => task.Name));
        Assert.Equal(CommittedLimits, tasks.Select(task => task.MaxOutputTokens));
        foreach (TaskDefinition task in tasks)
        {
            Assert.False(string.IsNullOrWhiteSpace(task.Prompt));
            Assert.DoesNotContain("{{", task.Prompt, StringComparison.Ordinal);
            Assert.DoesNotContain("}}", task.Prompt, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Committed_tasks_folder_holds_exactly_the_three_json_files()
    {
        // The folder-level repository check of the story: .gitkeep is gone and nothing else is in the folder.
        string[] entries = [.. Directory.GetFileSystemEntries(CommittedFolder()).Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal)];

        Assert.Equal(CommittedFileNames, entries);
    }

    [Fact]
    public void Committed_files_are_ascii_with_lf_endings_and_one_trailing_newline()
    {
        foreach (string file in CommittedFileNames)
        {
            byte[] bytes = File.ReadAllBytes(Path.Combine(CommittedFolder(), file));
            string text = Encoding.UTF8.GetString(bytes);

            Assert.All(bytes, value => Assert.True(value is < 0x80, file + " holds a non-ASCII byte"));
            Assert.DoesNotContain('\r', text);
            Assert.EndsWith("}\n", text, StringComparison.Ordinal);
            Assert.False(text.StartsWith(ByteOrderMark, StringComparison.Ordinal), file + " starts with a byte order mark");
        }
    }

    [Fact]
    public void Committed_prompts_are_self_contained_and_differ_in_subject()
    {
        IReadOnlyList<TaskDefinition> tasks = TaskSetLoader.Load(CommittedFolder());

        foreach (TaskDefinition task in tasks)
        {
            int words = task.Prompt.Split([' ', '\n'], StringSplitOptions.RemoveEmptyEntries).Length;

            Assert.InRange(words, 110, 260);
            Assert.DoesNotContain("://", task.Prompt, StringComparison.Ordinal);
            Assert.DoesNotContain("www.", task.Prompt, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("nvapi", task.Prompt, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(tasks.Count, tasks.Select(task => task.Prompt).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("timeline", tasks[0].Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("migrat", tasks[1].Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("energy", tasks[2].Prompt, StringComparison.OrdinalIgnoreCase);
    }

    // ---- criterion 2: order and selection ----

    [Fact]
    public void Tasks_come_back_in_file_name_order_not_task_name_order()
    {
        using TempWorkspace workspace = Folder(("01-first.json", Valid("zulu", 10)), ("02-second.json", Valid("alpha", 20)), ("03-third.json", Valid("mike", 30)));

        IReadOnlyList<TaskDefinition> tasks = TaskSetLoader.Load(TasksPath(workspace));

        Assert.Equal(["zulu", "alpha", "mike"], tasks.Select(task => task.Name));
        Assert.Equal([10, 20, 30], tasks.Select(task => task.MaxOutputTokens));
    }

    [Fact]
    public void File_name_order_is_ordinal()
    {
        // Ordinal puts an upper case name before a lower case one and "10" before "9"; a culture sort would not.
        using TempWorkspace workspace = Folder(("a-lower.json", Valid("lower")), ("B-upper.json", Valid("upper")), ("9-nine.json", Valid("nine")), ("10-ten.json", Valid("ten")));

        IReadOnlyList<TaskDefinition> tasks = TaskSetLoader.Load(TasksPath(workspace));

        Assert.Equal(["ten", "nine", "upper", "lower"], tasks.Select(task => task.Name));
    }

    [Fact]
    public void Select_returns_the_one_task_with_that_name()
    {
        IReadOnlyList<TaskDefinition> tasks = TaskSetLoader.Load(CommittedFolder());

        TaskDefinition task = TaskSetLoader.Select(tasks, "energy-explainer");

        Assert.Equal("energy-explainer", task.Name);
        Assert.Equal(384, task.MaxOutputTokens);
    }

    [Fact]
    public void Load_with_a_name_returns_exactly_that_task()
    {
        IReadOnlyList<TaskDefinition> tasks = TaskSetLoader.Load(CommittedFolder(), "api-migration");

        TaskDefinition task = Assert.Single(tasks);
        Assert.Equal("api-migration", task.Name);
        Assert.Equal(768, task.MaxOutputTokens);
    }

    // ---- criterion 4: unknown task name ----

    [Fact]
    public void An_unknown_name_exits_2_and_lists_the_available_names_in_file_order()
    {
        ConfigurationException failure = Assert.Throws<ConfigurationException>(() => TaskSetLoader.Load(CommittedFolder(), "bogus"));

        AssertExitCodeTwo(failure);
        Assert.Equal("unknown task bogus; available: incident-brief, api-migration, energy-explainer", failure.Message);
    }

    [Fact]
    public void An_unknown_name_is_echoed_bounded_and_without_control_characters()
    {
        string requested = "bad\nname" + new string('x', 200);

        ConfigurationException failure = Assert.Throws<ConfigurationException>(() => TaskSetLoader.Load(CommittedFolder(), requested));

        AssertExitCodeTwo(failure);
        Assert.DoesNotContain('\n', failure.Message);
        Assert.DoesNotContain(new string('x', 100), failure.Message, StringComparison.Ordinal);
        Assert.StartsWith("unknown task bad?name", failure.Message, StringComparison.Ordinal);
        Assert.EndsWith("; available: incident-brief, api-migration, energy-explainer", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Task_names_are_matched_exactly()
    {
        IReadOnlyList<TaskDefinition> tasks = TaskSetLoader.Load(CommittedFolder());

        Assert.Throws<ConfigurationException>(() => TaskSetLoader.Select(tasks, "Incident-Brief"));
        Assert.Throws<ConfigurationException>(() => TaskSetLoader.Select(tasks, string.Empty));
    }

    // ---- criterion 3: a bad file exits 2 and names the file, never a value ----

    public static TheoryData<string, string, string> BadFiles() => new()
    {
        { "missing-name", """{"prompt":"p","maxOutputTokens":10}""", "missing name" },
        { "missing-prompt", """{"name":"n","maxOutputTokens":10}""", "missing prompt" },
        { "missing-limit", """{"name":"n","prompt":"p"}""", "missing maxOutputTokens" },
        { "limit-zero", """{"name":"n","prompt":"p","maxOutputTokens":0}""", "maxOutputTokens must be at least 1" },
        { "limit-negative", """{"name":"n","prompt":"p","maxOutputTokens":-5}""", "maxOutputTokens must be at least 1" },
        { "limit-text", """{"name":"n","prompt":"p","maxOutputTokens":"512"}""", "invalid maxOutputTokens" },
        { "limit-fraction", """{"name":"n","prompt":"p","maxOutputTokens":1.5}""", "invalid maxOutputTokens" },
        { "limit-huge", """{"name":"n","prompt":"p","maxOutputTokens":99999999999}""", "invalid maxOutputTokens" },
        { "limit-above-ceiling", """{"name":"n","prompt":"p","maxOutputTokens":32769}""", "maxOutputTokens must be at most 32768" },
        { "limit-int-max", """{"name":"n","prompt":"p","maxOutputTokens":2147483647}""", "maxOutputTokens must be at most 32768" },
        { "prompt-above-ceiling", "{\"name\":\"n\",\"prompt\":\"" + new string('a', 16385) + "\",\"maxOutputTokens\":10}", "prompt longer than 16384 characters" },
        { "lone-surrogate-prompt", "{\"name\":\"n\",\"prompt\":\"" + LoneSurrogate + "\",\"maxOutputTokens\":10}", "malformed JSON" },
        { "lone-surrogate-name", "{\"name\":\"" + LoneSurrogate + "\",\"prompt\":\"p\",\"maxOutputTokens\":10}", "malformed JSON" },
        { "lone-surrogate-key", "{\"" + LoneSurrogate + "\":1,\"name\":\"n\",\"prompt\":\"p\",\"maxOutputTokens\":10}", "malformed JSON" },
        { "malformed", """{"name":"SECRET-MARKER-VALUE""", "malformed JSON" },
        { "not-json", "SECRET-MARKER-VALUE", "malformed JSON" },
        { "empty-file", "", "malformed JSON" },
        { "array-root", """["SECRET-MARKER-VALUE"]""", "not a JSON object" },
        { "null-root", "null", "not a JSON object" },
        { "unknown-member", """{"name":"n","prompt":"p","maxOutputTokens":10,"SECRET-MARKER-VALUE":1}""", "unknown member" },
        { "repeated-member", """{"name":"n","name":"m","prompt":"p","maxOutputTokens":10}""", "duplicate member" },
        { "name-upper", """{"name":"SECRET-MARKER-VALUE","prompt":"p","maxOutputTokens":10}""", "invalid name" },
        { "name-space", """{"name":"has space","prompt":"p","maxOutputTokens":10}""", "invalid name" },
        { "name-underscore", """{"name":"a_b","prompt":"p","maxOutputTokens":10}""", "invalid name" },
        { "name-leading-hyphen", """{"name":"-a","prompt":"p","maxOutputTokens":10}""", "invalid name" },
        { "name-empty", """{"name":"","prompt":"p","maxOutputTokens":10}""", "invalid name" },
        { "name-null", """{"name":null,"prompt":"p","maxOutputTokens":10}""", "invalid name" },
        { "name-number", """{"name":7,"prompt":"p","maxOutputTokens":10}""", "invalid name" },
        { "name-too-long", "{\"name\":\"" + new string('a', 41) + "\",\"prompt\":\"p\",\"maxOutputTokens\":10}", "invalid name" },
        { "prompt-empty", """{"name":"n","prompt":"","maxOutputTokens":10}""", "invalid prompt" },
        { "prompt-blank", """{"name":"n","prompt":"   ","maxOutputTokens":10}""", "invalid prompt" },
        { "prompt-null", """{"name":"n","prompt":null,"maxOutputTokens":10}""", "invalid prompt" },
        { "prompt-placeholder", """{"name":"n","prompt":"Summarise {{timeline}} now.","maxOutputTokens":10}""", "invalid prompt" },
        { "prompt-closing-braces", """{"name":"n","prompt":"Summarise timeline}} now.","maxOutputTokens":10}""", "invalid prompt" },
    };

    [Theory]
    [MemberData(nameof(BadFiles))]
    public void A_bad_task_file_exits_2_naming_only_the_file(string caseName, string content, string problem)
    {
        string fileName = caseName + ".json";
        using TempWorkspace workspace = Folder(("00-good.json", Valid("good")), (fileName, content));

        ConfigurationException failure = Rejected(TasksPath(workspace));

        AssertExitCodeTwo(failure);
        Assert.Equal($"tasks file {fileName}: {problem}", failure.Message);
        Assert.DoesNotContain(workspace.Root, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-MARKER-VALUE", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_first_bad_file_in_file_order_is_the_one_named()
    {
        using TempWorkspace workspace = Folder(("02-late.json", "{"), ("01-early.json", """{"name":"n","prompt":"p","maxOutputTokens":0}"""));

        ConfigurationException failure = Rejected(TasksPath(workspace));

        Assert.Equal("tasks file 01-early.json: maxOutputTokens must be at least 1", failure.Message);
    }

    [Fact]
    public void A_bad_file_fails_the_load_even_when_a_task_is_requested_that_is_in_a_good_file()
    {
        using TempWorkspace workspace = Folder(("01-good.json", Valid("good")), ("02-bad.json", "{"));

        ConfigurationException failure = Assert.Throws<ConfigurationException>(() => TaskSetLoader.Load(TasksPath(workspace), "good"));

        Assert.Equal("tasks file 02-bad.json: malformed JSON", failure.Message);
    }

    [Fact]
    public void Duplicate_names_exit_2_naming_the_second_file()
    {
        using TempWorkspace workspace = Folder(("01-a.json", Valid("same")), ("02-b.json", Valid("same", 200)), ("03-c.json", Valid("other")));

        ConfigurationException failure = Rejected(TasksPath(workspace));

        AssertExitCodeTwo(failure);
        Assert.Equal("tasks file 02-b.json: duplicate name", failure.Message);
    }

    // ---- the folder itself ----

    [Fact]
    public void A_folder_without_json_files_exits_2()
    {
        using TempWorkspace workspace = Folder(("notes.txt", "not a task"), (".gitkeep", string.Empty));

        ConfigurationException failure = Rejected(TasksPath(workspace));

        AssertExitCodeTwo(failure);
        Assert.Equal("no task files (*.json) in the tasks folder", failure.Message);
    }

    [Fact]
    public void A_missing_folder_exits_2_without_its_path()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();

        ConfigurationException failure = Rejected(Path.Combine(workspace.Root, "absent"));

        AssertExitCodeTwo(failure);
        Assert.Equal("the tasks folder does not exist", failure.Message);
        Assert.DoesNotContain(workspace.Root, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Entries_that_are_not_json_files_are_ignored()
    {
        using TempWorkspace workspace = Folder(("01-a.json", Valid("a")), ("02-b.json.bak", "{"), ("notes.txt", "x"), (".gitkeep", string.Empty), ("c.jsonc", "{"));
        Directory.CreateDirectory(Path.Combine(TasksPath(workspace), "folder.json"));

        IReadOnlyList<TaskDefinition> tasks = TaskSetLoader.Load(TasksPath(workspace));

        Assert.Equal("a", Assert.Single(tasks).Name);
    }

    [Theory]
    [InlineData("UPPER.JSON")]
    [InlineData("Mixed.Json")]
    [InlineData("02-b.jSON")]
    public void A_file_whose_extension_is_json_in_another_case_is_refused_not_skipped(string fileName)
    {
        using TempWorkspace workspace = Folder(("01-a.json", Valid("a")), (fileName, Valid("b")));

        ConfigurationException failure = Rejected(TasksPath(workspace));

        AssertExitCodeTwo(failure);
        Assert.Equal($"tasks file {fileName}: file extension must be lowercase .json", failure.Message);
    }

    [Fact]
    public void A_folder_of_only_a_wrong_case_file_names_the_file_not_an_empty_folder()
    {
        using TempWorkspace workspace = Folder(("only.JSON", Valid("only")));

        Assert.Equal("tasks file only.JSON: file extension must be lowercase .json", Rejected(TasksPath(workspace)).Message);
    }

    [Fact]
    public void Hidden_files_are_loaded_like_any_other()
    {
        using TempWorkspace workspace = Folder(("01-a.json", Valid("a")), (".hidden.json", Valid("hidden")));
        File.SetAttributes(Path.Combine(TasksPath(workspace), ".hidden.json"), FileAttributes.Hidden);

        // Ordinal order puts the dot before the digit.
        Assert.Equal(["hidden", "a"], TaskSetLoader.Load(TasksPath(workspace)).Select(task => task.Name));
    }

    [Fact]
    public void Load_returns_an_array_so_the_set_cannot_be_changed_through_the_list()
    {
        IReadOnlyList<TaskDefinition> all = TaskSetLoader.Load(CommittedFolder());
        IReadOnlyList<TaskDefinition> one = TaskSetLoader.Load(CommittedFolder(), "incident-brief");

        Assert.IsType<TaskDefinition[]>(all);
        Assert.IsType<TaskDefinition[]>(one);
    }

    // ---- limits on what a file may ask of the process ----

    [Fact]
    public void A_file_of_exactly_64_KiB_loads_and_one_byte_more_is_refused()
    {
        const int cap = 64 * 1024;
        string json = Valid("n");
        using TempWorkspace workspace = Folder(("at-cap.json", json + new string(' ', cap - json.Length)));
        Assert.Equal(cap, new FileInfo(Path.Combine(TasksPath(workspace), "at-cap.json")).Length);

        Assert.Equal("n", Assert.Single(TaskSetLoader.Load(TasksPath(workspace))).Name);

        _ = workspace.WriteFile(Path.Combine("tasks", "zz-over-cap.json"), Valid("m") + new string(' ', cap - Valid("m").Length + 1));
        Assert.Equal("tasks file zz-over-cap.json: larger than 64 KiB", Rejected(TasksPath(workspace)).Message);
    }

    [Fact]
    public void A_far_larger_file_is_refused_without_being_read_whole()
    {
        using TempWorkspace workspace = Folder(("big.json", Valid("n") + new string(' ', 5 * 1024 * 1024)));

        Assert.Equal("tasks file big.json: larger than 64 KiB", Rejected(TasksPath(workspace)).Message);
    }

    [Fact]
    public void The_edges_of_the_prompt_and_output_limits_load()
    {
        string prompt = new('a', TaskDefinition.MaxPromptLength);
        string json = new JsonObject { ["name"] = "edge", ["prompt"] = prompt, ["maxOutputTokens"] = TaskDefinition.MaxOutputTokensLimit }.ToJsonString();
        using TempWorkspace workspace = Folder(("edge.json", json));

        TaskDefinition task = Assert.Single(TaskSetLoader.Load(TasksPath(workspace)));

        Assert.Equal(16384, task.Prompt.Length);
        Assert.Equal(32768, task.MaxOutputTokens);
    }

    [Fact]
    public void The_limits_are_the_documented_numbers()
    {
        Assert.Equal(16384, TaskDefinition.MaxPromptLength);
        Assert.Equal(32768, TaskDefinition.MaxOutputTokensLimit);
    }

    // ---- bytes that are not UTF-8 ----

    public static TheoryData<string, byte[]> NotUtf8() => new()
    {
        { "invalid-lead-bytes", [0xFF, 0xFE, 0xC0] },
        { "utf16-with-mark", [.. new UnicodeEncoding(bigEndian: false, byteOrderMark: true).GetPreamble(), .. new UnicodeEncoding(bigEndian: false, byteOrderMark: false).GetBytes(Valid("n"))] },
        { "truncated-sequence", [.. Encoding.ASCII.GetBytes("{\"name\":\"n\",\"prompt\":\"p"), 0xE2, 0x82] },
        { "encoded-surrogate", [.. Encoding.ASCII.GetBytes("{\"name\":\"n\",\"prompt\":\"p"), 0xED, 0xA0, 0x80] },
    };

    [Theory]
    [MemberData(nameof(NotUtf8))]
    public void A_file_that_is_not_valid_UTF8_is_refused_not_decoded_with_replacement_characters(string caseName, byte[] bytes)
    {
        using TempWorkspace workspace = Folder();
        File.WriteAllBytes(Path.Combine(TasksPath(workspace), caseName + ".json"), bytes);

        ConfigurationException failure = Rejected(TasksPath(workspace));

        AssertExitCodeTwo(failure);
        Assert.Equal($"tasks file {caseName}.json: not valid UTF-8", failure.Message);
    }

    [Fact]
    public void A_utf8_file_with_a_byte_order_mark_and_non_ascii_text_still_loads()
    {
        // The bytes are written directly: the mark and one two-byte letter (U+00E9 as C3 A9) in the prompt.
        byte[] bytes = [.. Encoding.UTF8.GetPreamble(), .. Encoding.ASCII.GetBytes("{\"name\":\"n\",\"prompt\":\"caf"), 0xC3, 0xA9, .. Encoding.ASCII.GetBytes("\",\"maxOutputTokens\":10}")];
        using TempWorkspace workspace = Folder();
        File.WriteAllBytes(Path.Combine(TasksPath(workspace), "bom.json"), bytes);

        TaskDefinition task = Assert.Single(TaskSetLoader.Load(TasksPath(workspace)));

        Assert.Equal(4, task.Prompt.Length);
    }

    // ---- what the file system can do to a load ----

    [Fact]
    public void A_file_that_cannot_be_opened_exits_2_naming_only_the_file()
    {
        using TempWorkspace workspace = Folder(("01-a.json", Valid("a")), ("02-b.json", Valid("b")));
        using var held = new FileStream(Path.Combine(TasksPath(workspace), "02-b.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        ConfigurationException failure = Rejected(TasksPath(workspace));

        AssertExitCodeTwo(failure);
        Assert.Equal("tasks file 02-b.json: cannot be read", failure.Message);
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain(workspace.Root, failure.Message, StringComparison.Ordinal);
    }

    [DeniedFolderFact]
    public void A_folder_that_cannot_be_listed_exits_2_without_its_path()
    {
        using TempWorkspace workspace = Folder(("01-a.json", Valid("a")));

        ConfigurationException failure;
        using (FolderAccess.DenyListing(TasksPath(workspace)))
        {
            failure = Rejected(TasksPath(workspace));
        }

        AssertExitCodeTwo(failure);
        Assert.Equal("the tasks folder cannot be read", failure.Message);
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain(workspace.Root, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_folder_path_with_a_trailing_separator_loads_normally()
    {
        using TempWorkspace workspace = Folder(("01-a.json", Valid("a")));

        Assert.Equal("a", Assert.Single(TaskSetLoader.Load(TasksPath(workspace) + Path.DirectorySeparatorChar)).Name);
        Assert.Equal("a", Assert.Single(TaskSetLoader.Load(TasksPath(workspace) + Path.AltDirectorySeparatorChar)).Name);
    }

    [Fact]
    public void A_tasks_folder_that_is_a_link_is_refused_with_or_without_a_trailing_separator()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string real = Path.Combine(workspace.Root, "real");
        Directory.CreateDirectory(real);
        File.WriteAllText(Path.Combine(real, "01-a.json"), Valid("a"));
        string link = Path.Combine(workspace.Root, "tasks");
        using IDisposable linkGuard = FolderAccess.CreateDirectoryLink(link, real);

        Assert.Equal("the tasks folder is a link", Rejected(link + Path.DirectorySeparatorChar).Message);
        Assert.Equal("the tasks folder is a link", Rejected(link + Path.AltDirectorySeparatorChar).Message);
    }

    [Fact]
    public void A_tasks_folder_that_is_a_link_is_refused()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string real = Path.Combine(workspace.Root, "real");
        Directory.CreateDirectory(real);
        File.WriteAllText(Path.Combine(real, "01-a.json"), Valid("a"));
        string link = Path.Combine(workspace.Root, "tasks");
        using IDisposable linkGuard = FolderAccess.CreateDirectoryLink(link, real);

        ConfigurationException failure = Rejected(link);

        AssertExitCodeTwo(failure);
        Assert.Equal("the tasks folder is a link", failure.Message);
        Assert.DoesNotContain(workspace.Root, failure.Message, StringComparison.Ordinal);
    }

    [SymbolicLinkFact]
    public void A_task_file_that_is_a_link_is_refused()
    {
        using TempWorkspace workspace = Folder(("01-a.json", Valid("a")));
        string outside = workspace.WriteFile("outside.txt", Valid("outside"));
        _ = File.CreateSymbolicLink(Path.Combine(TasksPath(workspace), "02-link.json"), outside);

        ConfigurationException failure = Rejected(TasksPath(workspace));

        AssertExitCodeTwo(failure);
        Assert.Equal("tasks file 02-link.json: is a link", failure.Message);
    }

    [Fact]
    public void A_file_with_a_byte_order_mark_loads()
    {
        using TempWorkspace workspace = Folder(("01-a.json", ByteOrderMark + Valid("a")));

        Assert.Equal("a", Assert.Single(TaskSetLoader.Load(TasksPath(workspace))).Name);
    }

    [Fact]
    public void Load_validates_its_arguments()
    {
        Assert.Throws<ArgumentException>(() => TaskSetLoader.Load(string.Empty));
        Assert.Throws<ArgumentNullException>(() => TaskSetLoader.Load(null!));
        Assert.Throws<ArgumentNullException>(() => TaskSetLoader.Load(CommittedFolder(), null!));
        Assert.Throws<ArgumentNullException>(() => TaskSetLoader.Select(null!, "x"));
        Assert.Throws<ArgumentNullException>(() => TaskSetLoader.Select([], null!));
    }

    // ---- the record ----

    [Theory]
    [InlineData("Upper", "p", 1, "name")]
    [InlineData("", "p", 1, "name")]
    [InlineData("a b", "p", 1, "name")]
    [InlineData("n", "", 1, "prompt")]
    [InlineData("n", "has {{ braces", 1, "prompt")]
    [InlineData("n", "p", 0, "maxOutputTokens")]
    [InlineData("n", "p", -1, "maxOutputTokens")]
    public void The_record_refuses_a_bad_value_naming_the_property_never_the_value(string name, string prompt, int maxOutputTokens, string property)
    {
        ArgumentException failure = Assert.ThrowsAny<ArgumentException>(() => new TaskDefinition(name, prompt, maxOutputTokens));

        Assert.Equal(property, failure.ParamName);
        Assert.DoesNotContain("has {{ braces", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_record_accepts_the_edges_of_every_rule()
    {
        var task = new TaskDefinition(new string('a', 40), "p", 1);

        Assert.Equal(40, task.Name.Length);
        Assert.Equal(1, task.MaxOutputTokens);
        Assert.Equal("0-9", new TaskDefinition("0-9", "p", TaskDefinition.MaxOutputTokensLimit).Name);
        Assert.Equal(TaskDefinition.MaxPromptLength, new TaskDefinition("n", new string('a', TaskDefinition.MaxPromptLength), 1).Prompt.Length);
    }

    [Fact]
    public void The_record_refuses_a_prompt_over_the_ceiling_naming_the_property_never_the_text()
    {
        string prompt = new('z', TaskDefinition.MaxPromptLength + 1);

        ArgumentException failure = Assert.ThrowsAny<ArgumentException>(() => new TaskDefinition("n", prompt, 1));

        Assert.Equal("prompt", failure.ParamName);
        Assert.DoesNotContain("zzzz", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(32769)]
    [InlineData(int.MaxValue)]
    public void The_record_refuses_an_output_limit_over_the_ceiling(int maxOutputTokens)
    {
        ArgumentException failure = Assert.ThrowsAny<ArgumentException>(() => new TaskDefinition("n", "p", maxOutputTokens));

        Assert.Equal("maxOutputTokens", failure.ParamName);
    }
}