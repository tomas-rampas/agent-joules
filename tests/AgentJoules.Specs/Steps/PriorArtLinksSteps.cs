using System.Net;

using AgentJoules.Specs.Support;
using AgentJoules.TestSupport;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-131: the prior-art citations resolve (REQ-025, REQ-032). The live-link scenario is gated by <see cref="IntegrationGate"/> like the other @integration features; the dead-link scenario runs on every run against a fake
/// handler. The check date reaches <c>docs/build-log.md</c> as a line the builder writes after the live check; the step asserts that the line exists and carries a date.
/// </summary>
[Binding]
[Scope(Feature = "AJ-131 Prior-art citations resolve")]
public sealed class PriorArtLinksSteps(PriorArtLinksContext context)
{
    private const string DeadUrl = "https://prior-art.invalid/dead-link";

    private readonly PriorArtLinksContext _context = context;

    [Given(@"^the links in the Prior art section$")]
    public void GivenTheLinksInThePriorArtSection()
    {
        _context.Open();
        if (_context.IsActive)
        {
            _context.Urls = DocumentChecks.PriorArtLinks(RepositoryRoot.ReadAllText("README.md"));
            Assert.True(_context.Urls.Count >= 4, "The Prior art section holds fewer than four links.");
        }
    }

    [Given(@"^a link in the Prior art section that answers HTTP 404$")]
    public void GivenADeadLink() => _context.Urls = [DeadUrl];

    [When(@"^each link is requested$")]
    public async Task WhenEachLinkIsRequested()
    {
        if (_context.IsActive)
        {
            _context.Result = await DocumentChecks.CheckLinksAsync(_context.Urls, _context.RealClient(), CancellationToken.None);
        }
    }

    [When(@"^the link check runs$")]
    public async Task WhenTheLinkCheckRuns() =>
        _context.Result = await DocumentChecks.CheckLinksAsync(_context.Urls, _context.FakeClient(HttpStatusCode.NotFound), CancellationToken.None);

    [Then(@"^each answers HTTP 200 and the check date is written to docs/build-log\.md$")]
    public void ThenEachAnswers200()
    {
        if (!_context.IsActive)
        {
            return;
        }

        LinkCheckResult result = _context.Result ?? throw new InvalidOperationException("The links were not requested.");
        Assert.True(result.Verified, string.Join("; ", result.Failures));
        Assert.Null(DocumentChecks.LinksCheckLogged(RepositoryRoot.ReadAllText("docs/build-log.md")));
    }

    [Then(@"^it fails naming the URL and the README is not marked verified$")]
    public void ThenItFailsNamingTheUrl()
    {
        LinkCheckResult result = _context.Result ?? throw new InvalidOperationException("The check did not run.");
        string failure = Assert.Single(result.Failures);

        Assert.Contains(DeadUrl, failure, StringComparison.Ordinal);
        Assert.Contains("404", failure, StringComparison.Ordinal);
        Assert.False(result.Verified);
    }
}