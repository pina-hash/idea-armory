using Armory.Core;

namespace Armory.Agent.Engine;

// Desk tracking (0.5.6; idea-app ledger 0521, docs/agent/TRACKING.md): what this computer saw of
// each team file, queued for the website. A file is opened when the student opens it in its own
// SolidWorks window (a reference an assembly loads is not), or when SolidWorks' lock marker for
// it appears without the link; closed when that window or marker goes; saved on every SolidWorks
// save the link reports. Only files the team has (a server id) are queued: a draft nobody else
// can see says nothing to the website. The host sends the queue (AgentHost.Tracking.cs); nothing
// here waits on the network.
public sealed partial class SyncEngine
{
    // Safe from any thread (DeskEventQueue locks).
    public DeskEventQueue DeskEvents { get; } = new();

    private void Desk(string vaultPath, DeskEventKind kind, DateTimeOffset at)
    {
        // The server's id when the last read had it, else the one this computer keeps for the file.
        Guid? id = remoteByPath.TryGetValue(vaultPath, out var remote) ? (remote.File.Deleted ? null : remote.File.Id)
            : state.Files.TryGetValue(vaultPath, out var st) ? st.FileId : null;
        if (id is { } fileId) DeskEvents.Add(fileId, kind, at);
    }

    // Without the link, SolidWorks' lock markers are the one sign of an open: one that appears is
    // an open, one that goes is a close.
    private void DeskMarkers(IReadOnlySet<string> before, IReadOnlySet<string> after, DateTimeOffset at)
    {
        foreach (var opened in after.Where(d => !before.Contains(d))) Desk(opened, DeskEventKind.Opened, at);
        foreach (var closed in before.Where(d => !after.Contains(d))) Desk(closed, DeskEventKind.Closed, at);
    }
}
