using System.Text.Json.Serialization;

using AgentJoules.Core.Labels;

namespace AgentJoules.Core.Results;

/// <summary>The <c>environment.assumptions</c> object: the factors that turn measured energy into money and carbon, each a label object so an unset factor is not-reported, never 0.</summary>
/// <param name="ElectricityPrice">The electricity price; not-reported when no price is configured.</param>
/// <param name="Gco2ePerKwh">The grid carbon intensity in grams of CO2e per kWh; not-reported when none is configured.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EnvironmentAssumptions(Metric ElectricityPrice, Metric Gco2ePerKwh);