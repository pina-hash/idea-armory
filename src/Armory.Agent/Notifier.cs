using Armory.Agent.Engine;
using Armory.Agent.Engine.View;

namespace Armory.Agent;

// Whether Windows shows this app's notifications (ToastNotifier.Setting): on, or off by the
// person, for this app, by policy or by the app's own registration.
internal enum ToastSetting { Enabled, Off }

// Windows' notifications for Armory, as the Notifier needs them. WindowsToasts is the only one on
// Windows; every member may throw when the notification platform can't be reached.
internal interface IToastPlatform
{
    ToastSetting Setting { get; }
    void Show(ToastContent toast);
    void Remove(string tag, string group);
    void Clear();
}

// How a notification went out.
internal enum NotifiedBy { Toast, Balloon, Nothing }

// Armory's notifications outside its window (docs/agent/EXPLORER.md, "Windows notifications").
// A Windows notification when it can show one; when notifications are off for Armory (the
// person's choice, Focus Assist aside, or policy) nothing at all, not even a tray balloon, since
// the window's own card and foot line carry the same words; the tray balloon only when Windows'
// notification API itself fails (it throws), or for a copy of Armory with no notification
// identity (toasts null: a test or a developer's build). Not thread safe: TrayApp calls it on
// its window thread.
internal sealed class Notifier(IToastPlatform? toasts, Action<string, string, bool> balloon, Action<string> log, TimeProvider? timeProvider = null)
{
    // An answer that follows another this soon replaces it instead of stacking.
    internal static readonly TimeSpan ReplaceWithin = TimeSpan.FromSeconds(6);

    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;
    private DateTimeOffset lastAnswer = DateTimeOffset.MinValue;
    private int answers;
    private bool saidOff;
    private bool saidFailed;

    // The tag of the newest answer.
    internal string AnswerTag => "answer-" + answers;

    // The one sentence of an action Armory did for File Explorer or a notification's button,
    // while the window is not showing. launch: the link a click opens (the window).
    internal NotifiedBy Answer(ActionResult result, string launch)
    {
        var now = time.GetUtcNow();
        if (now - lastAnswer >= ReplaceWithin) answers++;
        lastAnswer = now;
        var (title, text) = ToastWords.Answer(result.Message);
        var toast = new ToastContent(AnswerTag, ToastXml.AnswerGroup, title, text, launch, []);
        return Show(toast, "IDEA Armory", result.Message, warning: !result.Ok);
    }

    // The check-out question about files SolidWorks opened. balloonWords: what the tray balloon
    // says instead, when it must.
    internal NotifiedBy Ask(ToastContent toast, (string Title, string Text) balloonWords) => Show(toast, balloonWords.Title, balloonWords.Text, warning: false);

    // A question that no longer stands (its files closed, or were checked out).
    internal void Withdraw(string tag, string group)
    {
        if (toasts is null) return;
        try { toasts.Remove(tag, group); }
        catch (Exception error) when (error is not OutOfMemoryException) { Failed(error); }
    }

    // At start and at quit: no notification of Armory's stays with a button that answers nothing.
    internal void ClearAll()
    {
        if (toasts is null) return;
        try { toasts.Clear(); }
        catch (Exception error) when (error is not OutOfMemoryException) { Failed(error); }
    }

    private NotifiedBy Show(ToastContent toast, string balloonTitle, string balloonText, bool warning)
    {
        if (toasts is not null)
        {
            try
            {
                if (toasts.Setting != ToastSetting.Enabled)
                {
                    if (!saidOff) log("notifications are off for Armory in Windows; the window asks instead");
                    saidOff = true;
                    return NotifiedBy.Nothing;
                }
                toasts.Show(toast);
                return NotifiedBy.Toast;
            }
            catch (Exception error) when (error is not OutOfMemoryException) { Failed(error); }
        }
        balloon(balloonTitle, balloonText, warning);
        return NotifiedBy.Balloon;
    }

    private void Failed(Exception error)
    {
        if (!saidFailed) log("Windows notifications failed, so Armory uses the tray instead: " + error.GetType().Name + ": " + error.Message);
        saidFailed = true;
    }
}

// One open file SolidWorks has that this computer has not checked out, for the notification
// that asks about it. CheckedOutBy: "Maria Lopez on LAB-PC-07" or "you on LAB-PC-07" when it can't
// be checked out here.
internal sealed record OpenPromptInfo(string Path, string Name, bool CanCheckOut, string? CheckedOutBy);

