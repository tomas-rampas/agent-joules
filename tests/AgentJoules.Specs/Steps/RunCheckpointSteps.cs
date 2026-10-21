using System.Globalization;
using System.Text.Json;

using AgentJoules.Core.Configuration;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Reporting.Results;
using AgentJoules.Specs.Support;
using AgentJoules.TestSupport;

using Reqnroll;

using Xunit;
using Xunit.Sdk;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-055: write run files atomically after every task and repeat. Scoped to the feature because sentences such as "the exit code is 1" and "the fake
/// handler recorded 0 requests" are reused by other stories with other meanings. The state lives in <see cref="RunCheckpointContext"/>. The writer,
/// the file name and the serialiser run for real; the failing write is made by the <c>BeforeMove</c> hook of the writer, which throws after the partial
/// file was written, which is the moment the scenario names. The last scenario runs <c>run --tier nvidia --out &lt;dir&gt; --repeat 1</c> through the
/// command line harness with the real <c>run</c> command (AJ-054, <see cref="RunCommandHarness"/>), whose writers probe the output directory before any request.
/// </summary>
[Binding]
[Scope(Feature = "AJ-055 Write run files atomically after every task and repeat")]
public sealed class RunCheckpointSteps(RunCheckpointContext context)
{
    // A write or a command that hangs must fail the scenario, not the run (system clock; it only ends a run that already failed).
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    private readonly RunCheckpointContext _context = context;

    private string FileName => _context.FileName ?? throw new XunitException("No run file name was created.");

    private ResultsFileWriter Writer => _context.Writer ?? throw new XunitException("No writer was created.");

