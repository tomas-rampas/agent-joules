using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;

using AgentJoules.Cli.Commands;
using AgentJoules.Cli.Hosting;
using AgentJoules.Cli.Output;
using AgentJoules.Core.Cli;
using AgentJoules.Core.Security;

namespace AgentJoules.Cli;

/// <summary>
/// The entry point of the command line (design 3.6): parses the arguments, runs the command inside a host and returns the documented exit code.
/// </summary>
/// <remarks>
/// <c>Console.Out</c> and <c>Console.Error</c> are replaced by <see cref="RedactingTextWriter"/> wrappers (created with <c>leaveOpen: true</c>) before
/// anything is parsed or any host is built, and the keys the file-free sources hold (user secrets, the process environment, the in-memory override)
/// are registered at the same moment, so a parse error, a path or a diagnostic message that echoes a key is masked. Response files (<c>@file</c>) are
/// disabled, because they would echo file contents in a parse error. The wrappers are drained in a <c>finally</c> (which forwards text held back
/// without a newline), a <c>ProcessExit</c> handler drains them too, and the original console writers are restored. An exception that escapes a
/// thread, a timer or an async void is printed by the runtime below <c>System.Console</c>, so an <c>AppDomain.UnhandledException</c> handler
/// writes the redacted line, drains the wrappers and calls <see cref="Environment.Exit(int)"/> with 1. Calls are serialised: the console is
/// process-wide.
/// </remarks>
public static class CliApplication
{
    private static readonly TimeSpan ProcessTerminationTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan WorkerThrowWait = TimeSpan.FromSeconds(30);
    private static readonly SemaphoreSlim RunGate = new(1, 1);

