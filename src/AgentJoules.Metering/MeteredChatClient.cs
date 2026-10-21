using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

using AgentJoules.Core.Cost;
using AgentJoules.Core.Energy;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;

using Microsoft.Extensions.AI;

namespace AgentJoules.Metering;

/// <summary>
/// The decorator that meters every call of one agent and publishes exactly one <see cref="StepReading"/> per call (REQ-007, design 3.3). In the
/// pipeline of an agent it is the outermost client, so the retry layer below it is inside the measurement window:
/// <c>ChatClientAgent -> MeteredChatClient -> RateLimitRetryChatClient -> OpenAIChatClient</c>.
/// </summary>
/// <remarks>
/// <para>
/// Attribution is by construction: the <see cref="StepIdentity"/> is a constructor argument and is copied into the reading, and there is no
/// ambient context to read it from. The price is the one-in-flight rule: a client serves one call at a time (an agent makes one call at a time),
/// and a second call that starts while one is in flight, buffered or streaming, throws <see cref="InvalidOperationException"/> and changes
/// nothing about the call in flight. A streaming call is in flight from the first <c>MoveNextAsync</c> until its enumerator is disposed or ends.
/// </para>
/// <para>
/// Per call, in this order: take the gate (its wait is outside the window and outside the latency; a call cancelled while it waits has no window
/// and so no reading); then, in a <c>try</c> that begins right after the gate was taken and ends by giving it back, open the window, read the
/// start, call the inner client, and in the <c>finally</c> close the window, give the gate back and publish the reading. A streaming call does
/// the same in an iterator whose <c>finally</c> runs on completion, on an exception, on a cancellation and when the consumer disposes the
/// enumerator early, which is why there is one reading per call on every path where the enumerator is disposed. An enumerator that is neither
/// run to its end nor disposed is never finished: an async iterator has no finalizer and nothing resumes a paused one, so the window stays open
/// and the process-wide gate stays held for the life of the gate (the run token only ends the other callers' waits; AJ-032 carry); consumers
/// must dispose (<c>await foreach</c> does). A stream that was obtained but never
/// enumerated started no call and costs nothing. The exception of a failed call is published as an <c>error</c> (or
/// <c>cancelled</c> or <c>incomplete</c>, see <see cref="OutcomeClassifier"/>) reading and then rethrown unchanged.
/// </para>
/// <para>
/// The time to the first token of a stream is the time from the start to the first update that holds non-empty answer text or reasoning text;
/// a buffered call has none (N/A, reason <c>buffered-call</c>), and a stream that ends without any text has none either (N/A, reason
/// <c>no-content-update</c>). The output fingerprint and the output text are those of the answer text only (reasoning text is not output).
/// The token counts are the provider's own (REQ-008, EDGE-007, see <see cref="TokenCounts"/>): the usage of a buffered response, or the last usage
/// content of a stream (the last replaces the earlier ones, because a server may repeat cumulative usage on every chunk); counts the provider
/// omitted are not-reported with the reason <c>provider-omitted-usage</c>, never 0. The cost comes only from the <see cref="ICostCalculator"/> of the
/// services. Completion tokens above the <see cref="ChatOptions.MaxOutputTokens"/> of the call add the <see cref="ReadingFlags.MaxTokensExceeded"/>
/// flag to the reading; the flag is absent when the call has no limit or the completion tokens are not reported.
/// </para>
/// <para>
/// The retry layer below this client (<see cref="RateLimitRetryChatClient"/>, EDGE-006) reports through the <see cref="RetryLedger"/> both share: the
/// client resets the ledger when a call starts (right after the gate was taken), and the reading carries its <see cref="StepReading.Retries"/> and
/// <see cref="StepReading.WaitMs"/>. The wait is out of the measurements it would distort: <c>latencyMs</c> is the elapsed time minus the wait and the time to
/// the first token is the first text update's time minus the start minus the wait, while the window of the energy meter, <c>startUtc</c> and <c>endUtc</c> span the
/// whole call. The wall seconds given to the <see cref="ICostCalculator"/> are the whole elapsed time, the wait included, because a rented GPU is billed for it.
/// </para>
/// <para>
/// A failure of the meter itself (the window cannot be opened or closed) is not swallowed when the call itself succeeded: it leaves the call as an
/// exception, after the gate was given back. When the call had already failed, its own exception wins, the meter failure is attached to it under
/// <see cref="MeterCloseFailureKey"/> in <see cref="Exception.Data"/>, and no reading is published (there is no energy to put in it).
/// The same holds for a failure of the reading itself (the cost calculator, the reading's checks, the sink, the OpenTelemetry instruments, which record the reading right after the sink received it, REQ-014): after a failed call it is attached under
/// <see cref="ReadingFailureKey"/>, after a successful call it surfaces.
/// </para>
/// </remarks>
public sealed class MeteredChatClient : DelegatingChatClient
{
    private const string MillisecondUnit = "ms";
    private const string BufferedReason = "buffered-call";
    private const string NoContentReason = "no-content-update";

