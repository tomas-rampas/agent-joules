using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

using AgentJoules.Core.Energy;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Runs;

using static AgentJoules.Core.Energy.EnergyShapeRules;

namespace AgentJoules.Core.Results;

/// <summary>
/// The reading of one metered call, which is one step of a workflow run (REQ-008, design 3.2). It mirrors the step skeleton of the results
/// file member for member, so it is written to the file as it is, camelCase through the serializer context. Every number that can be absent
/// is a <see cref="Metric"/> (a value that does not exist is null with the label N/A or not-reported and a reason, never 0); the plain
/// members are identifiers, timestamps, the outcome and counts that always exist.
/// </summary>
/// <remarks>
/// <para>
/// The constructor checks every invariant, so a reading that exists is well formed, and a results document read back is held to the same rules.
/// The identity is complete, both timestamps are set and UTC and the end is not before the start, the retry count and the wait are not negative,
/// the outcome is one of <see cref="StepOutcomes"/>, and the flags are in the vocabulary. The energy members follow the shape rules of
/// <see cref="EnergyReading"/> (labels measured, not-reported or N/A, the units J and W, a negative net together with the below-baseline flag,
/// the sampling-fallback flag only with the sampling method, throttle reasons from the five names together with the throttled flag). A hosted tier
/// (nvidia, nebius) holds N/A with the reason <c>hosted-tier</c> in all four energy metrics, no method, samples, rate or throttle reasons and none
/// of the energy flags, because its GPU is not measured; a local or cloudgpu reading never carries that reason and names its method whenever the
/// gross energy is measured. Quality and the two derived metrics are filled by the judge in a later story; until then they are N/A with the reason
/// <c>not-scored</c>, never a number. A document with a member this type does not know is rejected, and one that omits a member without a default
/// is rejected when the serializer options set <c>RespectRequiredConstructorParameters</c>.
/// </para>
/// <para>
/// <see cref="OutputText"/> lives in memory only, for the judge; it is never serialized and never printed by <see cref="object.ToString"/>, and the
/// file keeps <see cref="OutputSha256"/> and <see cref="OutputChars"/> instead. <see cref="Window"/> lives in memory only too, for the run assembler (the
/// <c>shared-window</c> flag and the union of the windows of a concurrent run, REQ-013); it is never serialized and never printed either.
/// </para>
/// </remarks>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StepReading
{
    private const string NotScored = "not-scored";
    private const int Sha256HexLength = 64;

    /// <summary>Creates a reading after checking every invariant.</summary>
    /// <param name="task">The task name; not blank.</param>
    /// <param name="repeat">The repeat number, from 1.</param>
    /// <param name="agent">The agent name; not blank.</param>
    /// <param name="modelId">The model the call went to; not blank.</param>
    /// <param name="tier">The tier of the call; a defined value.</param>
    /// <param name="startUtc">When the call started; UTC and set.</param>
    /// <param name="endUtc">When the call ended; UTC, set and not before <paramref name="startUtc"/>.</param>
    /// <param name="outcome">One of <see cref="StepOutcomes"/>.</param>
    /// <param name="latencyMs">The call duration in milliseconds.</param>
    /// <param name="ttftMs">The time to the first token in milliseconds: measured, or N/A (reason <c>buffered-call</c> for a buffered call); never not-reported.</param>
    /// <param name="inputTokens">The prompt tokens the provider reported.</param>
    /// <param name="outputTokens">The completion tokens the provider reported.</param>
    /// <param name="grossJoules">The gross GPU energy of the window; N/A with the reason <c>hosted-tier</c> on a hosted tier.</param>
    /// <param name="netJoules">The gross energy minus the idle baseline; N/A with the reason <c>hosted-tier</c> on a hosted tier.</param>
    /// <param name="avgW">The average power over the window; N/A with the reason <c>hosted-tier</c> on a hosted tier.</param>
    /// <param name="peakW">The peak power in the window; N/A with the reason <c>hosted-tier</c> on a hosted tier.</param>
    /// <param name="cost">The cost of the call; the price table fills it in a later story.</param>
    /// <param name="retries">How many times the call was retried; not negative.</param>
    /// <param name="waitMs">How long the retries waited in milliseconds; not negative.</param>
    /// <param name="flags">The qualifiers of the reading; only bits of the vocabulary, and none that names a measurement on a hosted tier.</param>
    /// <param name="method">The method that supplied the gross energy; null when none did, always on a hosted tier, required whenever the gross energy is measured, and never <see cref="EnergyMethod.Unmetered"/>.</param>
    /// <param name="sampleCount">The number of power samples in the window; null when there were none to count; not negative.</param>
    /// <param name="sampleRateHz">The sampling rate in hertz; null when there was no sampling; positive and finite.</param>
    /// <param name="throttleReasons">The throttle reasons seen in the window, or null when none can be said; copied, from the five throttle names in bit order, and non-empty exactly when the throttled flag is set.</param>
    /// <param name="quality">The judge score; null means N/A with the reason <c>not-scored</c>.</param>
    /// <param name="qualityReason">Why there is no score; null with a null <paramref name="quality"/> becomes <c>not-scored</c>.</param>
    /// <param name="joulesPerQualityPoint">Net joules per quality point; null means N/A with the reason <c>not-scored</c>.</param>
    /// <param name="costPerQualityPoint">Cost per quality point; null means N/A with the reason <c>not-scored</c>.</param>
    /// <param name="outputSha256">The lowercase hexadecimal SHA-256 of the output text; null when there is none.</param>
    /// <param name="outputChars">The length of the output text in characters; null when there is none; not negative.</param>
    /// <param name="outputText">The output text, kept in memory for the judge and never serialized.</param>
    /// <param name="window">The meter's window of this step, kept in memory for the run assembler and never serialized; null when it is not known (a reading read back from a file); when set, both bounds are finite and the stop is after the start.</param>
    /// <exception cref="ArgumentNullException">A metric is null.</exception>
    /// <exception cref="ArgumentException">Another argument violates the rule named above.</exception>
    [JsonConstructor]
    public StepReading(
        string task,
        int repeat,
        string agent,
        string modelId,
        Tier tier,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        string outcome,
        Metric latencyMs,
        Metric ttftMs,
        Metric inputTokens,
        Metric outputTokens,
        Metric grossJoules,
        Metric netJoules,
        Metric avgW,
        Metric peakW,
        Metric cost,
        int retries,
        long waitMs,
        ReadingFlags flags,
        EnergyMethod? method = null,
        int? sampleCount = null,
        double? sampleRateHz = null,
        IReadOnlyList<string>? throttleReasons = null,
        Metric? quality = null,
        string? qualityReason = null,
        Metric? joulesPerQualityPoint = null,
        Metric? costPerQualityPoint = null,
        string? outputSha256 = null,
        int? outputChars = null,
        string? outputText = null,
        WindowSpan? window = null)
    {
        RequireText(nameof(task), task);
        RequireText(nameof(agent), agent);
        RequireText(nameof(modelId), modelId);

        if (repeat < 1)
        {
            throw Rejected(nameof(repeat), $"The repeat number counts from 1, but it is {repeat}.");
        }

        if (!Enum.IsDefined(tier))
        {
            throw Rejected(nameof(tier), $"The tier {(int)tier} is not a defined tier.");
        }

        RequireUtc(nameof(startUtc), startUtc);
        RequireUtc(nameof(endUtc), endUtc);
        if (endUtc < startUtc)
        {
            throw Rejected(nameof(endUtc), $"The end of the step is before its start.");
        }

        if (!StepOutcomes.IsKnown(outcome))
        {
            throw Rejected(nameof(outcome), $"The outcome must be ok, cancelled, error or incomplete, but it is {(outcome is null ? "null" : TextEcho.ForMessage(outcome))}.");
        }

        if (retries < 0)
        {
            throw Rejected(nameof(retries), $"The retry count must not be negative, but it is {retries}.");
        }

        if (waitMs < 0)
        {
            throw Rejected(nameof(waitMs), $"The wait must not be negative, but it is {waitMs} ms.");
        }

        ArgumentNullException.ThrowIfNull(latencyMs);
        ArgumentNullException.ThrowIfNull(ttftMs);
        ArgumentNullException.ThrowIfNull(inputTokens);
        ArgumentNullException.ThrowIfNull(outputTokens);
        ArgumentNullException.ThrowIfNull(grossJoules);
        ArgumentNullException.ThrowIfNull(netJoules);
        ArgumentNullException.ThrowIfNull(avgW);
        ArgumentNullException.ThrowIfNull(peakW);
        ArgumentNullException.ThrowIfNull(cost);

        // A time to the first token exists or does not apply (a buffered call has no first token); "not reported" is never the honest answer.
        if (ttftMs.Label is not (MetricLabel.Measured or MetricLabel.NotApplicable))
        {
            throw Rejected(nameof(ttftMs), $"The time to the first token must be measured or N/A, but it is {ttftMs.Label}.");
        }

        if (!ReadingFlagNames.IsKnown(flags))
        {
            throw Rejected(nameof(flags), $"The flags value {(int)flags} has a bit outside the reading-flag vocabulary.");
        }

        if (method is { } stored && (!Enum.IsDefined(stored) || stored == EnergyMethod.Unmetered))
        {
            throw Rejected(nameof(method), $"The method must be counter, sampling or absent, but it is {(int)stored}; a step that measured nothing stores no method.");
        }

        if (sampleCount is < 0)
        {
            throw Rejected(nameof(sampleCount), $"The sample count must not be negative, but it is {sampleCount}.");
        }

        if (sampleRateHz is { } rate && (!double.IsFinite(rate) || rate <= 0.0))
        {
            throw Rejected(nameof(sampleRateHz), $"The sample rate must be positive and finite, but it is {rate} Hz.");
        }

        // The shape rules of EnergyReading: the same labels, units, flag pairings and throttle vocabulary, whoever builds the record.
        RequireShape(nameof(grossJoules), grossJoules, JouleUnit);
        RequireShape(nameof(netJoules), netJoules, JouleUnit);
        RequireShape(nameof(avgW), avgW, WattUnit);
        RequireShape(nameof(peakW), peakW, WattUnit);
        RequireNetFlagPairing(netJoules, flags);
        RequireFallbackMethod(flags, method);
        ReadOnlyCollection<string>? reasons = CopyReasons(throttleReasons);
        RequireThrottleShape(flags, reasons);

        if (TierKinds.IsHosted(tier))
        {
            // A hosted tier has no GPU of ours to measure: its energy is N/A with one reason, and nothing about samples, a method or a measurement flag can be stated.
            RequireHostedNotApplicable(nameof(grossJoules), grossJoules);
            RequireHostedNotApplicable(nameof(netJoules), netJoules);
            RequireHostedNotApplicable(nameof(avgW), avgW);
            RequireHostedNotApplicable(nameof(peakW), peakW);
            RequireAbsent(nameof(method), method is null);
            RequireAbsent(nameof(sampleCount), sampleCount is null);
            RequireAbsent(nameof(sampleRateHz), sampleRateHz is null);
            RequireAbsent(nameof(throttleReasons), reasons is null);
            RequireNoEnergyFlags(flags);
        }
        else
        {
            RequireNotHostedReason(nameof(grossJoules), grossJoules);
            RequireNotHostedReason(nameof(netJoules), netJoules);
            RequireNotHostedReason(nameof(avgW), avgW);
            RequireNotHostedReason(nameof(peakW), peakW);
            if (grossJoules.Label == MetricLabel.Measured && method is null)
            {
                throw Rejected(nameof(method), $"A measured gross energy needs the method that measured it.");
            }
        }

        ValidateOutputFingerprint(outputSha256, outputChars);
        if (window is { } span && !(double.IsFinite(span.StartSeconds) && double.IsFinite(span.StopSeconds) && span.StopSeconds > span.StartSeconds))
        {
            throw Rejected(nameof(window), $"The window must have finite bounds and stop after it starts, but it is {span.StartSeconds} to {span.StopSeconds} s.");
        }

        Task = task;
        Repeat = repeat;
        Agent = agent;
        ModelId = modelId;
        Tier = tier;
        StartUtc = startUtc;
        EndUtc = endUtc;
        Outcome = outcome;
        Retries = retries;
        WaitMs = waitMs;
        LatencyMs = latencyMs;
        TtftMs = ttftMs;
        InputTokens = inputTokens;
        OutputTokens = outputTokens;
        GrossJoules = grossJoules;
        NetJoules = netJoules;
        AvgW = avgW;
        PeakW = peakW;
        Method = method;
        SampleCount = sampleCount;
        SampleRateHz = sampleRateHz;
        Cost = cost;
        Quality = quality ?? Metric.NotApplicable(NotScored);
        QualityReason = qualityReason ?? (quality is null ? NotScored : null);
        JoulesPerQualityPoint = joulesPerQualityPoint ?? Metric.NotApplicable(NotScored);
        CostPerQualityPoint = costPerQualityPoint ?? Metric.NotApplicable(NotScored);
        Flags = flags;
        OutputSha256 = outputSha256;
        OutputChars = outputChars;
        ThrottleReasons = reasons;
        OutputText = outputText;
        Window = window;
    }

    /// <summary>The task the workflow ran.</summary>
    public string Task { get; }

    /// <summary>The repeat number of the (task, tier) run, from 1.</summary>
    public int Repeat { get; }

    /// <summary>The agent name, for example <c>Planner</c>.</summary>
    public string Agent { get; }

    /// <summary>The model the call went to.</summary>
    public string ModelId { get; }

    /// <summary>The tier of the call; always the strict string form, whatever the options of the caller say.</summary>
    [JsonConverter(typeof(TierJsonConverter))]
    public Tier Tier { get; }

    /// <summary>When the call started, UTC.</summary>
    public DateTimeOffset StartUtc { get; }

    /// <summary>When the call ended, UTC.</summary>
    public DateTimeOffset EndUtc { get; }

    /// <summary>How the step ended: one of <see cref="StepOutcomes"/>.</summary>
    public string Outcome { get; }

    /// <summary>How many times the call was retried.</summary>
    public int Retries { get; }

    /// <summary>How long the retries waited, in milliseconds.</summary>
    public long WaitMs { get; }

    /// <summary>The call duration in milliseconds.</summary>
    public Metric LatencyMs { get; }

    /// <summary>The time to the first token in milliseconds; N/A (reason <c>buffered-call</c>) for a buffered call.</summary>
    public Metric TtftMs { get; }

    /// <summary>The prompt tokens the provider reported.</summary>
    public Metric InputTokens { get; }

    /// <summary>The completion tokens the provider reported.</summary>
    public Metric OutputTokens { get; }

    /// <summary>The gross GPU energy of the window; N/A with the reason <c>hosted-tier</c> on a hosted tier.</summary>
    public Metric GrossJoules { get; }

    /// <summary>The gross energy minus the idle baseline.</summary>
    public Metric NetJoules { get; }

    /// <summary>The average power over the window, in watts.</summary>
    public Metric AvgW { get; }

    /// <summary>The largest power sample in the window, in watts.</summary>
    public Metric PeakW { get; }

    /// <summary>The method that supplied the gross energy; null when none did. Always the strict string form.</summary>
    [JsonConverter(typeof(EnergyMethodJsonConverter))]
    public EnergyMethod? Method { get; }

    /// <summary>The number of power samples in the window; null when there were none to count.</summary>
    public int? SampleCount { get; }

    /// <summary>The sampling rate in hertz; null when there was no sampling.</summary>
    public double? SampleRateHz { get; }

    /// <summary>The cost of the call.</summary>
    public Metric Cost { get; }

    /// <summary>The judge score; N/A with the reason <c>not-scored</c> until the judge has run.</summary>
    public Metric Quality { get; }

    /// <summary>Why there is no score; <c>not-scored</c> until the judge has run.</summary>
    public string? QualityReason { get; }

    /// <summary>Net joules per quality point; N/A with the reason <c>not-scored</c> until the judge has run.</summary>
    public Metric JoulesPerQualityPoint { get; }

    /// <summary>Cost per quality point; N/A with the reason <c>not-scored</c> until the judge has run.</summary>
    public Metric CostPerQualityPoint { get; }

    /// <summary>The qualifiers of this reading, serialized as an array of vocabulary strings whatever the options of the caller say.</summary>
    [JsonConverter(typeof(ReadingFlagsJsonConverter))]
    public ReadingFlags Flags { get; }

    /// <summary>The lowercase hexadecimal SHA-256 of the output text; null when there is none.</summary>
    public string? OutputSha256 { get; }

    /// <summary>The length of the output text in characters; null when there is none.</summary>
    public int? OutputChars { get; }

    /// <summary>The throttle reasons seen in the window; null when none can be said.</summary>
    public IReadOnlyList<string>? ThrottleReasons { get; }

    /// <summary>The output text, kept in memory for the judge; never serialized and never printed.</summary>
    [JsonIgnore]
    public string? OutputText { get; }

    /// <summary>The meter's window of this step, in memory only, for the run assembler; never serialized and never printed.</summary>
    [JsonIgnore]
    public WindowSpan? Window { get; }

    // The record-generated printer would include OutputText, so a log line or an exception message that formats a reading would leak the model
    // output. This one prints every member except the text (its length is OutputChars) and the in-memory window, which is not part of the file.
    private bool PrintMembers(StringBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Append(CultureInfo.InvariantCulture, $"Task = {Task}, Repeat = {Repeat}, Agent = {Agent}, ModelId = {ModelId}, Tier = {Tier}, StartUtc = {StartUtc:O}, EndUtc = {EndUtc:O}, ");
        builder.Append(CultureInfo.InvariantCulture, $"Outcome = {Outcome}, Retries = {Retries}, WaitMs = {WaitMs}, LatencyMs = {LatencyMs}, TtftMs = {TtftMs}, InputTokens = {InputTokens}, OutputTokens = {OutputTokens}, ");
        builder.Append(CultureInfo.InvariantCulture, $"GrossJoules = {GrossJoules}, NetJoules = {NetJoules}, AvgW = {AvgW}, PeakW = {PeakW}, Method = {Method}, SampleCount = {SampleCount}, SampleRateHz = {SampleRateHz}, ");
        builder.Append(CultureInfo.InvariantCulture, $"Cost = {Cost}, Quality = {Quality}, QualityReason = {QualityReason}, JoulesPerQualityPoint = {JoulesPerQualityPoint}, CostPerQualityPoint = {CostPerQualityPoint}, ");
        builder.Append(CultureInfo.InvariantCulture, $"Flags = {Flags}, OutputSha256 = {OutputSha256}, OutputChars = {OutputChars}, ThrottleReasons = {(ThrottleReasons is null ? "null" : string.Join("|", ThrottleReasons))}");
        return true;
    }

    private static void RequireText(string parameter, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw Rejected(parameter, $"The {parameter} must not be null or blank.");
        }
    }

    private static void RequireUtc(string parameter, DateTimeOffset value)
    {
        if (value == default)
        {
            throw Rejected(parameter, $"The timestamp is not set.");
        }

        if (value.Offset != TimeSpan.Zero)
        {
            throw Rejected(parameter, $"The timestamp must be UTC, but its offset is {value.Offset}.");
        }
    }

    // The reason hosted-tier belongs to a tier that measures nothing; a measured tier that says so is lying about what happened.
    private static void RequireNotHostedReason(string parameter, Metric metric)
    {
        if (string.Equals(metric.Reason, MeterCapabilities.HostedTierReason, StringComparison.Ordinal))
        {
            throw Rejected(parameter, $"The reason {MeterCapabilities.HostedTierReason} belongs to the hosted tiers, but the tier is measured.");
        }
    }

    private static void RequireAbsent(string parameter, bool isAbsent)
    {
        if (!isAbsent)
        {
            throw Rejected(parameter, $"A hosted tier measures no energy, so {parameter} must be absent.");
        }
    }

    private static void ValidateOutputFingerprint(string? sha256, int? chars)
    {
        if (sha256 is not null && !IsLowerHex(sha256))
        {
            throw Rejected("outputSha256", $"The output hash must be {Sha256HexLength} lowercase hexadecimal digits.");
        }

        if (chars is < 0)
        {
            throw Rejected("outputChars", $"The output length must not be negative, but it is {chars}.");
        }
    }

    private static bool IsLowerHex(string text)
    {
        if (text.Length != Sha256HexLength)
        {
            return false;
        }

        foreach (char c in text)
        {
            if (!(c is >= '0' and <= '9' || c is >= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }
}