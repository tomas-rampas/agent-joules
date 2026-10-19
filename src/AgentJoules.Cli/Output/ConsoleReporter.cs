using System.Globalization;

using AgentJoules.Core.Energy;
using AgentJoules.Core.Labels;

namespace AgentJoules.Cli.Output;

/// <summary>
/// Writes what the CLI tells the user while it runs: results to the output writer and warnings to the error writer. Each report is one
/// whole line per call, so a <see cref="RedactingTextWriter"/> in front of either writer sees complete lines.
/// </summary>
/// <remarks>
/// AJ-030 adds the idle-baseline warning; AJ-034 extends this class with the rest of the console output (the run header and the
/// per-step lines), which is why it already takes both writers. Numbers are formatted with the invariant culture, never the culture of
/// the machine.
/// </remarks>
/// <param name="output">The writer for results (standard output); not used by the baseline warning.</param>
/// <param name="error">The writer for warnings and diagnostics (standard error).</param>
public sealed class ConsoleReporter(TextWriter output, TextWriter error)
{
    private readonly TextWriter _error = error ?? throw new ArgumentNullException(nameof(error));

    /// <summary>The writer for results; kept for the AJ-034 console output.</summary>
    internal TextWriter Output { get; } = output ?? throw new ArgumentNullException(nameof(output));

    /// <summary>
    /// Warns about an unstable idle baseline: when <see cref="ReadingFlags.BaselineUnstable"/> is set, writes exactly one line to the
    /// error writer, <c>Warning: idle baseline unstable (CV 0.20 &gt; 0.10)</c>, with the coefficient of variation and the threshold
    /// to two decimals; writes nothing otherwise (a stable baseline, and a baseline that was not reported, have no flag).
    /// </summary>
    /// <param name="baseline">The measured baseline.</param>
    /// <exception cref="ArgumentNullException"><paramref name="baseline"/> is null.</exception>
    public void ReportBaseline(BaselineResult baseline)
    {
        ArgumentNullException.ThrowIfNull(baseline);

        if (!baseline.Flags.HasFlag(ReadingFlags.BaselineUnstable) || baseline.CoefficientOfVariation is not { } coefficient)
        {
            return;
        }

        _error.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Warning: idle baseline unstable (CV {coefficient:F2} > {baseline.MaxCoefficientOfVariation:F2})"));
    }
}