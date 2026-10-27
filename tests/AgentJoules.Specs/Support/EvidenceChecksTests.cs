using AgentJoules.TestSupport;

using Xunit;

namespace AgentJoules.Specs.Support;

/// <summary>
/// One row for each additive rule of <see cref="EvidenceChecks"/> that no scenario of the evidence stories exercises (negative rows assert the violation text, the two acceptance rows assert none).
/// Each row lays a minimal fixture out in a <see cref="TempWorkspace"/>; nothing reads the repository, the network or a key.
/// </summary>
public sealed class EvidenceChecksTests
{
    private const string CloudGpuRun = "results/20261025T000000Z-cloudgpu.json";
    private const string CloudGpuProbe = "results/probe-20261025T000000Z-cloudgpu.json";
    private const string LocalRun = "results/20261025T000000Z-local.json";
    private const string Posts = "content/linkedin-posts.md";

    private static string CloudGpuFile(string provider, string precision, string preset, int repeat = 3) =>
        "{\"run\":{\"status\":\"completed\",\"repeat\":" + repeat + "},\"environment\":{\"provider\":\"" + provider + "\",\"precision\":\"" + precision + "\",\"instancePreset\":\"" + preset + "\"}}";

    private static string LocalFile(string load, string offload, string power, string runtime) =>
        "{\"run\":{\"status\":\"completed\",\"repeat\":3},\"baseline\":{\"seconds\":30},\"environment\":{" + load + offload + "\"powerSource\":\"" + power + "\",\"localRuntime\":\"" + runtime + "\"}}";

    private static void AssertViolation(IReadOnlyList<string> violations, string expected) =>
        Assert.True(violations.Contains(expected, StringComparer.Ordinal), "expected: " + expected + "\nactual:\n" + string.Join("\n", violations));

    [Theory]
    [InlineData("other-cloud", "FP8", "gpu-h100", "environment.provider other-cloud is not nebius-ai-cloud")]
    [InlineData("nebius-ai-cloud", "FP16", "gpu-h100", "environment.precision FP16 is not FP8")]
    [InlineData("nebius-ai-cloud", "FP8", "", "environment.instancePreset is empty")]
    public void CloudGpuRunRejectsAWrongEnvironment(string provider, string precision, string preset, string expected)
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = workspace.WriteFile(CloudGpuRun, CloudGpuFile(provider, precision, preset));

