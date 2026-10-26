using System.Security.Cryptography;

using AgentJoules.TestSupport.Docs;

using Reqnroll;

using Xunit;
using Xunit.Sdk;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-128 (REQ-031, EDGE-016, EDGE-017): the contract of <c>docs/cloudgpu-run.md</c>. Every scenario builds a scratch document in memory (<see cref="CloudGpuRunDraft"/>: the six sections, complete by default, and the one or
/// two lines the scenario changes) and reads it with <see cref="CloudGpuRunDocumentParser"/> and an empty environment, so no file, key variable or network is touched. The steps are scoped to the feature because sentences such as
/// "the contract test runs" and "the result is pass" belong to several stories. The check of the committed document is the unit test <c>The_committed_cloudgpu_run_document_passes_the_contract_when_it_exists</c>.
/// </summary>
public sealed partial class CloudGpuSteps
{
    private const string RunDocumentFeature = "Document the cloudgpu run, the cost and the user's teardown";
    private const string RunDocumentAlphanumerics = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    private readonly CloudGpuRunDraft _documentDraft = new();

    private CloudGpuRunDocumentParser? DocumentResult { get; set; }
    private string? _documentAddress;
    private string? _documentSuffix;

    private CloudGpuRunDocumentParser DocumentParser => DocumentResult ?? throw new XunitException("The contract test has not run yet.");

    private string DocumentProblems => string.Join('\n', DocumentParser.Problems);

    [Given(@"^a run document with the six headings, a Cost section of ""([^""]+)"" and a Teardown section containing ""([^""]+)""$")]
    [Scope(Feature = RunDocumentFeature)]
    public void GivenARunDocumentWithTheSixHeadings(string cost, string word)
    {
        SetDocumentCost(cost);
        Assert.Contains(word, string.Join('\n', _documentDraft.Lines("Teardown")), StringComparison.Ordinal);
    }

    [Given(@"^the Cost section states ""([^""]+)""$")]
    [Scope(Feature = RunDocumentFeature)]
    public void GivenTheCostSectionStatesDocument(string cost) => SetDocumentCost(cost);

    [Given(@"^the document has no ""([^""]+)"" heading$")]
    [Scope(Feature = RunDocumentFeature)]
    public void GivenTheDocumentHasNoHeadingDocument(string heading) => _ = _documentDraft.Remove(heading);

    [Given(@"^the Teardown section says ""([^""]+)"" and does not contain ""([^""]+)""$")]
    [Scope(Feature = RunDocumentFeature)]
    public void GivenTheTeardownSectionSaysDocument(string text, string absent)
    {
        _ = _documentDraft.Set("Teardown", text);
        Assert.DoesNotContain(absent, text, StringComparison.OrdinalIgnoreCase);
    }

    [Given(@"^the Commands section contains a dotted IPv4 address$")]
    [Scope(Feature = RunDocumentFeature)]
    public void GivenTheCommandsSectionContainsAnAddressDocument()
    {
        _documentAddress = "203.0.113." + RandomNumberGenerator.GetInt32(10, 250).ToString(System.Globalization.CultureInfo.InvariantCulture);
        _ = _documentDraft.Add("Commands", "curl " + _documentAddress);
    }

    [Given(@"^the document contains a key built at runtime as ""nvapi-"" plus a random suffix$")]
    [Scope(Feature = RunDocumentFeature)]
    public void GivenTheDocumentContainsARuntimeBuiltKeyDocument()
    {
        _documentSuffix = RandomNumberGenerator.GetString(RunDocumentAlphanumerics, 32);
        _ = _documentDraft.Add("Commands", "export NVIDIA_API_KEY=nvapi-" + _documentSuffix);
    }

    [When(@"^the contract test runs$")]
    [Scope(Feature = RunDocumentFeature)]
    public void WhenTheContractTestRunsDocument() =>
        DocumentResult = CloudGpuRunDocumentParser.Parse(_documentDraft.Render(), new Dictionary<string, string?>());

    [Then(@"^the result is pass$")]
    [Scope(Feature = RunDocumentFeature)]
    public void ThenTheResultIsPassDocument() => Assert.True(DocumentParser.Passed, "Expected a pass but the contract reported:\n" + DocumentProblems);

    [Then(@"^the result is fail and the message names ""([^""]+)""$")]
    [Scope(Feature = RunDocumentFeature)]
    public void ThenTheResultIsFailAndTheMessageNamesDocument(string name) => AssertDocumentProblemContains(name);

    [Then(@"^the result is fail and the message names the 30 USD cap$")]
    [Scope(Feature = RunDocumentFeature)]
    public void ThenTheResultIsFailAndTheMessageNamesTheCapDocument() => AssertDocumentProblemContains("30 USD cap");

    [Then(@"^the result is fail and the message says the teardown is not confirmed$")]
    [Scope(Feature = RunDocumentFeature)]
    public void ThenTheResultIsFailAndTheMessageSaysNotConfirmedDocument() => AssertDocumentProblemContains("the teardown is not confirmed");

    [Then(@"^the result is fail without echoing the address$")]
    [Scope(Feature = RunDocumentFeature)]
    public void ThenTheResultIsFailWithoutEchoingTheAddressDocument()
    {
        string address = _documentAddress ?? throw new XunitException("The scenario built no address.");

        AssertDocumentProblemContains("IPv4");
        Assert.DoesNotContain(address, DocumentProblems, StringComparison.Ordinal);
    }

    [Then(@"^the result is fail and the message does not echo the suffix$")]
    [Scope(Feature = RunDocumentFeature)]
    public void ThenTheResultIsFailAndTheMessageDoesNotEchoTheSuffixDocument()
    {
        string suffix = _documentSuffix ?? throw new XunitException("The scenario built no key.");

        AssertDocumentProblemContains("a key-like token at line");
        Assert.DoesNotContain(suffix, DocumentProblems, StringComparison.Ordinal);
    }

    // "USD 6.12" or "6.12 USD" as the observed cost; the stop reason is the one of a run the guard did not stop.
    private void SetDocumentCost(string cost) =>
        _ = _documentDraft.Set("Cost", "Observed cost: " + cost + " (billing page, 2026-11-01T10:45:00Z).", "Stop reason: no stop triggered.");

    private void AssertDocumentProblemContains(string text)
    {
        Assert.False(DocumentParser.Passed, "Expected a fail but the contract passed.");
        Assert.Contains(DocumentParser.Problems, problem => problem.Contains(text, StringComparison.Ordinal));
    }
}