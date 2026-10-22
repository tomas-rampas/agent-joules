using System.Text.Json.Serialization;

namespace AgentJoules.Core.Runs;

/// <summary>
/// Who a step is and where it runs: the attribution of a call is a constructor argument of the metered client, never ambient state (design 3.3).
/// </summary>
/// <param name="Agent">The agent name, for example <c>Planner</c>; <c>Judge</c> for a judge call.</param>
/// <param name="ModelId">The model identifier the call goes to.</param>
/// <param name="Tier">The tier the call belongs to.</param>
/// <param name="Task">The name of the task the workflow runs.</param>
/// <param name="Repeat">The repeat number of the (task, tier) run, from 1.</param>
/// <param name="IsJudge">True for a judge call, whose readings go to the judge sink and never to the workflow sink.</param>
/// <param name="JudgedAgent">For a judge call, the name of the workflow agent whose step output is scored; copied into the reading so that it, with the task and the repeat, names exactly one step. Null for a workflow agent.</param>
public sealed record StepIdentity(string Agent, string ModelId, [property: JsonConverter(typeof(TierJsonConverter))] Tier Tier, string Task, int Repeat, bool IsJudge, string? JudgedAgent = null);