using System.Collections.Concurrent;
using Armory.Agent.Engine.View;

namespace Armory.EndToEnd.Tests;

// Watches every view and every activity message one computer raises while a test runs (across
// restarts), not only the view a test reads at the end: a bulk add must be quiet the whole time
// (brief section 4). Each view and message that is not is recorded (an assertion inside the
// engine's own event would stop its pass), and Check fails with the first ones. Quiet means: at
// most MaxCards notice cards, each of a kind the test expects; nothing in My files (an import's
// files are not check outs, addendum 7); waiting only as ActivityView.Waiting, one count, never a
// row or a card per file (and none at all when the run stays online); at most 8 files listed as
// moving.
internal sealed class QuietWatch : IDisposable
{
    private readonly Computer computer;
    private readonly ConcurrentQueue<string> violations = new();
    private int views, activities, midPass;

    public QuietWatch(Computer computer, int maxCards, bool online, params string[] kinds)
    {
        this.computer = computer;
        MaxCards = maxCards;
        Online = online;
        Kinds = kinds;
        computer.Views += OnView;
        computer.Activities += OnActivity;
    }

    public int MaxCards { get; }
    public bool Online { get; }
    public string[] Kinds { get; }
    public int ViewsSeen => Volatile.Read(ref views);
    public int ActivitiesSeen => Volatile.Read(ref activities);
    // Views built while files were still moving (the status line said what was moving).
    public int MidPassViews => Volatile.Read(ref midPass);

    private void OnView(AgentView view)
    {
        var n = Interlocked.Increment(ref views);
        if (view.Sync.State == SyncStates.Syncing) Interlocked.Increment(ref midPass);
        if (view.Notices.Count > MaxCards) violations.Enqueue($"view {n}: {view.Notices.Count} cards ({string.Join(", ", view.Notices.Take(5).Select(c => c.Kind + ": " + c.Title))})");
        foreach (var card in view.Notices.Where(c => !Kinds.Contains(c.Kind)))
            violations.Enqueue($"view {n}: a {card.Kind} card \"{card.Title}\"");
        if (view.MyFiles.Count > 0)
            violations.Enqueue($"view {n}: {view.MyFiles.Count} rows in My files, the first {view.MyFiles[0].Path} \"{view.MyFiles[0].Checkout.Label}\" note \"{view.MyFiles[0].Note}\"");
        Activity($"view {n}", view.Activity);
    }

    private void OnActivity(ActivityView activity)
    {
        Activity($"activity {Interlocked.Increment(ref activities)}", activity);
        if (activity.Line is { } line) lines.TryAdd(System.Text.RegularExpressions.Regex.Replace(line, @"\d[\d,.]*", "#"), 0);
    }

    // The activity lines seen, numbers replaced by "#" ("Checking in # of # files").
    private readonly ConcurrentDictionary<string, int> lines = new(StringComparer.Ordinal);
    public IReadOnlyCollection<string> Lines => lines.Keys.Order(StringComparer.Ordinal).ToArray();

    private void Activity(string where, ActivityView activity)
    {
        if (activity.Active.Count > 8) violations.Enqueue($"{where}: {activity.Active.Count} files listed as moving");
        if (Online && activity.Waiting is not null) violations.Enqueue($"{where}: waiting \"{activity.Waiting.Line}\" while online");
    }

    public void Check()
    {
        Assert.True(ViewsSeen > 0, "no view was seen");
        Assert.True(violations.IsEmpty, $"{violations.Count} views or messages were not quiet: " + string.Join(" | ", violations.Take(5)));
    }

    public void Dispose()
    {
        computer.Views -= OnView;
        computer.Activities -= OnActivity;
    }
}
