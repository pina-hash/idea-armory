using System.Threading.Channels;
using Armory.Agent.Engine;

namespace Armory.SolidWorks;

public sealed record SolidWorksLinkOptions
{
    // The vault root: only documents under it are reported.
    public required string VaultRoot { get; init; }
    // Where solidworks.json lives (the student's own Save to Version setting, kept for a crash);
    // null keeps it in memory only (tests).
    public string? SettingsFile { get; init; }
    public Action<string>? Log { get; init; }
    public TimeProvider Clock { get; init; } = TimeProvider.System;
    // How often to look for SolidWorks processes (and whether a starting one is ready).
    public TimeSpan DiscoveryInterval { get; init; } = TimeSpan.FromSeconds(2);
    // Which processes to look at. Null: every process named SLDWORKS (or the test-only
    // ARMORY_SOLIDWORKS_PROCESS) in this session; tests give their fakes' process ids.
    public Func<IReadOnlyList<int>>? Candidates { get; init; }
    public TimeSpan ModifySettle { get; init; } = TimeSpan.FromSeconds(3);
}

// The SolidWorks link inside IdeaArmory.exe (docs/agent/SOLIDWORKS.md; research
// addin-registration.md). No add-in is registered and nothing is loaded into SolidWorks: every
// two seconds the link looks for this Windows user's SolidWorks processes in this session,
// attaches to each one through the Running Object Table once it finished starting, and follows
// its application and document events out of process, on one STA thread with its own message
// loop and message filter. What it sees goes to the engine as records on one channel; the
// engine's commands come back through the ISolidWorksLink methods. When SolidWorks closes, when
// Armory quits, or when the process dies, every reference is released and the student's own
// Save to Version setting put back.
public sealed class SolidWorksLink : ISolidWorksLink, IAsyncDisposable
{
    private readonly SolidWorksLinkOptions options;
    private readonly Channel<LinkRecord> records = Channel.CreateUnbounded<LinkRecord>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });
    private readonly Dictionary<int, LinkSession> sessions = [];
    // Processes already told about once (started as administrator), and ones that said they
    // are closing (never attached to again while they finish).
    private readonly HashSet<int> refused = [];
    private readonly HashSet<int> closing = [];
    private readonly LinkContext context;
    private readonly StaThread thread;
    private MessageFilter? filter;
    private bool started, disposed;

    public SolidWorksLink(SolidWorksLinkOptions options)
    {
        this.options = options;
        var settings = LinkSettings.Load(options.SettingsFile, options.Log);
        thread = new StaThread("Armory SolidWorks link", options.Log, onStart: RegisterFilter, onStop: () => filter?.Revoke());
        context = new LinkContext
        {
            Thread = thread,
            Settings = settings,
            Emit = record => records.Writer.TryWrite(record),
            Log = options.Log,
            Clock = options.Clock,
            VaultRoot = options.VaultRoot,
            ModifySettle = options.ModifySettle,
            OtherSessionOfRelease = (pid, major) => sessions.Values.Any(s => s.Pid != pid && s.State == LinkSession.Phase.Attached && s.Revision.Major == major),
        };
        // The filter registered as the thread started (before any of this was posted).
        thread.Post(() => context.Filter = filter);
    }

    public ChannelReader<LinkRecord> Records => records.Reader;

    // Discovery starts (its first look at once).
    public void Start()
    {
        if (started) return;
        started = true;
        thread.Post(Discover);
        thread.Every(options.DiscoveryInterval, Discover);
    }

    // The vault root changed (Settings): documents are reported under the new one.
    public void SetVaultRoot(string vaultRoot) => thread.Post(() => context.VaultRoot = vaultRoot);

    // ---- ISolidWorksLink --------------------------------------------------------------------

    public Task<WritableOutcome> MakeWritableAsync(string fullPath, CancellationToken cancellationToken)
        => thread.InvokeAsync(() => SessionWith(fullPath) is { } session ? session.MakeWritable(fullPath) : WritableOutcome.LinkGone).WaitAsync(cancellationToken);

    public Task<SaveDownOutcome> SaveInPinnedReleaseAsync(string fullPath, CancellationToken cancellationToken)
        => thread.InvokeAsync(() => SessionWith(fullPath) is { } session ? session.SaveInPinnedRelease(fullPath) : SaveDownOutcome.LinkGone).WaitAsync(cancellationToken);

    public void SetPins(IReadOnlyList<ProjectPin> pins) => thread.Post(() =>
    {
        context.Pins = pins;
        foreach (var session in sessions.Values) session.SetPins(pins);
    });

    public void KeepLocal(string fullPath, bool keep) => thread.Post(() =>
    {
        foreach (var session in sessions.Values) session.KeepLocal(fullPath, keep);
    });

    public void StatusText(string text) => thread.Post(() =>
    {
        foreach (var session in sessions.Values) session.StatusText(text);
    });

    public void Replay() => thread.Post(() =>
    {
        foreach (var session in sessions.Values) session.Replay();
        foreach (var pid in refused) records.Writer.TryWrite(new LinkRefused(pid, "started as administrator"));
    });

    // How many SolidWorks are linked now (tests).
    internal Task<int> AttachedCountAsync() => thread.InvokeAsync(() => sessions.Values.Count(s => s.State == LinkSession.Phase.Attached));

    // ---- Discovery (on the link's thread) ------------------------------------------------------------

    private void RegisterFilter()
    {
        var candidate = new MessageFilter();
        if (candidate.Register()) filter = candidate;
        else options.Log?.Invoke("solidworks: couldn't register the message filter; a busy SolidWorks fails calls at once");
    }

    private void Discover()
    {
        if (disposed) return;
        IReadOnlyList<SolidWorksProcesses.Candidate> found;
        try
        {
            found = options.Candidates is { } candidates
                ? candidates().Where(SolidWorksProcesses.Alive).Select(SolidWorksProcesses.Inspect).ToList()
                : SolidWorksProcesses.Find(SolidWorksProcesses.ProcessName());
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            options.Log?.Invoke("solidworks: couldn't look for SolidWorks: " + error.Message);
            return;
        }
        var alive = found.Select(c => c.Pid).ToHashSet();
        // Sessions whose process is gone (closed or killed), or that let go already (SolidWorks
        // said it is closing): released quietly, and a closing one is not attached to again.
        foreach (var (pid, session) in sessions.ToList())
        {
            var running = alive.Contains(pid) && SolidWorksProcesses.Alive(pid);
            if (running && session.State != LinkSession.Phase.Gone) continue;
            session.Release(running ? "SolidWorks closed" : "SolidWorks is gone");
            sessions.Remove(pid);
            if (running) closing.Add(pid);
        }
        foreach (var pid in refused.Where(p => !alive.Contains(p)).ToList())
        {
            refused.Remove(pid);
            records.Writer.TryWrite(new LinkDetached(pid, "SolidWorks closed"));
        }
        closing.IntersectWith(alive);
        foreach (var candidate in found)
        {
            if (sessions.ContainsKey(candidate.Pid) || refused.Contains(candidate.Pid) || closing.Contains(candidate.Pid)) continue;
            switch (candidate.Owner)
            {
                case SolidWorksProcesses.Owner.Elevated:
                    refused.Add(candidate.Pid);
                    records.Writer.TryWrite(new LinkRefused(candidate.Pid, "started as administrator"));
                    options.Log?.Invoke($"solidworks: pid {candidate.Pid} runs as administrator; not linked");
                    continue;
                case SolidWorksProcesses.Owner.Other:
                    continue;
            }
            sessions[candidate.Pid] = new LinkSession(candidate.Pid, candidate.ExecutablePath, context);
        }
        foreach (var session in sessions.Values.ToList())
        {
            if (session.State is LinkSession.Phase.Finding or LinkSession.Phase.Starting) session.Tick();
            if (session.State == LinkSession.Phase.Gone)
            {
                // It said it is closing (DestroyNotify) or the connection broke: not attached to
                // again while the process finishes.
                closing.Add(session.Pid);
                sessions.Remove(session.Pid);
            }
        }
    }

    // The session that has this document open (the newest when several do).
    private LinkSession? SessionWith(string fullPath)
        => sessions.Values.Where(s => s.State == LinkSession.Phase.Attached).OrderByDescending(s => s.Pid).FirstOrDefault(s => s.IsOpen(fullPath))
           ?? sessions.Values.FirstOrDefault(s => s.State == LinkSession.Phase.Attached);

    // ---- The end --------------------------------------------------------------------------------------

    // Armory quits (or the vault runtime stops): every session releases everything and puts the
    // student's own Save to Version back; then the thread stops (the message filter revoked).
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            await thread.InvokeAsync(() =>
            {
                foreach (var session in sessions.Values) session.Release("Armory quit");
                sessions.Clear();
                return true;
            }).WaitAsync(TimeSpan.FromSeconds(20));
        }
        catch (Exception error) when (error is TimeoutException or TaskCanceledException) { options.Log?.Invoke("solidworks: releasing SolidWorks took too long"); }
        await Task.Run(() => thread.Stop(TimeSpan.FromSeconds(5)));
        records.Writer.TryComplete();
        thread.Dispose();
    }
}
