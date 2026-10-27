using System.CommandLine;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using AgentJoules.Cli.Hosting;
using AgentJoules.Cli.Output;
using AgentJoules.Core.Cli;
using AgentJoules.Core.Configuration;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Core.Security;
using AgentJoules.Reporting.Content;
using AgentJoules.Reporting.Json;
using AgentJoules.Reporting.Readme;
using AgentJoules.Reporting.Render;
using AgentJoules.Reporting.Results;

using Microsoft.Extensions.Configuration;

namespace AgentJoules.Cli.Commands;

/// <summary>
/// <c>report [--in path]... [--out folder] [--update-readme] [--verify-readme] [--render-content] [--config path]</c> (AJ-086, AJ-144, REQ-016, REQ-025, REQ-026, REQ-030): turns the newest usable run file of each tier
/// into <c>summary.md</c>, <c>joules-per-step.svg</c> and <c>index.html</c>. The command builds no host (no key, no GPU, no telemetry) and reads files only; relative paths resolve against <see cref="CommandHost.WorkingDirectory"/>.
/// <c>--update-readme</c> replaces the text between the results markers of <c>README.md</c> with <see cref="ReadmeResultsBlock"/> (the screenshot link only while <c>docs/report/dashboard.png</c> exists); <c>--verify-readme</c>
/// compares the same block and exits 7 on a difference (and writes nothing when it is the only flag); <c>--render-content</c> renders <c>content/linkedin-posts.template.md</c> into <c>content/linkedin-posts.md</c> with
/// <see cref="ContentTemplateRenderer"/>. Both texts are computed, and every failure raised, before any file is written; a text that holds a registered key is refused, never masked.
/// </summary>
/// <remarks>
/// <para>
/// <c>--in</c> (default <c>results</c>) takes one run file or folder per occurrence and may be repeated; the inputs are combined by <see cref="RunFileSelector"/>. A missing path, a file whose name is not a run file
/// name, a file reached under one name from two inputs and a header that cannot be read end with exit 2 naming the file; a readable file that is not used (a run that failed, is still running or is
/// <c>non-publishable</c>) is listed as <c>Skipped &lt;file&gt;: &lt;reason&gt;</c> (the first 20, then <c>Skipped &lt;N&gt; more files</c>) and does not change the exit code. Only the selected files are read (strict UTF-8, at most
/// <see cref="RunFileValidator.MaxBytes"/>) and checked with <see cref="RunFileValidator"/>; a failing file prints its lines in the format of <c>validate</c> on standard output and the command ends with exit 2. A file that
/// holds a key the process knows (<see cref="CommandHost.Redactor"/> would change its text) fails with the line <c>&lt;file&gt;: (root) holds a registered key</c> and is never rendered.
/// </para>
/// <para>
/// Nothing is written before every file has passed and all three texts have been rendered; a rendered text that the redactor would change is refused (exit 2, nothing written) instead of masked, so the written bytes are
/// always the exact deterministic render. The texts are then staged as <c>&lt;name&gt;.partial</c> files (UTF-8 without a byte order mark, flushed) in <c>--out</c> (default <c>docs/report</c>, created when absent; a file in
/// its place is exit 2), and only when all three exist are they moved over their targets in the order summary, chart, page. Other files of the folder are left alone. A failed write is exit 1 and names the file only. After the files,
/// <c>&lt;working directory&gt;/docs/.nojekyll</c> is created when <c>docs</c> exists and the marker does not, whatever <c>--out</c> is.
/// </para>
/// <para>
/// <c>--config</c> (default <c>config/appsettings.json</c>) is read for <c>Report:RepositoryUrl</c> only: the JSON file (at most 1 MiB), then the <c>AGENTJOULES__</c> environment variables when the process environment is a source, bound
/// through <see cref="OptionsLoader.Bind(IConfiguration)"/> (so an unknown key is refused) and then the rule of <see cref="ReportOptions.IsRepositoryUrl"/>; the rest of the file is not validated. An explicit
/// <c>--config</c> that does not exist is exit 2; an absent default file means no URL. No console line shows a configured value, a line names a configuration file by its file name, and every path echo is masked before it is cut.
/// </para>
/// </remarks>
internal static partial class ReportCommand
{
    private const string DefaultIn = "results";
    private const string DefaultOut = "docs/report";
    private const string DefaultConfig = "config/appsettings.json";
    private const string DocsFolder = "docs";
    private const string SummaryFileName = "summary.md";
    private const string PageFileName = "index.html";
    private const string ReadmeName = "README.md";
    private const string ScreenshotFileName = "dashboard.png";
    private const string ContentFolder = "content";
    private const string TemplateFileName = "linkedin-posts.template.md";
    private const string ContentFileName = "linkedin-posts.md";
    private const string PartialSuffix = ".partial";
    private const string BuilderParameterSuffix = " (Parameter 'runs')";
    private const int PathEchoLength = 256;
    private const int MaxListedLines = 20;
    private const int MaxConfigBytes = 1024 * 1024;
    private const int CopyBuffer = 81920;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    internal static Command Create(CommandHost host)
    {
        var input = new Option<string[]>("--in")
        {
            Description = "A run file or a folder of run files; repeat the option to combine inputs.",
            HelpName = "path",
            AllowMultipleArgumentsPerToken = false,
            DefaultValueFactory = static _ => [DefaultIn],
        };
        var output = new Option<string>("--out")
        {
            Description = "The folder the summary, the chart and the page are written to; created when absent.",
            HelpName = "folder",
            DefaultValueFactory = static _ => DefaultOut,
        };
        var config = new Option<string?>("--config")
        {
            Description = "Path of the configuration file, read for Report:RepositoryUrl only (default config/appsettings.json).",
            HelpName = "path",
        };

        var updateReadme = new Option<bool>("--update-readme")
        {
            Description = "Replace the text between the results markers of README.md with the generated results block.",
        };
        var verifyReadme = new Option<bool>("--verify-readme")
        {
            Description = "Exit 7 when the text between the results markers of README.md is not the generated results block.",
        };
        var renderContent = new Option<bool>("--render-content")
        {
            Description = "Render content/linkedin-posts.template.md from the latest run files into content/linkedin-posts.md.",
        };

        var command = new Command("report", "Write the Markdown summary, the SVG chart and the HTML page from the run files.") { input, output, updateReadme, verifyReadme, renderContent, config };
        command.Validators.Add(result =>
        {
            // An update writes the block and a verify compares it; asked for together, the verify would be skipped and a stale block would pass.
            if (result.GetValue(updateReadme) && result.GetValue(verifyReadme))
            {
                result.AddError("--update-readme and --verify-readme cannot be combined");
            }
        });
        command.SetAction((parseResult, cancellationToken) =>
            ReportAsync(
                host,
                parseResult.GetValue(input) ?? [DefaultIn],
                parseResult.GetValue(output) ?? DefaultOut,
                new ContentFlags(parseResult.GetValue(updateReadme), parseResult.GetValue(verifyReadme), parseResult.GetValue(renderContent)),
                parseResult.GetValue(config),
                cancellationToken));
        return command;
    }

