using System.CommandLine;
using System.Text.RegularExpressions;

using AgentJoules.Cli;
using AgentJoules.Core.Cli;
using AgentJoules.Core.Configuration;
using AgentJoules.Core.Runs;
using AgentJoules.TestSupport;
using AgentJoules.Workflow;
using AgentJoules.Workflow.Providers;
using AgentJoules.Workflow.Runs;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using Xunit;

namespace AgentJoules.UnitTests;

/// <summary>
/// AJ-052 (EDGE-011): the key check before any call, the one message that names every missing variable, the judge key (a warning outside <c>--dry-run</c>, part of the message
/// inside it), the SSH variables of the <c>cloudgpu</c> tier, and the deny-by-default harness. AJ-053 and AJ-054 add the model check and the dry-run output to this class. The
/// <c>run</c> command does not exist yet (AJ-054 builds it), so the command line tests use <see cref="PreflightRunCommand"/>: the real <see cref="PreflightService"/>, the real
/// console reporter and the real provider pipeline behind a command that only parses <c>--tier</c> and <c>--dry-run</c>. Keys are runtime-built fakes in an in-memory configuration;
/// no test reads or sets a process variable.
/// </summary>
[Trait("Spec", "EDGE-011")]
public sealed class DryRunTests
{
    // A command that hangs must fail the test, not the run (system clock; it only ends a run that already failed).
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    private static string NvidiaBaseUrl => ProviderClientFixtures.BaseUrl(Tier.Nvidia);

    private static string NebiusBaseUrl => ProviderClientFixtures.BaseUrl(Tier.Nebius);

    private static string Key() => ProviderClientFixtures.FakeKey();

