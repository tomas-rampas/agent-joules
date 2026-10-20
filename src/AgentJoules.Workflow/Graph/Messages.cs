namespace AgentJoules.Workflow.Graph;

/// <summary>Which of the two parallel branches produced a <see cref="BranchOutput"/>.</summary>
public enum BranchRole
{
    /// <summary>The Researcher branch: the research notes (facts, constraints and risks).</summary>
    Researcher = 1,

    /// <summary>The Writer branch: the draft answer.</summary>
    Writer = 2,
}

/// <summary>The input of the workflow, and the message the Planner receives: the self-contained prompt of the task.</summary>
/// <param name="Prompt">The task prompt, sent to the agents as it is in the user message of each step; never put in instructions, logs or exception messages.</param>
public sealed record TaskInput(string Prompt);

/// <summary>The message the Planner sends to both branches: the task prompt and the plan the Planner wrote for it.</summary>
/// <param name="Prompt">The task prompt, carried forward so that each branch can build its user message from the labelled sections <c>Task</c> and <c>Plan</c>.</param>
/// <param name="Plan">The text the Planner produced.</param>
public sealed record PlanMessage(string Prompt, string Plan);

/// <summary>The message a branch sends to the Reviewer: which branch it is and the text it produced.</summary>
/// <param name="Role">The branch that produced <paramref name="Text"/>.</param>
/// <param name="Text">The research notes of the Researcher or the draft of the Writer.</param>
public sealed record BranchOutput(BranchRole Role, string Text);

/// <summary>The output of the workflow: the corrected final answer of the Reviewer.</summary>
/// <param name="Text">The text the Reviewer produced.</param>
public sealed record FinalOutput(string Text);