    // The three flags of AJ-144. Without any of them the command only writes the summary, the chart and the page.
    private readonly record struct ContentFlags(bool UpdateReadme, bool VerifyReadme, bool RenderContent)
    {
        // A run that only verifies writes nothing, not even the report files.
        public bool WritesReport => !VerifyReadme || UpdateReadme || RenderContent;

        public bool Any => UpdateReadme || VerifyReadme || RenderContent;
    }

    [GeneratedRegex(@"^runs\[(\d+)\]")]
    private static partial Regex RunIndex();

    private static async Task<int> ReportAsync(CommandHost host, string[] inputs, string output, ContentFlags flags, string? configPath, CancellationToken cancellationToken)
    {
        try
        {
            await RunAsync(host, inputs, output, flags, configPath, cancellationToken);
            return (int)ExitCode.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Ctrl+C or SIGTERM during a read: the same shutdown line and code as a hosted command; nothing was written before the reads were over.
            host.Error.WriteLine(ErrorLine.Format("interrupted by shutdown", nameof(OperationCanceledException)));
            return (int)ExitCode.Unreachable;
        }
    }

    private static async Task RunAsync(CommandHost host, string[] inputs, string output, ContentFlags flags, string? configPath, CancellationToken cancellationToken)
    {
        string workingDirectory = host.WorkingDirectory;
        Uri? repositoryUrl = ReadRepositoryUrl(host, workingDirectory, configPath);
        string[] resolved = [.. inputs.Select(given => ResolveInput(workingDirectory, given, host.Redactor))];
        string outputFolder = ResolveOutput(workingDirectory, output, host.Redactor);

        RunFileSelection selection = Select(resolved);
        if (selection.Problems.Count > 0)
        {
            WriteCapped(host.Output, [.. selection.Problems.Select(static problem => $"{problem.FileName}: {problem.Reason}")], static count => $"{count} more {Plural(count, "file cannot", "files cannot")} be used");
            throw new ConfigurationException(string.Create(CultureInfo.InvariantCulture, $"report: {selection.Problems.Count} run {Plural(selection.Problems.Count, "file", "files")} cannot be used"));
        }

        (List<SelectedRun> runs, Dictionary<string, string> texts) = await ReadSelectedAsync(host, selection, cancellationToken);
        ReportModel model = Build(host, runs);

        (string Name, string Text)[] files =
        [
            (SummaryFileName, SummaryMarkdownRenderer.Render(model)),
            (ReportHtmlRenderer.ChartFileName, JoulesSvgRenderer.Render(model)),
            (PageFileName, RenderPage(model, repositoryUrl)),
        ];

        // A rendered text that holds a key the process knows is refused, never masked: masking would change the bytes of a deterministic render (and could break the markup or the policy hash of the page).
        foreach ((string name, string text) in files)
        {
            if (!string.Equals(host.Redactor.Redact(text), text, StringComparison.Ordinal))
            {
                throw new ConfigurationException($"report: {name} holds a registered key; nothing written");
            }
        }

        // The README and the content are computed (and every failure of theirs raised) before anything is written, so a run that fails leaves every file as it was.
        List<(string Folder, string Name, string Text)> extra = [];
        if (flags.Any)
        {
            await PrepareContentAsync(host, flags, model, selection, texts, extra, cancellationToken);
        }

        // Everything has passed and been rendered; from here the files are written.
        WriteCapped(host.Output, [.. selection.Skipped.Select(static skip => $"Skipped {skip.FileName}: {skip.Reason}")], static count => $"Skipped {count} more {Plural(count, "file", "files")}");
        if (flags.WritesReport)
        {
            WriteFiles(outputFolder, files);
            host.Output.WriteLine($"Report written to {Echo(host.Redactor, output)}: {string.Join(", ", files.Select(static file => file.Name))}");
            EnsureNoJekyll(workingDirectory);
        }

        foreach ((string folder, string name, string text) in extra)
        {
            WriteFiles(folder, [(name, text)]);
            host.Output.WriteLine($"{name} written");
        }

        if (flags.VerifyReadme)
        {
            host.Output.WriteLine($"{ReadmeName} results block matches the generated block");
        }
    }

