using System.Text.RegularExpressions;

using AgentJoules.TestSupport;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-126 (REQ-031): serve Nemotron 3 Nano in FP8 with NIM on the VM. The scenarios follow the AJ-023 convention (see the AJ-125 steps): with the gate AGENTJOULES_INTEGRATION closed, or with the committed evidence absent, a
/// step writes <c>not asserted: &lt;reason&gt;</c> and passes; otherwise it asserts the <c>Commands</c> section of <c>docs/cloudgpu-run.md</c> and the <c>## cloudgpu</c> section of <c>docs/endpoints.md</c> (read with the production
/// endpoints parser). A scenario about an incident (the fallback image, the KV-cache cap) asserts only when the run document records it. The document is checked for the shape of a key and for the value of a key variable that is
/// set in this process; no value is ever echoed. Every step is scoped to the tag <c>AJ-S126</c>.
/// </summary>
public sealed partial class CloudGpuSteps
{
    private const string NimTag = "AJ-S126";

    private static readonly string[] KeyVariables = ["NGC_API_KEY", "NVIDIA_API_KEY"];

    private static string[] ProfileLines(string commands) =>
        [.. commands.Split('\n').Where(static line => line.Contains("NIM_MODEL_PROFILE", StringComparison.Ordinal))];

    private static string? NoProfileRecorded(string commands) =>
        ProfileLines(commands).Length > 0 ? null : "the Commands section records no NIM_MODEL_PROFILE line yet";

    // The 1-based numbers of the lines of a text that satisfy the test: a failure names the lines and never repeats their content.
    private static int[] LineNumbers(string text, Func<string, bool> test) =>
        [.. text.Split('\n').Select(static (line, index) => (line, number: index + 1)).Where(pair => test(pair.line)).Select(static pair => pair.number)];

    private static void AssertFp8Profile(string commands)
    {
        int[] notFp8 = LineNumbers(commands, static line => line.Contains("NIM_MODEL_PROFILE", StringComparison.Ordinal) && !line.Contains("fp8", StringComparison.OrdinalIgnoreCase));
        int[] bf16 = LineNumbers(commands, static line => line.Contains("NIM_MODEL_PROFILE", StringComparison.Ordinal) && line.Contains("bf16", StringComparison.OrdinalIgnoreCase));
        Assert.True(notFp8.Length == 0, "NIM_MODEL_PROFILE lines of the Commands section without fp8, at section lines " + string.Join(", ", notFp8));
        Assert.True(bf16.Length == 0, "NIM_MODEL_PROFILE lines of the Commands section that name bf16, at section lines " + string.Join(", ", bf16));
    }

