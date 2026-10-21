using System.Diagnostics.CodeAnalysis;

using AgentJoules.Core.Cli;
using AgentJoules.Core.Labels;

namespace AgentJoules.Workflow.Agents;

/// <summary>One agent of the workflow: its fixed name and its fixed instruction.</summary>
/// <param name="Name">The agent name, the <c>Agent</c> of every reading it produces.</param>
/// <param name="Instructions">The instruction text of design 3.4, without any tier prefix.</param>
public sealed record AgentDefinition(string Name, string Instructions);

/// <summary>
/// The four agents of the workflow and their instructions (REQ-011, design 3.4): Planner, Researcher, Writer and Reviewer, in this fixed order, which is
/// also the order of the steps of a run. The catalog is the one place that decides which agent names exist: the agent factory refuses any other name, so a
/// reading can only ever be attributed to one of these four (the judge is not an agent of the catalog; it has its own client and identity).
/// </summary>
/// <remarks>
/// The instruction of an agent is system text only. The user message of a step (the task, the plan or the research notes the step needs) is assembled by
/// the step executors of the workflow, not here. A tier may put text in front of every instruction with <c>Tiers:&lt;tier&gt;:SystemPromptPrefix</c>
/// (<see cref="InstructionsFor"/>), for a model that needs a mode switch in its system prompt.
/// </remarks>
public static class AgentCatalog
{
    /// <summary>The name of the agent that breaks the task into steps.</summary>
    public const string Planner = "Planner";

    /// <summary>The name of the agent that collects the facts, constraints and risks.</summary>
    public const string Researcher = "Researcher";

    /// <summary>The name of the agent that writes the draft answer.</summary>
    public const string Writer = "Writer";

    /// <summary>The name of the agent that returns the corrected final answer.</summary>
    public const string Reviewer = "Reviewer";

    /// <summary>What separates a tier prefix from the instruction: one blank line, with LF line ends on every platform so that the text is the same on every host.</summary>
    public const string PrefixSeparator = "\n\n";

    // Declared before the lists that are built from it: static initializers run in textual order.
    private static readonly AgentDefinition[] Definitions =
    [
        new(Planner, "Break the task into 3 to 5 numbered steps; no prose."),
        new(Researcher, "Given the task and plan, list the facts, constraints and risks the answer must respect."),
        new(Writer, "Given the task and plan, write the complete answer."),
        new(Reviewer, "Given the task, the research notes and the draft, return the corrected final answer only."),
    ];

    /// <summary>The four agents in the fixed order of a run; the list cannot be changed.</summary>
    public static IReadOnlyList<AgentDefinition> All { get; } = Array.AsReadOnly(Definitions);

    /// <summary>The four agent names in the fixed order of a run; the list cannot be changed.</summary>
    public static IReadOnlyList<string> Names { get; } = Array.AsReadOnly(Definitions.Select(static definition => definition.Name).ToArray());

    /// <summary>Finds the agent called <paramref name="name"/>; the match is ordinal, so <c>planner</c> is not <c>Planner</c>.</summary>
    /// <param name="name">The agent name.</param>
    /// <returns>The definition.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    /// <exception cref="ConfigurationException">The catalog has no agent of that name (exit code 2). The message echoes the name cut to 64 characters with control characters replaced, and lists the four valid names.</exception>
    public static AgentDefinition Get(string name) =>
        TryGet(name, out AgentDefinition? definition)
            ? definition
            : throw new ConfigurationException($"The agent name \"{TextEcho.ForMessage(name)}\" is not in the catalog; the agents are {string.Join(", ", Names.Take(Names.Count - 1))} and {Names[^1]}");

    /// <summary>Finds the agent called <paramref name="name"/>; the match is ordinal.</summary>
    /// <param name="name">The agent name.</param>
    /// <param name="definition">The definition when the catalog has the agent, otherwise null.</param>
    /// <returns>True when the catalog has an agent of that name.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    public static bool TryGet(string name, [NotNullWhen(true)] out AgentDefinition? definition)
    {
        ArgumentNullException.ThrowIfNull(name);

        definition = Definitions.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal));
        return definition is not null;
    }

    /// <summary>The instruction an agent runs with: the instruction of the catalog, preceded by the tier prefix when the tier has one.</summary>
    /// <param name="name">The agent name.</param>
    /// <param name="systemPromptPrefix"><c>Tiers:&lt;tier&gt;:SystemPromptPrefix</c>, or null. A prefix that is empty or white space is ignored; otherwise its trailing white space is removed and <see cref="PrefixSeparator"/> (a blank line) is put between it and the instruction.</param>
    /// <returns>The instruction text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    /// <exception cref="ConfigurationException">The catalog has no agent of that name.</exception>
    public static string InstructionsFor(string name, string? systemPromptPrefix)
    {
        AgentDefinition definition = Get(name);

        return string.IsNullOrWhiteSpace(systemPromptPrefix)
            ? definition.Instructions
            : systemPromptPrefix.TrimEnd() + PrefixSeparator + definition.Instructions;
    }
}