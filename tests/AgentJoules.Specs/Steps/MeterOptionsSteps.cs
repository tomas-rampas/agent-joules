using System.Globalization;

using AgentJoules.Core.Cli;
using AgentJoules.Core.Configuration;
using AgentJoules.TestSupport;

using Microsoft.Extensions.Configuration;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-033: the GPU, meter and baseline options. Self-contained: its state lives here, not in ScenarioWorld. The class is scoped to the
/// feature because sentences such as "the options are validated" and "validation succeeds" are likely to be reused by later option
/// stories (M2 adds the cost and tier sections); a scoped binding wins inside this feature and never matches another one. Every key goes
/// through an in-memory configuration, so no step sets a process environment variable. "When the CLI starts with these options" is
/// served by <see cref="OptionsLoader.Load"/> and the exit code of the <see cref="ConfigurationException"/> it throws; the real CLI start
/// arrives with the AJ-034 harness.
/// </summary>
[Binding]
[Scope(Feature = "Bind and validate the GPU, meter and baseline options")]
public sealed class MeterOptionsSteps
{
    private const double Tolerance = 1e-9;
    private const string EnvironmentKey = "AGENTJOULES__Gpu__Index";

    private readonly List<(string Key, string Value)> _pairs = [];
    private bool _shippedFile;
    private AgentJoulesOptions? _options;
    private ConfigurationException? _failure;

    private IConfiguration CurrentConfiguration =>
        !_shippedFile
            ? ConfigurationFixtures.InMemory([.. _pairs])
            : ConfigurationFixtures.FromJsonFile(ConfigurationFixtures.AppSettingsPath(), [.. _pairs]);

    private AgentJoulesOptions Options => _options ?? throw new InvalidOperationException("The options were not bound.");

    private ConfigurationException Failure =>
        _failure ?? throw new InvalidOperationException("Validation did not fail.");

    private void Set(string key, string value) => _pairs.Add((AgentJoulesOptions.SectionName + ConfigurationPath.KeyDelimiter + key, value));

    [Given(@"^config/appsettings\.json$")]
    public void GivenTheShippedFile() => UseTheShippedFile();

    // The file is read when the options are bound; failing here names the missing file at the step that asked for it.
    private void UseTheShippedFile()
    {
        Assert.True(File.Exists(ConfigurationFixtures.AppSettingsPath()), "config/appsettings.json does not exist");
        _shippedFile = true;
    }

    [Given(@"^the isolated configuration holds (\S+) ""([^""]*)""$")]
    public void GivenTheIsolatedConfigurationHoldsAnEnvironmentStyleKey(string name, string value)
    {
        // The override is layered over the shipped file, as the host layers the environment over the file; the scenario proves overriding.
        UseTheShippedFile();
        _pairs.Add((ConfigurationFixtures.NormaliseEnvironmentKey(name), value));
    }

    [Given(@"^Meter:SampleRateHz is (\S+) and Meter:AllowBelowMinRate is (true|false)$")]
    public void GivenTheRateAndTheOverride(string rate, string allowBelowMinRate)
    {
        Set("Meter:SampleRateHz", rate);
        Set("Meter:AllowBelowMinRate", allowBelowMinRate);
    }

    [Given(@"^Meter:Method is ""([^""]*)""$")]
    public void GivenTheMethod(string method) => Set("Meter:Method", method);

    [Given(@"^Gpu:Index is (-?\d+)$")]
    public void GivenTheGpuIndex(string index) => Set("Gpu:Index", index);

    [Given(@"^Baseline:Seconds is (\S+)$")]
    public void GivenTheBaselineSeconds(string seconds) => Set("Baseline:Seconds", seconds);

    [When(@"^the options are bound$")]
    public void WhenTheOptionsAreBound()
    {
        _options = OptionsLoader.Bind(CurrentConfiguration);
    }

    [When(@"^the options are validated$")]
    public void WhenTheOptionsAreValidated()
    {
        _options = OptionsLoader.Bind(CurrentConfiguration);
        try
        {
            OptionsLoader.ValidateOrThrow(_options);
        }
        catch (ConfigurationException failure)
        {
            _failure = failure;
        }
    }

    [When(@"^the CLI starts with these options$")]
    public void WhenTheCliStartsWithTheseOptions()
    {
        try
        {
            _options = OptionsLoader.Load(CurrentConfiguration);
        }
        catch (ConfigurationException failure)
        {
            _failure = failure;
        }
    }

    [Then(@"^Gpu:Index is (\d+), Meter:Method is ""([^""]*)"", Meter:SampleRateHz is ([\d.]+), AllowBelowMinRate is (true|false), Baseline:Seconds is ([\d.]+) and the CV limit is ([\d.]+)$")]
    public void ThenTheDocumentedDefaultsAreBound(int index, string method, string rate, string allowBelowMinRate, string seconds, string cvLimit)
    {
        Assert.Equal(index, Options.Gpu.Index);
        Assert.Equal(method, Options.Meter.Method);
        Assert.Equal(Invariant(rate), Options.Meter.SampleRateHz, Tolerance);
        Assert.Equal(bool.Parse(allowBelowMinRate), Options.Meter.AllowBelowMinRate);
        Assert.Equal(Invariant(seconds), Options.Baseline.Seconds, Tolerance);
        Assert.Equal(Invariant(cvLimit), Options.Baseline.MaxCoefficientOfVariation, Tolerance);
    }

    [Then(@"^Gpu:Index is (\d+) and no process environment variable was set$")]
    public void ThenTheGpuIndexIsBoundAndTheProcessEnvironmentIsUntouched(int index)
    {
        Assert.Equal(index, Options.Gpu.Index);

        // The rest of the file is untouched: the override replaced one value, it did not replace the file.
        Assert.Equal("auto", Options.Meter.Method);

        // The key was only ever held by the in-memory provider; the process environment never saw it.
        Assert.Null(Environment.GetEnvironmentVariable(EnvironmentKey));
    }

    [Then(@"^validation succeeds$")]
    public void ThenValidationSucceeds()
    {
        Assert.Null(_failure);
        Assert.NotNull(_options);
    }

    [Then(@"^validation fails naming ([A-Za-z:]+)$")]
    public void ThenValidationFailsNamingAKey(string key)
    {
        Assert.Contains(key, Failure.Message, StringComparison.Ordinal);
    }

    [Then(@"^the exit code is 2 and the message names ([A-Za-z:]+) and ([A-Za-z:]+)$")]
    public void ThenTheExitCodeIsTwoAndTheMessageNamesBothKeys(string first, string second)
    {
        Assert.Equal(ExitCode.InvalidArguments, Failure.ExitCode);
        Assert.Equal(2, (int)Failure.ExitCode);
        Assert.Contains(first, Failure.Message, StringComparison.Ordinal);
        Assert.Contains(second, Failure.Message, StringComparison.Ordinal);
    }

    private static double Invariant(string text) => double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
}