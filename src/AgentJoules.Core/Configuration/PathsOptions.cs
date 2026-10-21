namespace AgentJoules.Core.Configuration;

/// <summary>
/// The <c>Paths</c> section of <c>AgentJoules</c> (design 3.8): where the files a run reads live, each relative to the content root (the working directory of the command). Every key
/// has a default, so the committed configuration needs no <c>Paths</c> entry. Only <see cref="Tasks"/> is read today (by <c>run</c>, which resolves it under the content root and refuses a rooted value or a <c>..</c> segment); the
/// other keys are read by the stories that use the files.
/// </summary>
/// <remarks>A mutable class with plain get and set accessors so that the configuration binding generator binds it.</remarks>
public sealed class PathsOptions
{
    /// <summary>The configuration key of <see cref="Tasks"/>, relative to the root section; messages name the key and never the value.</summary>
    public const string TasksKey = "Paths:Tasks";

    /// <summary>Key <c>Paths:Tasks</c>, default <c>tasks</c>: the folder of the task files (one <c>*.json</c> per task).</summary>
    public string Tasks { get; set; } = "tasks";

    /// <summary>Key <c>Paths:Prices</c>, default <c>config/prices.json</c>: the price table of the cost model.</summary>
    public string Prices { get; set; } = "config/prices.json";

    /// <summary>Key <c>Paths:Rubric</c>, default <c>config/judge-rubric.md</c>: the rubric the judge scores with.</summary>
    public string Rubric { get; set; } = "config/judge-rubric.md";

    /// <summary>Key <c>Paths:Endpoints</c>, default <c>docs/endpoints.md</c>: the verified endpoints and model IDs of every tier.</summary>
    public string Endpoints { get; set; } = "docs/endpoints.md";
}