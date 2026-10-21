using Microsoft.Extensions.Time.Testing;

namespace AgentJoules.TestSupport;

/// <summary>
/// Drives a <see cref="FakeTimeProvider"/> forward until a task that waits on it completes.
/// <see cref="Task.Yield"/> between steps lets continuations run but does not wait for the code under test
/// to go idle, so a test that asserts exact tick counts must advance fake time by hand instead of through
/// this driver.
/// </summary>
public static class FakeTimeDriver
{
    /// <summary>
    /// Advances <paramref name="fake"/> by <paramref name="step"/> per iteration, yielding between steps so
    /// continuations run, until <paramref name="task"/> completes; then awaits it so a fault propagates.
    /// </summary>
    /// <param name="task">The task to run to completion; it must wait on <paramref name="fake"/>.</param>
    /// <param name="fake">The clock to advance.</param>
    /// <param name="step">How far each iteration advances the clock.</param>
    /// <param name="maxSteps">The iteration cap; one million steps by default.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="step"/> or <paramref name="maxSteps"/> is zero or negative.</exception>
    /// <exception cref="TimeoutException">The task did not complete within <paramref name="maxSteps"/> steps.</exception>
    public static Task RunUntilCompleteAsync(Task task, FakeTimeProvider fake, TimeSpan step, int maxSteps = 1_000_000)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(fake);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(step, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxSteps, 0);

        return DriveAsync(task, fake, step, maxSteps);
    }

    /// <summary>
    /// Runs <paramref name="task"/> to completion on a clock that moves only when the task waits: whenever a timer is created on <paramref name="fake"/> the clock
    /// advances by exactly its due time, so the fake time that passes is the sum of the waits and of whatever the code under test advances itself, and never
    /// a step more. The real-time guard only ends a task that stops without waiting and without finishing.
    /// Only timer creation is tracked (<c>ITimer.Change</c> on an existing timer is not), and the driver assumes one outstanding timer at a time: a task that
    /// waits on several timers at once is not driven correctly.
    /// </summary>
    /// <param name="task">The task to run to completion.</param>
    /// <param name="fake">The clock the task waits on.</param>
    /// <param name="guard">The real time after which a task that neither finishes nor waits is a failure; 30 seconds when null.</param>
    /// <param name="maxStep">
    /// The longest single advance, or null to advance each due time in one step. A <c>PeriodicTimer</c> on a fake clock fires every period crossed by one advance with the clock
    /// already at the end of it (design 5.4), so the samples of a sampling loop that runs during a long wait (the idle baseline of a measured run) all carry the same instant; with a
    /// step of one sampling period the wait is advanced period by period, each tick fires at its own instant, and a power script that depends on time is sampled as it changes.
    /// </param>
    /// <param name="beforeStep">
    /// Awaited before every advance (each step when <paramref name="maxStep"/> is set), bounded by the guard, or null for none: a test that flips something from inside a tick (a
    /// cancellation) holds the clock here until that has run, so no further tick can overtake it.
    /// </param>
    /// <returns>A task that completes when <paramref name="task"/> did; a fault of it propagates.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxStep"/> is zero or negative.</exception>
    /// <exception cref="TimeoutException">The task neither finished nor started a wait within the guard.</exception>
    public static Task RunWaitsToCompletionAsync(Task task, TimerTrackingTimeProvider fake, TimeSpan? guard = null, TimeSpan? maxStep = null, Func<Task>? beforeStep = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(fake);
        if (maxStep is { } step)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(step, TimeSpan.Zero, nameof(maxStep));
        }

        return DriveWaitsAsync(task, fake, guard ?? TimeSpan.FromSeconds(30), maxStep, beforeStep);
    }

    private static async Task DriveWaitsAsync(Task task, TimerTrackingTimeProvider fake, TimeSpan guard, TimeSpan? maxStep, Func<Task>? beforeStep)
    {
        while (!task.IsCompleted)
        {
            Task next = fake.DueTimes.WaitToReadAsync().AsTask();
            Task first = await Task.WhenAny(task, next).WaitAsync(guard);
            if (first == task)
            {
                break;
            }

            if (fake.DueTimes.TryRead(out TimeSpan due))
            {
                await AdvanceAsync(fake, due, maxStep, beforeStep, guard);
            }
        }

        await task;
    }

    // Waits for the test's hold before an advance, bounded by the guard; no hold advances at once.
    private static async Task HoldAsync(Func<Task>? beforeStep, TimeSpan guard)
    {
        if (beforeStep is not null)
        {
            await beforeStep().WaitAsync(guard);
        }
    }

    // One advance of the due time, or steps of at most maxStep with a yield between them so the continuations of each tick run before the next one; the hold runs before each advance.
    private static async Task AdvanceAsync(FakeTimeProvider fake, TimeSpan due, TimeSpan? maxStep, Func<Task>? beforeStep, TimeSpan guard)
    {
        if (maxStep is not { } step)
        {
            await HoldAsync(beforeStep, guard);
            fake.Advance(due);
            return;
        }

        TimeSpan remaining = due;
        while (remaining > TimeSpan.Zero)
        {
            TimeSpan next = remaining < step ? remaining : step;
            await HoldAsync(beforeStep, guard);
            fake.Advance(next);
            remaining -= next;
            if (remaining > TimeSpan.Zero)
            {
                await Task.Yield();
            }
        }
    }

    private static async Task DriveAsync(Task task, FakeTimeProvider fake, TimeSpan step, int maxSteps)
    {
        DateTimeOffset start = fake.GetUtcNow();
        int iterations = 0;
        while (!task.IsCompleted)
        {
            if (iterations++ >= maxSteps)
            {
                throw new TimeoutException(
                    $"The task did not complete after {fake.GetUtcNow() - start} of fake time ({maxSteps} steps of {step}).");
            }

            fake.Advance(step);
            await Task.Yield();
        }

        await task;
    }
}