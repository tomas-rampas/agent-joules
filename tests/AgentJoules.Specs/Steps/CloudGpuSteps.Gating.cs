using AgentJoules.Core.Gpu;
using AgentJoules.Nvml;
using AgentJoules.Nvml.Interop;
using AgentJoules.TestSupport;

using Microsoft.Extensions.Time.Testing;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-121 (REQ-031): the gate on the SSH variables, MIG and the tier flag. The steps are scoped to the tag <c>AJ-S121</c> because "I run" and "the exit code is 3" are sentences of every CLI story. Every scenario runs the real
/// <c>run</c> command through <see cref="RunCommandHarness"/> with the real preflight, a scripted GPU, fake models endpoints for the four tiers (the deny-all handler stays in force for anything else) and runtime-built fake values.
/// </summary>
public sealed partial class CloudGpuSteps
{
    private const string GatingTag = "AJ-S121";
    private const string SshHost = "CLOUDGPU_SSH_HOST";
    private const string SshUser = "CLOUDGPU_SSH_USER";

    // A command that hangs must fail the scenario, not the run (system clock; it only ends a run that already failed).
    private static readonly TimeSpan GatingHangGuard = TimeSpan.FromSeconds(30);

    private readonly Dictionary<string, string?> _gatingSettings = new(StringComparer.Ordinal)
    {
        ["NVIDIA_API_KEY"] = ProviderClientFixtures.FakeKey(),
        ["NEBIUS_API_KEY"] = ProviderClientFixtures.FakeKey(),
    };

    private readonly ScriptedNvmlApi _gatingGpu = new(new FakeTimeProvider());
    private readonly List<string> _gatingFakeValues = [];

    private bool _gatingCloudGpuEnabled = true;
    private bool _gatingRequireSsh;
    private CliRunResult? _gatingResult;

    private CliRunResult GatingResult => _gatingResult ?? throw new Xunit.Sdk.XunitException("The command has not run yet.");

    private string GatingOutput => GatingResult.Stdout + "\n" + GatingResult.Stderr;

    private string[] GatingTierLines => [.. GatingResult.Stdout.Split('\n').Select(static line => line.TrimEnd('\r')).Where(static line => line.Length > 0).Select(static line => line.Split(' ')[0])];

    // The runtime-built fake value of an SSH variable; remembered so that the output can be checked for it.
    private void SetGatingSsh(string variable)
    {
        string value = "fake-" + Guid.NewGuid().ToString("N");
        _gatingSettings[variable] = value;
        _gatingFakeValues.Add(value);
    }

    private void AssertNoGatingValue() =>
        Assert.All(_gatingFakeValues, value => Assert.DoesNotContain(value, GatingOutput, StringComparison.Ordinal));

    [Given(@"^CloudGpu:RequireSshVariables is true and both SSH variables are set to runtime-built fake values$")]
    [Scope(Tag = GatingTag)]
    public void GivenRequireTrueAndBothSshSet()
    {
        _gatingRequireSsh = true;
        SetGatingSsh(SshHost);
        SetGatingSsh(SshUser);
    }

    [Given(@"^CloudGpu:RequireSshVariables is false and the SSH variables are unset$")]
    [Scope(Tag = GatingTag)]
    public void GivenRequireFalseAndSshUnset()
    {
        _gatingRequireSsh = false;
        Assert.False(_gatingSettings.ContainsKey(SshHost) || _gatingSettings.ContainsKey(SshUser));
    }

    [Given(@"^the fake NVML returns return code 3 for the MIG mode$")]
    [Scope(Tag = GatingTag)]
    public void GivenMigReturnCode3() => _gatingGpu.MigModeReturn = NvmlReturn.NotSupported;

    [Given(@"^Tiers:cloudgpu:Enabled is true$")]
    [Scope(Tag = GatingTag)]
    public void GivenCloudGpuEnabled() => _gatingCloudGpuEnabled = true;

    [Given(@"^CloudGpu:RequireSshVariables is true and CLOUDGPU_SSH_HOST is unset$")]
    [Scope(Tag = GatingTag)]
    public void GivenRequireTrueAndHostUnset()
    {
        _gatingRequireSsh = true;

        // The user variable is present, so the message must name exactly the host variable and the user value is a leak to look for.
        SetGatingSsh(SshUser);
    }

    [Given(@"^both SSH variables are unset and RequireSshVariables is true$")]
    [Scope(Tag = GatingTag)]
    public void GivenBothUnsetAndRequireTrue() => _gatingRequireSsh = true;

    [Given(@"^the fake NVML reports MIG mode enabled$")]
    [Scope(Tag = GatingTag)]
    public void GivenMigEnabled() => _gatingGpu.MigModeCurrent = 1;

    [Given(@"^the committed configuration with Tiers:cloudgpu:Enabled false$")]
    [Scope(Tag = GatingTag)]
    public void GivenCommittedConfiguration()
    {
        // The setting is left out, so the value of config/appsettings.json (false) applies.
        _gatingCloudGpuEnabled = false;
        _gatingRequireSsh = true;
    }