    /// <summary>Runs the command line and returns the process exit code.</summary>
    /// <param name="args">The arguments without the program name.</param>
    /// <param name="cancellationToken">
    /// Cancelled by the caller. <c>Program.cs</c> passes <see cref="CancellationToken.None"/>: Ctrl+C (SIGINT) and SIGTERM cancel the token
    /// System.CommandLine hands to the command, and SIGHUP raises <c>ApplicationStopping</c>; all three end the command with exit code 5.
    /// </param>
    /// <param name="overrides">Null in production; tests pass deny-by-default overrides.</param>
    /// <returns>
    /// 0 on success; 2 for a parse error, which prints one <c>error: &lt;messages&gt; (ParseError)</c> line on stderr (every parse message, joined with <c>; </c>), or for no command, which prints the usage
    /// on stderr; otherwise the code <see cref="ExitCodeMapper"/> assigns.
    /// </returns>
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken, CliHostOverrides? overrides = null)
    {
        ArgumentNullException.ThrowIfNull(args);

        await RunGate.WaitAsync(cancellationToken);
        try
        {
            return await RunSerialisedAsync(args, overrides, cancellationToken);
        }
        finally
        {
            RunGate.Release();
        }
    }

    private static async Task<int> RunSerialisedAsync(string[] args, CliHostOverrides? overrides, CancellationToken cancellationToken)
    {
        TextWriter originalOut = Console.Out;
        TextWriter originalError = Console.Error;
        var keys = new KeyRegistry(new SecretRedactor());

        // One synchronised wrapper per stream, shared by Console and by this class, so the logger thread and the command never interleave inside a call.
        TextWriter output = TextWriter.Synchronized(new RedactingTextWriter(overrides?.Stdout ?? originalOut, keys.Redactor, leaveOpen: true));
        TextWriter innerError = overrides?.Stderr ?? originalError;
        TextWriter error = TextWriter.Synchronized(new RedactingTextWriter(innerError, keys.Redactor, leaveOpen: true));

        // Under overrides the unhandled-exception line also goes to the real stderr, because the harness writer is not read when the process dies;
        // when the harness writer is the real stderr there is nothing to add.
        TextWriter? realError = overrides is null || ReferenceEquals(innerError, originalError) ? null : originalError;

        void DrainOnExit(object? sender, EventArgs e) => DrainQuietly(output, error);
        void OnUnhandled(object sender, UnhandledExceptionEventArgs e) => HandleUnhandled(e, keys.Redactor, output, error, realError);

        AppDomain.CurrentDomain.ProcessExit += DrainOnExit;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandled;
        Console.SetOut(output);
        Console.SetError(error);
        try
        {
            keys.RegisterEarly(overrides, error);
            return await ExecuteAsync(args, overrides, keys, output, error, cancellationToken);
        }
        catch (Exception failure)
        {
            // Anything the command wrappers did not map (an action that bypasses the host) still ends as one redacted line and a code.
            ErrorLine.Write(error, failure, keys.Redactor);
            return ExitCodeMapper.Map(failure, cancellationToken.IsCancellationRequested);
        }
        finally
        {
            // In production both handlers stay registered for the life of the process: the wrappers are leaveOpen and the process is about to end.
            if (overrides is not null)
            {
                AppDomain.CurrentDomain.UnhandledException -= OnUnhandled;
                AppDomain.CurrentDomain.ProcessExit -= DrainOnExit;
            }

            Console.SetOut(originalOut);
            Console.SetError(originalError);
            DrainQuietly(output, error);
        }
    }

    private static async Task<int> ExecuteAsync(
        string[] args,
        CliHostOverrides? overrides,
        KeyRegistry keys,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var host = new CommandHost(overrides, keys, output, error);
        RootCommand root = BuildRoot(host, overrides, keys, error);

        // A response file (@path) would echo the file's lines in a parse error.
        ParseResult parseResult = root.Parse(args, new ParserConfiguration { ResponseFileTokenReplacer = null });
        if (parseResult.Errors.Count > 0)
        {
            // System.CommandLine returns 1 for a parse error by default (design C-04); the contract is 2. Like every failure, the parse errors end as exactly one line
            // "error: <message> (<type>)": the messages of all the errors joined with "; ", each masked and folded on its own, and the type name of the error objects.
            string messages = string.Join("; ", parseResult.Errors.Select(parseError => ErrorLine.Sanitize(parseError.Message, keys.Redactor)).Where(static message => message.Length > 0));
            error.WriteLine(ErrorLine.Format(messages, nameof(ParseError), keys.Redactor));

            return (int)ExitCode.InvalidArguments;
        }

        var configuration = new InvocationConfiguration
        {
            Output = output,
            Error = error,
            ProcessTerminationTimeout = ProcessTerminationTimeout,
            EnableDefaultExceptionHandler = false,
        };
        overrides?.OnInvocationConfigured?.Invoke(configuration);

        return await parseResult.InvokeAsync(configuration, cancellationToken);
    }

    private static RootCommand BuildRoot(CommandHost host, CliHostOverrides? overrides, KeyRegistry keys, TextWriter error)
    {
        var root = new RootCommand("Agent Joules: measure the GPU energy of AI agent workflows.");
        var throwOnWorker = new Option<string>(CliOptions.DebugThrowOnWorker)
        {
            Description = "Diagnostic: start a worker thread that throws with this message.",
            Hidden = true,
        };
        root.Add(throwOnWorker);
        root.Add(ProbeCommand.Create(host));
        root.Add(RunCommand.Create(host));
        root.Add(ValidateCommand.Create(host));

        // A command only a test adds runs inside a host exactly like probe does.
        if (overrides?.AdditionalCommands is { } additional)
        {
            foreach (Command command in additional)
            {
                HostedAction.Attach(command, host);
                root.Add(command);
            }
        }

        root.SetAction((parseResult, cancellationToken) =>
            parseResult.GetValue(throwOnWorker) is { } message
                ? ThrowOnWorkerAsync(message, error, keys.Redactor, cancellationToken)
                : PrintUsageAsync(root, error));
        return root;
    }

    // No command: the usage is printed, exit 2, no error line (it is help text, not a failure; assumed, the design is silent).
    private static async Task<int> PrintUsageAsync(RootCommand root, TextWriter error)
    {
        var usage = new InvocationConfiguration { Output = error, Error = error, EnableDefaultExceptionHandler = false, ProcessTerminationTimeout = null };
        await root.Parse(["--help"]).InvokeAsync(usage);
        return (int)ExitCode.InvalidArguments;
    }

    // The hidden diagnostic behind the child-process test: an exception that escapes a bare thread reaches AppDomain.UnhandledException, whose
    // handler exits the process with 1. The main thread only waits; reaching the end of the wait means the handler did not run.
    private static async Task<int> ThrowOnWorkerAsync(string message, TextWriter error, SecretRedactor redactor, CancellationToken cancellationToken)
    {
        var worker = new Thread(() => throw new InvalidOperationException(message)) { IsBackground = true, Name = "debug-throw-on-worker" };
        worker.Start();
        await Task.Delay(WorkerThrowWait, cancellationToken);

        ErrorLine.Write(error, new TimeoutException("the worker-thread exception handler did not run within 30 s"), redactor);
        return (int)ExitCode.Unexpected;
    }

    private static void HandleUnhandled(UnhandledExceptionEventArgs e, SecretRedactor redactor, TextWriter output, TextWriter error, TextWriter? realError)
    {
        try
        {
            string line = e.ExceptionObject is Exception exception
                ? ErrorLine.Format(exception, redactor)
                : ErrorLine.Format(Convert.ToString(e.ExceptionObject, CultureInfo.InvariantCulture), "unhandled", redactor);
            error.WriteLine(line);
            error.Flush();
            realError?.WriteLine(line);
            DrainQuietly(output, error);
        }
        finally
        {
            Environment.Exit((int)ExitCode.Unexpected);
        }
    }

    // Disposing a leaveOpen wrapper forwards the text held back without a newline and leaves the inner writer open; it may be repeated. A failure
    // of one writer (a closed pipe) must not stop the other from draining, and must never surface from an exit path.
    private static void DrainQuietly(TextWriter output, TextWriter error)
    {
        foreach (TextWriter writer in new[] { output, error })
        {
            try
            {
                writer.Dispose();
            }
            catch (Exception)
            {
                // Nothing can be reported: the writer that failed is the one that would carry the report.
            }
        }
    }
}