    // Fake base URLs for the two hosted tiers, then the pairs of the test; a later pair with the same key wins.
    private static Dictionary<string, string?> Settings(params (string Key, string? Value)[] pairs)
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["AgentJoules:Tiers:nvidia:BaseUrl"] = NvidiaBaseUrl,
            ["AgentJoules:Tiers:nebius:BaseUrl"] = NebiusBaseUrl,
        };
        foreach ((string key, string? value) in pairs)
        {
            settings[key] = value;
        }

        return settings;
    }

    private static async Task<CliRunResult> RunAsync(
        string arguments,
        Dictionary<string, string?> settings,
        IReadOnlyDictionary<string, HttpMessageHandler>? endpoints = null)
    {
        var command = new PreflightRunCommand();
        var overrides = new CliHostOverrides(
            Services: services =>
            {
                _ = services.AddAgentJoulesProviderClients().AddAgentJoulesPreflight();
                foreach ((string tierName, HttpMessageHandler endpoint) in endpoints ?? new Dictionary<string, HttpMessageHandler>())
                {
                    _ = services.AddHttpClient(ProviderClientFactory.HttpClientName(tierName)).ConfigurePrimaryHttpMessageHandler(() => endpoint);
                }
            },
            Configuration: settings,
            AdditionalCommands: [command.Create()],
            OnHostStarted: command.Attach);

        return await CliHarness.RunAsync(arguments, overrides).WaitAsync(HangGuard);
    }

    private static PreflightService ServiceFor(params (string Key, string Value)[] overrides)
    {
        IConfiguration configuration = ConfigurationFixtures.FromJsonFile(ConfigurationFixtures.AppSettingsPath(), overrides);
        return new PreflightService(configuration, Options.Create(OptionsLoader.Load(configuration)));
    }

    private static string SingleStderrLine(CliRunResult result) => Assert.Single(result.StderrLines, line => line.Length > 0);

    // ---- criteria 1 and 2: one message, every variable, the user-secrets alternative ----

    [Fact]
    public async Task Missing_keys_listed_in_one_message()
    {
        CliRunResult result = await RunAsync("run --tier all --dry-run", Settings());

        Assert.Equal(3, result.ExitCode);
        string line = SingleStderrLine(result);
        Assert.StartsWith("error: ", line, StringComparison.Ordinal);
        Assert.Contains("NVIDIA_API_KEY", line, StringComparison.Ordinal);
        Assert.Contains("NEBIUS_API_KEY", line, StringComparison.Ordinal);
        Assert.EndsWith("(MissingKeyException)", line, StringComparison.Ordinal);
        Assert.Empty(result.Stdout);
        Assert.Empty(result.DeniedRequests);
    }

    [Fact]
    public async Task User_secrets_alternative_shows_the_literal_placeholder_and_never_a_value()
    {
        // The nvidia key (and with it the judge key) is set, so the one missing variable is NEBIUS_API_KEY and a real value is within reach of the message.
        string present = Key();
        CliRunResult result = await RunAsync("run --tier all --dry-run", Settings(("NVIDIA_API_KEY", present)));

        Assert.Equal(3, result.ExitCode);
        string line = SingleStderrLine(result);
        Assert.Contains("dotnet user-secrets set \"<NAME>\" \"<value>\" --project src/AgentJoules.Cli", line, StringComparison.Ordinal);
        Assert.Contains("<value>", line, StringComparison.Ordinal);
        Assert.Contains("NEBIUS_API_KEY", line, StringComparison.Ordinal);
        Assert.DoesNotContain("NVIDIA_API_KEY", line, StringComparison.Ordinal);
        Assert.DoesNotContain(present, result.Stderr + result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Message_is_one_line_names_every_variable_once_and_hides_a_name_that_is_not_a_name()
    {
        string typedKey = string.Concat("typed-", Key());

        string message = MissingKeyMessage.Format(["NVIDIA_API_KEY", "NEBIUS_API_KEY", typedKey]);

        Assert.DoesNotContain('\n', message);
        Assert.DoesNotContain('\r', message);
        Assert.Contains("NVIDIA_API_KEY, NEBIUS_API_KEY", message, StringComparison.Ordinal);
        Assert.Contains(MissingKeyMessage.UserSecretsAlternative, message, StringComparison.Ordinal);
        Assert.Equal("dotnet user-secrets set \"<NAME>\" \"<value>\" --project src/AgentJoules.Cli", MissingKeyMessage.UserSecretsAlternative);
        Assert.DoesNotContain(typedKey, message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => MissingKeyMessage.Format([]));
    }

    [Fact]
    public void Missing_key_exception_exits_3_and_keeps_the_names_in_order()
    {
        var failure = new MissingKeyException("message", ["A_API_KEY", "B_API_KEY"]);

        Assert.Equal(ExitCode.MissingKey, failure.ExitCode);
        Assert.Equal(["A_API_KEY", "B_API_KEY"], failure.Variables);
        Assert.Equal("message", failure.Message);
        Assert.Throws<ArgumentException>(() => new MissingKeyException("message", []));
        Assert.Equal(3, ExitCodeMapper.Map(failure, shutdownRequested: false));
    }

    // ---- criterion 3: no character of the key beyond the public prefix ----

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Key_suffix_is_never_printed(bool debugLogging, bool opaqueKey)
    {
        // The endpoint refuses the connection and echoes the bearer token it saw in the failure text; the key must still not reach stdout or stderr, with or without the Debug records.
        // The opaque key (letters and digits, 120 characters, no nvapi- prefix) is masked only by its registration, which the nvapi- pattern would otherwise hide.
        string key = opaqueKey
            ? string.Concat(Key(), Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"))[..120]
            : string.Concat("nvapi-", Key(), "_Qd5R8hC1");
        var refusal = new CredentialEchoingRefusalHandler();
        Dictionary<string, string?> settings = Settings(("NVIDIA_API_KEY", key));
        if (debugLogging)
        {
            settings["Logging:LogLevel:Default"] = "Debug";
        }

        CliRunResult result = await RunAsync("run --tier nvidia", settings, new Dictionary<string, HttpMessageHandler> { ["nvidia"] = refusal });

        string output = result.Stdout + "\n" + result.Stderr;
        string suffix = opaqueKey ? key : key["nvapi-".Length..];
        Assert.Equal(key, refusal.LastBearerToken);
        Assert.NotEqual(0, result.ExitCode);
        Assert.EndsWith("Exception)", Assert.Single(result.ErrorLines), StringComparison.Ordinal);
        Assert.DoesNotContain(key, output, StringComparison.Ordinal);
        Assert.DoesNotContain(suffix, output, StringComparison.Ordinal);
        for (int start = 0; start + 5 <= suffix.Length; start++)
        {
            Assert.DoesNotContain(suffix.Substring(start, 5), output, StringComparison.Ordinal);
        }

        if (!opaqueKey)
        {
            Assert.DoesNotMatch(new Regex(@"nvapi-(?!\*{4})", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5)), output);
            Assert.Contains("nvapi-****", output, StringComparison.Ordinal);
        }

        if (debugLogging)
        {
            Assert.Contains(result.StderrLines, line => line.StartsWith("dbug:", StringComparison.Ordinal));
        }
    }

    // ---- criterion 4: a missing workflow key ends the command before any request ----

    [Fact]
    public async Task Missing_workflow_key_exits_3_before_any_request()
    {
        var nvidia = new FakeHttpMessageHandler();
        var nebius = new FakeHttpMessageHandler();
        var endpoints = new Dictionary<string, HttpMessageHandler> { ["nvidia"] = nvidia, ["nebius"] = nebius };

        // The nvidia key is present, so its request would go out first if the check did not stop the command.
        CliRunResult result = await RunAsync("run --tier all", Settings(("NVIDIA_API_KEY", Key())), endpoints);

        Assert.Equal(3, result.ExitCode);
        Assert.Contains("NEBIUS_API_KEY", SingleStderrLine(result), StringComparison.Ordinal);
        Assert.Equal(0, nvidia.RequestCount);
        Assert.Equal(0, nebius.RequestCount);
        Assert.Empty(result.DeniedRequests);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_key_that_is_empty_or_white_space_counts_as_missing(string value)
    {
        CliRunResult result = await RunAsync("run --tier nvidia --dry-run", Settings(("NVIDIA_API_KEY", value)));

        Assert.Equal(3, result.ExitCode);
        Assert.Contains("NVIDIA_API_KEY", SingleStderrLine(result), StringComparison.Ordinal);
    }

    // ---- criterion 5: the judge key ----

    [Fact]
    public async Task Missing_judge_key_outside_dry_run_only_warns_and_the_command_continues()
    {
        var nebius = new FakeHttpMessageHandler();

        // The judge tier is nvidia (the default), whose key is absent; the selected tier is nebius, whose key is present.
        CliRunResult result = await RunAsync(
            "run --tier nebius",
            Settings(("NEBIUS_API_KEY", Key())),
            new Dictionary<string, HttpMessageHandler> { ["nebius"] = nebius });

        Assert.Equal(0, result.ExitCode);
        string warning = SingleStderrLine(result);
        Assert.StartsWith("Warning: judge key NVIDIA_API_KEY is not set", warning, StringComparison.Ordinal);
        Assert.Empty(result.ErrorLines);
        Assert.Equal(1, nebius.RequestCount);
    }

    [Fact]
    public async Task Missing_judge_key_joins_the_same_message_in_dry_run()
    {
        CliRunResult result = await RunAsync("run --tier nebius --dry-run", Settings(("NEBIUS_API_KEY", Key())));

        Assert.Equal(3, result.ExitCode);
        string line = SingleStderrLine(result);
        Assert.Contains("NVIDIA_API_KEY", line, StringComparison.Ordinal);
        Assert.DoesNotContain("NEBIUS_API_KEY", line, StringComparison.Ordinal);
        Assert.Contains("<value>", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Judge_key_that_is_also_a_workflow_key_is_named_once()
    {
        CliRunResult result = await RunAsync("run --tier nvidia --dry-run", Settings());

        Assert.Equal(3, result.ExitCode);
        string line = SingleStderrLine(result);
        Assert.Equal(1, Regex.Count(line, "NVIDIA_API_KEY", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Judge_tier_without_a_key_variable_has_no_judge_key_to_check()
    {
        IConfiguration configuration = ConfigurationFixtures.FromJsonFile(ConfigurationFixtures.AppSettingsPath(), ("AgentJoules:Judge:Tier", "local"), ("NEBIUS_API_KEY", Key()));
        var service = new PreflightService(configuration, Options.Create(OptionsLoader.Load(configuration)));

        KeyCheckResult dryRun = service.CheckKeys([Tier.Nebius], dryRun: true);
        KeyCheckResult run = service.CheckKeys([Tier.Nebius], dryRun: false);

        Assert.Null(dryRun.MissingJudgeVariable);
        Assert.Null(run.MissingJudgeVariable);
    }

    [Fact]
    public void Missing_judge_key_outside_dry_run_is_returned_for_the_warning_and_a_present_one_is_not()
    {
        PreflightService missing = ServiceFor(("NEBIUS_API_KEY", Key()));
        PreflightService present = ServiceFor(("NEBIUS_API_KEY", Key()), ("NVIDIA_API_KEY", Key()));

        Assert.Equal("NVIDIA_API_KEY", missing.CheckKeys([Tier.Nebius], dryRun: false).MissingJudgeVariable);
        Assert.Null(present.CheckKeys([Tier.Nebius], dryRun: false).MissingJudgeVariable);
        Assert.Null(present.CheckKeys([Tier.Nebius], dryRun: true).MissingJudgeVariable);

        MissingKeyException failure = Assert.Throws<MissingKeyException>(() => missing.CheckKeys([Tier.Nebius], dryRun: true));
        Assert.Equal(["NVIDIA_API_KEY"], failure.Variables);
    }

    // ---- criterion 6: the SSH variables of cloudgpu ----

    [Theory]
    [InlineData("CLOUDGPU_SSH_HOST", "CLOUDGPU_SSH_USER")]
    [InlineData("CLOUDGPU_SSH_USER", "CLOUDGPU_SSH_HOST")]
    public async Task Missing_ssh_variable_for_cloudgpu_exits_3_when_RequireSshVariables_is_true(string absent, string present)
    {
        CliRunResult result = await RunAsync(
            "run --tier cloudgpu --dry-run",
            Settings(("AgentJoules:Tiers:cloudgpu:Enabled", "true"), ("AgentJoules:CloudGpu:RequireSshVariables", "true"), ("NVIDIA_API_KEY", Key()), (present, Key())));

        Assert.Equal(3, result.ExitCode);
        string line = SingleStderrLine(result);
        Assert.Contains(absent, line, StringComparison.Ordinal);
        Assert.DoesNotContain(present, line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Both_ssh_variables_are_named_when_both_are_missing()
    {
        CliRunResult result = await RunAsync(
            "run --tier cloudgpu --dry-run",
            Settings(("AgentJoules:Tiers:cloudgpu:Enabled", "true"), ("AgentJoules:CloudGpu:RequireSshVariables", "true"), ("NVIDIA_API_KEY", Key())));

        Assert.Equal(3, result.ExitCode);
        string line = SingleStderrLine(result);
        Assert.Contains("CLOUDGPU_SSH_HOST, CLOUDGPU_SSH_USER", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_ssh_variables_do_not_matter_on_the_VM_where_RequireSshVariables_is_false()
    {
        CliRunResult result = await RunAsync(
            "run --tier cloudgpu --dry-run",
            Settings(("AgentJoules:Tiers:cloudgpu:Enabled", "true"), ("AgentJoules:CloudGpu:RequireSshVariables", "false"), ("NVIDIA_API_KEY", Key())));

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain(result.StderrLines, line => line.Length > 0);
    }

    [Fact]
    public void Ssh_variables_are_only_checked_for_a_selected_cloudgpu_tier()
    {
        PreflightService service = ServiceFor(("AgentJoules:Tiers:cloudgpu:Enabled", "true"), ("NVIDIA_API_KEY", Key()), ("NEBIUS_API_KEY", Key()));

        _ = service.CheckKeys([Tier.Local, Tier.Nvidia, Tier.Nebius], dryRun: true);
        MissingKeyException failure = Assert.Throws<MissingKeyException>(() => service.CheckKeys([Tier.Local, Tier.CloudGpu], dryRun: true));

        Assert.Equal([PreflightService.SshHostVariable, PreflightService.SshUserVariable], failure.Variables);
    }

    [Fact]
    public void CloudGpu_section_defaults_to_requiring_the_ssh_variables_and_binds_both_values()
    {
        Assert.True(new CloudGpuOptions().RequireSshVariables);
        Assert.True(new AgentJoulesOptions().CloudGpu.RequireSshVariables);

        IConfiguration shipped = ConfigurationFixtures.FromJsonFile(ConfigurationFixtures.AppSettingsPath());
        Assert.True(bool.Parse(shipped["AgentJoules:CloudGpu:RequireSshVariables"]!));
        Assert.True(OptionsLoader.Load(shipped).CloudGpu.RequireSshVariables);

        IConfiguration vm = ConfigurationFixtures.FromJsonFile(ConfigurationFixtures.AppSettingsPath(), (ConfigurationFixtures.NormaliseEnvironmentKey("AGENTJOULES__CloudGpu__RequireSshVariables"), "false"));
        Assert.False(OptionsLoader.Load(vm).CloudGpu.RequireSshVariables);
    }

    [Theory]
    [InlineData("maybe")]
    [InlineData("1")]
    public void CloudGpu_RequireSshVariables_that_is_not_true_or_false_is_refused_without_its_value(string value)
    {
        IConfiguration configuration = ConfigurationFixtures.FromJsonFile(ConfigurationFixtures.AppSettingsPath(), ("AgentJoules:CloudGpu:RequireSshVariables", value));

        ConfigurationException failure = Assert.Throws<ConfigurationException>(() => OptionsLoader.Load(configuration));

        Assert.Contains("CloudGpu:RequireSshVariables", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(value, failure.Message.Replace("CloudGpu:RequireSshVariables", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    // ---- the tier selection ----

    [Fact]
    public void All_selects_the_enabled_tiers_in_the_order_of_a_run_and_a_name_selects_one_tier_even_when_disabled()
    {
        PreflightService shipped = ServiceFor();
        PreflightService withCloudGpu = ServiceFor(("AgentJoules:Tiers:cloudgpu:Enabled", "true"));

        Assert.Equal([Tier.Local, Tier.Nvidia, Tier.Nebius], shipped.SelectTiers("all"));
        Assert.Equal([Tier.Local, Tier.CloudGpu, Tier.Nvidia, Tier.Nebius], withCloudGpu.SelectTiers("all"));
        Assert.Equal([Tier.CloudGpu], shipped.SelectTiers("cloudgpu"));
        Assert.Equal([Tier.Nebius], shipped.SelectTiers("nebius"));
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("")]
    [InlineData("nvidia,nebius")]
    public void An_unknown_tier_selection_exits_2_without_echoing_it(string selection)
    {
        PreflightService service = ServiceFor();

        ConfigurationException failure = Assert.Throws<ConfigurationException>(() => service.SelectTiers(selection));

        Assert.Equal(ExitCode.InvalidArguments, failure.ExitCode);
        if (selection.Length > 0)
        {
            Assert.DoesNotContain(selection, failure.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void All_with_no_enabled_tier_exits_2()
    {
        PreflightService service = ServiceFor(
            ("AgentJoules:Tiers:local:Enabled", "false"),
            ("AgentJoules:Tiers:nvidia:Enabled", "false"),
            ("AgentJoules:Tiers:nebius:Enabled", "false"));

        ConfigurationException failure = Assert.Throws<ConfigurationException>(() => service.SelectTiers("all"));

        Assert.Equal(ExitCode.InvalidArguments, failure.ExitCode);
    }

    // ---- the key variable allowlist (carried from AJ-043) ----

    [Theory]
    [InlineData("NVIDIA_API_KEY", true)]
    [InlineData("NEBIUS_API_KEY", true)]
    [InlineData("OTHER_API_KEY", true)]
    [InlineData("_API_KEY", true)]
    [InlineData("GITHUB_TOKEN", false)]
    [InlineData("PATH", false)]
    [InlineData("NVIDIA_API_KEY_BACKUP", false)]
    [InlineData("nvidia_api_key", false)]
    [InlineData("API-KEY", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Key_variable_names_are_limited_to_names_that_end_in_API_KEY(string? name, bool expected)
    {
        Assert.Equal(expected, VariableNames.IsKeyVariable(name));
    }

    [Theory]
    [InlineData("GITHUB_TOKEN")]
    [InlineData("PATH")]
    public void A_configured_key_variable_outside_the_allowlist_is_refused_at_start_without_its_name(string variable)
    {
        IConfiguration configuration = ConfigurationFixtures.FromJsonFile(ConfigurationFixtures.AppSettingsPath(), ("AgentJoules:Tiers:nvidia:ApiKeyVariable", variable));

        ConfigurationException failure = Assert.Throws<ConfigurationException>(() => OptionsLoader.Load(configuration));

        Assert.Contains("Tiers:nvidia:ApiKeyVariable", failure.Message, StringComparison.Ordinal);
        Assert.Contains("_API_KEY", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(variable, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_custom_key_variable_ending_in_API_KEY_is_the_one_checked()
    {
        Dictionary<string, string?> settings = Settings(("AgentJoules:Tiers:nvidia:ApiKeyVariable", "OTHER_API_KEY"), ("NVIDIA_API_KEY", Key()));

        CliRunResult result = await RunAsync("run --tier nvidia --dry-run", settings);

        Assert.Equal(3, result.ExitCode);
        string line = SingleStderrLine(result);
        Assert.Contains("OTHER_API_KEY", line, StringComparison.Ordinal);
        Assert.DoesNotContain("NVIDIA_API_KEY", line, StringComparison.Ordinal);
    }

    // ---- criterion 7 and the definition of done: the harness is deny-by-default ----

    [Fact]
    public async Task Harness_makes_no_real_requests()
    {
        // A harness run with no override: nothing is sent, nothing is denied.
        CliRunResult quiet = await CliHarness.RunAsync("probe").WaitAsync(HangGuard);
        Assert.Equal(0, quiet.ExitCode);
        Assert.Empty(quiet.DeniedRequests);

        // Negative controls: a client that would reach the network (the default handler, and an explicit real SocketsHttpHandler) is denied, the URL is named, and each attempt is counted.
        const string unrouted = "https://unrouted.fake.test/v1/models";
        const string real = "https://real.fake.test/v1/models";
        var fake = new FakeHttpMessageHandler();
        IHost? host = null;
        var send = new Command("fake-send");
        send.SetAction(async (_, cancellationToken) =>
        {
            IHttpClientFactory clients = host!.Services.GetRequiredService<IHttpClientFactory>();
            using HttpResponseMessage routed = await clients.CreateClient("routed").GetAsync(new Uri("https://routed.fake.test/v1/models"), cancellationToken);
            return routed.IsSuccessStatusCode ? await DenyAsync(clients, unrouted, "default", cancellationToken) : 1;
        });

        var overrides = new CliHostOverrides(
            Services: services =>
            {
                _ = services.AddHttpClient("default");
                _ = services.AddHttpClient("real").ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler());
                _ = services.AddHttpClient("routed").ConfigurePrimaryHttpMessageHandler(() => fake);
            },
            AdditionalCommands: [send],
            OnHostStarted: started => host = started);

        CliRunResult denied = await CliHarness.RunAsync("fake-send", overrides).WaitAsync(HangGuard);

        Assert.Equal(1, denied.ExitCode);
        Assert.Equal(1, fake.RequestCount);
        Assert.Equal(unrouted, Assert.Single(denied.DeniedRequests));
        Assert.Contains(unrouted, Assert.Single(denied.ErrorLines), StringComparison.Ordinal);

        // The same for the explicit real handler, in its own run.
        var sendReal = new Command("fake-send-real");
        sendReal.SetAction(async (_, cancellationToken) => await DenyAsync(host!.Services.GetRequiredService<IHttpClientFactory>(), real, "real", cancellationToken));
        CliRunResult deniedReal = await CliHarness.RunAsync("fake-send-real", overrides with { AdditionalCommands = [sendReal] }).WaitAsync(HangGuard);

        Assert.Equal(1, deniedReal.ExitCode);
        Assert.Equal(real, Assert.Single(deniedReal.DeniedRequests));
    }

    private static async Task<int> DenyAsync(IHttpClientFactory clients, string url, string name, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await clients.CreateClient(name).GetAsync(new Uri(url), cancellationToken);
        return response.IsSuccessStatusCode ? 0 : 1;
    }

    [Fact]
    public async Task Harness_reads_neither_the_process_environment_nor_user_secrets_unless_asked()
    {
        var withConfiguration = new CliHostOverrides(Configuration: new Dictionary<string, string?> { ["NVIDIA_API_KEY"] = Key() });
        CliHarnessRun plain = CliHarness.Start("probe");
        CliHarnessRun withMemory = CliHarness.Start("probe", withConfiguration);
        _ = await plain.Result.WaitAsync(HangGuard);
        _ = await withMemory.Result.WaitAsync(HangGuard);

        Assert.DoesNotContain(plain.ConfigurationProviderNames, name => name.Contains("EnvironmentVariables", StringComparison.Ordinal));
        Assert.DoesNotContain(withMemory.ConfigurationProviderNames, name => name.Contains("EnvironmentVariables", StringComparison.Ordinal));

        // One JSON provider is the configuration file: the user-secrets source is a JSON provider too, so a second one would show here.
        Assert.Single(plain.ConfigurationProviderNames, name => name == "JsonConfigurationProvider");
        Assert.Single(withMemory.ConfigurationProviderNames, name => name == "JsonConfigurationProvider");
        Assert.Equal(plain.ConfigurationProviderNames.Count + 1, withMemory.ConfigurationProviderNames.Count);
    }

    [Fact]
    public void Only_the_preflight_service_can_create_a_key_check_result()
    {
        // AJ-054 makes the orchestrator require a result, so nothing else may build one.
        Assert.Empty(typeof(KeyCheckResult).GetConstructors());
    }

    [Fact]
    public void A_second_AddAgentJoulesPreflight_changes_nothing_and_a_second_AddAgentJoulesProviderClients_throws()
    {
        var services = new ServiceCollection();

        _ = services.AddAgentJoulesPreflight().AddAgentJoulesPreflight();
        _ = Assert.Single(services, descriptor => descriptor.ServiceType == typeof(PreflightService));

        _ = services.AddAgentJoulesProviderClients();
        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() => services.AddAgentJoulesProviderClients());
        Assert.Contains("already registered", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Deny_all_handler_throws_naming_the_url_and_counts_every_attempt()
    {
        using var handler = new DenyAllHttpMessageHandler();
        using var client = new HttpClient(handler);

        InvalidOperationException first = Assert.Throws<InvalidOperationException>(() => client.GetAsync(new Uri("https://nebius.fake.test/v1/models")).GetAwaiter().GetResult());
        InvalidOperationException second = Assert.Throws<InvalidOperationException>(() => client.PostAsync(new Uri("https://nvidia.fake.test/v1/chat/completions"), new StringContent("{}")).GetAwaiter().GetResult());

        Assert.Contains("https://nebius.fake.test/v1/models", first.Message, StringComparison.Ordinal);
        Assert.Contains("https://nvidia.fake.test/v1/chat/completions", second.Message, StringComparison.Ordinal);
        Assert.Equal(2, handler.Attempts);
        Assert.Equal(["https://nebius.fake.test/v1/models", "https://nvidia.fake.test/v1/chat/completions"], handler.RequestedUrls);
    }
}