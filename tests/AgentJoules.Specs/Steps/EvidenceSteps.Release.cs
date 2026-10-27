using AgentJoules.Specs.Support;
using AgentJoules.TestSupport;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-157: the release notes, the three zip assets, the git index and the release command of the checklist. Scoped to the story tag because sentences such as "it fails with" and "it is parsed" belong to several
/// documentation stories. The positive scenarios read the committed tree; each negative scenario lays a fixture out in a temporary workspace with <see cref="SubmissionFixtures.WritePackage"/> and one defect. The
/// key-shaped value of the zip scenario is built at run time and is never printed: the violation names the zip only.
/// </summary>
[Binding]
[Scope(Tag = "AJ-S157")]
public sealed class ReleasePackageSteps(SubmissionContext context, IReqnrollOutputHelper output)
{
    private const string ReleaseZip = "agent-joules-v0.1.0-";
    private const string ZipsAbsentReason = "artifacts/ holds none of the three release zips (the folder is git-ignored, so a clean checkout has none); the zip contents and the existence of the command's assets are not asserted";

    private readonly SubmissionContext _context = context;
    private readonly IReqnrollOutputHelper _output = output;

    private void AssertNoViolations() =>
        Assert.True(_context.Violations.Count == 0, string.Join("\n", _context.Violations));

    private TempWorkspace PackageFixture()
    {
        TempWorkspace workspace = _context.NewFixture();
        SubmissionFixtures.WritePackage(workspace);
        _context.Check = () => EvidenceChecks.ReleasePackageViolations(_context.Root);
        return workspace;
    }

    [Given(@"^the committed docs/release-notes-v0\.1\.0\.md$")]
    [Given(@"^the built artifacts folder$")]
    [Given(@"^the committed checklist and the artifacts folder$")]
    public void GivenTheCommittedTree() => _context.UseRepository();

    [When(@"^it is parsed$")]
    public void WhenTheNotesAreParsed() => _context.Result = EvidenceChecks.ReleaseNotesViolations(_context.Root);

    [Then(@"^it has the eight headings and the sentence ""Energy is GPU-board energy only\.""$")]
    public void ThenTheNotesAreComplete() => AssertNoViolations();

    // The artifacts folder is git-ignored, so a clean checkout (the CI runners) holds none of the three zips: the scenario then passes without asserting their contents and says so.
    private bool ZipsAbsent()
    {
        if (EvidenceChecks.AnyReleaseAssetExists(_context.Root))
        {
            return false;
        }

        _output.WriteLine("passed without asserting: " + ZipsAbsentReason);
        return true;
    }

    [When(@"^the zip listings are read$")]
    public void WhenTheZipsAreRead() =>
        _context.Result = ZipsAbsent()
            ? EvidenceChecks.TrackedArtifactViolations(_context.Root)
            : [.. EvidenceChecks.ReleaseZipViolations(_context.Root), .. EvidenceChecks.TrackedArtifactViolations(_context.Root)];

    [Then(@"^the fdd zip lists AgentJoules\.Cli\.dll, the results zip counts every results json and the report zip lists index\.html$")]
    public void ThenTheZipsHaveTheirContents() => AssertNoViolations();

    [When(@"^the paths in the ""gh release create"" block are checked$")]
    public void WhenTheCommandIsChecked() => _context.Result = EvidenceChecks.ReleaseCommandViolations(_context.Root, requireAssets: !ZipsAbsent());

    [Then(@"^every path exists and the notes path equals docs/release-notes-v0\.1\.0\.md$")]
    public void ThenTheCommandMatchesTheFiles() => AssertNoViolations();

    [Given(@"^a results zip fixture containing a runtime-built ""nvapi-"" plus 16 characters value$")]
    public void GivenAZipWithKeyMaterial()
    {
        TempWorkspace workspace = PackageFixture();
        SubmissionFixtures.WriteZip(workspace.Root, "results", ("results/20261025T131930Z-local.json", "{\"note\":\"" + SubmissionFixtures.KeyShapedValue() + "\"}"));
    }

    [Given(@"^a repository where ""artifacts/agent-joules-v0\.1\.0-fdd\.zip"" is tracked$")]
    public void GivenATrackedAsset()
    {
        TempWorkspace workspace = PackageFixture();
        SubmissionFixtures.RunGit(workspace.Root, "init", "--quiet");
        SubmissionFixtures.RunGit(workspace.Root, "add", "-f", "artifacts/" + ReleaseZip + "fdd.zip");
    }

    [Given(@"^release notes containing ""([^""]+)""$")]
    public void GivenNotesWithAClaim(string sentence)
    {
        TempWorkspace workspace = PackageFixture();
        _ = workspace.WriteFile("docs/release-notes-v0.1.0.md", SubmissionFixtures.ReleaseNotes() + "\n" + sentence + "\n");
    }

    [Given(@"^a checklist command naming ""(artifacts/[^""]+)"" that does not exist$")]
    public void GivenAMissingAsset(string asset)
    {
        TempWorkspace workspace = PackageFixture();
        File.Delete(Path.Combine(workspace.Root, asset.Replace('/', Path.DirectorySeparatorChar)));
    }

    [Given(@"^release notes mentioning cloudgpu figures and no cloudgpu run file$")]
    public void GivenNotesWithCloudGpuFigures()
    {
        TempWorkspace workspace = PackageFixture();
        _ = workspace.WriteFile("docs/release-notes-v0.1.0.md", SubmissionFixtures.ReleaseNotes() + "\nThe cloudgpu run measured 120 J, see results/20261026T124721Z-cloudgpu.json.\n");
    }

    [When(@"^the package check runs$")]
    public void WhenThePackageCheckRuns() => _context.RunCheck();

    [Then(@"^it fails with ""([^""]*)""$")]
    public void ThenItFailsWith(string failure) => Assert.Contains(failure, _context.Violations);
}