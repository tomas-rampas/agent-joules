using System.CommandLine;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AgentJoules.Cli;

/// <summary>
/// Test overrides for <see cref="CliApplication.RunAsync"/>; production passes null. Non-null overrides are deny-by-default (design 3.6): the
/// environment-variable and user-secrets sources are omitted unless requested, the default GPU factory reports no GPU, and every relative path
/// resolves against <see cref="WorkingDirectory"/>.
/// </summary>
/// <param name="Services">Registers or replaces services after the defaults and after the services a command adds to its own host (the telemetry of <c>run</c>), so fakes win.</param>
/// <param name="Configuration">In-memory keys, the last configuration source; root keys such as <c>NVIDIA_API_KEY</c> and full keys such as <c>AgentJoules:Meter:SampleRateHz</c>.</param>
/// <param name="IncludeProcessEnvironment">True to add the process environment variables as a configuration source.</param>
/// <param name="IncludeUserSecrets">True to add the user-secrets store as a configuration source.</param>
/// <param name="Stdout">The writer behind the redacting standard-output wrapper; <c>Console.Out</c> when null.</param>
/// <param name="Stderr">The writer behind the redacting standard-error wrapper; <c>Console.Error</c> when null.</param>
/// <param name="WorkingDirectory">The content root; the current directory when null.</param>
/// <param name="AdditionalCommands">Test-only commands added to the root; each runs inside a built and started host with its token linked to <c>ApplicationStopping</c>.</param>
/// <param name="OnHostStarted">Called once per host after <c>StartAsync</c> succeeded and before the command action runs (the harness raises shutdown through it).</param>
/// <param name="OnInvocationConfigured">Called with the exact <see cref="InvocationConfiguration"/> immediately before <c>InvokeAsync</c>.</param>
public sealed record CliHostOverrides(
    Action<IServiceCollection>? Services = null,
    IReadOnlyDictionary<string, string?>? Configuration = null,
    bool IncludeProcessEnvironment = false,
    bool IncludeUserSecrets = false,
    TextWriter? Stdout = null,
    TextWriter? Stderr = null,
    string? WorkingDirectory = null,
    IReadOnlyList<Command>? AdditionalCommands = null,
    Action<IHost>? OnHostStarted = null,
    Action<InvocationConfiguration>? OnInvocationConfigured = null);