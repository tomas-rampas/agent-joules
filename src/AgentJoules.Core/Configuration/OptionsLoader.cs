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
        ArgumentNullException.ThrowIfNull(root);
        IConfigurationSection section = root.GetSection(AgentJoulesOptions.SectionName);

        List<string> shape = ConfigurationShape.ShapeProblems(section);
        if (shape.Count > 0)
        {
            throw new ConfigurationException(string.Join('\n', shape));
        }

        try
        {
            // Get returns null when the section has no keys at all. The unknown-key switch is a backstop: the shape check has named them already.
            return section.Get<AgentJoulesOptions>(binder => binder.ErrorOnUnknownConfiguration = true) ?? new AgentJoulesOptions();
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