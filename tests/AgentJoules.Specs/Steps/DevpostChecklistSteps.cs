using AgentJoules.Specs.Support;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-138: the Devpost checklist rules and fields (REQ-028). Scoped to the feature because sentences such as "it fails with" and "the checklist check runs" belong to several documentation stories. The Given steps read
/// the committed checklist through <see cref="DevpostCommitted"/> and the negative ones mutate the text in memory; the checks are the pure functions of <see cref="DocumentChecks"/>. The cloudgpu state of a scenario is the
/// committed one unless the scenario models a workspace without a run file.
/// </summary>
[Binding]
[Scope(Feature = "AJ-138 Devpost checklist rules and fields")]
public sealed class DevpostChecklistSteps(DevpostContext context)
{
    private readonly DevpostContext _context = context;

    [Given(@"^the committed docs/devpost-checklist\.md$")]
    [Given(@"^the committed checklist$")]
    [Given(@"^the Fields to artifacts checkbox list$")]
    public void GivenTheCommittedChecklist()
    {
        _context.Text = DevpostCommitted.Checklist();
        _context.CloudGpuRan = DevpostCommitted.CloudGpuRan();
    }

    [Given(@"^a copy of the checklist without the line ""([^""]*)""$")]
    public void GivenACopyWithoutTheLine(string label)
    {
        GivenTheCommittedChecklist();
        _context.Text = string.Join('\n', DocumentChecks.Lines(_context.Text).Where(line => !line.TrimStart().StartsWith(label, StringComparison.Ordinal)));
        Assert.DoesNotContain(DocumentChecks.Lines(_context.Text), line => line.TrimStart().StartsWith(label, StringComparison.Ordinal));
    }

    [Given(@"^a copy of the checklist whose first conflict has no resolution$")]
    public void GivenACopyWithAConflictWithoutAResolution()
    {
        GivenTheCommittedChecklist();
        string[] lines = DocumentChecks.Lines(_context.Text);
        int at = Array.FindIndex(lines, static line => line.StartsWith("1. ", StringComparison.Ordinal));
        Assert.True(at >= 0, "The checklist has no numbered conflict.");
        int cut = lines[at].IndexOf("Resolution", StringComparison.Ordinal);
        Assert.True(cut > 0, "The first conflict has no Resolution word.");
        lines[at] = lines[at][..cut].TrimEnd();
        _context.Text = string.Join('\n', lines);
    }

    [Given(@"^a copy of the checklist mapping ""([^""]*)"" to ""([^""]*)""$")]
    public void GivenACopyMappingAField(string field, string artifact)
    {
        GivenTheCommittedChecklist();
        string[] lines = DocumentChecks.Lines(_context.Text);
        int at = Array.FindIndex(lines, line => line.StartsWith("- [", StringComparison.Ordinal) && line.Contains(field, StringComparison.OrdinalIgnoreCase) && line.Contains(" -> ", StringComparison.Ordinal));
        Assert.True(at >= 0, "The checklist has no row for " + field);
        lines[at] = lines[at][..(lines[at].IndexOf(" -> ", StringComparison.Ordinal) + 4)] + artifact;
        _context.Text = string.Join('\n', lines);
    }

    [Given(@"^a copy of the checklist whose Built-with list has (\d+) tags$")]
    public void GivenACopyWithTags(int count)
    {
        GivenTheCommittedChecklist();
        _context.Text = DocumentChecks.WithTagCount(_context.Text, count);
        Assert.Equal(count, DocumentChecks.BuiltWithTags(_context.Text).Count);
    }

    [Given(@"^a copy of the checklist with the line ""([^""]*)""$")]
    public void GivenACopyWithARuleLine(string line)
    {
        GivenTheCommittedChecklist();
        IReadOnlyList<string> body = DocumentChecks.SectionAtAnyLevel(_context.Text, "Rules");
        Assert.NotEmpty(body);
        _context.Text = DocumentChecks.WithSectionBody(_context.Text, "Rules", [.. body, line]);
    }

