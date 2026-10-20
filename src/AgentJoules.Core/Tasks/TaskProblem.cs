namespace AgentJoules.Core.Tasks;

/// <summary>Which rule of <see cref="TaskDefinition"/> a value broke.</summary>
public enum TaskProblem
{
    /// <summary>The name is empty, too long, starts with a hyphen or holds a character other than a lowercase letter, a digit or a hyphen.</summary>
    InvalidName = 1,

    /// <summary>The prompt is blank or holds a <c>{{</c> or <c>}}</c> placeholder token.</summary>
    InvalidPrompt = 2,

    /// <summary>The prompt is longer than <see cref="TaskDefinition.MaxPromptLength"/> characters.</summary>
    PromptTooLong = 3,

    /// <summary>The output limit is less than 1.</summary>
    OutputLimitTooSmall = 4,

    /// <summary>The output limit is more than <see cref="TaskDefinition.MaxOutputTokensLimit"/>.</summary>
    OutputLimitTooLarge = 5,
}