    // ---- the README and the content (AJ-144) ----

    private static async Task PrepareContentAsync(CommandHost host, ContentFlags flags, ReportModel model, RunFileSelection selection, Dictionary<string, string> texts, List<(string Folder, string Name, string Text)> extra, CancellationToken cancellationToken)
    {
        string workingDirectory = host.WorkingDirectory;
        if (flags.UpdateReadme || flags.VerifyReadme)
        {
            // The screenshot is a file the user adds; the block links it only while it exists, and verify applies the same rule.
            string block = ReadmeResultsBlock.Render(model, File.Exists(Path.Combine(workingDirectory, DocsFolder, "report", ScreenshotFileName)));
            string readmePath = Path.Combine(workingDirectory, ReadmeName);
            string readme = await ReadTextFileAsync(readmePath, ReadmeName, cancellationToken);
            if (flags.UpdateReadme)
            {
                string updated = ReadmeResultsBlock.Update(readme, block);
                RefuseKey(host, updated, ReadmeName);
                extra.Add((workingDirectory, ReadmeName, updated));
            }
            else if (!ReadmeResultsBlock.Verify(readme, block))
            {
                throw new ContentDiffersException($"{ReadmeName}: the results block differs from the generated block");
            }
        }

        if (flags.RenderContent)
        {
            string template = await ReadTextFileAsync(Path.Combine(workingDirectory, ContentFolder, TemplateFileName), TemplateFileName, cancellationToken, ContentTemplateRenderer.MaxTemplateBytes);
            // A template that holds a registered key is refused with the fixed message before anything of it is echoed, as a run file is.
            RefuseKey(host, template, TemplateFileName);
            Dictionary<Tier, ContentSource> sources = [];
            foreach ((Tier tier, string path) in selection.Latest)
            {
                // The text that was read and validated, not a second read of the file.
                string fileName = Path.GetFileName(path);
                sources[tier] = new ContentSource(fileName, texts[fileName]);
            }

            string rendered = ContentTemplateRenderer.Render(template, sources);
            RefuseKey(host, rendered, ContentFileName);
            extra.Add((Path.Combine(workingDirectory, ContentFolder), ContentFileName, rendered));
        }
    }