    // The workspace holds no run file of the cloudgpu tier: the rule is asked of an empty list of files, as AJ-141 does.
    [Given(@"^a workspace with no completed or partial cloudgpu run file and a Built-with list containing ""([^""]*)""$")]
    public void GivenAWorkspaceWithoutACloudGpuFile(string tag)
    {
        _context.Text = DocumentChecks.WithTag(DevpostCommitted.Checklist(), tag);
        _context.CloudGpuRan = DocumentChecks.CloudGpuRan([], static _ => string.Empty);
        Assert.False(_context.CloudGpuRan);
        Assert.Contains(tag, DocumentChecks.BuiltWithTags(_context.Text));
    }

    [When(@"^the Rules section is parsed$")]
    public void WhenTheRulesSectionIsParsed() => _context.Failures = DocumentChecks.ChecklistRules(_context.Text);

    [When(@"^each artifact path is checked$")]
    public void WhenEachArtifactPathIsChecked() => _context.Failures = DocumentChecks.ChecklistFields(_context.Text, RepositoryFiles.Exists);

    [When(@"^the line starting ""Demo URL:"" is read$")]
    public void WhenTheDemoUrlLineIsRead() => _context.Failures = DocumentChecks.ChecklistDemoUrl(_context.Text);

    [When(@"^the deadline line is read$")]
    public void WhenTheDeadlineLineIsRead() => _context.Failures = DocumentChecks.ChecklistDeadline(_context.Text);

    [When(@"^the source, conflicts and Built-with sections are checked$")]
    public void WhenTheSectionsAreChecked() =>
        _context.Failures = [.. DocumentChecks.ChecklistSource(_context.Text), .. DocumentChecks.ChecklistConflicts(_context.Text), .. DocumentChecks.ChecklistBuiltWith(_context.Text, _context.CloudGpuRan)];

    [When(@"^the checklist check runs$")]
    public void WhenTheChecklistCheckRuns() => _context.Failures = DevpostCommitted.ChecklistFailures(_context.Text, _context.CloudGpuRan);

    [Then(@"^it has 13 items and each ends with an evidence class$")]
    public void ThenItHas13Items()
    {
        Assert.Empty(_context.Failures);
        Assert.True(DocumentChecks.CountOf(_context.Text, "(evidence: ") >= DocumentChecks.RuleItemCount, "fewer than 13 evidence classes");
    }

    [Then(@"^every path exists or is marked ""\(user\)""$")]
    public void ThenEveryPathExists()
    {
        Assert.DoesNotContain(_context.Failures, static failure => failure.StartsWith("artifact not found", StringComparison.Ordinal));
        Assert.DoesNotContain(_context.Failures, static failure => failure.StartsWith("missing field", StringComparison.Ordinal));
    }

    [Then(@"^the About the project row names ""([^""]*)""$")]
    public void ThenTheAboutRowNames(string path)
    {
        Assert.Empty(_context.Failures);
        Assert.Contains(DocumentChecks.Lines(_context.Text), line => line.StartsWith("- [", StringComparison.Ordinal) && line.Contains("About the project", StringComparison.OrdinalIgnoreCase) && line.Contains(path, StringComparison.Ordinal));
    }

    [Then(@"^it names the GitHub Pages report page and gives a reason$")]
    public void ThenItNamesThePage()
    {
        Assert.Empty(_context.Failures);
        Assert.Contains(DocumentChecks.Lines(_context.Text), static line => line.StartsWith(DocumentChecks.DemoUrlLabel, StringComparison.Ordinal) && line.Contains(DocumentChecks.ReportPageUrl, StringComparison.Ordinal));
    }

    [Then(@"^it contains ""([^""]*)""$")]
    public void ThenItContains(string text)
    {
        Assert.Empty(_context.Failures);
        Assert.Contains(text, _context.Text, StringComparison.Ordinal);
    }

    [Then(@"^every one passes and the Tags line equals the count$")]
    public void ThenEveryOnePasses()
    {
        Assert.Empty(_context.Failures);
        Assert.Contains($"Tags: {DocumentChecks.BuiltWithTags(_context.Text).Count}", DocumentChecks.Lines(_context.Text));
    }

    [Then(@"^it fails with ""([^""]*)""$")]
    public void ThenItFailsWith(string failure) =>
        Assert.True(_context.Failures.Any(entry => entry.Contains(failure, StringComparison.Ordinal)), "failures: " + string.Join("; ", _context.Failures));
}