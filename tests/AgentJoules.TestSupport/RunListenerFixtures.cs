using AgentJoules.Workflow.Runs;

using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace AgentJoules.TestSupport;

/// <summary>Plumbing for the tests that put a decorator around the run listener of the command.</summary>
public static class RunListenerFixtures
{
    /// <summary>
    /// Replaces the run listener of the command with <paramref name="wrap"/> applied to the one the composition root registered (the console listener), so the output of the command is still written.
    /// </summary>
    /// <param name="setup">The setup of the command.</param>
    /// <param name="wrap">Builds the decorator around the console listener.</param>
    public static void Decorate(RunCommandSetup setup, Func<IRunListener, IRunListener> wrap)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(wrap);

        RunOrchestrationFixtures.AddServices(setup, services =>
        {
            ServiceDescriptor original = Assert.Single(services, static descriptor => !descriptor.IsKeyedService && descriptor.ServiceType == typeof(IRunListener));
            _ = services.AddSingleton<IRunListener>(provider => wrap((IRunListener)ActivatorUtilities.CreateInstance(provider, original.ImplementationType!)));
        });
    }
}