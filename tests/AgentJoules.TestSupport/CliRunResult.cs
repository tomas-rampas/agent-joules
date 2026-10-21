namespace AgentJoules.TestSupport;

/// <summary>What a harness run produced: the exit code and everything written to the two output writers once <c>RunAsync</c> returned.</summary>
/// <param name="ExitCode">The process exit code the application returned.</param>
/// <param name="Stdout">Text the application wrote to standard output, after the final drain.</param>
/// <param name="Stderr">Text the application wrote to standard error, after the final drain.</param>
public sealed record CliRunResult(int ExitCode, string Stdout, string Stderr)
{
    /// <summary>True when <c>Console.Out</c> and <c>Console.Error</c> were the same instances before and after the run (measured inside the harness gate, so no other harness run can interfere).</summary>
    public bool ConsoleRestored { get; init; } = true;

    /// <summary>The URL of every request the harness refused because no fake route handled it (the <see cref="DenyAllHttpMessageHandler"/> of the run), in order; empty when the run stayed inside its fakes.</summary>
    public IReadOnlyList<string> DeniedRequests { get; init; } = [];

    /// <summary>The lines of <see cref="Stderr"/> that start with <c>error:</c>.</summary>
    public IReadOnlyList<string> ErrorLines => [.. StderrLines.Where(line => line.StartsWith("error:", StringComparison.Ordinal))];

    /// <summary>The lines of <see cref="Stderr"/> without their line terminators.</summary>
    public IReadOnlyList<string> StderrLines => [.. Stderr.Split('\n').Select(line => line.TrimEnd('\r'))];
}