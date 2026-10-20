using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

using AgentJoules.Cli;
using AgentJoules.Core.Cli;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Core.Security;
using AgentJoules.Reporting.Json;
using AgentJoules.Reporting.Results;
using AgentJoules.TestSupport;

using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace AgentJoules.UnitTests;

/// <summary>
/// AJ-055 (REQ-017): the run file name, the atomic checkpoint writer, the five run statuses, the byte format of the file (UTF-8 without a byte order mark,
/// LF, invariant numbers), the results records with the names of the prompt skeleton, the null value of an N/A metric, and the exit code 1 for an output
/// directory that cannot be written before any call.
/// </summary>
[Trait("Spec", "REQ-017")]
public sealed class RunCheckpointTests
{
    // A write that hangs must fail the test, not the run (system clock; it only ends a run that already failed).
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    private static readonly DateTimeOffset Start = new(2026, 10, 22, 9, 15, 30, TimeSpan.Zero);
    private static readonly string[] AllNemotron = [RunCheckpointFixtures.NemotronModel, "NVIDIA/Nemotron-Super", "nemotron-mini"];
    private static readonly string[] BadStatusTokens = ["\"Running\"", "\"RUNNING\"", "\" running\"", "\"done\"", "\"running, failed\"", "1", "0", "null", "true", "[]"];
    private static readonly Type[] RecordTypes = [typeof(RunDocument), typeof(RunInfo), typeof(EnvironmentInfo), typeof(StepReading), typeof(Totals)];
    private static readonly string[] EnergyNames = ["grossJoules", "netJoules", "avgW", "peakW"];
    private static readonly string[] BadFileNames = ["..\\evil.json", "../evil.json", "nested/20261022T091530Z-nvidia.json", "report.json", string.Empty];

    private static JsonTypeInfo<RunDocument> Info =>
        AgentJoulesJsonContext.Default.GetTypeInfo(typeof(RunDocument)) as JsonTypeInfo<RunDocument>
        ?? throw new Xunit.Sdk.XunitException("AgentJoulesJsonContext does not register RunDocument.");

    private static string Serialise(RunDocument document) => JsonSerializer.Serialize(document, Info);

    private static ResultsFileWriter WriterIn(TempWorkspace workspace, string fileName, Action<string>? beforeMove = null, SecretRedactor? redactor = null) =>
        new(Path.Combine(workspace.Root, "results"), fileName, redactor) { BeforeMove = beforeMove };

    // ---- criterion 1: the run file name ----

    [Fact]
    public void Run_file_name_rules()
    {
        Assert.Equal("^\\d{8}T\\d{6}Z-(local|cloudgpu|nvidia|nebius)(-baseline)?\\.json$", RunFileName.Pattern);

        // The committed example of the story: UTC from the clock, the tier in lowercase, no suffix for an all-Nemotron run.
        var time = new FakeTimeProvider(Start);
        Assert.Equal("20261022T091530Z-nvidia.json", RunFileName.Create(time, Tier.Nvidia, AllNemotron));
        Assert.Equal("20261022T091530Z-nvidia.json", RunFileName.Create(Start, Tier.Nvidia, AllNemotron));

        // Every tier gives a name that matches the pattern, with and without the suffix.
        foreach (Tier tier in Enum.GetValues<Tier>())
        {
            string plain = RunFileName.Create(Start, tier, AllNemotron);
            string baseline = RunFileName.Create(Start, tier, [.. AllNemotron, "qwen3-32b"]);
            Assert.True(RunFileName.IsValid(plain), plain);
            Assert.True(RunFileName.IsValid(baseline), baseline);
            Assert.EndsWith("-baseline.json", baseline, StringComparison.Ordinal);
            Assert.DoesNotContain("-baseline", plain, StringComparison.Ordinal);
        }

        // The suffix rule: any one model ID without "nemotron", whatever the case, switches it on; the case of "nemotron" does not matter.
        Assert.False(RunFileName.IsBaseline(["NEMOTRON-x", "nvidia/NeMoTrOn-3"]));
        Assert.True(RunFileName.IsBaseline(["nemotron-a", "meta/llama-3.1-70b-instruct"]));
        Assert.True(RunFileName.IsBaseline(["nemotron-a", string.Empty]));

        // The time is read from the clock, not from the wall: moving the fake clock moves the name; a later offset is converted to UTC.
        time.SetUtcNow(new DateTimeOffset(2027, 1, 2, 3, 4, 5, TimeSpan.Zero));
        Assert.Equal("20270102T030405Z-local.json", RunFileName.Create(time, Tier.Local, AllNemotron));
        var plusTwo = new DateTimeOffset(2026, 10, 22, 11, 15, 30, TimeSpan.FromHours(2));
        Assert.Equal("20261022T091530Z-nebius.json", RunFileName.Create(plusTwo, Tier.Nebius, AllNemotron));
    }

