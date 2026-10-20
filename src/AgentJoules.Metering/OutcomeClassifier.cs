using System.Net.Sockets;

using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;

namespace AgentJoules.Metering;

/// <summary>
/// Decides how a metered call ended (REQ-007, EDGE-016): one of the <see cref="StepOutcomes"/> strings, from the exception the call ended with,
/// the tier and whether any update of the stream had arrived. The rules, in this order:
/// <list type="number">
/// <item><description>No exception: <c>ok</c>.</description></item>
/// <item><description>An <see cref="OperationCanceledException"/> (or a subclass) while the caller's token is cancelled: <c>cancelled</c>. A cancellation the caller did not ask for, such as the timeout of an HTTP client, is not this rule and ends as <c>error</c>; so does one whose inner exception is a <see cref="TimeoutException"/>, even when the caller's token is cancelled too, because the timeout is what ended the call.</description></item>
/// <item><description>A transport failure: an <see cref="IOException"/>, a <see cref="SocketException"/> or an <see cref="HttpRequestException"/> anywhere in the tree of inner exceptions (every member of an <see cref="AggregateException"/> is followed, breadth first, up to 64 exceptions), and no <see cref="HttpRequestException"/> with a <see cref="HttpRequestException.StatusCode"/> anywhere in it. A tree larger than the limit is not understood and counts as no transport failure. It is <c>incomplete</c> on the cloudgpu tier (a connection that is cut is how a stopped or recycled GPU instance looks), and on any tier when the drop came after the first update was received (the answer was already under way, EDGE-016). The same failure before the first update on the local, nvidia and nebius tiers is <c>error</c>.</description></item>
/// <item><description>Everything else, including every HTTP-status error, every timeout and a later <c>RetriesExhaustedException</c>: <c>error</c>.</description></item>
/// </list>
/// A stream whose consumer walked away is not an exception, so the client decides <c>cancelled</c> for it before it asks this class.
/// </summary>
public static class OutcomeClassifier
{
    // The most exceptions of one tree this class looks at; a deeper or wider tree is not something a transport layer produces.
    private const int NodeLimit = 64;

    /// <summary>Classifies how a call ended.</summary>
    /// <param name="error">The exception the call ended with, or null when it finished.</param>
    /// <param name="tier">The tier of the call.</param>
    /// <param name="firstUpdateSeen">True when any update of the stream had arrived before the call ended, whether or not it held text (a role-only first chunk counts); always false for a buffered call.</param>
    /// <param name="callerToken">The token the caller passed to the call; it is last by convention (CA1068), and it is what tells a cancellation of the caller from a timeout.</param>
    /// <returns>One of the <see cref="StepOutcomes"/> constants.</returns>
    public static string Classify(Exception? error, Tier tier, bool firstUpdateSeen, CancellationToken callerToken)
    {
        return error is null
            ? StepOutcomes.Ok
            : IsCallerCancellation(error, callerToken)
                ? StepOutcomes.Cancelled
                : IsTransportFailure(error) && (tier == Tier.CloudGpu || firstUpdateSeen)
                    ? StepOutcomes.Incomplete
                    : StepOutcomes.Error;
    }

    private static bool IsCallerCancellation(Exception error, CancellationToken callerToken) =>
        error is OperationCanceledException && callerToken.IsCancellationRequested && error.InnerException is not TimeoutException;

    // A transport failure is a cut connection, not an answer of the server: the tree holds an I/O, socket or HTTP-transport exception and no
    // HTTP status, because a status means the server answered (and the answer is an error).
    private static bool IsTransportFailure(Exception error)
    {
        Queue<Exception> pending = new();
        pending.Enqueue(error);
        int visited = 0;
        bool transport = false;
        while (pending.Count > 0 && visited < NodeLimit)
        {
            Exception current = pending.Dequeue();
            visited++;
            switch (current)
            {
                case HttpRequestException { StatusCode: not null }:
                    return false;
                case HttpRequestException or IOException or SocketException:
                    transport = true;
                    break;
                default:
                    break;
            }

            if (current is AggregateException aggregate)
            {
                foreach (Exception inner in aggregate.InnerExceptions)
                {
                    pending.Enqueue(inner);
                }
            }
            else if (current.InnerException is { } next)
            {
                pending.Enqueue(next);
            }
        }

        // Exceptions left over mean the limit was hit before the whole tree was seen; one of them could be the HTTP status, so say nothing.
        return transport && pending.Count == 0;
    }
}