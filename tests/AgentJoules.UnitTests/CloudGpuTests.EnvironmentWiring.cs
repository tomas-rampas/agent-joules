using AgentJoules.Core.Configuration;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.TestSupport;
using AgentJoules.Workflow.Runs;

using Xunit;

namespace AgentJoules.UnitTests;

public sealed partial class CloudGpuTests
{
    /// <summary>
    /// The cloudgpu fields of <c>environment</c> (AJ-122 wiring for AJ-127): what <see cref="RunAssembler.BuildEnvironment"/> takes from <c>CloudGpu</c> for the cloudgpu tier and what it leaves alone for the other tiers.
    /// </summary>
    [Trait("Spec", "REQ-031")]
    public sealed class EnvironmentWiring
    {
        private static EnvironmentInfo Build(Tier tier, CloudGpuOptions? cloudGpu)
        {
            AgentJoulesOptions options = ConfigurationFixtures.ShippedOptions();
            TierOptions tierOptions = options.Tiers[TierOptions.NameOf(tier)];
            return RunAssembler.BuildEnvironment(tier, tierOptions, gpu: null, runtime: null, modelLoad: null, cost: options.Cost, powerSource: null, cloudGpu: cloudGpu);
        }

        private static CloudGpuOptions Rented() => new()
        {
            Provider = "other-cloud",
            InstancePreset = "gpu-h100-1x",
            Region = "eu-north1",
            ContainerImage = "registry.example/nim/llm",
            ContainerDigest = "sha256:abc123",
            ObservedCostUsd = 12.4,
        };

        [Fact]
        public void The_cloudgpu_tier_writes_the_five_fields_and_the_cost_as_billed_after_the_run()
        {
            EnvironmentInfo environment = Build(Tier.CloudGpu, Rented());

            Assert.Equal("other-cloud", environment.Provider);
            Assert.Equal("gpu-h100-1x", environment.InstancePreset);
            Assert.Equal("eu-north1", environment.Region);
            Assert.Equal("registry.example/nim/llm", environment.ContainerImage);
            Assert.Equal("sha256:abc123", environment.ContainerDigest);
            Assert.Equal(MetricLabel.NotReported, environment.CloudCostUsd.Label);
            Assert.Equal(RunAssembler.BilledAfterRunReason, environment.CloudCostUsd.Reason);
            Assert.Null(environment.CloudCostUsd.Value);
        }

        [Fact]
        public void The_provider_falls_back_to_nebius_ai_cloud_while_it_is_not_set()
        {
            CloudGpuOptions options = Rented();
            options.Provider = null;

            Assert.Equal("nebius-ai-cloud", Build(Tier.CloudGpu, options).Provider);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Blank_values_become_null_and_a_blank_provider_falls_back(string? blank)
        {
            var options = new CloudGpuOptions { Provider = blank, InstancePreset = blank, Region = blank, ContainerImage = blank, ContainerDigest = blank };

            EnvironmentInfo environment = Build(Tier.CloudGpu, options);

            Assert.Equal("nebius-ai-cloud", environment.Provider);
            Assert.Null(environment.InstancePreset);
            Assert.Null(environment.Region);
            Assert.Null(environment.ContainerImage);
            Assert.Null(environment.ContainerDigest);
        }

        [Fact]
        public void The_values_are_trimmed()
        {
            var options = new CloudGpuOptions { Provider = "  other-cloud ", InstancePreset = " p ", Region = " r ", ContainerImage = " i ", ContainerDigest = " d " };

            EnvironmentInfo environment = Build(Tier.CloudGpu, options);

            Assert.Equal("other-cloud", environment.Provider);
            Assert.Equal("p", environment.InstancePreset);
            Assert.Equal("r", environment.Region);
            Assert.Equal("i", environment.ContainerImage);
            Assert.Equal("d", environment.ContainerDigest);
        }

        [Fact]
        public void A_cost_that_was_not_observed_is_billed_after_the_run_too()
        {
            CloudGpuOptions options = Rented();
            options.ObservedCostUsd = null;

            Metric cost = Build(Tier.CloudGpu, options).CloudCostUsd;

            Assert.Equal(MetricLabel.NotReported, cost.Label);
            Assert.Equal(RunAssembler.BilledAfterRunReason, cost.Reason);
        }

        [Fact]
        public void A_hosted_tier_ignores_the_cloudgpu_options()
        {
            EnvironmentInfo environment = Build(Tier.Nvidia, Rented());

            Assert.Equal(RunAssembler.ProviderOf(Tier.Nvidia), environment.Provider);
            Assert.Null(environment.InstancePreset);
            Assert.Null(environment.Region);
            Assert.Null(environment.ContainerImage);
            Assert.Null(environment.ContainerDigest);
            Assert.Equal(MetricLabel.NotApplicable, environment.CloudCostUsd.Label);
            Assert.Equal(RunAssembler.NotCloudGpuReason, environment.CloudCostUsd.Reason);
        }
    }
}