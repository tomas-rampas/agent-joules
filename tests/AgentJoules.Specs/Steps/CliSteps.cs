using AgentJoules.Cli;
using AgentJoules.Core.Cli;
using AgentJoules.TestSupport;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-034: the CLI foundation. Self-contained: its state lives here, not in ScenarioWorld. The class is scoped to the feature because the
/// sentences "the exit code is ..." and "I run ..." will be reused by every later CLI story with other arguments; a scoped binding wins
/// inside this feature and never matches another one. Every key goes through the in-memory configuration of the harness, so no step sets a
/// process environment variable; the child-process scenario passes its message as an argument.
/// </summary>
[Binding]
[Scope(Feature = "Build the CLI foundation with exit codes and redacted output")]
public sealed class CliSteps
{
    private const string Suffix = "abcdef1234567890";
    private const string ScriptedMessage = "scripted failure";
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    private readonly List<(string Key, string? Value)> _configuration = [];
    private Exception? _gpuFactoryFailure;
    private string _arguments = "probe";
    private string _expectedMessage = ScriptedMessage;
    private string _childMessage = string.Empty;
    private CliHarnessRun? _run;
    private CliRunResult? CurrentResult { get; set; }

    private CliRunResult Result => CurrentResult ?? throw new InvalidOperationException("The CLI was not run.");

    private CliHarnessRun Run => _run ?? throw new InvalidOperationException("The CLI was not started.");

    private static string BuildNvidiaKey() => string.Concat("nvapi-", Suffix);

    private CliHostOverrides Overrides() => CliHarness.Overrides(_configuration, _gpuFactoryFailure);

    private async Task RunAsync(string arguments)
    {
        _run = CliHarness.Start(arguments, Overrides());
        CurrentResult = await _run.Result;
    }

    [Given(@"^the CLI harness(?: with no overrides)?$")]
    public static void GivenTheCliHarness()
    {
        // The harness always runs with isolated overrides; there is nothing to configure.
    }

    [Given(@"^a service override that throws (MeasurementPreconditionException|InvalidOperationException)$")]
    public void GivenAServiceOverrideThatThrows(string type)
    {
        _gpuFactoryFailure = type == nameof(MeasurementPreconditionException)
            ? new MeasurementPreconditionException(ScriptedMessage)
            : new InvalidOperationException(ScriptedMessage);
        _expectedMessage = ScriptedMessage;
    }

    [Given(@"^a service override that throws an exception whose message contains a runtime-built nvapi key$")]
    public void GivenAServiceOverrideThatThrowsWithAKey()
    {
        _gpuFactoryFailure = new InvalidOperationException("request failed for " + BuildNvidiaKey());
    }

    [Given(@"^Meter:SampleRateHz is 5 without the override in the in-memory configuration$")]
    public void GivenTheRateWithoutTheOverride()
    {
        _configuration.Add(("AgentJoules:Meter:SampleRateHz", "5"));
    }

    [Given(@"^the in-memory configuration sets NVIDIA_API_KEY to a four-character value$")]
    public void GivenAFourCharacterKey()
    {
        _configuration.Add(("NVIDIA_API_KEY", string.Concat("k#", "9Q")));
    }

