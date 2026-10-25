using AgentJoules.TestSupport;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The state of one AJ-104 scenario (report Ollama failures with the exact fix command), handed to the step class by Reqnroll context injection. The Given steps only record how the loopback fake that
/// stands where Ollama stands must behave (its tags, a refusal, a delay) and which tag the local tier is configured with; the When step builds the fake and the setup from
/// <see cref="OllamaFixtures"/>, runs the real <c>run</c> command through <see cref="RunCommandHarness"/> and keeps the fake and the result. Nothing leaves the process.
/// </summary>
public sealed class OllamaFailuresContext
{
    /// <summary>The tags the models route lists.</summary>
    public List<string> Tags { get; } = [];

    /// <summary>The configured <c>Tiers:local:ModelId</c>; the tag of the story when null.</summary>
    public string? ConfiguredTag { get; set; }

    /// <summary>True when the fake refuses every connection.</summary>
    public bool Refuse { get; set; }

    /// <summary>True when the fake holds every answer back for longer than the configured timeout.</summary>
    public bool Stall { get; set; }

    /// <summary>The fake of the last run, or null before it ran.</summary>
    public FakeHttpMessageHandler? Endpoint { get; private set; }

    /// <summary>The result of the last run, or null before it ran.</summary>
    public CliRunResult? Result { get; private set; }

    /// <summary>The configured timeout of the local tier in a scenario that stalls, in seconds.</summary>
    public const int StallTimeoutSeconds = 1;

    /// <summary>Builds the fake and the setup from the recorded Given steps and runs <paramref name="arguments"/> through the real command.</summary>
    /// <param name="arguments">The command line, for example <c>run --tier local --dry-run</c>.</param>
    /// <returns>A task that completes when the command ended and <see cref="Result"/> is set.</returns>
    public async Task RunAsync(string arguments)
    {
        var endpoint = new FakeHttpMessageHandler
        {
            ModelIds = [.. Tags],
            OllamaStyleModels = true,
            ResponseDelay = Stall ? TimeSpan.FromSeconds(20) : TimeSpan.Zero,
            ConnectionRefused = Refuse,
        };
        Endpoint = endpoint;

        RunCommandSetup setup = OllamaFixtures.Setup(endpoint, ConfiguredTag, timeoutSeconds: Stall ? StallTimeoutSeconds : null);
        Result = await RunCommandHarness.RunAsync(arguments, setup);
    }
}