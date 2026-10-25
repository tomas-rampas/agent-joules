using AgentJoules.Workflow.Runs;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentJoules.TestSupport;

/// <summary>
/// The one recorder of the operations of a local run (AJ-106, REQ-021): the fake NVML appends <see cref="Probe"/> when a GPU session starts, the clock of the run appends <see cref="MeterStart"/> when the meter's
/// sampling timer is created and <see cref="Baseline"/> when the one-shot wait of the idle baseline is created, and the fake endpoints append <see cref="WarmUp"/> for the unmetered load call and
/// <see cref="TaskCall"/> for every completion of the workflow. A test asserts the exact sequence. The preflight opens the GPU once before the run starts; <see cref="Register"/> puts a marker between that and the
/// orchestrator, so the sequence of a run starts at the probe of the orchestrator. Thread-safe.
/// </summary>
public sealed class OperationRecorder
{
    /// <summary>The GPU session of the orchestrator started (the probe).</summary>
    public const string Probe = "probe";

    /// <summary>The sampling timer of the meter was created (the meter started).</summary>
    public const string MeterStart = "meter-start";

    /// <summary>The unmetered warm-up call reached the endpoint: Ollama <c>POST /api/generate</c> or the short chat completion of the NIM container.</summary>
    public const string WarmUp = "warm-up";

    /// <summary>The one-shot wait of the idle baseline window was created.</summary>
    public const string Baseline = "baseline";

    /// <summary>A completion of the workflow (a step of a task) reached the endpoint.</summary>
    public const string TaskCall = "task-call";

    private readonly Lock _gate = new();
    private readonly List<string> _operations = [];

    /// <summary>Every operation recorded so far in the current run, in order.</summary>
    public IReadOnlyList<string> Operations
    {
        get
        {
            lock (_gate)
            {
                return [.. _operations];
            }
        }
    }

    /// <summary>The sequence as a test prints it: operations in order, a run of equal ones written once with its count, for example <c>probe, meter-start, warm-up, baseline, task-call x4</c>.</summary>
    /// <returns>The text; empty when nothing was recorded.</returns>
    public string Describe()
    {
        List<string> parts = [];
        string[] operations = [.. Operations];
        for (int i = 0; i < operations.Length;)
        {
            int j = i;
            while (j < operations.Length && string.Equals(operations[j], operations[i], StringComparison.Ordinal))
            {
                j++;
            }

            parts.Add(j - i == 1 ? operations[i] : $"{operations[i]} x{j - i}");
            i = j;
        }

        return string.Join(", ", parts);
    }

    /// <summary>Appends one operation.</summary>
    /// <param name="operation">One of the constants of this class.</param>
    public void Record(string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        lock (_gate)
        {
            _operations.Add(operation);
        }
    }

    /// <summary>The number of times <paramref name="operation"/> was recorded.</summary>
    /// <param name="operation">One of the constants of this class.</param>
    /// <returns>The count.</returns>
    public int Count(string operation) => Operations.Count(recorded => string.Equals(recorded, operation, StringComparison.Ordinal));

    /// <summary>Forgets everything recorded so far; the run marker of <see cref="Register"/> calls it when the orchestrator starts.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _operations.Clear();
        }
    }

    /// <summary>Replaces the orchestrator of the host with the production one behind a marker that clears this recorder when the run starts, so the preflight's own GPU session is not part of the sequence.</summary>
    /// <param name="services">The services of the host.</param>
    public void Register(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        _ = services.RemoveAll<IRunOrchestrator>();
        _ = services.AddSingleton<IRunOrchestrator>(provider => new RunMarker(this, ActivatorUtilities.CreateInstance<RunOrchestrator>(provider)));
    }

    private sealed class RunMarker(OperationRecorder owner, IRunOrchestrator inner) : IRunOrchestrator
    {
        public Task<int> RunAsync(RunPlan plan, CancellationToken cancellationToken)
        {
            owner.Clear();
            return inner.RunAsync(plan, cancellationToken);
        }
    }
}