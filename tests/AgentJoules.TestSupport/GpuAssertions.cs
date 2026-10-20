using System.Globalization;

using Xunit;

namespace AgentJoules.TestSupport;

/// <summary>Assertions on the facts of the laptop GPU that hold for any RTX 3080 Laptop board; none of them pins an exact value.</summary>
public static class GpuAssertions
{
    private const double MinimumPowerLimitWatts = 50;
    private const double MaximumPowerLimitWatts = 200;

    /// <summary>Passes when the power limit is between 50 and 200 W inclusive; fails for anything else, NaN included.</summary>
    public static void PowerLimitInRange(double watts)
    {
        if (!(watts >= MinimumPowerLimitWatts && watts <= MaximumPowerLimitWatts))
        {
            Assert.Fail(string.Create(
                CultureInfo.InvariantCulture,
                $"The enforced power limit {watts} W is outside the range {MinimumPowerLimitWatts} to {MaximumPowerLimitWatts} W."));
        }
    }

    /// <summary>Passes when the device name contains 3080.</summary>
    public static void NameContains3080(string name)
    {
        Assert.Contains("3080", name, StringComparison.Ordinal);
    }
}