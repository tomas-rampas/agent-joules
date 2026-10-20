using System.Runtime.InteropServices;

using Microsoft.Extensions.Hosting;

namespace AgentJoules.Cli.Hosting;

/// <summary>
/// The host lifetime of the CLI. It does not install a console handler: <c>DisableDefaults</c> still registers <c>ConsoleLifetime</c>, which would
/// add a second Ctrl+C handler and print <c>Application started...</c> on every command (design-review N-07). Ctrl+C (SIGINT) and SIGTERM are
/// handled by System.CommandLine, which cancels the token handed to <c>RunAsync</c>; the command token is linked to
/// <c>ApplicationStopping</c>, so both end in the same exit code 5. Nothing registers SIGHUP, so this lifetime does: SIGHUP raises
/// <c>StopApplication()</c>. On Windows registering SIGHUP succeeds (measured on Windows 11); a platform that does not support it
/// throws <see cref="PlatformNotSupportedException"/>, which is ignored, and the command then ends through the other two signals only.
/// </summary>
/// <param name="lifetime">The application lifetime whose <c>ApplicationStopping</c> a SIGHUP raises.</param>
internal sealed class SignalHostLifetime(IHostApplicationLifetime lifetime) : IHostLifetime, IDisposable
{
    private PosixSignalRegistration? _hangup;

    public Task WaitForStartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _hangup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, context =>
            {
                context.Cancel = true;
                lifetime.StopApplication();
            });
        }
        catch (PlatformNotSupportedException)
        {
            // This platform has no SIGHUP; the other signals still end the command.
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _hangup?.Dispose();
        _hangup = null;
    }
}