    // A text that holds a key the process knows is refused, never masked.
    private static void RefuseKey(CommandHost host, string text, string name)
    {
        if (!string.Equals(host.Redactor.Redact(text), text, StringComparison.Ordinal))
        {
            throw new ConfigurationException($"report: {name} holds a registered key; nothing written");
        }
    }

    private static async Task<string> ReadTextFileAsync(string path, string name, CancellationToken cancellationToken, int maxBytes = RunFileValidator.MaxBytes)
    {
        if (!File.Exists(path))
        {
            throw new ConfigurationException($"report: the file {name} does not exist");
        }

        try
        {
            // On Unix a FIFO named like the file would block in open(); an entry of size zero is not opened at all (the same guard as the configuration read).
            RefuseEmptyWithoutOpening(path, $"report: the file {name} could not be read as UTF-8 text");
            byte[]? bytes = await ReadBoundedAsync(path, cancellationToken, maxBytes);

            return bytes is null ? throw new ConfigurationException($"report: the file {name} is larger than {maxBytes} bytes") : StrictUtf8.GetString(bytes);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or NotSupportedException or DecoderFallbackException or System.Security.SecurityException)
        {
            throw new ConfigurationException($"report: the file {name} could not be read as UTF-8 text");
        }
    }

    private static string Plural(int count, string singular, string plural) => count == 1 ? singular : plural;

    // The path as the user gave it, masked before it is cut: a cut text would let a part of a key through that the masking no longer matches.
    private static string Echo(SecretRedactor redactor, string text) => TextEcho.ForMessage(redactor.Redact(text), PathEchoLength);

    private static ReportModel Build(CommandHost host, List<SelectedRun> runs)
    {
        try
        {
            return ReportAggregator.Build(runs);
        }
        catch (ArgumentException failure)
        {
            // The message begins with runs[i] and names members, never a value: the file is printed in the form of the other problem lines, and the index is dropped.
            string message = failure.Message.EndsWith(BuilderParameterSuffix, StringComparison.Ordinal) ? failure.Message[..^BuilderParameterSuffix.Length] : failure.Message;
            Match index = RunIndex().Match(message);
            if (!index.Success || !int.TryParse(index.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int position) || position >= runs.Count)
            {
                throw new ConfigurationException("report: the run files were refused by the report builder");
            }

            string member = message[index.Length..].TrimStart('.', ' ');
            host.Output.WriteLine($"{runs[position].FileName}: {TextEcho.ForMessage(member, PathEchoLength)}");
            throw new ConfigurationException("report: 1 run file cannot be used");
        }
    }

    private static string RenderPage(ReportModel model, Uri? repositoryUrl)
    {
        try
        {
            return ReportHtmlRenderer.Render(model, repositoryUrl);
        }
        catch (ArgumentException)
        {
            // The configuration rule runs first and is the same check; this is the second line of defence, and the message of the renderer is fixed.
            throw new ConfigurationException(ReportOptions.RepositoryUrlMessage);
        }
    }

    // ---- the inputs ----