    [Given(@"^a run for tier ""([^""]*)"" starting at (\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}) UTC$")]
    public void GivenARunForTierStartingAt(string tier, string start)
    {
        _context.Tier = Enum.GetValues<Tier>().Single(candidate => string.Equals(TierOptions.NameOf(candidate), tier, StringComparison.Ordinal));
        DateTimeOffset startUtc = DateTimeOffset.ParseExact(start, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        _context.Time.SetUtcNow(startUtc);
    }

    [Given(@"^a run where one agent model ID does not contain ""([^""]*)""$")]
    public void GivenARunWhereOneAgentModelIdDoesNotContain(string text)
    {
        const string other = "qwen3-32b";
        Assert.DoesNotContain(text, other, StringComparison.OrdinalIgnoreCase);
        _context.ModelIds = [RunCheckpointFixtures.NemotronModel, other, RunCheckpointFixtures.NemotronModel, RunCheckpointFixtures.NemotronModel];
    }

    [When(@"^the run file name is created$")]
    public void WhenTheRunFileNameIsCreated() =>
        _context.FileName = RunFileName.Create(_context.Time, _context.Tier, _context.ModelIds);

    [Then(@"^the name is ""([^""]*)""$")]
    public void ThenTheNameIs(string expected)
    {
        Assert.Equal(expected, FileName);
        Assert.True(RunFileName.IsValid(FileName), "The name does not match the committed pattern.");
    }

    [Then(@"^the name ends with ""([^""]*)""$")]
    public void ThenTheNameEndsWith(string suffix)
    {
        Assert.EndsWith(suffix, FileName, StringComparison.Ordinal);
        Assert.True(RunFileName.IsValid(FileName), "The name does not match the committed pattern.");
    }

    [Given(@"^a started run$")]
    public void GivenAStartedRun()
    {
        _context.FileName = RunFileName.Create(RunCheckpointFixtures.RunStartUtc, Tier.Nvidia, [RunCheckpointFixtures.NemotronModel]);
        _ = _context.CreateWriter(FileName);
    }

    [When(@"^the first checkpoint is written$")]
    public async Task WhenTheFirstCheckpointIsWritten() =>
        await Writer.WriteAsync(RunCheckpointFixtures.Document(RunStatus.Running), CancellationToken.None).WaitAsync(HangGuard);

    [Then(@"^the file parses and run\.status is ""([^""]*)""$")]
    public void ThenTheFileParsesAndRunStatusIs(string status)
    {
        byte[] bytes = File.ReadAllBytes(Writer.FilePath);
        using JsonDocument parsed = JsonDocument.Parse(bytes);
        Assert.Equal(status, parsed.RootElement.GetProperty("run").GetProperty("status").GetString());

        // The same bytes also read back as the typed document through the registered context.
        RunDocument document = JsonSerializer.Deserialize(bytes, RunCheckpointContext.RunDocumentInfo)!;
        Assert.Equal(RunStatus.Running, document.Run.Status);
    }

    [Given(@"^a complete file from checkpoint 1$")]
    public async Task GivenACompleteFileFromCheckpointOne()
    {
        _context.FileName = RunFileName.Create(RunCheckpointFixtures.RunStartUtc, Tier.Nvidia, [RunCheckpointFixtures.NemotronModel]);

        // The hook fails the second checkpoint after its partial file was written; the first checkpoint passes it.
        int writes = 0;
        _ = _context.CreateWriter(FileName, partial =>
        {
            if (++writes == 2)
            {
                Assert.True(File.Exists(partial), "The partial file should exist when the hook runs.");
                throw new IOException("The disk failed after the partial file was written.");
            }
        });

        await Writer.WriteAsync(RunCheckpointFixtures.Document(RunStatus.Running, stepCount: 1), CancellationToken.None).WaitAsync(HangGuard);
        _context.FirstCheckpointBytes = File.ReadAllBytes(Writer.FilePath);
    }

    [When(@"^writing checkpoint 2 fails after the partial file was created$")]
    public async Task WhenWritingCheckpointTwoFails() =>
        _context.WriteFailure = await Record.ExceptionAsync(() =>
            Writer.WriteAsync(RunCheckpointFixtures.Document(RunStatus.Running, stepCount: 2), CancellationToken.None).WaitAsync(HangGuard));

    [Then(@"^the file still holds the content of checkpoint 1$")]
    public void ThenTheFileStillHoldsTheContentOfCheckpointOne()
    {
        Assert.IsType<IOException>(_context.WriteFailure);
        Assert.Equal(_context.FirstCheckpointBytes, File.ReadAllBytes(Writer.FilePath));
        Assert.False(File.Exists(Writer.FilePath + ".partial"), "The partial file should have been removed.");
    }

    [Given(@"^a step whose cost is N/A with reason ""([^""]*)""$")]
    public void GivenAStepWhoseCostIsNotApplicable(string reason) =>
        _context.Document = RunCheckpointFixtures.Document(RunStatus.Running, stepCount: 1, cost: Metric.NotApplicable(reason));

    [When(@"^the document is serialised$")]
    public void WhenTheDocumentIsSerialised() => _context.Serialise(_context.Document ?? throw new XunitException("No document was built."));

    [Then(@"^the cost value is null and the label is ""([^""]*)""$")]
    public void ThenTheCostValueIsNullAndTheLabelIs(string label)
    {
        using JsonDocument parsed = JsonDocument.Parse(_context.SerialisedText!);
        JsonElement cost = parsed.RootElement.GetProperty("steps")[0].GetProperty("cost");
        Assert.Equal(JsonValueKind.Null, cost.GetProperty("value").ValueKind);
        Assert.Equal(label, cost.GetProperty("label").GetString());
    }

    [Then(@"^the text ""([^""]*)"" does not appear as the cost value$")]
    public void ThenTheTextDoesNotAppearAsTheCostValue(string text)
    {
        using JsonDocument parsed = JsonDocument.Parse(_context.SerialisedText!);
        JsonElement value = parsed.RootElement.GetProperty("steps")[0].GetProperty("cost").GetProperty("value");
        Assert.NotEqual(text, value.GetRawText());
        Assert.DoesNotMatch(@"""cost"":\s*\{\s*""value"":\s*" + System.Text.RegularExpressions.Regex.Escape(text) + @"\b", _context.SerialisedText!);
    }

    [Given(@"^an output directory that cannot be written$")]
    public void GivenAnOutputDirectoryThatCannotBeWritten() => _context.UseUnwritableDirectory();

    [When(@"^""run --tier nvidia --out <dir> --repeat 1"" is executed$")]
    public async Task WhenRunIsExecuted() => await _context.RunCommandAsync().WaitAsync(HangGuard);

    [Then(@"^the exit code is (\d+)$")]
    public void ThenTheExitCodeIs(int expected)
    {
        Assert.True(expected == _context.ExitCode, $"Expected exit code {expected} but got {_context.ExitCode}; stderr: {_context.Stderr}");

        // Exit code 1 alone would also be any other failure: the error line must be the writer's, naming the directory, and never a stack trace.
        if (expected != 0)
        {
            string directoryName = Path.GetFileName(_context.OutDirectory ?? throw new XunitException("No output directory was chosen."));
            Assert.Contains("cannot be written", _context.Stderr, StringComparison.Ordinal);
            Assert.Contains(directoryName, _context.Stderr, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("   at ", _context.Stderr, StringComparison.Ordinal);
    }

    [Then(@"^the fake handler recorded (\d+) requests$")]
    public void ThenTheFakeHandlerRecordedRequests(int expected)
    {
        // The models route and the completions route together, and the orchestrator was never reached.
        Assert.Equal(expected, _context.RequestCount);
        Assert.Equal(0, _context.OrchestratorRuns);
    }
}