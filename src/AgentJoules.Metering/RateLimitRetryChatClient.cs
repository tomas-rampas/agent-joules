using System.ClientModel;
using System.ClientModel.Primitives;
using System.Globalization;
using System.Runtime.CompilerServices;

using AgentJoules.Core.Cli;

using Microsoft.Extensions.AI;

namespace AgentJoules.Metering;

/// <summary>
/// Retries a call that the provider answered with HTTP 429 (EDGE-006, ADR-11). In the pipeline of an agent it sits directly under the metering decorator
/// and over the provider client: <c>MeteredChatClient -> RateLimitRetryChatClient -> OpenAIChatClient</c>, so every attempt and every wait is inside the
/// measurement window and the reading is still one per call.
/// </summary>
/// <remarks>
/// <para>
/// Only a <see cref="ClientResultException"/> whose <see cref="ClientResultException.Status"/> is 429 is retried: a 500, a timeout, a cancellation and a
/// connection error are attempted once and surface unchanged. The SDK's own retry is switched off (AJ-044), so one attempt is exactly one HTTP request. A
/// streaming call is retried only when the 429 comes out of the first <c>MoveNextAsync</c>, which is when the SDK sends the request and reads the status;
/// once a first update was handed to the consumer, every later failure, a 429 included, propagates, because the consumer has already seen part of an answer.
/// </para>
/// <para>
/// The wait before a retry is the <c>Retry-After</c> header of the 429 answer when it holds a whole number of seconds or an HTTP date that is not in the
/// past; the header is the server's own figure and is not capped by <see cref="RetryOptions.MaxDelay"/>. A valid header above
/// <see cref="RetryOptions.MaxRetryAfter"/> ends the call at once with a <see cref="RetriesExhaustedException"/>: waiting less than the server asked would only
/// be rejected again, and waiting that long would hold the call, and with a serialized run the meter gate, for as long as the server likes. Without a usable
/// header (none, a negative or unparseable value, a date in the past) the wait is full jitter,
/// <c>random.NextDouble() x min(MaxDelay, BaseDelay x Factor^n)</c>, where n is 0 for the first retry. A wait that would take the summed wait of the call
/// above <see cref="RetryOptions.MaxTotalWait"/> ends the call at once in the same way. Every wait is a whole number of milliseconds and is taken on the
/// <see cref="TimeProvider"/> (a fake clock in tests). What was actually waited, measured on that clock, goes into the <see cref="RetryLedger"/> even when the
/// wait is cut short by a cancellation (that part counts as wait, not as a retry), so the latency of a cancelled call excludes it too. The attempt that is
/// rejected for the <see cref="RetryOptions.MaxAttempts"/>-th time ends the call with a <see cref="RetriesExhaustedException"/> as well, which carries only
/// the attempt count and the status.
/// </para>
/// <para>
/// A client serves one call at a time, like the metering decorator above it. This class logs nothing: the response of a rejected call, its headers and its
/// request URI can echo the credential, so none of them leaves this class except as the number the wait was computed from. The ledger belongs to exactly
/// one pipeline: the client offers it as a service (<see cref="GetService"/>), and the <see cref="MeteredChatClient"/> above refuses a different instance.
/// </para>
/// </remarks>
public sealed class RateLimitRetryChatClient : DelegatingChatClient
{
    private const int RateLimited = 429;
    private const string RetryAfterHeader = "Retry-After";

    // A conservative bound (about 24 days) below the longest wait Task.Delay accepts, so that no configured ceiling can make the wait itself throw.
    private static readonly TimeSpan LargestConfigurableWait = TimeSpan.FromMilliseconds(int.MaxValue);

    // The three formats of an HTTP date (RFC 9110): IMF-fixdate, the obsolete RFC 850 form and asctime.
    private static readonly string[] HttpDateFormats =
    [
        "r",
        "dddd, dd'-'MMM'-'yy HH':'mm':'ss 'GMT'",
        "ddd MMM d HH':'mm':'ss yyyy",
    ];

    private readonly RetryLedger _ledger;
    private readonly RetryOptions _policy;
    private readonly TimeProvider _time;
    private readonly Random _random;
    private readonly Lock _randomGate = new();

    /// <summary>Creates the retry layer of one agent's pipeline.</summary>
    /// <param name="inner">The client whose 429 answers are retried.</param>
    /// <param name="ledger">The ledger that records the retries and their waits; it belongs to this pipeline alone, and the <see cref="MeteredChatClient"/> above must use the same instance (it finds it through <see cref="GetService"/>).</param>
    /// <param name="options">The policy: attempts, base, factor, cap, and the ceilings of one <c>Retry-After</c> and of the whole wait.</param>
    /// <param name="time">The clock the waits are taken on; a fake clock in tests.</param>
    /// <param name="random">The source of the jitter; a seeded instance in tests. It is used under a lock, so one instance may be shared.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="options"/> has fewer than 1 attempt, a negative base delay, a maximum delay that is negative or above <see cref="RetryOptions.MaxRetryAfter"/>, a non-positive <see cref="RetryOptions.MaxRetryAfter"/> or <see cref="RetryOptions.MaxTotalWait"/>, one of them beyond what <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/> can wait, or a factor below 1 or not finite.</exception>
    public RateLimitRetryChatClient(IChatClient inner, RetryLedger ledger, RetryOptions options, TimeProvider time, Random random)
        : base(inner)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(random);
        Check(options);

