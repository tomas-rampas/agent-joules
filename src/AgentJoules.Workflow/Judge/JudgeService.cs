using AgentJoules.Core.Configuration;
using AgentJoules.Core.Energy;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Quality;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Core.Security;
using AgentJoules.Core.Tasks;
using AgentJoules.Metering;
using AgentJoules.Workflow.Providers;
using AgentJoules.Workflow.Runs;

using Microsoft.Extensions.AI;

namespace AgentJoules.Workflow.Judge;

/// <summary>
/// The judge of a run (REQ-010, design 3.4 "Judge"): it scores each workflow step that finished <c>ok</c> with output by one buffered call to the model <c>Judge:ModelId</c> on the tier
/// <c>Judge:Tier</c>, with temperature 0, and meters the call exactly like a workflow call, through a <see cref="MeteredChatClient"/> over a <see cref="RateLimitRetryChatClient"/> over the
/// provider client of the tier, but into the judge sink the request carries, so a judge reading never reaches the workflow results or its totals.
/// </summary>
/// <remarks>
/// <para>
/// One service decides every step, in one place. For each requested step, in order: a step whose outcome is not <c>ok</c> gets the quality N/A <c>step-not-ok</c>, an <c>ok</c> step without
/// output (<see cref="StepReading.OutputChars"/> null or 0) gets N/A <c>empty-output</c>, and neither is sent to the judge; every other step is sent. A step the judge could not score ends with
/// N/A <c>parse-error: &lt;excerpt&gt;</c> (the reply was not an integer from 1 to 5) or N/A <c>judge-failed: &lt;kind&gt;</c> (the call threw, <c>kind</c> being the type name of the exception; or the judge
/// is not usable: <c>judge-model-not-configured</c> when <c>Judge:ModelId</c> is blank, <c>judge-tier-not-metered</c> when <c>Judge:Tier</c> is a measured tier other than the tier of the run). Every
/// quality that is N/A carries the <c>qualityReason</c> <c>not-scored</c>; a score carries none. The derived metrics are not written here: <c>RunAssembler.Assemble</c> computes them from the quality.
/// A failure of one step never stops the others and never escapes, except a cancellation of the request token.
/// </para>
/// <para>
/// The meter of a call: a hosted <c>Judge:Tier</c> (<c>nvidia</c>, <c>nebius</c>) gets a new <see cref="UnmeteredEnergyMeter"/> per call, so every judge reading carries the four energy metrics as N/A
/// <c>hosted-tier</c> by construction; a measured <c>Judge:Tier</c> equal to the tier of the run is measured by the meter of the run, under the same gate; a measured judge on another tier run would
/// measure a GPU that is not the one of this run, so it is refused. The gate, the cost calculator, the instruments and the clock are the tier run's.
/// </para>
/// <para>
/// The step output is model output: it is placed in the prompt and never logged, echoed, put in an exception message or in a reason. The reply is cut to <see cref="MaxReplyChars"/> characters before
/// it is parsed, and <see cref="JudgeScoreParser"/> redacts it through the <see cref="SecretRedactor"/> of the process before anything of it is kept. The rubric is read once by the registration
/// and arrives here as text. The service holds no state of a run, so one instance serves every tier run of the process.
/// </para>
/// </remarks>
public sealed class JudgeService : IJudgeService
{
    /// <summary>The most characters of a judge reply that are parsed; a longer reply is cut. The reply is redacted whole before the cut (defence in depth) and then bounded, so the bound limits the parser's input, not the redaction.</summary>
    public const int MaxReplyChars = 32768;

    /// <summary>The reason of the quality of a step that did not finish <c>ok</c>.</summary>
    public const string StepNotOkReason = "step-not-ok";

    /// <summary>The reason of the quality of an <c>ok</c> step that produced no output.</summary>
    public const string EmptyOutputReason = "empty-output";

    /// <summary>The failure kind of a reply that is not an integer from 1 to 5.</summary>
    public const string ParseErrorKind = "parse-error";

    /// <summary>The failure kind of a measured <c>Judge:Tier</c> that is not the tier of the run.</summary>
    public const string JudgeTierNotMeteredKind = "judge-tier-not-metered";

