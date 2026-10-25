using System.Globalization;

using AgentJoules.Core.Cli;
using AgentJoules.Core.Configuration;
using AgentJoules.Core.Energy;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;

namespace AgentJoules.Cli.Output;

/// <summary>
/// Writes what the CLI tells the user while it runs: results to the output writer and warnings to the error writer. Each report is one
/// whole line per call, so a <see cref="RedactingTextWriter"/> in front of either writer sees complete lines.
/// </summary>
/// <remarks>
/// AJ-030 added the idle-baseline warning; AJ-056 adds the per-step lines and the closing lines of a tier of <c>run</c> on the output writer
/// (<see cref="ReportStep"/>, <see cref="ReportTierCompleted"/>), which the orchestrator reaches through <see cref="ConsoleRunListener"/>; AJ-057 adds the lines of a tier that a failed step
/// stopped (<see cref="ReportTierFailed"/>) and the warning for a partial file that could not be written (<see cref="ReportCheckpointFailed"/>); AJ-077 adds the one warning line per tier run in which the judge failed for some steps (<see cref="ReportJudgeFailed"/>).
/// Numbers are formatted with the invariant culture, never the culture of the machine.
/// </remarks>
/// <param name="output">The writer for results (standard output); not used by the baseline warning.</param>
/// <param name="error">The writer for warnings and diagnostics (standard error).</param>
public sealed class ConsoleReporter(TextWriter output, TextWriter error)
{
    private const string EnergyFormat = "0.###";
    private const string TokenFormat = "0";
    private const string MillisecondFormat = "F0";
    private const string CostFormat = "0.######";

    // The --out value is the operator's own text; it is scrubbed, and cut only when it is far longer than a path. The same cut as the partial-file suffix of the step failure message.
    private const int MaxPathLength = StepFailedException.MaxPartialFileLength;

    // One failure detail of the judge warning: a kind of a few dozen characters, a colon and a reason of at most 120.
    private const int MaxJudgeReasonLength = 200;

    private readonly TextWriter _error = error ?? throw new ArgumentNullException(nameof(error));

    /// <summary>The writer for results: the step lines and the closing lines of a run.</summary>
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

    /// <summary>
    /// States the scope of every joule the measured <c>run</c> prints (prompt honesty rule 6): writes exactly <see cref="ReportSentences.GpuBoardOnly"/> as one line to the output writer. The command
    /// calls it once, before the first tier starts, so the sentence precedes the first step line.
    /// </summary>
    public void ReportEnergyScope() => Output.WriteLine(ReportSentences.GpuBoardOnly);

    /// <summary>
    /// Tells the user what <c>calibrate</c> wrote (REQ-021, AJ-109), as two lines on the output writer: <c>calibration: &lt;path&gt;</c>, the file as the user would type it (the <c>--out</c> value and the file name), and
    /// <c>counter=&lt;J|N/A&gt; sampling=&lt;J|N/A&gt; difference=&lt;value|N/A&gt; window=&lt;s&gt; s samples=&lt;n&gt;</c>: the two totals in joules with two decimals, the relative difference with four, the measured window with one decimal and the
    /// sample count. A method that did not measure shows <c>N/A</c>; the reason is in the file.
    /// </summary>
    /// <param name="displayPath">The path of the file as shown to the user.</param>
    /// <param name="document">The calibration that was written.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public void ReportCalibration(string displayPath, CalibrationDocument document)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayPath);
        ArgumentNullException.ThrowIfNull(document);

