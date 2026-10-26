using System.Globalization;

using AgentJoules.Core.Runs;
using AgentJoules.Reporting.Readme;
using AgentJoules.Specs.Support;
using AgentJoules.TestSupport;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-144: the <c>report</c> flags <c>--update-readme</c>, <c>--verify-readme</c> and <c>--render-content</c> and the content template renderer (REQ-025, REQ-026, REQ-016). Scoped to the feature because sentences such as
/// "the exit code is 2" are reused by other stories with other meanings. The state lives in <see cref="ReportFlagsContext"/>. Every command scenario runs the real command line through the harness in a temporary
/// workspace; the placeholder scenarios run the real renderer on run documents built in memory.
/// </summary>
[Binding]
[Scope(Feature = "AJ-144 report flags and the content template renderer")]
public sealed class ReportFlagsSteps(ReportFlagsContext context)
{
    private const string VerifyArguments = "report --verify-readme";
    private const string UpdateArguments = "report --update-readme";
    private const string BothVariants =
        "## Variant: Short\nLocal {{results.local.totals.netJoules.value|F0}} J, see {{repo.url}}\n\n## Variant: Long\nNvidia {{results.nvidia.totals.inputTokens.value|F0}} tokens, see {{repo.url}}\n";

    private readonly ReportFlagsContext _context = context;

    [Given(@"^a workspace with latest results files for local, nvidia and nebius and a README with markers$")]
    public void GivenAWorkspaceWithThreeTiersAndAReadme()
    {
        _context.AddResults(Tier.Local, Tier.Nvidia, Tier.Nebius);
        _context.WriteReadme();
    }

    [Given(@"^an updated README converted to CRLF line endings$")]
    public async Task GivenAnUpdatedReadmeInCrlf()
    {
        _context.AddResults(Tier.Local, Tier.Nvidia, Tier.Nebius);
        _context.WriteReadme();
        Assert.Equal(0, (await _context.RunAsync(UpdateArguments)).ExitCode);
        _context.ConvertReadmeToCrlf();
        Assert.Contains("\r\n", _context.ReadmeText(), StringComparison.Ordinal);
    }

    [Given(@"^an updated README whose results block has one number edited by hand$")]
    public async Task GivenAnUpdatedReadmeEditedByHand()
    {
        _context.AddResults(Tier.Local, Tier.Nvidia, Tier.Nebius);
        _context.WriteReadme();
        Assert.Equal(0, (await _context.RunAsync(UpdateArguments)).ExitCode);
        _context.EditBlockByHand();
    }

    [Given(@"^a README without the line ""([^""]*)""$")]
    public void GivenAReadmeWithoutTheLine(string line)
    {
        _context.AddResults(Tier.Local);
        _context.WriteReadme(ReportFlagsContext.ReadmeTemplate.Replace(line + "\n", string.Empty, StringComparison.Ordinal));
        Assert.DoesNotContain(line, _context.ReadmeText(), StringComparison.Ordinal);
    }

    [Given(@"^a workspace with the template and latest results files for local and nvidia$")]
    public void GivenAWorkspaceWithTheTemplate()
    {
        _context.AddResults(Tier.Local, Tier.Nvidia);
        _context.WriteTemplate(BothVariants);
    }

    [Given(@"^a template placeholder ""([^""]*)"" that the file lacks$")]
    public void GivenAPlaceholderTheFileLacks(string placeholder)
    {
        _context.AddResults(Tier.Local);
        _context.WriteTemplate("## Variant: One\n" + placeholder + "\n");
    }

    [Given(@"^a template placeholder for cloudgpu and no completed or partial cloudgpu run file$")]
    public void GivenAPlaceholderForCloudGpuWithoutAFile()
    {
        _context.AddResults(Tier.Local);
        _context.WriteTemplate("## Variant: One\n{{results.cloudgpu.totals.netJoules.value}}\n");
    }

    [Given(@"^a template variant whose rendered text is (\d+) characters$")]
    public void GivenAVariantOfLength(int length)
    {
        _context.AddResults(Tier.Local);
        _context.WriteLongVariantTemplate(length);
    }

    [Given(@"^a workspace where docs/report/dashboard\.png exists$")]
    public void GivenAWorkspaceWithTheScreenshot()
    {
        _context.AddResults(Tier.Local, Tier.Nvidia, Tier.Nebius);
        _context.WriteReadme();
        _context.WriteScreenshot();
    }

    [Given(@"^an updated README that links docs/report/dashboard\.png and a workspace where the file is then deleted$")]
    public async Task GivenAnUpdatedReadmeThatLinksTheScreenshot()
    {
        _context.AddResults(Tier.Local, Tier.Nvidia, Tier.Nebius);
        _context.WriteReadme();
        _context.WriteScreenshot();
        Assert.Equal(0, (await _context.RunAsync(UpdateArguments)).ExitCode);
        Assert.Contains("docs/report/dashboard.png", _context.ReadmeText(), StringComparison.Ordinal);
        _context.DeleteScreenshot();
    }

    [Given(@"^a placeholder ""([^""]*)"" and a run file whose first step has net joules 12\.34 labelled measured$")]
    public void GivenAPlaceholderAndAFirstStep(string placeholder)
    {
        string json = RunDocumentFixtures.Edit(
            RunDocumentFixtures.CommittedText(Tier.Local),
            root =>
            {
                var net = root["steps"]![0]!["netJoules"]!.AsObject();
                net["value"] = 12.34;
                net["label"] = "measured";
            });
        _context.UseSource(Tier.Local, json);
        _context.Placeholder = placeholder;
    }

