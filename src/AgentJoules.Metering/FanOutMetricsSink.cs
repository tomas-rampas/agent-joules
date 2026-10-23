using AgentJoules.Core.Results;

using Microsoft.Extensions.Logging;

namespace AgentJoules.Metering;

/// <summary>
/// Forwards every reading to each of its targets in the given order (REQ-032, EDGE-019, design 3.10): the workflow readings of a run with a dashboard reach the sink that keeps them
/// for the results file first and the dashboard second, so the live view sees the same reading instances as the file. A target that throws never reaches the caller or the other
/// targets: its exception is caught, and the first failure of each target is logged at Warning with the type names of the target and the exception only (never a message, which
/// could echo provider text). Later failures of that target are silent. One instance serves one tier run.
/// </summary>
public sealed partial class FanOutMetricsSink : IMetricsSink
{
    private readonly ILogger _logger;
    private readonly IMetricsSink[] _targets;

    // One flag per target: 0 until its first failure was logged.
    private readonly int[] _failureLogged;

    /// <summary>Creates a fan-out over <paramref name="targets"/>, which are copied.</summary>
    /// <param name="logger">The logger of the first failure of each target.</param>
    /// <param name="targets">The targets, in the order every reading reaches them.</param>
    /// <exception cref="ArgumentNullException"><paramref name="logger"/> or <paramref name="targets"/> is null.</exception>
    /// <exception cref="ArgumentException">A target is null.</exception>
    public FanOutMetricsSink(ILogger logger, params IMetricsSink[] targets)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(targets);
        if (Array.Exists(targets, static target => target is null))
        {
            throw new ArgumentException("A target of the fan-out is null.", nameof(targets));
        }

        _logger = logger;
        _targets = [.. targets];
        _failureLogged = new int[_targets.Length];
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="reading"/> is null; no target was called.</exception>
    public void Publish(StepReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);

        for (int i = 0; i < _targets.Length; i++)
        {
            try
            {
                _targets[i].Publish(reading);
            }
            catch (Exception failure)
            {
                if (Interlocked.Exchange(ref _failureLogged[i], 1) == 0)
                {
                    LogFailure(_targets[i].GetType().Name, failure.GetType().Name);
                }
            }
        }
    }

    // A logger that throws must not reach the caller or keep the reading from the other targets: its failure is dropped.
    private void LogFailure(string targetType, string exceptionType)
    {
        try
        {
            TargetFailed(_logger, targetType, exceptionType);
        }
        catch (Exception)
        {
            // Dropped on purpose (EDGE-019).
        }
    }

    [LoggerMessage(EventId = 201, Level = LogLevel.Warning, Message = "A metrics sink target failed ({TargetType}, {ExceptionType}); the reading reached the other targets and later failures of this target are not logged")]
    private static partial void TargetFailed(ILogger logger, string targetType, string exceptionType);
}