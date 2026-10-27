using AgentJoules.TestSupport;

using Xunit;

namespace AgentJoules.Specs.Support;

/// <summary>
/// One row for each additive rule of the AJ-156 part of <see cref="EvidenceChecks"/> that no scenario of the story exercises, and the ordering cases (the last line of a verdict, the label before a fenced block).
/// The temporary repositories hold empty commits made without a hook and a bare remote inside the temporary folder; no row sends anything to a remote or reads the network. The rows named Live read the committed repository and change nothing.
/// </summary>
public sealed class GitBoundaryChecksTests
{
    private const string Label = "User action (the builder never runs this):";

    [Theory]
    [InlineData("AJ-006 REQ-001: scaffold", true, true)]
    [InlineData("AJ-045 EDGE-006: retry on 429", true, true)]
    [InlineData("AJ-155: format", true, true)]
    [InlineData("AJ-150 AJ-151 AJ-152: pooled evidence checks", true, false)]
    [InlineData("AJ-138 AJ-139 REQ-028 EDGE-002: pooled with ids", true, false)]
    [InlineData("M6 kickoff K2 to K6 REQ-031: the cloudgpu tier", true, false)]
    [InlineData("M6 exit review fixes: the browser statement", true, false)]
    [InlineData("M6 exit peer review round 2 minors: nits", true, false)]
    [InlineData("M5 exit addendum: CI run green", true, true)]
    [InlineData("M5 exit: merged", true, true)]
    [InlineData("xM7 exit: prefixed", false, false)]
    [InlineData("tidy readme", false, false)]
    [InlineData("AJ-12: two digits", false, false)]
    [InlineData("AJ-123 no colon", false, false)]
    [InlineData("aj-123: lower case", false, false)]
    [InlineData("AJ-123 REQ-001 AJ-124: ids out of order", false, false)]
    [InlineData("M10 exit: two digit milestone", false, false)]
    [InlineData("M7 planning: not an accepted milestone subject", false, false)]
    [InlineData("M7 exited the build", false, false)]
    [InlineData("M7 kickoffs of the pools", false, false)]
    public void SubjectFormsAreAcceptedOrRejected(string subject, bool amended, bool literal)
    {
        Assert.Equal(amended, EvidenceChecks.IsAcceptedSubject(subject));
        Assert.Equal(literal, EvidenceChecks.IsLiteralSubject(subject));
    }

    [GitFact]
    public void HistoryReportsBothCountsAndAnEmptyRange()
    {
        using GitBoundaryContext repo = new();
        string start = repo.NewRepository("planning");
        Assert.Equal((0, 0), EvidenceChecks.HistoryCounts(repo.Root, start));
        repo.Commit("AJ-150 AJ-151: pooled");
        repo.Commit("AJ-152 REQ-001: single");
        repo.Commit("tidy readme");
        Assert.Equal((2, 1), EvidenceChecks.HistoryCounts(repo.Root, start));
        Assert.Equal(2, EvidenceChecks.LiteralHistoryCount(repo.Root, start));
        Assert.Equal(["commit without story id: tidy readme"], EvidenceChecks.HistoryViolations(repo.Root, start));
    }

    [GitFact]
    public void HistoryIgnoresMergesAndTheBuildStartItself()
    {
        using GitBoundaryContext repo = new();
        string start = repo.NewRepository("planning commit that is outside the range");
        string noHooks = "core.hooksPath=" + Path.Combine(repo.Root, "no-hooks");
        _ = repo.Git("-c", noHooks, "checkout", "--quiet", "-b", "side");
        repo.Commit("AJ-010: on the side branch");
        _ = repo.Git("-c", noHooks, "checkout", "--quiet", "main");
        repo.Commit("AJ-011: on main");
        _ = repo.Git("-c", "core.hooksPath=" + Path.Combine(repo.Root, "no-hooks"), "merge", "--quiet", "--no-ff", "--no-verify", "-m", "Merge branch side", "side");
        Assert.Empty(EvidenceChecks.HistoryViolations(repo.Root, start));
        Assert.Equal(3, EvidenceChecks.CommitsAhead(repo.Root, start));
    }

