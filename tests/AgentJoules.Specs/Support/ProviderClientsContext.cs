using System.Text.Json;
using System.Text.Json.Nodes;

using AgentJoules.Core.Configuration;
using AgentJoules.Core.Runs;
using AgentJoules.TestSupport;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The state of one AJ-044 scenario (provider chat clients per tier), handed to the step class by Reqnroll context injection. It holds the four-tier
/// options the scenario edits, the fake endpoint of the tier under test when the scenario scripts one, the container built from them, what the
/// requests recorded, and the temporary workspace of the scenarios that start the CLI. Keys are runtime-built fakes in an in-memory configuration; no
/// process environment variable is read or set and no request leaves the process.
/// </summary>
public sealed class ProviderClientsContext : IDisposable
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private ProviderClientHost? _host;

    /// <summary>The name of the extra field the extra-body scenario sets.</summary>
    public const string ExtraFieldName = "extra_field";

    /// <summary>The value of the extra field.</summary>
    public const string ExtraFieldValue = "extra-value";

    /// <summary>The options of the four tiers with fake https base URLs; steps edit them before the container is built.</summary>
    public AgentJoulesOptions Options { get; } = ProviderClientFixtures.FourTiers();

    /// <summary>The name of the tier the scenario is about.</summary>
    public string TierName { get; set; } = "nvidia";

    /// <summary>The <c>maxOutputTokens</c> of the task the scenario sends.</summary>
    public int MaxOutputTokens { get; set; } = 512;

    /// <summary>The fake endpoint the scenario scripted for the tier under test, or null for a default one.</summary>
    public FakeHttpMessageHandler? Endpoint { get; set; }

    /// <summary>The model ID that the wire carried for an agent, by agent name.</summary>
    public Dictionary<string, string> ModelByAgent { get; } = new(StringComparer.Ordinal);

    /// <summary>The failure of the chat request of a scenario that expects one, or null.</summary>
    public Exception? Thrown { get; set; }

    /// <summary>The <c>ExtraBodyJson</c> the scenario put on the tier, or null.</summary>
    public string? ExtraBodyJson { get; set; }

    /// <summary>The workspace that holds the edited copy of the committed file, or null when the scenario does not start the CLI.</summary>
    public TempWorkspace? Workspace { get; private set; }

    /// <summary>What the CLI run produced, or null before the CLI ran.</summary>
    public CliRunResult? Run { get; set; }

    /// <summary>The tier the scenario is about.</summary>
    public Tier TierValue => Enum.Parse<Tier>(TierName, ignoreCase: true);

    /// <summary>The options of the tier the scenario is about.</summary>
    public TierOptions TierOptionsUnderTest => Options.Tiers[TierName];

    /// <summary>The container built from the options, created on first use so that every step before it can still edit them.</summary>
    public ProviderClientHost Host => _host ??= ProviderClientFixtures.Build(
        Options,
        handlers: Endpoint is null ? null : new Dictionary<Tier, FakeHttpMessageHandler> { [TierValue] = Endpoint });

    /// <summary>The fake endpoint of the tier under test.</summary>
    public FakeHttpMessageHandler Fake => Host.Handler(TierValue);

    /// <summary>
    /// Writes a copy of the committed file into a new workspace with <c>Tiers:&lt;TierName&gt;:ExtraBodyJson</c> set to <see cref="ExtraBodyJson"/>, the
    /// file the CLI of the scenario reads.
    /// </summary>
    /// <exception cref="InvalidOperationException">The committed file has no such tier, or a workspace exists already.</exception>
    public void WriteEditedCopy()
    {
        if (Workspace is not null)
        {
            throw new InvalidOperationException("The scenario already holds a workspace.");
        }

        JsonObject root = JsonNode.Parse(File.ReadAllText(ConfigurationFixtures.AppSettingsPath()))?.AsObject()
            ?? throw new InvalidOperationException("config/appsettings.json is not a JSON object.");
        JsonObject tier = root[AgentJoulesOptions.SectionName]?["Tiers"]?[TierName]?.AsObject()
            ?? throw new InvalidOperationException("config/appsettings.json has no tier " + TierName + ".");
        tier[nameof(TierOptions.ExtraBodyJson)] = ExtraBodyJson;

        Workspace = TempWorkspace.CreateEmpty();
        Workspace.WriteConfig(root.ToJsonString(Indented));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _host?.Dispose();
        Workspace?.Dispose();
    }
}