    private static string ResolveInput(string workingDirectory, string given, SecretRedactor redactor)
    {
        if (string.IsNullOrWhiteSpace(given))
        {
            throw new ConfigurationException("the --in value must not be blank");
        }

        string echo = Echo(redactor, given);
        string full = FullPath(workingDirectory, given, echo);
        if (Directory.Exists(full))
        {
            return full;
        }

        bool exists = File.Exists(full);

        return exists && RunFileName.IsValid(Path.GetFileName(full))
            ? full
            : throw new ConfigurationException(
                exists
                    ? $"the file \"{echo}\" is not a run file: its name is not <timestamp>-<tier>[-baseline].json"
                    : $"the path \"{echo}\" does not exist");
    }

    private static string ResolveOutput(string workingDirectory, string given, SecretRedactor redactor)
    {
        if (string.IsNullOrWhiteSpace(given))
        {
            throw new ConfigurationException("the --out value must not be blank");
        }

        string echo = Echo(redactor, given);
        string full = FullPath(workingDirectory, given, echo);

        return File.Exists(full) ? throw new ConfigurationException($"--out \"{echo}\" is a file, not a folder") : full;
    }

    private static string FullPath(string workingDirectory, string given, string echo)
    {
        try
        {
            return Path.GetFullPath(given, workingDirectory);
        }
        catch (Exception failure) when (failure is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ConfigurationException($"the path \"{echo}\" is not a valid path");
        }
    }

    private static RunFileSelection Select(string[] resolved)
    {
        try
        {
            return RunFileSelector.Select(resolved);
        }
        catch (ArgumentException)
        {
            // The inputs were checked above; this is a path that changed meanwhile. The message of the selector echoes the name, so it is not passed on.
            throw new ConfigurationException("report: an input is neither a folder nor a run file");
        }
    }

    // ---- the configuration ----

    private static Uri? ReadRepositoryUrl(CommandHost host, string workingDirectory, string? configPath)
    {
        bool explicitPath = configPath is not null;
        string given = configPath ?? DefaultConfig;
        if (string.IsNullOrWhiteSpace(given))
        {
            throw new ConfigurationException("the --config value must not be blank");
        }

        string full = FullPath(workingDirectory, given, Echo(host.Redactor, given));
        string name = Echo(host.Redactor, Path.GetFileName(Path.TrimEndingDirectorySeparator(full)));

        using var configuration = new ConfigurationManager();
        if (Directory.Exists(full))
        {
            throw new ConfigurationException($"the configuration file \"{name}\" is a folder");
        }

        if (File.Exists(full))
        {
            AddJsonFile(configuration, full, name);
        }
        else if (explicitPath)
        {
            throw new ConfigurationException($"the configuration file \"{name}\" does not exist");
        }

        if (host.IncludeProcessEnvironment)
        {
            _ = configuration.AddEnvironmentVariables();
        }

        // report builds no host, so the key variables the file declares (a tier's ApiKeyVariable) are registered here, as the host does, before any text is judged against the redactor.
        host.RegisterConfiguredKeys(configuration);

        // The shape check refuses an unknown key anywhere in the section; nothing else of the file is judged, because report renders committed files and needs no tier.
        string? value = OptionsLoader.Bind(configuration).Report.RepositoryUrl;

        if (value is null)
        {
            return null;
        }

        // The rule of the configuration refuses an nvapi- key; the host redactor also knows the key values of this process, so a URL that holds one is refused here, before any run file is read.
        return ReportOptions.IsRepositoryUrl(value, out Uri? uri) && string.Equals(host.Redactor.Redact(value), value, StringComparison.Ordinal)
            ? uri
            : throw new ConfigurationException(ReportOptions.RepositoryUrlMessage);
    }

