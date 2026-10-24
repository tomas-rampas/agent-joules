using AgentJoules.Core.Cost;
using AgentJoules.Core.Energy;
using AgentJoules.Core.Live;

namespace AgentJoules.Metering;

/// <summary>
/// The process-wide services every <see cref="MeteredChatClient"/> shares (design 3.3): the one meter, the one gate, the sink the readings go
/// to, the cost calculator, the OpenTelemetry instruments and the clock. The orchestrator builds one record per tier run for the workflow agents and one for the judge, which
/// differ in the sink, so a judge reading never reaches the workflow results.
/// </summary>
/// <remarks>
/// The live event sink of the dashboard (design 3.10) is the body property <see cref="Live"/>, not a positional member, so every construction site that has no dashboard stays as it
/// is and gets <see cref="NullLiveEventSink"/>; the judge's services keep the null sink, so a judge call emits nothing.
/// </remarks>
/// <param name="Meter">The meter that opens and closes the measurement window of a call; a hosted tier gets the unmetered one.</param>
/// <param name="Gate">The gate that decides whether windows may overlap; taken before the window opens and given back after it closes.</param>
/// <param name="Sink">The sink that receives the finished reading of every call.</param>
/// <param name="Cost">The only source of the cost of a step (M2: <see cref="NotConfiguredCostCalculator"/>); the decorator holds no price of its own.</param>
/// <param name="Instruments">The OpenTelemetry instruments every finished reading is recorded on, right after the sink received it (REQ-014).</param>
/// <param name="Time">The clock for the call start, the call end and the latency; a fake clock in tests.</param>
public sealed record MeteringServices(IEnergyMeter Meter, IMeterGate Gate, IMetricsSink Sink, ICostCalculator Cost, AgentJoulesInstruments Instruments, TimeProvider Time)
{
    /// <summary>
    /// The live sink the streaming progress of a call goes to (<see cref="StepProgressEvent"/>, at most one per 100 ms); <see cref="NullLiveEventSink.Instance"/> unless the run has
    /// a dashboard. Every emit goes through <see cref="LiveEventSinkExtensions.EmitSafely"/>, so the sink can never change a reading.
    /// </summary>
    public ILiveEventSink Live { get; init; } = NullLiveEventSink.Instance;
}