        Output.WriteLine("calibration: " + displayPath);
        Output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"counter={Total(document.CounterJoules, "F2")} sampling={Total(document.SamplingJoules, "F2")} difference={Total(document.RelativeDifference, "F4")} window={document.WindowSeconds.ToString("F1", CultureInfo.InvariantCulture)} s samples={document.SampleCount}"));
    }

    // A joule total carries its unit; the dimensionless difference does not; a method that did not measure is N/A.
    private static string Total(Metric metric, string format) =>
        metric.Value is { } value ? value.ToString(format, CultureInfo.InvariantCulture) + (metric.Unit == "J" ? " J" : string.Empty) : "N/A";

    /// <summary>
    /// Tells the user where the live dashboard listens (REQ-032, AJ-091): one line <c>Dashboard: &lt;uri&gt;</c> to the output writer, the loopback URI the server printed. <c>run</c> writes it after the
    /// checks and before the energy-scope sentence, so the scope sentence stays the last line before the first step line.
    /// </summary>
    /// <param name="uri">The URI of the server.</param>
    /// <exception cref="ArgumentNullException"><paramref name="uri"/> is null.</exception>
    public void ReportDashboard(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        Output.WriteLine("Dashboard: " + uri.AbsoluteUri);
    }

    /// <summary>
    /// Warns that the dashboard did not start (EDGE-018): exactly one line to the error writer, <c>Warning: the dashboard did not start (&lt;reason&gt;); the run continues without it</c>; the reason is one
    /// of the fixed reasons of the server (<c>no free dashboard port in 5178-5197</c>, <c>start timed out after 5 s</c>), scrubbed of control characters.
    /// </summary>
    /// <param name="reason">The reason the server gave.</param>
    /// <exception cref="ArgumentException"><paramref name="reason"/> is null or blank.</exception>
    public void ReportDashboardNotStarted(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        _error.WriteLine($"Warning: the dashboard did not start ({TextEcho.ForMessage(reason)}); the run continues without it");
    }

    /// <summary>
    /// Warns that the idle baseline of a measured tier is shorter than 30 s (REQ-021, AJ-106): exactly <c>Warning: baseline shorter than 30 s is not publishable</c> as one line to the error writer. The run goes on and
    /// its files carry the run flag <c>non-publishable</c>. The command calls it once, before the first tier, and only when a measured tier is selected and the baseline is shorter than <see cref="BaselineOptions.PublishableSeconds"/>.
    /// </summary>
    public void ReportShortBaseline() => _error.WriteLine("Warning: baseline shorter than 30 s is not publishable");

    /// <summary>Writes one note of the command (for example the note of a power interval shorter than the sampling period) as one line to the error writer.</summary>
    /// <param name="note">The note.</param>
    /// <exception cref="ArgumentException"><paramref name="note"/> is null or blank.</exception>
    public void ReportNote(string note)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(note);
        _error.WriteLine(note);
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
    /// Shows the user what the models endpoint lists when a configured model ID is not among it (EDGE-008): a header line, <c>available models of nvidia (first 20 of 30):</c> (or
    /// <c>available models of nvidia (12):</c> when nothing was cut), and then one ID per line, indented by two spaces, all to the error writer; for an empty list the one line
    /// <c>no models listed</c>. The error line of the command (<see cref="ErrorLine"/>) is one folded line, so the command host prints the list of IDs here, right after that line.
    /// Each ID is cut and scrubbed again, so a caller that did not echo it cannot put a control character on the terminal. A failure of the configuration check of <c>validate --config</c> (an empty
    /// <see cref="ModelNotFoundException.TierName"/>) has no endpoint list and prints nothing here.
    /// </summary>
    /// <param name="failure">The failure; its <see cref="ModelNotFoundException.AvailableIds"/> are printed in their order.</param>
    /// <exception cref="ArgumentNullException"><paramref name="failure"/> is null.</exception>
    public void ReportModelNotFound(ModelNotFoundException failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        // The configuration check of `validate --config` has no endpoint list: its problem lines were printed by the command and it names no tier.
        if (failure.TierName.Length == 0)
        {
            return;
        }

        if (failure.AvailableCount == 0)
        {
            _error.WriteLine("no models listed");
            return;
        }

        string scope = failure.AvailableCount > failure.AvailableIds.Count
            ? string.Create(CultureInfo.InvariantCulture, $"first {failure.AvailableIds.Count} of {failure.AvailableCount}")
            : failure.AvailableCount.ToString(CultureInfo.InvariantCulture);
        _error.WriteLine($"available models of {TextEcho.ForMessage(failure.TierName)} ({scope}):");
        foreach (string id in failure.AvailableIds)
        {
            _error.WriteLine("  " + TextEcho.ForMessage(id));
        }
    }

    /// <summary>
    /// Writes the line of one finished step to the output writer (REQ-017 criterion 6, design 3.6 "Run console output"):
    /// <c>&lt;task&gt; r&lt;repeat&gt; &lt;agent&gt; &lt;outcome&gt; netJ=&lt;v|N/A&gt; avgW=&lt;v|N/A&gt; in=&lt;v|not-reported&gt; out=&lt;v|not-reported&gt; latency=&lt;ms&gt;</c>.
    /// A value that does not exist prints its label text exactly (<c>N/A</c> or <c>not-reported</c>), never 0; joules and watts print with up to three decimals, tokens as integers and the
    /// latency in whole milliseconds, all in the invariant culture.
    /// </summary>
    /// <param name="reading">The reading of the step.</param>
    /// <exception cref="ArgumentNullException"><paramref name="reading"/> is null.</exception>
    public void ReportStep(StepReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);

        Output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{TextEcho.ForMessage(reading.Task)} r{reading.Repeat} {TextEcho.ForMessage(reading.Agent)} {TextEcho.ForMessage(reading.Outcome)} netJ={Format(reading.NetJoules, EnergyFormat)} avgW={Format(reading.AvgW, EnergyFormat)} in={Format(reading.InputTokens, TokenFormat)} out={Format(reading.OutputTokens, TokenFormat)} latency={Format(reading.LatencyMs, MillisecondFormat)}"));
    }

    /// <summary>
    /// Writes the closing lines of a tier to the output writer, after its step lines: <c>results: &lt;displayPath&gt;</c> and
    /// <c>totals: grossJ=&lt;v|N/A&gt; netJ=&lt;v|N/A&gt; in=&lt;v|not-reported&gt; out=&lt;v|not-reported&gt; cost=&lt;CUR v[,CUR v]|N/A&gt; wallClockMs=&lt;v&gt;</c>, with the formatting rules of
    /// <see cref="ReportStep"/>; the cost lists each currency in ordinal order with up to six decimals and is <c>N/A</c> when no step carried a cost or a step lacked its cost (every currency N/A <c>missing-step-costs</c>).
    /// </summary>
    /// <param name="displayPath">The run file as the user names it (the <c>--out</c> value as given and the file name), never the absolute path.</param>
    /// <param name="totals">The totals of the final document.</param>
    /// <exception cref="ArgumentException"><paramref name="displayPath"/> is null or blank.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="totals"/> is null.</exception>
    public void ReportTierCompleted(string displayPath, Totals totals)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayPath);
        ArgumentNullException.ThrowIfNull(totals);

        // No priced step, or a currency without a value (a step lacked its cost): the run has no cost to show.
        string cost = totals.CostByCurrency.Count == 0 || totals.CostByCurrency.Values.Any(static entry => entry.Value is null)
            ? MetricLabelText.NotApplicable
            : string.Join(',', totals.CostByCurrency.OrderBy(static pair => pair.Key, StringComparer.Ordinal).Select(static pair => TextEcho.ForMessage(pair.Key) + " " + Format(pair.Value, CostFormat)));
        Output.WriteLine("results: " + TextEcho.ForMessage(displayPath, MaxPathLength));
        Output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"totals: grossJ={Format(totals.GrossJoules, EnergyFormat)} netJ={Format(totals.NetJoules, EnergyFormat)} in={Format(totals.InputTokens, TokenFormat)} out={Format(totals.OutputTokens, TokenFormat)} cost={cost} wallClockMs={Format(totals.WallClockMs, MillisecondFormat)}"));
    }

    /// <summary>
    /// Warns that the checkpoint that ends a tier early could not be written: exactly one line to the error writer, naming the exception type only (its message can quote a path). The line is
    /// <c>Warning: the run file could not be written after the shutdown (&lt;Type&gt;); ...</c> for <see cref="CheckpointKind.Shutdown"/> (the <c>incomplete</c> checkpoint) and
    /// <c>Warning: the run file could not be written after the failed step (&lt;Type&gt;); ...</c> for <see cref="CheckpointKind.StepFailure"/> (the <c>failed</c> checkpoint); the ending is
    /// <c>the last complete checkpoint was kept</c> when a checkpoint of the tier is on disk and <c>no checkpoint of this tier was written</c> when none is.
    /// </summary>
    /// <param name="exceptionTypeName">The type name of the failure.</param>
    /// <param name="previousCheckpointKept">True when a checkpoint of the tier was written before.</param>
    /// <param name="kind">Which checkpoint could not be written.</param>
    /// <exception cref="ArgumentException"><paramref name="exceptionTypeName"/> is null or empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is not a defined <see cref="CheckpointKind"/>.</exception>
    public void ReportCheckpointFailed(string exceptionTypeName, bool previousCheckpointKept, CheckpointKind kind)
    {
        ArgumentException.ThrowIfNullOrEmpty(exceptionTypeName);
        string after = kind switch
        {
            CheckpointKind.Shutdown => "the shutdown",
            CheckpointKind.StepFailure => "the failed step",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        string kept = previousCheckpointKept ? "the last complete checkpoint was kept" : "no checkpoint of this tier was written";
        _error.WriteLine($"Warning: the run file could not be written after {after} ({TextEcho.ForMessage(exceptionTypeName)}); {kept}");
    }

    /// <summary>
    /// Tells the user that a failed step stopped the tier and where the partial results are (EDGE-006): two lines to the output writer, <c>results: &lt;displayPath&gt;</c> in the format of
    /// <see cref="ReportTierCompleted"/> and <c>status: failed at &lt;agent&gt;</c> (or <c>status: incomplete at &lt;agent&gt;</c> when the connection dropped after the first update);
    /// no <c>totals:</c> line, because the totals of a stopped tier are not a result. The path and the agent name are scrubbed of control characters and cut when far too long.
    /// </summary>
    /// <param name="displayPath">The partial file as the user names it (the <c>--out</c> value as given and the file name), never the absolute path.</param>
    /// <param name="agent">The name of the agent whose step failed.</param>
    /// <param name="status">The status of the partial file: <see cref="RunStatus.Failed"/> or <see cref="RunStatus.Incomplete"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="displayPath"/> or <paramref name="agent"/> is null or blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="status"/> is neither <see cref="RunStatus.Failed"/> nor <see cref="RunStatus.Incomplete"/>.</exception>
    public void ReportTierFailed(string displayPath, string agent, RunStatus status)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(agent);
        string word = status switch
        {
            RunStatus.Failed => "failed",
            RunStatus.Incomplete => "incomplete",
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        };

        Output.WriteLine("results: " + TextEcho.ForMessage(displayPath, MaxPathLength));
        Output.WriteLine($"status: {word} at {TextEcho.ForMessage(agent)}");
    }

    // A value in the invariant culture, or the label text of a value that does not exist (never 0).
    private static string Format(Metric metric, string format) =>
        metric.Value is { } value ? value.ToString(format, CultureInfo.InvariantCulture) : MetricLabelText.Of(metric.Label);

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

    /// <summary>
    /// Warns that the judge failed for some steps of a tier run (EDGE-012): exactly one line to the error writer, <c>Warning: the judge failed for &lt;f&gt; of &lt;n&gt; steps of tier &lt;tier&gt;
    /// (&lt;kinds&gt;); their quality is N/A and the exit code is unchanged</c>. The kinds are exception type names (with the HTTP status when the failure carried one) and refusal tokens, each scrubbed of control characters and
    /// cut when far too long; with reasons, the parentheses read <c>(&lt;kinds&gt;; reasons: &lt;kind&gt;: &lt;reason&gt;; ...)</c>. A reason is the first line of the message of the failure, masked and cut to 120 characters by the judge;
    /// no other text of a response, no URL, no key and no model text is ever printed, because none of them reaches this method.
    /// </summary>
    /// <param name="tier">The tier whose steps were judged.</param>
    /// <param name="failedSteps">How many judged steps failed.</param>
    /// <param name="judgedSteps">How many steps were sent to the judge.</param>
    /// <param name="failureKinds">The distinct failure kinds, in the order to print.</param>
    /// <param name="failureDetails">
    /// The reason lines, <c>&lt;kind&gt;: &lt;first line of the redacted message&gt;</c>, printed after the kinds as <c>; reasons: a; b</c> inside the parentheses; null or empty prints no reasons. They come from the judge already
    /// redacted and bounded, and each is scrubbed and cut again here.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="failureKinds"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tier"/> is not a defined tier.</exception>
    public void ReportJudgeFailed(Tier tier, int failedSteps, int judgedSteps, IReadOnlyList<string> failureKinds, IReadOnlyList<string>? failureDetails = null)
    {
        ArgumentNullException.ThrowIfNull(failureKinds);

        // The reasons follow the kinds inside the parentheses; each is a line the judge already masked and bounded, scrubbed and cut again here.
        string kinds = string.Join(", ", failureKinds.Select(static kind => TextEcho.ForMessage(kind)));
        string reasons = failureDetails is { Count: > 0 }
            ? "; reasons: " + string.Join("; ", failureDetails.Select(static detail => TextEcho.ForMessage(detail, MaxJudgeReasonLength)))
            : string.Empty;
        _error.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Warning: the judge failed for {failedSteps} of {judgedSteps} steps of tier {TierOptions.NameOf(tier)} ({kinds}{reasons}); their quality is N/A and the exit code is unchanged"));
    }
}