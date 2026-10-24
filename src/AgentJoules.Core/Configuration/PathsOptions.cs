namespace AgentJoules.Core.Configuration;

/// <summary>
/// The <c>Paths</c> section of <c>AgentJoules</c> (design 3.8): where the files a run reads live, each relative to the content root (the working directory of the command). Every key
/// has a default, so the committed configuration needs no <c>Paths</c> entry. Only <see cref="Tasks"/>, <see cref="Prices"/>, <see cref="Rubric"/> and <see cref="Endpoints"/> are read today (<c>run</c> reads the first three, <c>validate --config</c> reads <see cref="Endpoints"/>); each is resolved under the content root, and a rooted value or a <c>..</c> segment is refused.
/// </summary>
/// <remarks>A mutable class with plain get and set accessors so that the configuration binding generator binds it.</remarks>
public sealed class PathsOptions
{
    /// <summary>The configuration key of <see cref="Tasks"/>, relative to the root section; messages name the key and never the value.</summary>
    public const string TasksKey = "Paths:Tasks";

    /// <summary>The configuration key of <see cref="Prices"/>, relative to the root section; messages name the key and never the value.</summary>
    public const string PricesKey = "Paths:Prices";

    /// <summary>The configuration key of <see cref="Rubric"/>, relative to the root section; messages name the key and never the value.</summary>
    public const string RubricKey = "Paths:Rubric";

    /// <summary>The configuration key of <see cref="Endpoints"/>, relative to the root section; messages name the key and never the value.</summary>
    public const string EndpointsKey = "Paths:Endpoints";

    /// <summary>Key <c>Paths:Tasks</c>, default <c>tasks</c>: the folder of the task files (one <c>*.json</c> per task).</summary>
    public string Tasks { get; set; } = "tasks";

    /// <summary>Key <c>Paths:Prices</c>, default <c>config/prices.json</c>: the price table of the cost model.</summary>
    public string Prices { get; set; } = "config/prices.json";

    /// <summary>Key <c>Paths:Rubric</c>, default <c>config/judge-rubric.md</c>: the rubric the judge scores with.</summary>
    public string Rubric { get; set; } = "config/judge-rubric.md";

    /// <summary>Key <c>Paths:Endpoints</c>, default <c>docs/endpoints.md</c>: the verified endpoints and model IDs of every tier.</summary>
    public string Endpoints { get; set; } = "docs/endpoints.md";
}