using System.Text;

using AgentJoules.Cli;
using AgentJoules.Core.Runs;
using AgentJoules.Reporting.Content;
using AgentJoules.Reporting.Readme;
using AgentJoules.TestSupport;

using Xunit.Sdk;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The state of one AJ-144 scenario (the <c>report</c> flags <c>--update-readme</c>, <c>--verify-readme</c> and <c>--render-content</c>, REQ-025, REQ-026 and REQ-016), handed to the step class by Reqnroll context injection.
/// The Given steps lay out a <see cref="TempWorkspace"/> that is the working directory of the run: run files from the in-memory fixtures in <c>results</c>, a README with the two marker lines, a template and, when asked,
/// the dashboard screenshot; the When steps run the real command line through the harness (or the real renderer on a placeholder); the Then steps read the exit code, the two streams and the files. There is no HTTP,
/// no model and no GPU. The repository is never written.
/// </summary>
public sealed class ReportFlagsContext : IDisposable
{
    /// <summary>The path of the README in the workspace.</summary>
    public const string ReadmeName = "README.md";

    /// <summary>The path of the rendered content file, relative to the workspace.</summary>
    public const string ContentPath = "content/linkedin-posts.md";

    /// <summary>The path of the template, relative to the workspace.</summary>
    public const string TemplatePath = "content/linkedin-posts.template.md";

    /// <summary>The path of the dashboard screenshot, relative to the workspace.</summary>
    public const string ScreenshotPath = "docs/report/dashboard.png";

    /// <summary>The README of a scenario: prose, the two marker lines around a stale block, prose.</summary>
    public const string ReadmeTemplate = "# Agent Joules\n\nIntro text.\n\n<!-- results:begin -->\nstale block\n<!-- results:end -->\n\nFooter text.\n";

    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly List<CliRunResult> _results = [];
    private readonly Dictionary<Tier, ContentSource> _sources = [];

    private TempWorkspace? _workspace;

    /// <summary>The README bytes saved by <see cref="SnapshotReadme"/>, or null.</summary>
    public byte[]? Snapshot { get; private set; }

    /// <summary>The body of the variant the renderer returned for a placeholder, or null.</summary>
    public string? Rendered { get; private set; }

    /// <summary>The placeholder of the scenario, or null.</summary>
    public string? Placeholder { get; set; }

    /// <summary>The number of characters of the variant body of <see cref="WriteLongVariantTemplate"/>.</summary>
    public int VariantLength { get; private set; }

    private TempWorkspace Workspace => _workspace ??= TempWorkspace.CreateEmpty();

    private string Root => Workspace.Root;

    /// <summary>Writes the committed fixture run file of each tier into <c>results</c>.</summary>
    /// <param name="tiers">The tiers: local, nvidia or nebius.</param>
    public void AddResults(params Tier[] tiers)
    {
        foreach (Tier tier in tiers)
        {
            _ = Workspace.WriteFile(Path.Combine("results", RunDocumentFixtures.CommittedFileName(tier)), RunDocumentFixtures.CommittedText(tier));
        }
    }

    /// <summary>The file name of the fixture run file of a tier.</summary>
    /// <param name="tier">The tier.</param>
    /// <returns>The name.</returns>
    public static string FileNameOf(Tier tier) => RunDocumentFixtures.CommittedFileName(tier);

    /// <summary>Writes the README with the two marker lines.</summary>
    public void WriteReadme() => WriteReadme(ReadmeTemplate);

    /// <summary>Writes the README.</summary>
    /// <param name="text">The text of the file.</param>
    public void WriteReadme(string text) => _ = Workspace.WriteFile(ReadmeName, text);

    /// <summary>Writes the dashboard screenshot file (a few bytes; the command only asks whether it exists).</summary>
    public void WriteScreenshot() => _ = Workspace.WriteFile(ScreenshotPath, "png");

    /// <summary>Deletes the dashboard screenshot file.</summary>
    public void DeleteScreenshot() => File.Delete(Path.Combine(Root, ScreenshotPath));

