using Microsoft.Extensions.Options;

namespace AgentJoules.Core.Configuration;

/// <summary>
/// Checks the annotated ranges of the flat sections (<c>Gpu</c>, <c>Meter</c>, <c>Baseline</c>, <c>Cost</c> and <c>CloudGpu</c>); the body is generated at build time
/// from the annotations. Rules that span two keys, <c>Meter:Method</c>, and every rule of <c>Tiers</c> (a dictionary, which the generator does not
/// enumerate) and <c>Judge</c> are checked by <see cref="CrossFieldValidator"/>; <c>Workflow</c> has no annotated member and no rule.
/// </summary>
[OptionsValidator]
public sealed partial class FlatSectionsValidator : IValidateOptions<AgentJoulesOptions>
{
}