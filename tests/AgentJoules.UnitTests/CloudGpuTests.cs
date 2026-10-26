using AgentJoules.Core.Gpu;
using AgentJoules.Nvml;
using AgentJoules.Nvml.Interop;
using AgentJoules.TestSupport;

using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace AgentJoules.UnitTests;

/// <summary>
/// The unit tests of the cloudgpu tier (M5, REQ-031). AJ-121 (the gate on the SSH variables, MIG and the tier flag) is the nested class <c>Gating</c>; the other stories of the tier are nested classes in their own partial files.
/// </summary>
[Trait("Spec", "REQ-031")]
public sealed partial class CloudGpuTests
{
    /// <summary>
    /// AJ-121: <c>run --tier cloudgpu --dry-run</c> through the real <c>run</c> command and the real preflight, with a scripted GPU, fake models endpoints and runtime-built fake values. Nothing reads a process variable.
    /// </summary>
    [Trait("Spec", "REQ-031")]
    public sealed class Gating
    {
        private const string Host = "CLOUDGPU_SSH_HOST";
        private const string User = "CLOUDGPU_SSH_USER";

        // A command that hangs must fail the test, not the run (system clock; it only ends a run that already failed).
        private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

        private static string FakeValue(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N");

        // Keys for both hosted tiers, the tier flag and the SSH requirement of the VM or the laptop; a later pair with the same key wins.
        private static Dictionary<string, string?> Settings(bool cloudGpuEnabled, bool requireSsh, params (string Key, string? Value)[] pairs)
        {
            var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["NVIDIA_API_KEY"] = ProviderClientFixtures.FakeKey(),
                ["NEBIUS_API_KEY"] = ProviderClientFixtures.FakeKey(),
                ["AgentJoules:CloudGpu:RequireSshVariables"] = requireSsh ? "true" : "false",
            };
            if (cloudGpuEnabled)
            {
                settings["AgentJoules:Tiers:cloudgpu:Enabled"] = "true";
            }

            foreach ((string key, string? value) in pairs)
            {
                settings[key] = value;
            }

            return settings;
        }

        private static ScriptedNvmlApi Gpu() => new(new FakeTimeProvider());

        private static async Task<CliRunResult> RunAsync(string arguments, Dictionary<string, string?> settings, ScriptedNvmlApi? gpu = null)
        {
            // Every tier answers its models route with the shipped IDs, so the model check passes and nothing falls through to the deny-all handler.
            var endpoints = new Dictionary<string, HttpMessageHandler>(StringComparer.Ordinal);
            foreach (string tierName in new[] { "local", "cloudgpu", "nvidia", "nebius" })
            {
                endpoints[tierName] = new FakeHttpMessageHandler { ModelIds = ConfigurationFixtures.ShippedModelIds() };
            }

            RunCommandSetup setup = RunCommandSetup.Create(settings, endpoints, gpu ?? Gpu());
            CliRunResult result = await RunCommandHarness.RunAsync(arguments, setup).WaitAsync(HangGuard);
            Assert.Empty(result.DeniedRequests);
            return result;
        }

        private static string[] TierNames(CliRunResult result) =>
            [.. result.Stdout.Split('\n').Select(static line => line.TrimEnd('\r')).Where(static line => line.Length > 0).Select(static line => line.Split(' ')[0])];

        private static string SingleStderrLine(CliRunResult result) => Assert.Single(result.StderrLines, line => line.Length > 0);

        // The MIG mode the probe reports for the scripted GPU: the real NVML binding opens it and maps the return code.
        private static string MigModeOf(ScriptedNvmlApi gpu)
        {
            GpuOpenResult opened = new NvmlGpuTelemetrySourceFactory(gpu).Open(0);
            Assert.True(opened.IsAvailable, opened.Reason);
            using IGpuTelemetrySource source = opened.Source;
            return source.Identity.MigMode;
        }

        [Fact]
        public async Task Both_ssh_variables_set_passes_and_no_value_is_printed()
        {
            string host = FakeValue("host");
            string user = FakeValue("user");

            CliRunResult result = await RunAsync("run --tier cloudgpu --dry-run", Settings(true, true, (Host, host), (User, user)));

            Assert.True(result.ExitCode == 0, $"Expected exit code 0 but got {result.ExitCode}; stderr: {result.Stderr}");
            Assert.Equal(["cloudgpu"], TierNames(result));
            string output = result.Stdout + result.Stderr;
            Assert.DoesNotContain(host, output, StringComparison.Ordinal);
            Assert.DoesNotContain(user, output, StringComparison.Ordinal);
        }

