using System.CommandLine;

using AgentJoules.Core.Cli;

namespace AgentJoules.TestSupport;

/// <summary>
/// The test-only commands the harness adds to the CLI root through <c>CliHostOverrides.AdditionalCommands</c>. The application runs each of
/// them inside a built and started host with its token linked to <c>ApplicationStopping</c>, exactly as it runs <c>probe</c>, so they exercise
/// the exit-code mapping, the shutdown path and the redacting writers without a GPU. One instance belongs to one run.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>fake-wait</c>: signals <see cref="WaitStarted"/>, awaits its token, and throws when it is cancelled (after recording <see cref="CancellationObserved"/>).</item>
/// <item><c>fake-write</c>: writes <see cref="JsonDocument"/> to <c>Console.Out</c> with <c>Write</c> and no newline, and returns 0.</item>
/// <item><c>fake-console-error &lt;text&gt;</c>: writes one line to <c>Console.Error</c> directly and returns 0.</item>
/// <item><c>fake-cancel</c>: throws <see cref="OperationCanceledException"/> although no shutdown was requested.</item>
/// <item><c>fake-throw &lt;type&gt; [message]</c>: throws <c>MeasurementPreconditionException</c>, <c>ConfigurationException</c> or <c>InvalidOperationException</c> with the message (default <c>fake failure</c>).</item>
/// </list>
/// </remarks>
public sealed class FakeCommands
{
    /// <summary>The document <c>fake-write</c> prints, without a trailing newline.</summary>
    public const string JsonDocument = "{\"fake\":true,\"items\":[1,2,3]}";

    private readonly TaskCompletionSource _waitStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _cancellationObserved;

    /// <summary>Creates the commands.</summary>
    public FakeCommands()
    {
        Commands = [CreateWait(), CreateWrite(), CreateConsoleError(), CreateCancel(), CreateThrow()];
    }

    /// <summary>The commands to hand to <c>CliHostOverrides.AdditionalCommands</c>.</summary>
    public IReadOnlyList<Command> Commands { get; }

    /// <summary>Completes when <c>fake-wait</c> started waiting on its token.</summary>
    public Task WaitStarted => _waitStarted.Task;

    /// <summary>True once <c>fake-wait</c> saw its token cancelled.</summary>
    public bool CancellationObserved => Volatile.Read(ref _cancellationObserved) == 1;

    private static Exception CreateException(string type, string message) => type switch
    {
        nameof(MeasurementPreconditionException) => new MeasurementPreconditionException(message),
        nameof(ConfigurationException) => new ConfigurationException(message),
        nameof(InvalidOperationException) => new InvalidOperationException(message),
        _ => new ArgumentOutOfRangeException(nameof(type), type, "unknown fake exception type"),
    };

    private static async Task<int> ThrowAsync(string type, string message)
    {
        await Task.Yield();
        throw CreateException(type, message);
    }

    private static async Task<int> CancelAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        await Task.Yield();
        throw new OperationCanceledException();
    }

    private static Task<int> WriteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        Console.Out.Write(JsonDocument);
        return Task.FromResult(0);
    }

    private Command CreateWait()
    {
        var command = new Command("fake-wait", "Waits until its token is cancelled.");
        command.SetAction(WaitAsync);
        return command;
    }

    private async Task<int> WaitAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        _waitStarted.TrySetResult();
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Volatile.Write(ref _cancellationObserved, 1);
            throw;
        }

        return 0;
    }

    private static Command CreateWrite()
    {
        var command = new Command("fake-write", "Writes a JSON document with Write and no trailing newline.");
        command.SetAction(WriteAsync);
        return command;
    }

    private static Command CreateConsoleError()
    {
        var command = new Command("fake-console-error", "Writes one line to Console.Error directly.");
        var text = new Argument<string>("text");
        command.Add(text);
        command.SetAction(parseResult =>
        {
            Console.Error.WriteLine(parseResult.GetRequiredValue(text));
            return 0;
        });
        return command;
    }

    private static Command CreateCancel()
    {
        var command = new Command("fake-cancel", "Throws OperationCanceledException with no shutdown requested.");
        command.SetAction(CancelAsync);
        return command;
    }

    private static Command CreateThrow()
    {
        var command = new Command("fake-throw", "Throws the named exception type with a message.");
        var type = new Argument<string>("type");
        var message = new Argument<string>("message")
        {
            Arity = ArgumentArity.ZeroOrOne,
            DefaultValueFactory = _ => "fake failure",
        };
        command.Add(type);
        command.Add(message);
        command.SetAction((parseResult, cancellationToken) =>
            ThrowAsync(parseResult.GetRequiredValue(type), parseResult.GetRequiredValue(message)));
        return command;
    }
}