    /// <summary>The failure kind of a blank <c>Judge:ModelId</c>.</summary>
    public const string ModelNotConfiguredKind = "judge-model-not-configured";

    /// <summary>The failure kind of a step that has output (its length is recorded) but not in memory: a reading read back from a file.</summary>
    public const string OutputNotInMemoryKind = "output-not-in-memory";

    /// <summary>The text before the failure kind in the reason of a quality the judge could not produce.</summary>
    public const string FailedPrefix = "judge-failed: ";

    /// <summary>The text before the excerpt in the reason of a reply that did not parse; the same text as the parser's.</summary>
    public const string ParseErrorPrefix = "parse-error: ";

    /// <summary>The excerpt of a reply longer than <see cref="MaxReplyChars"/> that did not parse: a fixed text, because the cut may land inside a key.</summary>
    public const string ReplyTooLongExcerpt = "(reply longer than 32768 characters)";

    /// <summary>The excerpt of a reply that did not parse and holds a think tag (an unclosed block or a stray closing tag): a fixed text, because a key split by the tag is not masked.</summary>
    public const string ThinkTagLeftExcerpt = "(think tag left in the reply)";

    /// <summary>The <see cref="StepReading.QualityReason"/> of every step whose quality is N/A.</summary>
    public const string NotScored = "not-scored";

    private readonly Tier _tier;
    private readonly string? _modelId;
    private readonly string _rubric;
    private readonly IProviderClientFactory _providers;
    private readonly SecretRedactor _redactor;
    private readonly RetryOptions _retryOptions;
    private readonly Random _random;
    private readonly int _maxOutputTokens;
    private readonly string? _systemPrompt;
    private readonly Lock _seedGate = new();

