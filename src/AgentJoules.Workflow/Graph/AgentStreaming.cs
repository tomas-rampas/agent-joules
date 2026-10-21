using System.Text;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentJoules.Workflow.Graph;

/// <summary>
/// What the four executors share: how a step talks to its agent and how the user message of each step is assembled (REQ-011, design 3.4). Every step streams, so
/// every call takes the metered streaming path and has a time to the first token on every tier. The user message is built from labelled sections; the task text,
/// the plan and the research notes go into user messages only, never into instructions, logs or exception messages.
/// </summary>
internal static class AgentStreaming
{
    /// <summary>The user message of the Planner: the labelled section <c>Task</c>.</summary>
    /// <param name="prompt">The task prompt.</param>
    /// <returns>The message text.</returns>
    internal static string PlannerMessage(string prompt) => string.Concat("Task:\n", prompt);

    /// <summary>The user message of the Researcher and of the Writer: the sections <c>Task</c> and <c>Plan</c>.</summary>
    /// <param name="prompt">The task prompt.</param>
    /// <param name="plan">The text the Planner produced.</param>
    /// <returns>The message text.</returns>
    internal static string BranchMessage(string prompt, string plan) => string.Concat("Task:\n", prompt, "\n\nPlan:\n", plan);

    /// <summary>The user message of the Reviewer: the sections <c>Task</c>, <c>Research</c> and <c>Draft</c>.</summary>
    /// <param name="prompt">The task prompt.</param>
    /// <param name="research">The text the Researcher produced.</param>
    /// <param name="draft">The text the Writer produced.</param>
    /// <returns>The message text.</returns>
    internal static string ReviewerMessage(string prompt, string research, string draft) =>
        string.Concat("Task:\n", prompt, "\n\nResearch:\n", research, "\n\nDraft:\n", draft);

    /// <summary>
    /// Streams <paramref name="agent"/> over one user message and folds the answer text of its updates into one string. The stream is consumed with
    /// <c>await foreach</c>, so it is disposed on every way out and the window and the meter gate of the call are given back.
    /// </summary>
    /// <param name="agent">The agent of the step.</param>
    /// <param name="userMessage">The text of the user message.</param>
    /// <param name="cancellationToken">The run token.</param>
    /// <returns>The answer text; reasoning text is not part of it.</returns>
    internal static async Task<string> RunAsync(AIAgent agent, string userMessage, CancellationToken cancellationToken)
    {
        StringBuilder answer = new();
        List<ChatMessage> messages = [new ChatMessage(ChatRole.User, userMessage)];

        await foreach (AgentResponseUpdate update in agent.RunStreamingAsync(messages, session: null, options: null, cancellationToken).ConfigureAwait(false))
        {
            _ = answer.Append(update.Text);
        }

        return answer.ToString();
    }
}