        [Fact]
        public async Task The_VM_needs_no_ssh_variables_when_RequireSshVariables_is_false()
        {
            CliRunResult result = await RunAsync("run --tier cloudgpu --dry-run", Settings(true, false));

            Assert.True(result.ExitCode == 0, $"Expected exit code 0 but got {result.ExitCode}; stderr: {result.Stderr}");
            Assert.Equal(["cloudgpu"], TierNames(result));
            Assert.DoesNotContain(Host, result.Stdout + result.Stderr, StringComparison.Ordinal);
        }

        [Fact]
        public async Task A_missing_host_variable_exits_3_names_it_and_prints_no_value()
        {
            string user = FakeValue("user");

            CliRunResult result = await RunAsync("run --tier cloudgpu --dry-run", Settings(true, true, (User, user)));

            Assert.Equal(3, result.ExitCode);
            string line = SingleStderrLine(result);
            Assert.Contains(Host, line, StringComparison.Ordinal);
            Assert.DoesNotContain(User, line, StringComparison.Ordinal);
            Assert.DoesNotContain(user, result.Stdout + result.Stderr, StringComparison.Ordinal);
            Assert.Empty(result.Stdout);
        }

        [Fact]
        public async Task Both_ssh_variables_missing_are_one_message_with_the_exit_code_of_a_missing_tier_key()
        {
            CliRunResult both = await RunAsync("run --tier cloudgpu --dry-run", Settings(true, true));
            Dictionary<string, string?> noKey = Settings(false, true);
            _ = noKey.Remove("NVIDIA_API_KEY");
            CliRunResult missingKey = await RunAsync("run --tier nvidia --dry-run", noKey);

            Assert.Equal(3, both.ExitCode);
            string line = SingleStderrLine(both);
            Assert.StartsWith("error: ", line, StringComparison.Ordinal);
            Assert.Contains(Host, line, StringComparison.Ordinal);
            Assert.Contains(User, line, StringComparison.Ordinal);
            Assert.EndsWith("(MissingKeyException)", line, StringComparison.Ordinal);
            Assert.Contains("NVIDIA_API_KEY", SingleStderrLine(missingKey), StringComparison.Ordinal);
            Assert.Equal(missingKey.ExitCode, both.ExitCode);
        }

        [Fact]
        public async Task MIG_not_supported_return_code_3_passes_and_the_probe_reports_not_supported()
        {
            ScriptedNvmlApi gpu = Gpu();
            gpu.MigModeReturn = NvmlReturn.NotSupported;

            CliRunResult result = await RunAsync("run --tier cloudgpu --dry-run", Settings(true, false), gpu);

            Assert.True(result.ExitCode == 0, $"Expected exit code 0 but got {result.ExitCode}; stderr: {result.Stderr}");
            Assert.Equal(3, (int)NvmlReturn.NotSupported);
            Assert.Equal("not-supported", MigModeOf(gpu));
        }

        [Fact]
        public async Task MIG_disabled_passes()
        {
            ScriptedNvmlApi gpu = Gpu();
            gpu.MigModeCurrent = 0;

            CliRunResult result = await RunAsync("run --tier cloudgpu --dry-run", Settings(true, false), gpu);

            Assert.True(result.ExitCode == 0, $"Expected exit code 0 but got {result.ExitCode}; stderr: {result.Stderr}");
            Assert.Equal("disabled", MigModeOf(gpu));
        }

        [Fact]
        public async Task MIG_enabled_exits_6_naming_MIG()
        {
            ScriptedNvmlApi gpu = Gpu();
            gpu.MigModeCurrent = 1;

            CliRunResult result = await RunAsync("run --tier cloudgpu --dry-run", Settings(true, false), gpu);

            Assert.Equal(6, result.ExitCode);
            Assert.Contains("MIG", SingleStderrLine(result), StringComparison.Ordinal);
            Assert.Equal("enabled", MigModeOf(gpu));
        }

        [Fact]
        public async Task The_committed_configuration_leaves_cloudgpu_out_of_the_all_tier_run()
        {
            CliRunResult result = await RunAsync("run --tier all --dry-run", Settings(false, true));

            Assert.True(result.ExitCode == 0, $"Expected exit code 0 but got {result.ExitCode}; stderr: {result.Stderr}");
            Assert.Equal(["local", "nvidia", "nebius"], TierNames(result));
            Assert.DoesNotContain("cloudgpu", result.Stdout, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task An_enabled_cloudgpu_tier_is_second_in_the_all_tier_run()
        {
            CliRunResult result = await RunAsync("run --tier all --dry-run", Settings(true, false));

            Assert.True(result.ExitCode == 0, $"Expected exit code 0 but got {result.ExitCode}; stderr: {result.Stderr}");
            Assert.Equal(["local", "cloudgpu", "nvidia", "nebius"], TierNames(result));
        }
    }
}