    [Given(@"^a placeholder for the nvidia gross joules whose value is null with label ""([^""]*)""$")]
    public void GivenAPlaceholderForNullGross(string label)
    {
        string json = RunDocumentFixtures.CommittedText(Tier.Nvidia);
        Assert.Contains($"\"label\": \"{label}\"", json, StringComparison.Ordinal);
        _context.UseSource(Tier.Nvidia, json);
        _context.Placeholder = "{{results.nvidia.totals.grossJoules.value}}";
    }

    [When(@"^""report --update-readme"" runs twice and then ""report --verify-readme"" runs$")]
    public async Task WhenUpdateTwiceThenVerify()
    {
        Assert.Equal(0, (await _context.RunAsync(UpdateArguments)).ExitCode);
        _context.SnapshotReadme();
        Assert.Equal(0, (await _context.RunAsync(UpdateArguments)).ExitCode);
        _ = await _context.RunAsync(VerifyArguments);
    }

    [When(@"^""(report [^""]*)"" runs$")]
    public async Task WhenReportRuns(string arguments) => _ = await _context.RunAsync(arguments);

    [When(@"^the placeholder is rendered$")]
    public void WhenThePlaceholderIsRendered() => _context.RenderPlaceholder();

    [Then(@"^the second update changes no byte and verify exits 0$")]
    public void ThenTheSecondUpdateChangesNothing()
    {
        Assert.Equal(_context.Snapshot, _context.ReadmeBytes());
        Assert.DoesNotContain((byte)'\r', _context.ReadmeBytes());
        Assert.Equal(0, _context.Last().ExitCode);
    }

    [Then(@"^the exit code is (\d+)$")]
    public void ThenTheExitCodeIs(int code) => Assert.Equal(code, _context.Last().ExitCode);

    [Then(@"^the exit code is 2 and the message names the marker$")]
    public void ThenExit2NamesTheMarker()
    {
        Assert.Equal(2, _context.Last().ExitCode);
        Assert.Contains(ReadmeResultsBlock.BeginMarker, _context.Last().Stderr, StringComparison.Ordinal);
    }

    [Then(@"^the exit code is 2 and the message names the placeholder$")]
    public void ThenExit2NamesThePlaceholder()
    {
        Assert.Equal(2, _context.Last().ExitCode);

        // The message names the placeholder by its line in the template and its first 16 characters, never more of the template.
        Assert.Contains("at line 2: {{results.local.", _context.Last().Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("joulesPerToken", _context.Last().Stderr, StringComparison.Ordinal);
    }

    [Then(@"^the exit code is 2 and the message names the tier ""([^""]*)""$")]
    public void ThenExit2NamesTheTier(string tier)
    {
        Assert.Equal(2, _context.Last().ExitCode);
        Assert.Contains(tier, _context.Last().Stderr, StringComparison.Ordinal);
    }

    [Then(@"^the exit code is 2 and the message names the variant and its length$")]
    public void ThenExit2NamesTheVariantAndItsLength()
    {
        Assert.Equal(2, _context.Last().ExitCode);
        Assert.Contains("Long", _context.Last().Stderr, StringComparison.Ordinal);
        Assert.Contains(_context.VariantLength.ToString(CultureInfo.InvariantCulture), _context.Last().Stderr, StringComparison.Ordinal);
    }

    [Then(@"^the first line of content/linkedin-posts\.md starts with ""([^""]*)"" and names both run files$")]
    public void ThenTheFirstLineIsTheSourcesLine(string prefix)
    {
        Assert.Equal(0, _context.Last().ExitCode);
        string first = _context.ContentText().Split('\n')[0];

        Assert.StartsWith(prefix, first, StringComparison.Ordinal);
        Assert.Contains(ReportFlagsContext.FileNameOf(Tier.Local), first, StringComparison.Ordinal);
        Assert.Contains(ReportFlagsContext.FileNameOf(Tier.Nvidia), first, StringComparison.Ordinal);
    }

    [Then(@"^""([^""]*)"" occurs 0 times and ""([^""]*)"" occurs once per variant$")]
    public void ThenThePlaceholdersAreResolved(string resolved, string unresolved)
    {
        string text = _context.ContentText();

        Assert.Equal(0, Count(text, resolved));
        Assert.Equal(2, Count(text, "## Variant:"));
        Assert.Equal(2, Count(text, unresolved));
        Assert.DoesNotContain('\r', text);
    }

    [Then(@"^the output reads ""N/A"" and never ""0"", ""null"" or an empty string$")]
    public void ThenTheOutputIsTheLabel()
    {
        Assert.Equal("N/A", _context.Rendered);
        Assert.NotEqual("0", _context.Rendered);
        Assert.NotEqual("null", _context.Rendered);
        Assert.NotEqual(string.Empty, _context.Rendered);
    }

    [Then(@"^the output reads ""([^""]*)""$")]
    public void ThenTheOutputReads(string expected) => Assert.Equal(expected, _context.Rendered);

    [Then(@"^the results block contains the link to docs/report/dashboard\.png and ""report --verify-readme"" exits 0$")]
    public async Task ThenTheBlockLinksTheScreenshot()
    {
        Assert.Equal(0, _context.Last().ExitCode);
        Assert.Contains("](docs/report/dashboard.png)", _context.ReadmeText(), StringComparison.Ordinal);
        Assert.Equal(0, (await _context.RunAsync(VerifyArguments)).ExitCode);
    }

    private static int Count(string text, string part)
    {
        int count = 0;
        int at = 0;
        while ((at = text.IndexOf(part, at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += part.Length;
        }

        return count;
    }
}