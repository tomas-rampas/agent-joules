using AgentJoules.Core.Energy;

namespace AgentJoules.Metering;

/// <summary>
/// The process-wide services every <see cref="MeteredChatClient"/> shares (design 3.3): the one meter, the one gate, the sink the readings go
/// to and the clock. The orchestrator builds one record per tier run for the workflow agents and one for the judge, which differ in the sink,
/// so a judge reading never reaches the workflow results.
/// </summary>
/// <remarks>
/// Later stories add the members the design lists beside these four (the cost calculator, the instruments and the live event sink); they are
/// not here yet, so nothing in this record is a stub.
/// </remarks>
/// <param name="Meter">The meter that opens and closes the measurement window of a call; a hosted tier gets the unmetered one.</param>
/// <param name="Gate">The gate that decides whether windows may overlap; taken before the window opens and given back after it closes.</param>
/// <param name="Sink">The sink that receives the finished reading of every call.</param>
/// <param name="Time">The clock for the call start, the call end and the latency; a fake clock in tests.</param>
public sealed record MeteringServices(IEnergyMeter Meter, IMeterGate Gate, IMetricsSink Sink, TimeProvider Time);