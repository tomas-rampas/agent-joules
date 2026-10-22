using System.ComponentModel.DataAnnotations;

namespace AgentJoules.Core.Configuration;

/// <summary>
/// A currency code for an option whose failure text is <see cref="ValidationAttribute.ErrorMessage"/> exactly, so that the message names the configuration key (<c>Cost:Currency</c>) and nothing else: the
/// generated <c>[OptionsValidator]</c> code prefixes the member name to the text of the stock <c>[Required]</c> and <c>[RegularExpression]</c> attributes and quotes nothing, while an attribute of this assembly is
/// used as written. The rule is <see cref="CostOptions.IsCurrencyCode"/>, in one place; null, empty, white space and a value that is not a string never pass, and no message holds the value.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
internal sealed class CurrencyCodeAttribute : ValidationAttribute
{
    /// <inheritdoc />
    protected override ValidationResult? IsValid(object? value, ValidationContext? validationContext) =>
        value is string text && CostOptions.IsCurrencyCode(text)
            ? ValidationResult.Success
            : new ValidationResult(ErrorMessage ?? "The value must be three upper-case letters");
}