    [GitFact]
    public void HistoryReportsACommitWithAnEmptySubjectByItsHash()
    {
        using GitBoundaryContext repo = new();
        string start = repo.NewRepository("planning");
        repo.Commit("AJ-010: fine");
        _ = repo.Git("-c", "core.hooksPath=" + Path.Combine(repo.Root, "no-hooks"), "commit", "--quiet", "--no-verify", "--allow-empty", "--allow-empty-message", "-m", string.Empty);
        string hash = repo.Head();
        Assert.Equal(["commit without story id: " + hash], EvidenceChecks.HistoryViolations(repo.Root, start));
        Assert.Equal((1, 1), EvidenceChecks.HistoryCounts(repo.Root, start));
    }

    [Fact]
    public void AContextWithoutARepositoryRefusesToRunGitOrWrite()
    {
        using GitBoundaryContext context = new();
        _ = Assert.Throws<InvalidOperationException>(() => context.Commit("AJ-001: x"));
        _ = Assert.Throws<InvalidOperationException>(() => context.Git("status"));
        _ = Assert.Throws<InvalidOperationException>(() => context.Write("x.txt", "x"));
    }

    [GitFact]
    public void HistoryFailsOnAnUnknownBuildStart()
    {
        using GitBoundaryContext repo = new();
        _ = repo.NewRepository("planning");
        Assert.Equal(["cannot list the history"], EvidenceChecks.HistoryViolations(repo.Root, new string('0', 40)));
        Assert.Equal((-1, -1), EvidenceChecks.HistoryCounts(repo.Root, new string('0', 40)));
        Assert.Equal(-1, EvidenceChecks.CommitsAhead(repo.Root, new string('0', 40)));
    }

    [GitFact]
    public void BoundaryStatesAnUntrackedOriginAndUsesTheRecordedBase()
    {
        using GitBoundaryContext repo = new();
        string start = repo.NewRepository("AJ-001: base");
        repo.Commit("AJ-002: local");
        BoundaryResult result = EvidenceChecks.BoundaryCheck(repo.Root, start);
        Assert.Empty(result.Violations);
        Assert.Equal(1, result.Ahead);
        Assert.Equal(start, result.OriginHash);
        Assert.Contains("1 commits ahead", result.Output, StringComparison.Ordinal);
        Assert.Contains("origin/main is not tracked", result.Output, StringComparison.Ordinal);
    }

    [GitFact]
    public void BoundaryRejectsAnOriginBeyondTheRecordedBase()
    {
        using GitBoundaryContext repo = new();
        string start = repo.NewRepository("AJ-001: base");
        repo.Commit("AJ-002: a commit the remote holds");
        repo.AttachOrigin();
        repo.Commit("AJ-003: a local commit after the remote");
        BoundaryResult result = EvidenceChecks.BoundaryCheck(repo.Root, start);
        Assert.Equal(["origin/main moved: the builder pushed"], result.Violations);
        Assert.Equal(2, result.Ahead);
    }

    [GitFact]
    public void BoundaryAcceptsAnAbbreviatedRecordedReference()
    {
        using GitBoundaryContext repo = new();
        string start = repo.NewRepository("AJ-001: base");
        repo.AttachOrigin();
        repo.Commit("AJ-002: local");
        BoundaryResult result = EvidenceChecks.BoundaryCheck(repo.Root, start[..7]);
        Assert.Empty(result.Violations);
        Assert.Equal(1, result.Ahead);
    }

    [GitFact]
    public void BoundaryFailsWhenTheRecordedReferenceAndOriginAreHead()
    {
        using GitBoundaryContext repo = new();
        string start = repo.NewRepository("AJ-001: base");
        repo.AttachOrigin();
        BoundaryResult result = EvidenceChecks.BoundaryCheck(repo.Root, start);
        Assert.Equal(["origin/main moved: the builder pushed"], result.Violations);
        Assert.Equal(0, result.Ahead);
        Assert.Equal(start, result.OriginHash);
    }