    // At most MaxConfigBytes + 1 bytes are read; a longer file is refused. A pipe or a device is no configuration file: a stream that cannot seek is refused, and on Unix, where opening a FIFO blocks,
    // an entry of size zero is not opened at all (an empty file is not JSON either).
    private static void AddJsonFile(ConfigurationManager configuration, string path, string name)
    {
        try
        {
            RefuseEmptyWithoutOpening(path, $"the configuration file \"{name}\" could not be read as JSON");
            using FileStream stream = File.OpenRead(path);
            if (!stream.CanSeek)
            {
                throw new IOException("The path is not a regular file.");
            }

            byte[] buffer = new byte[MaxConfigBytes + 1];
            int length = 0;
            int read;
            while (length < buffer.Length && (read = stream.Read(buffer, length, buffer.Length - length)) > 0)
            {
                length += read;
            }

            if (length > MaxConfigBytes)
            {
                throw new ConfigurationException($"the configuration file \"{name}\" is larger than {MaxConfigBytes} bytes");
            }

            using var held = new MemoryStream(buffer, 0, length);
            _ = configuration.AddJsonStream(held);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            // The message of the operating system holds the full path and is not passed on.
            throw new ConfigurationException($"the configuration file \"{name}\" could not be read");
        }
        catch (Exception failure) when (failure is InvalidDataException or FormatException or JsonException or InvalidOperationException)
        {
            // The inner exceptions quote offending keys and text; a lone surrogate in a string raises an InvalidOperationException.
            throw new ConfigurationException($"the configuration file \"{name}\" could not be read as JSON");
        }
    }

