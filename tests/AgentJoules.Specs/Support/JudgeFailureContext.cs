using System.Text.Json;

using AgentJoules.Core.Cost;
using AgentJoules.Core.Energy;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Core.Security;
using AgentJoules.Core.Tasks;
using AgentJoules.Metering;
using AgentJoules.TestSupport;
using AgentJoules.Workflow.Judge;

using Microsoft.Extensions.AI;

using Xunit;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The state of one AJ-077 scenario (never let a judge failure change the exit code), handed to the step class by Reqnroll context injection. Two kinds of scenario share it. The unit
/// scenario runs the real <see cref="JudgeService"/> over fake provider clients on a fake clock, one of which fails. The command scenarios run the real <c>run</c> command with the
/// production orchestrator and the production judge: the workflow tier has an endpoint of its own that answers well, and the judge tier has another endpoint of its own that answers
/// the status or the reply the scenario says, so only the judge fails. Every wait of the retry layer runs on a fake clock that the test advances, so a 429 that is never lifted takes no
/// wall-clock time. Nothing leaves the process and no process variable is read or set.
/// </summary>
public sealed class JudgeFailureContext : IDisposable
{
    /// <summary>The number of steps of the unit scenario.</summary>
    public const int StepCount = 12;

    private const string TaskName = "summarise";
    private const string TaskPrompt = "Summarise the incident for the on-call engineer.";
    private static readonly string NebiusModel = ConfigurationFixtures.ShippedModelId("nebius");
    private static readonly string NvidiaModel = ConfigurationFixtures.ShippedModelId("nvidia");

    // A reading needs a call of positive length, and a fake clock does not move by itself: every call takes 10 ms on the clock of the run.
    private static readonly TimeSpan CallDuration = TimeSpan.FromMilliseconds(10);

    private readonly TimerTrackingTimeProvider _time = new();
    private readonly InMemoryMetricsSink _judgeSink = new();
    private TempWorkspace? _workspace;
    private int _unitCalls;

    /// <summary>The 1-based number of the judge call that fails in the unit scenario, or 0 for none.</summary>
    public int FailingCall { get; set; }

    /// <summary>What the fake judge replies, in the unit scenario and in a command scenario that answers with a reply.</summary>
    public string Reply { get; set; } = "4";

    /// <summary>The HTTP status the judge endpoint answers on every completion call in a command scenario, or null to answer <see cref="Reply"/>.</summary>
    public int? JudgeStatus { get; set; }

    /// <summary>True when the judge key is absent in a command scenario.</summary>
    public bool JudgeKeyAbsent { get; set; }

    /// <summary>The result of the unit scenario, or null before the judge ran.</summary>
    public JudgeResult? Result { get; private set; }

    /// <summary>The result of the command, or null before it ran.</summary>
    public CliRunResult? Command { get; private set; }

    /// <summary>The fake endpoint of the judge tier in a command scenario, or null before the command ran or when the judge key is absent; it counts the completion calls the judge made.</summary>
    public FakeHttpMessageHandler? JudgeEndpoint { get; private set; }

    /// <summary>The run file of the command scenario, parsed; null before the command ran. Disposed with the context.</summary>
    public JsonDocument? Document { get; private set; }

    /// <summary>The steps of the run file of the command scenario.</summary>
    public JsonElement[] FileSteps => RunOrchestrationFixtures.Steps(Document ?? throw new Xunit.Sdk.XunitException("The command has not run yet."));

