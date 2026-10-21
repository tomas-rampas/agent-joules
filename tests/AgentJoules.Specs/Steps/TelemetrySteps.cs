using System.CommandLine;
using System.Diagnostics.Metrics;
using System.Globalization;

using AgentJoules.Cli.Hosting;
using AgentJoules.Core.Runs;
using AgentJoules.Specs.Support;
using AgentJoules.TestSupport;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;

using Reqnroll;

using Xunit;

using CoreMetric = AgentJoules.Core.Labels.Metric;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-051: the OpenTelemetry instruments of the meter <c>AgentJoules</c> and the telemetry setup (REQ-014). Scoped to the feature because sentences such as "the call completes" are
/// reused by other stories with other meanings. The state lives in <see cref="TelemetryContext"/>; the exporters are checked by what the setup adds to the service collection (the OTLP
/// one, because building its provider would send data to the endpoint) and by what the console receives (the console one, which has no writer to inject).
/// </summary>
[Binding]
[Scope(Feature = "AJ-051 Record OpenTelemetry instruments and the activity source")]
public sealed class TelemetrySteps(TelemetryContext context)
{
    private const string EndpointKey = "OTEL_EXPORTER_OTLP_ENDPOINT";

    private static readonly string[] FiveInstruments =
    [
        "agentjoules.step.energy",
        "agentjoules.step.power",
        "agentjoules.step.tokens",
        "agentjoules.step.latency",
        "agentjoules.step.cost",
    ];

    private readonly TelemetryContext _context = context;

    [Given(@"^an in-memory metric exporter and a metered local fake call with measured energy and usage$")]
    public void GivenALocalCallWithEnergyAndUsage()
    {
        _context.Rig = TelemetryRig.Create();

        // The M2 cost calculator answers N/A, so a stub prices the call and the cost instrument has something to record.
        _context.Call = new TelemetryCall { Cost = new FixedCostCalculator(CoreMetric.Estimated(0.01, "EUR")) };
    }

    [Given(@"^an in-memory metric exporter and a metered fake call on tier ""([^""]*)""$")]
    public void GivenACallOnTier(string tier)
    {
        _context.Rig = TelemetryRig.Create();
        _context.Call = new TelemetryCall { Tier = Enum.Parse<Tier>(tier, ignoreCase: true) };
    }

    [Given(@"^an in-memory metric exporter and a metered fake call whose response has no usage$")]
    public void GivenACallWithoutUsage()
    {
        _context.Rig = TelemetryRig.Create();
        _context.Call = new TelemetryCall { Usage = null };
    }

    [Given(@"^the environment variable OTEL_EXPORTER_OTLP_ENDPOINT is set in the configuration override$")]
    public void GivenTheEndpointIsSet()
    {
        _context.Configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([new KeyValuePair<string, string?>(EndpointKey, "http://127.0.0.1:4317")])
            .Build();
        _context.ConsoleEnabled = false;
    }

    [Given(@"^the run command with --no-console-telemetry$")]
    public void GivenTheRunCommandWithTheOptOutFlag()
    {
        // The shared option on its own command shows what the flag decides; the run command (AJ-054) offers the same option and DryRunTests.Run measures its registrations.
        Option<bool> option = TelemetryOptions.CreateNoConsoleTelemetry();
        var run = new Command("run") { option };
        ParseResult parsed = run.Parse(["--no-console-telemetry"]);
        Assert.Empty(parsed.Errors);
        _context.ConsoleEnabled = !parsed.GetValue(option);
    }

    [When(@"^the call completes and the meter provider is flushed$")]
    public async Task WhenTheCallCompletesAndIsFlushed()
    {
        TelemetryRig rig = _context.RequireRig();
        _ = await rig.CallAsync(_context.Call);
        _context.Points = rig.Flush();
    }

    [When(@"^the telemetry setup is built$")]
    public void WhenTheTelemetrySetupIsBuilt()
    {
        var services = new ServiceCollection();
        _ = services.AddMetrics();
        _ = TelemetrySetup.Add(services, _context.Configuration, _context.ConsoleEnabled);
        _context.Services = services;
    }

    [Then(@"^the instruments energy, power, tokens, latency and cost each have a measurement$")]
    public void ThenEveryInstrumentHasAMeasurement()
    {
        foreach (string instrument in FiveInstruments)
        {
            Assert.Contains(_context.Points, point => point.Instrument == instrument);
        }

        Assert.Equal(FiveInstruments.Order(StringComparer.Ordinal), _context.Points.Select(point => point.Instrument).Distinct().Order(StringComparer.Ordinal));
    }

