using System.Text.Json.Serialization;

namespace AgentJoules.Core.Results;

/// <summary>One entry of <c>environment.models</c>: which model an agent used and whether it is a baseline model (not a Nemotron model, REQ-017).</summary>
/// <param name="Agent">The agent name.</param>
/// <param name="ModelId">The model identifier the agent called.</param>
/// <param name="Baseline">True when the model ID does not contain <c>nemotron</c> (case-insensitive).</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EnvironmentModel(string Agent, string ModelId, bool Baseline);