    [When(@"^I run ""([^""]*)""$")]
    [Scope(Tag = GatingTag)]
    public async Task WhenIRunGating(string arguments)
    {
        _gatingSettings["AgentJoules:CloudGpu:RequireSshVariables"] = _gatingRequireSsh ? "true" : "false";
        if (_gatingCloudGpuEnabled)
        {
            _gatingSettings["AgentJoules:Tiers:cloudgpu:Enabled"] = "true";
        }

        // Every tier answers its models route with the shipped IDs; any other request reaches the deny-all handler and is recorded.
        var endpoints = new Dictionary<string, HttpMessageHandler>(StringComparer.Ordinal);
        foreach (string tierName in new[] { "local", "cloudgpu", "nvidia", "nebius" })
        {
            endpoints[tierName] = new FakeHttpMessageHandler { ModelIds = ConfigurationFixtures.ShippedModelIds() };
        }

        RunCommandSetup setup = RunCommandSetup.Create(_gatingSettings, endpoints, _gatingGpu);
        _gatingResult = await RunCommandHarness.RunAsync(arguments, setup).WaitAsync(GatingHangGuard);
        Assert.Empty(_gatingResult.DeniedRequests);
    }

    [Then(@"^the SSH variable check passes$")]
    [Scope(Tag = GatingTag)]
    public void ThenTheSshCheckPasses()
    {
        Assert.True(GatingResult.ExitCode == 0, $"Expected exit code 0 but got {GatingResult.ExitCode}; stderr: {GatingResult.Stderr}");
        Assert.Equal(["cloudgpu"], GatingTierLines);
        Assert.DoesNotContain(SshHost, GatingOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(SshUser, GatingOutput, StringComparison.Ordinal);
        AssertNoGatingValue();
    }

    [Then(@"^the MIG check passes and the probe reports migMode ""([^""]*)""$")]
    [Scope(Tag = GatingTag)]
    public void ThenTheMigCheckPasses(string migMode)
    {
        Assert.True(GatingResult.ExitCode == 0, $"Expected exit code 0 but got {GatingResult.ExitCode}; stderr: {GatingResult.Stderr}");
        Assert.Equal(["cloudgpu"], GatingTierLines);

        // The probe reads the MIG mode through the same binding and the same scripted GPU.
        GpuOpenResult opened = new NvmlGpuTelemetrySourceFactory(_gatingGpu).Open(0);
        Assert.True(opened.IsAvailable, opened.Reason);
        using IGpuTelemetrySource source = opened.Source;
        Assert.Equal(migMode, source.Identity.MigMode);
    }

    [Then(@"^the tiers are listed in the order local, cloudgpu, nvidia, nebius$")]
    [Scope(Tag = GatingTag)]
    public void ThenTheTiersAreListedInOrder()
    {
        Assert.True(GatingResult.ExitCode == 0, $"Expected exit code 0 but got {GatingResult.ExitCode}; stderr: {GatingResult.Stderr}");
        Assert.Equal(["local", "cloudgpu", "nvidia", "nebius"], GatingTierLines);
    }

    [Then(@"^the exit code is (\d+)$")]
    [Scope(Tag = GatingTag)]
    public void ThenTheExitCodeIsGating(int expected) =>
        Assert.True(expected == GatingResult.ExitCode, $"Expected exit code {expected} but got {GatingResult.ExitCode}; stderr: {GatingResult.Stderr}");

    [Then(@"^the output names ""([^""]*)""$")]
    [Scope(Tag = GatingTag)]
    public void ThenTheOutputNames(string variable)
    {
        Assert.Contains(variable, GatingOutput, StringComparison.Ordinal);

        // The other SSH variable is present in this scenario, so it is neither named nor printed.
        Assert.DoesNotContain(SshUser, GatingOutput, StringComparison.Ordinal);
        AssertNoGatingValue();
    }

    [Then(@"^the output names ""([^""]*)"" and ""([^""]*)"" and prints no value$")]
    [Scope(Tag = GatingTag)]
    public void ThenTheOutputNamesBoth(string first, string second)
    {
        // One message on one line names both variables; there is no value to print because none was set.
        string line = Assert.Single(GatingResult.StderrLines, static text => text.Length > 0);
        Assert.Contains(first, line, StringComparison.Ordinal);
        Assert.Contains(second, line, StringComparison.Ordinal);
        Assert.Empty(GatingResult.Stdout);
        AssertNoGatingValue();
    }

    [Then(@"^the output names MIG$")]
    [Scope(Tag = GatingTag)]
    public void ThenTheOutputNamesMig() => Assert.Contains("MIG", Assert.Single(GatingResult.ErrorLines), StringComparison.Ordinal);

    [Then(@"^no line for ""([^""]*)"" is printed$")]
    [Scope(Tag = GatingTag)]
    public void ThenNoLineIsPrinted(string tierName)
    {
        Assert.DoesNotContain(tierName, GatingResult.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(tierName, GatingTierLines);
        Assert.Equal(["local", "nvidia", "nebius"], GatingTierLines);
    }
}