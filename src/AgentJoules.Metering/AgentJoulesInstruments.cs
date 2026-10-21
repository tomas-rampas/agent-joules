using System.Diagnostics;
using System.Diagnostics.Metrics;

using AgentJoules.Core.Configuration;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Results;

namespace AgentJoules.Metering;

/// <summary>
/// The OpenTelemetry instruments of the meter <c>AgentJoules</c> (REQ-014, design 3.3): one histogram each for the energy, the power and the latency of a step, a counter of tokens and a counter
/// of cost. <see cref="Record"/> is the single entry point; the metered client calls it with every finished <see cref="StepReading"/>, right after the sink received it.
/// </summary>
/// <remarks>
/// <para>
/// Every measurement carries three attributes, <c>gen_ai.agent.name</c>, <c>aj.tier</c> (the lowercase tier name) and <c>gen_ai.request.model</c>, and nothing else but the one dimension of its
/// instrument: <c>aj.energy.kind</c> (<c>gross</c> or <c>net</c>) on the energy, <c>aj.token.type</c> (<c>input</c> or <c>output</c>) on the tokens and <c>aj.currency</c> on the cost. No prompt, no
/// answer and no task name is an attribute. The task is deliberately not one either: the telemetry answers "which agent on which model and tier", the results file answers the rest.
/// </para>
/// <para>
/// A metric whose value does not exist (N/A or not-reported: the energy of a hosted tier, the tokens a provider did not report, a cost nobody priced) is not recorded, so no zero stands in for
/// it. A measured zero is a value and is recorded. No negative value is ever recorded: a net energy below the idle baseline is negative by design (the reading keeps it, flagged
/// below-baseline), but a histogram of joules has no meaning for it, so that one measurement is skipped while the gross energy of the same step is recorded.
/// </para>
/// <para>
/// The meter comes from an <see cref="IMeterFactory"/>, the one of the service container in a run (it resolves from a host built without defaults, without an explicit <c>AddMetrics()</c>) and
/// <c>AddMetrics()</c> in a test, so a test that creates its own factory sees only its own measurements. The factory owns the meter and disposes it; this class holds no unmanaged resource.
/// Instruments are thread-safe, so concurrent steps record without a lock.
/// </para>
/// </remarks>
public sealed class AgentJoulesInstruments
{
    /// <summary>The name of the meter; register it with the meter provider (<c>AddMeter</c>) to export the instruments.</summary>
    public const string MeterName = "AgentJoules";

    /// <summary>The prefix of the name of every instrument of this program; an instrument on the meter <see cref="MeterName"/> without it (the ones of the agent middleware) is not part of the contract.</summary>
    public const string MeterPrefix = "agentjoules.";

    /// <summary>The prefix of the name of each of the five step instruments.</summary>
    public const string StepPrefix = MeterPrefix + "step.";

    /// <summary>The energy of a step in joules, a histogram; the attribute <c>aj.energy.kind</c> is <c>gross</c> or <c>net</c>.</summary>
    public const string EnergyName = StepPrefix + "energy";

    /// <summary>The average power of a step in watts, a histogram.</summary>
    public const string PowerName = StepPrefix + "power";

    /// <summary>The tokens of a step, a counter; the attribute <c>aj.token.type</c> is <c>input</c> or <c>output</c>.</summary>
    public const string TokensName = StepPrefix + "tokens";

    /// <summary>The latency of a step in milliseconds, a histogram.</summary>
    public const string LatencyName = StepPrefix + "latency";

    /// <summary>The cost of a step, a counter; the attribute <c>aj.currency</c> is the currency code of the amount.</summary>
    public const string CostName = StepPrefix + "cost";

    private const string AgentTag = "gen_ai.agent.name";
    private const string TierTag = "aj.tier";
    private const string ModelTag = "gen_ai.request.model";
    private const string EnergyKindTag = "aj.energy.kind";
    private const string TokenTypeTag = "aj.token.type";
    private const string CurrencyTag = "aj.currency";

    private readonly Histogram<double> _energy;
    private readonly Histogram<double> _power;
    private readonly Counter<long> _tokens;
    private readonly Histogram<double> _latency;
    private readonly Counter<double> _cost;

    /// <summary>Creates the meter <see cref="MeterName"/> from <paramref name="factory"/> and the five instruments on it.</summary>
    /// <param name="factory">The factory that owns the meter: the one of the service container, or the one a test builds with <c>AddMetrics()</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is null.</exception>
    public AgentJoulesInstruments(IMeterFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        Meter meter = factory.Create(new System.Diagnostics.Metrics.MeterOptions(MeterName));
        _energy = meter.CreateHistogram<double>(EnergyName, "J", "The gross and the net GPU energy of one step.");
        _power = meter.CreateHistogram<double>(PowerName, "W", "The average GPU power over one step.");
        _tokens = meter.CreateCounter<long>(TokensName, "{token}", "The input and output tokens the provider reported for one step.");
        _latency = meter.CreateHistogram<double>(LatencyName, "ms", "The duration of one step, the rate-limit waits left out.");
        _cost = meter.CreateCounter<double>(CostName, null, "The cost of one step in the currency of its price.");
    }

    /// <summary>
    /// Records the measurements of <paramref name="reading"/> that exist: gross and net energy, average power, input and output tokens, latency and cost. A value that is N/A or not-reported is
    /// skipped, and so is a negative one (see the remarks of the type).
    /// </summary>
    /// <param name="reading">The finished reading of one step.</param>
    /// <exception cref="ArgumentNullException"><paramref name="reading"/> is null.</exception>
    public void Record(StepReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);

        var identity = new TagList
        {
            { AgentTag, reading.Agent },
            { TierTag, TierOptions.NameOf(reading.Tier) },
            { ModelTag, reading.ModelId },
        };

        if (Existing(reading.GrossJoules) is { } gross)
        {
            TagList tags = identity;
            tags.Add(EnergyKindTag, "gross");
            _energy.Record(gross, tags);
        }

        if (Existing(reading.NetJoules) is { } net)
        {
            TagList tags = identity;
            tags.Add(EnergyKindTag, "net");
            _energy.Record(net, tags);
        }

        if (Existing(reading.AvgW) is { } power)
        {
            _power.Record(power, identity);
        }

        if (Existing(reading.InputTokens) is { } input)
        {
            TagList tags = identity;
            tags.Add(TokenTypeTag, "input");
            _tokens.Add((long)input, tags);
        }

        if (Existing(reading.OutputTokens) is { } output)
        {
            TagList tags = identity;
            tags.Add(TokenTypeTag, "output");
            _tokens.Add((long)output, tags);
        }

        if (Existing(reading.LatencyMs) is { } latency)
        {
            _latency.Record(latency, identity);
        }

        if (reading.Cost.Currency is { } currency && Existing(reading.Cost) is { } cost)
        {
            TagList tags = identity;
            tags.Add(CurrencyTag, currency);
            _cost.Add(cost, tags);
        }
    }

    // The value of a metric that exists and can be recorded: null for N/A and not-reported, and for a negative value (a measured zero is a value).
    private static double? Existing(Metric metric) => metric.Value is >= 0.0 and { } value ? value : null;
}