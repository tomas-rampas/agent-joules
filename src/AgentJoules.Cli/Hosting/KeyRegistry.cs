using AgentJoules.Core.Configuration;
using AgentJoules.Core.Security;

using Microsoft.Extensions.Configuration;

namespace AgentJoules.Cli.Hosting;

/// <summary>
/// Registers the values of the three fixed key variables and of every tier's configured <c>ApiKeyVariable</c> (EDGE-011) with the <see cref="SecretRedactor"/> as soon as a source can name them. The sources that need
/// no file (user secrets, the process environment, the in-memory override) are registered at the top of <c>RunAsync</c>, before the arguments
/// are parsed, so a parse error, a <c>--config</c> path or a <c>--debug-throw-on-worker</c> message that echoes a key is masked; the host
/// registers again once the JSON file is read, which is also the first moment a variable name that only the JSON file declares is known. Registration is idempotent and a value that cannot be registered warns once per variable.
/// </summary>
/// <remarks>
/// Known limitation: a malformed user-secrets file cannot be read, so the keys it holds are not registered early; a key that lives only in that
/// broken file and is also typed on the command line would be echoed by the parse error. The host then reports the file (exit code 2) and never
/// the key.
/// </remarks>
internal sealed class KeyRegistry(SecretRedactor redactor)
{
    private const string TiersSection = AgentJoulesOptions.SectionName + ":" + nameof(AgentJoulesOptions.Tiers);

    private static readonly string[] KeyVariables = ["NVIDIA_API_KEY", "NEBIUS_API_KEY", "NGC_API_KEY"];

    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);

    /// <summary>The redactor every writer and formatter masks with.</summary>
    internal SecretRedactor Redactor { get; } = redactor;

    /// <summary>
    /// Registers the keys the sources that need no file hold. A source that cannot be read (a malformed secrets file) is skipped here; the
    /// host reports it with exit code 2 when it builds.
    /// </summary>
    internal void RegisterEarly(CliHostOverrides? overrides, TextWriter error)
    {
        List<Action<IConfigurationBuilder>> sources = [];
        if (overrides is null || overrides.IncludeUserSecrets)
        {
            sources.Add(static builder => builder.AddUserSecrets(typeof(CliApplication).Assembly, optional: true));
        }

        if (overrides is null || overrides.IncludeProcessEnvironment)
        {
            sources.Add(static builder => builder.AddEnvironmentVariables());
        }

        if (overrides?.Configuration is { } memory)
        {
            sources.Add(builder => builder.AddInMemoryCollection(memory));
        }

        RegisterEarly(sources, error);
    }

    /// <summary>
    /// Registers the keys the given file-free <paramref name="sources"/> hold: all of them together first, so a variable named in one source and valued in another is found, then each
    /// one alone, so a source that cannot be read (the combined pass fails as a whole) does not hide the keys of the others.
    /// </summary>
    internal void RegisterEarly(IReadOnlyList<Action<IConfigurationBuilder>> sources, TextWriter error)
    {
        RegisterFrom(
            builder =>
            {
                foreach (Action<IConfigurationBuilder> source in sources)
                {
                    source(builder);
                }
            },
            error);

        if (sources.Count > 1)
        {
            foreach (Action<IConfigurationBuilder> source in sources)
            {
                RegisterFrom(source, error);
            }
        }
    }

    /// <summary>Registers the keys the built host configuration holds (every source, including the JSON file).</summary>
    internal void Register(IConfiguration configuration, TextWriter error)
    {
        foreach (string variable in KeyVariablesOf(configuration))
        {
            string? value = configuration[variable];
            if (!string.IsNullOrWhiteSpace(value) && !Redactor.Register(value) && _warned.Add(variable))
            {
                error.WriteLine($"warning: {variable} is set but could not be registered for redaction");
            }
        }
    }

    // The three fixed names, then the ApiKeyVariable of every tier the configuration declares (design 3.2: the redactor loads every ApiKeyVariable; the judge reads the variable of its
    // tier, so it has none of its own). Only a name that passes the allowlist (VariableNames.IsKeyVariable: capital letters, digits, underscores, ends in _API_KEY) is looked up, so a
    // configuration change cannot make the registry register the value of an unrelated variable and rewrite unrelated output; the validator refuses any other name at start.
    private static HashSet<string> KeyVariablesOf(IConfiguration configuration)
    {
        HashSet<string> names = new(KeyVariables, StringComparer.Ordinal);
        foreach (IConfigurationSection tier in configuration.GetSection(TiersSection).GetChildren())
        {
            if (tier[nameof(TierOptions.ApiKeyVariable)] is { } variable && VariableNames.IsKeyVariable(variable))
            {
                _ = names.Add(variable);
            }
        }

        return names;
    }

    private void RegisterFrom(Action<IConfigurationBuilder> add, TextWriter error)
    {
        try
        {
            var builder = new ConfigurationBuilder();
            add(builder);
            Register(builder.Build(), error);
        }
        catch (Exception failure) when (failure is InvalidDataException or FormatException or IOException or UnauthorizedAccessException)
        {
            // Skipped: the host names the unreadable file and exits 2.
        }
    }
}