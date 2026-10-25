using System.Text.Json;

using AgentJoules.TestSupport;

using Xunit;

namespace AgentJoules.UnitTests;

public sealed partial class AttributionBaselineTests
{
    /// <summary>
    /// The settle interval of K1 (user decision 3 of the M4 exit): <c>Baseline:SettleSeconds</c> is waited through the injected clock between the warm-up and the baseline window, printed once when it is above 0 and written to the run file.
    /// Every test runs the real <c>run --tier local</c> command through <see cref="LocalRunRig"/> on a fake clock; no live endpoint is called.
    /// </summary>
    [Trait("Spec", "REQ-021")]
    public sealed class SettleCases
    {
        private const long Size = 1000;
        private const string SettleLine = "Settle: 2 s before the baseline window";
        private static readonly TimeSpan Settle = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan Window = TimeSpan.FromSeconds(30);

        private static LocalRunRig OllamaRig() => LocalRunRig.Ollama(OllamaFixtures.Ps(OllamaFixtures.PsEntry(OllamaFixtures.StoryTag, Size, Size)));

        [Fact]
        public async Task The_default_waits_nothing_prints_no_settle_line_and_writes_zero()
        {
            using LocalRunRig rig = OllamaRig();

            CliRunResult result = await rig.RunAsync("run --tier local --task incident-brief --repeat 1 --baseline-seconds 30");

            Assert.Equal(0, result.ExitCode);
            Assert.DoesNotContain("Settle:", result.Stdout + "\n" + result.Stderr, StringComparison.Ordinal);
            Assert.DoesNotContain(Settle, rig.Time.History);
            Assert.Equal(1, rig.Recorder.Count(OperationRecorder.Baseline));
            using JsonDocument document = rig.ReadRunFile();
            JsonElement settle = document.RootElement.GetProperty("baseline").GetProperty("settleSeconds");
            Assert.Equal(JsonValueKind.Number, settle.ValueKind);
            Assert.Equal(0.0, settle.GetDouble());
        }

        [Fact]
        public async Task A_settle_of_2_s_waits_on_the_clock_before_the_baseline_window_prints_the_line_and_is_recorded()
        {
            using LocalRunRig rig = OllamaRig();
            rig.Setup.Settings["AgentJoules:Baseline:SettleSeconds"] = "2";

            CliRunResult result = await rig.RunAsync("run --tier local --task incident-brief --repeat 1 --baseline-seconds 30");

            Assert.Equal(0, result.ExitCode);
            TimeSpan[] waits = [.. rig.Time.History.Where(static due => due == Settle || due == Window)];
            Assert.Equal([Settle, Window], waits);
            Assert.Equal(1, Occurrences(result.Stdout + "\n" + result.Stderr, SettleLine));
            Assert.Equal("probe, meter-start, warm-up, baseline x2, task-call x4", rig.Recorder.Describe());
            using JsonDocument document = rig.ReadRunFile();
            Assert.Equal(2.0, document.RootElement.GetProperty("baseline").GetProperty("settleSeconds").GetDouble());
            Assert.Equal(30.0, document.RootElement.GetProperty("baseline").GetProperty("seconds").GetDouble(), 1.0);
        }

        [Fact]
        public async Task A_fractional_settle_is_told_with_its_fraction()
        {
            using LocalRunRig rig = OllamaRig();
            rig.Setup.Settings["AgentJoules:Baseline:SettleSeconds"] = "2.5";

            CliRunResult result = await rig.RunAsync("run --tier local --task incident-brief --repeat 1 --baseline-seconds 30");

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(1, Occurrences(result.Stdout + "\n" + result.Stderr, "Settle: 2.5 s before the baseline window"));
        }

        [Fact]
        public async Task The_settle_line_is_told_before_the_wait_not_after()
        {
            using LocalRunRig rig = OllamaRig();
            rig.Setup.Settings["AgentJoules:Baseline:SettleSeconds"] = "2";
            DateTimeOffset start = rig.Time.GetUtcNow();
            TimeSpan? elapsedWhenTold = null;
            bool waitsStartedWhenTold = true;
            RunListenerFixtures.Decorate(rig.Setup, inner => new OrderRecordingListener(inner, static () => null, _ =>
            {
                elapsedWhenTold = rig.Time.GetUtcNow() - start;
                waitsStartedWhenTold = rig.Time.History.Contains(Settle) || rig.Time.History.Contains(Window);
            }));

            CliRunResult result = await rig.RunAsync("run --tier local --task incident-brief --repeat 1 --baseline-seconds 30");

            Assert.Equal(0, result.ExitCode);
            // The warm-up has already advanced the clock when the line is told; the 2 s settle and the 30 s window both follow it.
            TimeSpan told = elapsedWhenTold ?? throw new Xunit.Sdk.XunitException("SettleStarted was not told.");
            Assert.True(rig.Time.GetUtcNow() - start >= told + Settle + Window, "the settle and the baseline window did not both run after the line was told.");
            Assert.False(waitsStartedWhenTold);
            Assert.Contains(Settle, rig.Time.History);
        }

        [Theory]
        [InlineData("3601")]
        [InlineData("-1")]
        public async Task A_settle_outside_zero_to_an_hour_exits_2_naming_the_key(string value)
        {
            using LocalRunRig rig = OllamaRig();
            rig.Setup.Settings["AgentJoules:Baseline:SettleSeconds"] = value;

            CliRunResult result = await rig.RunAsync("run --tier local --task incident-brief --repeat 1");

            Assert.Equal(2, result.ExitCode);
            Assert.Contains("Baseline:SettleSeconds", result.Stderr + result.Stdout, StringComparison.Ordinal);
        }

        private static int Occurrences(string text, string part)
        {
            int count = 0;
            for (int at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
            {
                count++;
            }

            return count;
        }
    }
}