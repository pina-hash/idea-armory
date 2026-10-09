using Armory.Agent.Engine.View;

namespace Armory.Agent;

// Send feedback, the same as the website's, and "Your feedback" (0.3.3; docs/agent/BRIDGE.md and
// docs/agent/CLIENT.md section 7): the window's half. FeedbackDesk decides; this only wires it to
// the host's sender, telemetry, API and sign-in, and keeps the window's last picture.
internal sealed partial class AgentHost
{
    private FeedbackDesk? desk;

    // Send feedback's picture of the window: the last one only, in memory only (MainWindow takes
    // it and serves it to the page; the Bridge keeps it here).
    internal WindowShots Shots { get; } = new();

    internal FeedbackDesk Desk => LazyInitializer.EnsureInitialized(ref desk,
        () => new FeedbackDesk(Feedback, Telemetry, Api, Sessions, Shots, TimeProvider.System, log.Info));

    // "Send feedback": without a picture the note is saved here first and sent when the site can
    // take it; with one it goes now and is never saved. One sentence back, never an error for a
    // site that isn't ready; with a picture that couldn't go, the offer to send it without one.
    internal async Task<ActionResult> SendFeedbackAsync(string? kind, string? body, string? tried, string? area, string? shot)
    {
        try { return await Desk.SendAsync(kind, body, tried, area, shot).ConfigureAwait(false); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            log.Error("send feedback failed", error);
            return shot is null
                ? new ActionResult(false, "Armory couldn't save your feedback. Try again in a moment.")
                : new ActionResult(false, "Armory couldn't send your feedback. Try again in a moment, or send it without the picture.", ActionResult.WithoutPicture);
        }
    }

    // "Your feedback": this account's notes and where each one is. Never throws.
    internal async Task<FeedbackListView> ReadMyFeedbackAsync()
    {
        try { return await Desk.ReadAsync().ConfigureAwait(false); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            log.Error("your feedback failed", error);
            return new FeedbackListView(FeedbackListView.Failed, false, FeedbackDesk.ListFailed, []);
        }
    }
}
