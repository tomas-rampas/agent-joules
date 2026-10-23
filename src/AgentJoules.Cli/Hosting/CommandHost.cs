using AgentJoules.Cli.Output;
using AgentJoules.Core.Cli;
using AgentJoules.Core.Security;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentJoules.Cli.Hosting;

/// <summary>
/// Runs one command action inside its own host: builds it, validates the options (before the host starts, so an options failure is the first and
/// only thing reported), starts it, hands the action a token linked to the System.CommandLine token and to <c>ApplicationStopping</c>, and turns any
/// failure into the one redacted error line and the exit code of <see cref="ExitCodeMapper"/>. Shared by <c>probe</c>, <c>run</c> and the commands a test adds
/// through <c>CliHostOverrides.AdditionalCommands</c>.
/// </summary>
internal sealed class CommandHost(CliHostOverrides? overrides, KeyRegistry keys, TextWriter output, TextWriter error)
{
    /// <summary>The redacting standard-output writer.</summary>
    internal TextWriter Output { get; } = output;

    /// <summary>The redacting standard-error writer.</summary>
    internal TextWriter Error { get; } = error;

    /// <summary>The directory relative paths resolve against: the working directory of the test overrides, or the current directory.</summary>
    internal string WorkingDirectory { get; } = overrides?.WorkingDirectory ?? Directory.GetCurrentDirectory();

    /// <summary>True in production and when the test overrides opt in: the process environment is a configuration source, as it is for the host of the other commands.</summary>
    internal bool IncludeProcessEnvironment { get; } = overrides is null || overrides.IncludeProcessEnvironment;

    /// <summary>The masker of the keys this process knows; a command that writes text to a file passes it through this before the file is written.</summary>
    internal SecretRedactor Redactor { get; } = keys.Redactor;

    /// <summary>
    /// Runs <paramref name="action"/> in a started host and returns its exit code, or the code of the failure it threw. An
    /// <see cref="OperationCanceledException"/> while the linked token is cancelled (the caller's token, which System.CommandLine cancels on
    /// SIGINT and SIGTERM, or <c>ApplicationStopping</c>, which SIGHUP raises) is a shutdown: exit 5 with the line
    /// <c>error: interrupted by shutdown (OperationCanceledException)</c>. A <see cref="ModelNotFoundException"/> prints its error line first and then the listed IDs
    /// (<see cref="ConsoleReporter.ReportModelNotFound"/>), because the error line is one folded line.
    /// </summary>
    /// <param name="configPath">The <c>--config</c> value, or null for <c>config/appsettings.json</c>.</param>
    /// <param name="configure">
    /// The services of this command, added after the composition root and before the test overrides (so a test still replaces them), with the host configuration; <c>run</c> adds its
    /// telemetry here, <c>probe</c> passes null.
    /// </param>
    /// <param name="action">The command action, run in the started host.</param>
    /// <param name="commandToken">The token System.CommandLine hands to the command.</param>
    /// <returns>The exit code of the action, or of the failure it threw.</returns>
    internal async Task<int> RunAsync(
        string? configPath,
        Action<IServiceCollection, IConfiguration>? configure,
        Func<IHost, CancellationToken, Task<int>> action,
        CancellationToken commandToken)
    {
        ArgumentNullException.ThrowIfNull(action);

        IHost? host = null;
        CancellationTokenSource? linked = null;
        try
        {
            host = AgentJoulesHostFactory.Build(configPath, configure, overrides, keys, Output, Error);
            IHostApplicationLifetime lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
            linked = CancellationTokenSource.CreateLinkedTokenSource(commandToken, lifetime.ApplicationStopping);

            // Validating first makes an options failure surface before the host logs anything about starting.
            host.Services.GetService<IStartupValidator>()?.Validate();
            await host.StartAsync(linked.Token);
            overrides?.OnHostStarted?.Invoke(host);

            return await action(host, linked.Token);
        }
        catch (Exception failure)
        {
            Exception reported = Unwrap(failure);
            bool shutdown = linked?.IsCancellationRequested ?? commandToken.IsCancellationRequested;
            if (shutdown && reported is OperationCanceledException)
            {
                Error.WriteLine(ErrorLine.Format("interrupted by shutdown", nameof(OperationCanceledException), keys.Redactor));
            }
            else
            {
                ErrorLine.Write(Error, reported, keys.Redactor);
                if (reported is ModelNotFoundException notFound)
                {
                    new ConsoleReporter(Output, Error).ReportModelNotFound(notFound);
                }
            }

            LogFailure(host, reported);
            return ExitCodeMapper.Map(reported, shutdown);
        }
        finally
        {
            if (host is not null)
            {
                await StopQuietlyAsync(host);
            }

            linked?.Dispose();
        }
    }

    // A host that fails to stop must not replace the exit code of the action: the failure is logged at Debug and nothing else is printed.
    private static async Task StopQuietlyAsync(IHost host)
    {
        try
        {
            await host.StopAsync(CancellationToken.None);
        }
        catch (Exception stopFailure)
        {
            ILogger logger = CreateLogger(host);
            Log.HostStopFailed(logger, stopFailure);
        }
        finally
        {
            // Disposing the host flushes the console logger, so every record is written before the writers are drained.
            host.Dispose();
        }
    }

    // The stack trace goes to the logger at Debug only; without a host (the configuration was unreadable) there is no logger to filter it.
    private static void LogFailure(IHost? host, Exception failure)
    {
        if (host is not null)
        {
            ILogger logger = CreateLogger(host);
            Log.CommandFailed(logger, failure);
        }
    }

    private static ILogger CreateLogger(IHost host) => host.Services.GetRequiredService<ILoggerFactory>().CreateLogger(Log.Category);

    // ValidateOnStart reports several failing options types as one AggregateException; a single one is the failure itself.
    private static Exception Unwrap(Exception failure) =>
        failure is AggregateException { InnerExceptions.Count: 1 } aggregate ? Unwrap(aggregate.InnerExceptions[0]) : failure;
}