using AgentJoules.Core.Results;
using AgentJoules.Metering;

using Microsoft.Extensions.Logging;

namespace AgentJoules.Workflow.Runs;

/// <summary>
/// The sink of one tier run: keeps every reading in <paramref name="inner"/> and tells <paramref name="listener"/> when a reading is published, so a step line appears when its step
/// finishes, and it carries every other listener call of the tier run too (<see cref="Tell"/>). The reading path never depends on the listener: the reading is kept first, a listener
/// failure is caught (the first one of the tier run is logged at Debug with its type only) and never reaches the metered client, whose instruments record the reading right after the publish. A <c>cancelled</c> reading published while the run token is cancelled is kept and not told:
/// the shutdown path tells its <c>incomplete</c> copy after the checkpoint is written. A <c>cancelled</c> reading published while the token is not cancelled (a consumer that walked away) is
/// told like any other. One lock keeps the lines of parallel branches whole.
/// </summary>
/// <param name="inner">The sink that keeps every reading of the tier run.</param>
/// <param name="listener">The listener of the run.</param>
/// <param name="logger">The logger of the first listener failure.</param>
/// <param name="runToken">The run token: cancelled by Ctrl+C, SIGTERM or host shutdown.</param>
internal sealed class RunListenerSink(InMemoryMetricsSink inner, IRunListener listener, ILogger logger, CancellationToken runToken) : IMetricsSink
{
    private readonly InMemoryMetricsSink _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly IRunListener _listener = listener ?? throw new ArgumentNullException(nameof(listener));
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly Lock _listenerLock = new();
    // Guarded by _listenerLock.
    private bool _failureLogged;

    /// <inheritdoc />
    public void Publish(StepReading reading)
    {
        _inner.Publish(reading);
        if (reading.Outcome == StepOutcomes.Cancelled && runToken.IsCancellationRequested)
        {
            return;
        }

        Tell(listener => listener.StepFinished(reading));
    }

    /// <summary>
    /// Calls <paramref name="call"/> on the listener under the one lock of the tier run and swallows any failure, because a console that cannot be written must not change a reading, a
    /// checkpoint or the exit code. Every listener call of the tier run goes through here (the step lines of the sink and the orchestrator's own calls), so one flag covers them all: the
    /// first failure of the tier run is logged at Debug with its exception type only, and later ones are not logged.
    /// </summary>
    /// <param name="call">The listener call.</param>
    internal void Tell(Action<IRunListener> call)
    {
        lock (_listenerLock)
        {
            try
            {
                call(_listener);
            }
            catch (Exception failure)
            {
                if (!_failureLogged)
                {
                    _failureLogged = true;
                    string type = failure.GetType().Name;
                    RunLog.ListenerFailed(_logger, type);
                }
            }
        }
    }
}