using System.Runtime.InteropServices;
using Armory.Agent.Engine.View;

namespace Armory.Agent;

// The tray's half of File Explorer and Windows notifications (docs/agent/EXPLORER.md): what the
// shell desk shows (the window, a reveal, a confirmation in front of everything, the answer in
// the foot line or as a notification), and the check-out question about files SolidWorks
// opened, as a Windows notification while the window is hidden (decisions D13 and C5).
internal sealed partial class TrayApp : IShellSurface
{
    // NotifyIcon refuses a longer balloon text.
    private const int BalloonTextLimit = 255;
    private ShellDesk? desk;
    private Notifier? notifier;
    private readonly OpenAsks asks = new();
    private System.Threading.Timer? askTimer;
    private int asksPosted;

    // Program calls this once the tray exists, with the link this launch was started for, if any:
    // the shell desk's waiting batches run, and every one after them.
    internal void AttachShell(ShellDesk shellDesk, string? link)
    {
        desk = shellDesk;
        // Only the installed copy has the notification identity (ShellIdentity); any other copy
        // speaks through the tray balloon.
        IToastPlatform? toasts = ShellIdentity.IsInstalledCopy(paths) ? new WindowsToasts(ShellIdentity.AppId) : null;
        notifier = new Notifier(toasts, ShowBalloon, log.Info);
        // A notification from an earlier run has buttons nobody can answer now.
        notifier.ClearAll();
        askTimer = new System.Threading.Timer(_ => Post(AskDue), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        host.ViewChanged += OnAsksView;
        shellDesk.Attach(host, this);
        if (link is not null)
        {
            var now = DateTimeOffset.UtcNow;
            shellDesk.Receive(new ShellBatch(ShellVerb.Uri, [link], now, now));
        }
    }

    // Quit: nothing more runs, and no notification of Armory's stays behind.
    private void CloseShell()
    {
        host.ViewChanged -= OnAsksView;
        askTimer?.Dispose();
        askTimer = null;
        desk?.Close();
        notifier?.ClearAll();
        notifier = null;
    }

    void IShellSurface.OpenWindow() => Post(OpenWindow);

    void IShellSurface.Reveal(string vaultPath) => Post(() =>
    {
        OpenWindow();
        window?.PostWhenReady(BridgeMessages.RevealMessage(vaultPath));
    });

    Task<bool> IShellSurface.ConfirmAsync(ShellQuestion question)
    {
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (marshal.IsDisposed) answer.TrySetResult(false);
        else
        {
            try { marshal.BeginInvoke(() => answer.TrySetResult(Ask(question))); }
            catch (InvalidOperationException) { answer.TrySetResult(false); }
        }
        return answer.Task;
    }

    void IShellSurface.Answer(ActionResult result) => Post(() =>
    {
        if (quitting) return;
        if (WindowShowing) window!.PostWhenReady(BridgeMessages.ActionResultMessage(BridgeMessages.ShellRequest, result.Ok, result.Message));
        else notifier?.Answer(result, OpenLink([], null));
    });

    // A TaskDialog with the window's words, in front of everything: ArmoryShell.exe let Armory
    // take the foreground for the click. Starts on Cancel.
    private bool Ask(ShellQuestion question)
    {
        if (quitting) return false;
        var ok = new TaskDialogButton(question.Ok);
        var cancel = TaskDialogButton.Cancel;
        var page = new TaskDialogPage
        {
            Caption = "IDEA Armory",
            Heading = question.Title,
            Text = question.Text,
            Icon = question.Warning ? TaskDialogIcon.Warning : TaskDialogIcon.None,
            AllowCancel = true,
            SizeToContent = true,
        };
        page.Buttons.Add(ok);
        page.Buttons.Add(cancel);
        page.DefaultButton = cancel;
        page.Created += (_, _) =>
        {
            if (page.BoundDialog is { } dialog) SetForegroundWindow(dialog.Handle);
        };
        var owner = WindowShowing ? window : null;
        var answer = owner is not null ? TaskDialog.ShowDialog(owner, page, TaskDialogStartupLocation.CenterOwner)
            : TaskDialog.ShowDialog(page, TaskDialogStartupLocation.CenterScreen);
        return answer == ok;
    }

    private void ShowBalloon(string title, string text, bool warning)
    {
        if (quitting) return;
        notify.ShowBalloonTip(10000, Clip(title, CheckOutPrompts.TitleLimit), Clip(text, BalloonTextLimit), warning ? ToolTipIcon.Warning : ToolTipIcon.None);
    }

    private static string Clip(string text, int limit) => text.Length <= limit ? text : text[..(limit - 3)] + "...";

    // A link that opens the window (show), or does action on paths, once.
    private string OpenLink(IReadOnlyList<string> paths, string? tag, string action = ProtocolLink.Show)
        => ProtocolLink.Format(desk!.Tokens.Issue(action, paths, tag), action);

    // ---- The check-out question about opened files -------------------------------------------

    // Any thread, every view: one look on the window thread at a time.
    private void OnAsksView(AgentView view)
    {
        if (Interlocked.Exchange(ref asksPosted, 1) == 1) return;
        Post(() =>
        {
            Volatile.Write(ref asksPosted, 0);
            UpdateAsks();
        });
    }

    private void UpdateAsks()
    {
        if (quitting || notifier is null) return;
        IReadOnlyList<OpenPromptInfo> open;
        try { open = host.CurrentOpenPrompts(); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            log.Error("could not read the open files", error);
            return;
        }
        foreach (var tag in asks.Update(open, WindowShowing, DateTimeOffset.UtcNow)) notifier.Withdraw(tag, ToastXml.OpenGroup);
        ScheduleAsk();
    }

    private void AskDue()
    {
        if (quitting || notifier is null) return;
        if (asks.Due(DateTimeOffset.UtcNow, WindowShowing) is { } ask) ShowAsk(ask);
        ScheduleAsk();
    }

    private void ScheduleAsk()
    {
        if (asks.NextDue is not { } due) return;
        var wait = due - DateTimeOffset.UtcNow;
        try { askTimer?.Change(wait < TimeSpan.Zero ? TimeSpan.Zero : wait, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { }
    }

    // One file: "Check out Plate.SLDPRT to edit it?" with Check out and reopen and Not now (or who
    // has it). Several: "SolidWorks opened 3 files you haven't checked out" with Open Armory.
    private void ShowAsk(OpenAsk ask)
    {
        var paths = ask.Files.Select(f => f.Path).ToArray();
        ToastContent toast;
        (string Title, string Text) balloon;
        if (ask.Files.Count == 1)
        {
            var file = ask.Files[0];
            var (title, text) = ToastWords.Ask(file);
            IReadOnlyList<ToastButton> buttons = file.CanCheckOut
                ? [new(ToastWords.CheckOutAndReopen, OpenLink(paths, ask.Tag, ProtocolLink.CheckOut)), new(ToastWords.NotNow, null)]
                : [];
            toast = new ToastContent(ask.Tag, ToastXml.OpenGroup, title, text, OpenLink(paths, ask.Tag), buttons);
            balloon = CheckOutPrompts.Words(file.Name, file.CanCheckOut ? null : file.CheckedOutBy);
        }
        else
        {
            var (title, text) = ToastWords.Group(ask.Files.Count);
            toast = new ToastContent(ask.Tag, ToastXml.OpenGroup, title, text, OpenLink(paths, ask.Tag),
                [new(ToastWords.OpenArmory, OpenLink(paths, ask.Tag)), new(ToastWords.NotNow, null)]);
            balloon = (title, "Click here to open Armory and check out the ones you'll change.");
        }
        var how = notifier!.Ask(toast, balloon) switch
        {
            NotifiedBy.Toast => "a Windows notification",
            NotifiedBy.Balloon => "the tray balloon",
            _ => "nothing (notifications are off; the window's card asks)",
        };
        log.Info("shell: " + ask.Files.Count + (ask.Files.Count == 1 ? " opened file" : " opened files") + " asked about by " + how);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);
}
