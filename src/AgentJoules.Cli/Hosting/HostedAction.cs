using System.CommandLine;
using System.CommandLine.Invocation;

namespace AgentJoules.Cli.Hosting;

/// <summary>
/// Wraps the action of a command a test added through <c>CliHostOverrides.AdditionalCommands</c> so it runs the way <c>probe</c> does: inside a
/// built and started host, with its token linked to <c>ApplicationStopping</c> and its failures mapped by <see cref="ExitCodeMapper"/>.
/// </summary>
internal sealed class HostedAction : AsynchronousCommandLineAction
{
    private readonly CommandLineAction _inner;
    private readonly CommandHost _host;

    private HostedAction(CommandLineAction inner, CommandHost host)
    {
        _inner = inner;
        _host = host;
    }

    /// <summary>Replaces the action of <paramref name="command"/> by a hosted one; a command without an action is left alone.</summary>
    internal static void Attach(Command command, CommandHost host)
    {
        CommandLineAction? action = command.Action is HostedAction hosted ? hosted._inner : command.Action;
        if (action is not null)
        {
            command.Action = new HostedAction(action, host);
        }
    }

    public override Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default) =>
        _host.RunAsync(
            configPath: null,
            (_, token) => _inner switch
            {
                AsynchronousCommandLineAction asynchronous => asynchronous.InvokeAsync(parseResult, token),
                SynchronousCommandLineAction synchronous => Task.FromResult(synchronous.Invoke(parseResult)),
                _ => throw new InvalidOperationException("unsupported command action: " + _inner.GetType().Name),
            },
            cancellationToken);
}