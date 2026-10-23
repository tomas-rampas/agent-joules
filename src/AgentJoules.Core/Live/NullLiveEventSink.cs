namespace AgentJoules.Core.Live;

/// <summary>The live sink of a run without <c>--dashboard</c>: it drops every event. The composition root registers it unless the dashboard replaces it.</summary>
public sealed class NullLiveEventSink : ILiveEventSink
{
    private NullLiveEventSink()
    {
    }

    /// <summary>The one instance.</summary>
    public static NullLiveEventSink Instance { get; } = new();

    /// <inheritdoc />
    public void Emit(LiveEvent liveEvent)
    {
    }
}