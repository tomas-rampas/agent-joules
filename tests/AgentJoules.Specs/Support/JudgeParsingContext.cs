using AgentJoules.Core.Quality;
using AgentJoules.Core.Security;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The state of one AJ-073 scenario (parse a judge reply into a score), handed to the step class by Reqnroll context injection. The Given steps set the reply text; the When step runs the real
/// <see cref="JudgeScoreParser"/> over it and keeps the result. There is no HTTP, no judge model and no file.
/// </summary>
public sealed class JudgeParsingContext
{
    /// <summary>The judge reply of the scenario; null until a Given step sets it.</summary>
    public string? Reply { get; set; }

    /// <summary>The result of the single parse of a scenario, or null before it ran.</summary>
    public JudgeParseResult? Result { get; private set; }

    /// <summary>Parses <see cref="Reply"/> with <see cref="JudgeScoreParser.Parse(string, SecretRedactor)"/> with a redactor that has no key registered, and keeps the result.</summary>
    /// <returns>The result.</returns>
    /// <exception cref="InvalidOperationException">No Given step set the reply.</exception>
    public JudgeParseResult Parse()
    {
        Result = JudgeScoreParser.Parse(Reply ?? throw new InvalidOperationException("No judge reply was set."), new SecretRedactor());

        return Result;
    }
}