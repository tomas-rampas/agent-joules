using System.Text.Json.Nodes;

using AgentJoules.Core.Runs;
using AgentJoules.Reporting.Results;
using AgentJoules.TestSupport;
using AgentJoules.TestSupport.Docs;
using AgentJoules.TestSupport.Evidence;

using Xunit;

namespace AgentJoules.UnitTests;

// One decoy fact sets GIT_DIR and its siblings process-wide, so this class runs alone (no other test calls git meanwhile).
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GitEnvironmentGroup
{
    public const string Name = "K2 git environment variables";
}

/// <summary>
/// K2 (M4 peer review m-2 and n-3, REQ-022): the evidence rules close their edges. A cited bare probe or calibration name is the same citation as a bare run-file name, and the newest local run file the methodology
/// checks read skips the files that <c>RunFileSelector.Latest</c> skips.
/// </summary>
[Collection(GitEnvironmentGroup.Name)]
[Trait("Spec", "REQ-022")]
public sealed class EvidenceSourceTests
{
    private const string UncommittedName = "20261026T000000Z-local.json";

    private static string Document() => File.ReadAllText(MethodologyDocumentParser.CommittedPath());

    private static IReadOnlyList<string> Evidence(string document, IReadOnlyCollection<string> committed, Func<string, string?>? readFile = null)
    {
        string root = ConfigurationFixtures.RepositoryRoot();
        return MethodologyDocumentParser.CheckEvidence(
            document,
            committed,
            readFile ?? (relative =>
            {
                string full = Path.Combine(root, relative);
                return File.Exists(full) ? File.ReadAllText(full) : null;
            }),
            MethodologyDocumentParser.TaskCaps());
    }

    [Theory]
    [InlineData("probe-20261031T010101Z.json")]
    [InlineData("probe-20261031T010101Z-attempt2.json")]
    [InlineData("calibration-20261031T010101Z.json")]
    [InlineData("calibration-20261031T010101Z-attempt2.json")]
    public void A_bare_probe_or_calibration_name_is_the_same_citation_as_a_results_prefixed_one(string bare)
    {
        string document = Document() + "\nThe file " + bare + " was also looked at.\n";

        Assert.Contains(Evidence(document, LaptopResults.CommittedResultsFiles()), failure => failure.Contains("results/" + bare, StringComparison.Ordinal) && failure.Contains("not a committed file", StringComparison.Ordinal));
    }

