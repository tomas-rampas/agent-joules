using AgentJoules.Cli;
using AgentJoules.Core.Cli;
using AgentJoules.Nvml.Interop;
using AgentJoules.TestSupport;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using Xunit;

namespace AgentJoules.UnitTests;

/// <summary>
/// AJ-034 (REQ-006): the exit codes 1, 2 and 6 of the command line, the redacted one-line error, and the shutdown, drain and escaped-exception
/// paths, all through the real application under <see cref="CliHarness"/>. Codes 3, 4, 5 (other than shutdown) and 7 follow in M2.
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
    /// without a GPU. Code 7 belongs to <c>report --verify-readme</c> (M3). A nested class, so the <c>Spec=REQ-006</c> trait of the probe tests does not tag these rows.
    /// </summary>
    [Trait("Spec", "REQ-017")]
    public sealed class RunCommandCodes
    {
        [Theory]
        [InlineData(1, "run --tier nvidia --out occupied", "(AgentJoulesException)")]
        [InlineData(2, "run --tier bogus", null)]
        [InlineData(3, "run --tier nebius --dry-run", "(MissingKeyException)")]
        [InlineData(4, "run --tier nvidia --dry-run", "(ModelNotFoundException)")]
        [InlineData(5, "run --tier nvidia", "(StepFailedException)")]
        [InlineData(6, "run --tier local --dry-run", "(MeasurementPreconditionException)")]
        public async Task The_run_command_maps_each_failure_to_its_exit_code(int expected, string arguments, string? errorType)
        {
            using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
            _ = workspace.WriteFile("occupied", "this path is a file");
            var nvidia = new FakeHttpMessageHandler { ModelIds = expected == 4 ? ["acme/other"] : ConfigurationFixtures.ShippedModelIds() };
            if (expected == 5)
            {
                nvidia.SetStatuses(FakeRoute.ChatCompletions, 500);
            }

            var setup = new RunCommandSetup { WorkingDirectory = workspace.Root };
            setup.Settings["NVIDIA_API_KEY"] = ProviderClientFixtures.FakeKey();
            setup.Settings["AgentJoules:Tiers:nvidia:BaseUrl"] = ProviderClientFixtures.BaseUrl(Core.Runs.Tier.Nvidia);
            setup.Endpoints["nvidia"] = nvidia;
            setup.Endpoints["local"] = new FakeHttpMessageHandler { ModelIds = ConfigurationFixtures.ShippedModelIds() };

            CliRunResult result = await RunCommandHarness.RunAsync(arguments, setup).WaitAsync(HangGuard);

            Assert.True(expected == result.ExitCode, $"Expected exit code {expected} but got {result.ExitCode}; stderr: {result.Stderr}");
            if (errorType is not null)
            {
                Assert.EndsWith(errorType, Assert.Single(result.ErrorLines), StringComparison.Ordinal);
            }
            else
            {
                // The parse error of the tier rule, not an unrecognized command.
                Assert.Contains("Must be one of", result.Stderr, StringComparison.Ordinal);
                Assert.DoesNotContain("Unrecognized command", result.Stderr, StringComparison.Ordinal);
            }

            Assert.DoesNotContain(StackFramePrefix, result.Stderr, StringComparison.Ordinal);
            Assert.Equal(expected == 5 ? 1 : 0, setup.Orchestrator.Runs);
        }
    }
}

internal sealed class ThrowOnStopService : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("the hosted service failed to stop");
}