using System.Text.Json;

using AgentJoules.Core.Security;
using AgentJoules.Dashboard;

using Microsoft.Extensions.Logging;

namespace AgentJoules.TestSupport;

/// <summary>
/// The one place a test builds a dashboard sink or snapshot (AJ-090): both take the redactor of the host as a required argument, so a test that does not care about it passes a fresh one, which
/// masks the <c>nvapi-</c> key pattern and nothing else registered.
/// </summary>
public static class DashboardFixtures
{
    /// <summary>A snapshot over <paramref name="redactor"/>, or over a fresh redactor.</summary>
    /// <param name="redactor">The redactor, or null for a fresh one.</param>
    /// <returns>The snapshot.</returns>
    public static DashboardSnapshot NewSnapshot(SecretRedactor? redactor = null) => new(redactor ?? new SecretRedactor());

    /// <summary>
    /// True when a payload holds a running energy estimate that is a number: an agent whose <c>runningNetJoules</c> is labelled <c>estimated</c> with a finite value. A null or a label without a value
    /// does not count, so the check fails when the integration of the power samples does nothing.
    /// </summary>
    /// <param name="payload">The payload, one line of JSON.</param>
    /// <returns>True when a number is estimated.</returns>
    public static bool HasNumericEstimate(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        using JsonDocument document = JsonDocument.Parse(payload);
        if (!document.RootElement.TryGetProperty("data", out JsonElement data) || !data.TryGetProperty("agents", out JsonElement agents) || agents.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (JsonElement agent in agents.EnumerateArray())
        {
            if (agent.TryGetProperty("runningNetJoules", out JsonElement running)
                && running.ValueKind == JsonValueKind.Object
                && running.TryGetProperty("label", out JsonElement label)
                && label.GetString() == "estimated"
                && running.TryGetProperty("value", out JsonElement value)
                && value.ValueKind == JsonValueKind.Number)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A sink over <paramref name="redactor"/>, or over a fresh redactor.</summary>
    /// <param name="logger">The logger of the first pump failure.</param>
    /// <param name="timeProvider">The clock of the emit instants.</param>
    /// <param name="redactor">The redactor, or null for a fresh one.</param>
    /// <returns>The sink.</returns>
    public static DashboardSink NewSink(ILogger logger, TimeProvider timeProvider, SecretRedactor? redactor = null) => new(logger, timeProvider, redactor ?? new SecretRedactor());
}