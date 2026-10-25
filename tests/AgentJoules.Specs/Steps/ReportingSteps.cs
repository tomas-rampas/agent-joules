using AgentJoules.Specs.Support;
using AgentJoules.TestSupport.Docs;

using Reqnroll;

using Xunit;
using Xunit.Sdk;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-112: write the methodology document with a contract test (REQ-022). Scoped to the feature because sentences such as "the contract test runs" and "the result is pass" belong to this story only. Every scenario
/// builds the document in memory (<see cref="MethodologyContractContext"/>) and runs <see cref="MethodologyDocumentParser.Check"/> with an in-memory reader of the results folder, so no file, environment variable or
/// network is touched; the check of the committed <c>docs/methodology.md</c> is the unit test <c>The_committed_methodology_document_passes_the_contract</c>.
/// </summary>
[Binding]
[Scope(Feature = "Write the methodology document with a contract test")]
public sealed class ReportingSteps(MethodologyContractContext context)
{
    private const string ExistingSentence = "The meter reads the NVML counter once per window.";

    private readonly MethodologyContractContext _context = context;

    private string[] _alternatives = [];

    private IReadOnlyList<string> Failures => _context.Failures ?? throw new XunitException("The contract test has not run yet.");

    [Given(@"^a methodology document with the five headings and the seven Limitations strings$")]
    public static void GivenACompleteDocument()
    {
        // The default document of the fixtures holds the five headings and the seven strings.
    }

    [Given(@"^it names a calibration file and quotes its stored relativeDifference$")]
    public static void GivenItNamesTheFileAndQuotesTheDifference()
    {
        // The default document names the calibration file and quotes the stored value.
    }

    [Given(@"^the seven strings appear in a different order$")]
    public void GivenTheStringsAreReordered() => _context.ReverseStrings = true;

    [Given(@"^the document has no ""([^""]*)"" heading$")]
    public void GivenTheDocumentHasNoHeading(string heading) => _context.OmitHeading = heading;

    [Given(@"^the Limitations section lacks the string ""([^""]*)""$")]
    public void GivenTheLimitationsSectionLacksTheString(string text) => _context.OmitString = text;

    [Given(@"^the document quotes 0\.5 while the named calibration file stores a different value$")]
    public void GivenTheDocumentQuotesAnotherValue() => _context.Quoted = "0.5";

    [Given(@"^the document names no results/calibration file$")]
    public void GivenTheDocumentNamesNoFile() => _context.NameFile = false;

    [Given(@"^the Limitations section lacks the sentence ""([^""]*)"" or the sentence ""([^""]*)""$")]
    public void GivenTheLimitationsSectionLacksASentence(string first, string second)
    {
        _context.OmitSentence = first;
        _alternatives = [first, second];
    }

    [Given(@"^docs/methodology\.md already holds a sentence written by an M3 story under no required heading$")]
    public void GivenTheDocumentHoldsAnEarlierSentence() => _context.Existing = ExistingSentence;

    [When(@"^the contract test runs$")]
    public void WhenTheContractTestRuns() => _context.Run();

    [When(@"^the headings are created by merging into the existing document$")]
    public void WhenTheHeadingsAreCreatedByMerging() => _context.Run();

    [Then(@"^the result is pass$")]
    public void ThenTheResultIsPass() => Assert.True(Failures.Count == 0, string.Join('\n', Failures));

    [Then(@"^the result is fail and the message names ""([^""]*)""$")]
    public void ThenTheResultIsFailAndNamesTheText(string text)
    {
        Assert.NotEmpty(Failures);
        Assert.Contains(Failures, failure => failure.Contains(text, StringComparison.Ordinal));
    }

    [Then(@"^the result is fail and the message names both values$")]
    public void ThenTheResultIsFailAndNamesBothValues()
    {
        Assert.NotEmpty(Failures);
        Assert.Contains(Failures, failure => failure.Contains(_context.Quoted, StringComparison.Ordinal) && failure.Contains(_context.Stored, StringComparison.Ordinal));
    }

    [Then(@"^the result is fail and the message says the calibration file must be named$")]
    public void ThenTheResultIsFailAndSaysTheFileMustBeNamed() =>
        Assert.Contains(Failures, failure => failure.Contains("the calibration file must be named", StringComparison.Ordinal));

    [Then(@"^the result is fail and the message names the sentence$")]
    public void ThenTheResultIsFailAndNamesTheSentence()
    {
        // Either sentence of the Given step, left out on its own, fails the contract and is named.
        foreach (string sentence in _alternatives)
        {
            _context.OmitSentence = sentence;
            _context.Run();
            Assert.Contains(Failures, failure => failure.Contains(sentence, StringComparison.Ordinal));
        }
    }

    [Then(@"^the sentence is still present verbatim in the document$")]
    public void ThenTheSentenceIsStillPresent()
    {
        Assert.Contains(ExistingSentence, _context.Render(), StringComparison.Ordinal);
        Assert.Empty(MethodologyDocumentParser.LostSentences(ExistingSentence, _context.Render()));
    }

    [Then(@"^the five required headings exist$")]
    public void ThenTheFiveHeadingsExist() =>
        Assert.DoesNotContain(Failures, static failure => failure.Contains("heading", StringComparison.OrdinalIgnoreCase));
}