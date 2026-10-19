using System.Globalization;
using System.Text.Json;

using AgentJoules.Core.Gpu;
using AgentJoules.Reporting.Json;

namespace AgentJoules.Cli.Output;

/// <summary>
/// Formats a <see cref="ProbeResult"/> as the text of <c>probe</c> or as its JSON object. The design left the label text open, so the labels and
/// their order are fixed here: <c>GPU name</c>, <c>GPU UUID</c>, <c>Driver version</c>, <c>Compute capability</c>, <c>VBIOS version</c>,
/// <c>Power limit W</c>, <c>MIG mode</c>, <c>Energy counter supported</c>, <c>Method</c>, <c>Sample rate Hz</c>. Numbers use the invariant culture.
/// </summary>
internal static class ProbeFormatter
{
    private const string NotAvailable = "N/A";
    private const string NoGpuLine = "no GPU";

    /// <summary>The lines of the text form: ten <c>label: value</c> lines, or the single line <c>no GPU</c>.</summary>
    internal static IReadOnlyList<string> Lines(ProbeResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.GpuPresent ? Facts(result) : [NoGpuLine];
    }

    private static string[] Facts(ProbeResult result) =>
    [
        "GPU name: " + Value(result.Name),
        "GPU UUID: " + Value(result.GpuUuid),
        "Driver version: " + Value(result.DriverVersion),
        "Compute capability: " + Value(result.ComputeCapability),
        "VBIOS version: " + Value(result.VbiosVersion),
        "Power limit W: " + (result.PowerLimitW is { } limit ? limit.ToString("0.###", CultureInfo.InvariantCulture) : NotAvailable),
        "MIG mode: " + Value(result.MigMode),
        "Energy counter supported: " + (result.EnergyCounterSupported is { } supported ? (supported ? "true" : "false") : NotAvailable),
        "Method: " + Method(result),
        "Sample rate Hz: " + (result.SampleRateHz is { } rate ? rate.ToString(CultureInfo.InvariantCulture) : NotAvailable),
    ];

    /// <summary>The JSON object through <see cref="AgentJoulesJsonContext"/>: camelCase, indented, LF, nulls written.</summary>
    internal static string Json(ProbeResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return JsonSerializer.Serialize(result, AgentJoulesJsonContext.Default.ProbeResult);
    }

    // A driver string is data, not text of ours: line terminators are folded into a space and every other control character becomes ?, so a value can
    // neither forge another label line nor reach the terminal as an escape sequence. The JSON keeps the exact value (System.Text.Json escapes it).
    private static string Value(string? value)
    {
        string text = ErrorLine.Scrub(ErrorLine.Fold(value));
        return text.Length == 0 ? NotAvailable : text;
    }

    // A null method on a present GPU means Meter:Method=counter on a GPU without the counter; probe reports it, run fails with exit 6 (M2).
    private static string Method(ProbeResult result) => result.Method switch
    {
        null => result.EnergyCounterSupported == false ? "N/A (counter forced but unsupported)" : NotAvailable,
        "sampling" when result.IsFallback => "sampling (sampling-fallback)",
        var method => method,
    };
}