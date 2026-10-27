using System.Net;

using AgentJoules.Specs.Support;
using AgentJoules.TestSupport;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-137: the article citations resolve (REQ-027). The live-link scenario is gated by <see cref="IntegrationGate"/> like the AJ-131 links and reuses its code (<see cref="DocumentChecks.CheckLinksAsync"/> over the
/// addresses on the citation lines of the committed article); the dead-link scenario runs on every run against a fake handler. The check date reaches <c>docs/build-log.md</c> as a line the builder writes after the
/// live check; the step asserts that the line exists and carries a date.
/// </summary>
[Binding]
[Scope(Feature = "AJ-137 Article citations resolve")]
public sealed class ArticleLinksSteps(PriorArtLinksContext context)
{
    private const string DeadUrl = "https://article.invalid/dead-link";

    private readonly PriorArtLinksContext _context = context;

    [Given(@"^the four links of the article$")]
    public void GivenTheLinksOfTheArticle()
    {
        _context.Open();
        if (_context.IsActive)
        {
            _context.Urls = DocumentChecks.ArticleLinks(RepositoryRoot.ReadAllText("content/article.md"));
            Assert.True(_context.Urls.Count >= 3, "The article holds fewer than three distinct citation links.");
        }
    }

    [Given(@"^a cited link that answers HTTP 404$")]
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

    [Then(@"^each answers HTTP 200 and the date is recorded in docs/build-log\.md$")]
    public void ThenEachAnswers200()
    {
        if (!_context.IsActive)
        {
            return;
        }

        LinkCheckResult result = _context.Result ?? throw new InvalidOperationException("The links were not requested.");
        Assert.True(result.Verified, string.Join("; ", result.Failures));
        Assert.Null(DocumentChecks.ArticleLinksLogged(RepositoryRoot.ReadAllText("docs/build-log.md")));
    }

    [Then(@"^it fails naming the URL$")]
    public void ThenItFailsNamingTheUrl()
    {
        LinkCheckResult result = _context.Result ?? throw new InvalidOperationException("The check did not run.");
        string failure = Assert.Single(result.Failures);

        Assert.Contains(DeadUrl, failure, StringComparison.Ordinal);
        Assert.Contains("404", failure, StringComparison.Ordinal);
        Assert.False(result.Verified);
    }
}