// One notification's worth: a tag and the files it asks about (one, or a group).
internal sealed record OpenAsk(string Tag, IReadOnlyList<OpenPromptInfo> Files);

// Which opened files get a notification, and when (decision D13 with C5): each open once, never
// while the window shows (its card asks), and opens close together as one notification (a group
// of N files): an open gathers for Gather after the last one, at most GatherAtMost after the
// first. A notification is withdrawn when none of its files is still open and unchecked out
// here. Nothing here ever checks a file out. Pure: time comes in with each call.
internal sealed class OpenAsks
{
    internal static readonly TimeSpan Gather = TimeSpan.FromSeconds(1.5);
    internal static readonly TimeSpan GatherAtMost = TimeSpan.FromSeconds(10);

    private readonly CheckOutPrompts once = new();
    private readonly List<OpenPromptInfo> gathering = [];
    private readonly Dictionary<string, string[]> shown = new(StringComparer.Ordinal);
    private DateTimeOffset first, last;

    // When the gathered opens are due to be asked about, or null.
    internal DateTimeOffset? NextDue => gathering.Count == 0 ? null : Min(last + Gather, first + GatherAtMost);

    // The files open now without a check out here, each time they change. Returns the tags of
    // notifications to withdraw.
    internal IReadOnlyList<string> Update(IReadOnlyList<OpenPromptInfo> open, bool windowShowing, DateTimeOffset now)
    {
        var paths = new HashSet<string>(open.Select(o => o.Path), StringComparer.OrdinalIgnoreCase);
        // A file closed (or checked out) may ask again the next time it is opened.
        once.KeepOnly(paths);
        gathering.RemoveAll(g => !paths.Contains(g.Path));
        foreach (var file in open)
        {
            if (!once.ShouldOffer(file.Path, windowShowing)) continue;
            if (gathering.Count == 0) first = now;
            last = now;
            gathering.Add(file);
        }
        List<string> gone = [];
        foreach (var (tag, files) in shown.ToArray())
        {
            if (files.Any(paths.Contains)) continue;
            shown.Remove(tag);
            gone.Add(tag);
        }
        return gone;
    }

    // The notification due at now, if any. While the window shows, the gathered opens are left
    // to its card.
    internal OpenAsk? Due(DateTimeOffset now, bool windowShowing)
    {
        if (NextDue is not { } due || now < due) return null;
        var files = gathering.ToArray();
        gathering.Clear();
        if (windowShowing) return null;
        var tag = ToastXml.TagFor(files.Select(f => f.Path));
        shown[tag] = files.Select(f => f.Path).ToArray();
        return new OpenAsk(tag, files);
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}

// One question before a save down, as its notification shows it: the prompt and its tag.
internal sealed record SaveDownAsk(string Tag, SaveDownPrompt Prompt);

// Which questions before a save down get a Windows notification (B2 on a computer newer than its
// project's year): each SaveDownPrompt once, only while the window is hidden (its notice card
// asks otherwise, and a prompt the card asked never comes back as a notification), with "Save in
// 2025" and "Keep on this computer only". A notification is withdrawn when its prompt no longer
// stands (answered, saved, or the file closed); the same prompt may ask again only after that.
// No answer is the team's rule, so nothing waits on one. Pure.
internal sealed class SaveDownAsks
{
    private readonly HashSet<string> asked = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> shown = new(StringComparer.Ordinal);

    // The prompts standing now. Returns the notifications to show and the tags to withdraw.
    internal (IReadOnlyList<SaveDownAsk> Show, IReadOnlyList<string> Withdraw) Update(IReadOnlyList<SaveDownPrompt> prompts, bool windowShowing)
    {
        var keys = new HashSet<string>(prompts.Select(p => p.Key), StringComparer.Ordinal);
        asked.IntersectWith(keys);
        List<string> withdraw = [];
        foreach (var (tag, key) in shown.ToArray())
        {
            if (keys.Contains(key)) continue;
            shown.Remove(tag);
            withdraw.Add(tag);
        }
        List<SaveDownAsk> show = [];
        foreach (var prompt in prompts)
        {
            if (!asked.Add(prompt.Key) || windowShowing) continue;
            var tag = TagOf(prompt);
            shown[tag] = prompt.Key;
            show.Add(new SaveDownAsk(tag, prompt));
        }
        return (show, withdraw);
    }

    // No file name reaches Windows' notification store: a hash of the prompt's key.
    internal static string TagOf(SaveDownPrompt prompt) => "s" + ToastXml.TagFor([prompt.Key]);
}
