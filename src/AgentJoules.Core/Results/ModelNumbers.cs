namespace AgentJoules.Core.Results;

/// <summary>
/// The number rules of the results model that a constructor, a <c>with</c> expression and a read all enforce (AJ-080): a double that exists is finite, because a number the document
/// holds as <c>1e400</c> is read as infinity and cannot be written back as JSON, and a range is stated where the file has one. A failure names the member of the file
/// (<c>environment.temperature</c>) and the rule, never the value.
/// </summary>
internal static class ModelNumbers
{
    /// <summary>Returns <paramref name="value"/> when it is null or finite.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is infinite or not a number.</exception>
    internal static double? FiniteOrNull(string member, double? value) =>
        value is { } number && !double.IsFinite(number)
            ? throw new ArgumentOutOfRangeException(member, $"{member} must be a finite number.")
            : value;

    /// <summary>Returns <paramref name="value"/> when it is null or a finite number from <paramref name="minimum"/> to <paramref name="maximum"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is infinite, not a number or outside the range.</exception>
    internal static double? InRangeOrNull(string member, double? value, double minimum, double maximum) =>
        value is { } number && !(double.IsFinite(number) && number >= minimum && number <= maximum)
            ? throw new ArgumentOutOfRangeException(member, $"{member} must be a finite number from {minimum} to {maximum}.")
            : value;

    /// <summary>Returns <paramref name="value"/> when it is at least <paramref name="minimum"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is below the minimum.</exception>
    internal static int AtLeast(string member, int value, int minimum) =>
        value < minimum
            ? throw new ArgumentOutOfRangeException(member, $"{member} must be at least {minimum}.")
            : value;
}