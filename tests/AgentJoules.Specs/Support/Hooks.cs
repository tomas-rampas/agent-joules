using Microsoft.Extensions.Time.Testing;

using Reqnroll;

namespace AgentJoules.Specs.Support;

[Binding]
public sealed class Hooks(ScenarioWorld world)
{
    private readonly ScenarioWorld _world = world;

    [BeforeScenario]
    public void CreateFakeTime()
    {
        _world.Time = new FakeTimeProvider();
    }

    [AfterScenario]
    public void DisposeScenarioState()
    {
        _world.Dispose();
    }
}