    /// <summary>Creates the judge.</summary>
    /// <param name="judgeTier">The tier whose endpoint serves the judge (<c>Judge:Tier</c>).</param>
    /// <param name="judgeModelId">The model of the judge (<c>Judge:ModelId</c>); blank makes every judgeable step N/A <c>judge-failed: judge-model-not-configured</c> without a call.</param>
    /// <param name="rubric">The rubric text, as <c>RubricLoader.Load</c> returned it.</param>
    /// <param name="providers">Makes the provider chat client at the bottom of each judge call.</param>
    /// <param name="redactor">The redactor of the process; it masks a registered key in a reply before the reply is kept.</param>
    /// <param name="retryOptions">The 429 retry policy of every judge call.</param>
    /// <param name="random">The source of the retry jitter: <see cref="Random.Shared"/>, used directly, or any other instance, from which one seed per call is drawn under a lock.</param>
    /// <param name="maxOutputTokens">The output cap of one judge call (<c>Judge:MaxOutputTokens</c>), from 1 to <see cref="MaxReplyChars"/>; the request carries it as the cap of the SDK and the body patch of the tier names the field.</param>
    /// <param name="systemPromptPrefix">The <c>SystemPromptPrefix</c> of the tier <paramref name="judgeTier"/>, sent as the system message of every judge call without its trailing white space; null, empty or white space sends no system message.</param>
    /// <exception cref="ArgumentNullException">An argument other than <paramref name="judgeModelId"/> or <paramref name="systemPromptPrefix"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="rubric"/> is blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="judgeTier"/> is not a defined tier, or <paramref name="maxOutputTokens"/> is not from 1 to 32768.</exception>
    public JudgeService(
        Tier judgeTier,
        string? judgeModelId,
        string rubric,
        IProviderClientFactory providers,
        SecretRedactor redactor,
        RetryOptions retryOptions,
        Random random,
        int maxOutputTokens = JudgeOptions.DefaultMaxOutputTokens,
        string? systemPromptPrefix = null)
    {
        if (!Enum.IsDefined(judgeTier))
        {
            throw new ArgumentOutOfRangeException(nameof(judgeTier), (int)judgeTier, "The judge tier is not a defined tier.");
        }

        if (maxOutputTokens is < 1 or > MaxReplyChars)
        {
            throw new ArgumentOutOfRangeException(nameof(maxOutputTokens), maxOutputTokens, "The judge output cap must be from 1 to 32768.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(rubric);
        _tier = judgeTier;
        _modelId = judgeModelId;
        _rubric = rubric;
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
        _retryOptions = retryOptions ?? throw new ArgumentNullException(nameof(retryOptions));
        _random = random ?? throw new ArgumentNullException(nameof(random));
        _maxOutputTokens = maxOutputTokens;

        // The same rule as AgentCatalog.InstructionsFor: a blank prefix is no prefix, and the trailing white space of a configured one is not sent.
        _systemPrompt = string.IsNullOrWhiteSpace(systemPromptPrefix) ? null : systemPromptPrefix.TrimEnd();
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="request"/> or a member of it is null.</exception>
    /// <exception cref="ArgumentException">A step names a task that the request does not hold; nothing was called.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<JudgeResult> ScoreAsync(JudgeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Steps);
        ArgumentNullException.ThrowIfNull(request.Tasks);
        ArgumentNullException.ThrowIfNull(request.Services);

        Dictionary<string, TaskDefinition> tasks = new(StringComparer.Ordinal);
        foreach (TaskDefinition task in request.Tasks)
        {
            _ = tasks.TryAdd(task.Name, task);
        }

        // Every task is looked up before the first call, so a request that names an unknown task has called nothing and published nothing.
        foreach (StepReading step in request.Steps)
        {
            ArgumentNullException.ThrowIfNull(step, nameof(request));
            if (!tasks.ContainsKey(step.Task))
            {
                throw new ArgumentException("A step of the request names a task that the request does not hold.", nameof(request));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        bool equalsAStepModel = request.Steps.Any(step => string.Equals(step.ModelId, _modelId, StringComparison.Ordinal));
        SortedSet<string> kinds = new(StringComparer.Ordinal);
        StepReading[] scored = new StepReading[request.Steps.Count];
        int judged = 0;
        int failed = 0;
        for (int i = 0; i < scored.Length; i++)
        {
            StepReading step = request.Steps[i];
            if (!string.Equals(step.Outcome, StepOutcomes.Ok, StringComparison.Ordinal))
            {
                scored[i] = RunAssembler.WithQuality(step, Metric.NotApplicable(StepNotOkReason), NotScored);
                continue;
            }

            if (step.OutputChars is null or 0)
            {
                scored[i] = RunAssembler.WithQuality(step, Metric.NotApplicable(EmptyOutputReason), NotScored);
                continue;
            }

            judged++;
            (Metric quality, string? qualityReason, string? failure) = await ScoreStepAsync(request, step, tasks[step.Task], cancellationToken).ConfigureAwait(false);
            scored[i] = RunAssembler.WithQuality(step, quality, qualityReason);
            if (failure is not null)
            {
                failed++;
                _ = kinds.Add(failure);
            }
        }

        // The equality of the ids is a fact only when a step was judged by a configured model; otherwise the run makes no statement (null), never "false".
        bool? selfJudged = judged > 0 && !string.IsNullOrWhiteSpace(_modelId) ? equalsAStepModel : null;

        return new JudgeResult(scored, selfJudged, judged, failed, [.. kinds]);
    }

    private static (Metric Quality, string? QualityReason, string? Failure) Failed(string kind) =>
        (Metric.NotApplicable(FailedPrefix + kind), NotScored, kind);

    private async Task<(Metric Quality, string? QualityReason, string? Failure)> ScoreStepAsync(JudgeRequest request, StepReading step, TaskDefinition task, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_modelId))
        {
            return Failed(ModelNotConfiguredKind);
        }

        // A measured judge tier is the GPU of one tier run: the meter of another run would measure the wrong thing.
        if (TierKinds.IsMeasured(_tier) && _tier != request.Tier)
        {
            return Failed(JudgeTierNotMeteredKind);
        }

        if (string.IsNullOrEmpty(step.OutputText))
        {
            return Failed(OutputNotInMemoryKind);
        }

        try
        {
            string reply = await CallAsync(request, step, task, step.OutputText, cancellationToken).ConfigureAwait(false);
            // Redact first, cut second: a registered key that straddles the cut would otherwise be cut in two and no longer match. The parser redacts what is left again, which changes nothing.
            JudgeParseResult parsed = JudgeScoreParser.Parse(Bounded(_redactor.Redact(reply)), _redactor);
            if (parsed.Parsed)
            {
                return (parsed.Quality, null, null);
            }

            // A reply that was cut, or that holds a think tag left in place, can show a part of a key that the redactor could not match whole: the excerpt is then a fixed text, never reply text.
            if (reply.Length > MaxReplyChars)
            {
                return (Metric.NotApplicable(ParseErrorPrefix + ReplyTooLongExcerpt), NotScored, ParseErrorKind);
            }

            return parsed.ThinkTagLeft
                ? (Metric.NotApplicable(ParseErrorPrefix + ThinkTagLeftExcerpt), NotScored, ParseErrorKind)
                : (parsed.Quality, NotScored, ParseErrorKind);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception failure)
        {
            // The type name only: the message of a failure can quote the request, the response or the path of a file.
            return Failed(failure.GetType().Name);
        }
    }

    // The reply cut to the bound, never in the middle of a surrogate pair.
    private static string Bounded(string reply)
    {
        if (reply.Length <= MaxReplyChars)
        {
            return reply;
        }

        int length = char.IsHighSurrogate(reply[MaxReplyChars - 1]) ? MaxReplyChars - 1 : MaxReplyChars;
        return reply[..length];
    }

    // One metered, buffered call on a pipeline of its own: the provider client, the retry layer, the metered client outermost, one ledger shared by the last two; disposed when the call ends.
    private async Task<string> CallAsync(JudgeRequest request, StepReading step, TaskDefinition task, string output, CancellationToken cancellationToken)
    {
        string prompt = JudgePromptBuilder.Build(_rubric, task.Prompt, step.Agent, output);
        UnmeteredEnergyMeter? hosted = TierKinds.IsHosted(_tier) ? new UnmeteredEnergyMeter(request.Services.Time) : null;
        try
        {
            MeteringServices services = request.Services;
            if (hosted is not null)
            {
                await hosted.StartAsync(cancellationToken).ConfigureAwait(false);
                services = services with { Meter = hosted };
            }

            var identity = new StepIdentity(TierOptions.JudgeAgentName, _modelId!, _tier, step.Task, step.Repeat, IsJudge: true, JudgedAgent: step.Agent);
            var ledger = new RetryLedger();
            Random jitter = JitterForOnePipeline();
            IChatClient provider = _providers.Create(_tier, _modelId!);
            IChatClient pipeline;
            try
            {
                pipeline = new ChatClientBuilder(provider)
                    .Use(inner => new MeteredChatClient(inner, identity, services, ledger))
                    .Use(inner => new RateLimitRetryChatClient(inner, ledger, _retryOptions, request.Services.Time, jitter))
                    .Build();
            }
            catch
            {
                provider.Dispose();
                throw;
            }

            using (pipeline)
            {
                ChatResponse response = await pipeline
                    .GetResponseAsync(Messages(prompt), new ChatOptions { ModelId = _modelId, Temperature = 0f, MaxOutputTokens = _maxOutputTokens }, cancellationToken)
                    .ConfigureAwait(false);

                return response.Text;
            }
        }
        finally
        {
            if (hosted is not null)
            {
                await hosted.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    // The messages of one judge call: the system prompt prefix of the judge tier first, when it has one (so that a mode switch such as ReasoningMode off reaches the judge as it reaches an agent), then the prompt.
    private List<ChatMessage> Messages(string prompt)
    {
        List<ChatMessage> messages = [];
        if (_systemPrompt is not null)
        {
            messages.Add(new ChatMessage(ChatRole.System, _systemPrompt));
        }

        messages.Add(new ChatMessage(ChatRole.User, prompt));
        return messages;
    }

    // Random.Shared is thread-safe and goes to the pipeline as it is; any other instance is not, so it only draws one seed per pipeline, under a lock (the rule of the agent factory).
    private Random JitterForOnePipeline()
    {
        if (ReferenceEquals(_random, Random.Shared))
        {
            return Random.Shared;
        }

        lock (_seedGate)
        {
            return new Random(_random.Next());
        }
    }
}