        AssertViolation(EvidenceChecks.CloudGpuRunCheck(workspace.Root).Violations, expected);
    }

    [Theory]
    [InlineData("{\"migMode\":\"disabled\"}")]
    [InlineData("{\"migMode\":\"disabled\",\"energyCounterSupported\":\"yes\"}")]
    public void CloudGpuProbeRejectsAMissingEnergyCounterFlag(string probe)
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = workspace.WriteFile(CloudGpuProbe, probe);

        AssertViolation(EvidenceChecks.CloudGpuProbeViolations(workspace.Root), "energyCounterSupported is missing or not a boolean");
    }

    [Fact]
    public void KeyScanFindsAnNvidiaTokenAndNeverEchoesIt()
    {
        // Built at run time, so no key-shaped literal sits in the source.
        string token = string.Concat("nvapi", "-", new string('k', 12));

        IReadOnlyList<string> violations = EvidenceChecks.KeyMaterialViolations("Run record\n" + token + "\n", keyValue: null);

        AssertViolation(violations, EvidenceChecks.KeyInRecord);
        Assert.False(violations.Any(violation => violation.Contains(token, StringComparison.Ordinal)), "a violation carries the token (the value is not echoed)");
        Assert.Empty(EvidenceChecks.KeyMaterialViolations("Run record without a key.\n", keyValue: null));
    }

    [Theory]
    [InlineData("partial", "run.status partial is not completed: local")]
    [InlineData("failed", "run.status failed is not completed: local")]
    public void TierRulesRejectAStatusOtherThanCompleted(string status, string expected)
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = workspace.WriteFile(LocalRun, "{\"run\":{\"status\":\"" + status + "\",\"repeat\":3}}");

        AssertViolation(EvidenceChecks.TierViolations(workspace.Root), expected);
    }

    [Fact]
    public void TierRulesRejectACloudGpuFileWithTooFewRepeats()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = workspace.WriteFile(CloudGpuRun, CloudGpuFile("nebius-ai-cloud", "FP8", "gpu-h100", repeat: 2));

        AssertViolation(EvidenceChecks.TierViolations(workspace.Root), "run.repeat 2 is below 3: cloudgpu");
    }

    [Theory]
    [InlineData("", "\"gpuOffloadPercent\":100,", "ac", "ollama", "environment.modelLoadSeconds is missing or not a number")]
    [InlineData("\"modelLoadSeconds\":1.5,", "", "ac", "ollama", "environment.gpuOffloadPercent is missing or not a number")]
    [InlineData("\"modelLoadSeconds\":1.5,", "\"gpuOffloadPercent\":100,", "solar", "ollama", "environment.powerSource solar is not one of ac, battery, unknown")]
    [InlineData("\"modelLoadSeconds\":1.5,", "\"gpuOffloadPercent\":100,", "ac", "llamafile", "environment.localRuntime llamafile is not one of nim, ollama")]
    public void LocalFileRulesRejectAMissingOrInvalidField(string load, string offload, string power, string runtime, string expected)
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = workspace.WriteFile(LocalRun, LocalFile(load, offload, power, runtime));

        AssertViolation(EvidenceChecks.LocalFileViolations(workspace.Root), expected);
    }

    [Fact]
    public void PostsRejectAVariantOverTheCharacterLimit()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string over = new('x', EvidenceChecks.VariantCharacterLimit + 1);
        _ = workspace.WriteFile(Posts, "## Variant: A\n\n" + over + "\n\n## Variant: B\n\nshort\n\n## Variant: C\n\nshort\n\nSources: a.json\n");

        IReadOnlyList<string> violations = EvidenceChecks.PostsViolations(workspace.Root);

        Assert.True(violations.Any(static violation => violation.StartsWith("variant A has ", StringComparison.Ordinal)), string.Join("\n", violations));
        Assert.DoesNotContain(violations, static violation => violation.StartsWith("variant B ", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Sources: absent.json", "missing source file: results/absent.json")]
    [InlineData("Sources: ../outside.json", "unresolved source outside results/: ../outside.json")]
    [InlineData("Sources:", "empty Sources line in content/linkedin-posts.md")]
    public void PostsRejectAnUnusableSourcesLine(string sources, string expected)
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = workspace.WriteFile("results/present.json", "{}");
        _ = workspace.WriteFile("outside.json", "{}");
        _ = workspace.WriteFile(Posts, "## Variant: A\n\nshort\n\n## Variant: B\n\nshort\n\n## Variant: C\n\nshort\n\n" + sources + "\n");

        AssertViolation(EvidenceChecks.PostsViolations(workspace.Root), expected);
    }

    [Fact]
    public void StoryRejectsAFillAfterBuildLineAnywhere()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = workspace.WriteFile("docs/devpost-story.md", "# Story\n\nA paragraph that still says fill-after-build in its body.\n");

        AssertViolation(EvidenceChecks.StoryViolations(workspace.Root), "Fill-after-build text in docs/devpost-story.md");
    }

    [Fact]
    public void VerificationDatesRejectADateBeforeTheEarliest()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        foreach (string document in EvidenceChecks.VerifiedDocuments)
        {
            _ = workspace.WriteFile(document, "Verified: 2026-10-16\n");
        }

        AssertViolation(EvidenceChecks.VerificationDates(workspace.Root).Violations, "verification date before 2026-10-17: docs/endpoints.md");
    }

    private static string Checklist(int demoUrls, int tracks, int bullets)
    {
        string urls = string.Concat(Enumerable.Repeat("Demo URL: https://example.invalid/\n", demoUrls));
        string trackHeadings = string.Concat(Enumerable.Repeat("## Tracks\n\nx\n\n", tracks));
        string items = string.Concat(Enumerable.Range(1, bullets).Select(static index => "- item " + index + "\n"));
        return urls + "Verified: 2026-10-20\n\n## Registration\n\nx\n\n" + trackHeadings + "## Judging criteria\n\nx\n\n## Credits\n\nx\n\n## Feedback form, user fills\n\n" + items;
    }

    [Theory]
    [InlineData(2, 1, 7, "Demo URL: line occurs 2 times, expected 1")]
    [InlineData(1, 2, 7, "heading occurs 2 times: Tracks")]
    [InlineData(1, 1, 6, "Feedback form, user fills lists 6 bullets, expected 7")]
    public void ChecklistRejectsADuplicateOrACountOutOfRange(int demoUrls, int tracks, int bullets, string expected)
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = workspace.WriteFile("docs/devpost-checklist.md", Checklist(demoUrls, tracks, bullets));

        AssertViolation(EvidenceChecks.ChecklistViolations(workspace.Root), expected);
    }

    [Theory]
    [InlineData(6, "owner: user occurs 6 times in docs/devpost-feedback.md, expected 7")]
    [InlineData(8, "owner: user occurs 8 times in docs/devpost-feedback.md, expected 7")]
    public void FeedbackRejectsAUserCountOtherThanSeven(int users, string expected)
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = workspace.WriteFile("docs/devpost-feedback.md", string.Concat(Enumerable.Range(1, users).Select(static index => "- answer " + index + " (owner: user)\n")));

        AssertViolation(EvidenceChecks.FeedbackViolations(workspace.Root), expected);
    }

    [Fact]
    public void FeedbackCountsLinesNotOccurrences()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string lines = string.Concat(Enumerable.Range(1, 5).Select(static index => "- answer " + index + " (owner: user)\n"));
        _ = workspace.WriteFile("docs/devpost-feedback.md", lines + "- both (owner: user) and (owner: user)\n");

        AssertViolation(EvidenceChecks.FeedbackViolations(workspace.Root), "owner: user occurs 6 times in docs/devpost-feedback.md, expected 7");
    }

    [Theory]
    [InlineData("<html>GPU-board energy only</html>", "missing chart link: joules-per-step.svg")]
    [InlineData("<html>joules-per-step.svg</html>", "missing statement: GPU-board energy only")]
    public void PageRejectsAMissingChartLinkOrStatement(string page, string expected)
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = workspace.WriteFile("docs/report/index.html", page);

        AssertViolation(EvidenceChecks.PageViolations(workspace.Root), expected);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void PostsRejectAVariantCountOtherThanThree(int variants)
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = workspace.WriteFile("results/present.json", "{}");
        string body = string.Concat(Enumerable.Range(1, variants).Select(static index => "## Variant: V" + index + "\n\nshort\n\n"));
        _ = workspace.WriteFile(Posts, body + "Sources: present.json\n");

        AssertViolation(EvidenceChecks.PostsViolations(workspace.Root), "content/linkedin-posts.md has " + variants + " variants, expected 3");
    }

    [Fact]
    public void PostsRejectAFileWithoutASourcesLine()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = workspace.WriteFile(Posts, "## Variant: A\n\nshort\n\n## Variant: B\n\nshort\n\n## Variant: C\n\nshort\n");

        AssertViolation(EvidenceChecks.PostsViolations(workspace.Root), "no Sources line in content/linkedin-posts.md");
    }

    [Fact]
    public void ChecklistRejectsAFileWithoutAVerifiedLine()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string text = Checklist(1, 1, 7).Replace("Verified: 2026-10-20\n", string.Empty, StringComparison.Ordinal);
        _ = workspace.WriteFile("docs/devpost-checklist.md", text);

        AssertViolation(EvidenceChecks.ChecklistViolations(workspace.Root), "no Verified line in docs/devpost-checklist.md");
    }

    [Fact]
    public void ReadmeRuleScopesCloudGpuToTheResultsBlock()
    {
        const string Outside = "cloudgpu is named here\n<!-- results:begin -->\nlocal only\n<!-- results:end -->\ncloudgpu again\n";
        const string Inside = "intro\n<!-- results:begin -->\ncloudgpu row\n<!-- results:end -->\n";

        Assert.Contains("results block lacks cloudgpu", EvidenceChecks.ReadmeResultsViolations(Outside));
        Assert.Empty(EvidenceChecks.ReadmeResultsViolations(Inside));
    }

    [Fact]
    public void CloudGpuProbeAcceptsMigNotSupported()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = workspace.WriteFile(CloudGpuProbe, "{\"migMode\":\"not-supported\",\"energyCounterSupported\":true}");

        Assert.Empty(EvidenceChecks.CloudGpuProbeViolations(workspace.Root));
    }

    [Fact]
    public void CloudGpuRunAcceptsAPartialRun()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = workspace.WriteFile(CloudGpuRun, CloudGpuFile("nebius-ai-cloud", "FP8", "gpu-h100").Replace("completed", "partial", StringComparison.Ordinal));

        Assert.Empty(EvidenceChecks.CloudGpuRunCheck(workspace.Root).Violations);
    }

    [Theory]
    [InlineData("results/20261025T000000Z-local.json.raw", "stray file: 20261025T000000Z-local.json.raw")]
    [InlineData("results/20261025T000000Z-local.json.partial", "stray file: 20261025T000000Z-local.json.partial")]
    public void StrayFilesRejectARawOrPartialFile(string path, string expected)
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = workspace.WriteFile(path, "{}");

        AssertViolation(EvidenceChecks.StrayFileViolations(workspace.Root), expected);
    }
}