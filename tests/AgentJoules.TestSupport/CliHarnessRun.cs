using System.CommandLine;
using System.Globalization;
using System.Text;

using AgentJoules.Cli;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AgentJoules.TestSupport;

/// <summary>
/// One run of <c>CliApplication.RunAsync</c> under the harness. <c>Console.Out</c> and <c>Console.Error</c> are process-wide, and the
/// application replaces them with redacting wrappers while it runs (criteria 4 and 13), so a process-wide gate lets one harness run at a time:
/// parallel test classes queue behind it instead of racing on the console. The harness always passes non-null overrides, so a run never
/// reads process environment variables or user secrets and never opens a real GPU unless a test opts in.
/// </summary>
public sealed class CliHarnessRun
{
    internal static readonly SemaphoreSlim Gate = new(1, 1);

    // True inside CliHarness.RunExclusiveAsync: a harness run or a second exclusive section started from there would wait for the gate its own
    // caller holds, so both fail fast instead of deadlocking.
    internal static readonly AsyncLocal<bool> InExclusiveSection = new();

    private readonly FakeCommands _commands = new();
    private readonly TaskCompletionSource<IHostApplicationLifetime> _lifetime = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TextWriter _stdout;
    private readonly TextWriter _stderr;
    private volatile IReadOnlyDictionary<string, string?>? _hostSettings;
    private volatile InvocationConfiguration? _invocation;
    private volatile IReadOnlyList<string> _providerNames = [];

    internal CliHarnessRun(string arguments, CliHostOverrides? overrides, TextWriter? stdout, TextWriter? stderr, CancellationToken cancellationToken)
    {
        _stdout = stdout ?? overrides?.Stdout ?? new StringWriter(CultureInfo.InvariantCulture);
        _stderr = stderr ?? overrides?.Stderr ?? new StringWriter(CultureInfo.InvariantCulture);
        Result = Task.Run(() => ExecuteAsync(arguments, overrides, cancellationToken), CancellationToken.None);
    }

    /// <summary>Completes with the exit code and the captured output once <c>RunAsync</c> returned and the writers were drained.</summary>
    public Task<CliRunResult> Result { get; }

    /// <summary>Completes when <c>fake-wait</c> started waiting on its token.</summary>
    public Task CommandWaiting => _commands.WaitStarted;

    /// <summary>True once <c>fake-wait</c> saw its token cancelled.</summary>
    public bool CancellationObserved => _commands.CancellationObserved;

    /// <summary>The configuration the application handed to <c>InvokeAsync</c> (through <c>CliHostOverrides.OnInvocationConfigured</c>); null until the application got that far.</summary>
    public InvocationConfiguration? InvocationConfiguration => _invocation;

    /// <summary>The type names of the configuration providers of the host, in order (no values); empty until a host started.</summary>
    public IReadOnlyList<string> ConfigurationProviderNames => _providerNames;

    /// <summary>True once the application built and started a host.</summary>
    public bool HostStarted => _hostSettings is not null;

    /// <summary>The value the host configuration held for <paramref name="key"/> when the host started (keys are case-insensitive); null when absent or when no host started.</summary>
    public string? Setting(string key) => _hostSettings is { } settings && settings.TryGetValue(key, out string? value) ? value : null;

    /// <summary>
    /// Raises <c>IHostApplicationLifetime.StopApplication()</c> on the host the application built. In production only SIGHUP takes that path into
    /// <c>ApplicationStopping</c> (Ctrl+C and SIGTERM cancel the command token instead); the harness takes it to prove the shutdown mapping. Waits for the host to start first.
    /// </summary>
    public async Task RaiseShutdownAsync(CancellationToken cancellationToken)
    {
        IHostApplicationLifetime lifetime = await _lifetime.Task.WaitAsync(cancellationToken);
        lifetime.StopApplication();
    }

    private static string[] Split(string arguments)
    {
        List<string> tokens = [];
        var current = new StringBuilder();
        bool quoted = false;
        bool pending = false;
        foreach (char character in arguments)
        {
            if (character == '"')
            {
                quoted = !quoted;
                pending = true;
            }
            else if (!quoted && char.IsWhiteSpace(character))
            {
                if (pending)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    pending = false;
                }
            }
            else
            {
                current.Append(character);
                pending = true;
            }
        }

        if (pending)
        {
            tokens.Add(current.ToString());
        }

        return [.. tokens];
    }

    private async Task<CliRunResult> ExecuteAsync(string arguments, CliHostOverrides? overrides, CancellationToken cancellationToken)
    {
        if (InExclusiveSection.Value)
        {
            throw new InvalidOperationException("a harness run cannot start inside CliHarness.RunExclusiveAsync: the exclusive section holds the gate the run waits for");
        }

        await Gate.WaitAsync(cancellationToken);
        try
        {
            CliHostOverrides supplied = overrides ?? new CliHostOverrides();
            using TempWorkspace? workspace = supplied.WorkingDirectory is null ? TempWorkspace.Create() : null;
            CliHostOverrides effective = supplied with
            {
                Stdout = _stdout,
                Stderr = _stderr,
                WorkingDirectory = supplied.WorkingDirectory ?? workspace!.Root,
                AdditionalCommands = [.. _commands.Commands, .. supplied.AdditionalCommands ?? []],
                OnHostStarted = host =>
                {
                    Capture(host);
                    supplied.OnHostStarted?.Invoke(host);
                },
                OnInvocationConfigured = configuration =>
                {
                    _invocation = configuration;
                    supplied.OnInvocationConfigured?.Invoke(configuration);
                },
            };

            TextWriter outBefore = Console.Out;
            TextWriter errorBefore = Console.Error;

            int exitCode = await CliApplication.RunAsync(Split(arguments), cancellationToken, effective);

            return new CliRunResult(exitCode, _stdout.ToString() ?? string.Empty, _stderr.ToString() ?? string.Empty)
            {
                ConsoleRestored = ReferenceEquals(Console.Out, outBefore) && ReferenceEquals(Console.Error, errorBefore),
            };
        }
        finally
        {
            Gate.Release();
        }
    }

    private void Capture(IHost host)
    {
        // A snapshot, not the live configuration: the host is disposed before the test reads it.
        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string?> pair in host.Services.GetRequiredService<IConfiguration>().AsEnumerable())
        {
            settings[pair.Key] = pair.Value;
        }

        _hostSettings = settings;
        _providerNames = host.Services.GetRequiredService<IConfiguration>() is IConfigurationRoot root
            ? [.. root.Providers.Select(provider => provider.GetType().Name)]
            : [];
        _lifetime.TrySetResult(host.Services.GetRequiredService<IHostApplicationLifetime>());
    }
}