    /// <summary>Runs the real judge over <see cref="StepCount"/> ok steps, the call number <see cref="FailingCall"/> of which fails, and keeps the result.</summary>
    /// <returns>A task that ends when the judge has run.</returns>
    public async Task JudgeAsync()
    {
        StepReading[] steps = [.. Enumerable.Range(1, StepCount).Select(static number => JudgeScoringContext.Step(AgentOf(number)))];
        await using var meter = new UnmeteredEnergyMeter(_time);
        await meter.StartAsync(CancellationToken.None);
        var providers = new FakeProviderClientFactory(_time, (_, _) =>
        {
            int call = Interlocked.Increment(ref _unitCalls);
            return call == FailingCall
                ? new FakeChatClient(_time) { Failure = new InvalidOperationException("the judge is down") }
                : new FakeChatClient(_time) { ResponseText = Reply, ResponseUsage = new UsageDetails { InputTokenCount = 100, OutputTokenCount = 1 } };
        });
        var services = new MeteringServices(meter, NoOpMeterGate.Instance, _judgeSink, NotConfiguredCostCalculator.Instance, TestInstruments.Create(), _time);
        var judge = new JudgeService(Tier.Nvidia, JudgeScoringContext.DefaultJudgeModel, "Score one step.\n1. a\n2. b\n3. c\n4. d\n5. e\n", providers, new SecretRedactor(), RetryOptions.Default, new Random(4711));

        Result = await judge.ScoreAsync(new JudgeRequest(Tier.Local, steps, [new TaskDefinition(TaskName, TaskPrompt, 256)], services), CancellationToken.None);
    }

    /// <summary>
    /// Runs <paramref name="arguments"/> with the production orchestrator and the production judge. The workflow tier is the one the command line selects (nvidia or nebius); the judge is on
    /// the other of the two, over its own endpoint, unless <see cref="JudgeKeyAbsent"/>. Keeps the result and the run file.
    /// </summary>
    /// <param name="arguments">The command line, <c>run --tier nvidia --repeat 1</c> for example.</param>
    /// <returns>A task that ends when the command has ended.</returns>
    public async Task RunCommandAsync(string arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(arguments);

        string[] words = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string workflowTier = words[Array.IndexOf(words, "--tier") + 1];
        string judgeTier = workflowTier == "nvidia" ? "nebius" : "nvidia";
        _time.SetUtcNow(RunOrchestrationFixtures.CommandStartUtc);

        _workspace = RunCommandHarness.CreateWorkspace();
        var endpoints = new Dictionary<string, HttpMessageHandler>(StringComparer.Ordinal)
        {
            [workflowTier] = RunOrchestrationFixtures.Endpoint(_time, CallDuration),
        };
        List<(string Key, string? Value)> settings = [];
        if (JudgeKeyAbsent)
        {
            settings.Add((workflowTier == "nvidia" ? ProviderClientFixtures.NebiusKeyVariable : ProviderClientFixtures.NvidiaKeyVariable, null));
        }
        else
        {
            // The models route lists the shipped models and answers 200 either way, so the preflight passes and only the completion calls of the judge fail.
            var judge = new FakeHttpMessageHandler { ModelIds = ConfigurationFixtures.ShippedModelIds(), ChatText = Reply, Clock = _time, DefaultDuration = CallDuration };
            if (JudgeStatus is { } status)
            {
                judge.SetStatuses(FakeRoute.ChatCompletions, status);
            }

            endpoints[judgeTier] = judge;
            JudgeEndpoint = judge;
            settings.Add(("AgentJoules:Judge:Tier", judgeTier));
            settings.Add(("AgentJoules:Judge:ModelId", judgeTier == "nebius" ? NebiusModel : NvidiaModel));
        }

        RunCommandSetup setup = RunOrchestrationFixtures.Production(_workspace.Root, endpoints, [.. settings]);
        setup.UseProductionJudge = true;
        RunOrchestrationFixtures.UseClock(setup, _time);

        Command = await RunOrchestrationFixtures.RunDrivenAsync(arguments, setup, _time).WaitAsync(TimeSpan.FromSeconds(60));
        Document = RunOrchestrationFixtures.Read(Assert.Single(RunOrchestrationFixtures.RunFilesIn(_workspace.Root)));
    }

    private static string AgentOf(int number) => ((number - 1) % 4) switch
    {
        0 => "Planner",
        1 => "Researcher",
        2 => "Writer",
        _ => "Reviewer",
    };

    /// <inheritdoc />
    public void Dispose()
    {
        Document?.Dispose();
        _workspace?.Dispose();
    }
}