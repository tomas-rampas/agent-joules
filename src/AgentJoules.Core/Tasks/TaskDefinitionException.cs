namespace AgentJoules.Core.Tasks;

/// <summary>
/// A value given to <see cref="TaskDefinition"/> breaks one of its rules. It is an <see cref="ArgumentException"/> that names the parameter, plus the rule
/// as a <see cref="TaskProblem"/>, so a caller that reads a document can report a fixed phrase without parsing a message. The message never holds the value.
/// </summary>
/// <param name="problem">The rule that was broken.</param>
/// <param name="message">A message that does not repeat the value.</param>
/// <param name="parameterName">The name of the parameter.</param>
public sealed class TaskDefinitionException(TaskProblem problem, string message, string parameterName) : ArgumentException(message, parameterName)
{
    /// <summary>The rule that was broken.</summary>
    public TaskProblem Problem { get; } = problem;
}