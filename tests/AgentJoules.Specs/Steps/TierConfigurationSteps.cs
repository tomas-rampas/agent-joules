using System.Text.Json.Nodes;

using AgentJoules.Cli;
using AgentJoules.Core.Configuration;
using AgentJoules.Specs.Support;
using AgentJoules.TestSupport;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-043: bind and validate the tier configuration. Scoped to the feature because sentences such as "the exit code is 2" and "when any command
/// starts" are reused by other stories with other meanings. The state lives in <see cref="TierConfigurationContext"/>. Every override goes through
/// an in-memory configuration, so no step sets a process environment variable; the scenarios that start a command run the real CLI in process
/// (<c>probe</c> with the unavailable GPU factory of the harness, which exits 0 on a valid configuration) over an edited copy of the committed file.
/// </summary>
[Binding]
[Scope(Feature = "AJ-043 Bind and validate tier configuration")]
public sealed class TierConfigurationSteps(TierConfigurationContext context)
{
    private readonly TierConfigurationContext _context = context;

    private AgentJoulesOptions Options => _context.Options ?? throw new InvalidOperationException("The options were not bound.");

    private CliRunResult Run => _context.Run ?? throw new InvalidOperationException("No command was started.");

    [Given(@"^the committed config/appsettings\.json$")]
    public static void GivenTheCommittedFile() => Assert.True(File.Exists(ConfigurationFixtures.AppSettingsPath()), "config/appsettings.json does not exist");

    [Given(@"^the committed config with nvidia ModelId ""([^""]*)""$")]
    public void GivenTheCommittedConfigWithAnNvidiaModelId(string modelId)
    {
        GivenTheCommittedFile();
        _context.SetOverride(AgentJoulesOptions.SectionName + ":Tiers:nvidia:ModelId", modelId);
    }

    [Given(@"^the config with ([A-Za-z:]+) set to ""([^""]*)""$")]
    public void GivenTheConfigWithAKeySet(string key, string value) =>
        _context.WriteEditedCopy(section =>
        {
            (JsonObject parent, string name) = TierConfigurationContext.Locate(section, key);
            parent[name] = value;
        });

    [Given(@"^the config with ([A-Za-z:]+) removed$")]
    public void GivenTheConfigWithAKeyRemoved(string key) =>
        _context.WriteEditedCopy(section =>
        {
            (JsonObject parent, string name) = TierConfigurationContext.Locate(section, key);
            Assert.True(parent.Remove(name), key + " is not in the committed file");
        });

    [When(@"^the options are bound and validated$")]
    public void WhenTheOptionsAreBoundAndValidated() => _context.Options = OptionsLoader.Load(_context.CommittedConfiguration);

    [When(@"^(AGENTJOULES__\S+) is set to ""([^""]*)"" in the configuration override$")]
    public void WhenAnEnvironmentStyleKeyIsSet(string name, string value)
    {
        // The file and the first override bind first, so the scenario proves that the key replaces a value that was bound before.
        string? before = OptionsLoader.Load(_context.CommittedConfiguration).Tiers["nvidia"].ModelId;

        _context.SetOverride(ConfigurationFixtures.NormaliseEnvironmentKey(name), value);
        _context.Options = OptionsLoader.Load(_context.CommittedConfiguration);
        Assert.NotEqual(before, _context.Options.Tiers["nvidia"].ModelId);
    }

    [When(@"^any command starts$")]
    public async Task WhenAnyCommandStarts()
    {
        TempWorkspace workspace = _context.Workspace ?? throw new InvalidOperationException("The scenario wrote no configuration file.");
        CliHostOverrides overrides = CliHarness.Overrides(_context.Overrides.Select(pair => (pair.Key, (string?)pair.Value))) with { WorkingDirectory = workspace.Root };

        _context.Run = await CliHarness.RunAsync("probe", overrides);
    }

    [Then(@"^tiers ""([^""]*)"", ""([^""]*)"", ""([^""]*)"" and ""([^""]*)"" each have a BaseUrl and a ModelId$")]
    public void ThenTheTiersHaveABaseUrlAndAModelId(string first, string second, string third, string fourth)
    {
        foreach (string name in new[] { first, second, third, fourth })
        {
            Assert.True(Options.Tiers.TryGetValue(name, out TierOptions? tier), "Tiers:" + name + " was not bound");
            Assert.False(string.IsNullOrWhiteSpace(tier.BaseUrl), "Tiers:" + name + ":BaseUrl is empty");
            Assert.False(string.IsNullOrWhiteSpace(tier.ModelId), "Tiers:" + name + ":ModelId is empty");
        }
    }

    [Then(@"^tier ""([^""]*)"" is disabled$")]
    public void ThenTheTierIsDisabled(string name) => Assert.False(Options.Tiers[name].Enabled);

    [Then(@"^the bound nvidia ModelId is ""([^""]*)""$")]
    public void ThenTheBoundModelIdIs(string expected)
    {
        Assert.Equal(expected, Options.Tiers["nvidia"].ModelId);

        // The rest of the file is untouched: the override replaced one value, it did not replace the file.
        Assert.Equal(4, Options.Tiers.Count);
        Assert.False(string.IsNullOrWhiteSpace(Options.Tiers["nvidia"].BaseUrl));
    }

    [Then(@"^the exit code is 2$")]
    public void ThenTheExitCodeIsTwo()
    {
        Assert.Equal(2, Run.ExitCode);
        Assert.Equal(string.Empty, Run.Stdout);
    }

    [Then(@"^the output names ""([^""]*)""$")]
    public void ThenTheOutputNamesTheKey(string key) =>
        Assert.Contains(Run.ErrorLines, line => line.Contains(key, StringComparison.Ordinal));
}