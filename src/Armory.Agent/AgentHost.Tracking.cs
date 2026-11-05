using Armory.Agent.Engine.View;
using Armory.Client;
using Armory.Core;

namespace Armory.Agent;

// Desk tracking (0.5.7; idea-app ledger 0521, docs/agent/TRACKING.md). About every 30 seconds,
// on its own task, never inside a sync pass:
//   1. what this computer saw of each team file (the engine's DeskEvents) goes to the website
//      (armory_file_events), so the Checked out view says who has a file open and since when;
//   2. once an hour, the site admin's check out rules (armory_checkout_rules) and today's class
//      period ends (<site>/api/armory/bells) are read again;
//   3. from five minutes before a bell to ten after, once per bell, a student with files checked
//      out here is asked to check them in (a Windows notification with a Check in button, the
//      tray's NudgeDue), and the website is told they were asked;
//   4. where the site admin switched on automatic check in, a file checked out here, closed and
//      untouched for that long is checked in, and the student is told in one sentence.
// A website without 0370 answers 404 PGRST202: nothing is queued for it and it is asked again in
// six hours. Every failure is logged once per kind and never touches a pass.
internal sealed partial class AgentHost
{
    private static readonly TimeSpan DeskEvery = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DeskRulesEvery = TimeSpan.FromHours(1);
    private static readonly TimeSpan DeskNotLiveRetry = TimeSpan.FromHours(6);
    private DateTimeOffset deskNotLiveUntil = DateTimeOffset.MinValue;
    private DateTimeOffset deskRulesAt = DateTimeOffset.MinValue;
    private SiteCheckoutRules? deskRules;
    private IReadOnlyList<DateTimeOffset> deskBells = [];
    private DateTimeOffset? deskLastBell;
    private string? deskProblem;
    // A file this computer already checked in by itself for this check out (its id and since).
    private readonly HashSet<string> deskAutoTried = new(StringComparer.Ordinal);

    // The end-of-period reminder: the bell and the files checked out here. The tray shows it.
    internal event Action<DateTimeOffset, IReadOnlyList<MyFileView>>? NudgeDue;

    private void StartDeskTracking()
    {
        var token = running.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try { await DeskTickAsync(token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (Exception error) when (error is not OutOfMemoryException) { DeskProblem("tick", error.GetType().Name + ": " + error.Message); }
                try { await Task.Delay(DeskEvery, parts.Clock, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }, token);
    }

    internal async Task DeskTickAsync(CancellationToken ct)
    {
        var engine = Volatile.Read(ref runtime)?.Engine;
        if (engine is null || Sessions.Current is not { } session || PickerShowing) return;
        var now = parts.Clock.GetUtcNow();
        if (now - deskRulesAt >= DeskRulesEvery)
        {
            deskRulesAt = now;
            deskRules = await Api.CheckoutRulesAsync(ct).ConfigureAwait(false);
            deskBells = await PeriodEnds.FetchAsync(siteHttp, site, ct).ConfigureAwait(false);
        }
        var view = engine.View;
        var mine = view.Connection == Connections.SignedIn
            ? view.MyFiles.Where(f => f.Checkout.State == CheckoutStates.Mine && Guid.TryParse(f.FileId, out _)).ToArray()
            : [];
        if (PeriodNudge.Due(deskBells, now, deskLastBell) is { } bell)
        {
            deskLastBell = bell;
            if (mine.Length > 0)
            {
                log.Info($"desk: end of period, {mine.Length} file(s) checked out here");
                foreach (var f in mine) engine.DeskEvents.Add(Guid.Parse(f.FileId!), DeskEventKind.Nudged, now);
                NudgeDue?.Invoke(bell, mine);
            }
        }
        if (deskRules?.AutoCheckInIdleMinutes is { } idle)
        {
            List<MyFileView> due = [];
            foreach (var f in mine)
            {
                var id = Guid.Parse(f.FileId!);
                if (!DateTimeOffset.TryParse(f.Checkout.Since, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal, out var since)) continue;
                var key = id.ToString("N") + since.ToUnixTimeSeconds();
                if (deskAutoTried.Contains(key) || f.Status == FileStatuses.CheckingInWhenClosed) continue;
                if (AutoCheckIn.Due(idle, since, engine.DeskEvents.LastTouched(id), engine.DeskEvents.IsOpen(id), now)) { deskAutoTried.Add(key); due.Add(f); }
            }
            if (due.Count > 0)
            {
                var result = await CheckInAsync(due.Select(f => f.Path).ToArray()).ConfigureAwait(false);
                log.Info($"desk: automatic check in of {due.Count} idle file(s): " + (result.Ok ? "done" : "refused"));
                if (result.Ok) Notice(DeskWords.AutoCheckedIn(due.Select(f => f.Name).ToArray(), idle));
            }
        }
        await DeskSendAsync(engine.DeskEvents, session.DeviceId, now, ct).ConfigureAwait(false);
    }

    private async Task DeskSendAsync(DeskEventQueue queue, Guid device, DateTimeOffset now, CancellationToken ct)
    {
        if (now < deskNotLiveUntil) { queue.Clear(); return; }
        while (queue.Count > 0 && !ct.IsCancellationRequested)
        {
            var batch = queue.Take();
            try
            {
                await Api.FileEventsAsync(device, batch.Select(e => new FileEventCall(e.FileId, e.KindWord, e.At)).ToArray(), ct).ConfigureAwait(false);
                deskProblem = null;
            }
            catch (ArmoryRpcException error) when (error.IsFunctionMissing)
            {
                // A website before 0370 tracks nothing: nothing waits for it.
                deskNotLiveUntil = now + DeskNotLiveRetry;
                queue.Clear();
                DeskProblem("send", "the website does not track open files yet");
                return;
            }
            catch (Exception error) when (error is not OutOfMemoryException and not OperationCanceledException)
            {
                queue.PutBack(batch);
                DeskProblem("send", error.GetType().Name + ": " + error.Message);
                return;
            }
        }
    }

    private void DeskProblem(string where, string what)
    {
        var line = where + ": " + what;
        if (line == deskProblem) return;
        deskProblem = line;
        log.Info("desk: " + line);
    }
}

// What the student reads about desk tracking. Grade 5, the six words (words.js): Check out,
// Check in, Available.
internal static class DeskWords
{
    internal const string NudgeTitle = "Class is ending";
    internal const string CheckInButton = "Check in";

    internal static string Nudge(IReadOnlyList<string> names) => names.Count == 1
        ? $"Check in {names[0]} so your team can use it."
        : $"Check in your {names.Count} files so your team can use them.";

    internal static string AutoCheckedIn(IReadOnlyList<string> names, int minutes) => names.Count == 1
        ? $"Armory checked in {names[0]}. Nobody had touched it for {minutes} minutes."
        : $"Armory checked in {names.Count} files. Nobody had touched them for {minutes} minutes.";

    internal const string TakenBackTitle = "A file was checked in for you";
    internal static string TakenBack(string detail) => detail + " Your saved work is kept.";
}
