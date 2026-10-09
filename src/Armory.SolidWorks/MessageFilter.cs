using System.Runtime.InteropServices;

namespace Armory.SolidWorks;

// The link thread's COM message filter (Microsoft's "Application is busy" pattern): while
// SolidWorks is busy (a modal dialog, a rebuild) it answers calls with SERVERCALL_RETRYLATER,
// and this filter retries the call every 100 ms for up to 30 seconds; after that the call fails
// and the link retries the work later, never losing it. Registered on the link's STA thread
// only, and revoked when the thread stops. While a SolidWorks is starting or closing, the link
// asks for a short limit (Hurry), so one busy SolidWorks never holds the thread for long.
[ComVisible(true)]
internal sealed class MessageFilter : IOleMessageFilter
{
    // SERVERCALL_ISHANDLED, SERVERCALL_RETRYLATER, PENDINGMSG_WAITDEFPROCESS.
    private const int ServerCallIsHandled = 0, ServerCallRetryLater = 2, PendingMessageWaitDefProcess = 2;
    internal static readonly TimeSpan RetryFor = TimeSpan.FromSeconds(30);
    private const int RetryAfterMilliseconds = 100;
    private IOleMessageFilter? previous;

    // How long a rejected call is retried now.
    internal TimeSpan Limit { get; private set; } = RetryFor;

    // How long to wait before retrying a rejected call (milliseconds), or -1 to give up.
    internal static int RetryAfter(int rejectType, int elapsedMilliseconds, TimeSpan limit)
        => rejectType == ServerCallRetryLater && elapsedMilliseconds < limit.TotalMilliseconds ? RetryAfterMilliseconds : -1;

    public int HandleInComingCall(int callType, IntPtr taskCaller, int tickCount, IntPtr interfaceInfo) => ServerCallIsHandled;

    public int RetryRejectedCall(IntPtr taskCallee, int tickCount, int rejectType) => RetryAfter(rejectType, tickCount, Limit);

    public int MessagePending(IntPtr taskCallee, int tickCount, int pendingType) => PendingMessageWaitDefProcess;

    internal bool Register() => Native.CoRegisterMessageFilter(this, out previous) >= 0;

    internal void Revoke() => Native.CoRegisterMessageFilter(previous, out _);

    // A shorter limit until the returned scope ends (on the link's thread).
    internal IDisposable Hurry(TimeSpan limit)
    {
        var before = Limit;
        Limit = limit < before ? limit : before;
        return new Scope(() => Limit = before);
    }

    private sealed class Scope(Action end) : IDisposable
    {
        public void Dispose() => end();
    }
}
