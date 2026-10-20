using System.ClientModel;
using System.Net;
using System.Text;

using AgentJoules.Core.Cli;
using AgentJoules.Core.Configuration;
using AgentJoules.Core.Cost;
using AgentJoules.Core.Energy;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Metering;
using AgentJoules.TestSupport;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace AgentJoules.UnitTests;

/// <summary>
/// AJ-045 (EDGE-006): HTTP 429 is retried with backoff, the wait stays out of the latency, and one reading spans every attempt. Every test runs the real
/// OpenAI SDK, the real Microsoft.Extensions.AI adapter and the real named <see cref="HttpClient"/> pipeline against a <see cref="FakeHttpMessageHandler"/>,
/// composed like the agent pipeline (<c>MeteredChatClient -> RateLimitRetryChatClient -> provider client</c>) over one ledger and one fake clock. The
/// clock moves only when the code under test starts a wait (<see cref="FakeTimeDriver.RunWaitsToCompletionAsync"/>) or when the fake endpoint advances it
/// for the duration of a call, so the elapsed fake time is exact and nothing waits on the wall clock.
/// </summary>
[Trait("Spec", "EDGE-006")]
public sealed class RetryPolicyTests
{
    private const int Seed = 12345;
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CallTime = TimeSpan.FromMilliseconds(10);
    private static readonly ChatMessage[] Hello = [new(ChatRole.User, "hello")];
    private static readonly Uri ChatUri = new("https://fake.test/v1/chat/completions");

    // The start of every FakeTimeProvider, so a test can script an HTTP date relative to the clock before the rig exists.
    private static readonly DateTimeOffset ClockStart = new FakeTimeProvider().GetUtcNow();

    // ----- the rig

    private sealed class CountingGate : IMeterGate
    {
        private readonly Lock _lock = new();

        public int Acquired { get; private set; }

        public int Released { get; private set; }

        public ValueTask<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                Acquired++;
            }

