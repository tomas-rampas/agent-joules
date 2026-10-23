using System.Reflection;

using AgentJoules.Core.Labels;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Reporting.Render;
using AgentJoules.TestSupport;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The state of one AJ-082 scenario (the aggregation of repeats into report rows), handed to the step class by Reqnroll context injection. The Given steps build run documents in memory with
/// <see cref="RunDocumentFixtures"/> (the committed fixture files are never read or changed); the When step runs the real <see cref="ReportAggregator"/>; the Then steps read the rows it returned.
/// There is no file, no HTTP, no model and no GPU.
/// </summary>
public sealed class AggregationContext
{
    private readonly List<SelectedRun> _runs = [];

    /// <summary>The model the When step built, or null before it.</summary>
    public ReportModel? Model { get; private set; }

    /// <summary>The names of the public members of the report model types that name a cross-tier figure; empty when the model is clean.</summary>
    public static IReadOnlyList<string> CrossTierMemberNames()
    {
        Type[] types = [typeof(SelectedRun), typeof(ReportCell), typeof(ReportRow), typeof(TierReport), typeof(ReportModel), typeof(ReportAggregator)];

        return
        [
            .. types
                .SelectMany(type => type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                .Select(member => member.Name)
                .Where(name => ReportRunFixtures.CrossTierWords.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase)))
                .Distinct(StringComparer.Ordinal),
        ];
    }

    /// <summary>Adds a local-tier file with one repeat per value, each a measured net energy of that many joules.</summary>
    /// <param name="netJoules">The net energy of each repeat.</param>
    public void AddNetJoulesRepeats(IReadOnlyList<double> netJoules) =>
        AddRun(Tier.Local, [.. netJoules.Select((value, index) => Step(Tier.Local, "model-a", index + 1, Metric.Measured(value, "J")))]);

    /// <summary>Adds a local-tier file with <paramref name="repeats"/> repeats of which the first carries <paramref name="flag"/>.</summary>
    /// <param name="repeats">The number of repeats.</param>
    /// <param name="flag">The reading flag of the first repeat.</param>
    public void AddRepeatsWithFlagOnFirst(int repeats, ReadingFlags flag) =>
        AddRun(Tier.Local, [.. Enumerable.Range(1, repeats).Select(repeat => Step(Tier.Local, "model-a", repeat, Metric.Measured(60.0, "J"), repeat == 1 ? flag : ReadingFlags.None))]);

    /// <summary>Adds a nvidia-tier file with <paramref name="repeats"/> repeats whose energy is N/A because the tier is hosted.</summary>
    /// <param name="repeats">The number of repeats.</param>
    public void AddHostedRepeats(int repeats) =>
        AddRun(Tier.Nvidia, [.. Enumerable.Range(1, repeats).Select(repeat => Step(Tier.Nvidia, "model-a", repeat, Metric.NotApplicable("hosted-tier")))]);

    /// <summary>Adds a file of <paramref name="tier"/> with one ok step of the model <paramref name="modelId"/>.</summary>
    /// <param name="tier">The tier of the file.</param>
    /// <param name="modelId">The model ID of the step.</param>
    public void AddSingleStepRun(Tier tier, string modelId) =>
        AddRun(tier, [Step(tier, modelId, 1, TierKinds.IsHosted(tier) ? Metric.NotApplicable("hosted-tier") : Metric.Measured(60.0, "J"))]);

    /// <summary>Runs the real aggregator over the files added so far.</summary>
    public void Aggregate() => Model = ReportAggregator.Build(_runs);

    private void AddRun(Tier tier, StepReading[] steps) => _runs.Add(ReportRunFixtures.RunOf(tier, steps));

    private static StepReading Step(Tier tier, string modelId, int repeat, Metric netJoules, ReadingFlags flags = ReadingFlags.None) =>
        ReportRunFixtures.Step(tier, repeat: repeat, modelId: modelId, net: netJoules, flags: flags);
}