    [GitFact]
    public void BoundaryWithAnUnknownRecordedReferenceCannotCount()
    {
        using GitBoundaryContext repo = new();
        _ = repo.NewRepository("AJ-001: base");
        Assert.Contains("cannot count the commits ahead", EvidenceChecks.BoundaryCheck(repo.Root, "deadbeef").Violations);
    }

    [Theory]
    [InlineData("abcdef1234567890abcdef1234567890abcdef12\trefs/heads/main\n", "abcdef1", true)]
    [InlineData("abcdef1234567890abcdef1234567890abcdef12\trefs/heads/main\r\n", "abcdef1234567890abcdef1234567890abcdef12", true)]
    [InlineData("abcdef1234567890abcdef1234567890abcdef12\trefs/heads/main\n", "0123456", false)]
    [InlineData("", "abcdef1", false)]
    [InlineData("abcdef1234567890abcdef1234567890abcdef12\trefs/heads/main\n", "", false)]
    [InlineData("1111111111111111111111111111111111111111\trefs/heads/a/main\nabcdef1234567890abcdef1234567890abcdef12\trefs/tags/main\n2222222222222222222222222222222222222222\trefs/heads/main\n", "abcdef1", false)]
    [InlineData("abcdef1234567890abcdef1234567890abcdef12\trefs/heads/a/main\n", "abcdef1", false)]
    [InlineData("abcdef1234567890abcdef1234567890abcdef12\trefs/heads/main\nabcdef1234567890abcdef1234567890abcdef12\trefs/tags/main\n", "abcdef1", false)]
    public void RemoteMainIsComparedWithTheRecordedHash(string printed, string recorded, bool passes) =>
        Assert.Equal(passes ? [] : ["remote main moved"], EvidenceChecks.RemoteMainViolations(printed, recorded));

    [GitFact]
    public void CleanTreeAcceptsACommittedTreeAndRejectsAnUntrackedFile()
    {
        using GitBoundaryContext repo = new();
        _ = repo.NewRepository("AJ-001: base");
        Assert.Empty(EvidenceChecks.CleanTreeViolations(repo.Root));
        repo.Write("new.txt", "x\n");
        Assert.Equal(["uncommitted changes"], EvidenceChecks.CleanTreeViolations(repo.Root));
    }

    [Fact]
    public void ChecklistPushNeedsAFencedBlock()
    {
        Assert.Empty(EvidenceChecks.ChecklistPushViolations("text\n\n```\ngit push -u origin main\n```\n"));
        Assert.Equal(["missing git push block"], EvidenceChecks.ChecklistPushViolations("Run git push -u origin main when ready.\n"));
        Assert.Equal(["missing git push block"], EvidenceChecks.ChecklistPushViolations("```\ngit status\n```\ngit push in prose\n"));
    }

    [Fact]
    public void TheChecklistCommandBlockRuleAcceptsEitherLabelAndRejectsANearMiss()
    {
        static string Checklist(string label) =>
            $"{label}\n\n```\ngit push -u origin main\n```\n\n{label}\n\n```\ngh api -X POST repos/x/y/pages\n# Deploy from a branch\n```\n\n{label}\n\n```\n{DocumentChecks.ReleaseCommand}\n```\n\nDeploy from a branch, branch main, folder /docs.\n";

        Assert.DoesNotContain("block not marked as user action: git push", DocumentChecks.ChecklistCommandBlocks(Checklist(DocumentChecks.UserActionLabel)));
        Assert.DoesNotContain("block not marked as user action: git push", DocumentChecks.ChecklistCommandBlocks(Checklist(DocumentChecks.DelegatedUserActionLabel)));
        IReadOnlyList<string> nearMiss = DocumentChecks.ChecklistCommandBlocks(Checklist("Delegated to the session:"));
        Assert.Contains("block not marked as user action: git push", nearMiss);
        Assert.Contains("block not marked as user action: gh api", nearMiss);
    }

