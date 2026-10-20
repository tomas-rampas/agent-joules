using AgentJoules.Core.Security;

using Microsoft.Extensions.Configuration;

namespace AgentJoules.Cli.Hosting;

/// <summary>
/// Registers the values of the three key variables with the <see cref="SecretRedactor"/> as soon as a source can name them. The sources that need
/// no file (user secrets, the process environment, the in-memory override) are registered at the top of <c>RunAsync</c>, before the arguments
/// are parsed, so a parse error, a <c>--config</c> path or a <c>--debug-throw-on-worker</c> message that echoes a key is masked; the host
/// registers again once the JSON file is read. Registration is idempotent and a value that cannot be registered warns once per variable.
/// </summary>
/// <remarks>
/// Known limitation: a malformed user-secrets file cannot be read, so the keys it holds are not registered early; a key that lives only in that
/// broken file and is also typed on the command line would be echoed by the parse error. The host then reports the file (exit code 2) and never
/// the key.
/// </remarks>
internal sealed class KeyRegistry(SecretRedactor redactor)
{
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
        if (overrides is null || overrides.IncludeUserSecrets)
        {
            RegisterFrom(static builder => builder.AddUserSecrets(typeof(CliApplication).Assembly, optional: true), error);
        }

        if (overrides is null || overrides.IncludeProcessEnvironment)
        {
            RegisterFrom(static builder => builder.AddEnvironmentVariables(), error);
        }

        if (overrides?.Configuration is { } memory)
        {
            RegisterFrom(builder => builder.AddInMemoryCollection(memory), error);
        }
    }

    /// <summary>Registers the keys the built host configuration holds (every source, including the JSON file).</summary>
    internal void Register(IConfiguration configuration, TextWriter error)
    {
        foreach (string variable in KeyVariables)
        {
            string? value = configuration[variable];
            if (!string.IsNullOrEmpty(value) && !Redactor.Register(value) && _warned.Add(variable))
            {
                error.WriteLine($"warning: {variable} is set but could not be registered for redaction");
            }
        }
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