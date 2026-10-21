using AgentJoules.Core.Configuration;

namespace AgentJoules.Workflow.Runs;

/// <summary>
/// Formats the one message of a missing-key failure (EDGE-011): every missing variable in one line, and the user-secrets alternative with the placeholders <c>&lt;NAME&gt;</c> and
/// <c>&lt;value&gt;</c> instead of a value. The message is a single line on purpose: the error line of the command line folds line breaks into spaces anyway, so one line is what the
/// user reads, and one write is what the redacting writer sees.
/// </summary>
public static class MissingKeyMessage
{
    /// <summary>The user-secrets command shown to the user, with the placeholders <c>&lt;NAME&gt;</c> and <c>&lt;value&gt;</c> exactly as written here.</summary>
    public const string UserSecretsAlternative = "dotnet user-secrets set \"<NAME>\" \"<value>\" --project src/AgentJoules.Cli";

    /// <summary>The text that stands for a name that is not shaped like a variable name.</summary>
    public const string HiddenName = "(name not shown)";

    /// <summary>
    /// Formats one single-line message that names every variable of <paramref name="variables"/> in order, and shows <see cref="UserSecretsAlternative"/>. A name that is not shaped like a
    /// variable name (<see cref="VariableNames.IsValid(string?)"/>) might be a key typed into the wrong setting, so it is replaced by <c>(name not shown)</c>.
    /// </summary>
    /// <param name="variables">The names of the missing variables, at least one.</param>
    /// <returns>The message; it holds names and the alternative only, never a value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="variables"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="variables"/> is empty.</exception>
    public static string Format(IReadOnlyList<string> variables)
    {
        ArgumentNullException.ThrowIfNull(variables);
        if (variables.Count == 0)
        {
            throw new ArgumentException("At least one missing variable is needed.", nameof(variables));
        }

        string names = string.Join(", ", variables.Select(static variable => VariableNames.IsValid(variable) ? variable : HiddenName));
        return $"Required variables are not set: {names}. Set each one in the environment, or store it with: {UserSecretsAlternative} (use each name above for <NAME>)";
    }
}