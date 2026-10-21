using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using AgentJoules.Cli;
using AgentJoules.Core.Cli;
using AgentJoules.Specs.Support;
using AgentJoules.TestSupport;

using Reqnroll;

using Xunit;
using Xunit.Sdk;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-058: every failure class ends in its documented exit code with one redacted error line. Scoped to the feature because sentences such as "the exit code is 2" are reused by other stories
/// with other meanings. The state lives in <see cref="ExitCodesContext"/>. Scenario 1 uses the mapper alone; every other scenario runs the real application through the real command line harness
/// (the parser, the preflight, the GPU check through the real NVML binding, the redacting writers and the deny-all handler are the real ones; the endpoints are recording fakes and the failing
/// service of the <c>probe</c> rows is a service override).
/// </summary>
[Binding]
[Scope(Feature = "AJ-058 Map every failure to the documented exit code")]
public sealed class ExitCodesSteps(ExitCodesContext context)
{
    // A command that hangs must fail the scenario, not the run (system clock; it only ends a run that already failed).
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    private static readonly string[] Separators = [", ", " and "];

    // The documented shape of the one failure line: error: <message> (<exception type>).
    private static readonly Regex ErrorLinePattern = new(@"^error: .+ \([A-Za-z]+\)$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));

    private const string KeyPrefix = "nvapi-";
    private const string StackFramePrefix = "   at ";

    private readonly ExitCodesContext _context = context;

    private CliRunResult Result => _context.Result ?? throw new XunitException("The command has not run yet.");

    // The exception of each class, built with its real constructor.
    private static Exception Build(string typeName) => typeName switch
    {
        nameof(ConfigurationException) => new ConfigurationException("a setting is invalid"),
        nameof(MissingKeyException) => new MissingKeyException("NVIDIA_API_KEY is not set", ["NVIDIA_API_KEY"]),
        nameof(ModelNotFoundException) => new ModelNotFoundException("nvidia", ["acme/absent"], ["acme/other"], 1),
        nameof(EndpointUnreachableException) => new EndpointUnreachableException("tier nvidia: models endpoint unreachable"),
        nameof(MeasurementPreconditionException) => new MeasurementPreconditionException("NVML is unavailable"),
        nameof(ContentDiffersException) => new ContentDiffersException("README results block differs from the generated one"),
        _ => throw new XunitException("The scenario names an exception type the steps cannot build: " + typeName),
    };

    [Given(@"^the exception types (.+)$")]
    public void GivenTheExceptionTypes(string names)
    {
        foreach (string name in names.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            _context.Exceptions.Add(Build(name));
        }

        Assert.Equal(6, _context.Exceptions.Count);
    }

    [When(@"^each is mapped by the exit code mapper$")]
    public void WhenEachIsMapped()
    {
        foreach (Exception exception in _context.Exceptions)
        {
            _context.MappedCodes.Add(ExitCodeMapper.Map(exception, shutdownRequested: false));
        }
    }

    [Then(@"^the codes are (.+) in that order$")]
    public void ThenTheCodesAre(string codes)
    {
        int[] expected = [.. codes.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(static code => int.Parse(code, CultureInfo.InvariantCulture))];

        Assert.Equal(expected, _context.MappedCodes);
    }

    [Given(@"^fake keys and fake endpoints$")]
    public void GivenFakeKeysAndFakeEndpoints()
    {
        _context.SetNvidiaKey();
        _context.RouteNvidia(new FakeHttpMessageHandler { ModelIds = ConfigurationFixtures.ShippedModelIds() });
    }

    [When(@"^""([^""]*)"" is executed$")]
    public async Task WhenCommandIsExecuted(string arguments)
    {
        _context.PrepareRun(arguments);
        await _context.RunAsync().WaitAsync(HangGuard);
    }

    [Given(@"^the failure ""([^""]*)""$")]
    public void GivenTheFailure(string failure)
    {
        switch (failure)
        {
            case "unexpected exception":
                _context.PrepareProbeThrowing(new InvalidOperationException("the GPU factory failed unexpectedly"));
                break;
            case "unknown tier value":
                _context.SetNvidiaKey();
                _context.RouteNvidia(new FakeHttpMessageHandler { ModelIds = ConfigurationFixtures.ShippedModelIds() });
                _context.PrepareRun("run --tier bogus");
                break;
            case "no API key":
                // No key anywhere: the nebius key and the key of the judge tier are both missing.
                _context.PrepareRun("run --tier nebius --dry-run");
                break;
            case "model absent from models list":
                _context.SetNvidiaKey();
                _context.RouteNvidia(new FakeHttpMessageHandler { ModelIds = ["acme/other"] });
                _context.PrepareRun("run --tier nvidia --dry-run");
                break;
            case "connection refused":
                _context.SetNvidiaKey();
                _context.RouteNvidia(new FakeHttpMessageHandler { ModelIds = ConfigurationFixtures.ShippedModelIds(), ConnectionRefused = true });
                _context.PrepareRun("run --tier nvidia --dry-run");
                break;
            case "GPU unavailable on local":
                _context.SetNvidiaKey();
                _context.RouteNvidia(new FakeHttpMessageHandler { ModelIds = ConfigurationFixtures.ShippedModelIds() });
                _context.RouteLocalWithoutNvml();
                _context.PrepareRun("run --tier local --repeat 1");
                break;
            case "content differs exception":
                _context.PrepareProbeThrowing(new ContentDiffersException("README results block differs from the generated one"));
                break;
            default:
                throw new XunitException("The scenario names a failure the steps do not know: " + failure);
        }
    }

    [When(@"^the command runs through the harness$")]
    public async Task WhenTheCommandRuns() => await _context.RunAsync().WaitAsync(HangGuard);

    [Then(@"^the exit code is (\d+)$")]
    public void ThenTheExitCodeIs(int expected)
    {
        Assert.True(expected == Result.ExitCode, $"Expected exit code {expected} but got {Result.ExitCode}; stderr: {Result.Stderr}");
        Assert.Empty(Result.DeniedRequests);
    }

    [Then(@"^stderr has exactly one line starting with ""([^""]*)""$")]
    public void ThenStderrHasExactlyOneLineStartingWith(string prefix)
    {
        // The model check (code 4) and the key check (code 3) may print their list and alternative lines before the error line; none of them starts with the prefix.
        string[] lines = [.. Result.StderrLines.Where(line => line.StartsWith(prefix, StringComparison.Ordinal))];

        Assert.True(lines.Length == 1, $"Expected exactly one line starting with \"{prefix}\" but found {lines.Length}; stderr: {Result.Stderr}");
        Assert.Matches(ErrorLinePattern, lines[0]);
        Assert.DoesNotContain(StackFramePrefix, Result.Stderr, StringComparison.Ordinal);
    }

    [Given(@"^a failing endpoint and a runtime-built key starting with ""([^""]*)""$")]
    public void GivenAFailingEndpointAndARuntimeBuiltKey(string prefix)
    {
        Assert.Equal(KeyPrefix, prefix);
        string key = prefix + Guid.NewGuid().ToString("N");
        var nvidia = new FakeHttpMessageHandler { ModelIds = ConfigurationFixtures.ShippedModelIds() };

        // The models route answers 401 and echoes the key in the body and in a header, as a rejected call can.
        nvidia.SetStatuses(FakeRoute.Models, 401);
        nvidia.CustomizeResponse = (route, response) =>
        {
            response.Content = new StringContent("{\"error\":{\"message\":\"Incorrect API key provided: " + key + "\"}}", Encoding.UTF8, "application/json");
            _ = response.Headers.TryAddWithoutValidation("X-Echo", key);
        };

        _context.SetNvidiaKey(key);
        _context.RouteNvidia(nvidia);
        _context.PrepareRun("run --tier nvidia --dry-run");
    }

    [When(@"^the command fails$")]
    public async Task WhenTheCommandFails()
    {
        await _context.RunAsync().WaitAsync(HangGuard);

        Assert.True(Result.ExitCode != 0, "The command was expected to fail but exited 0.");
        Assert.True(_context.Nvidia?.Count(FakeRoute.Models) >= 1, "The failing endpoint received no request, so the scenario proved nothing.");
    }

    [Then(@"^stderr does not contain any character of the key after the nvapi- prefix$")]
    public void ThenStderrHoldsNoPartOfTheKey()
    {
        string secret = (_context.Key ?? throw new XunitException("No key was built."))[KeyPrefix.Length..];

        // The error line must exist (the command failed visibly) and must show no 6-character window of the secret part: a whole-key check would miss a truncated echo.
        _ = Assert.Single(Result.ErrorLines);
        for (int start = 0; start + 6 <= secret.Length; start++)
        {
            string window = secret.Substring(start, 6);
            Assert.DoesNotContain(window, Result.Stderr, StringComparison.Ordinal);
            Assert.DoesNotContain(window, Result.Stdout, StringComparison.Ordinal);
        }
    }

    [Given(@"^an unexpected exception$")]
    public void GivenAnUnexpectedException() =>
        _context.PrepareProbeThrowing(new InvalidOperationException("the GPU factory failed unexpectedly"));

    [When(@"^the command fails at the default log level$")]
    public async Task WhenTheCommandFailsAtTheDefaultLogLevel()
    {
        await _context.RunAsync().WaitAsync(HangGuard);

        Assert.Equal(1, Result.ExitCode);
        _ = Assert.Single(Result.ErrorLines);
    }

    [Then(@"^stderr has no stack frame lines$")]
    public void ThenStderrHasNoStackFrameLines()
    {
        Assert.DoesNotContain(Result.StderrLines, static line => line.StartsWith(StackFramePrefix, StringComparison.Ordinal));
        Assert.DoesNotContain(StackFramePrefix, Result.Stderr, StringComparison.Ordinal);
    }
}