using System.Text.Json.Serialization;

namespace AgentJoules.Core.Tasks;

/// <summary>
/// One task of the fixed task set (REQ-012): the name that <c>--task</c> selects, the self-contained prompt that is sent to the agents, and the output cap
/// every call of the task carries. It is read from a file in <c>tasks/</c> through <see cref="CoreJsonContext"/>, so its constructor is the one place
/// that decides what a valid task is; a member that is not one of the three is refused (design 3.4, 3.5). A refusal names the property and never holds its value.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TaskDefinition
{
    /// <summary>The most characters a task name may have.</summary>
    public const int MaxNameLength = 40;

    /// <summary>The most characters a prompt may have: far above the longest committed prompt, far below what could fill the memory of a run.</summary>
    public const int MaxPromptLength = 16384;

    /// <summary>The largest output cap a task may ask for: above the largest committed cap (768) with room, below the limit at which a provider would reject the request anyway.</summary>
    public const int MaxOutputTokensLimit = 32768;

    private const string PlaceholderOpen = "{{";
    private const string PlaceholderClose = "}}";

    /// <summary>Creates a task.</summary>
    /// <param name="name">1 to 40 characters: lowercase ASCII letters, digits and hyphens, not starting with a hyphen.</param>
    /// <param name="prompt">The prompt text: not blank, at most 16384 characters, and without a <c>{{</c> or <c>}}</c> placeholder token, because the prompt is sent as it is.</param>
    /// <param name="maxOutputTokens">The output cap of every call of the task; from 1 to 32768.</param>
    /// <exception cref="TaskDefinitionException">A value breaks its rule; <see cref="ArgumentException.ParamName"/> names the parameter, <see cref="TaskDefinitionException.Problem"/> the rule, and the message does not repeat the value.</exception>
    [JsonConstructor]
    public TaskDefinition(string name, string prompt, int maxOutputTokens)
    {
        if (!IsValidName(name))
        {
            throw new TaskDefinitionException(TaskProblem.InvalidName, $"A task name must be 1 to {MaxNameLength} characters of lowercase letters, digits and hyphens and must not start with a hyphen.", nameof(name));
        }

        if (string.IsNullOrWhiteSpace(prompt)
            || prompt.Contains(PlaceholderOpen, StringComparison.Ordinal)
            || prompt.Contains(PlaceholderClose, StringComparison.Ordinal))
        {
            throw new TaskDefinitionException(TaskProblem.InvalidPrompt, $"A task prompt must not be blank and must not hold a {PlaceholderOpen} or {PlaceholderClose} placeholder.", nameof(prompt));
        }

        if (prompt.Length > MaxPromptLength)
        {
            throw new TaskDefinitionException(TaskProblem.PromptTooLong, $"A task prompt must be at most {MaxPromptLength} characters.", nameof(prompt));
        }

        if (maxOutputTokens < 1)
        {
            throw new TaskDefinitionException(TaskProblem.OutputLimitTooSmall, "The output limit of a task must be at least 1.", nameof(maxOutputTokens));
        }

        if (maxOutputTokens > MaxOutputTokensLimit)
        {
            throw new TaskDefinitionException(TaskProblem.OutputLimitTooLarge, $"The output limit of a task must be at most {MaxOutputTokensLimit}.", nameof(maxOutputTokens));
        }

        Name = name;
        Prompt = prompt;
        MaxOutputTokens = maxOutputTokens;
    }

    /// <summary>The name that <c>--task</c> selects; unique in the task set.</summary>
    public string Name { get; }

    /// <summary>The self-contained prompt that every agent call of the task starts from.</summary>
    public string Prompt { get; }

    /// <summary>The output cap that every call of the task carries, so a run of the task is comparable across tiers.</summary>
    public int MaxOutputTokens { get; }

    private static bool IsValidName(string? name) =>
        !string.IsNullOrEmpty(name)
        && name.Length <= MaxNameLength
        && IsLowerLetterOrDigit(name[0])
        && name.All(character => IsLowerLetterOrDigit(character) || character == '-');

    private static bool IsLowerLetterOrDigit(char character) => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character);
}