    [Then(@"^every measurement has attributes gen_ai\.agent\.name, aj\.tier and gen_ai\.request\.model$")]
    public void ThenEveryMeasurementHasTheThreeAttributes()
    {
        TelemetryRig rig = _context.RequireRig();
        Assert.NotEmpty(_context.Points);
        foreach (MeasurementPoint point in _context.Points)
        {
            Assert.Equal(rig.Agent, point.Tag("gen_ai.agent.name"));
            Assert.Equal("local", point.Tag("aj.tier"));
            Assert.Equal(rig.Model, point.Tag("gen_ai.request.model"));
        }
    }

    [Then(@"^the energy and power instruments have no measurement$")]
    public void ThenEnergyAndPowerHaveNoMeasurement()
    {
        // The call did happen: its latency was recorded, so an empty energy series is not an empty exporter.
        Assert.Contains(_context.Points, point => point.Instrument == "agentjoules.step.latency");
        Assert.DoesNotContain(_context.Points, point => point.Instrument is "agentjoules.step.energy" or "agentjoules.step.power");
    }

    [Then(@"^the tokens instrument has no measurement and no zero value$")]
    public void ThenTokensHaveNoMeasurement()
    {
        Assert.Contains(_context.Points, point => point.Instrument == "agentjoules.step.latency");
        Assert.DoesNotContain(_context.Points, point => point.Instrument == "agentjoules.step.tokens");
    }

    [Then(@"^an OTLP exporter is registered$")]
    public void ThenAnOtlpExporterIsRegistered()
    {
        ServiceCollection services = _context.Services ?? throw new InvalidOperationException("No When step built the telemetry setup.");

        var without = new ServiceCollection();
        _ = without.AddMetrics();
        _ = TelemetrySetup.Add(without, new ConfigurationBuilder().Build(), _context.ConsoleEnabled);

        // The options of the OTLP exporter exist exactly when it was added, and it was added once to the metrics and once to the traces.
        Assert.True(HasOtlpOptions(services));
        Assert.False(HasOtlpOptions(without));
        Assert.Equal(Registrations(without, "IConfigureMeterProviderBuilder") + 1, Registrations(services, "IConfigureMeterProviderBuilder"));
        Assert.Equal(Registrations(without, "IConfigureTracerProviderBuilder") + 1, Registrations(services, "IConfigureTracerProviderBuilder"));
    }

    [Then(@"^no console exporter is registered$")]
    public void ThenNoConsoleExporterIsRegistered()
    {
        ServiceCollection services = _context.Services ?? throw new InvalidOperationException("No When step built the telemetry setup.");
        Assert.False(_context.ConsoleEnabled);

        // This step starts the providers of the setup below; with an endpoint in the configuration that would start an OTLP exporter that sends data.
        Assert.Null(_context.Configuration[EndpointKey]);

        // One metrics reader fewer than the same setup with the console on ...
        var withConsole = new ServiceCollection();
        _ = withConsole.AddMetrics();
        _ = TelemetrySetup.Add(withConsole, _context.Configuration, consoleEnabled: true);
        Assert.Equal(Registrations(withConsole, "IConfigureMeterProviderBuilder") - 1, Registrations(services, "IConfigureMeterProviderBuilder"));

        // ... and nothing reaches the console when a measurement is recorded and flushed.
        string agent = "Agent-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        using var capture = new ConsoleCapture();
        using (ServiceProvider provider = services.BuildServiceProvider())
        {
            MeterProvider meters = provider.GetRequiredService<MeterProvider>();
            new AgentJoules.Metering.AgentJoulesInstruments(provider.GetRequiredService<IMeterFactory>()).Record(StepReadingFixtures.Metered(agent));
            Assert.True(meters.ForceFlush(30_000));
        }

        Assert.DoesNotContain(agent, capture.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("agentjoules.step", capture.Text, StringComparison.Ordinal);
    }

    private static bool HasOtlpOptions(IServiceCollection services) => services.Any(descriptor => descriptor.ServiceType == typeof(IOptionsFactory<OtlpExporterOptions>));

    // The provider builders the SDK registers are internal types, so the registrations are counted by interface name: each exporter adds exactly one.
    private static int Registrations(IServiceCollection services, string interfaceName) => services.Count(descriptor => descriptor.ServiceType.Name == interfaceName);
}