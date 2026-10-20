using AgentJoules.Core.Cli;
using AgentJoules.Core.Energy;

using Microsoft.Extensions.Options;

namespace AgentJoules.Core.Configuration;

/// <summary>
/// Checks what annotations cannot: that <c>Meter:Method</c> is a known name, and the 10 Hz rule. A rate below 10 Hz is valid only
/// when <c>Meter:AllowBelowMinRate</c> is true.
/// </summary>
/// <remarks>
/// Every failure is reported, one line each, and the checks do not stop at the first one. A rate of 0 or less, NaN or infinity, which
/// no override allows, is left to the annotation on <see cref="MeterOptions.SampleRateHz"/> so that a caller that runs both validators
/// sees that failure once.
/// </remarks>
public sealed class CrossFieldValidator : IValidateOptions<AgentJoulesOptions>
{
    /// <summary>The lowest sample rate in hertz that is valid without <c>Meter:AllowBelowMinRate</c>.</summary>
    public const double MinimumRateHz = 10.0;

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, AgentJoulesOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<string> failures = [];
        MeterOptions meter = options.Meter;

        // Parse trims the value and matches it without regard to case; its message already names Meter:Method.
        try
        {
            MeterMethodSelector.Parse(meter.Method);
        }
        catch (ConfigurationException failure)
        {
            failures.Add(failure.Message);
        }

        // A rate of 0 or less, NaN and infinity belong to the annotation on Meter:SampleRateHz; only a positive rate is judged here.
        if (meter.SampleRateHz > 0.0 && meter.SampleRateHz < MinimumRateHz && !meter.AllowBelowMinRate)
        {
            failures.Add("Meter:SampleRateHz is below 10 Hz; set Meter:AllowBelowMinRate to true to allow it");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}