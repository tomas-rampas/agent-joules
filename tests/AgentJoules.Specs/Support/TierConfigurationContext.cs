using System.Text.Json;
using System.Text.Json.Nodes;

using AgentJoules.Core.Configuration;
using AgentJoules.TestSupport;

using Microsoft.Extensions.Configuration;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The state of one AJ-043 scenario (bind and validate the tier configuration), handed to the step class by Reqnroll context injection. It holds
/// the in-memory overrides that are layered over the committed file (the stand-in for the <c>AGENTJOULES__*</c> variables, so no process
/// environment variable is ever set), the options bound from them, and the temporary workspace whose edited copy of the file the CLI reads.
/// </summary>
public sealed class TierConfigurationContext : IDisposable
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>The in-memory overrides, as full configuration keys, layered over the committed file in order; a later pair wins.</summary>
    public List<(string Key, string Value)> Overrides { get; } = [];

    /// <summary>Sets an override; a key that is already set (configuration keys are case-insensitive, and one in-memory provider holds one value per key) is replaced.</summary>
    /// <param name="key">The full configuration key.</param>
    /// <param name="value">The value.</param>
    public void SetOverride(string key, string value)
    {
        Overrides.RemoveAll(pair => string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase));
        Overrides.Add((key, value));
    }

    /// <summary>The options bound and validated from the committed file and the overrides, or null before a step bound them.</summary>
    public AgentJoulesOptions? Options { get; set; }

    /// <summary>The workspace that holds the edited copy of the file, or null when the scenario does not start the CLI.</summary>
    public TempWorkspace? Workspace { get; private set; }

    /// <summary>What the CLI run of the scenario produced, or null before the CLI ran.</summary>
    public CliRunResult? Run { get; set; }

    /// <summary>The committed file with the overrides on top, the order the host uses (file first, then the environment).</summary>
    public IConfiguration CommittedConfiguration => ConfigurationFixtures.FromJsonFile(ConfigurationFixtures.AppSettingsPath(), [.. Overrides]);

    /// <summary>
    /// Writes a copy of the committed file into a new workspace after <paramref name="edit"/> changed the <c>AgentJoules</c> object; a key that the
    /// edit removes is absent from the written JSON, which an in-memory null would not achieve for a key bound from the file.
    /// </summary>
    /// <param name="edit">The change to the <c>AgentJoules</c> object of the copy.</param>
    /// <exception cref="InvalidOperationException">The committed file does not exist or holds no <c>AgentJoules</c> object, or a workspace exists already.</exception>
    public void WriteEditedCopy(Action<JsonObject> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);

        if (Workspace is not null)
        {
            throw new InvalidOperationException("The scenario already holds a workspace.");
        }

        JsonObject root = JsonNode.Parse(File.ReadAllText(ConfigurationFixtures.AppSettingsPath()))?.AsObject()
            ?? throw new InvalidOperationException("config/appsettings.json is not a JSON object.");
        JsonObject section = root[AgentJoulesOptions.SectionName]?.AsObject()
            ?? throw new InvalidOperationException("config/appsettings.json has no AgentJoules object.");

        edit(section);

        Workspace = TempWorkspace.CreateEmpty();
        Workspace.WriteConfig(root.ToJsonString(Indented));
    }

    /// <summary>Finds the object that holds the last segment of <paramref name="key"/> (a colon path under <c>AgentJoules</c>) and returns it with that segment.</summary>
    /// <param name="section">The <c>AgentJoules</c> object.</param>
    /// <param name="key">The key path, for example <c>Tiers:nvidia:ReasoningMode</c>.</param>
    /// <returns>The parent object and the name of the last segment.</returns>
    /// <exception cref="InvalidOperationException">A segment above the last one is not an object of the committed file.</exception>
    public static (JsonObject Parent, string Name) Locate(JsonObject section, string key)
    {
        ArgumentNullException.ThrowIfNull(section);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        string[] segments = key.Split(':');
        JsonObject parent = section;
        foreach (string segment in segments[..^1])
        {
            parent = parent[segment]?.AsObject() ?? throw new InvalidOperationException("The committed file has no object at " + key + ".");
        }

        return (parent, segments[^1]);
    }

    /// <inheritdoc />
    public void Dispose() => Workspace?.Dispose();
}