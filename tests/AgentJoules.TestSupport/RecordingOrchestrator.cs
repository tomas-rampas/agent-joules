using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Workflow.Runs;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentJoules.TestSupport;

/// <summary>
/// Wraps the production orchestrator of the <c>run</c> command: counts the calls and records every write of every tier's checkpoint writer (the document itself, so its tier, status and
/// steps can be read) before it hands the document to the real writer. A failure rule, when given, makes the writer throw for the documents it selects (after recording them, before the real
/// writer sees them), which is how a test makes a checkpoint write fail. Shared by the unit tests and the scenarios of the run orchestration stories (AJ-056, AJ-057).
/// </summary>
/// <param name="fail">Selects the documents whose write fails, with the failure to raise; null fails none.</param>
public sealed class RecordingOrchestrator(Func<RunDocument, Exception?>? fail = null)
{
    private readonly Func<RunDocument, Exception?>? _fail = fail;
    private readonly Lock _gate = new();
    private readonly List<RunDocument> _documents = [];
    private int _calls;

    /// <summary>The number of times the command asked the orchestrator to run.</summary>
    public int Calls => Volatile.Read(ref _calls);

    /// <summary>Every document written so far, in write order, across all tiers (the ones whose write failed included).</summary>
    public IReadOnlyList<RunDocument> Documents
    {
        get
        {
            lock (_gate)
            {
                return [.. _documents];
            }
        }
    }

    /// <summary>Every write so far, in order: the tier, the status and the step count of the document.</summary>
    public IReadOnlyList<(Tier Tier, RunStatus Status, int Steps)> Writes => [.. Documents.Select(static document => (document.Run.Tier, document.Run.Status, document.Steps.Count))];

    /// <summary>Replaces the orchestrator of the host with the production one behind this recorder.</summary>
    /// <param name="services">The services of the host.</param>
    public void Register(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        _ = services.RemoveAll<IRunOrchestrator>();
        _ = services.AddSingleton<IRunOrchestrator>(provider => new Bound(this, ActivatorUtilities.CreateInstance<RunOrchestrator>(provider)));
    }

    private void CountCall() => Interlocked.Increment(ref _calls);

    private void Record(RunDocument document)
    {
        lock (_gate)
        {
            _documents.Add(document);
        }
    }

    private sealed class Bound(RecordingOrchestrator owner, IRunOrchestrator inner) : IRunOrchestrator
    {
        public Task<int> RunAsync(RunPlan plan, CancellationToken cancellationToken)
        {
            owner.CountCall();
            Dictionary<Tier, IRunCheckpointWriter> writers = plan.Writers.ToDictionary(static pair => pair.Key, pair => (IRunCheckpointWriter)new Writer(owner, pair.Value));
            var recorded = new RunPlan(plan.Tiers, plan.Tasks, plan.Mode, plan.Repeat, plan.BaselineSeconds, writers, plan.Keys, plan.Models, plan.StartUtc, plan.OutputDisplay);
            return inner.RunAsync(recorded, cancellationToken);
        }
    }

    private sealed class Writer(RecordingOrchestrator owner, IRunCheckpointWriter inner) : IRunCheckpointWriter
    {
        public string FilePath => inner.FilePath;

        public Task WriteAsync(RunDocument document, CancellationToken cancellationToken)
        {
            owner.Record(document);
            return owner._fail?.Invoke(document) is { } failure ? Task.FromException(failure) : inner.WriteAsync(document, cancellationToken);
        }
    }
}