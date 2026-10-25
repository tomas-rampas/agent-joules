namespace AgentJoules.Core.Configuration;

/// <summary>The <c>Baseline</c> section of <c>AgentJoules</c>: how the idle baseline is measured and judged.</summary>
/// <remarks>A mutable class with plain get and set accessors so that the configuration binding generator binds it.</remarks>
public sealed class BaselineOptions
{
    /// <summary>The shortest idle baseline that is fit to be published, in seconds, and the default of <c>Baseline:Seconds</c>: a baseline below it makes the run <c>non-publishable</c> and the command warns (REQ-021, AJ-106).</summary>
    public const double PublishableSeconds = 30.0;

    /// <summary>The smallest <c>Baseline:Seconds</c>: 1 ms.</summary>
    public const double MinimumSeconds = 0.001;

    /// <summary>The largest <c>Baseline:Seconds</c>: the longest wait of the meter's timer, <c>uint.MaxValue - 1</c> ms.</summary>
    public const double MaximumSeconds = 4294967.294;

    /// <summary>
    /// Key <c>Baseline:Seconds</c>, default 30: the length of the idle baseline window in seconds; finite, from 0.001 (1 ms) to
    /// 4294967.294 (the longest wait of the meter's timer, <c>uint.MaxValue - 1</c> ms).
    /// </summary>
    [MinimumValue(MinimumSeconds, Maximum = MaximumSeconds, ErrorMessage = "Baseline:Seconds must be a finite number from 0.001 to 4294967.294")]
    public double Seconds { get; set; } = PublishableSeconds;

    /// <summary>Key <c>Baseline:MaxCoefficientOfVariation</c>, default 0.10: the largest coefficient of variation a stable baseline may have; finite and greater than 0.</summary>
    [MinimumValue(0.0, IsExclusive = true, ErrorMessage = "Baseline:MaxCoefficientOfVariation must be a finite number greater than 0")]
    public double MaxCoefficientOfVariation { get; set; } = 0.10;
}