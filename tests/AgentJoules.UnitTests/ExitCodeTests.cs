using System.Runtime.InteropServices;

using AgentJoules.Cli;
using AgentJoules.Core.Cli;
using AgentJoules.Nvml.Interop;
using AgentJoules.TestSupport;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace AgentJoules.UnitTests;

/// <summary>
/// AJ-034 (REQ-006): the exit codes 1, 2 and 6 of the command line, the redacted one-line error, and the shutdown, drain and escaped-exception
/// paths, all through the real application under <see cref="CliHarness"/>. The codes 3, 4, 5 and 7 and the one-line failure contract of AJ-058 are in the nested classes below.
/// </summary>
[Trait("Spec", "REQ-006")]
public sealed class ExitCodeTests
{
    private const string Suffix = "abcdef1234567890";
    private const string StackFramePrefix = "   at ";
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    private static string BuildNvidiaKey() => string.Concat("nvapi-", Suffix);

    private static string BuildOpaqueKey() => Guid.NewGuid().ToString("N");

    [Fact]
    public async Task Unknown_option_exits_2_with_the_errors_on_stderr_not_1()
    {
        CliRunResult result = await CliHarness.RunAsync("probe --bogus");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("--bogus", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_command_exits_2_with_usage_on_stderr()
    {
        CliRunResult result = await CliHarness.RunAsync(string.Empty);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Usage", result.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("probe", result.Stderr, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.Stdout);
    }

    [Fact]
    public async Task Help_exits_0_with_the_commands_on_stdout_and_nothing_on_stderr_and_hides_the_debug_option()
    {
        CliRunResult result = await CliHarness.RunAsync("--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("probe", result.Stdout, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.Stderr);
        Assert.DoesNotContain("debug-throw-on-worker", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Probe_help_exits_0_with_the_probe_options_on_stdout_and_nothing_on_stderr()
    {
        CliRunResult result = await CliHarness.RunAsync("probe --help");

        Assert.Equal(0, result.ExitCode);
        foreach (string option in new[] { "--json", "--save", "--suffix", "--config" })
        {
            Assert.Contains(option, result.Stdout, StringComparison.Ordinal);
        }

        Assert.Equal(string.Empty, result.Stderr);
    }

    [Fact]
    public async Task Precondition_failure_from_a_service_override_exits_6_with_one_error_line()
    {
        CliHostOverrides overrides = CliHarness.Overrides(gpuFactoryThrows: new MeasurementPreconditionException("NVML is unavailable"));

        CliRunResult result = await CliHarness.RunAsync("probe", overrides);

        Assert.Equal(6, result.ExitCode);
        Assert.Equal("error: NVML is unavailable (MeasurementPreconditionException)", Assert.Single(result.ErrorLines));
    }

    [Fact]
    public async Task Precondition_failure_from_a_command_exits_6()
    {
        CliRunResult result = await CliHarness.RunAsync("fake-throw MeasurementPreconditionException counter-not-supported");

        Assert.Equal(6, result.ExitCode);
        Assert.Equal("error: counter-not-supported (MeasurementPreconditionException)", Assert.Single(result.ErrorLines));
    }

    [Fact]
    public async Task Configuration_exception_exits_2()
    {
        CliRunResult result = await CliHarness.RunAsync("fake-throw ConfigurationException bad-setting");

        Assert.Equal(2, result.ExitCode);
        Assert.Equal("error: bad-setting (ConfigurationException)", Assert.Single(result.ErrorLines));
    }

    [Fact]
    public async Task Options_validation_failure_exits_2_and_the_message_names_both_keys()
    {
        CliHostOverrides overrides = CliHarness.Overrides(configuration: [("AgentJoules:Meter:SampleRateHz", "5")]);

        CliRunResult result = await CliHarness.RunAsync("probe", overrides);

        Assert.Equal(2, result.ExitCode);
        string error = Assert.Single(result.ErrorLines);
        Assert.Contains("Meter:SampleRateHz", error, StringComparison.Ordinal);
        Assert.Contains("Meter:AllowBelowMinRate", error, StringComparison.Ordinal);
        Assert.EndsWith("(OptionsValidationException)", error, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Spec", "REQ-017")]
    public async Task Unexpected_exception_exits_1_with_message_and_type_and_no_stack_trace_at_Information()
    {
        CliHostOverrides overrides = CliHarness.Overrides(gpuFactoryThrows: new InvalidOperationException("boom"));

        CliRunResult result = await CliHarness.RunAsync("probe", overrides);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("error: boom (InvalidOperationException)", Assert.Single(result.ErrorLines));
        Assert.DoesNotContain(StackFramePrefix, result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stack_trace_is_written_to_stderr_at_Debug_level()
    {
        CliHostOverrides overrides = CliHarness.Overrides(
            configuration: [("Logging:LogLevel:Default", "Debug")],
            gpuFactoryThrows: new InvalidOperationException("boom"));

        CliRunResult result = await CliHarness.RunAsync("probe", overrides);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("error: boom (InvalidOperationException)", Assert.Single(result.ErrorLines));
        Assert.Contains(StackFramePrefix, result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Multi_line_message_is_folded_into_one_error_line()
    {
        CliRunResult result = await CliHarness.RunAsync("fake-throw InvalidOperationException \"first\r\nsecond\nthird\"");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("error: first second third (InvalidOperationException)", Assert.Single(result.ErrorLines));
    }

    [Fact]
    public async Task Key_in_an_error_message_is_redacted_to_the_public_prefix_without_the_suffix()
    {
        string key = BuildNvidiaKey();
        CliHostOverrides overrides = CliHarness.Overrides(gpuFactoryThrows: new InvalidOperationException("request failed for " + key));

        CliRunResult result = await CliHarness.RunAsync("probe", overrides);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("error: request failed for nvapi-**** (InvalidOperationException)", Assert.Single(result.ErrorLines));
        Assert.DoesNotContain(Suffix, result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(Suffix, result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Registered_configuration_keys_are_redacted()
    {
        string nvidia = BuildOpaqueKey();
        string nebius = BuildOpaqueKey();
        string ngc = BuildOpaqueKey();
        CliHostOverrides overrides = CliHarness.Overrides(
            configuration: [("NVIDIA_API_KEY", nvidia), ("NEBIUS_API_KEY", nebius), ("NGC_API_KEY", ngc)]);

        CliRunResult result = await CliHarness.RunAsync($"fake-throw InvalidOperationException keys:{nvidia},{nebius},{ngc}", overrides);

        // N-09: a registered opaque value takes the full mask, not the nvapi- prefix form.
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("error: keys:****,****,**** (InvalidOperationException)", Assert.Single(result.ErrorLines));
        foreach (string key in new[] { nvidia, nebius, ngc })
        {
            Assert.DoesNotContain(key, result.Stderr, StringComparison.Ordinal);
            Assert.DoesNotContain(key, result.Stdout, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Process_environment_is_not_a_configuration_source_under_the_default_overrides()
    {
        // PATH exists on every machine, so the assertion is meaningful even where no NVIDIA key is set; the key is read by presence only, never copied.
        Assert.False(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PATH")), "the machine has no PATH variable to prove isolation with");

        CliHarnessRun run = CliHarness.Start("probe");
        _ = await run.Result;

        Assert.True(run.HostStarted, "the probe command never started a host");
        Assert.True(string.IsNullOrEmpty(run.Setting("NVIDIA_API_KEY")));
        Assert.True(string.IsNullOrEmpty(run.Setting("NEBIUS_API_KEY")));
        Assert.True(string.IsNullOrEmpty(run.Setting("NGC_API_KEY")));
        Assert.Null(run.Setting("PATH"));
    }

    [Fact]
    public async Task Shutdown_cancels_command_and_exits_5()
    {
        using var guard = new CancellationTokenSource(HangGuard);
        CliHarnessRun run = CliHarness.Start("fake-wait");
        await Task.WhenAny(run.CommandWaiting, run.Result).WaitAsync(guard.Token);
        if (!run.CommandWaiting.IsCompleted)
        {
            Assert.Fail("the command never started waiting; run ended with: " + (await run.Result).Stderr);
        }

        await run.RaiseShutdownAsync(guard.Token);
        CliRunResult result = await run.Result.WaitAsync(guard.Token);

        Assert.True(run.CancellationObserved);
        Assert.Equal(5, result.ExitCode);
        Assert.Equal("error: interrupted by shutdown (OperationCanceledException)", Assert.Single(result.ErrorLines));
        Assert.NotNull(run.InvocationConfiguration);
        Assert.Equal((TimeSpan?)TimeSpan.FromSeconds(15), run.InvocationConfiguration.ProcessTerminationTimeout);
    }

    [Fact]
    public async Task Unrelated_cancellation_exits_1()
    {
        CliRunResult result = await CliHarness.RunAsync("fake-cancel");

        Assert.Equal(1, result.ExitCode);
        string error = Assert.Single(result.ErrorLines);
        Assert.EndsWith("(OperationCanceledException)", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelling_the_caller_token_while_a_command_waits_exits_5()
    {
        // System.CommandLine cancels the token handed to RunAsync on SIGINT and SIGTERM; cancelling it here is the same chain.
        using var guard = new CancellationTokenSource(HangGuard);
        using var caller = new CancellationTokenSource();
        CliHarnessRun run = CliHarness.Start("fake-wait", cancellationToken: caller.Token);
        await Task.WhenAny(run.CommandWaiting, run.Result).WaitAsync(guard.Token);
        if (!run.CommandWaiting.IsCompleted)
        {
            Assert.Fail("the command never started waiting; run ended with: " + (await run.Result).Stderr);
        }

        await caller.CancelAsync();
        CliRunResult result = await run.Result.WaitAsync(guard.Token);

        Assert.True(run.CancellationObserved);
        Assert.Equal(5, result.ExitCode);
        Assert.Equal("error: interrupted by shutdown (OperationCanceledException)", Assert.Single(result.ErrorLines));
    }

    [Fact]
    public async Task Options_failure_prints_exactly_one_line_at_the_default_level()
    {
        CliHostOverrides overrides = CliHarness.Overrides(configuration: [("AgentJoules:Meter:SampleRateHz", "5")]);

        CliRunResult result = await CliHarness.RunAsync("probe", overrides);

        Assert.Equal(2, result.ExitCode);
        Assert.Single(result.StderrLines, line => line.Length > 0);
        Assert.DoesNotContain(StackFramePrefix, result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("fail:", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_key_exits_2_naming_the_key_with_exactly_one_line()
    {
        CliHostOverrides overrides = CliHarness.Overrides(configuration: [("AgentJoules:Gpu:Idx", "1")]);

        CliRunResult result = await CliHarness.RunAsync("probe", overrides);

        Assert.Equal(2, result.ExitCode);
        string line = Assert.Single(result.StderrLines, line => line.Length > 0);
        Assert.Contains("Idx", line, StringComparison.Ordinal);
        Assert.EndsWith("(ConfigurationException)", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_config_file_exits_2_naming_the_path()
    {
        using TempWorkspace workspace = TempWorkspace.Create();

        CliRunResult result = await CliHarness.RunAsync("probe --config absent.json", new CliHostOverrides(WorkingDirectory: workspace.Root));

        Assert.Equal(2, result.ExitCode);
        string line = Assert.Single(result.ErrorLines);
        Assert.Contains("absent.json", line, StringComparison.Ordinal);
        Assert.EndsWith("(ConfigurationException)", line, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{ \"AgentJoules\": ", "truncated")]
    [InlineData("{ \"AgentJoules\": { \"Gpu\": { \"Index\": 1, \"Index\": 2 } } }", "duplicated")]
    public async Task Unreadable_config_file_exits_2_naming_the_path_and_never_the_content(string content, string innerWording)
    {
        using TempWorkspace workspace = TempWorkspace.Create();
        workspace.WriteFile("bad.json", content);

        CliRunResult result = await CliHarness.RunAsync("probe --config bad.json", new CliHostOverrides(WorkingDirectory: workspace.Root));

        Assert.Equal(2, result.ExitCode);
        string line = Assert.Single(result.ErrorLines);
        Assert.Contains("bad.json", line, StringComparison.Ordinal);
        Assert.DoesNotContain("Index", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(innerWording, result.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("custom.json")]
    [InlineData(".aj.json")]
    [InlineData("nested/dir/custom.json")]
    public async Task Config_path_resolves_against_the_working_directory_even_when_it_starts_with_a_dot(string relativePath)
    {
        using TempWorkspace workspace = TempWorkspace.Create();
        workspace.WriteFile(relativePath, File.ReadAllText(ConfigurationFixtures.AppSettingsPath()));

        CliRunResult result = await CliHarness.RunAsync("probe --config " + relativePath, new CliHostOverrides(WorkingDirectory: workspace.Root));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("no GPU", result.Stdout.Trim());
        Assert.Equal(string.Empty, result.Stderr);
    }

    [Fact]
    public async Task Invalid_logging_level_exits_2_naming_the_key_and_never_the_value()
    {
        string value = "Loud" + Guid.NewGuid().ToString("N")[..8];
        CliHostOverrides overrides = CliHarness.Overrides(configuration: [("Logging:LogLevel:Default", value)]);

        CliRunResult result = await CliHarness.RunAsync("probe", overrides);

        Assert.Equal(2, result.ExitCode);
        string line = Assert.Single(result.StderrLines, line => line.Length > 0);
        Assert.Contains("Logging:LogLevel:Default", line, StringComparison.Ordinal);
        Assert.DoesNotContain(value, result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task User_secrets_and_environment_variables_are_not_sources_under_the_default_overrides()
    {
        CliHarnessRun run = CliHarness.Start("probe");
        _ = await run.Result;

        // The file is the only JSON-based provider: user secrets would add a second one. The environment provider is absent by name.
        Assert.True(run.HostStarted, "the probe command never started a host");
        Assert.Single(run.ConfigurationProviderNames, name => name.StartsWith("Json", StringComparison.Ordinal));
        Assert.DoesNotContain(run.ConfigurationProviderNames, name => name.Contains("Environment", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Environment_variables_are_a_configuration_source_on_the_production_path()
    {
        // The positive control of the isolation test above, in a child process whose environment is built from scratch: the witness variable is
        // the only AGENTJOULES__ setting it has, so the real variables of the machine never enter a test process.
        var environment = new Dictionary<string, string> { ["AGENTJOULES__Meter__SampleRateHz"] = "5" };

        CliRunResult result = await CliChildProcess.RunAsync(["probe"], TimeSpan.FromSeconds(60), environment);

        Assert.Equal(2, result.ExitCode);
        string line = Assert.Single(result.ErrorLines);
        Assert.Contains("Meter:SampleRateHz", line, StringComparison.Ordinal);
        Assert.Contains("Meter:AllowBelowMinRate", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Config_path_that_is_a_directory_exits_2_saying_it_is_not_a_file()
    {
        using TempWorkspace workspace = TempWorkspace.Create();

        CliRunResult result = await CliHarness.RunAsync("probe --config config", new CliHostOverrides(WorkingDirectory: workspace.Root));

        Assert.Equal(2, result.ExitCode);
        string line = Assert.Single(result.ErrorLines);
        Assert.Contains("is not a file", line, StringComparison.Ordinal);
        Assert.DoesNotContain("not found", line, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Logging:Console:MaxQueueLength")]
    [InlineData("Logging:Console:QueueFullMode")]
    [InlineData("Logging:Console:FormatterName")]
    public async Task Console_logger_options_in_the_configuration_are_ignored_and_never_echoed(string key)
    {
        string value = "notanumber-" + Guid.NewGuid().ToString("N")[..8];
        CliHostOverrides overrides = CliHarness.Overrides(configuration: [(key, value), ("Logging:LogLevel:Default", "Debug")]);

        CliRunResult result = await CliHarness.RunAsync("probe", overrides);

        // Exit 0 proves the console key did not reach the binder; the Debug record proves the LogLevel keys still did.
        Assert.Equal(0, result.ExitCode);
        Assert.Contains(result.StderrLines, line => line.StartsWith("dbug:", StringComparison.Ordinal));
        Assert.DoesNotContain(value, result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(value, result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exclusive_section_is_not_reentrant_and_refuses_runs_started_inside_it()
    {
        using var guard = new CancellationTokenSource(HangGuard);

        Task nested = CliHarness.RunExclusiveAsync(() => CliHarness.RunExclusiveAsync(() => Task.FromResult(0)));
        Task run = CliHarness.RunExclusiveAsync(() => CliHarness.RunAsync("--help"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => nested.WaitAsync(guard.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => run.WaitAsync(guard.Token));
    }

    [Fact]
    public async Task Host_stop_failure_keeps_the_exit_code_and_prints_no_error_line()
    {
        var overrides = new CliHostOverrides(Services: services => services.AddHostedService<ThrowOnStopService>());

        CliRunResult result = await CliHarness.RunAsync("fake-write", overrides);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(FakeCommands.JsonDocument, result.Stdout);
        Assert.Equal(string.Empty, result.Stderr);
    }

    [Fact]
    public async Task Host_stop_failure_is_logged_at_Debug()
    {
        CliHostOverrides overrides = new(
            Services: services => services.AddHostedService<ThrowOnStopService>(),
            Configuration: new Dictionary<string, string?> { ["Logging:LogLevel:Default"] = "Debug" });

        CliRunResult result = await CliHarness.RunAsync("fake-write", overrides);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("failed to stop", result.Stderr, StringComparison.Ordinal);
        Assert.Empty(result.ErrorLines);
    }

    [Fact]
    public async Task Output_without_trailing_newline_is_drained_at_exit()
    {
        CliRunResult result = await CliHarness.RunAsync("fake-write");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(FakeCommands.JsonDocument, result.Stdout);
    }

    [Fact]
    public async Task Unhandled_worker_thread_exception_is_redacted_and_exits_1()
    {
        string message = "worker failed " + BuildNvidiaKey();

        CliRunResult result = await CliChildProcess.RunAsync([CliChildProcess.WorkerThrowOption, message], TimeSpan.FromSeconds(60));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("error:", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("nvapi-****", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(Suffix, result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(Suffix, result.Stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(typeof(MeasurementPreconditionException), false, 6)]
    [InlineData(typeof(ConfigurationException), false, 2)]
    [InlineData(typeof(MeasurementPreconditionException), true, 6)]
    [InlineData(typeof(InvalidOperationException), false, 1)]
    [InlineData(typeof(InvalidOperationException), true, 1)]
    [InlineData(typeof(OperationCanceledException), true, 5)]
    [InlineData(typeof(TaskCanceledException), true, 5)]
    [InlineData(typeof(OperationCanceledException), false, 1)]
    [InlineData(typeof(TaskCanceledException), false, 1)]
    public void Mapper_maps_each_exception_family_to_its_code(Type type, bool shutdownRequested, int expected)
    {
        var exception = (Exception)Activator.CreateInstance(type, "mapped")!;

        Assert.Equal(expected, ExitCodeMapper.Map(exception, shutdownRequested));
    }

    [Fact]
    public void Mapper_returns_the_code_of_any_AgentJoulesException()
    {
        Assert.Equal(4, ExitCodeMapper.Map(new AgentJoulesException(ExitCode.ModelNotFound, "absent"), shutdownRequested: false));
        Assert.Equal(7, ExitCodeMapper.Map(new AgentJoulesException(ExitCode.ContentDiffers, "differs"), shutdownRequested: true));
    }

    [Fact]
    public void Mapper_maps_OptionsValidationException_to_2()
    {
        var exception = new OptionsValidationException("AgentJoules", typeof(object), ["Meter:SampleRateHz is below 10 Hz"]);

        Assert.Equal(2, ExitCodeMapper.Map(exception, shutdownRequested: false));
    }

    // The three tests below moved here from PowerIntegrationTests (the AJ-030 carry-forward): they assert the exit code of a failure, which is
    // REQ-006 material under a REQ-004 class trait. Each keeps its original trait on the method.

    [Theory]
    [Trait("Spec", "REQ-004")]
    [InlineData(3, MeterSelectionFixtures.NotSupportedReason)]
    [InlineData(999, "counter-not-supported: 999")]
    [Trait("Spec", "EDGE-002")]
    public void Forced_counter_with_the_counter_unsupported_is_a_precondition_failure_naming_the_setting_and_the_reason(int code, string reason)
    {
        MeasurementPreconditionException failure = MeterSelectionFixtures.SelectionFails<MeasurementPreconditionException>("counter", (NvmlReturn)code);

        Assert.Equal(ExitCode.PreconditionFailed, failure.ExitCode);
        Assert.Equal(6, (int)failure.ExitCode);
        Assert.Contains("Meter:Method=counter", failure.Message, StringComparison.Ordinal);
        Assert.Contains(reason, failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [Trait("Spec", "REQ-004")]
    [InlineData("turbo")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void An_unknown_missing_or_blank_method_is_a_configuration_error_naming_the_setting(string? configured)
    {
        ConfigurationException failure = MeterSelectionFixtures.SelectionFails<ConfigurationException>(configured);

        Assert.Equal(ExitCode.InvalidArguments, failure.ExitCode);
        Assert.Equal(2, (int)failure.ExitCode);
        Assert.Contains("Meter:Method", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [Trait("Spec", "REQ-004")]
    [InlineData(0.0)]
    [InlineData(-10.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void A_non_positive_or_non_finite_sample_rate_is_a_configuration_error_naming_the_setting(double rateHz)
    {
        ConfigurationException failure = MeterSelectionFixtures.SelectionFails<ConfigurationException>("auto", rateHz: rateHz);

        Assert.Equal(ExitCode.InvalidArguments, failure.ExitCode);
        Assert.Contains("Meter:SampleRateHz", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExitCode_values_match_the_documented_list()
    {
        // The numbers are the documented contract; the enum must hold exactly these eight members.
        Assert.Equal(0, (int)ExitCode.Success);
        Assert.Equal(1, (int)ExitCode.Unexpected);
        Assert.Equal(2, (int)ExitCode.InvalidArguments);
        Assert.Equal(3, (int)ExitCode.MissingKey);
        Assert.Equal(4, (int)ExitCode.ModelNotFound);
        Assert.Equal(5, (int)ExitCode.Unreachable);
        Assert.Equal(6, (int)ExitCode.PreconditionFailed);
        Assert.Equal(7, (int)ExitCode.ContentDiffers);
        Assert.Equal(8, Enum.GetValues<ExitCode>().Length);
    }

    /// <summary>
    /// AJ-054 (REQ-017): the exit codes the <c>run</c> command reaches in M2, one row each, through the real command under the harness with the orchestrator double: 1 for an output
    /// directory that cannot be written, 2 for a value outside the allowed ones, 3 for a missing key, 4 for a model ID that is not listed, 5 for a failed step and 6 for a measured tier
    /// without a GPU. Code 7 belongs to <c>report --verify-readme</c> (M6, AJ-144). A nested class, so the <c>Spec=REQ-006</c> trait of the probe tests does not tag these rows.
    /// </summary>
    [Trait("Spec", "REQ-017")]
    public sealed class RunCommandCodes
    {
        [Theory]
        [InlineData(1, "run --tier nvidia --out occupied", "(AgentJoulesException)")]
        [InlineData(2, "run --tier bogus", "(ParseError)")]
        [InlineData(3, "run --tier nebius --dry-run", "(MissingKeyException)")]
        [InlineData(4, "run --tier nvidia --dry-run", "(ModelNotFoundException)")]
        [InlineData(5, "run --tier nvidia", "(StepFailedException)")]
        [InlineData(6, "run --tier local --dry-run", "(MeasurementPreconditionException)")]
        public async Task The_run_command_maps_each_failure_to_its_exit_code(int expected, string arguments, string errorType)
        {
            using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
            _ = workspace.WriteFile("occupied", "this path is a file");
            var nvidia = new FakeHttpMessageHandler { ModelIds = expected == 4 ? ["acme/other"] : ConfigurationFixtures.ShippedModelIds() };
            if (expected == 5)
            {
                nvidia.SetStatuses(FakeRoute.ChatCompletions, 500);
            }

            // The failed step runs through the production orchestrator (AJ-056): the real workflow raises StepFailedException for the Planner's 500; the other rows keep the double.
            var setup = new RunCommandSetup { WorkingDirectory = workspace.Root, UseProductionOrchestrator = expected == 5 };
            setup.Settings["NVIDIA_API_KEY"] = ProviderClientFixtures.FakeKey();
            setup.Settings["AgentJoules:Tiers:nvidia:BaseUrl"] = ProviderClientFixtures.BaseUrl(Core.Runs.Tier.Nvidia);
            setup.Endpoints["nvidia"] = nvidia;
            setup.Endpoints["local"] = new FakeHttpMessageHandler { ModelIds = ConfigurationFixtures.ShippedModelIds() };

            CliRunResult result = await RunCommandHarness.RunAsync(arguments, setup).WaitAsync(HangGuard);

            Assert.True(expected == result.ExitCode, $"Expected exit code {expected} but got {result.ExitCode}; stderr: {result.Stderr}");
            Assert.EndsWith(errorType, Assert.Single(result.ErrorLines), StringComparison.Ordinal);
            if (expected == 2)
            {
                // The parse error of the tier rule, not an unrecognized command.
                Assert.Contains("Must be one of", result.Stderr, StringComparison.Ordinal);
                Assert.DoesNotContain("Unrecognized command", result.Stderr, StringComparison.Ordinal);
            }

            Assert.DoesNotContain(StackFramePrefix, result.Stderr, StringComparison.Ordinal);
            Assert.Equal(0, setup.Orchestrator.Runs);
            if (expected == 5)
            {
                Assert.True(nvidia.Count(FakeRoute.ChatCompletions) >= 1, "The production orchestrator made no completion request.");
            }
        }
    }

    /// <summary>
    /// AJ-058 (REQ-017, EDGE-001): the one-line failure contract through the real application under the harness: a refused connection (5), a service override that throws
    /// <see cref="ContentDiffersException"/> (7), the missing NVML library of a measured tier (6, EDGE-001), the parse error that folds into one <c>error:</c> line (2) and the redaction of a key
    /// that a failing endpoint echoes. A nested class, so the <c>Spec=REQ-006</c> trait of the outer class does not tag these tests.
    /// </summary>
    [Trait("Spec", "REQ-017")]
    public sealed class FailureLines
    {
        private static string LibraryName => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "nvml.dll" : "libnvidia-ml.so.1";

        // One hosted tier (nvidia, also the judge tier) with a fake key, a fake base URL and the given endpoint.
        private static RunCommandSetup NvidiaSetup(FakeHttpMessageHandler nvidia, string? key = null)
        {
            var setup = new RunCommandSetup();
            setup.Settings["NVIDIA_API_KEY"] = key ?? ProviderClientFixtures.FakeKey();
            setup.Settings["AgentJoules:Tiers:nvidia:BaseUrl"] = ProviderClientFixtures.BaseUrl(Core.Runs.Tier.Nvidia);
            setup.Endpoints["nvidia"] = nvidia;
            return setup;
        }

        // The failure printed exactly one line starting with "error:", ending in the type, with no stack frame at the default level.
        private static string AssertOneErrorLine(CliRunResult result, string typeName)
        {
            string line = Assert.Single(result.ErrorLines);
            Assert.EndsWith("(" + typeName + ")", line, StringComparison.Ordinal);
            Assert.DoesNotContain(StackFramePrefix, result.Stderr, StringComparison.Ordinal);
            return line;
        }

        // No 6-character window of the secret part may be shown (a whole-value check would miss a truncated echo).
        private static void AssertNoWindowOf(string secret, string text)
        {
            for (int start = 0; start + 6 <= secret.Length; start++)
            {
                Assert.DoesNotContain(secret.Substring(start, 6), text, StringComparison.Ordinal);
            }
        }

        [Fact]
        public async Task A_refused_connection_exits_5_with_one_error_line_and_no_completion_call()
        {
            var nvidia = new FakeHttpMessageHandler { ModelIds = ConfigurationFixtures.ShippedModelIds(), ConnectionRefused = true };
            RunCommandSetup setup = NvidiaSetup(nvidia);

            CliRunResult result = await RunCommandHarness.RunAsync("run --tier nvidia --dry-run", setup).WaitAsync(HangGuard);

            Assert.True(result.ExitCode == 5, $"Expected exit code 5 but got {result.ExitCode}; stderr: {result.Stderr}");
            _ = AssertOneErrorLine(result, "EndpointUnreachableException");
            Assert.True(nvidia.Count(FakeRoute.Models) >= 1, "The refusing endpoint received no models request, so the test proved nothing.");
            Assert.Equal(0, nvidia.Count(FakeRoute.ChatCompletions));
            Assert.Equal(0, setup.Orchestrator.Runs);
            Assert.Empty(result.DeniedRequests);
        }

        [Fact]
        public async Task A_service_override_that_throws_ContentDiffersException_exits_7_with_one_error_line()
        {
            CliHostOverrides overrides = CliHarness.Overrides(gpuFactoryThrows: new ContentDiffersException("README results block differs from the generated one"));

            CliRunResult result = await CliHarness.RunAsync("probe", overrides).WaitAsync(HangGuard);

            Assert.Equal(7, result.ExitCode);
            Assert.Equal("error: README results block differs from the generated one (ContentDiffersException)", AssertOneErrorLine(result, "ContentDiffersException"));
        }

        [Fact]
        [Trait("Spec", "EDGE-001")]
        public async Task Run_tier_local_without_the_NVML_library_exits_6_naming_it_and_makes_no_completion_call()
        {
            var local = new FakeHttpMessageHandler { ModelIds = ConfigurationFixtures.ShippedModelIds() };
            RunCommandSetup setup = NvidiaSetup(new FakeHttpMessageHandler { ModelIds = ConfigurationFixtures.ShippedModelIds() });
            setup.Endpoints["local"] = local;
            setup.Gpu = new ScriptedNvmlApi(new FakeTimeProvider()) { MissingLibrary = true };

            // The production orchestrator is in the path, so "no completion call" is a real claim: the GPU check fails in the preflight, before any orchestrator runs.
            setup.UseProductionOrchestrator = true;

            CliRunResult result = await RunCommandHarness.RunAsync("run --tier local --repeat 1", setup).WaitAsync(HangGuard);

            Assert.True(result.ExitCode == 6, $"Expected exit code 6 but got {result.ExitCode}; stderr: {result.Stderr}");
            Assert.Contains(LibraryName, AssertOneErrorLine(result, "MeasurementPreconditionException"), StringComparison.Ordinal);
            Assert.True(local.Count(FakeRoute.Models) >= 1, "The local endpoint received no models request, so the preflight did not run.");
            Assert.Equal(0, local.Count(FakeRoute.ChatCompletions));
            Assert.Empty(result.DeniedRequests);
        }

        [Fact]
        public async Task A_missing_key_exits_3_with_one_error_line_and_makes_no_request()
        {
            // No key anywhere: neither NEBIUS_API_KEY nor NVIDIA_API_KEY (the key of the judge tier) is in the configuration.
            var nvidia = new FakeHttpMessageHandler { ModelIds = ConfigurationFixtures.ShippedModelIds() };
            var nebius = new FakeHttpMessageHandler { ModelIds = ConfigurationFixtures.ShippedModelIds() };
            var setup = new RunCommandSetup();
            setup.Settings["AgentJoules:Tiers:nvidia:BaseUrl"] = ProviderClientFixtures.BaseUrl(Core.Runs.Tier.Nvidia);
            setup.Settings["AgentJoules:Tiers:nebius:BaseUrl"] = ProviderClientFixtures.BaseUrl(Core.Runs.Tier.Nebius);
            setup.Endpoints["nvidia"] = nvidia;
            setup.Endpoints["nebius"] = nebius;

            CliRunResult result = await RunCommandHarness.RunAsync("run --tier nebius --dry-run", setup).WaitAsync(HangGuard);

            Assert.True(result.ExitCode == 3, $"Expected exit code 3 but got {result.ExitCode}; stderr: {result.Stderr}");
            _ = AssertOneErrorLine(result, "MissingKeyException");
            Assert.Equal(0, nvidia.RequestCount);
            Assert.Equal(0, nebius.RequestCount);
            Assert.Empty(result.DeniedRequests);
            Assert.Equal(0, setup.Orchestrator.Runs);
        }

        [Fact]
        public async Task A_parse_error_prints_one_error_line_typed_ParseError_and_exits_2()
        {
            CliRunResult result = await CliHarness.RunAsync("run --tier bogus").WaitAsync(HangGuard);

            Assert.Equal(2, result.ExitCode);
            string line = AssertOneErrorLine(result, "ParseError");
            Assert.Contains("Must be one of", line, StringComparison.Ordinal);
            Assert.DoesNotContain("Unrecognized command", result.Stderr, StringComparison.Ordinal);
            Assert.Equal(line, Assert.Single(result.StderrLines, static text => text.Length > 0));
            Assert.Equal(string.Empty, result.Stdout);
        }

        [Fact]
        public async Task Two_parse_errors_at_once_still_print_one_error_line_holding_both_messages()
        {
            CliRunResult result = await CliHarness.RunAsync("run --tier bogus --repeat zero").WaitAsync(HangGuard);

            Assert.Equal(2, result.ExitCode);
            string line = AssertOneErrorLine(result, "ParseError");
            Assert.Contains("Must be one of", line, StringComparison.Ordinal);
            Assert.Contains("--repeat", line, StringComparison.Ordinal);
            Assert.Contains("; ", line, StringComparison.Ordinal);
            Assert.Equal(line, Assert.Single(result.StderrLines, static text => text.Length > 0));
            Assert.Equal(string.Empty, result.Stdout);
        }

        [Fact]
        public async Task A_parse_error_line_masks_a_key_the_user_typed()
        {
            string key = "nvapi-" + Guid.NewGuid().ToString("N");

            CliRunResult result = await CliHarness.RunAsync("run --tier " + key).WaitAsync(HangGuard);

            Assert.Equal(2, result.ExitCode);
            string line = AssertOneErrorLine(result, "ParseError");

            // The echo happened (System.CommandLine quotes the rejected value) and was masked to the public prefix.
            Assert.Contains("'nvapi-****'", line, StringComparison.Ordinal);
            AssertNoWindowOf(key["nvapi-".Length..], result.Stderr);
        }

        [Fact]
        public async Task A_registered_key_in_the_second_parse_message_is_masked_in_the_joined_line()
        {
            // An opaque key without the nvapi- prefix is masked only because the configuration registers it; the key is the value of --mode, so it sits in the second message.
            string key = Guid.NewGuid().ToString("N");
            CliHostOverrides overrides = CliHarness.Overrides(configuration: [("NEBIUS_API_KEY", key)]);

            CliRunResult result = await CliHarness.RunTokensAsync(["run", "--tier", "bogus", "--mode", key], overrides).WaitAsync(HangGuard);

            Assert.Equal(2, result.ExitCode);
            string line = AssertOneErrorLine(result, "ParseError");
            Assert.Contains("Must be one of", line, StringComparison.Ordinal);
            Assert.Contains("'****'", line, StringComparison.Ordinal);
            Assert.DoesNotContain(key, result.Stderr, StringComparison.Ordinal);
            AssertNoWindowOf(key, result.Stderr);
        }

        [Fact]
        public async Task A_hostile_token_cannot_forge_a_second_line_or_smuggle_a_control_character_into_the_parse_error_line()
        {
            // A line feed followed by a forged "error:" prefix, and a right-to-left override written as a cast, never literally.
            string hostile = "x\nerror: forged" + (char)0x202E;

            CliRunResult result = await CliHarness.RunTokensAsync(["run", "--tier", hostile]).WaitAsync(HangGuard);

            Assert.Equal(2, result.ExitCode);
            string line = Assert.Single(result.StderrLines, static text => text.Length > 0);
            Assert.StartsWith("error: ", line, StringComparison.Ordinal);
            Assert.EndsWith(" (ParseError)", line, StringComparison.Ordinal);
            Assert.DoesNotContain('\n', result.Stderr.TrimEnd('\r', '\n'));
            Assert.DoesNotContain((char)0x202E, result.Stderr);
            _ = AssertOneErrorLine(result, "ParseError");
        }

        [Fact]
        public async Task A_failing_endpoint_that_echoes_the_key_leaves_no_part_of_it_in_the_error_line()
        {
            string key = "nvapi-" + Guid.NewGuid().ToString("N");
            var nvidia = new FakeHttpMessageHandler { ModelIds = ConfigurationFixtures.ShippedModelIds() };
            nvidia.SetStatuses(FakeRoute.Models, 401);
            nvidia.CustomizeResponse = (route, response) =>
            {
                response.Content = new StringContent("{\"error\":{\"message\":\"Incorrect API key provided: " + key + "\"}}", System.Text.Encoding.UTF8, "application/json");
                _ = response.Headers.TryAddWithoutValidation("X-Echo", key);
            };
            RunCommandSetup setup = NvidiaSetup(nvidia, key);

            CliRunResult result = await RunCommandHarness.RunAsync("run --tier nvidia --dry-run", setup).WaitAsync(HangGuard);

            Assert.Equal(5, result.ExitCode);
            _ = AssertOneErrorLine(result, "EndpointUnreachableException");
            Assert.True(nvidia.Count(FakeRoute.Models) >= 1, "The failing endpoint received no request, so the test proved nothing.");
            AssertNoWindowOf(key["nvapi-".Length..], result.Stderr);
            AssertNoWindowOf(key["nvapi-".Length..], result.Stdout);
        }
    }

    /// <summary>
    /// AJ-058 (REQ-017): the documented code of each exception class, in the order of the story, and the documented meaning of each code. A nested class, so the <c>Spec=REQ-006</c> trait of the
    /// outer class does not tag these tests.
    /// </summary>
    [Trait("Spec", "REQ-017")]
    public sealed class DocumentedCodes
    {
        [Fact]
        public void The_six_exception_classes_map_to_2_3_4_5_6_and_7_in_that_order()
        {
            Exception[] failures =
            [
                new ConfigurationException("bad setting"),
                new MissingKeyException("NVIDIA_API_KEY is not set", ["NVIDIA_API_KEY"]),
                new ModelNotFoundException("nvidia", ["acme/absent"], ["acme/other"], 1),
                new EndpointUnreachableException("tier nvidia: models endpoint unreachable"),
                new MeasurementPreconditionException("NVML is unavailable"),
                new ContentDiffersException("README results block differs from the generated one"),
            ];

            Assert.Equal([2, 3, 4, 5, 6, 7], failures.Select(static failure => ExitCodeMapper.Map(failure, shutdownRequested: false)));
            Assert.Equal([2, 3, 4, 5, 6, 7], failures.Select(static failure => ExitCodeMapper.Map(failure, shutdownRequested: true)));
        }

        [Theory]
        [InlineData(ExitCode.Success, "success")]
        [InlineData(ExitCode.Unexpected, "unexpected")]
        [InlineData(ExitCode.InvalidArguments, "invalid arguments or configuration")]
        [InlineData(ExitCode.MissingKey, "missing API key")]
        [InlineData(ExitCode.ModelNotFound, "model not found")]
        [InlineData(ExitCode.Unreachable, "unreachable, retries exhausted, step failed or run interrupted")]
        [InlineData(ExitCode.PreconditionFailed, "measurement precondition failed")]
        [InlineData(ExitCode.ContentDiffers, "generated content differs")]
        public void Describe_returns_the_documented_meaning_of_each_code(ExitCode code, string meaning)
        {
            Assert.Equal(meaning, ExitCodeMapper.Describe(code));
        }

        [Fact]
        public void Describe_covers_every_defined_code()
        {
            foreach (ExitCode code in Enum.GetValues<ExitCode>())
            {
                Assert.False(string.IsNullOrWhiteSpace(ExitCodeMapper.Describe(code)), $"No meaning for {code}.");
            }
        }

        [Fact]
        public void Describe_rejects_a_value_the_enum_does_not_define()
        {
            _ = Assert.Throws<ArgumentOutOfRangeException>(() => ExitCodeMapper.Describe((ExitCode)99));
        }
    }

    /// <summary>
    /// AJ-144 (REQ-025): the exit code 7 of <c>report --verify-readme</c> through the real command under the harness: a results block that differs from the generated one ends with
    /// <see cref="ContentDiffersException"/>, one <c>error:</c> line and no write; the same block verifies with 0 and a missing marker is 2. A nested class, so the <c>Spec=REQ-006</c> trait of the probe tests does not tag these rows.
    /// </summary>
    [Trait("Spec", "REQ-025")]
    public sealed class ReportVerifyCode
    {
        private const string Readme = "# Title\n\n<!-- results:begin -->\nstale\n<!-- results:end -->\n";

        private static TempWorkspace Workspace()
        {
            TempWorkspace workspace = TempWorkspace.CreateEmpty();
            _ = workspace.WriteFile(Path.Combine("results", RunDocumentFixtures.CommittedFileName(Core.Runs.Tier.Local)), RunDocumentFixtures.CommittedText(Core.Runs.Tier.Local));
            return workspace;
        }

        private static Task<CliRunResult> RunAsync(TempWorkspace workspace, string arguments) =>
            CliHarness.RunTokensAsync(arguments.Split(' '), new CliHostOverrides(WorkingDirectory: workspace.Root)).WaitAsync(HangGuard);

        private static Task<CliRunResult> RunWithKeyAsync(TempWorkspace workspace, string arguments) =>
            CliHarness.RunTokensAsync(
                arguments.Split(' '),
                new CliHostOverrides(Configuration: new Dictionary<string, string?> { ["NEBIUS_API_KEY"] = ProviderClientFixtures.FakeKey() }, WorkingDirectory: workspace.Root)).WaitAsync(HangGuard);

        [Fact]
        public async Task A_verify_only_run_writes_no_file_and_creates_no_folder()
        {
            using TempWorkspace workspace = Workspace();
            _ = workspace.WriteFile("README.md", Readme);

            Assert.Equal(7, (await RunAsync(workspace, "report --verify-readme")).ExitCode);
            _ = await RunAsync(workspace, "report --update-readme");
            Directory.Delete(Path.Combine(workspace.Root, "docs"), recursive: true);
            Assert.Equal(0, (await RunAsync(workspace, "report --verify-readme")).ExitCode);

            Assert.False(Directory.Exists(Path.Combine(workspace.Root, "docs")));
        }

        [Fact]
        public async Task Update_and_verify_together_are_an_argument_error_exit_2_and_nothing_is_written()
        {
            using TempWorkspace workspace = Workspace();
            string path = workspace.WriteFile("README.md", Readme);

            CliRunResult result = await RunAsync(workspace, "report --update-readme --verify-readme");

            Assert.Equal(2, result.ExitCode);
            Assert.Contains("--update-readme and --verify-readme cannot be combined", result.Stderr, StringComparison.Ordinal);
            Assert.Equal(Readme, File.ReadAllText(path));
            Assert.False(Directory.Exists(Path.Combine(workspace.Root, "docs")));
        }

        [Fact]
        public async Task A_registered_key_written_escaped_in_a_run_file_value_the_template_uses_is_refused_and_nothing_is_written()
        {
            using TempWorkspace workspace = TempWorkspace.CreateEmpty();
            string key = ProviderClientFixtures.FakeKey();
            string escaped = "\\u" + ((int)key[0]).ToString("X4", System.Globalization.CultureInfo.InvariantCulture) + key[1..];
            string text = RunDocumentFixtures.CommittedText(Core.Runs.Tier.Local);
            string edited = text.Replace("\"os\": \"fixture-os\"", "\"os\": \"" + escaped + "\"", StringComparison.Ordinal);
            Assert.NotEqual(text, edited);
            _ = workspace.WriteFile(Path.Combine("results", RunDocumentFixtures.CommittedFileName(Core.Runs.Tier.Local)), edited);
            _ = workspace.WriteFile(Path.Combine("content", "linkedin-posts.template.md"), "## Variant: A\nOS {{results.local.environment.os}}\n");

            CliRunResult result = await RunWithKeyAsync(workspace, "report --render-content");

            Assert.Equal(2, result.ExitCode);
            Assert.Contains("linkedin-posts.md holds a registered key; nothing written", result.Stderr, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(workspace.Root, "content", "linkedin-posts.md")));
            Assert.False(Directory.Exists(Path.Combine(workspace.Root, "docs")));
        }

        [Fact]
        public async Task A_key_variable_that_only_the_config_file_names_is_registered_and_its_value_in_the_content_is_refused()
        {
            // report builds no host: the variable name PROBE_ONLY_API_KEY is declared by config/appsettings.json alone, so the early registration cannot know it; its value is held by the overrides configuration.
            using TempWorkspace workspace = TempWorkspace.CreateEmpty();
            string key = ProviderClientFixtures.FakeKey();
            string escaped = "\\u" + ((int)key[0]).ToString("X4", System.Globalization.CultureInfo.InvariantCulture) + key[1..];
            string text = RunDocumentFixtures.CommittedText(Core.Runs.Tier.Local);
            string edited = text.Replace("\"os\": \"fixture-os\"", "\"os\": \"" + escaped + "\"", StringComparison.Ordinal);
            Assert.NotEqual(text, edited);
            _ = workspace.WriteFile(Path.Combine("results", RunDocumentFixtures.CommittedFileName(Core.Runs.Tier.Local)), edited);
            _ = workspace.WriteFile(Path.Combine("content", "linkedin-posts.template.md"), "## Variant: A\nOS {{results.local.environment.os}}\n");
            _ = workspace.WriteFile(Path.Combine("config", "appsettings.json"), "{\"AgentJoules\":{\"Tiers\":{\"nebius\":{\"ApiKeyVariable\":\"PROBE_ONLY_API_KEY\"}}}}");

            CliRunResult result = await CliHarness.RunTokensAsync(
                ["report", "--render-content"],
                new CliHostOverrides(Configuration: new Dictionary<string, string?> { ["PROBE_ONLY_API_KEY"] = key }, WorkingDirectory: workspace.Root)).WaitAsync(HangGuard);

            Assert.Equal(2, result.ExitCode);
            Assert.Contains("linkedin-posts.md holds a registered key; nothing written", result.Stderr, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(workspace.Root, "content", "linkedin-posts.md")));
        }

        [Fact]
        public async Task A_key_variable_valued_differently_in_the_environment_and_in_the_later_sources_has_both_values_registered()
        {
            // The host's order puts the environment last, report's combined order puts the secrets and the override after it: the process environment value (the one the host uses) must be registered too.
            // User secrets cannot be faked in the harness, so the override (added after the environment, as the secrets are) stands in for them; the variable name is declared by the config file alone.
            const string variable = "PROBE_TWO_VALUES_API_KEY";
            using TempWorkspace workspace = TempWorkspace.CreateEmpty();
            string environmentValue = ProviderClientFixtures.FakeKey();
            string laterValue = ProviderClientFixtures.FakeKey() + "x";
            string escaped = "\\u" + ((int)environmentValue[0]).ToString("X4", System.Globalization.CultureInfo.InvariantCulture) + environmentValue[1..];
            string text = RunDocumentFixtures.CommittedText(Core.Runs.Tier.Local);
            string edited = text.Replace("\"os\": \"fixture-os\"", "\"os\": \"" + escaped + "\"", StringComparison.Ordinal);
            Assert.NotEqual(text, edited);
            _ = workspace.WriteFile(Path.Combine("results", RunDocumentFixtures.CommittedFileName(Core.Runs.Tier.Local)), edited);
            _ = workspace.WriteFile(Path.Combine("content", "linkedin-posts.template.md"), "## Variant: A\nOS {{results.local.environment.os}}\n");
            _ = workspace.WriteFile(Path.Combine("config", "appsettings.json"), "{\"AgentJoules\":{\"Tiers\":{\"nebius\":{\"ApiKeyVariable\":\"" + variable + "\"}}}}");
            string? previous = Environment.GetEnvironmentVariable(variable);
            Environment.SetEnvironmentVariable(variable, environmentValue);
            try
            {
                CliRunResult result = await CliHarness.RunTokensAsync(
                    ["report", "--render-content"],
                    new CliHostOverrides(Configuration: new Dictionary<string, string?> { [variable] = laterValue }, IncludeProcessEnvironment: true, WorkingDirectory: workspace.Root)).WaitAsync(HangGuard);

                Assert.Equal(2, result.ExitCode);
                Assert.Contains("linkedin-posts.md holds a registered key; nothing written", result.Stderr, StringComparison.Ordinal);
                Assert.False(File.Exists(Path.Combine(workspace.Root, "content", "linkedin-posts.md")));
            }
            finally
            {
                Environment.SetEnvironmentVariable(variable, previous);
            }
        }

        [Fact]
        public async Task A_registered_key_of_17_characters_typed_into_a_variant_name_is_refused_and_never_echoed()
        {
            using TempWorkspace workspace = Workspace();
            string key = ProviderClientFixtures.FakeKey() + "k";
            _ = workspace.WriteFile(Path.Combine("content", "linkedin-posts.template.md"), "## Variant: " + key + "\n" + new string('x', 1301) + "\n");

            CliRunResult result = await CliHarness.RunTokensAsync(
                ["report", "--render-content"],
                new CliHostOverrides(Configuration: new Dictionary<string, string?> { ["NEBIUS_API_KEY"] = key }, WorkingDirectory: workspace.Root)).WaitAsync(HangGuard);

            Assert.Equal(2, result.ExitCode);
            Assert.Contains("linkedin-posts.template.md holds a registered key; nothing written", result.Stderr, StringComparison.Ordinal);
            Assert.DoesNotContain(key[..9], result.Stderr, StringComparison.Ordinal);
            Assert.DoesNotContain(key[..9], result.Stdout, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(workspace.Root, "content", "linkedin-posts.md")));
        }

        [Fact]
        public async Task A_registered_key_in_the_readme_outside_the_markers_is_refused_and_no_file_changes()
        {
            using TempWorkspace workspace = Workspace();
            string readme = "# Title " + ProviderClientFixtures.FakeKey() + "\n\n<!-- results:begin -->\nstale\n<!-- results:end -->\n";
            string path = workspace.WriteFile("README.md", readme);

            CliRunResult result = await RunWithKeyAsync(workspace, "report --update-readme");

            Assert.Equal(2, result.ExitCode);
            Assert.Contains("README.md holds a registered key; nothing written", result.Stderr, StringComparison.Ordinal);
            Assert.Equal(readme, File.ReadAllText(path));
            Assert.False(Directory.Exists(Path.Combine(workspace.Root, "docs")));
        }

        [Fact]
        public async Task A_stale_block_exits_7_with_one_error_line_and_the_readme_is_not_touched()
        {
            using TempWorkspace workspace = Workspace();
            string path = workspace.WriteFile("README.md", Readme);

            CliRunResult result = await RunAsync(workspace, "report --verify-readme");

            Assert.Equal(7, result.ExitCode);
            Assert.EndsWith("(ContentDiffersException)", Assert.Single(result.ErrorLines), StringComparison.Ordinal);
            Assert.Equal(Readme, File.ReadAllText(path));
        }

        [Fact]
        public async Task An_updated_block_verifies_with_0_and_a_missing_marker_exits_2()
        {
            using TempWorkspace workspace = Workspace();
            _ = workspace.WriteFile("README.md", Readme);

            Assert.Equal(0, (await RunAsync(workspace, "report --update-readme")).ExitCode);
            Assert.Equal(0, (await RunAsync(workspace, "report --verify-readme")).ExitCode);

            _ = workspace.WriteFile("README.md", "# Title\n");
            CliRunResult missing = await RunAsync(workspace, "report --verify-readme");

            Assert.Equal(2, missing.ExitCode);
            Assert.Contains("<!-- results:begin -->", missing.Stderr, StringComparison.Ordinal);
        }
    }
}

internal sealed class ThrowOnStopService : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("the hosted service failed to stop");
}