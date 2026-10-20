namespace AgentJoules.Core.Energy;

/// <summary>
/// Everything <see cref="ReadingCalculator.Compute"/> needs to turn one window into an <see cref="EnergyReading"/>.
/// </summary>
/// <param name="Samples">
/// The power samples of the window in strictly increasing timestamp order, both edges included. The list must not change
/// during the call to <see cref="ReadingCalculator.Compute"/>: an array or a <see cref="List{T}"/> is read in place, so hold the
/// owner's lock or pass a snapshot.
/// </param>
/// <param name="WindowStartSeconds">The window start in seconds since meter start.</param>
/// <param name="WindowStopSeconds">The window stop in seconds since meter start; later than the start.</param>
/// <param name="Method">The method that supplies the gross energy.</param>
/// <param name="CounterDeltaMillijoules">The counter difference over the window; must be <c>null</c> with the sampling method, and <c>null</c> with the counter method means the counter was not read.</param>
/// <param name="BaselineMeanWatts">The idle baseline power in watts (finite, not negative), or <c>null</c> when none was measured.</param>
/// <param name="SampleRateHz">The configured sampling rate in hertz; positive and finite.</param>
/// <param name="IsSamplingFallback">True when automatic selection fell back to sampling because the counter is unsupported; only valid with the sampling method.</param>
public readonly record struct ReadingInput(
    IReadOnlyList<PowerSample> Samples,
    double WindowStartSeconds,
    double WindowStopSeconds,
    EnergyMethod Method,
    long? CounterDeltaMillijoules,
    double? BaselineMeanWatts,
    double SampleRateHz,
    bool IsSamplingFallback);