    [Fact]
    public void A_newest_local_run_file_with_the_non_publishable_flag_is_not_the_latest()
    {
        string root = ConfigurationFixtures.RepositoryRoot();
        string[] committed = [.. LaptopResults.CommittedResultsFiles(), "results/" + UncommittedName];
        string? Read(string relative) => relative.EndsWith(UncommittedName, StringComparison.Ordinal)
            ? """{"run":{"tier":"local","status":"completed","flags":["non-publishable"]}}"""
            : (File.Exists(Path.Combine(root, relative)) ? File.ReadAllText(Path.Combine(root, relative)) : null);

        Assert.DoesNotContain(Evidence(Document(), committed, Read), static failure => failure.Contains(UncommittedName, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("completed", "", true)]
    [InlineData("partial", "", true)]
    [InlineData("running", "", false)]
    [InlineData("failed", "", false)]
    [InlineData("incomplete", "", false)]
    [InlineData("completed", "\"non-publishable\"", false)]
    [InlineData("partial", "\"shared-window\",\"non-publishable\"", false)]
    public void The_methodology_pick_skips_exactly_what_RunFileSelector_skips(string status, string flags, bool selectable)
    {
        string json = "{\"run\":{\"tier\":\"local\",\"status\":\"" + status + "\",\"flags\":[" + flags + "]}}";
        using var workspace = TempWorkspace.CreateEmpty();
        _ = workspace.WriteFile(Path.Combine("results", "20261025T000000Z-local.json"), json);

        string? selected = RunFileSelector.Latest(Path.Combine(workspace.Root, "results"), Tier.Local);

        Assert.Equal(selectable, selected is not null);
        Assert.Equal(selectable, MethodologyDocumentParser.IsSelectableRunFile(json));
    }

    [GitFact]
    public void Only_a_file_in_the_HEAD_tree_is_a_committed_name_a_staged_and_an_untracked_file_are_not()
    {
        using var workspace = TempWorkspace.CreateEmpty();
        string run = File.ReadAllText(Path.Combine(ConfigurationFixtures.RepositoryRoot(), LaptopResults.CommittedResultsFiles().First(static name => name.EndsWith("-local.json", StringComparison.Ordinal))));
        _ = workspace.WriteFile(Path.Combine("results", "20261025T000000Z-local.json"), run);
        InitScratch(workspace.Root);
        Git(workspace.Root, "add", "results/20261025T000000Z-local.json");
        Commit(workspace.Root);
        _ = workspace.WriteFile(Path.Combine("results", "20261025T000001Z-local.json"), run);
        Git(workspace.Root, "add", "results/20261025T000001Z-local.json");
        // The untracked newer file has repeat 1 and 12 steps: were it the pick, the shape rule would fail on it.
        JsonNode oneRepeat = JsonNode.Parse(run)!;
        oneRepeat["run"]!["repeat"] = 1;
        oneRepeat["steps"] = new JsonArray([.. oneRepeat["steps"]!.AsArray().Take(12).Select(static step => step!.DeepClone())]);
        _ = workspace.WriteFile(Path.Combine("results", "20261025T000002Z-local.json"), oneRepeat.ToJsonString());

        IReadOnlyList<string> committed = LaptopResults.CommittedResultsFiles(workspace.Root);

        Assert.Equal(["results/20261025T000000Z-local.json"], committed);
        var reader = new CommittedEvidenceReader(workspace.Root, committed);
        Assert.Equal(Path.Combine(workspace.Root, "results", "20261025T000000Z-local.json"), reader.LatestLocalRunFile());
        string[] files = [.. LaptopCampaign.CommittedRunFiles(null, workspace.Root).Select(static path => Path.GetFileName(path))];
        Assert.Equal(["20261025T000000Z-local.json"], files);
        Assert.Equal(Path.Combine(workspace.Root, "results", "20261025T000000Z-local.json"), LaptopCampaign.LatestCommittedRunFile(Tier.Local, workspace.Root));
        Assert.Null(LaptopCampaign.LatestCommittedRunFile(Tier.Nvidia, workspace.Root));
    }

    [GitFact]
    public void A_committed_local_run_file_the_selector_rejects_is_named_in_an_InvalidDataException()
    {
        using var workspace = TempWorkspace.CreateEmpty();
        _ = workspace.WriteFile(Path.Combine("results", "20261025T000000Z-local.json"), "{\"run\":{\"tier\":\"local\",");
        InitScratch(workspace.Root);
        Git(workspace.Root, "add", "results/20261025T000000Z-local.json");
        Commit(workspace.Root);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => LaptopCampaign.LatestCommittedRunFile(Tier.Local, workspace.Root));

        Assert.StartsWith("20261025T000000Z-local.json: ", exception.Message, StringComparison.Ordinal);
    }

    [GitFact]
    public void The_git_calls_ignore_an_inherited_GIT_DIR_and_never_commit_into_another_repository()
    {
        using var decoy = TempWorkspace.CreateEmpty();
        using var scratch = TempWorkspace.CreateEmpty();
        _ = decoy.WriteFile("decoy.txt", "decoy");
        InitScratch(decoy.Root);
        Git(decoy.Root, "add", "decoy.txt");
        Commit(decoy.Root);
        string before = Git(decoy.Root, "rev-list", "--count", "--all").Trim();
        _ = scratch.WriteFile(Path.Combine("results", "20261025T000000Z-local.json"), "{}");
        string[] names = ["GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_OBJECT_DIRECTORY"];
        string?[] saved = [.. names.Select(static name => Environment.GetEnvironmentVariable(name))];
        try
        {
            Environment.SetEnvironmentVariable("GIT_DIR", Path.Combine(decoy.Root, ".git"));
            Environment.SetEnvironmentVariable("GIT_WORK_TREE", decoy.Root);
            Environment.SetEnvironmentVariable("GIT_INDEX_FILE", Path.Combine(decoy.Root, ".git", "index"));
            Environment.SetEnvironmentVariable("GIT_OBJECT_DIRECTORY", Path.Combine(decoy.Root, ".git", "objects"));

            InitScratch(scratch.Root);
            Git(scratch.Root, "add", "results/20261025T000000Z-local.json");
            Commit(scratch.Root);

            Assert.Equal(["results/20261025T000000Z-local.json"], LaptopResults.CommittedResultsFiles(scratch.Root));
        }
        finally
        {
            for (int index = 0; index < names.Length; index++)
            {
                Environment.SetEnvironmentVariable(names[index], saved[index]);
            }
        }

        Assert.Equal(before, Git(decoy.Root, "rev-list", "--count", "--all").Trim());
        Assert.DoesNotContain("results/", Git(decoy.Root, "ls-tree", "-r", "--name-only", "HEAD"), StringComparison.Ordinal);
    }

    private static void InitScratch(string root) => Assert.Equal(0, GitRunner.Run(root, "init", "--quiet", root).ExitCode);

    // The scratch commit loads no hook and signs nothing; the identity is passed inline so no global config is needed.
    private static void Commit(string root) =>
        Git(root, "-c", "user.name=k2", "-c", "user.email=k2@example.invalid", "-c", "commit.gpgsign=false", "-c", "core.hooksPath=" + Path.Combine(root, "no-hooks"), "commit", "--quiet", "--no-verify", "-m", "scratch");

    private static string Git(string root, params string[] arguments)
    {
        GitResult result = GitRunner.Run(root, ["-C", root, .. arguments]);
        Assert.True(result.ExitCode == 0, $"git {arguments[0]} exited with {result.ExitCode}: {result.Stderr}{result.Stdout}");
        return result.Stdout;
    }
}