    [Fact]
    public void CommandsAreLabelledAsUserActions()
    {
        Assert.Empty(EvidenceChecks.UserActionLabelViolations($"{Label}\n\n```\ngit push -u origin main\n```\n"));
        Assert.Empty(EvidenceChecks.UserActionLabelViolations($"{EvidenceChecks.DelegatedUserActionLabel}\n\n```\ngit push -u origin main\n```\n"));
        Assert.Equal(["unlabelled command: git push -u origin main"], EvidenceChecks.UserActionLabelViolations("Delegated to the session:\n\n```\ngit push -u origin main\n```\n"));
        Assert.Empty(EvidenceChecks.UserActionLabelViolations("Read-only check:\n\n```\ngh run list --limit 1\n```\n"));
        Assert.Equal(["unlabelled command: git push -u origin main"], EvidenceChecks.UserActionLabelViolations("Push it:\n\n```\ngit push -u origin main\n```\n"));
        Assert.Equal(["unlabelled command: gh release create v1 --notes-file n.md"], EvidenceChecks.UserActionLabelViolations($"{Label}\n\n```\ngit push\n```\nThen:\n```\ngh release create v1 --notes-file n.md\n```\n"));
        Assert.Equal(["unlabelled command: gh api repos/x/y/pages"], EvidenceChecks.UserActionLabelViolations("```\ngh api repos/x/y/pages\n```\n"));
    }

    [Theory]
    [InlineData("nebius compute instance create --name x")]
    [InlineData("az vm create --name x")]
    [InlineData("gcloud compute instances create x")]
    [InlineData("aws ec2 run-instances --count 1")]
    public void CloudCommandsNeedTheLabelToo(string command)
    {
        Assert.Equal(["unlabelled command: " + command], EvidenceChecks.UserActionLabelViolations($"Create it:\n\n```\n{command}\n```\n"));
        Assert.Empty(EvidenceChecks.UserActionLabelViolations($"{Label}\n\n```\n{command}\n```\n"));
    }

    [Theory]
    [InlineData("- git push origin main", "git push origin main")]
    [InlineData("* `gh release create v1`", "gh release create v1`")]
    [InlineData("$ aws s3 ls", "aws s3 ls")]
    [InlineData("git push -u origin main", "git push -u origin main")]
    [InlineData("> gh api repos/x", "gh api repos/x")]
    public void ABareCommandLineOutsideAFenceIsUnlabelled(string line, string reported)
    {
        Assert.Equal(["unlabelled command: " + reported], EvidenceChecks.UserActionLabelViolations($"Steps:\n{line}\n"));
        Assert.Empty(EvidenceChecks.UserActionLabelViolations("Run the push yourself when you are ready; the git push command is in the block above.\n"));
    }

    [Fact]
    public void TheLabelBelongsToTheNextBlockOnly()
    {
        string text = $"{Label}\n\n```\ngit push\n```\n\n```\ngh release create v1\n```\n";
        Assert.Equal(["unlabelled command: gh release create v1"], EvidenceChecks.UserActionLabelViolations(text));
    }

    [Fact]
    public void BuildLogRunsAreFoundOutsideFencesOnly()
    {
        Assert.Empty(EvidenceChecks.BuildLogRunViolations("- Measured: `gh release create` 1; `git push` 1 or more\n"));
        Assert.Empty(EvidenceChecks.BuildLogRunViolations("```\ngh release create v1\n```\n"));
        Assert.Empty(EvidenceChecks.BuildLogRunViolations("- the upload: git push origin main at the exit by the user decision\n"));
        Assert.Equal(["command run by the builder: - the orchestrator ran git push"], EvidenceChecks.BuildLogRunViolations("- the orchestrator ran git push\n"));
        Assert.Equal(["command run by the builder: - the builder ran `gh release create v1`"], EvidenceChecks.BuildLogRunViolations("- the builder ran `gh release create v1`\n"));
        Assert.Equal(["command run by the builder: $ gh release create v1"], EvidenceChecks.BuildLogRunViolations("$ gh release create v1\n"));
        Assert.Equal(["command run by the builder: - `gh release create v1`"], EvidenceChecks.BuildLogRunViolations("```\nfenced\n```\n- `gh release create v1`\n"));
    }

