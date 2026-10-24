using System.ComponentModel.DataAnnotations;

namespace AgentJoules.Core.Configuration;

/// <summary>
/// A finite numeric range for an option whose failure text is <see cref="ValidationAttribute.ErrorMessage"/> exactly, so that the message
/// can name the configuration key (<c>Gpu:Index</c>) instead of the property path the generated validator would print.
/// </summary>
/// <remarks>
/// The generated <c>[OptionsValidator]</c> code re-implements <see cref="RangeAttribute"/> and drops its <c>ErrorMessage</c>, printing
/// "The field AgentJoulesOptions.Gpu.Index must be between ..." instead; an attribute of this assembly is used as written. A value that
/// is NaN or infinite never passes. Only <see cref="int"/>, <see cref="long"/>, <see cref="float"/>, <see cref="double"/> and
/// <see cref="decimal"/> values are accepted; anything else, a string included, is a programming error and is never parsed.
/// </remarks>
/// <param name="minimum">The lower bound.</param>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
internal sealed class MinimumValueAttribute(double minimum) : ValidationAttribute
{
    /// <summary>The lower bound.</summary>
    public double Minimum { get; } = minimum;

    /// <summary>True when the value must be greater than <see cref="Minimum"/>; false when it may equal it.</summary>
    public bool IsExclusive { get; set; }

    /// <summary>The upper bound, inclusive; no upper bound when left at positive infinity.</summary>
    public double Maximum { get; set; } = double.PositiveInfinity;

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The value is not an int, long, float, double or decimal; the message names the member and the type, never the value.</exception>
    protected override ValidationResult? IsValid(object? value, ValidationContext? validationContext)
    {
        // A null value is not this attribute's business: the nullable numbers it is applied to are optional, and an unset one passes.
        if (value is null)
        {
            return ValidationResult.Success;
        }

        double number = value switch
        {
            int i => i,
            long l => l,
            float f => f,
            double d => d,
            decimal m => (double)m,
            _ => throw new InvalidOperationException(
                $"{nameof(MinimumValueAttribute)} on {validationContext?.MemberName ?? validationContext?.DisplayName ?? "a member"} cannot check a value of type {value.GetType().Name}."),
        };

        bool valid = double.IsFinite(number) && (IsExclusive ? number > Minimum : number >= Minimum) && number <= Maximum;

        // No member name on the result: the builder would prefix the line with it ("Index: ...") and the message already names the key.
        return valid ? ValidationResult.Success : new ValidationResult(ErrorMessage ?? FormatErrorMessage(validationContext?.DisplayName ?? string.Empty));
    }
}