using Armory.Agent.Engine.View;

namespace Armory.Agent;

// Which Armory this is (0.4.3, InstallRecord): the version and its date for Settings and the
// tray, and the one-line notices said once at start (an update installed; how to pin Armory
// again when an old taskbar pin was put right). A notice waits until the tray listens.
internal sealed partial class AgentHost
{
    private readonly object noticeGate = new();
    private readonly List<string> noticesWaiting = [];
    private Action<string>? noticeHandler;
    private InstallRecord? installed;

    internal InstallRecord? Installed => Volatile.Read(ref installed);

    // The tray's way to say a notice: every waiting one now, then each as it comes.
    internal void OnNotice(Action<string> handler)
    {
        string[] waiting;
        lock (noticeGate)
        {
            noticeHandler = handler;
            waiting = [.. noticesWaiting];
            noticesWaiting.Clear();
        }
        foreach (var words in waiting) handler(words);
    }

    private void Notice(string words)
    {
        Action<string>? handler;
        lock (noticeGate)
        {
            handler = noticeHandler;
            if (handler is null) noticesWaiting.Add(words);
        }
        handler?.Invoke(words);
    }

    // In the constructor, before the settings are read (a first install has none yet).
    private void StartInstallRecord()
    {
        var (record, notice) = InstallRecord.Start(paths, AgentPaths.Version, parts.Clock.GetUtcNow());
        Volatile.Write(ref installed, record);
        log.Info("install: " + record.Words + (record.Previous is { } from ? " (from " + from + ")" : ""));
        if (notice is not null) Notice(notice);
    }

    // The shell identity's repair rewrote a taskbar pin: the sentence on pinning again, once ever.
    private void PinsRepaired(IReadOnlyList<TaskbarPins.Fix> fixes)
    {
        if (fixes.Count == 0 || Installed is not { PinNoticeShown: false } record) return;
        Volatile.Write(ref installed, InstallRecord.MarkPinNotice(paths, record));
        Notice(TaskbarPins.RepinWords);
    }

    private AboutView? AboutNow()
    {
        if (Installed is not { } record) return null;
        var device = clients is null ? null : Sessions.Current?.DeviceId;
        return new AboutView(record.Version, record.Words, record.Since.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            parts.MachineName, device is { } id ? DeviceTag(id) : null);
    }

    // The website's and the check-out rows' tag: the device id's first four characters.
    internal static string DeviceTag(Guid device) => device.ToString("N")[..4];
}
