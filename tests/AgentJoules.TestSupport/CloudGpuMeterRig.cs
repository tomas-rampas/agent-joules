using System.Text.Json;

namespace AgentJoules.TestSupport;

/// <summary>
/// The rig of the AJ-124 scenarios and tests (EDGE-015): a <see cref="LocalRunRig"/> whose cloudgpu tier answers from a fake endpoint, with the energy counter of the scripted GPU supported or not and <c>Meter:Method</c> set
/// the way a builder would set it. Nothing leaves the process; the clock is the fake clock of the rig.
/// </summary>
public static class CloudGpuMeterRig
{
    /// <summary>The command of the run scenarios: one repeat of the committed task on the cloudgpu tier.</summary>
    public const string RunCommand = "run --tier cloudgpu --repeat 1 --task incident-brief";

    /// <summary>The line the console prints once when automatic selection fell back to sampling.</summary>
    public const string FallbackLine = "Energy method: sampling (counter unsupported)";

    /// <summary>Creates the rig; the caller disposes it.</summary>
    /// <param name="counterSupported">False scripts NOT_SUPPORTED for the energy counter.</param>
    /// <param name="meterMethod">The value of <c>Meter:Method</c>, or null for the shipped default.</param>
    /// <returns>The rig.</returns>
    public static LocalRunRig Create(bool counterSupported, string? meterMethod = null)
    {
        LocalRunRig rig = LocalRunRig.Nim();
        rig.Setup.Endpoints["cloudgpu"] = new FakeHttpMessageHandler { ModelIds = ConfigurationFixtures.ShippedModelIds(), Clock = rig.Time, DefaultDuration = TimeSpan.FromSeconds(1) };

        // The cloudgpu tier asks for SSH variables unless the run happens on the VM itself; the run here is a fake and has no host.
        rig.Setup.Settings["AgentJoules:CloudGpu:RequireSshVariables"] = "false";
        if (meterMethod is not null)
        {
            rig.Setup.Settings["AgentJoules:Meter:Method"] = meterMethod;
        }

        rig.Setup.Gpu!.CounterSupported = counterSupported;
        return rig;
    }

    /// <summary>The steps of the one run file the command wrote that carry a reading (an energy method).</summary>
    /// <param name="document">The run file.</param>
    /// <returns>The step elements that have a method.</returns>
    public static JsonElement[] ReadingSteps(JsonDocument document) =>
        [.. RunOrchestrationFixtures.Steps(document).Where(static step => step.TryGetProperty("method", out JsonElement method) && method.ValueKind == JsonValueKind.String)];

    /// <summary>The flag names of one step.</summary>
    /// <param name="step">The step element.</param>
    /// <returns>The flags as text.</returns>
    public static string[] FlagsOf(JsonElement step) => [.. step.GetProperty("flags").EnumerateArray().Select(static flag => flag.GetString() ?? string.Empty)];
}