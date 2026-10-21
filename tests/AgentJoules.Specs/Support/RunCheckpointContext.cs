using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Reporting.Json;
using AgentJoules.Reporting.Results;
using AgentJoules.TestSupport;

using Microsoft.Extensions.Time.Testing;

using Xunit.Sdk;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The state of one AJ-055 scenario (write run files atomically after every task and repeat), handed to the step class by Reqnroll context injection.
/// It holds the inputs of the file name, the workspace and the writer of the checkpoint scenarios, the serialised document, and the pieces of the
/// unwritable-directory scenario (the directory, the fake endpoint, the exit code). The unwritable-directory scenario runs the real <c>run</c> command through
/// <see cref="RunCommandHarness"/> (AJ-054) with the recording fake endpoint as the nvidia client.
/// </summary>
public sealed class RunCheckpointContext : IDisposable
{
    private TempWorkspace? _workspace;
    private IDisposable? _denial;

    /// <summary>The scenario clock the run file name is read from.</summary>
    public FakeTimeProvider Time { get; } = new();

    /// <summary>The tier of the run whose file name is created.</summary>
    public Tier Tier { get; set; } = Tier.Nvidia;

    /// <summary>The model IDs of the agents of the run whose file name is created; all Nemotron unless a step changes one.</summary>
    public IReadOnlyList<string> ModelIds { get; set; } = [RunCheckpointFixtures.NemotronModel, RunCheckpointFixtures.NemotronModel, RunCheckpointFixtures.NemotronModel, RunCheckpointFixtures.NemotronModel];

    /// <summary>The run file name that was created, or null before the When step.</summary>
    public string? FileName { get; set; }

    /// <summary>The writer of the checkpoint scenarios, or null before a step created it.</summary>
    public ResultsFileWriter? Writer { get; private set; }

    /// <summary>The bytes of the run file after checkpoint 1, or null before it was written.</summary>
    public byte[]? FirstCheckpointBytes { get; set; }

    /// <summary>The failure of the write of checkpoint 2, or null.</summary>
    public Exception? WriteFailure { get; set; }

    /// <summary>The document of the serialisation scenario, or null before the Given step built it.</summary>
    public RunDocument? Document { get; set; }

    /// <summary>The text the document was serialised to, or null before the When step.</summary>
    public string? SerialisedText { get; set; }

    /// <summary>The directory <c>--out</c> names in the unwritable-directory scenario.</summary>
    public string? OutDirectory { get; private set; }

    /// <summary>The fake endpoint of the unwritable-directory scenario; it records every request it receives.</summary>
    public FakeHttpMessageHandler Handler { get; } = new() { ModelIds = ConfigurationFixtures.ShippedModelIds() };

    /// <summary>The exit code of the command, or null before it ran.</summary>
    public int? ExitCode { get; set; }

    /// <summary>The standard error of the command.</summary>
    public string Stderr { get; set; } = string.Empty;

    /// <summary>The workspace of the scenario, created on first use.</summary>
    public TempWorkspace Workspace => _workspace ??= TempWorkspace.CreateEmpty();

    /// <summary>The type information of the run document, from the registration in <see cref="AgentJoulesJsonContext"/>.</summary>
    /// <exception cref="XunitException">The context does not register <see cref="RunDocument"/>.</exception>
    public static JsonTypeInfo<RunDocument> RunDocumentInfo =>
        AgentJoulesJsonContext.Default.GetTypeInfo(typeof(RunDocument)) as JsonTypeInfo<RunDocument>
        ?? throw new XunitException("AgentJoulesJsonContext does not register RunDocument.");

    /// <summary>Creates the writer for <paramref name="fileName"/> in the <c>results</c> folder of the workspace.</summary>
    /// <param name="fileName">The run file name.</param>
    /// <param name="beforeMove">The hook that runs between the finished partial file and the move, or null.</param>
    /// <returns>The writer, which is also <see cref="Writer"/>.</returns>
    public ResultsFileWriter CreateWriter(string fileName, Action<string>? beforeMove = null)
    {
        Writer = new ResultsFileWriter(Path.Combine(Workspace.Root, "results"), fileName) { BeforeMove = beforeMove };
        return Writer;
    }

    /// <summary>Serialises <paramref name="document"/> with the registered context into <see cref="SerialisedText"/>.</summary>
    /// <param name="document">The document.</param>
    public void Serialise(RunDocument document) => SerialisedText = JsonSerializer.Serialize(document, RunDocumentInfo);

    /// <summary>
    /// Chooses the directory of the unwritable-directory scenario: a folder that denies file creation to the current user where the host allows it, and
    /// otherwise a path that is occupied by a file, which cannot become a directory either.
    /// </summary>
    public void UseUnwritableDirectory()
    {
        if (FolderAccess.DenyListingSkipReason() is null)
        {
            OutDirectory = Path.Combine(Workspace.Root, "denied");
            _ = Directory.CreateDirectory(OutDirectory);
            _denial = FolderAccess.DenyWriting(OutDirectory);
        }
        else
        {
            OutDirectory = Workspace.WriteFile("occupied", "this path is a file");
        }
    }

    /// <summary>
    /// Runs <c>run --tier nvidia --out &lt;OutDirectory&gt; --repeat 1</c> through the real <c>run</c> command (<see cref="RunCommandHarness"/>) with the nvidia key and the recording
    /// endpoint <see cref="Handler"/> as the nvidia client, whose models route lists the shipped IDs: had the writer probe not stopped the command, the model check and the completion
    /// request of the orchestrator double would have reached it.
    /// </summary>
    /// <returns>A task that completes when the command ended and <see cref="ExitCode"/> is set.</returns>
    public async Task RunCommandAsync()
    {
        string directory = OutDirectory ?? throw new InvalidOperationException("No output directory was chosen.");
        string arguments = $"run --tier nvidia --out \"{directory}\" --repeat 1";

        RunCommandSetup setup = RunCommandSetup.Create(
            [
                KeyValuePair.Create("NVIDIA_API_KEY", (string?)ProviderClientFixtures.FakeKey()),
                KeyValuePair.Create("AgentJoules:Tiers:nvidia:BaseUrl", (string?)ProviderClientFixtures.BaseUrl(Tier.Nvidia)),
            ],
            [KeyValuePair.Create("nvidia", (HttpMessageHandler)Handler)]);

        CliRunResult result = await RunCommandHarness.RunAsync(arguments, setup);
        ExitCode = result.ExitCode;
        Stderr = result.Stderr;
        OrchestratorRuns = setup.Orchestrator.Runs;
    }

    /// <summary>The requests that reached the fake endpoint (the models route and the completions route together).</summary>
    public int RequestCount => Handler.RequestCount;

    /// <summary>How many times the command reached the orchestrator double, or null before it ran.</summary>
    public int? OrchestratorRuns { get; private set; }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            _denial?.Dispose();
        }
        finally
        {
            _workspace?.Dispose();
        }
    }
}