using Microsoft.Extensions.Options;

namespace AgentJoules.Core.Configuration;

/// <summary>
/// Checks the annotated ranges of the flat sections (<c>Gpu</c>, <c>Meter</c> and <c>Baseline</c>); the body is generated at build time
/// from the annotations. Rules that span two keys, and <c>Meter:Method</c>, are checked by <see cref="CrossFieldValidator"/>.
/// </summary>
[OptionsValidator]
public sealed partial class FlatSectionsValidator : IValidateOptions<AgentJoulesOptions>
{
}