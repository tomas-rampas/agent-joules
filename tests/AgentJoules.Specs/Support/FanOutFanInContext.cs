using AgentJoules.TestSupport;
using AgentJoules.Workflow.Graph;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The state of one AJ-050 scenario (the workflow graph), handed to the step class by Reqnroll context injection. The Given steps only choose how the rig is
/// built (the mode, the delay of a branch, an agent whose endpoint answers an error); the When step builds the <see cref="WorkflowRig"/> for the task and runs
/// the workflow once, keeping the output or the exception it ended with. Nothing in it waits on the wall clock and no request leaves the process.
/// </summary>
public sealed class FanOutFanInContext : IAsyncDisposable
{
    /// <summary>The name of the task a When step runs when the scenario names none.</summary>
    public const string DefaultTask = WorkflowRig.TaskName;

    /// <summary>True when a Given step chose serialized mode.</summary>
    public bool Serialized { get; set; }

    /// <summary>The fake time each agent takes before its text, by agent name; an agent that is not in it takes the default of the rig.</summary>
    public Dictionary<string, TimeSpan> Delays { get; } = [];

    /// <summary>The agent whose endpoint answers <see cref="EndpointStatus"/>, or null when every client is a plain fake.</summary>
    public string? FailingAgent { get; set; }

    /// <summary>The HTTP status the endpoint of <see cref="FailingAgent"/> answers.</summary>
    public int EndpointStatus { get; set; }

    /// <summary>The rig the When step built.</summary>
    public WorkflowRig? Rig { get; private set; }

    /// <summary>The final output of the run, or null when it failed or has not run.</summary>
    public FinalOutput? Output { get; private set; }

    /// <summary>The exception the run ended with, or null.</summary>
    public Exception? Thrown { get; private set; }

    /// <summary>The rig of the scenario.</summary>
    /// <exception cref="InvalidOperationException">No When step ran the workflow.</exception>
    public WorkflowRig RequireRig() => Rig ?? throw new InvalidOperationException("No When step ran the workflow.");

    /// <summary>Builds the rig as the Given steps chose and runs the workflow once for the task <paramref name="taskName"/>.</summary>
    /// <param name="taskName">The name of the task.</param>
    /// <param name="guard">The real time after which a run that neither finishes nor waits is a failure.</param>
    /// <returns>A task that completes when the run ended, by an output or by an exception that is kept in <see cref="Thrown"/>.</returns>
    public async Task RunAsync(string taskName, TimeSpan guard)
    {
        Rig = FailingAgent is null
            ? await WorkflowRig.CreateAsync(
                (agent, time) => WorkflowRig.StreamingClient(time, agent, Delays.TryGetValue(agent, out TimeSpan delay) ? delay : null),
                Serialized,
                taskName)
            : await WorkflowRig.CreateWithFailingEndpointAsync(FailingAgent, EndpointStatus, Serialized, taskName);

        try
        {
            Output = await Rig.RunAsync().WaitAsync(guard);
        }
        catch (Exception failure) when (failure is not TimeoutException)
        {
            Thrown = failure;
        }
    }

    /// <summary>Disposes the rig; Reqnroll does not dispose an async-disposable context, so the <c>AfterScenario</c> hook of the step class calls this.</summary>
    public ValueTask DisposeAsync() => Rig?.DisposeAsync() ?? ValueTask.CompletedTask;
}