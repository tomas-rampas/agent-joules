using System.Diagnostics.CodeAnalysis;

namespace AgentJoules.Core.Gpu;

/// <summary>The outcome of opening a GPU: an open source, or an unavailable result with a reason.</summary>
public sealed record GpuOpenResult
{
    private GpuOpenResult(IGpuTelemetrySource? source, string? reason)
    {
        Source = source;
        Reason = reason;
    }

    /// <summary>True when <see cref="Source"/> is open.</summary>
    [MemberNotNullWhen(true, nameof(Source))]
    [MemberNotNullWhen(false, nameof(Reason))]
    public bool IsAvailable => Source is not null;

    /// <summary>The open source; null when unavailable. The caller disposes it.</summary>
    public IGpuTelemetrySource? Source { get; }

    /// <summary>Why the GPU is unavailable; null when available.</summary>
    public string? Reason { get; }

    /// <summary>An available result holding <paramref name="source"/>.</summary>
    public static GpuOpenResult Available(IGpuTelemetrySource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new GpuOpenResult(source, null);
    }

    /// <summary>An unavailable result with a non-empty <paramref name="reason"/>.</summary>
    public static GpuOpenResult Unavailable(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new GpuOpenResult(null, reason);
    }
}