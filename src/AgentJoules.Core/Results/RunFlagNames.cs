using System.Collections.ObjectModel;

namespace AgentJoules.Core.Results;

/// <summary>
/// The vocabulary of <c>run.flags</c> (REQ-015): the run-level qualifiers a results file may carry, as the exact strings of the file. <see cref="RunInfo"/> accepts no other string on
/// construction, on <c>with</c> and on read, so a typo cannot reach a file; the schema (AJ-079) takes its enumeration from <see cref="All"/>. The names are compared ordinally.
/// </summary>
public static class RunFlagNames
{
    /// <summary>The run-level partial-offload qualifier of the prompt skeleton.</summary>
    public const string PartialOffload = "partial-offload";

    /// <summary>The run is not fit to be published as a result; set, for example, when the idle baseline was shorter than 30 seconds (design section 7, <c>--baseline-seconds</c>).</summary>
    public const string NonPublishable = "non-publishable";

    /// <summary>Every known flag, in the order of the vocabulary: <see cref="PartialOffload"/>, <see cref="NonPublishable"/>.</summary>
    public static IReadOnlyList<string> All { get; } = new ReadOnlyCollection<string>([PartialOffload, NonPublishable]);

    /// <summary>Tells whether a string is a flag of the vocabulary; the comparison is ordinal, so the case matters.</summary>
    /// <param name="name">The string to look up, or null.</param>
    /// <returns>True when <paramref name="name"/> is one of <see cref="All"/>.</returns>
    public static bool IsKnown(string? name) =>
        name is not null && All.Contains(name, StringComparer.Ordinal);
}