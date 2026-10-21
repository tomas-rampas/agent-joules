using AgentJoules.Core.Cli;
using AgentJoules.Core.Configuration;
using AgentJoules.Metering;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace AgentJoules.Cli.Hosting;

/// <summary>
/// Registers the OpenTelemetry providers of a run (REQ-014, design 3.7, ADR-08). Metrics: the meter <c>AgentJoules</c> of <see cref="AgentJoulesInstruments"/> (only its five
/// <c>agentjoules.step.*</c> instruments; the instruments of the agent middleware on the same meter are dropped), the console exporter when the command asks for it, and the OTLP exporter
/// when an endpoint is configured. Traces: the activity sources <c>AgentJoules</c> (the agent spans of <c>UseOpenTelemetry</c>) and <c>Microsoft.Agents.AI.Workflows*</c> (the workflow,
/// executor, edge and message spans that <c>AgentJoulesWorkflowFactory</c> enables), exported to OTLP only, never to the console (agent spans are too verbose for the terminal).
/// </summary>
/// <remarks>
/// <para>
/// The console exporter is on by default for <c>run</c> and <c>calibrate</c> and off for every other command, so <c>probe --json</c> stays machine-readable: the caller of <see cref="Add"/>
/// decides and passes the flag (<c>!parseResult.GetValue(</c><see cref="TelemetryOptions.CreateNoConsoleTelemetry"/><c>)</c> for the two commands that print metrics, <c>false</c> for the others;
/// <c>run --dry-run</c> records no metric and passes <c>false</c>, so its standard output stays the tier lines).
/// The console exporter writes to the console, not to the output writer of the command host.
/// </para>
/// <para>
/// The OTLP decision reads <see cref="EndpointKey"/> from the <see cref="IConfiguration"/> it is given and never from the environment, so the isolated configuration of a test controls it; in a
/// run the host configuration carries the environment variable of that name. A value that is empty or blank adds no exporter. The metrics exporter is added when the general key or
/// <see cref="MetricsEndpointKey"/> is set, the traces exporter when the general key or <see cref="TracesEndpointKey"/> is set (the exporter honours a signal-specific endpoint over the general one).
/// </para>
/// <para>
/// Any other value must be an absolute http or https URL without user info, query or fragment (the shape rule of <see cref="EndpointRules"/>), or <see cref="Add"/> throws a
/// <see cref="ConfigurationException"/> (exit code 2) that names the variable and never shows the value, which can carry credentials. Plain http to a host that is not loopback, the usual collector in
/// a compose network, is allowed while no header travels with it; when the headers that apply to the endpoint are set (<see cref="HeadersKey"/> for every endpoint, <see cref="MetricsHeadersKey"/> for the
/// metrics endpoint, <see cref="TracesHeadersKey"/> for the traces endpoint; for the general endpoint any of the three) the endpoint must be https unless the host is loopback, because headers can carry a
/// token. The message then names both variables and no value.
/// </para>
/// <para>
/// The exporters are added without a configure callback and the endpoint is not set in code, so the SDK binds every OTLP option (endpoint, protocol, headers, timeout) from one source, the
/// registered <see cref="IConfiguration"/>, and keeps its own rule that appends the signal path to the endpoint for the <c>http/protobuf</c> protocol. <see cref="Add"/> registers the
/// configuration it was given when the container has none, so that in a bare container the SDK never falls back to the process environment for some options and to the given configuration for
/// another; in the host, which already registers its configuration, the call changes nothing.
/// </para>
/// <para>
/// The hosting integration of OpenTelemetry starts the providers with the host and disposes them with the service provider, which flushes the metrics and the spans still buffered; a command that
/// ends the run must therefore dispose the host (the command host does) rather than abandon it. <c>EnableSensitiveData</c> of the agent spans and of the workflow spans stays off (see <c>AgentFactory</c>
/// and <c>AgentJoulesWorkflowFactory</c>).
/// </para>
/// </remarks>
public static class TelemetrySetup
{
    /// <summary>The configuration key (the environment variable of the OpenTelemetry specification) that holds the OTLP endpoint.</summary>
    public const string EndpointKey = "OTEL_EXPORTER_OTLP_ENDPOINT";

    /// <summary>The key of the endpoint that the exporter uses for the metrics instead of <see cref="EndpointKey"/> when it is set.</summary>
    public const string MetricsEndpointKey = "OTEL_EXPORTER_OTLP_METRICS_ENDPOINT";

    /// <summary>The key of the endpoint that the exporter uses for the traces instead of <see cref="EndpointKey"/> when it is set.</summary>
    public const string TracesEndpointKey = "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT";

