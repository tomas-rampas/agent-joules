using System.Text.Json.Serialization;

using AgentJoules.Core.Labels;

namespace AgentJoules.Core.Results;

/// <summary>The <c>environment.assumptions</c> object: the factors that turn measured energy into money and carbon, each a label object so an unset factor is N/A, never 0.</summary>
/// <param name="ElectricityPrice">The electricity price per kWh, <c>estimated</c> in the configured currency (it is an assumption, not a measurement); the price always exists because <c>Cost:ElectricityPricePerKwh</c> has a default.</param>
/// <param name="Gco2ePerKwh">The grid carbon intensity in grams of CO2e per kWh, <c>estimated</c> with the unit <c>gCO2e/kWh</c> when <c>Cost:GCo2ePerKwh</c> is set, N/A <c>not-configured</c> otherwise.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EnvironmentAssumptions(Metric ElectricityPrice, Metric Gco2ePerKwh);