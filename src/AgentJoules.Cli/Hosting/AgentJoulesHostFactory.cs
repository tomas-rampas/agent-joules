using AgentJoules.Cli.Output;
using AgentJoules.Core.Cli;
using AgentJoules.Core.Configuration;
using AgentJoules.Core.Energy;
using AgentJoules.Core.Gpu;
using AgentJoules.Core.Live;
using AgentJoules.Nvml;
using AgentJoules.Nvml.Platform;
using AgentJoules.Workflow;
using AgentJoules.Workflow.Runs;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.FileProviders.Physical;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentJoules.Cli.Hosting;

/// <summary>
/// Builds the host of one command (ADR-18): <see cref="HostApplicationBuilder"/> with <c>DisableDefaults</c>, exactly the configuration sources
/// the prompt names (the JSON file, optional user secrets, environment variables) plus the in-memory test override, options bound through
/// <see cref="OptionsLoader.Bind(IConfiguration, AgentJoulesOptions)"/> and validated on start, logging to stderr at every level, and a lifetime that turns SIGHUP into
/// <c>ApplicationStopping</c> without installing a second Ctrl+C handler.
/// </summary>
/// <remarks>
/// Logging configuration: only the <c>Logging:LogLevel</c> keys (and the per-provider <c>Logging:&lt;Provider&gt;:LogLevel</c> sections) are passed to the
/// logging system, after validation. Every other <c>Logging:*</c> key, in particular <c>Logging:Console:*</c> (queue length, queue-full mode,
/// formatter options), is ignored, because the CLI fixes the console provider options itself and the binder would echo a wrong-typed value.
/// </remarks>
internal static class AgentJoulesHostFactory
{
    internal const string DefaultConfigPath = "config/appsettings.json";