    [Theory]
    [InlineData("findings: none\nVERDICT: APPROVED\n", true)]
    [InlineData("findings: none\nVERDICT: APPROVED\n\n  \n", true)]
    [InlineData("VERDICT: APPROVED\nfollow-up note\n", false)]
    [InlineData("VERDICT: CHANGES_REQUIRED\nVERDICT: APPROVED", true)]
    [InlineData("VERDICT: APPROVED\nVERDICT: CHANGES_REQUIRED\n", false)]
    [InlineData("VERDICT: APPROVED with notes\n", false)]
    [InlineData("", false)]
    public void TheVerdictIsTheLastLine(string review, bool passes) =>
        Assert.Equal(passes ? [] : ["last line is not VERDICT: APPROVED"], EvidenceChecks.VerdictLastLineViolations(review));

    [Fact]
    public void LiveCommittedChecklistHoldsAndLabelsTheUserActions()
    {
        string checklist = RepositoryRoot.ReadAllText("docs/devpost-checklist.md");
        Assert.Empty(EvidenceChecks.ChecklistPushViolations(checklist));
        Assert.Empty(EvidenceChecks.UserActionLabelViolations(checklist));
    }

    [Fact]
    public void LiveBuildLogShowsNoRunOfAPushOrARelease() =>
        Assert.Empty(EvidenceChecks.BuildLogRunViolations(RepositoryRoot.ReadAllText("docs/build-log.md")));

    [BuildStartHistoryFact]
    public void LiveHistoryHasAcceptedSubjectsAndTheBoundaryIsCounted()
    {
        foreach (string hash in new[] { EvidenceChecks.BuildStartHash, EvidenceChecks.RemoteReferenceHash })
        {
            Assert.True(GitRunner.Run(RepositoryRoot.Path, "cat-file", "-e", hash + "^{commit}").ExitCode == 0, "the commit " + hash + " is missing from a full clone");
        }

        IReadOnlyList<string> violations = EvidenceChecks.HistoryViolations(RepositoryRoot.Path, EvidenceChecks.BuildStartHash);
        Assert.True(violations.Count == 0, string.Join("\n", violations));
        (_, int amended) = EvidenceChecks.HistoryCounts(RepositoryRoot.Path, EvidenceChecks.BuildStartHash);
        Assert.Equal(0, amended);
        Assert.True(EvidenceChecks.CommitsAhead(RepositoryRoot.Path, EvidenceChecks.BuildStartHash) > 0);
    }
}

/// <summary>
/// A fact that is skipped, with its reason, when git is not on PATH or the repository is a shallow clone (as the CI checkout makes) that lacks the build-start or the recorded reference commit;
/// a full clone that lacks one of them fails the fact instead of skipping it.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class BuildStartHistoryFactAttribute : FactAttribute
{
    /// <summary>Creates the attribute; <see cref="FactAttribute.Skip"/> is set when git is missing or a shallow clone lacks one of the two commits.</summary>
    public BuildStartHistoryFactAttribute()
    {
        if (!GitRunner.Available())
        {
            Skip = "git is not on PATH, so the history cannot be listed";
            return;
        }

        bool shallow = string.Equals(GitRunner.Run(RepositoryRoot.Path, "rev-parse", "--is-shallow-repository").Stdout.Trim(), "true", StringComparison.Ordinal);
        foreach (string hash in new[] { EvidenceChecks.BuildStartHash, EvidenceChecks.RemoteReferenceHash })
        {
            if (HasCommit(hash))
            {
                continue;
            }

            // A full clone that lacks a commit is a real failure: the fact then runs and its rule reports "cannot list the history".
            if (shallow)
            {
                Skip = "a shallow clone lacks the build-start " + EvidenceChecks.BuildStartHash[..7] + " or the recorded reference " + EvidenceChecks.RemoteReferenceHash;
            }

            return;
        }
    }

    private static bool HasCommit(string hash) => GitRunner.Run(RepositoryRoot.Path, "cat-file", "-e", hash + "^{commit}").ExitCode == 0;
}