using AgentJoules.Core.Cli;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace AgentJoules.Core.Configuration;

/// <summary>Binds <see cref="AgentJoulesOptions"/> from configuration and validates it, failing with exit code 2.</summary>
/// <remarks>
/// This is the host-free path, used by tests and by code that runs before the host exists. AJ-034 registers the same
/// <see cref="FlatSectionsValidator"/> and <see cref="CrossFieldValidator"/> in the host with <c>ValidateOnStart</c> and maps
/// <see cref="OptionsValidationException"/> to exit code 2.
/// </remarks>
public static class OptionsLoader
{
    /// <summary>Binds the <c>AgentJoules</c> section of <paramref name="root"/> through the generated binder; an absent key keeps the default of its property.</summary>
    /// <remarks>
    /// A key the options do not have, a key that is set but empty, and a value of the wrong shape or type are rejected, so a typo cannot
    /// silently keep a default. No message holds a configured value and no exception is chained: the binder quotes the value in its own
    /// message, and a value can be a key.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="root"/> is null.</exception>
    /// <exception cref="ConfigurationException">
    /// A key is unknown or empty, a value is given for a section or a section for a value, or a value cannot be converted to the type
    /// of its key (for example text for <c>Gpu:Index</c>). The message names the keys and never shows a value.
    /// </exception>
    public static AgentJoulesOptions Bind(IConfiguration root)
    {
        var options = new AgentJoulesOptions();
        Bind(root, options);
        return options;
    }

    /// <summary>Binds the <c>AgentJoules</c> section of <paramref name="root"/> into <paramref name="target"/> through the generated binder, whatever sections the options have; a key that is absent leaves the value of its property.</summary>
    /// <remarks>The host fills its options object through this overload, so a section added to <see cref="AgentJoulesOptions"/> reaches the CLI without a second list to keep in step. The checks and messages are those of <see cref="Bind(IConfiguration)"/>.</remarks>
    /// <param name="root">The configuration that holds the <c>AgentJoules</c> section.</param>
    /// <param name="target">The options object to fill.</param>
    /// <exception cref="ArgumentNullException"><paramref name="root"/> or <paramref name="target"/> is null.</exception>
    /// <exception cref="ConfigurationException">As for <see cref="Bind(IConfiguration)"/>.</exception>
    public static void Bind(IConfiguration root, AgentJoulesOptions target)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(target);
        IConfigurationSection section = root.GetSection(AgentJoulesOptions.SectionName);

        List<string> shape = ConfigurationShape.ShapeProblems(section);
        if (shape.Count > 0)
        {
            throw new ConfigurationException(string.Join('\n', shape));
        }

        // An instant without an offset would be bound as local time: refused before the bind (CloudGpu:TierStartUtc), and every other type problem is named with it.
        if (ConfigurationShape.InstantProblems(section).Count > 0)
        {
            throw new ConfigurationException(string.Join('\n', ConfigurationShape.TypeProblems(section)));
        }

        try
        {
            // The unknown-key switch is a backstop: the shape check has named them already.
            section.Bind(target, binder => binder.ErrorOnUnknownConfiguration = true);
        }
        catch (InvalidOperationException)
        {
            // The binder's message quotes the value, so it is dropped and the offending keys are found again.
            List<string> types = ConfigurationShape.TypeProblems(section);
            throw new ConfigurationException(
                types.Count > 0
                    ? string.Join('\n', types)
                    : $"The {AgentJoulesOptions.SectionName} configuration section could not be bound; check its keys and values");
        }
    }

    /// <summary>Runs <see cref="FlatSectionsValidator"/> and then <see cref="CrossFieldValidator"/>, both even when the first fails.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="ConfigurationException">
    /// Any rule fails. The message holds every failure, one per line, with a failure that both validators report once.
    /// </exception>
    public static void ValidateOrThrow(AgentJoulesOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        string[] failures =
        [
            .. Failures(new FlatSectionsValidator().Validate(Options.DefaultName, options)),
            .. Failures(new CrossFieldValidator().Validate(Options.DefaultName, options)),
        ];

        if (failures.Length > 0)
        {
            throw new ConfigurationException(string.Join('\n', failures.Distinct(StringComparer.Ordinal)));
        }
    }

    /// <summary>Binds <paramref name="root"/> and validates the result.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="root"/> is null.</exception>
    /// <exception cref="ConfigurationException">A value cannot be converted, or any rule fails.</exception>
    public static AgentJoulesOptions Load(IConfiguration root)
    {
        AgentJoulesOptions options = Bind(root);
        ValidateOrThrow(options);
        return options;
    }

    private static IEnumerable<string> Failures(ValidateOptionsResult result) => result.Failures ?? [];
}