    [Given(@"^NVIDIA_API_KEY is set in the process environment$")]
    public static void GivenTheKeyIsInTheProcessEnvironment()
    {
        // Parallel-safe tests never write a process variable, so this step cannot set one. The Then step proves the isolation with PATH,
        // which every machine has, and checks the key by name; a real key on the laptop only makes the proof stronger.
        Assert.False(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PATH")), "the machine has no PATH variable to prove isolation with");
    }

    [Given(@"^a fake long-running command that waits on its cancellation token$")]
    public void GivenAFakeLongRunningCommand()
    {
        _arguments = "fake-wait";
    }

    [Given(@"^a command that throws OperationCanceledException with no shutdown requested$")]
    public void GivenACommandThatThrowsOperationCanceled()
    {
        _arguments = "fake-cancel";
    }

    [Given(@"^a fake command that writes a JSON document with Write and no newline$")]
    public void GivenAFakeCommandThatWritesJson()
    {
        _arguments = "fake-write";
    }

    [Given(@"^the Cli started as a child process with the hidden worker-throw switch and a runtime-built nvapi key in the message$")]
    public void GivenTheChildProcessSetup()
    {
        _childMessage = "worker failed " + BuildNvidiaKey();
    }

    [When(@"^I run ""([^""]*)""$")]
    public async Task WhenIRun(string arguments)
    {
        _arguments = arguments;
        if (_gpuFactoryFailure is InvalidOperationException failure)
        {
            _expectedMessage = failure.Message;
        }

        await RunAsync(arguments);
    }

    [When(@"^I run it through the harness$")]
    public async Task WhenIRunItThroughTheHarness()
    {
        await RunAsync(_arguments);
    }

    [When(@"^the harness runs with the default overrides$")]
    public async Task WhenTheHarnessRunsWithTheDefaultOverrides()
    {
        // A probe builds and starts a host, which is what the configuration is read from; its own result is not the subject here.
        _run = CliHarness.Start("probe");
        CurrentResult = await _run.Result;
    }

    [When(@"^the host raises ApplicationStopping \(the Ctrl\+C, SIGTERM and SIGHUP path\)$")]
    public async Task WhenTheHostRaisesApplicationStopping()
    {
        using var guard = new CancellationTokenSource(HangGuard);
        _run = CliHarness.Start(_arguments, Overrides());
        await Task.WhenAny(_run.CommandWaiting, _run.Result).WaitAsync(guard.Token);
        if (!_run.CommandWaiting.IsCompleted)
        {
            Assert.Fail("the command never started waiting; run ended with: " + (await _run.Result).Stderr);
        }

        await _run.RaiseShutdownAsync(guard.Token);
        CurrentResult = await _run.Result.WaitAsync(guard.Token);
    }

    [When(@"^the worker thread throws$")]
    public async Task WhenTheWorkerThreadThrows()
    {
        CurrentResult = await CliChildProcess.RunAsync([CliChildProcess.WorkerThrowOption, _childMessage], TimeSpan.FromSeconds(60));
    }

    [Then(@"^the exit code is (\d+)$")]
    public void ThenTheExitCodeIs(int code)
    {
        Assert.Equal(code, Result.ExitCode);
    }

    [Then(@"^the exit code is 0 and stdout lists the probe options$")]
    public void ThenHelpListsTheProbeOptions()
    {
        Assert.Equal(0, Result.ExitCode);
        foreach (string option in new[] { "--json", "--save", "--suffix", "--config" })
        {
            Assert.Contains(option, Result.Stdout, StringComparison.Ordinal);
        }
    }

    [Then(@"^the exit code is (\d+) and stderr has one line starting ""([^""]*)""$")]
    public void ThenTheExitCodeAndOneLineStartingWith(int code, string prefix)
    {
        Assert.Equal(code, Result.ExitCode);
        Assert.Single(Result.StderrLines, line => line.StartsWith(prefix, StringComparison.Ordinal));
    }

    [Then(@"^the exit code is (\d+) and stderr shows ""error: <message> \((\w+)\)""$")]
    public void ThenTheExitCodeAndTheErrorLine(int code, string type)
    {
        Assert.Equal(code, Result.ExitCode);
        Assert.Equal($"error: {_expectedMessage} ({type})", Assert.Single(Result.ErrorLines));
    }

    [Then(@"^the exit code is 1 and stderr has one ""error:"" line$")]
    public void ThenTheExitCodeIsOneWithOneErrorLine()
    {
        Assert.Equal(1, Result.ExitCode);
        Assert.Single(Result.ErrorLines);
    }

    [Then(@"^stderr contains ""nvapi-\*\*\*\*"" and does not contain the key suffix$")]
    public void ThenStderrIsRedacted()
    {
        Assert.Contains("nvapi-****", Result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(Suffix, Result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(Suffix, Result.Stdout, StringComparison.Ordinal);
    }

    [Then(@"^stderr contains ""warning"" and ""NVIDIA_API_KEY"" and does not contain the value$")]
    public void ThenStderrWarnsByVariableName()
    {
        string value = _configuration.Single(pair => pair.Key == "NVIDIA_API_KEY").Value!;

        Assert.Contains("warning", Result.Stderr, StringComparison.Ordinal);
        Assert.Contains("NVIDIA_API_KEY", Result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(value, Result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(value, Result.Stdout, StringComparison.Ordinal);
    }

    [Then(@"^the configuration seen by the command has no NVIDIA_API_KEY value$")]
    public void ThenTheConfigurationHasNoKey()
    {
        Assert.True(Run.HostStarted, "the probe command never started a host");
        Assert.True(string.IsNullOrEmpty(Run.Setting("NVIDIA_API_KEY")));

        // PATH is the witness that the whole environment is excluded, not only this key.
        Assert.Null(Run.Setting("PATH"));
    }

    [Then(@"^the command sees the cancellation, the exit code is 5 and ProcessTerminationTimeout is 15 s \(the incomplete run state is written by AJ-056-T6a, not asserted here\)$")]
    public void ThenTheShutdownIsObserved()
    {
        Assert.True(Run.CancellationObserved);
        Assert.Equal((int)ExitCode.Unreachable, Result.ExitCode);
        Assert.NotNull(Run.InvocationConfiguration);
        Assert.Equal((TimeSpan?)TimeSpan.FromSeconds(15), Run.InvocationConfiguration.ProcessTerminationTimeout);
    }

    [Then(@"^stdout holds the complete JSON after RunAsync returns$")]
    public void ThenStdoutHoldsTheJson()
    {
        Assert.Equal(0, Result.ExitCode);
        Assert.Equal(FakeCommands.JsonDocument, Result.Stdout);
    }

    [Then(@"^the process exits 1, stderr contains ""error:"" and ""nvapi-\*\*\*\*"" and does not contain the key suffix$")]
    public void ThenTheChildExitsOneRedacted()
    {
        Assert.Equal(1, Result.ExitCode);
        Assert.Contains("error:", Result.Stderr, StringComparison.Ordinal);
        Assert.Contains("nvapi-****", Result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(Suffix, Result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(Suffix, Result.Stdout, StringComparison.Ordinal);
    }
}