    /// <summary>The key of the headers (<c>name=value</c> pairs, possibly a token) that the exporter sends with every request, to every endpoint.</summary>
    public const string HeadersKey = "OTEL_EXPORTER_OTLP_HEADERS";

    /// <summary>The key of the headers that the exporter sends with the metrics instead of <see cref="HeadersKey"/> when it is set.</summary>
    public const string MetricsHeadersKey = "OTEL_EXPORTER_OTLP_METRICS_HEADERS";

    /// <summary>The key of the headers that the exporter sends with the traces instead of <see cref="HeadersKey"/> when it is set.</summary>
    public const string TracesHeadersKey = "OTEL_EXPORTER_OTLP_TRACES_HEADERS";

    /// <summary>The activity source of the workflow spans of the agent framework; the trailing wildcard matches every source of the framework's workflow namespace.</summary>
    private const string WorkflowSources = "Microsoft.Agents.AI.Workflows*";

    /// <summary>Adds the OpenTelemetry providers to <paramref name="services"/>.</summary>
    /// <param name="services">The collection of the host being built.</param>
    /// <param name="configuration">
    /// The configuration the OTLP settings are read from, key <see cref="EndpointKey"/> and the other <c>OTEL_EXPORTER_OTLP_*</c> keys; registered as the configuration of the container when it has none.
    /// Pass the host's configuration: a configuration registered later would be the one the SDK binds from, bypassing this validation.
    /// </param>
    /// <param name="consoleEnabled">True to print the metrics to the console.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="configuration"/> is null.</exception>
    /// <exception cref="ConfigurationException">An OTLP endpoint variable is set to a value that is not an acceptable endpoint (exit code 2); nothing was added.</exception>
    public static IServiceCollection Add(IServiceCollection services, IConfiguration configuration, bool consoleEnabled)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Every key is checked before anything is registered, so a refusal leaves the collection as it was.
        Validate(configuration, EndpointKey, HeadersKey, MetricsHeadersKey, TracesHeadersKey);
        Validate(configuration, MetricsEndpointKey, HeadersKey, MetricsHeadersKey);
        Validate(configuration, TracesEndpointKey, HeadersKey, TracesHeadersKey);
        bool general = IsSet(configuration, EndpointKey);
        bool metricsOtlp = general || IsSet(configuration, MetricsEndpointKey);
        bool tracesOtlp = general || IsSet(configuration, TracesEndpointKey);

        services.TryAddSingleton(configuration);
        _ = services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                _ = metrics.AddMeter(AgentJoulesInstruments.MeterName);

                // The agent wrapper (UseOpenTelemetry) creates a meter of the same name for the gen_ai.client.* instruments of the agent framework. Only the instruments of this program (the
                // five agentjoules.step.* ones today) are the contract of the meter, so every other instrument of it is dropped before it reaches an exporter.
                _ = metrics.AddView(static instrument =>
                    instrument.Meter.Name == AgentJoulesInstruments.MeterName && !instrument.Name.StartsWith(AgentJoulesInstruments.MeterPrefix, StringComparison.Ordinal)
                        ? MetricStreamConfiguration.Drop
                        : null);
                if (consoleEnabled)
                {
                    _ = metrics.AddConsoleExporter();
                }

                if (metricsOtlp)
                {
                    _ = metrics.AddOtlpExporter();
                }
            })
            .WithTracing(tracing =>
            {
                _ = tracing.AddSource(AgentJoulesInstruments.MeterName).AddSource(WorkflowSources);
                if (tracesOtlp)
                {
                    _ = tracing.AddOtlpExporter();
                }
            });

        return services;
    }

    // True when the key holds something other than white space.
    private static bool IsSet(IConfiguration configuration, string key) => !string.IsNullOrWhiteSpace(configuration[key]);

    // Refuses a set endpoint that has the wrong shape, and a plain http endpoint off loopback when one of the headers keys that apply to it is set (headers can carry a token); the message
    // names the variables and the rule, never a value. An unset or blank key is fine.
    private static void Validate(IConfiguration configuration, string key, params string[] headersKeys)
    {
        if (!IsSet(configuration, key))
        {
            return;
        }

        string? headers = headersKeys.FirstOrDefault(headersKey => IsSet(configuration, headersKey));
        _ = Uri.TryCreate(configuration[key]!.Trim(), UriKind.Absolute, out Uri? endpoint);
        switch (EndpointRules.Check(endpoint, requireHttpsOffLoopback: headers is not null))
        {
            case EndpointProblem.None:
                return;
            case EndpointProblem.PlainHttpOffLoopback:
                throw new ConfigurationException($"{key} must be https unless the host is loopback when {headers} is set, because headers can carry a token; the values are not shown");
            default:
                throw new ConfigurationException($"{key} must be an absolute http or https URL without user info, query or fragment; the value is not shown");
        }
    }
}