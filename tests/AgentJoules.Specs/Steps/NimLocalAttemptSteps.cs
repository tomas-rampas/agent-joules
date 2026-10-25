using AgentJoules.Specs.Support;
using AgentJoules.TestSupport.Docs;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-103: the time-boxed NIM-local attempt is recorded in <c>docs/nim-local-attempt.md</c> (REQ-019, EDGE-010). The six scenarios about "the builder" follow the AJ-101 pattern: the Given step evaluates
/// <see cref="NimLocalAttemptContext.Open"/> (the gate AGENTJOULES_INTEGRATION, and for the first scenario the variable NGC_API_KEY, which is read and never printed); when the gate is closed, a variable is absent or
/// the document does not exist, the remaining steps record the reason and pass. With the gate open they assert the committed document through <see cref="NimAttemptDocumentParser"/> and never perform the builder's
/// action: no pull, no login, no container, no request and no write happen here (the live check is the gated <c>NimLocalEndpointTests</c>). A scenario whose precondition the committed record does not meet (the
/// fallback scenarios when the decision is <c>nim</c>, a login rejection the record does not mention) records that as its note and passes. Scoped to the feature because the Then sentences about the Decision section
/// belong to this story only.
/// </summary>
[Binding]
[Scope(Feature = "Run the NIM-local attempt within 90 minutes")]
public sealed class NimLocalAttemptSteps(NimLocalAttemptContext context, IReqnrollOutputHelper output)
{
    private const string LocalRuntimeOllama = "ollama";

    // Words that say why the NIM-local attempt was not used.
    private static readonly string[] WhyMarkers = ["not used", "because", "fall back", "falls back", "falling back", "fell back", "fallback"];

    private readonly NimLocalAttemptContext _context = context;
    private readonly IReqnrollOutputHelper _output = output;

    private bool _preconditionHolds = true;

    // True when the step must assert; otherwise the reason is written to the scenario output.
    private bool Asserting()
    {
        if (!_context.IsActive)
        {
            _output.WriteLine("skipped: " + _context.Note);
            return false;
        }

        if (!_preconditionHolds)
        {
            _output.WriteLine("passed without asserting: " + _context.Note);
            return false;
        }

        return true;
    }

    // Marks the scenario as one the committed record cannot answer: the note is written by every later step.
    private void PreconditionFails(string note)
    {
        _preconditionHolds = false;
        _context.Skip(note);
    }

    // The fallback scenarios assert only when the committed decision is ollama.
    private void RequireFallbackDecision()
    {
        _context.Open();
        if (_context.IsActive && _context.Document.DecisionRuntime != LocalRuntimeOllama)
        {
            PreconditionFails("the committed decision is not ollama, so the fallback scenario has nothing to assert");
        }
    }

    [Given(@"^Docker Desktop with GPU support is running and NGC_API_KEY is set$")]
    public void GivenDockerAndTheKeyAreAvailable()
    {
        _context.Open("NGC_API_KEY");
        if (_context.IsActive && _context.Document.DecisionRuntime != "nim")
        {
            PreconditionFails("the committed decision is not nim, so the success scenario has nothing to assert");
        }
    }

    [When(@"^the builder pulls and starts a Nemotron NIM and GET /v1/models returns a model ID within 90 minutes$")]
    public void WhenTheModelsCallReturnedWithinTheBox()
    {
        if (!Asserting())
        {
            return;
        }

        NimAttemptDocument document = _context.Document;
        Assert.NotNull(document.Started);
        Assert.NotNull(document.Ended);
        Assert.True(document.Ended - document.Started <= NimAttemptRules.Box, "The attempt took longer than the 90-minute box.");
    }

    [Then(@"^the Outcome section records the model ID and the memory readings$")]
    public void ThenTheOutcomeRecordsTheModelIdAndTheMemoryReadings()
    {
        if (!Asserting())
        {
            return;
        }

        string outcome = _context.SectionBody("Outcome");
        Assert.Matches(@"(?i)model id\b|""id""\s*:\s*""[^""]+""", outcome);
        Assert.Contains("memory.used", outcome, StringComparison.Ordinal);
        Assert.Matches(@"\d+ MiB, \d+ MiB", outcome);
    }

    [Then(@"^the Decision section says ""Local runtime: (nim|ollama)""$")]
    public void ThenTheDecisionSays(string runtime)
    {
        if (!Asserting())
        {
            return;
        }

        Assert.Equal(runtime, _context.Document.DecisionRuntime);
    }

    [Given(@"^the NIM container exits during start-up with an error$")]
    public void GivenTheContainerExitsDuringStartUp() => RequireFallbackDecision();

    [When(@"^the builder records the decision$")]
    public void WhenTheBuilderRecordsTheDecision()
    {
        if (!Asserting())
        {
            return;
        }

        Assert.Contains(NimAttemptDocumentParser.DecisionHeading, _context.Document.Headings);
    }

    [Then(@"^README\.md links ""docs/nim-local-attempt\.md"" and states why NIM-local was not used$")]
    public void ThenTheReadmeLinksTheAttemptAndStatesWhy()
    {
        if (!Asserting())
        {
            return;
        }

        string readme = NimLocalAttemptContext.ReadReadme();
        Assert.Matches(@"\]\(docs/nim-local-attempt\.md[)#]", readme);
        Assert.Contains(
            readme.Split('\n'),
            static line => line.Contains("NIM", StringComparison.Ordinal) && WhyMarkers.Any(marker => line.Contains(marker, StringComparison.OrdinalIgnoreCase)));
    }

