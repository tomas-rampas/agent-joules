using System.Text.Json.Serialization;

using AgentJoules.Core.Gpu;
using AgentJoules.Core.Labels;

using static AgentJoules.Core.Energy.EnergyShapeRules;

namespace AgentJoules.Core.Results;

/// <summary>
/// The content of <c>results/calibration-&lt;yyyyMMddTHHmmssZ&gt;.json</c> (REQ-021, AJ-109): one window of the task workflow measured by two methods at once, the energy counter and power sampling, with both totals
/// and their relative difference. The totals and the difference are label objects: <c>measured</c> numbers, or <c>N/A</c> with the reason when a method could not measure the window; nothing is estimated.
/// </summary>
/// <remarks>
/// <para>
/// The plain fields are the task, the configured <c>seconds</c>, the measured <c>windowSeconds</c> (the wall time of the window, which ends after the repeat that was in flight at the deadline), the
/// <c>sampleCount</c> and <c>sampleRateHz</c> of the sampling, the completed <c>repeats</c>, the window's <c>startUtc</c> and <c>endUtc</c>, the tool version and commit as a run file has them, the
/// <c>probe</c> of the GPU and the <see cref="CalibrationEnvironment"/>. The file's name carries the start of the command, which is earlier than <c>startUtc</c> by the warm-up.
/// </para>
/// <para>
/// <see cref="CounterJoules"/> and <see cref="SamplingJoules"/> are measured in joules (unit <c>J</c>); <see cref="RelativeDifference"/> is <c>(counter - sampling) / counter</c>, a dimensionless
/// measured number (unit <c>ratio</c>). The model rejects a metric of any other label or unit on construction and on read.
/// </para>
/// </remarks>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CalibrationDocument
{
    /// <summary>The unit of <see cref="RelativeDifference"/>: a pure ratio.</summary>
    public const string RatioUnit = "ratio";

    /// <summary>The reason of an N/A counter total and difference when the GPU has no usable energy counter (the counter total carries the probe's reason after a colon).</summary>
    public const string CounterNotSupportedReason = "counter-not-supported";

    /// <summary>The reason of an N/A difference when the counter measured 0 J over the window.</summary>
    public const string CounterZeroReason = "counter-zero";

    /// <summary>The reason of an N/A sampling total and difference when the window holds no power sample.</summary>
    public const string NoSamplesReason = "no-samples";

    /// <summary>The reason of an N/A counter total and difference when the counter read lower at the end of the window than at its start.</summary>
    public const string CounterWentBackwardsReason = "counter-went-backwards";

    private const string JouleUnit = "J";

    /// <summary>Creates a calibration document.</summary>
    /// <param name="task">The name of the task whose workflow ran in the window.</param>
    /// <param name="seconds">The configured length of the window, <c>--seconds</c>; 1 or more.</param>
    /// <param name="windowSeconds">The measured wall time of the window; positive and finite.</param>
    /// <param name="sampleCount">The power samples of the window; not negative.</param>
    /// <param name="sampleRateHz">The configured sampling rate; positive and finite.</param>
    /// <param name="repeats">The completed repeats of the workflow; 1 or more.</param>
    /// <param name="startUtc">The instant the window opened.</param>
    /// <param name="endUtc">The instant the window closed; not before <paramref name="startUtc"/>.</param>
    /// <param name="toolVersion">The version of the tool that wrote the file.</param>
    /// <param name="gitCommit">The commit the tool was built from, or <c>unknown</c>.</param>
    /// <param name="counterJoules">The energy counter's total for the window: measured joules, or N/A with a reason.</param>
    /// <param name="samplingJoules">The power-sampling integral over the window: measured joules, or N/A with a reason.</param>
    /// <param name="relativeDifference">(counter - sampling) / counter: a measured ratio, or N/A with a reason.</param>
    /// <param name="probe">The probe of the GPU that was measured.</param>
    /// <param name="environment">The runtime and power-source facts.</param>
    /// <exception cref="ArgumentNullException">A reference argument is null.</exception>
    /// <exception cref="ArgumentException">A text is blank, a number is outside its range, the window ends before it starts, or a metric is not a measured number of its unit or N/A.</exception>
    [JsonConstructor]
    public CalibrationDocument(
        string task,
        int seconds,
        double windowSeconds,
        int sampleCount,
        double sampleRateHz,
        int repeats,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        string toolVersion,
        string gitCommit,
        Metric counterJoules,
        Metric samplingJoules,
        Metric relativeDifference,
        ProbeResult probe,
        CalibrationEnvironment environment)
    {
        RequireText(nameof(task), task);
        RequireText(nameof(toolVersion), toolVersion);
        RequireText(nameof(gitCommit), gitCommit);
        ArgumentNullException.ThrowIfNull(counterJoules);
        ArgumentNullException.ThrowIfNull(samplingJoules);
        ArgumentNullException.ThrowIfNull(relativeDifference);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(environment);

        if (!probe.GpuPresent)
        {
            throw new ArgumentException("A calibration measures a GPU, so its probe must have found one.", nameof(probe));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(seconds, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(sampleCount);
        ArgumentOutOfRangeException.ThrowIfLessThan(repeats, 1);
        if (!double.IsFinite(windowSeconds) || windowSeconds <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(windowSeconds), "The window must be positive and finite.");
        }

        if (!double.IsFinite(sampleRateHz) || sampleRateHz <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz), "The sample rate must be positive and finite.");
        }

        if (endUtc < startUtc)
        {
            throw new ArgumentException("The window must not end before it starts.", nameof(endUtc));
        }

        RequireShape(nameof(counterJoules), counterJoules, JouleUnit);
        RequireShape(nameof(samplingJoules), samplingJoules, JouleUnit);
        RequireShape(nameof(relativeDifference), relativeDifference, RatioUnit);
        RequireNotReportedAbsent(nameof(counterJoules), counterJoules);
        RequireNotReportedAbsent(nameof(samplingJoules), samplingJoules);
        RequireNotReportedAbsent(nameof(relativeDifference), relativeDifference);

        Task = task;
        Seconds = seconds;
        WindowSeconds = windowSeconds;
        SampleCount = sampleCount;
        SampleRateHz = sampleRateHz;
        Repeats = repeats;
        StartUtc = startUtc;
        EndUtc = endUtc;
        ToolVersion = toolVersion;
        GitCommit = gitCommit;
        CounterJoules = counterJoules;
        SamplingJoules = samplingJoules;
        RelativeDifference = relativeDifference;
        Probe = probe;
        Environment = environment;
    }

    /// <summary>The name of the task whose workflow ran in the window.</summary>
    public string Task { get; }

    /// <summary>The configured length of the window in seconds (<c>--seconds</c>).</summary>
    public int Seconds { get; }

    /// <summary>The measured wall time of the window in seconds; it is at least <see cref="Seconds"/> because the window ends after the repeat in flight at the deadline.</summary>
    public double WindowSeconds { get; }

    /// <summary>The power samples of the window, its two edge samples included.</summary>
    public int SampleCount { get; }

    /// <summary>The configured sampling rate in hertz (<c>Meter:SampleRateHz</c>).</summary>
    public double SampleRateHz { get; }

    /// <summary>The completed repeats of the task workflow in the window.</summary>
    public int Repeats { get; }

    /// <summary>The instant the window opened.</summary>
    public DateTimeOffset StartUtc { get; }

    /// <summary>The instant the window closed.</summary>
    public DateTimeOffset EndUtc { get; }

    /// <summary>The version of the tool that wrote the file.</summary>
    public string ToolVersion { get; }

    /// <summary>The commit the tool was built from, or <c>unknown</c>.</summary>
    public string GitCommit { get; }

    /// <summary>The energy counter's total for the window in joules (measured), or N/A with a reason.</summary>
    public Metric CounterJoules { get; }

    /// <summary>The integral of the sampled power over the window in joules (measured), or N/A with a reason.</summary>
    public Metric SamplingJoules { get; }

    /// <summary>(counter - sampling) / counter as a measured ratio, or N/A with a reason.</summary>
    public Metric RelativeDifference { get; }

    /// <summary>The probe of the GPU that was measured.</summary>
    public ProbeResult Probe { get; }

    /// <summary>The runtime and power-source facts of the calibration.</summary>
    public CalibrationEnvironment Environment { get; }

    /// <summary>
    /// The relative difference of the two totals (REQ-021): <c>(counter - sampling) / counter</c> as a measured ratio when both totals are numbers and the counter is not 0. An N/A counter total makes the
    /// difference N/A with the counter's reason up to its colon (<c>counter-not-supported</c>), a counter of exactly 0 J gives <see cref="CounterZeroReason"/>, and an N/A sampling total gives its own reason
    /// (<see cref="NoSamplesReason"/>); the counter is looked at first. Nothing is estimated or clamped: a sampling total above the counter gives a negative difference.
    /// </summary>
    /// <param name="counterJoules">The counter total.</param>
    /// <param name="samplingJoules">The sampling total.</param>
    /// <returns>The difference.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static Metric DifferenceOf(Metric counterJoules, Metric samplingJoules)
    {
        ArgumentNullException.ThrowIfNull(counterJoules);
        ArgumentNullException.ThrowIfNull(samplingJoules);

        return counterJoules.Value is not { } counter
            ? Metric.NotApplicable(Prefix(counterJoules.Reason))
            : counter == 0.0
            ? Metric.NotApplicable(CounterZeroReason)
            : samplingJoules.Value is not { } sampling
            ? Metric.NotApplicable(Prefix(samplingJoules.Reason))
            : Metric.Measured((counter - sampling) / counter, RatioUnit);
    }

    // The reason of a total up to its colon: "counter-not-supported: <probe reason>" stands for "counter-not-supported". A total with no reason cannot be N/A, so the fallback is never reached by a valid metric.
    private static string Prefix(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return CounterNotSupportedReason;
        }

        int colon = reason.IndexOf(':', StringComparison.Ordinal);
        return colon > 0 ? reason[..colon] : reason;
    }

    private static void RequireText(string parameter, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"The {parameter} must not be null or blank.", parameter);
        }
    }

    // A calibration file says "N/A" for a method that did not measure, never "not-reported": the vocabulary of this file has one word for an absent method.
    private static void RequireNotReportedAbsent(string parameter, Metric metric)
    {
        if (metric.Label == MetricLabel.NotReported)
        {
            throw new ArgumentException($"The {parameter} must be measured or N/A, but it is not-reported.", parameter);
        }
    }
}