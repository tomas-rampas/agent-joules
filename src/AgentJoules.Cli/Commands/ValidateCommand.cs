using System.CommandLine;
using System.Globalization;
using System.Text;

using AgentJoules.Cli.Hosting;
using AgentJoules.Cli.Output;
using AgentJoules.Core.Cli;
using AgentJoules.Core.Configuration;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Security;
using AgentJoules.Reporting;
using AgentJoules.Reporting.Endpoints;
using AgentJoules.Reporting.Results;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AgentJoules.Cli.Commands;

/// <summary>
/// <c>validate [path]</c> (AJ-080, REQ-015): checks run files against the embedded schema and the results model, with <see cref="RunFileValidator"/>. The path is a run file or a folder (default
/// <c>results</c>), relative paths resolve against <see cref="CommandHost.WorkingDirectory"/>. In a folder only the files whose name matches <see cref="RunFileName.Pattern"/> are checked, in ordinal
/// order of the name: <c>probe-*.json</c>, <c>calibration-*.json</c> and everything else are ignored, not reported. In this run-file mode the command builds no host (no configuration, no key, no GPU, no telemetry), so it works
/// in a folder that has no <c>config</c> and no <c>docs</c>. Standard output carries the failure lines of each file and then, always, <c>Validated &lt;n&gt; files</c> (n counts the run files checked, valid
/// or not). When any file fails, the action throws <see cref="ConfigurationException"/> (<c>&lt;k&gt; of &lt;n&gt; run files are invalid</c>) after printing, so the application writes the one error line
/// and exits 2. A missing path and a named file that is not a run file name exit 2 and name the path as given (cut to 256 characters).
/// </summary>
/// <remarks>
/// <c>--config</c> (AJ-100, REQ-018) is registered with zero or one value (C-05) and switches the command to the model-ID check: run files are ignored, and a positional path with it exits 2
/// (<c>validate --config takes no path argument</c>). The flag form checks the configuration the host binds like every other command (<c>config/appsettings.json</c> under the content root and the
/// environment overrides); with a value, that JSON file replaces <c>config/appsettings.json</c>. The check is <see cref="ConfigModelValidator"/> over <c>docs/endpoints.md</c> (<c>Paths:Endpoints</c>,
/// resolved under the content root like <c>Paths:Prices</c>) and is file-only: it makes no request. When every ID is recorded the command prints <c>Validated &lt;n&gt; model IDs</c> and exits 0; otherwise
/// it prints each problem line on standard output and ends with a <see cref="ModelNotFoundException"/> (exit 4, one folded error line on standard error). A missing or unreadable document is exit 2 and
/// names <c>Paths:Endpoints</c>.
/// </remarks>
internal static class ValidateCommand
{
    private const string DefaultPath = "results";
    private const int PathEchoLength = 256;
    private const int MaxEndpointsBytes = 1024 * 1024;
    private const string EndpointsUnreadable = PathsOptions.EndpointsKey + ": the endpoints document could not be read; the path is not shown";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

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
            Description = "Check the model IDs of the configuration against docs/endpoints.md (file only, no request) instead of checking run files: every tier model, agent model and the judge model must be listed in its section. Without a value the configuration is config/appsettings.json; with a path, that file. Takes no path argument.",
            HelpName = "path",
            Arity = ArgumentArity.ZeroOrOne,
        };

        var command = new Command("validate", "Check run files against the results schema and the results model.") { path, config };
        command.SetAction((parseResult, cancellationToken) =>
            ValidateAsync(host, parseResult.GetValue(path), parseResult.GetResult(config) is not null, parseResult.GetValue(config), cancellationToken));
        return command;
    }

    private static async Task<int> ValidateAsync(CommandHost host, string? given, bool configGiven, string? configPath, CancellationToken cancellationToken)
    {
        if (configGiven)
        {
            return given is null
                ? await CheckModelIdsAsync(host, configPath, cancellationToken)
                : throw new ConfigurationException("validate --config takes no path argument");
        }

        string[] files = Select(host.WorkingDirectory, given ?? DefaultPath, host.Redactor);
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

    // validate --config: the host binds the configuration (the named file or config/appsettings.json, plus the environment), and the validator compares its model IDs with the endpoints document.
    private static Task<int> CheckModelIdsAsync(CommandHost host, string? configPath, CancellationToken cancellationToken) =>
        host.RunAsync(configPath, configure: null, (running, _) => Task.FromResult(CheckModelIds(host, running)), cancellationToken);

    private static int CheckModelIds(CommandHost host, IHost running)
    {
        AgentJoulesOptions options = running.Services.GetRequiredService<IOptions<AgentJoulesOptions>>().Value;
        EndpointsDocument document = EndpointsDocParser.Parse(ReadEndpointsDocument(host.WorkingDirectory, options.Paths.Endpoints));

        ConfigValidationResult result = ConfigModelValidator.Validate(options, document, host.Redactor);
        if (!result.IsValid)
        {
            foreach (string line in result.Problems)
            {
                host.Output.WriteLine(line);
            }

            throw new ModelNotFoundException(result.Problems);
        }

        host.Output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Validated {result.ValidatedIds} model IDs"));
        return (int)ExitCode.Success;
    }

    // The document named by Paths:Endpoints, resolved under the content root; a missing or unreadable file names the key and never the path (the message of the operating system holds it).
    private static string ReadEndpointsDocument(string workingDirectory, string value)
    {
        string full = ContentPaths.Resolve(workingDirectory, value, PathsOptions.EndpointsKey);
        byte[] bytes;
        try
        {
            using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);

            // A pipe can block a read for ever and the null device opens as an empty file; neither is a document.
            if (!stream.CanSeek)
            {
                throw new ConfigurationException(EndpointsUnreadable);
            }

            // One byte more than the cap: reading it proves the file is larger, and no more of a large file is ever read.
            byte[] buffer = new byte[MaxEndpointsBytes + 1];
            int read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            bytes = read > MaxEndpointsBytes ? throw new ConfigurationException(EndpointsUnreadable) : buffer.AsSpan(0, read).ToArray();
        }
        catch (Exception failure) when (failure is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new ConfigurationException(PathsOptions.EndpointsKey + ": the endpoints document was not found; the path is not shown");
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            throw new ConfigurationException(EndpointsUnreadable);
        }

        // The same bounds as the other file readers: bytes that are not UTF-8 (a UTF-16 document included) are refused, a UTF-8 byte order mark is accepted and dropped.
        ReadOnlySpan<byte> content = bytes;
        if (content.StartsWith(Encoding.UTF8.Preamble))
        {
            content = content[Encoding.UTF8.Preamble.Length..];
        }

        try
        {
            return StrictUtf8.GetString(content);
        }
        catch (DecoderFallbackException)
        {
            throw new ConfigurationException(EndpointsUnreadable);
        }
    }

    // The run files to check: the named file, or the files of the folder whose name is a run file name, in ordinal order of the name.
    private static string[] Select(string workingDirectory, string given, SecretRedactor redactor)
    {
        string echo = TextEcho.ForMessage(redactor.Redact(given), PathEchoLength);
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