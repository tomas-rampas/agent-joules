using AgentJoules.Core.Cli;
using AgentJoules.Specs.Support;
using AgentJoules.TestSupport;

using Reqnroll;

using Xunit;
using Xunit.Sdk;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-070: load a price table that cannot represent a zero price. Scoped to the feature because sentences such as "the exit code is 2" are reused by
/// other stories with other meanings. The state lives in <see cref="PriceTableContext"/>. The loader runs for real over a file written to a temporary
/// folder; "exit code" and "output" are the exit code and the message of the <see cref="ConfigurationException"/> the loader throws.
/// </summary>
[Binding]
[Scope(Feature = "AJ-070 Load a price table that cannot represent a zero price")]
public sealed class PriceTableSteps(PriceTableContext context)
{
    private readonly PriceTableContext _context = context;

    private ConfigurationException Failure => _context.Failure ?? throw new XunitException("The loader did not fail.");

    [Given(@"^a price file with one nebius row that has both prices, a source URL and a retrieval date$")]
    public void GivenAFileWithACompleteRow() => _context.WriteRows(PriceFileFixtures.Row());

    [Given(@"^a price file with one nebius row$")]
    public void GivenAFileWithOneRow() => _context.WriteRows(PriceFileFixtures.Row());

    [Given(@"^a price file whose only row has no sourceUrl$")]
    public void GivenAFileWithARowWithoutSourceUrl() => _context.WriteRows(PriceFileFixtures.Row(sourceUrl: null));

    [Given(@"^a price file whose only row has inputUsdPer1M 0$")]
    public void GivenAFileWithAZeroPrice() => _context.WriteRows(PriceFileFixtures.Row(inputUsdPer1M: "0"));

    [Given(@"^a price file with two rows for the same tier and model ID$")]
    public void GivenAFileWithTwoRowsOfOneKey() => _context.WriteRows(PriceFileFixtures.Row(), PriceFileFixtures.Row());

    [When(@"^the price table is loaded$")]
    public void WhenTheTableIsLoaded() => _context.Load();

    [When(@"^the price table is loaded and ""([^""]*)"" with that model ID is looked up$")]
    public void WhenTheTableIsLoadedAndTheModelIsLookedUp(string tier) => _context.LoadAndFind(tier, PriceTableContext.ModelId);

    [When(@"^""([^""]*)"" with another model ID is looked up$")]
    public void WhenAnotherModelIsLookedUp(string tier) => _context.LoadAndFind(tier, "vendor/another-model");

    [Then(@"^the row is returned$")]
    public void ThenTheRowIsReturned()
    {
        Assert.Null(_context.Failure);
        Assert.NotNull(_context.Found);
        Assert.Equal(PriceTableContext.ModelId, _context.Found.ModelId);
        Assert.Equal(0.06, _context.Found.InputUsdPer1M);
        Assert.Equal(0.24, _context.Found.OutputUsdPer1M);
    }

    [Then(@"^the lookup returns no row$")]
    public void ThenTheLookupReturnsNoRow()
    {
        Assert.Null(_context.Failure);
        Assert.True(_context.LookedUp, "No lookup ran.");
        Assert.Null(_context.Found);
    }

    [Then(@"^the exit code is 2$")]
    public void ThenTheExitCodeIsTwo()
    {
        Assert.Equal(ExitCode.InvalidArguments, Failure.ExitCode);
        Assert.Equal(2, (int)Failure.ExitCode);
    }

    [Then(@"^the output names row (\d+) and the field ""([^""]*)""$")]
    public void ThenTheOutputNamesRowAndField(int row, string field)
    {
        Assert.Contains($"row {row}", Failure.Message, StringComparison.Ordinal);
        Assert.Contains(field, Failure.Message, StringComparison.Ordinal);
    }

    [Then(@"^the output names the field ""([^""]*)""$")]
    public void ThenTheOutputNamesTheField(string field) => Assert.Contains(field, Failure.Message, StringComparison.Ordinal);
}