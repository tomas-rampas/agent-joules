namespace AgentJoules.Metering;

/// <summary>A gate that can be stopped (AJ-123, EDGE-016): once stopped it refuses every acquisition, so the calls that wait for it or ask for it later send no request. The cloudgpu tier's gate is the one implementation.</summary>
public interface IStoppableMeterGate
{
    /// <summary>Refuses every acquisition from now on; calling it again does nothing.</summary>
    void Halt();
}