    /// <summary>Builds the host; the caller validates and starts it.</summary>
    /// <param name="configPath">The <c>--config</c> value, or null for <c>config/appsettings.json</c>; relative paths resolve against the content root.</param>
    /// <param name="configure">The services of the command (for <c>run</c>, its telemetry), applied after the composition root and before the test overrides; null for none.</param>
    /// <param name="overrides">Null in production.</param>
    /// <param name="keys">The registry the configured key values are registered with.</param>
    /// <param name="output">The standard-output writer (the redacting wrapper).</param>
    /// <param name="error">The standard-error writer (the redacting wrapper).</param>
    /// <exception cref="ConfigurationException">
    /// A configuration file is missing or unreadable, a log level is invalid, or the command's services refuse the configuration (an OTLP endpoint of the wrong shape); the message names
    /// the path or key and never a value.
    /// </exception>
    internal static IHost Build(
        string? configPath,
        Action<IServiceCollection, IConfiguration>? configure,
        CliHostOverrides? overrides,
        KeyRegistry keys,
        TextWriter output,
        TextWriter error)
    {
        string contentRoot = overrides?.WorkingDirectory ?? Directory.GetCurrentDirectory();
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true,
            ContentRootPath = contentRoot,
        });

        AddConfigurationSources(builder.Configuration, configPath, overrides, contentRoot);
        keys.Register(builder.Configuration, error);
        ConfigureLogging(builder);
        ConfigureOptions(builder.Services);
        ConfigureServices(builder.Services, keys, output, error, contentRoot);

        // The services of this command only (run registers its telemetry); before the overrides, so a test can still replace them.
        configure?.Invoke(builder.Services, builder.Configuration);

        // Under overrides the deny-by-default services are applied after the command's services, so a command can never re-open what the isolation closes.
        ApplyIsolation(builder.Services, overrides);

        // Last, so a test replaces any default above.
        overrides?.Services?.Invoke(builder.Services);

        return builder.Build();
    }

    private static void AddConfigurationSources(ConfigurationManager configuration, string? configPath, CliHostOverrides? overrides, string contentRoot)
    {
        AddJsonFile(configuration, Path.GetFullPath(configPath ?? DefaultConfigPath, contentRoot));

        if (overrides is null || overrides.IncludeUserSecrets)
        {
            AddUserSecrets(configuration);
        }

        if (overrides is null || overrides.IncludeProcessEnvironment)
        {
            configuration.AddEnvironmentVariables();
        }

        if (overrides?.Configuration is { } memory)
        {
            configuration.AddInMemoryCollection(memory);
        }
    }

    // The default file provider hides dot-prefixed files, so the provider is built with no exclusion filter. A failure names the path only:
    // the inner exceptions quote offending keys and text.
    private static void AddJsonFile(ConfigurationManager configuration, string path)
    {
        if (Directory.Exists(path))
        {
            throw new ConfigurationException("the configuration file is not a file: " + path);
        }

        try
        {
            using var provider = new PhysicalFileProvider(Path.GetDirectoryName(path)!, ExclusionFilters.None);
            configuration.AddJsonFile(provider, Path.GetFileName(path), optional: false, reloadOnChange: false);
        }
        catch (Exception failure) when (IsFileFailure(failure))
        {
            throw new ConfigurationException(Describe("the configuration file", path, failure));
        }
    }

    private static void AddUserSecrets(ConfigurationManager configuration)
    {
        try
        {
            configuration.AddUserSecrets(typeof(CliApplication).Assembly, optional: true);
        }
        catch (Exception failure) when (IsFileFailure(failure))
        {
            throw new ConfigurationException(Describe("the user-secrets file", UserSecretsPath(), failure));
        }
    }

    private static bool IsFileFailure(Exception failure) =>
        failure is IOException or UnauthorizedAccessException or InvalidDataException or FormatException;

    private static string Describe(string what, string path, Exception failure) => failure switch
    {
        FileNotFoundException or DirectoryNotFoundException => $"{what} was not found: {path}",
        InvalidDataException or FormatException => $"{what} could not be read as JSON: {path}",
        _ => $"{what} could not be read: {path}",
    };

    // Where AddUserSecrets looks for this assembly's secrets.json (the same rule as the UserSecrets package).
    private static string UserSecretsPath()
    {
        string id = typeof(CliApplication).Assembly
            .GetCustomAttributes(typeof(Microsoft.Extensions.Configuration.UserSecrets.UserSecretsIdAttribute), inherit: false)
            .OfType<Microsoft.Extensions.Configuration.UserSecrets.UserSecretsIdAttribute>()
            .Select(attribute => attribute.UserSecretsId)
            .FirstOrDefault() ?? "unknown";

        string? root = Environment.GetEnvironmentVariable("APPDATA");
        return string.IsNullOrEmpty(root)
            ? Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? string.Empty, ".microsoft", "usersecrets", id, "secrets.json")
            : Path.Combine(root, "Microsoft", "UserSecrets", id, "secrets.json");
    }

    private static void ConfigureLogging(HostApplicationBuilder builder)
    {
        // Only the LogLevel keys of the Logging section reach the logging configuration. An invalid level would make that configuration throw
        // and echo the value, and a wrong-typed console option (Logging:Console:MaxQueueLength) would make the binder echo it, so the levels are
        // validated here (the message names the key only) and every other Logging key is dropped: the CLI fixes the console provider options itself.
        var levels = new List<KeyValuePair<string, string?>>();
        CollectLogLevels(builder.Configuration.GetSection("Logging"), levels);
        IConfigurationSection logging = new ConfigurationBuilder().AddInMemoryCollection(levels).Build().GetSection("Logging");

        builder.Logging.AddConfiguration(logging);
        builder.Logging.AddAgentJoulesConsole();
        builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Warning);

        // CommandHost logs the failure itself, at Debug; the host's own Error record would put a stack trace on stderr at the default level. Its
        // Critical records (a background service that crashed) stay visible.
        builder.Logging.AddFilter("Microsoft.Extensions.Hosting.Internal.Host", LogLevel.Critical);
    }

    // Copies every key under a LogLevel section (Logging:LogLevel and the per-provider Logging:<Provider>:LogLevel sections) after validating its value.
    private static void CollectLogLevels(IConfigurationSection section, List<KeyValuePair<string, string?>> levels)
    {
        bool inLevels = string.Equals(section.Key, "LogLevel", StringComparison.OrdinalIgnoreCase);
        foreach (IConfigurationSection child in section.GetChildren())
        {
            if (!inLevels)
            {
                CollectLogLevels(child, levels);
            }
            else if (child.Value is { } value)
            {
                if (!(Enum.TryParse(value, ignoreCase: true, out LogLevel level) && Enum.IsDefined(level)))
                {
                    throw new ConfigurationException(child.Path + " is not a valid log level");
                }

                levels.Add(new KeyValuePair<string, string?>(child.Path, value));
            }
        }
    }

    private static void ConfigureOptions(IServiceCollection services)
    {
        services.AddSingleton<IConfigureOptions<AgentJoulesOptions>, AgentJoulesOptionsSetup>();
        services.AddSingleton<IValidateOptions<AgentJoulesOptions>, FlatSectionsValidator>();
        services.AddSingleton<IValidateOptions<AgentJoulesOptions>, CrossFieldValidator>();
        services.AddOptions<AgentJoulesOptions>().ValidateOnStart();
    }

    private static void ConfigureServices(IServiceCollection services, KeyRegistry keys, TextWriter output, TextWriter error, string contentRoot)
    {
        services.RemoveAll<IHostLifetime>();
        services.AddSingleton<IHostLifetime, SignalHostLifetime>();
        services.AddSingleton(keys.Redactor);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new ConsoleReporter(output, error));

        // The composition root of every command (AJ-054, AJ-056): the provider clients of the tiers (registered once; a second call throws), the preflight with the dry run, the
        // orchestrator of a measured run and the seams it takes: the cost calculator (the price-table one of M3, read from Paths:Prices only when first resolved), the judge service (AddAgentJoulesJudge, the rubric read from Paths:Rubric only when first resolved), the local runtime that Local:Runtime selects (ollama, or the no-op one until the NIM runtime of AJ-105) and
        // the console listener of the step lines. The meter gate, the agent factory and the instruments belong to run and are added by its service hook. Nothing here is resolved
        // unless a command asks for it.
        services.AddAgentJoulesProviderClients().AddAgentJoulesPreflight();
        services.AddSingleton<IRunOrchestrator, RunOrchestrator>();
        services.AddAgentJoulesCost(contentRoot);
        services.AddAgentJoulesJudge(contentRoot);
        services.AddAgentJoulesLocalRuntime();
        services.AddSingleton<IRunListener, ConsoleRunListener>();

        // The live sink of the orchestrator: the null sink unless the dashboard registers its own (AJ-091).
        services.TryAddSingleton<ILiveEventSink>(NullLiveEventSink.Instance);

        services.AddSingleton<IGpuTelemetrySourceFactory>(static _ => new NvmlGpuTelemetrySourceFactory(new NvmlApi()));

        // The power source of the machine (REQ-021, AJ-106): the Windows API or sysfs reader; a test harness registers one that never reads the host.
        services.TryAddSingleton<IPowerSourceDetector>(static _ => new PowerSourceDetector());
    }

    // The deny-by-default services of design 3.6 (b) under test overrides: no GPU is reachable and the power source is unknown unless the test overrides register otherwise (EDGE-011, AJ-106: a run never reads the
    // host's power state under test overrides). Applied after the command's services and before the test overrides; the deny-all HTTP handler is the harness's own, installed through the test overrides.
    private static void ApplyIsolation(IServiceCollection services, CliHostOverrides? overrides)
    {
        if (overrides is not null)
        {
            services.RemoveAll<IGpuTelemetrySourceFactory>();
            services.AddSingleton<IGpuTelemetrySourceFactory, UnavailableGpuTelemetrySourceFactory>();
            services.RemoveAll<IPowerSourceDetector>();
            services.AddSingleton<IPowerSourceDetector, UnknownPowerSourceDetector>();
        }
    }
}