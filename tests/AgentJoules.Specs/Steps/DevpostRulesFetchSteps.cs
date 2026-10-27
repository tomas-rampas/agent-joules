using System.Net;

using AgentJoules.Specs.Support;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-138: the official rules page fetch (REQ-028). The scenario about the live page is gated by <see cref="AgentJoules.TestSupport.IntegrationGate"/> like the other @integration features: with the gate closed it makes no
/// request and records the reason, with it open it requests the Rules page once. The committed checklist is read, never written: it must carry a verification date or the <c>not verified</c> statement with the HTTP status.
/// The scenario about a 403 answer is hermetic: the handler is a fake, so no request leaves the process.
/// </summary>
[Binding]
[Scope(Feature = "AJ-138 Official rules page fetch")]
public sealed class DevpostRulesFetchSteps(DevpostContext context)
{
    private const string RulesUrl = "https://nebiusglobalaihackathon.devpost.com/rules";

    private readonly DevpostContext _context = context;

    [Given(@"^network access$")]
    public void GivenNetworkAccess() => _context.Open();

    [Given(@"^the page answers HTTP 403$")]
    public void GivenThePageAnswers403() => _context.Status = (int)HttpStatusCode.Forbidden;

    [When(@"^the builder requests https://nebiusglobalaihackathon\.devpost\.com/rules$")]
    public async Task WhenTheBuilderRequestsTheRulesPage()
    {
        if (!_context.IsActive)
        {
            return;
        }

        using HttpResponseMessage response = await _context.RealClient().GetAsync(new Uri(RulesUrl), CancellationToken.None);
        _context.Status = (int)response.StatusCode;
    }

    [When(@"^the checklist is written$")]
    public async Task WhenTheChecklistIsWritten()
    {
        using HttpResponseMessage response = await _context.FakeClient((HttpStatusCode)_context.Status).GetAsync(new Uri(RulesUrl), CancellationToken.None);
        _context.Status = (int)response.StatusCode;
        _context.Section = DocumentChecks.SourceSectionFor(_context.Status, DocumentChecks.EarliestVerification);
    }

    [Then(@"^either the text is recorded with the fetch date or the checklist says ""not verified"" with the HTTP status$")]
    public void ThenTheChecklistRecordsTheOutcome()
    {
        if (!_context.IsActive)
        {
            return;
        }

        // Whatever the page answers today, the committed checklist is one of the two forms: a verification date, or "not verified" with the HTTP status.
        Assert.True(_context.Status > 0, "The request returned no status.");
        Assert.Empty(DocumentChecks.ChecklistSource(DevpostCommitted.Checklist()));
    }

    [Then(@"^the Source and verification section contains ""HTTP 403"" and ""not verified"" and asks the user to paste the Rules tab text$")]
    public void ThenTheSectionRecordsThe403()
    {
        string section = string.Join('\n', _context.Section);

        Assert.Equal(403, _context.Status);
        Assert.Contains("HTTP 403", section, StringComparison.Ordinal);
        Assert.Contains("not verified", section, StringComparison.Ordinal);
        Assert.Empty(DocumentChecks.SourceSectionMatchesStatus(_context.Section, 403));

        // The check bites: a section that claims a verification for a 403 answer is rejected.
        Assert.NotEmpty(DocumentChecks.SourceSectionMatchesStatus(["- Verified: 2026-10-26."], 403));
        Assert.NotEmpty(DocumentChecks.SourceSectionMatchesStatus(["- not verified: HTTP 403."], 403));
    }
}