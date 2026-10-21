using System.Globalization;

using AgentJoules.Core.Configuration;
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
    /// Discloses an automatic fallback to sampling: when <see cref="MeterCapabilities.IsFallback"/> is set, writes the one line of
    /// <see cref="MeterCapabilities.Describe"/>, <c>Energy method: sampling (counter unsupported)</c>, to the error writer, never to the output
    /// writer; writes nothing otherwise.
    /// </summary>
    /// <param name="capabilities">The selector outcome.</param>
    /// <exception cref="ArgumentNullException"><paramref name="capabilities"/> is null.</exception>
    public void ReportFallback(MeterCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        if (capabilities.IsFallback)
        {
            _error.WriteLine(capabilities.Describe());
        }
    }

    /// <summary>Tells the user where <c>probe --save</c> wrote its file: one line <c>saved: &lt;path&gt;</c> to the error writer, so standard output stays the probe output.</summary>
    /// <param name="relativePath">The path relative to the content root with forward slashes, for example <c>results/probe-20261019T093015Z.json</c>.</param>
    /// <exception cref="ArgumentException"><paramref name="relativePath"/> is null or empty.</exception>
    public void ReportSaved(string relativePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(relativePath);
        _error.WriteLine("saved: " + relativePath);
    }

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

    /// <summary>
    /// Warns that the judge key is not set outside a dry run (EDGE-011): exactly one line to the error writer, <c>Warning: judge key NVIDIA_API_KEY is not set; the run continues and the
    /// judge will not score it</c>. The name is echoed only when <see cref="VariableNames.IsValid(string?)"/> accepts it, so a value that is not shaped like a name is never printed.
    /// </summary>
    /// <param name="variable">The name of the judge key variable.</param>
    /// <exception cref="ArgumentException"><paramref name="variable"/> is null or empty.</exception>
    public void ReportMissingJudgeKey(string variable)
    {
        ArgumentException.ThrowIfNullOrEmpty(variable);

        string shown = VariableNames.IsValid(variable) ? variable : "(name not shown)";
        _error.WriteLine($"Warning: judge key {shown} is not set; the run continues and the judge will not score it");
    }
}