    [Given(@"^90 minutes have passed since the first catalog search without a successful models call$")]
    public void GivenTheBoxExpired()
    {
        RequireFallbackDecision();
        if (_preconditionHolds && _context.IsActive && !_context.BoxWasExceeded)
        {
            PreconditionFails("the committed record does not show an exceeded box, so the expiry scenario has nothing to assert");
        }
    }

    [When(@"^the builder applies the hard stop$")]
    public void WhenTheBuilderAppliesTheHardStop()
    {
        if (!Asserting())
        {
            return;
        }

        Assert.NotNull(_context.Document.BoxExceededReason);
    }

    [Then(@"^the container is stopped and removed$")]
    public void ThenTheContainerIsStoppedAndRemoved()
    {
        if (!Asserting())
        {
            return;
        }

        Assert.Matches(@"docker (container )?(rm|stop)\b", _context.SectionBody("Commands"));
    }

    [Then(@"^the document records the status, the errors and the switch to Ollama$")]
    public void ThenTheDocumentRecordsTheStatusTheErrorsAndTheSwitch()
    {
        if (!Asserting())
        {
            return;
        }

        Assert.False(string.IsNullOrWhiteSpace(_context.SectionBody("Outcome")), "The Outcome section is empty.");
        Assert.Equal(LocalRuntimeOllama, _context.Document.DecisionRuntime);
    }

    [Given(@"^the NGC login to nvcr\.io returns an authorization error$")]
    public void GivenTheLoginIsRejected()
    {
        _context.Open();
        if (_context.IsActive && !_context.OutcomeReportsRejectedLogin)
        {
            PreconditionFails("the committed Outcome does not report a rejected NGC login, so the scenario has nothing to assert");
        }
    }

    [When(@"^the builder writes the Outcome section$")]
    public void WhenTheBuilderWritesTheOutcome()
    {
        if (!Asserting())
        {
            return;
        }

        Assert.False(string.IsNullOrWhiteSpace(_context.SectionBody("Outcome")), "The Outcome section is empty.");
    }

    [Then(@"^the error text is recorded without any key value$")]
    public void ThenTheErrorTextIsRecordedWithoutAnyKeyValue()
    {
        if (!Asserting())
        {
            return;
        }

        // The whole contract covers the Commands section's redaction (--password-stdin, the logout) and the key shapes; the key value itself is checked by the next step.
        IReadOnlyList<string> violations = NimAttemptRules.Check(_context.Document, NimLocalAttemptContext.KeyValue());
        Assert.True(violations.Count == 0, "The document breaks the contract:\n" + string.Join('\n', violations));
    }

    [Then(@"^the grep of the document for NGC_API_KEY prints nothing$")]
    public void ThenTheGrepForTheKeyPrintsNothing()
    {
        if (!Asserting())
        {
            return;
        }

        string? key = NimLocalAttemptContext.KeyValue();
        if (string.IsNullOrEmpty(key))
        {
            _output.WriteLine("NGC_API_KEY is not set: only the key shapes were checked");
            return;
        }

        Assert.False(_context.Document.Raw.Contains(key, StringComparison.Ordinal), "docs/nim-local-attempt.md holds the NGC_API_KEY value; the text is not shown");
    }

    [Given(@"^the container starts and exits with an out-of-memory error on the 16 GB GPU$")]
    public void GivenTheContainerRunsOutOfMemory()
    {
        RequireFallbackDecision();
        if (_preconditionHolds && _context.IsActive && !_context.OutcomeReportsOutOfMemory)
        {
            PreconditionFails("the committed Outcome reports no out-of-memory exit, so the scenario has nothing to assert");
        }
    }

    [Then(@"^the memory readings and the error are recorded$")]
    public void ThenTheMemoryReadingsAndTheErrorAreRecorded()
    {
        if (!Asserting())
        {
            return;
        }

        string outcome = _context.SectionBody("Outcome");
        Assert.Contains("memory.used", outcome, StringComparison.Ordinal);
        Assert.Matches(@"\d+ MiB, \d+ MiB", outcome);
        Assert.True(_context.OutcomeReportsOutOfMemory, "The Outcome section does not record the out-of-memory error.");
    }

    [Given(@"^the catalog search finds no Nemotron NIM that supports compute capability 8\.6$")]
    public void GivenNoSupportedImageExists()
    {
        RequireFallbackDecision();
        if (_preconditionHolds && _context.IsActive && !_context.ImageStatesNoneFound)
        {
            PreconditionFails("the committed Image section does not state that no image was found, so the scenario has nothing to assert");
        }
    }

    [When(@"^the builder writes the Image section$")]
    public void WhenTheBuilderWritesTheImageSection()
    {
        if (!Asserting())
        {
            return;
        }

        Assert.Contains("Image", _context.Document.Headings);
    }

    [Then(@"^the section states that none was found and the Decision falls back to Ollama$")]
    public void ThenTheImageSectionStatesNoneAndTheDecisionFallsBack()
    {
        if (!Asserting())
        {
            return;
        }

        Assert.True(_context.ImageStatesNoneFound, "The Image section does not state that none was found.");
        Assert.Equal(LocalRuntimeOllama, _context.Document.DecisionRuntime);
    }
}