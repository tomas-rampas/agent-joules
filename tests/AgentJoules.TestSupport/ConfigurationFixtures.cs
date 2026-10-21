using AgentJoules.Core.Configuration;

using Microsoft.Extensions.Configuration;

namespace AgentJoules.TestSupport;

/// <summary>
/// Isolated configuration for the options tests (design 6.2): every key goes through an in-memory provider, so no test ever sets a process
/// environment variable and no two tests can see each other's keys.
/// </summary>
public static class ConfigurationFixtures
{
    private const string SolutionFile = "AgentJoules.slnx";
    private const string EnvironmentSeparator = "__";

    /// <summary>The path of the shipped <c>config/appsettings.json</c>; the file itself may not exist yet.</summary>
    public static string AppSettingsPath() => Path.Combine(RepositoryRoot(), "config", "appsettings.json");

    /// <summary>The options the shipped <c>config/appsettings.json</c> binds to. Each call loads the file again and returns a new instance, so a test that changes the options cannot affect another test.</summary>
    /// <returns>A new options instance.</returns>
    public static AgentJoulesOptions ShippedOptions() => OptionsLoader.Load(FromJsonFile(AppSettingsPath()));

    /// <summary>Every model ID the shipped configuration names: the model of each tier and the judge model, each once.</summary>
    /// <returns>The model IDs.</returns>
    public static string[] ShippedModelIds()
    {
        AgentJoulesOptions shipped = ShippedOptions();
        return [.. shipped.Tiers.Values.Select(static tier => tier.ModelId).Append(shipped.Judge.ModelId).OfType<string>().Distinct(StringComparer.Ordinal)];
    }

    /// <summary>Builds a configuration that holds only <paramref name="pairs"/>; a later pair with the same key wins.</summary>
    public static IConfiguration InMemory(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(pair => new KeyValuePair<string, string?>(pair.Key, pair.Value)))
            .Build();

    /// <summary>
    /// Builds a configuration from the JSON file at <paramref name="path"/> with <paramref name="overrides"/> layered on top, the order the host
    /// uses (file first, then the environment); the file must exist.
    /// </summary>
    public static IConfiguration FromJsonFile(string path, params (string Key, string Value)[] overrides) =>
        new ConfigurationBuilder()
            .AddJsonFile(path, optional: false, reloadOnChange: false)
            .AddInMemoryCollection(overrides.Select(pair => new KeyValuePair<string, string?>(pair.Key, pair.Value)))
            .Build();

    /// <summary>
    /// Turns an environment variable name into the configuration key the environment provider would produce: a double underscore becomes a
    /// colon, so <c>AGENTJOULES__Gpu__Index</c> becomes <c>AGENTJOULES:Gpu:Index</c>. Configuration keys are case-insensitive, so the key binds
    /// to the <c>AgentJoules</c> section.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null or blank.</exception>
    public static string NormaliseEnvironmentKey(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return name.Replace(EnvironmentSeparator, ConfigurationPath.KeyDelimiter, StringComparison.Ordinal);
    }

    /// <summary>The folder that holds <c>AgentJoules.slnx</c>, found by walking up from the test assembly.</summary>
    /// <exception cref="InvalidOperationException">No parent folder holds the solution file.</exception>
    public static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, SolutionFile)))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException(SolutionFile + " was not found above " + AppContext.BaseDirectory);
    }
}