    /// <summary>Writes the template.</summary>
    /// <param name="text">The text of the file.</param>
    public void WriteTemplate(string text) => _ = Workspace.WriteFile(TemplatePath, text);

    /// <summary>Writes a template of one variant named <c>Long</c> whose body (the text below the heading) holds exactly <paramref name="length"/> characters.</summary>
    /// <param name="length">The length of the rendered variant.</param>
    public void WriteLongVariantTemplate(int length)
    {
        VariantLength = length;
        WriteTemplate("## Variant: Long\n" + new string('x', length) + "\n");
    }

    /// <summary>The README as it is on disk.</summary>
    /// <returns>The bytes.</returns>
    public byte[] ReadmeBytes() => File.ReadAllBytes(Path.Combine(Root, ReadmeName));

    /// <summary>The README as it is on disk.</summary>
    /// <returns>The text.</returns>
    public string ReadmeText() => Utf8WithoutBom.GetString(ReadmeBytes());

    /// <summary>Remembers the README bytes now.</summary>
    public void SnapshotReadme() => Snapshot = ReadmeBytes();

    /// <summary>Writes the README again with CRLF line endings.</summary>
    public void ConvertReadmeToCrlf() => File.WriteAllBytes(Path.Combine(Root, ReadmeName), Utf8WithoutBom.GetBytes(ReadmeText().Replace("\n", "\r\n", StringComparison.Ordinal)));

    /// <summary>The content file as it is on disk.</summary>
    /// <returns>The text.</returns>
    public string ContentText() => File.ReadAllText(Path.Combine(Root, ContentPath), Utf8WithoutBom);

    /// <summary>Runs the real command line in the workspace; <paramref name="arguments"/> are split on spaces.</summary>
    /// <param name="arguments">The arguments after the program name, for example <c>report --update-readme</c>.</param>
    /// <returns>A task that completes with the result, which is also kept for <see cref="Last"/>.</returns>
    public async Task<CliRunResult> RunAsync(string arguments)
    {
        CliRunResult result = await CliHarness.RunTokensAsync(arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries), new CliHostOverrides(WorkingDirectory: Workspace.Root));
        _results.Add(result);
        return result;
    }

    /// <summary>The last run.</summary>
    /// <returns>The result.</returns>
    /// <exception cref="XunitException">No command has run.</exception>
    public CliRunResult Last() => _results.Count > 0 ? _results[^1] : throw new XunitException("The command has not run yet.");

    /// <summary>Replaces the first digit of the results block of the README with another digit, the edit of a hand.</summary>
    public void EditBlockByHand()
    {
        string text = ReadmeText();
        int from = text.IndexOf(ReadmeResultsBlock.BeginMarker, StringComparison.Ordinal) + ReadmeResultsBlock.BeginMarker.Length;
        int digit = text.AsSpan(from).IndexOfAnyInRange('0', '9') + from;
        char replacement = text[digit] == '7' ? '3' : '7';
        _ = Workspace.WriteFile(ReadmeName, string.Concat(text.AsSpan(0, digit), replacement.ToString(), text.AsSpan(digit + 1)));
    }

    /// <summary>Keeps a run file text as the source of a tier for <see cref="RenderPlaceholder"/>.</summary>
    /// <param name="tier">The tier.</param>
    /// <param name="json">The text of the run file.</param>
    public void UseSource(Tier tier, string json) => _sources[tier] = new ContentSource(FileNameOf(tier), json);

    /// <summary>Renders the placeholder as the body of one variant through the real renderer.</summary>
    public void RenderPlaceholder()
    {
        string output = ContentTemplateRenderer.Render("## Variant: One\n" + (Placeholder ?? throw new XunitException("No placeholder was given.")) + "\n", _sources);
        string[] lines = output.Split('\n');
        Rendered = lines[Array.FindIndex(lines, static line => line.StartsWith("## Variant:", StringComparison.Ordinal)) + 1];
    }

    /// <inheritdoc />
    public void Dispose() => _workspace?.Dispose();
}