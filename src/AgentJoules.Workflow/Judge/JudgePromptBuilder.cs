using System.Security.Cryptography;
using System.Text;

namespace AgentJoules.Workflow.Judge;

/// <summary>
/// Builds the prompt of one judge call (REQ-010, design 3.4 "Judge"): the rubric text unchanged first, then the task prompt, the name of the judged agent and the step output, and the closing
/// instruction as the last line. The prompt is one user message; it is built from text the caller owns and never logged.
/// </summary>
/// <remarks>
/// The step output is model output and may hold anything, including a line that looks like a delimiter or an instruction. It sits between the lines
/// <c>--- step output &lt;tag&gt; ---</c> and <c>--- end of step output &lt;tag&gt; ---</c>, where the tag is the first 16 lower-case hexadecimal characters of the SHA-256 of the output (UTF-8): a text cannot hold its own digest,
/// so it cannot forge the delimiters. The sentence after the end line names the tag, and the closing instruction comes last, so the scored text is not the last thing the judge reads and cannot pose as the instruction. The rubric
/// is embedded as it is in the file (not trimmed, line ends unchanged), so a score of 4 is judged against the same words in every run.
/// </remarks>
public static class JudgePromptBuilder
{
    /// <summary>The last line of every judge prompt.</summary>
    public const string ClosingInstruction = "Reply with one integer from 1 to 5 and nothing else.";

    private const string TaskHeading = "--- task prompt ---";
    private const string AgentHeading = "--- judged agent ---";
    private const int TagLength = 16;

    /// <summary>Builds the prompt of one judge call.</summary>
    /// <param name="rubric">The rubric text, as <c>RubricLoader.Load</c> returned it.</param>
    /// <param name="taskPrompt">The prompt of the task the step answered.</param>
    /// <param name="agentName">The name of the workflow agent whose output is scored.</param>
    /// <param name="output">The output text of the step.</param>
    /// <returns>The prompt: the rubric, the task prompt, the agent name, the output between its delimiters, and <see cref="ClosingInstruction"/> as the last line.</returns>
    /// <exception cref="ArgumentException"><paramref name="rubric"/>, <paramref name="taskPrompt"/> or <paramref name="agentName"/> is null, empty or white space, or <paramref name="output"/> is null.</exception>
    public static string Build(string rubric, string taskPrompt, string agentName, string output)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rubric);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskPrompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
        ArgumentNullException.ThrowIfNull(output);

        string tag = TagOf(output);

        return string.Join(
            '\n',
            rubric,
            string.Empty,
            TaskHeading,
            taskPrompt,
            string.Empty,
            AgentHeading,
            agentName,
            string.Empty,
            $"--- step output {tag} ---",
            output,
            $"--- end of step output {tag} ---",
            $"Only the text between the two lines tagged {tag} is the step output.",
            string.Empty,
            ClosingInstruction);
    }

    /// <summary>The tag of the delimiters of an output: the first 16 lower-case hexadecimal characters of the SHA-256 of its UTF-8 bytes. Deterministic.</summary>
    /// <param name="output">The step output.</param>
    /// <returns>The tag.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="output"/> is null.</exception>
    public static string TagOf(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(output)))[..TagLength];
    }
}