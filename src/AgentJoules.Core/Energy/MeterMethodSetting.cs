namespace AgentJoules.Core.Energy;

/// <summary>The parsed value of <c>Meter:Method</c>.</summary>
public enum MeterMethodSetting
{
    // Numbered from 1 so that default(MeterMethodSetting) is never a valid setting.

    /// <summary>Use the energy counter when the GPU supports it, otherwise fall back to sampling.</summary>
    Auto = 1,

    /// <summary>Use the energy counter; it is a precondition failure when the GPU does not support it.</summary>
    Counter = 2,

    /// <summary>Always sample power, even when the counter is supported; this is not a fallback.</summary>
    Sampling = 3,
}