    private static void RefuseEmptyWithoutOpening(string path, string message)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var info = new FileInfo(path);
        FileSystemInfo target = info.ResolveLinkTarget(returnFinalTarget: true) ?? info;
        if (target is FileInfo { Exists: true, Length: 0 })
        {
            throw new ConfigurationException(message);
        }
    }

    // ---- the run files ----

    // Reads the newest usable file of each tier and variant, in ordinal order of the file name. Every file is checked even when an earlier one failed, so one run lists all the broken files.
    private static async Task<(List<SelectedRun> Runs, Dictionary<string, string> Texts)> ReadSelectedAsync(CommandHost host, RunFileSelection selection, CancellationToken cancellationToken)
    {
        (RunVariant Variant, string Path)[] selected =
        [
            .. selection.Latest.Values.Select(static path => (RunVariant.Primary, path)),
            .. selection.LatestBaseline.Values.Select(static path => (RunVariant.Baseline, path)),
        ];
        Array.Sort(selected, static (left, right) => string.CompareOrdinal(Path.GetFileName(left.Path), Path.GetFileName(right.Path)));

        List<SelectedRun> runs = [];
        Dictionary<string, string> texts = new(StringComparer.Ordinal);
        int failed = 0;
        foreach ((RunVariant variant, string path) in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string name = Path.GetFileName(path);
            (IReadOnlyList<string> failures, RunDocument? document, string? text) = await ReadCheckedAsync(path, name, host.Redactor, cancellationToken);
            foreach (string line in failures)
            {
                host.Output.WriteLine(line);
            }

            if (failures.Count > 0 || document is null || text is null)
            {
                failed++;
            }
            else
            {
                runs.Add(new SelectedRun(variant, name, document));
                texts[name] = text;
            }
        }

        return failed == 0
            ? (runs, texts)
            : throw new ConfigurationException(string.Create(CultureInfo.InvariantCulture, $"report: {failed} run {Plural(failed, "file", "files")} failed validation"));
    }

    // One read serves the check and the model, so the file that was checked is the file that is rendered. The failure lines are those of validate; the three read failures use its wording.
    internal static async Task<(IReadOnlyList<string> Failures, RunDocument? Document)> ReadAndCheckAsync(string path, string name, SecretRedactor redactor, CancellationToken cancellationToken)
    {
        (IReadOnlyList<string> failures, RunDocument? document, _) = await ReadCheckedAsync(path, name, redactor, cancellationToken);

        return (failures, document);
    }

    // The same read with the checked text as well, so that --render-content renders the bytes that were validated and reads no run file a second time.
    private static async Task<(IReadOnlyList<string> Failures, RunDocument? Document, string? Text)> ReadCheckedAsync(string path, string name, SecretRedactor redactor, CancellationToken cancellationToken)
    {
        byte[]? bytes;
        try
        {
            bytes = await ReadBoundedAsync(path, cancellationToken);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
        {
            return ([$"{name}: (root) could not be read"], null, null);
        }

        if (bytes is null)
        {
            return ([string.Create(CultureInfo.InvariantCulture, $"{name}: (root) is larger than {RunFileValidator.MaxBytes} bytes")], null, null);
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return ([$"{name}: (root) is not valid UTF-8"], null, null);
        }

        // A file that holds a key the process knows is refused before anything is parsed or rendered: no text of it is printed, and the render never has to be masked.
        if (!string.Equals(redactor.Redact(text), text, StringComparison.Ordinal))
        {
            return ([$"{name}: (root) holds a registered key"], null, null);
        }

        IReadOnlyList<string> failures = RunFileValidator.Check(name, text);
        if (failures.Count > 0)
        {
            return (failures, null, null);
        }

        try
        {
            RunDocument? document = JsonSerializer.Deserialize(text, AgentJoulesJsonContext.Default.RunDocument);

            return document is null ? ([$"{name}: (root) holds no run document"], null, null) : (failures, document, text);
        }
        catch (Exception failure) when (failure is JsonException or ArgumentException)
        {
            // The check read the same text through the same model, so this does not happen; it is a failure of the file if it does, never a crash.
            return ([$"{name}: (model) could not be read"], null, null);
        }
    }

    // At most maxBytes (by default RunFileValidator.MaxBytes) bytes are held; a longer file gives null.
    private static async Task<byte[]?> ReadBoundedAsync(string path, CancellationToken cancellationToken, int maxBytes = RunFileValidator.MaxBytes)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            BufferSize = 0,
        };
        var stream = new FileStream(path, options);
        await using (stream.ConfigureAwait(false))
        {
            // A pipe or a device is not a run file: it may never end or never answer, so it is refused at once (the failure line is the one of a file that could not be read).
            if (!stream.CanSeek)
            {
                throw new IOException("The path is not a regular file.");
            }

            using var held = new MemoryStream();
            byte[] chunk = new byte[CopyBuffer];
            while (true)
            {
                int read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return held.ToArray();
                }

                if (held.Length + read > maxBytes)
                {
                    return null;
                }

                held.Write(chunk, 0, read);
            }
        }
    }

    // ---- the output ----

    private static void WriteCapped(TextWriter writer, string[] lines, Func<int, string> more)
    {
        foreach (string line in lines.Take(MaxListedLines))
        {
            writer.WriteLine(line);
        }

        if (lines.Length > MaxListedLines)
        {
            writer.WriteLine(more(lines.Length - MaxListedLines));
        }
    }

    // All three files are staged (written and flushed under their .partial names) before any target is replaced, so a failure to stage leaves the old set as it was. Only a failure of the moves themselves,
    // which are one replace each, can leave a mixed set; the partials that remain are removed.
    private static void WriteFiles(string folder, (string Name, string Text)[] files)
    {
        try
        {
            _ = Directory.CreateDirectory(folder);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // The message of the operating system holds the full path.
            throw new AgentJoulesException(ExitCode.Unexpected, "report: the output folder could not be created");
        }

        foreach ((string name, string text) in files)
        {
            try
            {
                Stage(Path.Combine(folder, name + PartialSuffix), text);
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                DiscardPartials(folder, files);
                throw new AgentJoulesException(ExitCode.Unexpected, $"report: {name} could not be written");
            }
        }

        foreach ((string name, _) in files)
        {
            try
            {
                File.Move(Path.Combine(folder, name + PartialSuffix), Path.Combine(folder, name), overwrite: true);
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                DiscardPartials(folder, files);
                throw new AgentJoulesException(ExitCode.Unexpected, $"report: {name} could not be written");
            }
        }
    }

    private static void Stage(string partial, string text)
    {
        // A stale partial file (a crashed report) is removed by name, so a link planted there is dropped and never written through.
        File.Delete(partial);
        using var stream = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(Utf8WithoutBom.GetBytes(text));
        stream.Flush(flushToDisk: true);
    }

    private static void DiscardPartials(string folder, (string Name, string Text)[] files)
    {
        foreach ((string name, _) in files)
        {
            try
            {
                File.Delete(Path.Combine(folder, name + PartialSuffix));
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                // Nothing more can be done; the failure that led here is the one reported.
            }
        }
    }

    private static void EnsureNoJekyll(string workingDirectory)
    {
        try
        {
            _ = NoJekyllWriter.EnsureExists(Path.Combine(workingDirectory, DocsFolder));
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            // A folder at the marker path raises UnauthorizedAccessException on Windows; the message holds the full path, so only the file name is reported.
            throw new ConfigurationException($"report: the marker file {NoJekyllWriter.FileName} could not be created in the docs folder");
        }
    }
}