            return ValueTask.FromResult<IAsyncDisposable>(new Releaser(this));
        }

        private sealed class Releaser(CountingGate owner) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                lock (owner._lock)
                {
                    owner.Released++;
                }

                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class RecordingCost : ICostCalculator
    {
        public List<double> WallSeconds { get; } = [];

        public Metric Compute(StepIdentity identity, TokenCounts tokens, EnergyReading energy, double wallSeconds)
        {
            WallSeconds.Add(wallSeconds);
            return Metric.NotApplicable("recorded");
        }
    }

    private sealed class ConstantRandom(double value) : Random
    {
        public override double NextDouble() => value;
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly ProviderClientHost _host;
        private readonly UnmeteredEnergyMeter _meter;
        private readonly DateTimeOffset _start;

        private Rig(ProviderClientHost host, UnmeteredEnergyMeter meter, IChatClient client, FakeHttpMessageHandler handler, TimerTrackingTimeProvider time, RetryLedger ledger, InMemoryMetricsSink sink, CountingGate gate, RecordingCost cost)
        {
            _host = host;
            _meter = meter;
            Client = client;
            Handler = handler;
            Time = time;
            Ledger = ledger;
            Sink = sink;
            Gate = gate;
            Cost = cost;
            _start = time.GetUtcNow();
        }

        public IChatClient Client { get; }

        public TimerTrackingTimeProvider Time { get; }

        public FakeHttpMessageHandler Handler { get; }

        public RetryLedger Ledger { get; }

        public InMemoryMetricsSink Sink { get; }

        public CountingGate Gate { get; }

        public RecordingCost Cost { get; }

        public TimeSpan Elapsed => Time.GetUtcNow() - _start;

        public StepReading Reading => Assert.Single(Sink.Snapshot());

        public int Requests => Handler.Count(FakeRoute.ChatCompletions);

        public static async Task<Rig> CreateAsync(
            Action<FakeHttpMessageHandler>? script = null,
            TimeSpan? responseDelay = null,
            int? timeoutSeconds = null,
            RetryOptions? retry = null,
            Random? random = null,
            Func<IChatClient, IChatClient>? decorate = null,
            bool viaBuilder = false,
            bool adoptLedger = false)
        {
            var time = new TimerTrackingTimeProvider();
            var handler = new FakeHttpMessageHandler { Clock = time, DefaultDuration = CallTime, ResponseDelay = responseDelay ?? TimeSpan.Zero };
            script?.Invoke(handler);

            AgentJoulesOptions options = ProviderClientFixtures.FourTiers();
            if (timeoutSeconds is { } seconds)
            {
                options.Tiers["nvidia"].TimeoutSeconds = seconds;
            }

            ProviderClientHost host = ProviderClientFixtures.Build(options, handlers: new Dictionary<Tier, FakeHttpMessageHandler> { [Tier.Nvidia] = handler });
            var meter = new UnmeteredEnergyMeter(time);
            await meter.StartAsync(CancellationToken.None);

            var ledger = new RetryLedger();
            var sink = new InMemoryMetricsSink();
            var gate = new CountingGate();
            var cost = new RecordingCost();
            var services = new MeteringServices(meter, gate, sink, cost, TestInstruments.Create(), time);
            var identity = new StepIdentity("Planner", "model-nvidia", Tier.Nvidia, "summarise", 1, IsJudge: false);

            IChatClient provider = host.Factory.Create(Tier.Nvidia, "model-nvidia");
            if (decorate is not null)
            {
                provider = decorate(provider);
            }

            RetryOptions policy = retry ?? RetryOptions.Default;
            Random jitter = random ?? new Random(Seed);

            // The pipeline of an agent: the metered client outside, the retry layer inside it, and one ledger shared by the two.
            IChatClient client = viaBuilder
                ? new ChatClientBuilder(provider)
                    .UseMetering(identity, services, ledger)
                    .Use(inner => new RateLimitRetryChatClient(inner, ledger, policy, time, jitter))
                    .Build()
                : adoptLedger
                    ? new MeteredChatClient(new RateLimitRetryChatClient(provider, ledger, policy, time, jitter), identity, services)
                    : new MeteredChatClient(new RateLimitRetryChatClient(provider, ledger, policy, time, jitter), identity, services, ledger);

            return new Rig(host, meter, client, handler, time, ledger, sink, gate, cost);
        }

        public async Task<ChatResponse> CallAsync(CancellationToken cancellationToken = default)
        {
            Task<ChatResponse> call = Client.GetResponseAsync(Hello, null, cancellationToken);
            await FakeTimeDriver.RunWaitsToCompletionAsync(call, Time);
            return await call;
        }

        public async Task<string> StreamAsync(CancellationToken cancellationToken = default)
        {
            Task<string> run = ConsumeAsync(cancellationToken);
            await FakeTimeDriver.RunWaitsToCompletionAsync(run, Time);
            return await run;
        }

        public async Task<List<ChatResponseUpdate>> CollectAsync()
        {
            Task<List<ChatResponseUpdate>> run = CollectCoreAsync();
            await FakeTimeDriver.RunWaitsToCompletionAsync(run, Time);
            return await run;
        }

        private async Task<List<ChatResponseUpdate>> CollectCoreAsync()
        {
            List<ChatResponseUpdate> updates = [];
            await foreach (ChatResponseUpdate update in Client.GetStreamingResponseAsync(Hello))
            {
                updates.Add(update);
            }

            return updates;
        }

        public async Task<string> ConsumeAsync(CancellationToken cancellationToken)
        {
            StringBuilder text = new();
            await foreach (ChatResponseUpdate update in Client.GetStreamingResponseAsync(Hello, null, cancellationToken))
            {
                _ = text.Append(update.Text);
            }

            return text.ToString();
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _meter.DisposeAsync();
            _host.Dispose();
        }
    }

    // The waits the policy computes for retries 0, 1, 2, ... with a Random that is replayed from the same seed: full jitter over min(cap, 2^attempt) seconds.
    private static double[] ExpectedDelaysMs(Random random, int count, double capSeconds = 30.0)
    {
        double[] delays = new double[count];
        for (int attempt = 0; attempt < count; attempt++)
        {
            delays[attempt] = Math.Floor(random.NextDouble() * Math.Min(capSeconds, Math.Pow(2.0, attempt)) * 1000.0);
        }

        return delays;
    }

    private static void AssertDelay(double expectedMs, TimeSpan actual) => Assert.InRange(actual.TotalMilliseconds, expectedMs - 1.0, expectedMs + 1.0);

    // A 429 that takes 10 ms of fake time (so the window of the call has a length), with a raw Retry-After.
    private static FakeAnswer RateLimited(string? retryAfter) => new(429, retryAfter, CallTime);

    private static long SumMs(IEnumerable<TimeSpan> delays) => delays.Sum(delay => (long)delay.TotalMilliseconds);

    // ----- criteria 1 and 6: 429, 429, 200 is retried and one reading spans the attempts

    [Fact]
    public async Task Two_429s_then_success()
    {
        await using Rig rig = await Rig.CreateAsync(handler => handler.SetStatuses(FakeRoute.ChatCompletions, 429, 429, 200));

        ChatResponse response = await rig.CallAsync();

        StepReading reading = rig.Reading;
        Assert.Equal("answer", response.Text);
        Assert.Equal(2, reading.Retries);
        Assert.Equal(StepOutcomes.Ok, reading.Outcome);
        Assert.Equal(3, rig.Requests);
        Assert.Equal(2, rig.Time.History.Count);
        Assert.Equal(SumMs(rig.Time.History), reading.WaitMs);
    }

    [Fact]
    public async Task One_reading_spans_attempts()
    {
        await using Rig rig = await Rig.CreateAsync(handler => handler.SetStatuses(FakeRoute.ChatCompletions, 429, 429, 200));

        _ = await rig.CallAsync();

        // One reading, one gate hold and one window for three HTTP requests; the reading starts before the first attempt and ends after the last one.
        StepReading reading = rig.Reading;
        Assert.Equal(3, rig.Requests);
        Assert.Equal(1, rig.Gate.Acquired);
        Assert.Equal(1, rig.Gate.Released);
        Assert.Equal(rig.Elapsed, reading.EndUtc - reading.StartUtc);
        Assert.Equal(ClockStart, reading.StartUtc);
        Assert.Equal(StepOutcomes.Ok, reading.Outcome);
    }

    [Fact]
    public async Task Ledger_is_reset_for_every_call()
    {
        await using Rig rig = await Rig.CreateAsync(handler => handler.SetStatuses(FakeRoute.ChatCompletions, 429, 429, 200));

        _ = await rig.CallAsync();
        _ = await rig.CallAsync();

        IReadOnlyList<StepReading> readings = rig.Sink.Snapshot();
        Assert.Equal(2, readings.Count);
        Assert.Equal(2, readings[0].Retries);
        Assert.Equal(SumMs(rig.Time.History), readings[0].WaitMs);
        Assert.Equal(0, readings[1].Retries);
        Assert.Equal(0L, readings[1].WaitMs);
        Assert.Equal(CallTime.TotalMilliseconds, readings[1].LatencyMs.Value);
    }

    [Fact]
    public async Task The_ledger_is_shared_when_the_pipeline_is_built_with_the_builder()
    {
        await using Rig rig = await Rig.CreateAsync(handler => handler.SetStatuses(FakeRoute.ChatCompletions, 429, 200), viaBuilder: true);

        _ = await rig.CallAsync();

        StepReading reading = rig.Reading;
        Assert.Equal(1, reading.Retries);
        Assert.Equal(SumMs(rig.Time.History), reading.WaitMs);
        Assert.Equal(StepOutcomes.Ok, reading.Outcome);
    }

    // ----- criteria 2 and 3: the wait is out of the latency; Retry-After wins

    [Fact]
    public async Task Wait_is_summed_in_waitMs_and_latency_is_elapsed_minus_wait()
    {
        await using Rig rig = await Rig.CreateAsync(handler => handler.SetAnswers(
            FakeRoute.ChatCompletions,
            FakeAnswer.TooManyRequests("2"),
            FakeAnswer.Ok(TimeSpan.FromMilliseconds(100))));

        _ = await rig.CallAsync();

        StepReading reading = rig.Reading;
        Assert.Equal(2000L, reading.WaitMs);
        Assert.Equal(100.0, reading.LatencyMs.Value);
        Assert.Equal(TimeSpan.FromMilliseconds(2100), rig.Elapsed);
        Assert.Equal(rig.Elapsed.TotalMilliseconds - reading.WaitMs, reading.LatencyMs.Value);
        Assert.Equal([TimeSpan.FromSeconds(2)], rig.Time.History);
    }

    [Fact]
    public async Task Time_to_first_token_excludes_the_wait()
    {
        await using Rig rig = await Rig.CreateAsync(handler => handler.SetAnswers(
            FakeRoute.ChatCompletions,
            FakeAnswer.TooManyRequests("2"),
            FakeAnswer.Ok(TimeSpan.FromMilliseconds(100))));

        string text = await rig.StreamAsync();

        StepReading reading = rig.Reading;
        Assert.Equal("answer", text);
        Assert.Equal(2000L, reading.WaitMs);
        Assert.Equal(MetricLabel.Measured, reading.TtftMs.Label);
        Assert.Equal(100.0, reading.TtftMs.Value);
        Assert.Equal(100.0, reading.LatencyMs.Value);
    }

    [Fact]
    public async Task Cost_wall_seconds_are_the_full_elapsed_time_including_the_wait()
    {
        await using Rig rig = await Rig.CreateAsync(handler => handler.SetAnswers(
            FakeRoute.ChatCompletions,
            FakeAnswer.TooManyRequests("2"),
            FakeAnswer.Ok(TimeSpan.FromMilliseconds(100))));

        _ = await rig.CallAsync();

        Assert.Equal(100.0, rig.Reading.LatencyMs.Value);
        Assert.Equal(2.1, Assert.Single(rig.Cost.WallSeconds), precision: 6);
    }

    [Theory]
    [InlineData("3", 3000)]
    [InlineData(" 7 ", 7000)]
    [InlineData("0", 0)]
    [InlineData("31", 31000)]
    [InlineData("60", 60_000)]
    public async Task Retry_after_in_seconds_overrides_the_computed_backoff(string header, long expectedMs)
    {
        await using Rig rig = await Rig.CreateAsync(handler => handler.SetAnswers(FakeRoute.ChatCompletions, FakeAnswer.TooManyRequests(header), FakeAnswer.Ok()));

        _ = await rig.CallAsync();

        StepReading reading = rig.Reading;
        Assert.Equal(expectedMs, reading.WaitMs);
        Assert.Equal(1, reading.Retries);
        Assert.Equal(expectedMs == 0 ? 0 : 1, rig.Time.History.Count);
    }

    [Fact]
    public async Task Retry_after_as_an_http_date_is_relative_to_the_clock()
    {
        string date = FakeAnswer.HttpDate(ClockStart.AddSeconds(3));
        await using Rig rig = await Rig.CreateAsync(handler => handler.SetAnswers(FakeRoute.ChatCompletions, FakeAnswer.TooManyRequests(date), FakeAnswer.Ok()));
        Assert.Equal(ClockStart, rig.Time.GetUtcNow());

        _ = await rig.CallAsync();

        Assert.Equal(3000L, rig.Reading.WaitMs);
        Assert.Equal([TimeSpan.FromSeconds(3)], rig.Time.History);
    }

    [Theory]
    [InlineData("-5")]
    [InlineData("soon")]
    [InlineData("1.5")]
    [InlineData("2 seconds")]
    [InlineData("")]
    [InlineData("Tue, 99 Oct 2026 12:00:03 GMT")]
    public async Task A_negative_or_unusable_retry_after_falls_back_to_the_computed_delay(string header)
    {
        await using Rig rig = await Rig.CreateAsync(handler => handler.SetAnswers(FakeRoute.ChatCompletions, FakeAnswer.TooManyRequests(header), FakeAnswer.Ok()));

        _ = await rig.CallAsync();

        TimeSpan waited = Assert.Single(rig.Time.History);
        AssertDelay(ExpectedDelaysMs(new Random(Seed), 1)[0], waited);
        Assert.Equal(SumMs(rig.Time.History), rig.Reading.WaitMs);
    }

    [Fact]
    public async Task A_retry_after_date_in_the_past_falls_back_to_the_computed_delay()
    {
        string date = FakeAnswer.HttpDate(ClockStart.AddSeconds(-5));
        await using Rig rig = await Rig.CreateAsync(handler => handler.SetAnswers(FakeRoute.ChatCompletions, FakeAnswer.TooManyRequests(date), FakeAnswer.Ok()));

        _ = await rig.CallAsync();

        AssertDelay(ExpectedDelaysMs(new Random(Seed), 1)[0], Assert.Single(rig.Time.History));
    }

    [Fact]
    public async Task Computed_backoff_is_full_jitter_over_base_times_two_to_the_attempt()
    {
        await using Rig rig = await Rig.CreateAsync(handler => handler.SetStatuses(FakeRoute.ChatCompletions, 429));

        _ = await Assert.ThrowsAsync<RetriesExhaustedException>(() => rig.CallAsync());

        // Five attempts, four waits: the delays of retries 0 to 3 are random.NextDouble() x min(30, 2^attempt) seconds, replayed from the same seed.
        double[] expected = ExpectedDelaysMs(new Random(Seed), 4);
        IReadOnlyList<TimeSpan> waits = rig.Time.History;
        Assert.Equal(4, waits.Count);
        for (int attempt = 0; attempt < 4; attempt++)
        {
            AssertDelay(expected[attempt], waits[attempt]);
        }

        Assert.Equal(SumMs(waits), rig.Reading.WaitMs);
    }

    [Fact]
    public async Task Computed_backoff_is_capped_at_the_maximum_delay()
    {
        var retry = new RetryOptions { MaxAttempts = 12 };
        await using Rig rig = await Rig.CreateAsync(handler => handler.SetStatuses(FakeRoute.ChatCompletions, 429), retry: retry, random: new ConstantRandom(0.5));

        _ = await Assert.ThrowsAsync<RetriesExhaustedException>(() => rig.CallAsync());

        // 2^attempt seconds is 1, 2, 4, 8, 16, 32, ...; half of the ceiling, with the ceiling held at 30 s from attempt 5 on.
        double[] expectedSeconds = [0.5, 1, 2, 4, 8, 15, 15, 15, 15, 15, 15];
        TimeSpan[] waits = [.. rig.Time.History];
        Assert.Equal(11, waits.Length);
        for (int attempt = 0; attempt < waits.Length; attempt++)
        {
            AssertDelay(expectedSeconds[attempt] * 1000.0, waits[attempt]);
        }
    }

    [Fact]
    public async Task Options_change_the_base_the_factor_and_the_cap()
    {
        var retry = new RetryOptions { MaxAttempts = 4, BaseDelay = TimeSpan.FromSeconds(2), Factor = 3.0, MaxDelay = TimeSpan.FromSeconds(10) };
        await using Rig rig = await Rig.CreateAsync(handler => handler.SetStatuses(FakeRoute.ChatCompletions, 429), retry: retry, random: new ConstantRandom(1.0));

        _ = await Assert.ThrowsAsync<RetriesExhaustedException>(() => rig.CallAsync());

        // Ceilings 2 s, 6 s, 18 s capped to 10 s; with a jitter of 1.0 the wait is the ceiling.
        TimeSpan[] waits = [.. rig.Time.History];
        Assert.Equal(3, waits.Length);
        AssertDelay(2000.0, waits[0]);
        AssertDelay(6000.0, waits[1]);
        AssertDelay(10_000.0, waits[2]);
    }

    // ----- criterion 4: exhaustion

    [Fact]
    public async Task Always_429_exhausts_after_5_attempts()
    {
        await using Rig rig = await Rig.CreateAsync(handler => handler.SetStatuses(FakeRoute.ChatCompletions, 429));

        RetriesExhaustedException failure = await Assert.ThrowsAsync<RetriesExhaustedException>(() => rig.CallAsync());

        StepReading reading = rig.Reading;
        Assert.Equal(5, rig.Requests);
        Assert.Equal(StepOutcomes.Error, reading.Outcome);
        Assert.Equal(4, reading.Retries);
        Assert.Equal(SumMs(rig.Time.History), reading.WaitMs);
        Assert.Equal(ExitCode.Unreachable, failure.ExitCode);
        Assert.Equal(5, failure.Attempts);
        Assert.Equal(429, failure.StatusCode);
        Assert.Equal("HTTP 429 after 5 attempts", failure.Message);

        // Nothing sends a sixth request afterwards.
        rig.Time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(5, rig.Requests);
    }

    // The body of a rejected call may echo the credential (and a header may too); the exception of the retry layer must not carry any part of it.
    private static Action<FakeRoute, HttpResponseMessage> EchoesTheKey(string key) =>
        (route, response) =>
        {
            if (route != FakeRoute.ChatCompletions)
            {
                return;
            }

            response.Content = new StringContent("{\"error\":{\"message\":\"Incorrect API key provided: " + key + "\"}}", Encoding.UTF8, "application/json");
            _ = response.Headers.TryAddWithoutValidation("X-Echo", key);
        };

    // Everything an exception shows: message, the full text with its stack, the data, the inner exception and the numbers it carries. No 6-character
    // window of the key may be in it (a whole-key check would miss a truncated or masked echo).
    private static void AssertNoKeyIn(string key, RetriesExhaustedException failure)
    {
        Assert.Null(failure.InnerException);
        Assert.Empty(failure.Data);
        string shown = string.Join('\n', failure.Message, failure.ToString(), failure.Source, failure.HelpLink, failure.Attempts, failure.StatusCode, failure.StackTrace);
        for (int start = 0; start + 6 <= key.Length; start++)
        {
            string window = key.Substring(start, 6);
            Assert.DoesNotContain(window, shown, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("Incorrect", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("fake.test", shown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Positive_control_the_sdk_exception_message_does_carry_the_key()
    {
        string key = ProviderClientFixtures.FakeKey();

        ClientResultException sdk = await RateLimitFixtures.CaptureRateLimitAsync(customize: EchoesTheKey(key));

        // This is why the retry layer must not wrap or quote it: the provider's own exception shows the response body.
        Assert.Contains(key, sdk.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_exhaustion_exception_carries_no_provider_text()
    {
        string key = ProviderClientFixtures.FakeKey();
        await using Rig rig = await Rig.CreateAsync(handler =>
        {
            handler.SetStatuses(FakeRoute.ChatCompletions, 429);
            handler.CustomizeResponse = EchoesTheKey(key);
        });

        RetriesExhaustedException failure = await Assert.ThrowsAsync<RetriesExhaustedException>(() => rig.CallAsync());

        AssertNoKeyIn(key, failure);
    }

    [Fact]
    public async Task The_stream_exhaustion_exception_carries_no_provider_text()
    {
        string key = ProviderClientFixtures.FakeKey();
        await using Rig rig = await Rig.CreateAsync(handler =>
        {
            handler.SetStatuses(FakeRoute.ChatCompletions, 429);
            handler.CustomizeResponse = EchoesTheKey(key);
        });

        RetriesExhaustedException failure = await Assert.ThrowsAsync<RetriesExhaustedException>(() => rig.StreamAsync());

        AssertNoKeyIn(key, failure);
    }

    [Fact]
    public async Task A_single_attempt_policy_does_not_retry()
    {
        await using Rig rig = await Rig.CreateAsync(handler => handler.SetStatuses(FakeRoute.ChatCompletions, 429), retry: new RetryOptions { MaxAttempts = 1 });

        RetriesExhaustedException failure = await Assert.ThrowsAsync<RetriesExhaustedException>(() => rig.CallAsync());

        Assert.Equal(1, rig.Requests);
        Assert.Equal(0, rig.Reading.Retries);
        Assert.Equal(0L, rig.Reading.WaitMs);
        Assert.Equal("HTTP 429 after 1 attempt", failure.Message);
        Assert.Empty(rig.Time.History);
    }

    [Fact]
    public void Exhaustion_is_an_error_outcome_on_every_tier()
    {
        var failure = new RetriesExhaustedException(5, 429);

        Assert.Equal(StepOutcomes.Error, OutcomeClassifier.Classify(failure, Tier.Nvidia, firstUpdateSeen: false, CancellationToken.None));
        Assert.Equal(StepOutcomes.Error, OutcomeClassifier.Classify(failure, Tier.CloudGpu, firstUpdateSeen: false, CancellationToken.None));
        Assert.Equal(StepOutcomes.Error, OutcomeClassifier.Classify(failure, Tier.Nebius, firstUpdateSeen: true, CancellationToken.None));
    }

    // ----- criterion 5: only 429 is retried

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    public async Task A_response_other_than_429_is_attempted_once(int status)
    {
        await using Rig rig = await Rig.CreateAsync(handler => handler.SetStatuses(FakeRoute.ChatCompletions, status));

        ClientResultException failure = await Assert.ThrowsAsync<ClientResultException>(() => rig.CallAsync());

        StepReading reading = rig.Reading;
        Assert.Equal(status, failure.Status);
        Assert.Equal(1, rig.Requests);
        Assert.Equal(StepOutcomes.Error, reading.Outcome);
        Assert.Equal(0, reading.Retries);
        Assert.Equal(0L, reading.WaitMs);
        Assert.Empty(rig.Time.History);
    }

    [Fact]
    public async Task A_500_after_a_429_ends_the_call_without_further_retries()
    {
        await using Rig rig = await Rig.CreateAsync(handler => handler.SetStatuses(FakeRoute.ChatCompletions, 429, 500));

        ClientResultException failure = await Assert.ThrowsAsync<ClientResultException>(() => rig.CallAsync());

        StepReading reading = rig.Reading;
        Assert.Equal(500, failure.Status);
        Assert.Equal(2, rig.Requests);
        Assert.Equal(1, reading.Retries);
        Assert.Equal(StepOutcomes.Error, reading.Outcome);
    }

    [Fact]
    public async Task A_connection_error_is_attempted_once()
    {
        await using Rig rig = await Rig.CreateAsync(handler => handler.ConnectionRefused = true);

        // The SDK wraps a transport failure in a ClientResultException with no status (0); only a status of 429 is retried.
        ClientResultException failure = await Assert.ThrowsAsync<ClientResultException>(() => rig.CallAsync());

        StepReading reading = rig.Reading;
        Assert.Equal(0, failure.Status);
        _ = Assert.IsType<HttpRequestException>(failure.InnerException);
        Assert.Equal(1, rig.Requests);
        Assert.Equal(StepOutcomes.Error, reading.Outcome);
        Assert.Equal(0, reading.Retries);
        Assert.Empty(rig.Time.History);
    }

    [Fact]
    public async Task A_timeout_is_attempted_once()
    {
        // This test deliberately waits about 1 s on the wall clock: the SDK network timeout is a real timer and cannot run on the fake clock.
        await using Rig rig = await Rig.CreateAsync(timeoutSeconds: 1, responseDelay: TimeSpan.FromSeconds(20));

        // The only thing that ends the call is the network timeout the factory gave the SDK; it is a cancellation the caller did not ask for.
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.CallAsync().WaitAsync(HangGuard));

        StepReading reading = rig.Reading;
        Assert.Equal(1, rig.Requests);
        Assert.Equal(StepOutcomes.Error, reading.Outcome);
        Assert.Equal(0, reading.Retries);
        Assert.Empty(rig.Time.History);
    }

    // ----- cancellation during the wait

    [Fact]
    public async Task Cancellation_during_the_wait_ends_the_call_as_cancelled_and_records_the_partial_wait()
    {
        await using Rig rig = await Rig.CreateAsync(handler => handler.SetAnswers(FakeRoute.ChatCompletions, RateLimited("2")));
        using var cancellation = new CancellationTokenSource();

        Task<ChatResponse> call = rig.Client.GetResponseAsync(Hello, null, cancellation.Token);
        _ = await rig.Time.DueTimes.WaitToReadAsync().AsTask().WaitAsync(HangGuard);
        rig.Time.Advance(TimeSpan.FromMilliseconds(750));
        await cancellation.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(HangGuard));

        // The retry did not happen, but the 750 ms were waited: latency is elapsed (10 ms of call, 750 ms of wait) minus the wait, for a cancelled call too.
        StepReading reading = rig.Reading;
        Assert.Equal(1, rig.Requests);
        Assert.Equal(StepOutcomes.Cancelled, reading.Outcome);
        Assert.Equal(0, reading.Retries);
        Assert.Equal(750L, reading.WaitMs);
        Assert.Equal(CallTime.TotalMilliseconds, reading.LatencyMs.Value);
        Assert.Equal(CallTime + TimeSpan.FromMilliseconds(750), rig.Elapsed);
        Assert.Equal(1, rig.Gate.Released);
    }

    [Fact]
    public async Task Cancellation_during_a_stream_wait_ends_the_call_as_cancelled_and_records_the_partial_wait()
    {
        await using Rig rig = await Rig.CreateAsync(handler => handler.SetAnswers(FakeRoute.ChatCompletions, RateLimited("2")));
        using var cancellation = new CancellationTokenSource();

        Task<string> run = rig.ConsumeAsync(cancellation.Token);
        _ = await rig.Time.DueTimes.WaitToReadAsync().AsTask().WaitAsync(HangGuard);
        rig.Time.Advance(TimeSpan.FromMilliseconds(750));
        await cancellation.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(HangGuard));

        StepReading reading = rig.Reading;
        Assert.Equal(1, rig.Requests);
        Assert.Equal(StepOutcomes.Cancelled, reading.Outcome);
        Assert.Equal(0, reading.Retries);
        Assert.Equal(750L, reading.WaitMs);
        Assert.Equal(CallTime.TotalMilliseconds, reading.LatencyMs.Value);
    }

    // ----- criterion 6: streaming

    [Fact]
    public async Task Stream_429_before_first_update_retried()
    {
        await using Rig rig = await Rig.CreateAsync(handler => handler.SetAnswers(FakeRoute.ChatCompletions, FakeAnswer.TooManyRequests(), FakeAnswer.Ok()));

        string text = await rig.StreamAsync();

        StepReading reading = rig.Reading;
        Assert.Equal("answer", text);
        Assert.Equal(2, rig.Requests);
        Assert.Equal(1, reading.Retries);
        Assert.Equal(StepOutcomes.Ok, reading.Outcome);
        Assert.All(rig.Handler.RequestsFor(FakeRoute.ChatCompletions), request => Assert.True(request.Json!["stream"]!.GetValue<bool>()));
        Assert.Equal(1, rig.Gate.Acquired);
    }

    [Fact]
    public async Task Stream_that_always_answers_429_exhausts_after_5_attempts()
    {
        await using Rig rig = await Rig.CreateAsync(handler => handler.SetStatuses(FakeRoute.ChatCompletions, 429));

        RetriesExhaustedException failure = await Assert.ThrowsAsync<RetriesExhaustedException>(() => rig.StreamAsync());

        StepReading reading = rig.Reading;
        Assert.Equal(5, rig.Requests);
        Assert.Equal(4, reading.Retries);
        Assert.Equal(StepOutcomes.Error, reading.Outcome);
        Assert.Equal("HTTP 429 after 5 attempts", failure.Message);
    }

    [Fact]
    public async Task Stream_429_after_the_first_update_is_not_retried()
    {
        ClientResultException rateLimit = await RateLimitFixtures.CaptureRateLimitAsync();
        await using Rig rig = await Rig.CreateAsync(decorate: provider => new FailAfterUpdatesChatClient(provider, updates: 1, rateLimit));

        ClientResultException failure = await Assert.ThrowsAsync<ClientResultException>(() => rig.StreamAsync());

        StepReading reading = rig.Reading;
        Assert.Same(rateLimit, failure);
        Assert.Equal(429, failure.Status);
        Assert.Equal(1, rig.Requests);
        Assert.Equal(StepOutcomes.Error, reading.Outcome);
        Assert.Equal(0, reading.Retries);
        Assert.Empty(rig.Time.History);
    }

    [Fact]
    public async Task Stream_transport_failure_after_the_first_update_is_not_retried()
    {
        await using Rig rig = await Rig.CreateAsync(handler => handler.SetAnswers(FakeRoute.ChatCompletions, FakeAnswer.StreamFailure(deltas: 1)));

        _ = await Assert.ThrowsAsync<IOException>(() => rig.StreamAsync());

        StepReading reading = rig.Reading;
        Assert.Equal(1, rig.Requests);

        // A cut stream after an update is EDGE-016's incomplete, not an error, and it is not a reason to retry.
        Assert.Equal(StepOutcomes.Incomplete, reading.Outcome);
        Assert.Equal(0, reading.Retries);
    }

    [Fact]
    public async Task Real_sdk_raises_a_stream_429_on_the_first_MoveNextAsync_and_keeps_the_retry_after_header()
    {
        var handler = new FakeHttpMessageHandler();
        handler.SetAnswers(FakeRoute.ChatCompletions, FakeAnswer.TooManyRequests("7"));
        using ProviderClientHost host = ProviderClientFixtures.Build(ProviderClientFixtures.FourTiers(), handlers: new Dictionary<Tier, FakeHttpMessageHandler> { [Tier.Nvidia] = handler });
        using IChatClient client = host.Factory.Create(Tier.Nvidia, "model-nvidia");

        IAsyncEnumerable<ChatResponseUpdate> updates = client.GetStreamingResponseAsync(Hello);
        await using IAsyncEnumerator<ChatResponseUpdate> enumerator = updates.GetAsyncEnumerator();

        // Nothing is sent until the first MoveNextAsync; the 429 surfaces there, as a ClientResultException with the raw response and its header.
        Assert.Equal(0, handler.RequestCount);
        ClientResultException failure = await Assert.ThrowsAsync<ClientResultException>(async () => await enumerator.MoveNextAsync().AsTask().WaitAsync(HangGuard));

        Assert.Equal(429, failure.Status);
        Assert.Equal(1, handler.RequestCount);
        Assert.True(failure.GetRawResponse()!.Headers.TryGetValue("Retry-After", out string? value));
        Assert.Equal("7", value);
    }

    [Fact]
    public async Task A_stream_that_ends_without_an_update_completes_without_a_retry()
    {
        var time = new FakeTimeProvider();
        using var inner = new FakeChatClient(time);
        using var retry = new RateLimitRetryChatClient(inner, new RetryLedger(), RetryOptions.Default, time, new Random(Seed));
        int updates = 0;

        await foreach (ChatResponseUpdate update in retry.GetStreamingResponseAsync(Hello))
        {
            Assert.NotNull(update);
            updates++;
        }

        Assert.Equal(0, updates);
        _ = Assert.Single(inner.Calls);
    }

    // ----- the ceiling and the budget of the wait

    [Theory]
    [InlineData("61")]
    [InlineData("86400")]
    [InlineData("99999999999999999999")]
    public async Task A_retry_after_above_the_ceiling_fails_fast_without_waiting(string header)
    {
        await using Rig rig = await Rig.CreateAsync(handler => handler.SetAnswers(FakeRoute.ChatCompletions, RateLimited(header)));

        RetriesExhaustedException failure = await Assert.ThrowsAsync<RetriesExhaustedException>(() => rig.CallAsync());

        // No wait and no fall back to the jitter: the server said it will not take the request before a time the call is not willing to wait for.
        StepReading reading = rig.Reading;
        Assert.Equal(1, failure.Attempts);
        Assert.Equal(429, failure.StatusCode);
        Assert.Equal(1, rig.Requests);
        Assert.Equal(StepOutcomes.Error, reading.Outcome);
        Assert.Equal(0, reading.Retries);
        Assert.Equal(0L, reading.WaitMs);
        Assert.Empty(rig.Time.History);
        Assert.Equal(CallTime, rig.Elapsed);
    }

    [Fact]
    public async Task A_stream_retry_after_above_the_ceiling_fails_fast_without_waiting()
    {
        await using Rig rig = await Rig.CreateAsync(handler => handler.SetAnswers(FakeRoute.ChatCompletions, RateLimited("61")));

        RetriesExhaustedException failure = await Assert.ThrowsAsync<RetriesExhaustedException>(() => rig.StreamAsync());

        Assert.Equal(1, failure.Attempts);
        Assert.Equal(1, rig.Requests);
        Assert.Equal(0L, rig.Reading.WaitMs);
        Assert.Empty(rig.Time.History);
    }

    [Fact]
    public async Task A_retry_after_date_at_the_ceiling_is_honoured_and_one_beyond_it_fails_fast()
    {
        await using Rig honoured = await Rig.CreateAsync(handler => handler.SetAnswers(FakeRoute.ChatCompletions, FakeAnswer.TooManyRequests(FakeAnswer.HttpDate(ClockStart.AddSeconds(60))), FakeAnswer.Ok()));
        await using Rig beyond = await Rig.CreateAsync(handler => handler.SetAnswers(FakeRoute.ChatCompletions, FakeAnswer.TooManyRequests(FakeAnswer.HttpDate(ClockStart.AddSeconds(61)))));

        _ = await honoured.CallAsync();
        RetriesExhaustedException failure = await Assert.ThrowsAsync<RetriesExhaustedException>(() => beyond.CallAsync());

        Assert.Equal(60_000L, honoured.Reading.WaitMs);
        Assert.Equal(1, failure.Attempts);
        Assert.Empty(beyond.Time.History);
    }

    [Fact]
    public async Task The_total_wait_of_a_call_is_budgeted_and_the_call_fails_fast_when_the_next_wait_would_exceed_it()
    {
        await using Rig rig = await Rig.CreateAsync(handler => handler.SetAnswers(FakeRoute.ChatCompletions, RateLimited("40")));

        RetriesExhaustedException failure = await Assert.ThrowsAsync<RetriesExhaustedException>(() => rig.CallAsync());

        // Three waits of 40 s fit the 120 s budget exactly; the fourth rejection would need a fourth, so it ends the call at once.
        StepReading reading = rig.Reading;
        Assert.Equal(4, failure.Attempts);
        Assert.Equal(4, rig.Requests);
        Assert.Equal(3, reading.Retries);
        Assert.Equal(120_000L, reading.WaitMs);
        Assert.Equal([TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(40)], rig.Time.History);
        Assert.Equal(StepOutcomes.Error, reading.Outcome);
    }

    [Fact]
    public async Task The_total_wait_budget_also_bounds_the_computed_backoff()
    {
        var retry = new RetryOptions { MaxTotalWait = TimeSpan.FromSeconds(1) };
        await using Rig rig = await Rig.CreateAsync(handler => handler.SetStatuses(FakeRoute.ChatCompletions, 429), retry: retry, random: new ConstantRandom(1.0));

        RetriesExhaustedException failure = await Assert.ThrowsAsync<RetriesExhaustedException>(() => rig.CallAsync());

        // A wait of 1 s fits the budget, the next of 2 s does not.
        Assert.Equal(2, failure.Attempts);
        Assert.Equal(2, rig.Requests);
        Assert.Equal(1000L, rig.Reading.WaitMs);
    }

    [Fact]
    public async Task The_budget_is_per_call_not_per_ledger()
    {
        var retry = new RetryOptions { MaxRetryAfter = TimeSpan.FromSeconds(100), MaxTotalWait = TimeSpan.FromSeconds(120) };
        await using Rig rig = await Rig.CreateAsync(handler => handler.SetAnswers(FakeRoute.ChatCompletions, FakeAnswer.TooManyRequests("100"), FakeAnswer.Ok(), FakeAnswer.TooManyRequests("100"), FakeAnswer.Ok()), retry: retry);

        _ = await rig.CallAsync();
        _ = await rig.CallAsync();

        // Two waits of 100 s are more than the 120 s budget; each call has a budget of its own, so both pass.
        IReadOnlyList<StepReading> readings = rig.Sink.Snapshot();
        Assert.Equal(100_000L, readings[0].WaitMs);
        Assert.Equal(100_000L, readings[1].WaitMs);
    }

    [Theory]
    [InlineData("Sat, 01 Jan 2000 00:00:03 GMT")]
    [InlineData("Saturday, 01-Jan-00 00:00:03 GMT")]
    [InlineData("Sat Jan 1 00:00:03 2000")]
    [InlineData("Sat Jan  1 00:00:03 2000")]
    [InlineData("  Sat, 01 Jan 2000 00:00:03 GMT  ")]
    public async Task Retry_after_accepts_the_three_http_date_formats(string date)
    {
        await using Rig rig = await Rig.CreateAsync(handler => handler.SetAnswers(FakeRoute.ChatCompletions, FakeAnswer.TooManyRequests(date), FakeAnswer.Ok()));
        Assert.Equal(ClockStart, rig.Time.GetUtcNow());

        _ = await rig.CallAsync();

        Assert.Equal(3000L, rig.Reading.WaitMs);
    }

    // ----- the ledger guard and the accuracy of the ledger

    [Fact]
    public void The_retry_layer_offers_its_ledger_as_a_service()
    {
        var time = new FakeTimeProvider();
        using var inner = new FakeChatClient(time);
        var ledger = new RetryLedger();
        using var retry = new RateLimitRetryChatClient(inner, ledger, RetryOptions.Default, time, new Random(Seed));

        Assert.Same(ledger, retry.GetService(typeof(RetryLedger)));
        Assert.Null(retry.GetService(typeof(RetryLedger), "key"));
        Assert.Null(retry.GetService(typeof(string)));
        Assert.Same(retry, retry.GetService(typeof(RateLimitRetryChatClient)));
        Assert.Same(inner, retry.GetService(typeof(FakeChatClient)));
    }

    [Fact]
    public void A_metered_client_refuses_a_ledger_that_is_not_the_one_of_its_retry_layer()
    {
        var time = new FakeTimeProvider();
        using var inner = new FakeChatClient(time);
        var ledger = new RetryLedger();
        using var retry = new RateLimitRetryChatClient(inner, ledger, RetryOptions.Default, time, new Random(Seed));
        var identity = new StepIdentity("Planner", "model-a", Tier.Local, "summarise", 1, IsJudge: false);
        var services = new MeteringServices(new UnmeteredEnergyMeter(time), NoOpMeterGate.Instance, new InMemoryMetricsSink(), NotConfiguredCostCalculator.Instance, TestInstruments.Create(), time);

        ArgumentException wrong = Assert.Throws<ArgumentException>(() => new MeteredChatClient(retry, identity, services, new RetryLedger()));
        using var same = new MeteredChatClient(retry, identity, services, ledger);
        using var plain = new MeteredChatClient(inner, identity, services, new RetryLedger());

        Assert.Equal("ledger", wrong.ParamName);
        Assert.NotNull(same);
        Assert.NotNull(plain);
    }

    [Fact]
    public void The_builder_refuses_a_ledger_that_is_not_the_one_of_the_retry_layer_below_it()
    {
        var time = new FakeTimeProvider();
        using var inner = new FakeChatClient(time);
        var identity = new StepIdentity("Planner", "model-a", Tier.Local, "summarise", 1, IsJudge: false);
        var services = new MeteringServices(new UnmeteredEnergyMeter(time), NoOpMeterGate.Instance, new InMemoryMetricsSink(), NotConfiguredCostCalculator.Instance, TestInstruments.Create(), time);
        var retryLedger = new RetryLedger();
        ChatClientBuilder mismatched = new ChatClientBuilder(inner)
            .UseMetering(identity, services, new RetryLedger())
            .Use(next => new RateLimitRetryChatClient(next, retryLedger, RetryOptions.Default, time, new Random(Seed)));
        ChatClientBuilder matched = new ChatClientBuilder(inner)
            .UseMetering(identity, services, retryLedger)
            .Use(next => new RateLimitRetryChatClient(next, retryLedger, RetryOptions.Default, time, new Random(Seed)));

        _ = Assert.Throws<ArgumentException>(() => mismatched.Build());
        using IChatClient built = matched.Build();
        Assert.NotNull(built);
    }

    [Fact]
    public async Task A_metered_client_without_a_ledger_adopts_the_one_of_its_retry_layer()
    {
        await using Rig rig = await Rig.CreateAsync(handler => handler.SetStatuses(FakeRoute.ChatCompletions, 429, 200), adoptLedger: true);

        _ = await rig.CallAsync();

        StepReading reading = rig.Reading;
        Assert.Equal(1, reading.Retries);
        Assert.Equal(SumMs(rig.Time.History), reading.WaitMs);
        // Two attempts of 10 ms each; the wait is not in the latency, which it would be if the ledger had not been adopted.
        Assert.Equal(2 * CallTime.TotalMilliseconds, reading.LatencyMs.Value);
    }

    [Fact]
    public async Task A_metered_client_without_a_retry_layer_has_a_ledger_of_its_own_and_reports_no_retries()
    {
        var time = new FakeTimeProvider();
        using var inner = new FakeChatClient(time);
        var meter = new UnmeteredEnergyMeter(time);
        await meter.StartAsync(CancellationToken.None);
        var sink = new InMemoryMetricsSink();
        var identity = new StepIdentity("Planner", "model-a", Tier.Nvidia, "summarise", 1, IsJudge: false);
        using var metered = new MeteredChatClient(inner, identity, new MeteringServices(meter, NoOpMeterGate.Instance, sink, NotConfiguredCostCalculator.Instance, TestInstruments.Create(), time));

        _ = await metered.GetResponseAsync(Hello);

        StepReading reading = Assert.Single(sink.Snapshot());
        Assert.Equal(0, reading.Retries);
        Assert.Equal(0L, reading.WaitMs);
    }

    [Fact]
    public void Ledger_adds_a_partial_wait_without_counting_a_retry()
    {
        var ledger = new RetryLedger();

        ledger.AddWait(TimeSpan.FromMilliseconds(750));
        ledger.Record(TimeSpan.FromMilliseconds(250));

        Assert.Equal(1, ledger.Retries);
        Assert.Equal(1000L, ledger.WaitMs);
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => ledger.AddWait(TimeSpan.FromMilliseconds(-1)));
    }

    [Fact]
    public async Task A_retried_stream_yields_each_update_exactly_once()
    {
        await using Rig clean = await Rig.CreateAsync();
        await using Rig retried = await Rig.CreateAsync(handler => handler.SetAnswers(FakeRoute.ChatCompletions, FakeAnswer.TooManyRequests(), FakeAnswer.Ok()));

        List<ChatResponseUpdate> expected = await clean.CollectAsync();
        List<ChatResponseUpdate> actual = await retried.CollectAsync();

        Assert.Equal(2, retried.Requests);
        Assert.Equal(expected.Count, actual.Count);
        Assert.Equal(expected.Select(update => update.Text), actual.Select(update => update.Text));
        Assert.Equal(["ans", "wer"], actual.Where(update => update.Text.Length > 0).Select(update => update.Text));
    }

    // ----- the pieces: ledger, options, constructor checks, the fake endpoint

    [Fact]
    public void Ledger_sums_retries_and_whole_milliseconds_and_resets()
    {
        var ledger = new RetryLedger();

        ledger.Record(TimeSpan.FromMilliseconds(1500));
        ledger.Record(TimeSpan.FromSeconds(2));
        ledger.Record(TimeSpan.Zero);

        Assert.Equal(3, ledger.Retries);
        Assert.Equal(3500L, ledger.WaitMs);

        ledger.Reset();

        Assert.Equal(0, ledger.Retries);
        Assert.Equal(0L, ledger.WaitMs);
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => ledger.Record(TimeSpan.FromMilliseconds(-1)));
        Assert.Equal(0, ledger.Retries);
    }

    [Fact]
    public void Retry_options_default_to_the_designed_policy()
    {
        RetryOptions options = RetryOptions.Default;

        Assert.Equal(5, options.MaxAttempts);
        Assert.Equal(TimeSpan.FromSeconds(1), options.BaseDelay);
        Assert.Equal(2.0, options.Factor);
        Assert.Equal(TimeSpan.FromSeconds(30), options.MaxDelay);
        Assert.Equal(TimeSpan.FromSeconds(60), options.MaxRetryAfter);
        Assert.Equal(TimeSpan.FromSeconds(120), options.MaxTotalWait);
    }

    [Fact]
    public void The_retry_client_checks_its_arguments_and_its_options()
    {
        var time = new FakeTimeProvider();
        using var inner = new FakeChatClient(time);
        var ledger = new RetryLedger();
        var random = new Random(Seed);

        _ = Assert.Throws<ArgumentNullException>(() => new RateLimitRetryChatClient(null!, ledger, RetryOptions.Default, time, random));
        _ = Assert.Throws<ArgumentNullException>(() => new RateLimitRetryChatClient(inner, null!, RetryOptions.Default, time, random));
        _ = Assert.Throws<ArgumentNullException>(() => new RateLimitRetryChatClient(inner, ledger, null!, time, random));
        _ = Assert.Throws<ArgumentNullException>(() => new RateLimitRetryChatClient(inner, ledger, RetryOptions.Default, null!, random));
        _ = Assert.Throws<ArgumentNullException>(() => new RateLimitRetryChatClient(inner, ledger, RetryOptions.Default, time, null!));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new RateLimitRetryChatClient(inner, ledger, new RetryOptions { MaxAttempts = 0 }, time, random));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new RateLimitRetryChatClient(inner, ledger, new RetryOptions { Factor = 0.5 }, time, random));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new RateLimitRetryChatClient(inner, ledger, new RetryOptions { Factor = double.NaN }, time, random));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new RateLimitRetryChatClient(inner, ledger, new RetryOptions { BaseDelay = TimeSpan.FromSeconds(-1) }, time, random));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new RateLimitRetryChatClient(inner, ledger, new RetryOptions { MaxDelay = TimeSpan.FromSeconds(-1) }, time, random));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new RateLimitRetryChatClient(inner, ledger, new RetryOptions { Factor = double.PositiveInfinity }, time, random));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new RateLimitRetryChatClient(inner, ledger, new RetryOptions { MaxDelay = TimeSpan.FromSeconds(61) }, time, random));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new RateLimitRetryChatClient(inner, ledger, new RetryOptions { MaxRetryAfter = TimeSpan.Zero }, time, random));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new RateLimitRetryChatClient(inner, ledger, new RetryOptions { MaxRetryAfter = TimeSpan.FromSeconds(-1) }, time, random));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new RateLimitRetryChatClient(inner, ledger, new RetryOptions { MaxTotalWait = TimeSpan.Zero }, time, random));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new RateLimitRetryChatClient(inner, ledger, new RetryOptions { MaxTotalWait = TimeSpan.FromSeconds(-1) }, time, random));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new RetriesExhaustedException(5, 99));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new RetriesExhaustedException(5, 600));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new RetriesExhaustedException(0, 429));
    }

    [Fact]
    public void The_metered_client_checks_its_ledger()
    {
        var time = new FakeTimeProvider();
        using var inner = new FakeChatClient(time);
        var identity = new StepIdentity("Planner", "model-a", Tier.Local, "summarise", 1, IsJudge: false);
        var services = new MeteringServices(new UnmeteredEnergyMeter(time), NoOpMeterGate.Instance, new InMemoryMetricsSink(), NotConfiguredCostCalculator.Instance, TestInstruments.Create(), time);

        _ = Assert.Throws<ArgumentNullException>(() => new MeteredChatClient(inner, identity, services, null!));
        _ = Assert.Throws<ArgumentNullException>(() => new ChatClientBuilder(inner).UseMetering(identity, services, null!));
    }

    [Fact]
    public async Task Fake_endpoint_scripts_answers_in_order_with_the_last_one_repeating_and_the_retry_after_header_verbatim()
    {
        var handler = new FakeHttpMessageHandler();
        handler.SetAnswers(FakeRoute.ChatCompletions, FakeAnswer.TooManyRequests("not a number"), FakeAnswer.Ok());
        using var invoker = new HttpMessageInvoker(handler, disposeHandler: false);

        using HttpResponseMessage first = await invoker.SendAsync(Chat(), CancellationToken.None).WaitAsync(HangGuard);
        using HttpResponseMessage second = await invoker.SendAsync(Chat(), CancellationToken.None).WaitAsync(HangGuard);
        using HttpResponseMessage third = await invoker.SendAsync(Chat(), CancellationToken.None).WaitAsync(HangGuard);

        Assert.Equal(HttpStatusCode.TooManyRequests, first.StatusCode);
        Assert.Equal(["not a number"], first.Headers.GetValues("Retry-After"));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.False(second.Headers.Contains("Retry-After"));
        Assert.Equal(HttpStatusCode.OK, third.StatusCode);
        Assert.Equal(3, handler.Count(FakeRoute.ChatCompletions));
    }

    [Fact]
    public void Fake_endpoint_formats_an_http_date_the_way_a_server_does()
    {
        Assert.Equal("Tue, 20 Oct 2026 12:00:03 GMT", FakeAnswer.HttpDate(new DateTimeOffset(2026, 10, 20, 14, 0, 3, TimeSpan.FromHours(2))));
    }

    [Fact]
    public async Task Fake_endpoint_moves_the_fake_clock_by_the_scripted_duration_before_it_answers()
    {
        var clock = new FakeTimeProvider();
        var handler = new FakeHttpMessageHandler { Clock = clock, DefaultDuration = TimeSpan.FromMilliseconds(30) };
        handler.SetAnswers(FakeRoute.ChatCompletions, FakeAnswer.Ok(TimeSpan.FromMilliseconds(100)), FakeAnswer.TooManyRequests(), new FakeAnswer(500));
        using var invoker = new HttpMessageInvoker(handler, disposeHandler: false);
        DateTimeOffset start = clock.GetUtcNow();

        using HttpResponseMessage first = await invoker.SendAsync(Chat(), CancellationToken.None).WaitAsync(HangGuard);
        TimeSpan afterFirst = clock.GetUtcNow() - start;
        using HttpResponseMessage second = await invoker.SendAsync(Chat(), CancellationToken.None).WaitAsync(HangGuard);
        TimeSpan afterSecond = clock.GetUtcNow() - start;
        using HttpResponseMessage third = await invoker.SendAsync(Chat(), CancellationToken.None).WaitAsync(HangGuard);
        TimeSpan afterThird = clock.GetUtcNow() - start;

        // A scripted duration wins, an explicit zero is zero, and an answer without one takes the default of the endpoint.
        Assert.Equal(TimeSpan.FromMilliseconds(100), afterFirst);
        Assert.Equal(TimeSpan.FromMilliseconds(100), afterSecond);
        Assert.Equal(TimeSpan.FromMilliseconds(130), afterThird);
    }

    [Fact]
    public async Task Fake_endpoint_applies_the_default_duration_to_a_refused_connection()
    {
        var clock = new FakeTimeProvider();
        var handler = new FakeHttpMessageHandler { Clock = clock, DefaultDuration = TimeSpan.FromMilliseconds(30), ConnectionRefused = true };
        using var invoker = new HttpMessageInvoker(handler, disposeHandler: false);
        DateTimeOffset start = clock.GetUtcNow();

        _ = await Assert.ThrowsAsync<HttpRequestException>(() => invoker.SendAsync(Chat(), CancellationToken.None).WaitAsync(HangGuard));

        Assert.Equal(TimeSpan.FromMilliseconds(30), clock.GetUtcNow() - start);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Fake_endpoint_refuses_a_duration_without_a_clock()
    {
        var handler = new FakeHttpMessageHandler();
        handler.SetAnswers(FakeRoute.ChatCompletions, FakeAnswer.Ok(TimeSpan.FromMilliseconds(5)));
        using var invoker = new HttpMessageInvoker(handler, disposeHandler: false);

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => invoker.SendAsync(Chat(), CancellationToken.None).WaitAsync(HangGuard));
    }

    [Fact]
    public async Task Fake_endpoint_cuts_a_stream_after_the_scripted_chunks_with_an_io_failure_on_the_body_read()
    {
        var handler = new FakeHttpMessageHandler { StreamDeltas = ["ans", "wer"] };
        handler.SetAnswers(FakeRoute.ChatCompletions, FakeAnswer.StreamFailure(deltas: 1));
        using var invoker = new HttpMessageInvoker(handler, disposeHandler: false);
        using HttpResponseMessage response = await invoker.SendAsync(Chat(stream: true), CancellationToken.None).WaitAsync(HangGuard);
        await using Stream body = await response.Content.ReadAsStreamAsync().WaitAsync(HangGuard);
        using var reader = new StreamReader(body, Encoding.UTF8);
        List<string> lines = [];

        _ = await Assert.ThrowsAsync<IOException>(async () =>
        {
            while (await reader.ReadLineAsync().WaitAsync(HangGuard) is { } line)
            {
                lines.Add(line);
            }
        });

        // The role chunk and the first delta arrived, the second delta and the terminator did not.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(lines, line => line.Contains("\"ans\"", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("\"wer\"", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("[DONE]", StringComparison.Ordinal));
    }

    private static HttpRequestMessage Chat(bool stream = false) =>
        new(HttpMethod.Post, ChatUri)
        {
            Content = new StringContent(stream ? """{"model":"m","stream":true}""" : """{"model":"m"}""", Encoding.UTF8, "application/json"),
        };
}