    /// <summary>The key under which the <see cref="Exception.Data"/> of a call's exception holds the failure of the meter when the window could not be closed while the call itself failed.</summary>
    public const string MeterCloseFailureKey = "AgentJoules.MeterCloseFailure";

    /// <summary>The key under which the <see cref="Exception.Data"/> of a call's exception holds the failure of building or publishing the reading (the cost calculator, the reading's own checks, the sink, the OpenTelemetry instruments) when the call itself had already failed.</summary>
    public const string ReadingFailureKey = "AgentJoules.ReadingFailure";

    /// <summary>The key under which the <see cref="Exception.Data"/> of a call's exception holds the failure of the inner enumerator's <c>DisposeAsync</c> when the call itself had already failed.</summary>
    public const string InnerDisposeFailureKey = "AgentJoules.InnerDisposeFailure";

    private readonly StepIdentity _identity;
    private readonly MeteringServices _services;
    private readonly RetryLedger _ledger;

    private int _inFlight;

    /// <summary>Creates a client that meters the calls of one agent and reads the retries of its calls from <paramref name="ledger"/>.</summary>
    /// <param name="inner">The client whose calls are metered; in an agent's pipeline it holds the <see cref="RateLimitRetryChatClient"/> that writes <paramref name="ledger"/>.</param>
    /// <param name="identity">Who the readings are attributed to; copied into every reading.</param>
    /// <param name="services">The shared meter, gate, sink, cost calculator, instruments and clock.</param>
    /// <param name="ledger">The ledger the retry layer below this client records its retries and waits in. It must be the same instance as the retry layer's, which offers it through <c>GetService(typeof(RetryLedger))</c>: the client resets it when a call starts and copies <see cref="RetryLedger.Retries"/> and <see cref="RetryLedger.WaitMs"/> into the reading. A ledger belongs to exactly one pipeline.</param>
    /// <exception cref="ArgumentNullException">An argument, or a member of <paramref name="services"/>, is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="inner"/> holds a retry layer whose ledger is not <paramref name="ledger"/>: such a pipeline would report 0 retries and a latency that includes the waits.</exception>
    public MeteredChatClient(IChatClient inner, StepIdentity identity, MeteringServices services, RetryLedger ledger)
        : base(inner)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(services.Meter);
        ArgumentNullException.ThrowIfNull(services.Gate);
        ArgumentNullException.ThrowIfNull(services.Sink);
        ArgumentNullException.ThrowIfNull(services.Cost);
        ArgumentNullException.ThrowIfNull(services.Instruments);
        ArgumentNullException.ThrowIfNull(services.Time);
        ArgumentNullException.ThrowIfNull(ledger);

        if (inner.GetService(typeof(RetryLedger)) is RetryLedger found && !ReferenceEquals(found, ledger))
        {
            throw new ArgumentException("The retry layer of this pipeline writes a different ledger than the one given; a ledger belongs to exactly one pipeline, and the metered client and its retry layer must share it.", nameof(ledger));
        }

