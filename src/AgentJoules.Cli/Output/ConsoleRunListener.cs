using AgentJoules.Core.Energy;
using AgentJoules.Core.Results;
using AgentJoules.Workflow.Runs;

namespace AgentJoules.Cli.Output;

/// <summary>
/// The <see cref="IRunListener"/> of the CLI (REQ-017 criterion 6, EDGE-005): every event of a run becomes the console output of <see cref="ConsoleReporter"/>, the step lines and the closing
/// lines of a tier on standard output, the baseline warning on standard error, each through the redacting writers of the command.
/// </summary>
/// <param name="reporter">The reporter of the command.</param>
internal sealed class ConsoleRunListener(ConsoleReporter reporter) : IRunListener
{
    private readonly ConsoleReporter _reporter = reporter ?? throw new ArgumentNullException(nameof(reporter));

    /// <inheritdoc />
    public void StepFinished(StepReading reading) => _reporter.ReportStep(reading);

    /// <inheritdoc />
    public void BaselineMeasured(BaselineResult baseline) => _reporter.ReportBaseline(baseline);

    /// <inheritdoc />
    public void TierCompleted(string displayPath, Totals totals) => _reporter.ReportTierCompleted(displayPath, totals);

    /// <inheritdoc />
    public void CheckpointFailed(string exceptionTypeName, bool previousCheckpointKept) => _reporter.ReportCheckpointFailed(exceptionTypeName, previousCheckpointKept);
}