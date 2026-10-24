using System.Text.Json.Nodes;

using AgentJoules.Cli;
using AgentJoules.Core.Configuration;
using AgentJoules.TestSupport;
using AgentJoules.Workflow.Providers;

using Microsoft.Extensions.DependencyInjection;

using Xunit.Sdk;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The state of one AJ-100 scenario (<c>AJ validate --config</c> checks the configured model IDs against <c>docs/endpoints.md</c>, REQ-018), handed to the step class by Reqnroll context injection. The Given steps
/// edit a small valid configuration and the sections of an endpoints document (<see cref="EndpointsFixtures"/>); the When step writes both into a <see cref="TempWorkspace"/> (the repository is never read or written)
/// and runs the real command line through the harness with that workspace as the working directory; the Then steps read the exit code and the two streams. The named HTTP client of every tier goes to one
/// <see cref="DenyAllHttpMessageHandler"/>, so the request count of the scenario is measured, not assumed.
/// </summary>
public sealed class EndpointsValidationContext : IDisposable
{
    private readonly List<SectionDraft> _sections = [.. EndpointsFixtures.DefaultTiers.Select(static tier => new SectionDraft(tier, EndpointsFixtures.VerifiedDate, [EndpointsFixtures.ModelOf(tier)]))];
    private TempWorkspace? _workspace;

    /// <summary>The configuration node the Given steps edit; it starts as <see cref="EndpointsFixtures.DefaultConfig"/>.</summary>
    public JsonObject Config { get; } = EndpointsFixtures.DefaultConfig();

    /// <summary>The recording deny-all handler behind the named HTTP client of every tier.</summary>
    public DenyAllHttpMessageHandler Handler { get; } = new();

    /// <summary>The line the scenario expects for the model ID it made absent, or null.</summary>
    public string? ExpectedMissingLine { get; private set; }

    /// <summary>The result of the run, or null before it ran.</summary>
    public CliRunResult? Result { get; private set; }

    /// <summary>The lines of standard output without line terminators and empty lines.</summary>
    public string[] StdoutLines => [.. Required().Stdout.Split('\n').Select(static line => line.TrimEnd('\r')).Where(static line => line.Length > 0)];

    /// <summary>The configuration section the Given steps edit.</summary>
    public JsonObject Section => EndpointsFixtures.SectionOf(Config);

    /// <summary>The section draft of <paramref name="tier"/>.</summary>
    /// <param name="tier">The lowercase tier name.</param>
    /// <returns>The draft, created at the end of the document when the scenario has none yet.</returns>
    public SectionDraft Draft(string tier)
    {
        SectionDraft? draft = _sections.Find(section => section.Tier == tier);
        if (draft is null)
        {
            draft = new SectionDraft(tier, EndpointsFixtures.VerifiedDate, []);
            _sections.Add(draft);
        }

        return draft;
    }

    /// <summary>Gives the tier a model ID that its section lists not.</summary>
    /// <param name="tier">The lowercase tier name.</param>
    /// <param name="modelId">The model ID the configuration names.</param>
    public void NameUnlistedModel(string tier, string modelId)
    {
        EndpointsFixtures.TierOf(Config, tier)["ModelId"] = modelId;
        ExpectedMissingLine = $"Missing in section ## {tier}: {modelId}";
    }

    /// <summary>Puts a disabled cloudgpu tier into the configuration and a <c>## cloudgpu</c> section without a Verified line and without model lines.</summary>
    public void DisableCloudGpuWithoutVerifiedLine()
    {
        Section["Tiers"]!.AsObject()["cloudgpu"] = EndpointsFixtures.TierEntry("cloudgpu", "model-cloudgpu-unlisted", enabled: false);
        SectionDraft draft = Draft("cloudgpu");
        draft.Verified = null;
        draft.Ids.Clear();
    }

    /// <summary>Runs <c>validate --config</c> (or the given arguments) in the workspace built from the current state.</summary>
    /// <param name="arguments">The command line without the program name, split on white space.</param>
    /// <returns>A task that completes when <see cref="Result"/> is set.</returns>
    public async Task RunAsync(string arguments)
    {
        TempWorkspace workspace = _workspace ??= TempWorkspace.CreateEmpty();
        workspace.WriteConfig(EndpointsFixtures.Text(Config));
        _ = workspace.WriteFile(EndpointsFixtures.DocumentPath, EndpointsFixtures.Document([.. _sections.Select(static section => EndpointsFixtures.Section(section.Tier, section.Verified, [.. section.Ids]))]));

        DenyAllHttpMessageHandler handler = Handler;
        var overrides = new CliHostOverrides(
            Services: services =>
            {
                foreach (string tier in TierOptions.KnownNames)
                {
                    _ = services.AddHttpClient(ProviderClientFactory.HttpClientName(tier)).ConfigurePrimaryHttpMessageHandler(() => handler);
                }
            },
            WorkingDirectory: workspace.Root);
        Result = await CliHarness.RunTokensAsync(arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries), overrides);
    }

    /// <summary>The result of the run.</summary>
    /// <exception cref="XunitException">The command has not run.</exception>
    public CliRunResult Required() => Result ?? throw new XunitException("The command has not run yet.");

    /// <inheritdoc />
    public void Dispose() => _workspace?.Dispose();

    /// <summary>One section of the document the scenario writes.</summary>
    /// <param name="tier">The tier name of the heading.</param>
    /// <param name="verified">The text after <c>Verified: </c>, or null for no such line.</param>
    /// <param name="ids">The IDs of the model lines.</param>
    public sealed class SectionDraft(string tier, string? verified, List<string> ids)
    {
        /// <summary>The tier name of the heading.</summary>
        public string Tier { get; } = tier;

        /// <summary>The text after <c>Verified: </c>, or null for no such line.</summary>
        public string? Verified { get; set; } = verified;

        /// <summary>The IDs of the model lines.</summary>
        public List<string> Ids { get; } = ids;
    }
}