        _ledger = ledger;
        _policy = options;
        _time = time;
        _random = random;
    }

    /// <inheritdoc />
    /// <remarks>Answers the <see cref="RetryLedger"/> of this client for <c>typeof(RetryLedger)</c> with no key, so that the metering decorator above can find and check it; everything else is answered as <see cref="DelegatingChatClient"/> does.</remarks>
    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        return serviceKey is null && serviceType == typeof(RetryLedger) ? _ledger : base.GetService(serviceType, serviceKey);
    }

    /// <inheritdoc />
    /// <exception cref="RetriesExhaustedException">Every attempt was answered with HTTP 429, or the server asked for a longer wait than the policy allows.</exception>
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        TimeSpan waited = TimeSpan.Zero;
        for (int retry = 0; ; retry++)
        {
            ClientResultException rejected;
            try
            {
                return await InnerClient.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            }
            catch (ClientResultException failure) when (failure.Status == RateLimited)
            {
                rejected = failure;
            }

            waited += await WaitBeforeRetryAsync(retry, rejected, waited, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    /// <remarks>The call starts at the first <c>MoveNextAsync</c> of the returned sequence; a 429 there is retried, a failure after the first update is not.</remarks>
    /// <exception cref="RetriesExhaustedException">Every attempt, each before its first update, was answered with HTTP 429, or the server asked for a longer wait than the policy allows; thrown by the first <c>MoveNextAsync</c>.</exception>
    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        return StreamAsync(messages, options, cancellationToken);
    }

    // An iterator cannot yield inside a try that has a catch: the retry loop, which has catches, is a method of its own that ends at the first update, and
    // the iterator only forwards the rest with the same shape as the metering decorator (a try/catch around MoveNextAsync alone, the yield outside it).
    private async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(IEnumerable<ChatMessage> messages, ChatOptions? options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        (IAsyncEnumerator<ChatResponseUpdate> updates, bool hasUpdate) = await OpenAsync(messages, options, cancellationToken).ConfigureAwait(false);
        Exception? error = null;
        try
        {
            while (hasUpdate)
            {
                yield return updates.Current;

                try
                {
                    hasUpdate = await updates.MoveNextAsync().ConfigureAwait(false);
                }
                catch (Exception failure)
                {
                    error = failure;
                    throw;
                }
            }
        }
        finally
        {
            await DisposeAsync(updates, error).ConfigureAwait(false);
        }
    }

    // Starts the stream and reads its first update, retrying a 429 that comes out of that first read. Returns the enumerator, positioned on its first
    // update (hasUpdate) or at its end; on any exception the enumerator is disposed here and the exception propagates.
    private async Task<(IAsyncEnumerator<ChatResponseUpdate> Updates, bool HasUpdate)> OpenAsync(IEnumerable<ChatMessage> messages, ChatOptions? options, CancellationToken cancellationToken)
    {
        TimeSpan waited = TimeSpan.Zero;
        for (int retry = 0; ; retry++)
        {
            IAsyncEnumerator<ChatResponseUpdate> updates = InnerClient.GetStreamingResponseAsync(messages, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
            ClientResultException rejected;
            try
            {
                return (updates, await updates.MoveNextAsync().ConfigureAwait(false));
            }
            catch (ClientResultException failure) when (failure.Status == RateLimited)
            {
                rejected = failure;
            }
            catch (Exception failure)
            {
                await DisposeAsync(updates, failure).ConfigureAwait(false);
                throw;
            }

            // The 429 is consumed: nothing the enumerator that produced it fails with on disposal is worth more than the retry that follows.
            await DisposeQuietlyAsync(updates).ConfigureAwait(false);
            waited += await WaitBeforeRetryAsync(retry, rejected, waited, cancellationToken).ConfigureAwait(false);
        }
    }

    // Ends the call with the exhaustion exception when the attempt that was just rejected was the last one, when the server asks for a longer wait than the
    // policy allows, or when the wait would take the call above its total budget; otherwise takes the wait and returns how long it actually was.
    private async Task<TimeSpan> WaitBeforeRetryAsync(int retry, ClientResultException rejected, TimeSpan waitedSoFar, CancellationToken cancellationToken)
    {
        int attemptsMade = retry + 1;
        if (attemptsMade >= _policy.MaxAttempts)
        {
            throw new RetriesExhaustedException(attemptsMade, RateLimited);
        }

        TimeSpan delay;
        if (ReadRetryAfter(rejected) is { } requested)
        {
            // The server's own figure, not a backoff to shorten: too long a wait ends the call, and so does a computed delay below.
            delay = requested <= _policy.MaxRetryAfter ? requested : throw new RetriesExhaustedException(attemptsMade, RateLimited);
        }
        else
        {
            delay = Jitter(retry);
        }

        return waitedSoFar + delay > _policy.MaxTotalWait
            ? throw new RetriesExhaustedException(attemptsMade, RateLimited)
            : await WaitAsync(delay, cancellationToken).ConfigureAwait(false);
    }

    // Takes one wait on the clock of the policy and writes into the ledger what was actually waited, floored to whole milliseconds: all of it as a retry when
    // the wait ran to its end, only the time that passed (no retry) when it was cut short.
    private async Task<TimeSpan> WaitAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        long started = _time.GetTimestamp();
        bool completed = false;
        TimeSpan waited;
        try
        {
            await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
            completed = true;
        }
        finally
        {
            waited = TimeSpan.FromMilliseconds(Math.Floor(_time.GetElapsedTime(started).TotalMilliseconds));
            if (completed)
            {
                _ledger.Record(waited);
            }
            else
            {
                _ledger.AddWait(waited);
            }
        }

        return waited;
    }

    // Full jitter: a uniform draw over [0, min(MaxDelay, BaseDelay x Factor^retry)), floored to whole milliseconds. A ceiling that is not below the cap (an
    // overflow to infinity and a NaN included) is the cap.
    private TimeSpan Jitter(int retry)
    {
        double capMs = _policy.MaxDelay.TotalMilliseconds;
        double ceilingMs = _policy.BaseDelay.TotalMilliseconds * Math.Pow(_policy.Factor, retry);
        if (!(ceilingMs < capMs))
        {
            ceilingMs = capMs;
        }

        double fraction;
        lock (_randomGate)
        {
            fraction = _random.NextDouble();
        }

        return TimeSpan.FromMilliseconds(Math.Floor(fraction * ceilingMs));
    }

    // The Retry-After of the rejected answer as a wait: whole seconds, or an HTTP date relative to the clock. Null when the header is absent, negative,
    // unparseable or a date that is not in the future (the caller then falls back to the computed delay). A value too large to represent is TimeSpan.MaxValue,
    // which is above every ceiling.
    private TimeSpan? ReadRetryAfter(ClientResultException rejected)
    {
        PipelineResponse? raw = rejected.GetRawResponse();
        if (raw is null || !raw.Headers.TryGetValue(RetryAfterHeader, out string? header) || header is null)
        {
            return null;
        }

        string value = header.Trim();
        if (value.Length > 0 && value.All(char.IsAsciiDigit))
        {
            return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long seconds) && seconds <= _policy.MaxRetryAfter.TotalSeconds
                ? TimeSpan.FromSeconds(seconds)
                : TimeSpan.MaxValue;
        }

        const DateTimeStyles styles = DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;
        if (DateTimeOffset.TryParseExact(value, HttpDateFormats, CultureInfo.InvariantCulture, styles, out DateTimeOffset date))
        {
            TimeSpan remaining = date - _time.GetUtcNow();
            return remaining < TimeSpan.Zero ? null : TimeSpan.FromMilliseconds(Math.Floor(remaining.TotalMilliseconds));
        }

        return null;
    }

    // A failing DisposeAsync after a failed read must not hide that failure: it rides along on the failure's Data, under the key the metering decorator
    // uses for the same thing. With no failure in flight the disposal failure is the call's own and surfaces.
    private static async ValueTask DisposeAsync(IAsyncEnumerator<ChatResponseUpdate> updates, Exception? error)
    {
        try
        {
            await updates.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception disposeFailure) when (error is not null)
        {
            error.Data[MeteredChatClient.InnerDisposeFailureKey] = disposeFailure;
        }
    }

    private static async ValueTask DisposeQuietlyAsync(IAsyncEnumerator<ChatResponseUpdate> updates)
    {
        try
        {
            await updates.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Deliberately dropped: see the call site, the enumerator already failed with the 429 that is being retried.
        }
    }

    private static void Check(RetryOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxAttempts, 1, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.BaseDelay, TimeSpan.Zero, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxDelay, TimeSpan.Zero, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaxRetryAfter, TimeSpan.Zero, nameof(options));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.MaxRetryAfter, LargestConfigurableWait, nameof(options));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.MaxDelay, options.MaxRetryAfter, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaxTotalWait, TimeSpan.Zero, nameof(options));
        if (!(options.Factor >= 1.0) || !double.IsFinite(options.Factor))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The retry factor must be a finite number of at least 1.");
        }
    }
}