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
    /// <returns>A task that completes when <paramref name="task"/> did; a fault of it propagates.</returns>
    /// <exception cref="TimeoutException">The task neither finished nor started a wait within the guard.</exception>
    public static Task RunWaitsToCompletionAsync(Task task, TimerTrackingTimeProvider fake, TimeSpan? guard = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(fake);

        return DriveWaitsAsync(task, fake, guard ?? TimeSpan.FromSeconds(30));
    }

    private static async Task DriveWaitsAsync(Task task, TimerTrackingTimeProvider fake, TimeSpan guard)
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
                fake.Advance(due);
            }
        }

        await task;
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