    [Fact]
    public void Run_file_name_validation_is_exact()
    {
        string arabicIndicDigits = string.Concat(Enumerable.Repeat(((char)0x0660).ToString(), 8));
        string[] invalid =
        [
            "20261022T091530Z-nvidia.json\n",
            "20261022T091530Z-nvidia.json ",
            "20261022t091530Z-nvidia.json",
            "20261022T091530Z-NVIDIA.json",
            "20261022T091530Z-hosted.json",
            "20261022T091530Z-nvidia-baseline-baseline.json",
            "20261022T091530-nvidia.json",
            "2026102T091530Z-nvidia.json",
            "20261022T091530Z-nvidia.json.partial",
            "20261022T091530Z-nvidia.JSON",
            "results/20261022T091530Z-nvidia.json",
            arabicIndicDigits + "T091530Z-nvidia.json",
            string.Empty,
        ];

        foreach (string name in invalid)
        {
            Assert.False(RunFileName.IsValid(name), name);
        }

        Assert.True(RunFileName.IsValid("20261022T091530Z-cloudgpu-baseline.json"));
        Assert.False(RunFileName.IsValid(null!));
    }

    [Fact]
    public void Run_file_name_rejects_bad_arguments()
    {
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => RunFileName.Create(Start, (Tier)99, AllNemotron));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => RunFileName.Create(Start, default, AllNemotron));
        _ = Assert.Throws<ArgumentException>(() => RunFileName.Create(Start, Tier.Local, []));
        _ = Assert.Throws<ArgumentNullException>(() => RunFileName.Create(Start, Tier.Local, null!));
        _ = Assert.Throws<ArgumentNullException>(() => RunFileName.Create(Start, Tier.Local, ["nemotron-a", null!]));
        _ = Assert.Throws<ArgumentNullException>(() => RunFileName.Create(null!, Tier.Local, AllNemotron));
    }

    // ---- criterion 2: the atomic move ----

    [Fact]
    public async Task Failed_write_keeps_previous_file()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        int writes = 0;
        string? partialSeen = null;
        ResultsFileWriter writer = WriterIn(workspace, "20261022T091530Z-nvidia.json", partial =>
        {
            // The hook runs after the partial file was written and closed, and before the move.
            partialSeen = partial;
            Assert.True(File.Exists(partial));
            Assert.EndsWith(".json.partial", partial, StringComparison.Ordinal);
            if (++writes == 2)
            {
                throw new IOException("The disk failed.");
            }
        });

        await writer.WriteAsync(RunCheckpointFixtures.Document(RunStatus.Running, stepCount: 1), CancellationToken.None).WaitAsync(HangGuard);
        byte[] first = File.ReadAllBytes(writer.FilePath);

        IOException failure = await Assert.ThrowsAsync<IOException>(() =>
            writer.WriteAsync(RunCheckpointFixtures.Document(RunStatus.Running, stepCount: 2), CancellationToken.None).WaitAsync(HangGuard));

        Assert.Equal("The disk failed.", failure.Message);
        Assert.Equal(first, File.ReadAllBytes(writer.FilePath));
        Assert.False(File.Exists(partialSeen), "The partial file of the failed write should be removed.");
        Assert.Equal([writer.FilePath], Directory.GetFiles(Path.GetDirectoryName(writer.FilePath)!));

        // The writer is still usable: the next checkpoint replaces the file.
        await writer.WriteAsync(RunCheckpointFixtures.Document(RunStatus.Completed, stepCount: 3), CancellationToken.None).WaitAsync(HangGuard);
        Assert.Equal(3, ReadBack(writer.FilePath).Steps.Count);
    }

    [WindowsFact]
    public async Task Replace_that_the_file_system_refuses_keeps_previous_file()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        ResultsFileWriter writer = WriterIn(workspace, "20261022T091530Z-nvidia.json");
        await writer.WriteAsync(RunCheckpointFixtures.Document(RunStatus.Running, stepCount: 1), CancellationToken.None).WaitAsync(HangGuard);
        byte[] first = File.ReadAllBytes(writer.FilePath);

        // A reader that does not share deletion makes the replace fail after the partial file was written, with no hook involved.
        using (var reader = new FileStream(writer.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Exception? refused = await Record.ExceptionAsync(() =>
                writer.WriteAsync(RunCheckpointFixtures.Document(RunStatus.Running, stepCount: 2), CancellationToken.None).WaitAsync(HangGuard));

            // The Windows replace fails with a sharing violation (IOException) or an access denial (UnauthorizedAccessException), nothing else.
            Assert.True(refused is IOException or UnauthorizedAccessException, refused?.GetType().FullName ?? "no exception");
            Assert.Equal(first, File.ReadAllBytes(writer.FilePath));
        }

        Assert.Equal([writer.FilePath], Directory.GetFiles(Path.GetDirectoryName(writer.FilePath)!));
        Assert.Equal(first, File.ReadAllBytes(writer.FilePath));
    }

    [Fact]
    public async Task Failed_first_write_leaves_no_file()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        ResultsFileWriter writer = WriterIn(workspace, "20261022T091530Z-nvidia.json", _ => throw new IOException("The disk failed."));

        _ = await Assert.ThrowsAsync<IOException>(() => writer.WriteAsync(RunCheckpointFixtures.Document(RunStatus.Running), CancellationToken.None).WaitAsync(HangGuard));

        Assert.Empty(Directory.GetFileSystemEntries(Path.GetDirectoryName(writer.FilePath)!));
    }

    [Fact]
    public async Task Cancelled_write_keeps_previous_file()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        ResultsFileWriter writer = WriterIn(workspace, "20261022T091530Z-nvidia.json");
        await writer.WriteAsync(RunCheckpointFixtures.Document(RunStatus.Running, stepCount: 1), CancellationToken.None).WaitAsync(HangGuard);
        byte[] first = File.ReadAllBytes(writer.FilePath);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.WriteAsync(RunCheckpointFixtures.Document(RunStatus.Running, stepCount: 2), cancelled.Token).WaitAsync(HangGuard));

        Assert.Equal(first, File.ReadAllBytes(writer.FilePath));
        Assert.Equal([writer.FilePath], Directory.GetFiles(Path.GetDirectoryName(writer.FilePath)!));
    }

    [Fact]
    public async Task Stale_partial_file_of_a_crashed_run_is_replaced()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string fileName = "20261022T091530Z-nvidia.json";
        string directory = Path.Combine(workspace.Root, "results");
        _ = Directory.CreateDirectory(directory);
        string stale = Path.Combine(directory, fileName + ".partial");
        File.WriteAllText(stale, "{ half a docu");
        ResultsFileWriter writer = WriterIn(workspace, fileName);

        await writer.WriteAsync(RunCheckpointFixtures.Document(RunStatus.Running), CancellationToken.None).WaitAsync(HangGuard);

        Assert.False(File.Exists(stale));
        Assert.Equal(RunStatus.Running, ReadBack(writer.FilePath).Run.Status);
    }

    [Fact]
    public async Task Overlapping_writes_are_refused()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        Task? overlapping = null;
        ResultsFileWriter? writer = null;
        writer = WriterIn(workspace, "20261022T091530Z-nvidia.json", _ =>
            overlapping ??= writer!.WriteAsync(RunCheckpointFixtures.Document(RunStatus.Running), CancellationToken.None));

        await writer.WriteAsync(RunCheckpointFixtures.Document(RunStatus.Running), CancellationToken.None).WaitAsync(HangGuard);

        Assert.NotNull(overlapping);
        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => overlapping);
    }

    // ---- criterion 3: the status values ----

    [Fact]
    public async Task First_checkpoint_has_status_running()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        ResultsFileWriter writer = WriterIn(workspace, "20261022T091530Z-nvidia.json");

        await writer.WriteAsync(RunCheckpointFixtures.Document(RunStatus.Running), CancellationToken.None).WaitAsync(HangGuard);

        using JsonDocument parsed = JsonDocument.Parse(File.ReadAllBytes(writer.FilePath));
        Assert.Equal("running", parsed.RootElement.GetProperty("run").GetProperty("status").GetString());
        Assert.Equal(RunStatus.Running, ReadBack(writer.FilePath).Run.Status);
    }

    [Fact]
    public void Run_status_takes_the_five_exact_strings()
    {
        (RunStatus Status, string Text)[] expected =
        [
            (RunStatus.Running, "running"),
            (RunStatus.Completed, "completed"),
            (RunStatus.Failed, "failed"),
            (RunStatus.Partial, "partial"),
            (RunStatus.Incomplete, "incomplete"),
        ];
        Assert.Equal(5, Enum.GetValues<RunStatus>().Length);

        foreach ((RunStatus status, string text) in expected)
        {
            RunDocument document = RunCheckpointFixtures.Document(status);
            JsonObject root = JsonNode.Parse(Serialise(document))!.AsObject();
            Assert.Equal(text, (string?)root["run"]!["status"]);
            Assert.Equal(status, JsonSerializer.Deserialize(Serialise(document), Info)!.Run.Status);
        }

        // Nothing but the five exact strings reads back.
        foreach (string bad in BadStatusTokens)
        {
            RunDocument document = RunCheckpointFixtures.Document(RunStatus.Running);
            JsonObject root = JsonNode.Parse(Serialise(document))!.AsObject();
            root["run"]!["status"] = JsonNode.Parse(bad);
            _ = Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize(root.ToJsonString(), Info));
        }

        RunDocument undefined = RunCheckpointFixtures.Document(RunStatus.Running);
        _ = Assert.Throws<JsonException>(() => Serialise(undefined with { Run = undefined.Run with { Status = (RunStatus)0 } }));
    }

    // ---- criterion 4: the bytes of the file ----

    [Fact]
    public async Task File_is_utf8_no_bom_lf()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        ResultsFileWriter writer = WriterIn(workspace, "20261022T091530Z-nvidia.json");
        string accented = "caf" + (char)0x00E9;
        string astral = char.ConvertFromUtf32(0x1F600);
        string lineBreaks = "line one\r\nline two\rline three\nline four";
        RunDocument document = RunCheckpointFixtures.Document(RunStatus.Running, stepCount: 2, cost: Metric.Estimated(1234.5, "USD"));
        document = document with { Run = document.Run with { Tasks = [accented, astral, lineBreaks] } };

        // A decimal comma in the thread's culture must not reach the file.
        CultureInfo original = CultureInfo.CurrentCulture;
        var commaCulture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        commaCulture.NumberFormat.NumberDecimalSeparator = ",";
        commaCulture.NumberFormat.NumberGroupSeparator = ".";
        try
        {
            CultureInfo.CurrentCulture = commaCulture;
            await writer.WriteAsync(document, CancellationToken.None).WaitAsync(HangGuard);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }

        byte[] bytes = File.ReadAllBytes(writer.FilePath);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "The file starts with a byte order mark.");
        Assert.Equal((byte)'{', bytes[0]);
        Assert.DoesNotContain((byte)'\r', bytes);
        Assert.Equal((byte)'\n', bytes[^1]);
        Assert.NotEqual((byte)'\n', bytes[^2]);

        // Valid UTF-8, strictly; and the text round-trips the strings that were hard to write.
        string text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        Assert.Equal([accented, astral, lineBreaks], ReadBack(writer.FilePath).Run.Tasks);
        Assert.Contains("1234.5", text, StringComparison.Ordinal);
        Assert.DoesNotContain("1234,5", text, StringComparison.Ordinal);
        Assert.Equal(Serialise(document) + "\n", text);
    }

    // ---- criterion 5: the records and the context ----

    [Fact]
    public void Records_exist_with_the_camelCase_names_of_the_skeleton()
    {
        // The records live in AgentJoules.Core, StepReading is the element of the steps and the judge list.
        Assembly core = typeof(StepReading).Assembly;
        foreach (Type type in RecordTypes)
        {
            Assert.Same(core, type.Assembly);
            Assert.True(type.IsSealed);
        }

        Assert.Equal(typeof(IReadOnlyList<StepReading>), typeof(RunDocument).GetProperty(nameof(RunDocument.Steps))!.PropertyType);
        Assert.Equal(typeof(IReadOnlyList<StepReading>), typeof(RunDocument).GetProperty(nameof(RunDocument.Judge))!.PropertyType);

        JsonObject root = JsonNode.Parse(Serialise(RunCheckpointFixtures.Document(RunStatus.Running, stepCount: 1)))!.AsObject();
        Assert.Equal(["schemaVersion", "run", "environment", "probe", "baseline", "steps", "judge", "totals"], Names(root));
        Assert.Equal("1", (string?)root["schemaVersion"]);
        Assert.Equal(["id", "tier", "tasks", "mode", "repeat", "startUtc", "toolVersion", "gitCommit", "status", "flags", "stopReason", "selfJudged"], Names(root["run"]!.AsObject()));
        Assert.Equal(
            [
                "gpuName", "driverVersion", "os", "dotnetVersion", "localRuntime", "runtimeVersion", "gpuOffloadPercent", "modelLoadSeconds", "powerSource",
                "reasoningMode", "temperature", "provider", "instancePreset", "region", "gpuUuid", "containerImage", "containerDigest", "precision", "cloudCostUsd",
                "energyScope", "assumptions", "models", "cloudCostObservedUtc",
            ],
            Names(root["environment"]!.AsObject()));
        Assert.Equal(["electricityPrice", "gco2ePerKwh"], Names(root["environment"]!["assumptions"]!.AsObject()));
        Assert.Equal(["agent", "modelId", "baseline"], Names(root["environment"]!["models"]![0]!.AsObject()));
        Assert.Equal("GPU-board only", (string?)root["environment"]!["energyScope"]);
        Assert.Equal(["grossJoules", "netJoules", "inputTokens", "outputTokens", "costByCurrency", "wallClockMs", "judge", "gco2e"], Names(root["totals"]!.AsObject()));
        Assert.Equal(["inputTokens", "outputTokens", "costByCurrency"], Names(root["totals"]!["judge"]!.AsObject()));

        string[] stepNames =
        [
            "task", "repeat", "agent", "modelId", "tier", "startUtc", "endUtc", "outcome", "retries", "waitMs", "latencyMs", "ttftMs", "inputTokens", "outputTokens",
            "grossJoules", "netJoules", "avgW", "peakW", "method", "sampleCount", "sampleRateHz", "cost", "quality", "qualityReason", "joulesPerQualityPoint",
            "costPerQualityPoint", "flags", "outputSha256", "outputChars", "throttleReasons",
        ];
        Assert.Equal(stepNames, Names(root["steps"]![0]!.AsObject()));
        Assert.Equal("nvidia", (string?)root["run"]!["tier"]);
        Assert.Equal("serialized", (string?)root["run"]!["mode"]);
    }

    [Fact]
    public void Json_context_registers_RunDocument()
    {
        Assert.NotNull(AgentJoulesJsonContext.Default.GetTypeInfo(typeof(RunDocument)));
        Assert.NotNull(AgentJoulesJsonContext.Default.Options.GetTypeInfo(typeof(RunDocument)));
        Assert.NotNull(AgentJoulesJsonContext.Default.GetTypeInfo(typeof(Core.Gpu.ProbeResult)));

        // LF newlines and nulls written, from the options of the context itself.
        Assert.Equal("\n", AgentJoulesJsonContext.Default.Options.NewLine);
        Assert.True(AgentJoulesJsonContext.Default.Options.WriteIndented);
        Assert.Equal(JsonNamingPolicy.CamelCase, AgentJoulesJsonContext.Default.Options.PropertyNamingPolicy);
    }

    [Fact]
    public void Document_read_back_is_held_to_its_type()
    {
        string text = Serialise(RunCheckpointFixtures.Document(RunStatus.Completed, stepCount: 2));
        Assert.Equal(text, Serialise(JsonSerializer.Deserialize(text, Info)!));

        JsonObject missingRun = JsonNode.Parse(text)!.AsObject();
        _ = missingRun.Remove("run");
        _ = Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize(missingRun.ToJsonString(), Info));

        JsonObject unknown = JsonNode.Parse(text)!.AsObject();
        unknown["extra"] = 1;
        _ = Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize(unknown.ToJsonString(), Info));

        JsonObject nullRun = JsonNode.Parse(text)!.AsObject();
        nullRun["run"] = null;
        _ = Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize(nullRun.ToJsonString(), Info));

        JsonObject numberTier = JsonNode.Parse(text)!.AsObject();
        numberTier["run"]!["tier"] = 3;
        _ = Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize(numberTier.ToJsonString(), Info));

        JsonObject duplicate = JsonNode.Parse(text)!.AsObject();
        string withDuplicate = duplicate.ToJsonString().Replace("\"schemaVersion\":\"1\"", "\"schemaVersion\":\"1\",\"schemaVersion\":\"1\"", StringComparison.Ordinal);
        _ = Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize(withDuplicate, Info));
    }

    // ---- criterion 6: an N/A metric is null, never 0 ----

    [Fact]
    public async Task Not_applicable_metric_serialises_a_null_value()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        ResultsFileWriter writer = WriterIn(workspace, "20261022T091530Z-nvidia.json");
        RunDocument document = RunCheckpointFixtures.Document(RunStatus.Running, stepCount: 1, cost: Metric.NotApplicable("free-tier-no-price"));

        await writer.WriteAsync(document, CancellationToken.None).WaitAsync(HangGuard);

        string text = File.ReadAllText(writer.FilePath);
        using JsonDocument parsed = JsonDocument.Parse(text);
        JsonElement cost = parsed.RootElement.GetProperty("steps")[0].GetProperty("cost");
        Assert.Equal(JsonValueKind.Null, cost.GetProperty("value").ValueKind);
        Assert.Equal("N/A", cost.GetProperty("label").GetString());
        Assert.Equal("free-tier-no-price", cost.GetProperty("reason").GetString());
        Assert.Contains("\"value\": null", text, StringComparison.Ordinal);

        // Every absent value in the file is null: the hosted energy metrics and the totals are N/A too, and not one of them has a number.
        foreach (JsonElement energy in EnergyNames.Select(name => parsed.RootElement.GetProperty("steps")[0].GetProperty(name)))
        {
            Assert.Equal(JsonValueKind.Null, energy.GetProperty("value").ValueKind);
        }

        Assert.DoesNotMatch(@"""value"":\s*0\b", text);
        Assert.Null(ReadBack(writer.FilePath).Steps[0].Cost.Value);
    }

    // ---- criterion 7: the writable-directory probe ----

    [Fact]
    public async Task Unwritable_out_dir_exits_1_before_calls()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        var handler = new FakeHttpMessageHandler();

        // Where the host allows it the folder denies file creation to the current user; on every host a path occupied by a file cannot become a folder.
        string occupied = workspace.WriteFile("occupied", "this path is a file");
        List<string> directories = [occupied, Path.Combine(occupied, "nested")];
        using var denial = new DisposableList();
        if (FolderAccess.DenyListingSkipReason() is null)
        {
            string denied = Path.Combine(workspace.Root, "denied");
            _ = Directory.CreateDirectory(denied);
            denial.Add(FolderAccess.DenyWriting(denied));
            directories.Add(denied);
        }

        foreach (string directory in directories)
        {
            AgentJoulesException failure = await Assert.ThrowsAsync<AgentJoulesException>(() => RunLikeTheCommandAsync(directory, handler));

            // The mapper turns the failure into exit code 1, and no call reached the endpoint.
            Assert.Equal(ExitCode.Unexpected, failure.ExitCode);
            Assert.Equal(1, ExitCodeMapper.Map(failure, shutdownRequested: false));
            Assert.Equal(0, handler.RequestCount);

            // One line, naming the directory.
            Assert.DoesNotContain('\n', failure.Message);
            Assert.DoesNotContain('\r', failure.Message);
            Assert.Contains(Path.GetFileName(directory), failure.Message, StringComparison.Ordinal);
        }

        denial.Dispose();

        // The control: a writable directory lets the command reach the endpoint, so the zero above is the probe's doing.
        await RunLikeTheCommandAsync(Path.Combine(workspace.Root, "writable"), handler);
        Assert.Equal(1, handler.RequestCount);
    }

    [DeniedFolderFact]
    public void Denied_out_dir_fails_the_probe_with_exit_1()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string denied = Path.Combine(workspace.Root, "denied");
        _ = Directory.CreateDirectory(denied);
        using IDisposable denial = FolderAccess.DenyWriting(denied);

        AgentJoulesException failure = Assert.Throws<AgentJoulesException>(() => new ResultsFileWriter(denied, "20261022T091530Z-nvidia.json"));

        Assert.Equal(1, ExitCodeMapper.Map(failure, shutdownRequested: false));
        Assert.Contains("denied", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Probe_creates_a_missing_directory_and_leaves_nothing_behind()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string directory = Path.Combine(workspace.Root, "a", "b", "results");

        var writer = new ResultsFileWriter(directory, "20261022T091530Z-nvidia.json");

        Assert.True(Directory.Exists(directory));
        Assert.Empty(Directory.GetFileSystemEntries(directory));
        Assert.Equal(Path.Combine(directory, "20261022T091530Z-nvidia.json"), writer.FilePath);
    }

    [Fact]
    public void Probe_failure_message_never_holds_a_registered_key()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string key = string.Concat("zk7w", "2qm9", "xv4t", "j8hd");
        string occupied = workspace.WriteFile("out-" + key, "this path is a file");
        var redactor = new SecretRedactor();
        Assert.True(redactor.Register(key));

        AgentJoulesException failure = Assert.Throws<AgentJoulesException>(() => new ResultsFileWriter(occupied, "20261022T091530Z-nvidia.json", redactor));

        Assert.DoesNotContain(key, failure.Message, StringComparison.Ordinal);
        Assert.Contains("****", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Writer_refuses_a_file_name_that_is_not_a_run_file_name()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string directory = Path.Combine(workspace.Root, "results");

        foreach (string name in BadFileNames)
        {
            _ = Assert.Throws<ArgumentException>(() => new ResultsFileWriter(directory, name));
        }

        _ = Assert.Throws<ArgumentNullException>(() => new ResultsFileWriter(directory, null!));
        _ = Assert.Throws<ArgumentException>(() => new ResultsFileWriter(string.Empty, "20261022T091530Z-nvidia.json"));
    }

    // ---- defence in depth: the redactor ----

    [Fact]
    public async Task Written_file_passes_through_the_redactor()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string leaked = "nvapi-" + string.Concat("Zk7w", "2qm9", "Xv4t", "J8hd");
        string registered = string.Concat("hq3n", "r8v2", "kd5m", "p7ws");
        var redactor = new SecretRedactor();
        Assert.True(redactor.Register(registered));
        ResultsFileWriter writer = WriterIn(workspace, "20261022T091530Z-nvidia.json", redactor: redactor);
        RunDocument document = RunCheckpointFixtures.Document(RunStatus.Running);
        document = document with { Run = document.Run with { Tasks = [leaked, "task-" + registered] } };

        await writer.WriteAsync(document, CancellationToken.None).WaitAsync(HangGuard);

        string text = File.ReadAllText(writer.FilePath);
        Assert.DoesNotContain(leaked, text, StringComparison.Ordinal);
        Assert.DoesNotContain(registered, text, StringComparison.Ordinal);
        Assert.Contains("nvapi-****", text, StringComparison.Ordinal);
        Assert.Equal(["nvapi-****", "task-****"], ReadBack(writer.FilePath).Run.Tasks);
    }

    // ---- the ignore rule ----

    [Fact]
    public void Gitignore_lists_the_partial_files()
    {
        string path = Path.Combine(ConfigurationFixtures.RepositoryRoot(), ".gitignore");
        byte[] bytes = File.ReadAllBytes(path);

        Assert.DoesNotContain((byte)'\r', bytes);
        Assert.Contains("results/*.partial", Encoding.UTF8.GetString(bytes).Split('\n'));
    }

    // ---- fix round: the redactor and the serialised text ----

    [Fact]
    public async Task Keys_that_the_json_encoder_escapes_are_masked_in_the_file()
    {
        // The serializer writes + < > & ' and every non-ASCII character as \uXXXX, so a key with one of them never matches the serialised text as it is.
        string[] keys =
        [
            string.Concat("k7w", "+", "2qm9xv4t"),
            string.Concat("caf", ((char)0x00E9).ToString(), "-r8v2kd5m"),
            string.Concat("a<b>", "&c'", "-p7wshq3n"),
        ];

        foreach (string key in keys)
        {
            using TempWorkspace workspace = TempWorkspace.CreateEmpty();
            var redactor = new SecretRedactor();
            Assert.True(redactor.Register(key));
            ResultsFileWriter writer = WriterIn(workspace, "20261022T091530Z-nvidia.json", redactor: redactor);
            RunDocument document = RunCheckpointFixtures.Document(RunStatus.Running);
            document = document with { Run = document.Run with { Tasks = ["task-" + key] } };

            await writer.WriteAsync(document, CancellationToken.None).WaitAsync(HangGuard);

            string text = File.ReadAllText(writer.FilePath);
            Assert.DoesNotContain(key, text, StringComparison.Ordinal);
            Assert.DoesNotContain(JsonEncodedText.Encode(key).ToString(), text, StringComparison.Ordinal);
            Assert.Equal(["task-****"], ReadBack(writer.FilePath).Run.Tasks);
        }
    }

    [Fact]
    public async Task Redaction_that_breaks_the_json_fails_the_write_and_keeps_the_previous_file()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        var redactor = new SecretRedactor();

        // Only a document with a step holds this text; masking it leaves `****` where a member separator was, which is not JSON.
        Assert.True(redactor.Register("\"retries\": 0,"));
        ResultsFileWriter writer = WriterIn(workspace, "20261022T091530Z-nvidia.json", redactor: redactor);
        await writer.WriteAsync(RunCheckpointFixtures.Document(RunStatus.Running), CancellationToken.None).WaitAsync(HangGuard);
        byte[] first = File.ReadAllBytes(writer.FilePath);

        AgentJoulesException failure = await Assert.ThrowsAsync<AgentJoulesException>(() =>
            writer.WriteAsync(RunCheckpointFixtures.Document(RunStatus.Running, stepCount: 1), CancellationToken.None).WaitAsync(HangGuard));

        Assert.Equal(1, ExitCodeMapper.Map(failure, shutdownRequested: false));
        Assert.DoesNotContain("retries", failure.Message, StringComparison.Ordinal);
        Assert.Equal(first, File.ReadAllBytes(writer.FilePath));
        Assert.Equal([writer.FilePath], Directory.GetFiles(Path.GetDirectoryName(writer.FilePath)!));
    }

    [Fact]
    public void Probe_failure_message_redacts_before_it_cuts()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string key = string.Concat("zk7w", "2qm9", "xv4t", "j8hd");
        var redactor = new SecretRedactor();
        Assert.True(redactor.Register(key));

        // The directory is below a file, so the probe fails; the key starts 5 characters before the 400-character cut of the echoed directory.
        string path = workspace.WriteFile("occupied", "this path is a file") + Path.DirectorySeparatorChar;
        while (path.Length + 101 <= 395)
        {
            path += new string('d', 100) + Path.DirectorySeparatorChar;
        }

        path += new string('d', 395 - path.Length) + key;

        AgentJoulesException failure = Assert.Throws<AgentJoulesException>(() => new ResultsFileWriter(path, "20261022T091530Z-nvidia.json", redactor));

        Assert.DoesNotContain(key[..5], failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(key[^5..], failure.Message, StringComparison.Ordinal);
        Assert.Contains("****", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stale_partial_that_is_a_hard_link_is_not_written_through()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string fileName = "20261022T091530Z-nvidia.json";
        string target = workspace.WriteFile("elsewhere.txt", "precious");
        string partial = Path.Combine(workspace.Root, "results", fileName + ".partial");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(partial)!);
        FolderAccess.CreateHardLink(partial, target);
        ResultsFileWriter writer = WriterIn(workspace, fileName);

        await writer.WriteAsync(RunCheckpointFixtures.Document(RunStatus.Running), CancellationToken.None).WaitAsync(HangGuard);

        Assert.Equal("precious", File.ReadAllText(target));
        Assert.False(File.Exists(partial));
        Assert.Equal(RunStatus.Running, ReadBack(writer.FilePath).Run.Status);
    }

    [Fact]
    public async Task First_write_refuses_to_replace_an_existing_complete_file()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string fileName = "20261022T091530Z-nvidia.json";
        ResultsFileWriter first = WriterIn(workspace, fileName);
        await first.WriteAsync(RunCheckpointFixtures.Document(RunStatus.Completed, stepCount: 3), CancellationToken.None).WaitAsync(HangGuard);
        byte[] complete = File.ReadAllBytes(first.FilePath);

        // A second run of the same tier in the same second gets the same name; its first checkpoint must not replace the finished run.
        ResultsFileWriter second = WriterIn(workspace, fileName);
        AgentJoulesException failure = await Assert.ThrowsAsync<AgentJoulesException>(() =>
            second.WriteAsync(RunCheckpointFixtures.Document(RunStatus.Running), CancellationToken.None).WaitAsync(HangGuard));

        Assert.Equal(1, ExitCodeMapper.Map(failure, shutdownRequested: false));
        Assert.Contains(fileName, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', failure.Message);
        Assert.Equal(complete, File.ReadAllBytes(first.FilePath));
        Assert.Equal([first.FilePath], Directory.GetFiles(Path.GetDirectoryName(first.FilePath)!));

        // The writer that created the file keeps replacing its own file on every later checkpoint.
        await first.WriteAsync(RunCheckpointFixtures.Document(RunStatus.Completed, stepCount: 4), CancellationToken.None).WaitAsync(HangGuard);
        Assert.Equal(4, ReadBack(first.FilePath).Steps.Count);
    }

    [Fact]
    public async Task Failed_first_write_can_be_retried()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        int writes = 0;
        ResultsFileWriter writer = WriterIn(workspace, "20261022T091530Z-nvidia.json", _ =>
        {
            if (++writes == 1)
            {
                throw new IOException("The disk failed.");
            }
        });

        _ = await Assert.ThrowsAsync<IOException>(() => writer.WriteAsync(RunCheckpointFixtures.Document(RunStatus.Running), CancellationToken.None).WaitAsync(HangGuard));
        await writer.WriteAsync(RunCheckpointFixtures.Document(RunStatus.Running), CancellationToken.None).WaitAsync(HangGuard);

        Assert.Equal(RunStatus.Running, ReadBack(writer.FilePath).Run.Status);
    }

    [Fact]
    public void Test_hook_is_not_public()
    {
        Assert.Null(typeof(ResultsFileWriter).GetProperty("BeforeMove", BindingFlags.Public | BindingFlags.Instance));
        Assert.NotNull(typeof(ResultsFileWriter).GetProperty("BeforeMove", BindingFlags.NonPublic | BindingFlags.Instance));
    }

    // ---- fix round: record shape and the shared predicate ----

    [Fact]
    public void Run_flags_are_an_empty_array_when_none_are_given()
    {
        RunDocument document = RunCheckpointFixtures.Document(RunStatus.Running);
        Assert.Empty(FlagsOf(document));

        JsonObject root = JsonNode.Parse(Serialise(document))!.AsObject();
        Assert.Empty(root["run"]!["flags"]!.AsArray());

        // A file that holds null reads back as no flags, and flags that are given survive.
        root["run"]!["flags"] = null;
        Assert.Empty(FlagsOf(JsonSerializer.Deserialize(root.ToJsonString(), Info)!));

        RunDocument flagged = document with { Run = document.Run with { Flags = ["partial-offload"] } };
        Assert.Equal(["partial-offload"], FlagsOf(JsonSerializer.Deserialize(Serialise(flagged), Info)!));
    }

    [Fact]
    public void Totals_gco2e_is_a_required_label_object()
    {
        ParameterInfo parameter = typeof(Totals).GetConstructors().Single().GetParameters().Single(candidate => candidate.Name == "Gco2e");
        Assert.False(parameter.HasDefaultValue, "Gco2e must have no default.");
        Assert.Equal(NullabilityState.NotNull, new NullabilityInfoContext().Create(parameter).WriteState);

        string text = Serialise(RunCheckpointFixtures.Document(RunStatus.Running));
        JsonNode gco2e = JsonNode.Parse(text)!["totals"]!["gco2e"]!;
        Assert.Equal("N/A", (string?)gco2e["label"]);
        Assert.Equal("not-configured", (string?)gco2e["reason"]);
        Assert.Null(gco2e["value"]);

        JsonObject root = JsonNode.Parse(text)!.AsObject();
        _ = root["totals"]!.AsObject().Remove("gco2e");
        _ = Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize(root.ToJsonString(), Info));
    }

    [Fact]
    public void Core_predicate_decides_the_nemotron_rule()
    {
        Assert.True(ModelIdRules.IsNemotron("nvidia/NEMOTRON-3"));
        Assert.True(ModelIdRules.IsNemotron("nemotron"));
        Assert.False(ModelIdRules.IsNemotron("qwen3-32b"));
        Assert.False(ModelIdRules.IsNemotron(string.Empty));
        _ = Assert.Throws<ArgumentNullException>(() => ModelIdRules.IsNemotron(null!));

        string[] mixed = [.. AllNemotron, "qwen3-32b"];
        Assert.False(ModelIdRules.RequiresBaselineSuffix(AllNemotron));
        Assert.True(ModelIdRules.RequiresBaselineSuffix(mixed));
        Assert.False(ModelIdRules.RequiresBaselineSuffix([]));
        _ = Assert.Throws<ArgumentNullException>(() => ModelIdRules.RequiresBaselineSuffix(["qwen3-32b", null!]));
        _ = Assert.Throws<ArgumentNullException>(() => ModelIdRules.RequiresBaselineSuffix(null!));

        // The file name asks the same predicate.
        Assert.Equal(ModelIdRules.RequiresBaselineSuffix(mixed), RunFileName.IsBaseline(mixed));
        Assert.Equal(ModelIdRules.RequiresBaselineSuffix(AllNemotron), RunFileName.IsBaseline(AllNemotron));
    }

    // ---- helpers ----

    private static RunDocument ReadBack(string path) =>
        JsonSerializer.Deserialize(File.ReadAllBytes(path), Info) ?? throw new Xunit.Sdk.XunitException("The file holds null.");

    // A null list fails the test where the type allows it; the run flags are never null.
    private static string[] FlagsOf(RunDocument document) => [.. document.Run.Flags ?? throw new Xunit.Sdk.XunitException("run.flags is null.")];

    private static string[] Names(JsonObject node) => [.. node.Select(member => member.Key)];

    // What the run command does first: construct the writer (the probe), and only then call the endpoint.
    private static async Task RunLikeTheCommandAsync(string directory, FakeHttpMessageHandler handler)
    {
        _ = new ResultsFileWriter(directory, "20261022T091530Z-nvidia.json");

        using var client = new HttpClient(handler, disposeHandler: false);
        using HttpResponseMessage response = await client.PostAsync(new Uri("https://nvidia.fake.test/v1/chat/completions"), new StringContent("{}")).WaitAsync(HangGuard);
        response.EnsureSuccessStatusCode();
    }

    private sealed class DisposableList : IDisposable
    {
        private readonly List<IDisposable> _items = [];

        public void Add(IDisposable item) => _items.Add(item);

        public void Dispose()
        {
            foreach (IDisposable item in _items)
            {
                item.Dispose();
            }

            _items.Clear();
        }
    }
}