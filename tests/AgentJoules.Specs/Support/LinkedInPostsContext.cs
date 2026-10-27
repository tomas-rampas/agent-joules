using System.Text;

using AgentJoules.Cli;
using AgentJoules.Core.Runs;
using AgentJoules.TestSupport;

using Xunit.Sdk;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The state of one AJ-134 scenario (the LinkedIn post drafts, REQ-026), handed to the step class by Reqnroll context injection. The scenarios that render lay out a <see cref="TempWorkspace"/> that is the working
/// directory of the run: the committed run files of <c>results/</c> (copied, never changed) or the in-memory fixtures, and a template; the real <c>report --render-content</c> runs through the harness. The template
/// scenarios are pure checks over a text. The repository is never written.
/// </summary>
public sealed class LinkedInPostsContext : IDisposable
{
    /// <summary>The path of the template, relative to the repository root and to the workspace.</summary>
    public const string TemplatePath = "content/linkedin-posts.template.md";

    /// <summary>The path of the rendered file, relative to the workspace.</summary>
    public const string ContentPath = "content/linkedin-posts.md";

    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    private TempWorkspace? _workspace;

    /// <summary>The template under test, possibly a mutated copy of the committed one.</summary>
    public string Template { get; set; } = string.Empty;

    /// <summary>The failures of the last check.</summary>
    public IReadOnlyList<string> Failures { get; set; } = [];

    /// <summary>The result of the last command, or null.</summary>
    public CliRunResult? Result { get; private set; }

    /// <summary>The length of the variant of <see cref="WriteLongVariantTemplate"/>.</summary>
    public int VariantLength { get; private set; }

    /// <summary>The rendered body of the placeholder scenario, or null.</summary>
    public string? Rendered { get; set; }

    private TempWorkspace Workspace => _workspace ??= TempWorkspace.CreateEmpty();

    /// <summary>Copies the committed tier run files of <c>results/</c> into the workspace.</summary>
    public void AddCommittedRunFiles()
    {
        foreach ((string name, string text) in RepositoryFiles.RunFiles())
        {
            _ = Workspace.WriteFile(Path.Combine("results", name), text);
        }
    }

    /// <summary>Writes the fixture run file of each tier into the workspace.</summary>
    /// <param name="tiers">The tiers: local, nvidia or nebius.</param>
    public void AddFixtureRunFiles(params Tier[] tiers) => ReadmeFixtures.AddCommitted(Workspace, tiers);

    /// <summary>Writes the template into the workspace.</summary>
    /// <param name="text">The template text.</param>
    public void WriteTemplate(string text) => _ = Workspace.WriteFile(TemplatePath, text);

    /// <summary>Writes a template of one variant named <c>Long</c> whose body holds exactly <paramref name="length"/> characters.</summary>
    /// <param name="length">The length.</param>
    public void WriteLongVariantTemplate(int length)
    {
        VariantLength = length;
        WriteTemplate("## Variant: Long\n" + new string('x', length) + "\n");
    }

    /// <summary>Runs the real command line in the workspace; <paramref name="arguments"/> are split on spaces.</summary>
    /// <param name="arguments">The arguments after the program name.</param>
    /// <returns>A task that completes with the result.</returns>
    public async Task<CliRunResult> RunAsync(string arguments)
    {
        Result = await CliHarness.RunTokensAsync(arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries), new CliHostOverrides(WorkingDirectory: Workspace.Root));

        return Result;
    }

    /// <summary>The result of the last command.</summary>
    /// <returns>The result.</returns>
    /// <exception cref="XunitException">No command has run.</exception>
    public CliRunResult Last() => Result ?? throw new XunitException("The command has not run yet.");

    /// <summary>The rendered file as it is in the workspace.</summary>
    /// <returns>The text.</returns>
    public string ContentText() => File.ReadAllText(Path.Combine(Workspace.Root, ContentPath), Utf8WithoutBom);

    /// <summary>The bytes of the rendered file as it is in the workspace.</summary>
    /// <returns>The bytes.</returns>
    public byte[] ContentBytes() => File.ReadAllBytes(Path.Combine(Workspace.Root, ContentPath));

    /// <summary>Whether a run file of that name is in the workspace.</summary>
    /// <param name="name">The file name.</param>
    /// <returns>True when it exists.</returns>
    public bool RunFileExists(string name) => File.Exists(Path.Combine(Workspace.Root, "results", name));

    /// <inheritdoc />
    public void Dispose() => _workspace?.Dispose();
}