        _identity = identity;
        _services = services;
        _ledger = ledger;
    }

    /// <summary>
    /// Creates a client that adopts the ledger of the <see cref="RateLimitRetryChatClient"/> inside <paramref name="inner"/> (found through <c>GetService(typeof(RetryLedger))</c>), or, for a
    /// pipeline that has no retry layer, a ledger of its own that nothing writes, so every reading has 0 retries and a wait of 0. A ledger belongs to exactly one pipeline.
    /// </summary>
    /// <param name="inner">The client whose calls are metered.</param>
    /// <param name="identity">Who the readings are attributed to; copied into every reading.</param>
    /// <param name="services">The shared meter, gate, sink, cost calculator, instruments and clock.</param>
    /// <exception cref="ArgumentNullException">An argument, or a member of <paramref name="services"/>, is null.</exception>
    public MeteredChatClient(IChatClient inner, StepIdentity identity, MeteringServices services)
        : this(inner, identity, services, LedgerOf(inner))
    {
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">Another call of this client is in flight.</exception>
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        EnterCall();
        try
        {
            IAsyncDisposable releaser = await _services.Gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
            MeteredCall call = new(this, releaser, streaming: false, options?.MaxOutputTokens, cancellationToken);
            try
            {
                call.Open();
                ChatResponse response = await InnerClient.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
                call.SetText(response.Text);
                call.SetUsage(response.Usage);
                call.Completed = true;
                return response;
            }
            catch (Exception failure)
            {
                call.Error = failure;
                throw;
            }
            finally
            {
                await call.FinishAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            ExitCall();
        }
    }

    /// <inheritdoc />
    /// <remarks>The call starts, and the one-in-flight rule and the gate apply, at the first <c>MoveNextAsync</c> of the returned sequence.</remarks>
    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        return StreamAsync(messages, options, cancellationToken);
    }

    // An iterator cannot yield inside a try that has a catch, so only the MoveNextAsync of the inner enumerator is in the try/catch, the yield is
    // outside it, and the outer finally (no catch) closes the window, gives the gate back and publishes on every way out of the iterator.
    private async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(IEnumerable<ChatMessage> messages, ChatOptions? options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnterCall();
        try
        {
            IAsyncDisposable releaser = await _services.Gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
            MeteredCall call = new(this, releaser, streaming: true, options?.MaxOutputTokens, cancellationToken);
            IAsyncEnumerator<ChatResponseUpdate>? updates = null;
            try
            {
                call.Open();
                while (true)
                {
                    bool hasNext;
                    ChatResponseUpdate? current = null;
                    try
                    {
                        updates ??= InnerClient.GetStreamingResponseAsync(messages, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
                        hasNext = await updates.MoveNextAsync().ConfigureAwait(false);

                        // Reading and observing the update is inside the try: a failure here is the call's error, never a benign outcome.
                        if (hasNext)
                        {
                            current = updates.Current;
                            call.Observe(current);
                        }
                    }
                    catch (Exception failure)
                    {
                        call.Error = failure;
                        throw;
                    }

                    if (!hasNext)
                    {
                        call.Completed = true;
                        break;
                    }

                    // The consumer walking away is only known when the finally runs while the iterator is paused right here.
                    call.PausedAtYield = true;
                    yield return current!;
                    call.PausedAtYield = false;
                }
            }
            finally
            {
                try
                {
                    await DisposeUpdatesAsync(updates, call).ConfigureAwait(false);
                }
                finally
                {
                    await call.FinishAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            ExitCall();
        }
    }

    // A failing DisposeAsync of the inner enumerator is the call's error even when the stream itself ended cleanly (a clean-looking ok must not hide it);
    // when the call had already failed, the first exception stays the call's error and this one is attached to it.
    private static async ValueTask DisposeUpdatesAsync(IAsyncEnumerator<ChatResponseUpdate>? updates, MeteredCall call)
    {
        if (updates is null)
        {
            return;
        }

        try
        {
            await updates.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception disposeFailure) when (call.Error is not null)
        {
            // The call had already failed: its exception is the one the caller receives, so the disposal failure rides along on it.
            call.Error.Data[InnerDisposeFailureKey] = disposeFailure;
        }
        catch (Exception disposeFailure)
        {
            call.Error = disposeFailure;
            throw;
        }
    }

    // The ledger of the retry layer inside the pipeline, or a fresh one when there is none.
    private static RetryLedger LedgerOf(IChatClient inner)
    {
        ArgumentNullException.ThrowIfNull(inner);

        return inner.GetService(typeof(RetryLedger)) as RetryLedger ?? new RetryLedger();
    }

    private void EnterCall()
    {
        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0)
        {
            throw new InvalidOperationException(
                $"The metered client of agent {_identity.Agent} already has a call in flight. An agent makes one call at a time, and a second call on the same instance would blur which reading it belongs to.");
        }
    }

    private void ExitCall() => Volatile.Write(ref _inFlight, 0);

    // The state of one call between the gate and the sink. Every field is touched by the one flow that runs the call, one step at a time.
    private sealed class MeteredCall(MeteredChatClient owner, IAsyncDisposable releaser, bool streaming, int? maxOutputTokens, CancellationToken callerToken)
    {
        private readonly StringBuilder _text = new();
        private IMeterWindow? _window;
        private long _startTimestamp;
        private DateTimeOffset _startUtc;
        private Metric? _timeToFirstToken;
        // The two counts the provider stated, each replaced on its own by a later non-null count and never added up.
        private long? _inputTokenCount;
        private long? _outputTokenCount;

        public Exception? Error { get; set; }

        public bool Completed { get; set; }

        // True while the iterator of a stream is paused at its yield; a finally that runs then is the consumer walking away.
        public bool PausedAtYield { get; set; }

        // True once any update arrived (even one without text, such as a role-only first chunk); this, not the first text, is what EDGE-016 means.
        private bool AnyUpdateSeen { get; set; }

        public void Open()
        {
            // The ledger belongs to one call at a time: whatever the previous call left in it is not this call's, and the retry layer below starts
            // writing only after this method returned.
            owner._ledger.Reset();

            TimeProvider time = owner._services.Time;
            _window = owner._services.Meter.OpenWindow();
            _startTimestamp = time.GetTimestamp();
            _startUtc = time.GetUtcNow();
        }

        public void SetText(string text) => _text.Append(text);

        // The usage of a buffered response or of a stream's update: only the two token counts are read, and no provider object is kept. A non-null
        // count replaces the earlier one; a null count leaves it, so a trailing usage chunk that states nothing wipes nothing.
        public void SetUsage(UsageDetails? usage)
        {
            _inputTokenCount = usage?.InputTokenCount ?? _inputTokenCount;
            _outputTokenCount = usage?.OutputTokenCount ?? _outputTokenCount;
        }

        private TokenCounts Tokens => TokenCounts.From(_inputTokenCount, _outputTokenCount);

        // Collects the answer text of an update and notes the time of the first update that holds text (answer or reasoning).
        public void Observe(ChatResponseUpdate update)
        {
            AnyUpdateSeen = true;
            bool hasText = false;
            foreach (AIContent content in update.Contents)
            {
                switch (content)
                {
                    case TextContent { Text.Length: > 0 } answer:
                        _text.Append(answer.Text);
                        hasText = true;
                        break;
                    case TextReasoningContent { Text.Length: > 0 }:
                        hasText = true;
                        break;
                    case UsageContent { Details: { } details }:
                        // The last stated count wins and replaces the earlier ones: a server that repeats cumulative usage on every chunk would be
                        // counted several times by a sum (ToChatResponse().Usage adds every UsageContent up).
                        SetUsage(details);
                        break;
                    default:
                        break;
                }
            }

            if (hasText && _timeToFirstToken is null)
            {
                _timeToFirstToken = Metric.Measured(Math.Max(0.0, ElapsedMilliseconds() - owner._ledger.WaitMs), MillisecondUnit);
            }
        }

        // Closes the window, gives the gate back and publishes the reading, in this order. A call whose window never opened has nothing to publish.
        public async ValueTask FinishAsync()
        {
            EnergyReading? energy = null;
            double elapsedMs = 0.0;
            long waitMs = 0;
            int retries = 0;
            DateTimeOffset endUtc = _startUtc;
            try
            {
                if (_window is not null)
                {
                    elapsedMs = ElapsedMilliseconds();
                    waitMs = owner._ledger.WaitMs;
                    retries = owner._ledger.Retries;
                    endUtc = owner._services.Time.GetUtcNow();
                    energy = _window.Close();
                }
            }
            catch (Exception meterFailure) when (Error is not null)
            {
                // The call's own exception wins: it is the one the caller needs, and it is on its way. The meter failure stays attached to it, and
                // no reading is published because there is no energy to put in it. A call that succeeded gets no such cover: its meter failure
                // surfaces, because a benchmark must not return ok without a measurement.
                Error.Data[MeterCloseFailureKey] = meterFailure;
            }
            finally
            {
                await releaser.DisposeAsync().ConfigureAwait(false);
            }

            if (energy is not null)
            {
                try
                {
                    StepReading reading = BuildReading(energy, elapsedMs, retries, waitMs, endUtc);
                    owner._services.Sink.Publish(reading);

                    // The instruments are recorded after the sink received the reading, in the same guarded block: a recording failure follows the rule of every other failure of the reading.
                    owner._services.Instruments.Record(reading);
                }
                catch (Exception readingFailure) when (Error is not null)
                {
                    // Same rule as the meter failure: the gate is already free, the call's own exception is the one the caller needs and stays the
                    // one that is thrown, and the failure of the cost calculator, the reading or the sink rides along on it. A call that succeeded
                    // gets no such cover: the failure surfaces, because a benchmark must not return ok without its reading.
                    Error.Data[ReadingFailureKey] = readingFailure;
                }
            }
        }

        // The whole time since the start, the retry waits included.
        private double ElapsedMilliseconds() => owner._services.Time.GetElapsedTime(_startTimestamp).TotalMilliseconds;

        private StepReading BuildReading(EnergyReading energy, double elapsedMs, int retries, long waitMs, DateTimeOffset endUtc)
        {
            StepIdentity identity = owner._identity;

            // No exception and no completion: a stream whose enumerator was disposed while paused at its yield did not fail, its consumer walked
            // away. Any other way to end without an exception or a completion is not understood, and an error is the honest label for it.
            string outcome = Error is null && !Completed
                ? PausedAtYield ? StepOutcomes.Cancelled : StepOutcomes.Error
                : OutcomeClassifier.Classify(Error, identity.Tier, firstUpdateSeen: AnyUpdateSeen, callerToken);

            string text = _text.ToString();
            bool hasOutput = text.Length > 0 || outcome == StepOutcomes.Ok;
            Metric ttft = _timeToFirstToken ?? Metric.NotApplicable(streaming ? NoContentReason : BufferedReason);

            // The one place a closed window becomes the energy members of a step reading. A hosted tier's unmetered window has no method, no
            // samples and no rate to state (the reading refuses the unmetered method and requires them absent on a hosted tier).
            bool metered = energy.Method != EnergyMethod.Unmetered;

            // The only source of the cost is the calculator of the services; the decorator holds no price. The wall time it is given is the whole elapsed
            // time, the retry waits included (a rented GPU is billed for them), while the latency of the reading leaves them out.
            double latencyMs = Math.Max(0.0, elapsedMs - waitMs);
            TokenCounts tokens = Tokens;
            Metric cost = owner._services.Cost.Compute(identity, tokens, energy, elapsedMs / 1000.0);

            return new StepReading(
                task: identity.Task,
                repeat: identity.Repeat,
                agent: identity.Agent,
                modelId: identity.ModelId,
                tier: identity.Tier,
                startUtc: _startUtc,
                endUtc: endUtc < _startUtc ? _startUtc : endUtc,
                outcome: outcome,
                latencyMs: Metric.Measured(latencyMs, MillisecondUnit),
                ttftMs: ttft,
                inputTokens: tokens.InputTokens,
                outputTokens: tokens.OutputTokens,
                grossJoules: energy.GrossJoules,
                netJoules: energy.NetJoules,
                avgW: energy.AverageWatts,
                peakW: energy.PeakWatts,
                cost: cost,
                retries: retries,
                waitMs: waitMs,
                flags: energy.Flags | (tokens.ExceedsOutputLimit(maxOutputTokens) ? ReadingFlags.MaxTokensExceeded : ReadingFlags.None),
                method: metered ? energy.Method : null,
                sampleCount: metered ? energy.SampleCount : null,
                sampleRateHz: metered ? energy.SampleRateHz : null,
                throttleReasons: energy.ThrottleReasons,
                outputSha256: hasOutput ? Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))) : null,
                outputChars: hasOutput ? text.Length : null,
                outputText: hasOutput ? text : null,

                // The window has closed (there is energy only after Close returned), so its span is final; the run assembler reads it for the shared-window
                // flag and the union of a concurrent run (AJ-047). A hosted window has a span too.
                window: _window?.Span);
        }
    }
}