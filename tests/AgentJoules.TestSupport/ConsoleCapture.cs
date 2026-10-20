using System.Globalization;

namespace AgentJoules.TestSupport;

/// <summary>
/// Captures what the process writes to <see cref="Console.Out"/> while it is alive. The console metrics exporter of OpenTelemetry writes to the console static and has no
/// writer to inject, so this is the only way a test can see whether it is registered. The redirection is process-wide, so it takes the gate of the CLI harness (the harness runs and <c>CliHarness.RunExclusiveAsync</c> replace the console statics too): a second
/// capture, a harness run or an exclusive section waits until the first is disposed, which serializes the tests that use the console statics. Create it in a sync test or step (the gate is a semaphore, not a thread-bound lock) and
/// dispose it before the test ends.
/// </summary>
public sealed class ConsoleCapture : IDisposable
{
    private readonly TextWriter _previous;
    private readonly StringWriter _writer = new(CultureInfo.InvariantCulture);
    private bool _disposed;

    /// <summary>Takes the gate and redirects <see cref="Console.Out"/> to a buffer.</summary>
    public ConsoleCapture()
    {
        if (CliHarnessRun.InExclusiveSection.Value)
        {
            throw new InvalidOperationException("A console capture cannot start inside CliHarness.RunExclusiveAsync: the exclusive section holds the gate the capture waits for.");
        }

        CliHarnessRun.Gate.Wait();
        CliHarnessRun.InExclusiveSection.Value = true;
        _previous = Console.Out;
        Console.SetOut(_writer);
    }

    /// <summary>Everything written to the console since the capture began.</summary>
    public string Text => _writer.ToString();

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Console.SetOut(_previous);
        _writer.Dispose();
        CliHarnessRun.InExclusiveSection.Value = false;
        CliHarnessRun.Gate.Release();
    }
}