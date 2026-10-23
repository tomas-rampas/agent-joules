using System.CommandLine;
using System.Globalization;

using AgentJoules.Cli.Hosting;
using AgentJoules.Cli.Output;
using AgentJoules.Core.Cli;
using AgentJoules.Core.Labels;
using AgentJoules.Reporting.Results;

namespace AgentJoules.Cli.Commands;

/// <summary>
/// <c>validate [path]</c> (AJ-080, REQ-015): checks run files against the embedded schema and the results model, with <see cref="RunFileValidator"/>. The path is a run file or a folder (default
/// <c>results</c>), relative paths resolve against <see cref="CommandHost.WorkingDirectory"/>. In a folder only the files whose name matches <see cref="RunFileName.Pattern"/> are checked, in ordinal
/// order of the name: <c>probe-*.json</c>, <c>calibration-*.json</c> and everything else are ignored, not reported. The command builds no host (no configuration, no key, no GPU, no telemetry), so it works
/// in a folder that has no <c>config</c> and no <c>docs</c>. Standard output carries the failure lines of each file and then, always, <c>Validated &lt;n&gt; files</c> (n counts the run files checked, valid
/// or not). When any file fails, the action throws <see cref="ConfigurationException"/> (<c>&lt;k&gt; of &lt;n&gt; run files are invalid</c>) after printing, so the application writes the one error line
/// and exits 2. A missing path and a named file that is not a run file name exit 2 and name the path as given (cut to 256 characters). <c>--config</c> is registered with zero or one value (C-05); in
/// M3 it exits 2 saying that the model-ID check arrives in M4, never 0.
/// </summary>
internal static class ValidateCommand
{
    private const string DefaultPath = "results";
    private const int PathEchoLength = 256;

    internal static Command Create(CommandHost host)
    {
        var path = new Argument<string?>("path")
        {
            Description = "A run file, or a folder whose run files are checked (default results).",
            Arity = ArgumentArity.ZeroOrOne,
        };

        // An argument that may take any text would swallow an option it does not know (`validate --bogus`); the parse check reports it instead, like every other command.
        path.Validators.Add(static result =>
        {
            if (result.GetValueOrDefault<string?>() is { } value && value.StartsWith('-'))
            {
                result.AddError($"Unrecognized command or argument '{TextEcho.ForMessage(value)}'.");
            }
        });
        var config = new Option<string?>("--config")
        {
            Description = "Reserved: with or without a configuration path it will check the model IDs in M4; until then it exits 2.",
            HelpName = "path",
            Arity = ArgumentArity.ZeroOrOne,
        };

        var command = new Command("validate", "Check run files against the results schema and the results model.") { path, config };
        command.SetAction((parseResult, cancellationToken) =>
            ValidateAsync(host, parseResult.GetValue(path), parseResult.GetResult(config) is not null, cancellationToken));
        return command;
    }

    private static async Task<int> ValidateAsync(CommandHost host, string? given, bool configGiven, CancellationToken cancellationToken)
    {
        if (configGiven)
        {
            throw new ConfigurationException("validate --config: the model-ID check arrives in M4; nothing was checked");
        }

        string[] files = Select(host.WorkingDirectory, given ?? DefaultPath);
        int invalid = 0;
        try
        {
            foreach (string file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                IReadOnlyList<string> failures = await RunFileValidator.CheckFileAsync(file, cancellationToken);
                foreach (string line in failures)
                {
                    host.Output.WriteLine(line);
                }

                if (failures.Count > 0)
                {
                    invalid++;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Ctrl+C or SIGTERM during a scan: the same shutdown line and code as a hosted command.
            host.Error.WriteLine(ErrorLine.Format("interrupted by shutdown", nameof(OperationCanceledException)));
            return (int)ExitCode.Unreachable;
        }

        host.Output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Validated {files.Length} files"));
        return invalid == 0
            ? (int)ExitCode.Success
            : throw new ConfigurationException(string.Create(CultureInfo.InvariantCulture, $"{invalid} of {files.Length} run files are invalid"));
    }

    // The run files to check: the named file, or the files of the folder whose name is a run file name, in ordinal order of the name.
    private static string[] Select(string workingDirectory, string given)
    {
        string echo = TextEcho.ForMessage(given, PathEchoLength);
        if (string.IsNullOrWhiteSpace(given))
        {
            throw new ConfigurationException("the path argument must not be blank");
        }

        string full;
        try
        {
            full = Path.GetFullPath(given, workingDirectory);
        }
        catch (Exception failure) when (failure is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ConfigurationException($"the path \"{echo}\" is not a valid path");
        }

        if (Directory.Exists(full))
        {
            return RunFilesIn(full, echo);
        }

        string[] named = File.Exists(full) ? [full] : throw new ConfigurationException($"the path \"{echo}\" does not exist");

        return RunFileName.IsValid(Path.GetFileName(full))
            ? named
            : throw new ConfigurationException($"the file \"{echo}\" is not a run file: its name is not <timestamp>-<tier>[-baseline].json");
    }

    // The listing is lazy, so it is materialised inside the try. A folder that cannot be listed (denied, removed meanwhile, a dangling junction, a path that is too long) ends as exit 2 naming the path as given:
    // the message of the operating system holds the full path and is not passed on.
    private static string[] RunFilesIn(string folder, string echo)
    {
        try
        {
            return
            [
                .. Directory.EnumerateFiles(folder, "*.json", SearchOption.TopDirectoryOnly)
                    .Where(static file => RunFileName.IsValid(Path.GetFileName(file)))
                    .Order(StringComparer.Ordinal),
            ];
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            throw new ConfigurationException($"the folder \"{echo}\" could not be read");
        }
    }
}