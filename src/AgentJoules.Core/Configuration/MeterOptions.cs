namespace AgentJoules.Core.Configuration;

/// <summary>The <c>Meter</c> section of <c>AgentJoules</c>: how energy is measured.</summary>
/// <remarks>
/// A mutable class with plain get and set accessors so that the configuration binding generator binds it. <c>Meter:Method</c> and the
/// 10 Hz rule are checked by <see cref="CrossFieldValidator"/>, not by annotations.
/// </remarks>
public sealed class MeterOptions
{
    /// <summary>Key <c>Meter:Method</c>, default <c>auto</c>: <c>auto</c>, <c>counter</c> or <c>sampling</c>, matched without regard to case.</summary>
    public string Method { get; set; } = "auto";

    /// <summary>
    /// The lowest rate in hertz the meter's timer accepts, <c>1000 / (uint.MaxValue - 1)</c>: the longest sampling period it can wait is
    /// <c>uint.MaxValue - 1</c> ms.
    /// </summary>
    internal const double LowestRateHz = 1000.0 / (uint.MaxValue - 1.0);

    /// <summary>
    /// Key <c>Meter:SampleRateHz</c>, default 10: the power sampling rate in hertz; finite, from <see cref="LowestRateHz"/>
    /// (2.3283064376228985E-07, a period of <c>uint.MaxValue - 1</c> ms) to 1000 (a period of 1 ms, the shortest the meter's timer
    /// accepts), and 10 or more unless <see cref="AllowBelowMinRate"/> is true.
    /// </summary>
    [MinimumValue(LowestRateHz, Maximum = 1000.0, ErrorMessage = "Meter:SampleRateHz must be a finite number from 2.3283064376228985E-07 to 1000")]
    public double SampleRateHz { get; set; } = 10.0;

    /// <summary>Key <c>Meter:AllowBelowMinRate</c>, default false: lets <see cref="SampleRateHz"/> be below 10 Hz.</summary>
    public bool AllowBelowMinRate { get; set; }
}