    private static void AssertMatches(string pattern, string text, string failure) =>
        Assert.True(Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)), failure);

    private void AssertEndpoints(bool withMeasurements)
    {
        AssertEvidence(
            () => CloudGpuEvidence.AbsentReason(CloudGpuEvidenceKind.Endpoints),
            () =>
            {
                string[] failures = [.. CloudGpuEvidence.EndpointsFailures(CloudGpuEvidence.EndpointsText()!, withMeasurements)];
                Assert.True(failures.Length == 0, "the ## cloudgpu section of docs/endpoints.md:\n" + string.Join('\n', failures));
            });
    }

    // AJ-126

    [Given(@"^the NGC login on the VM succeeded through standard input$")]
    [Scope(Tag = NimTag)]
    public void GivenTheNgcLoginUsedStandardInput() =>
        AssertSection(
            "Commands",
            commands =>
            {
                Assert.True(commands.Contains("--password-stdin", StringComparison.Ordinal), "the Commands section has no --password-stdin.");
                Assert.True(LineNumbers(commands, static line => Regex.IsMatch(line, @"--password(?!-stdin)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))).Length == 0, "the Commands section passes the NGC password as an argument.");
            });

    [Given(@"^the NIM profile listing contains a single-GPU FP8 profile$")]
    [Given(@"^the NIM profile listing contains only BF16 profiles$")]
    [Given(@"^the pull of ""(?:[^""]*)"" returns a not-found error$")]
    [Given(@"^the container exits with a memory error at the default context length$")]
    [Given(@"^the NGC login returns an authorization error$")]
    [Given(@"^nvidia-smi lists a compute process other than the NIM process$")]
    [When(@"^the builder pulls ""(?:[^""]*)""$")]
    [When(@"^the builder uses ""(?:[^""]*)""$")]
    [When(@"^the builder reads the listing$")]
    [When(@"^the builder sets NIM_MAX_MODEL_LEN to a lower value$")]
    [When(@"^the builder records the result$")]
    [When(@"^the builder checks exclusivity$")]
    [Scope(Tag = NimTag)]
    public static void NimBuilderProcedure()
    {
        // The pull, the profile listing and the container start are builder commands over SSH; a test never runs them. The Then steps check what they left in docs/cloudgpu-run.md and docs/endpoints.md.
    }

    [Then(@"^the pull string is confirmed and the digest is recorded as ContainerDigest$")]
    [Scope(Tag = NimTag)]
    public void ThenThePullAndDigestAreRecorded() =>
        AssertSection(
            "Commands",
            commands =>
            {
                Assert.True(commands.Contains("docker logout", StringComparison.Ordinal), "the Commands section has no docker logout.");
                Assert.True(commands.Contains("docker pull", StringComparison.Ordinal), "the Commands section has no docker pull.");
                Assert.True(Regex.IsMatch(commands, @"ContainerDigest[^\r\n]{0,40}?\bsha256:[0-9a-f]{16,}", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)), "the Commands section does not record ContainerDigest with a sha256 value.");
            });

    [When(@"^the container starts with NIM_MODEL_PROFILE set to it$")]
    [Scope(Tag = NimTag)]
    public void WhenTheContainerStartsWithTheProfile() =>
        AssertSection("Commands", AssertFp8Profile, NoProfileRecorded);

    [Then(@"^GET /v1/models from the VM returns a model ID$")]
    [Scope(Tag = NimTag)]
    public void ThenTheModelsCallReturnsAnId() => AssertEndpoints(withMeasurements: false);

    [Then(@"^the ""## cloudgpu"" section of docs/endpoints\.md records the ID, the date and the command$")]
    [Scope(Tag = NimTag)]
    public void ThenTheEndpointsSectionRecordsTheCall() => AssertEndpoints(withMeasurements: true);

    [Then(@"^the Commands section records the error and the substitution$")]
    [Scope(Tag = NimTag)]
    public void ThenTheSubstitutionIsRecorded() =>
        AssertSection(
            "Commands",
            commands =>
            {
                AssertMatches(@"not found|manifest unknown|denied|unauthori[sz]ed|error", commands, "the Commands section records no pull error.");
                AssertMatches(@"substitut|fallback|instead", commands, "the Commands section records no substitution.");
            },
            static commands => NoIncident(commands, "model-free-nim", "use of the fallback image model-free-nim"));

    [Then(@"^the builder stops and reports instead of using BF16$")]
    [Scope(Tag = NimTag)]
    public void ThenBf16IsNeverUsed() => AssertSection("Commands", AssertFp8Profile, NoProfileRecorded);

    [Then(@"^the value used is recorded and the models call succeeds$")]
    [Scope(Tag = NimTag)]
    public void ThenTheContextCapIsRecorded()
    {
        AssertSection(
            "Commands",
            commands => Assert.True(Regex.IsMatch(commands, @"NIM_MAX_MODEL_LEN\D{0,4}\d{3,}", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)), "the Commands section records no NIM_MAX_MODEL_LEN value."),
            static commands => NoIncident(commands, "NIM_MAX_MODEL_LEN", "context length cap (the container started at the default)"));
        AssertEndpoints(withMeasurements: false);
    }

    [Then(@"^the error is recorded without any key value$")]
    [Scope(Tag = NimTag)]
    public void ThenNoKeyValueIsRecorded() =>
        AssertDocument(
            document =>
            {
                Assert.False(CloudGpuEvidence.HasSecretShape(document), "docs/cloudgpu-run.md holds the shape of a key (a key-like token: nvapi- or 40 or more mixed characters).");
                foreach (string variable in KeyVariables)
                {
                    string? value = Environment.GetEnvironmentVariable(variable);
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        Assert.False(document.Contains(value, StringComparison.Ordinal), $"docs/cloudgpu-run.md holds the value of {variable} (the value is not echoed).");
                    }
                }
            });

    [Then(@"^measurement does not start and the process is recorded$")]
    [Scope(Tag = NimTag)]
    public void ThenTheExclusivityCheckIsRecorded() =>
        AssertSection("Commands", commands => Assert.True(commands.Contains("query-compute-apps", StringComparison.Ordinal), "the Commands section records no nvidia-smi query-compute-apps check."));
}