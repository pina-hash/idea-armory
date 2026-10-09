using Armory.Agent.Engine.View;

namespace Armory.Agent.Engine;

// The window's running lines ("What Armory is doing", ActivityView.Log), 0.3.3 (feedback N2 and
// N8, docs/agent/ENGINE.md "Activity"). A run is the passes that move files one after the other
// (a long download's slices, and the units they carry): while it goes on the lines say how far it
// got ("Downloaded 600 of 1,429 files", at most every ProgressEvery), and one line says when it is
// over ("Finished: 1,429 files downloaded in 7 min."). Until 0.3.3 every slice wrote "Sync
// finished" while hundreds of files were still to come. Pausing or going offline ends a run with
// what it did so far. A step that may take a while says what it is doing ("Looking over 1,467
// files on this computer", "Asking Armory what changed") once it has taken StepQuietFor, so a quick
// pass writes nothing. The words never say "sync" (decision D5).
public sealed partial class SyncEngine
{
    internal static readonly TimeSpan ProgressEvery = TimeSpan.FromSeconds(10);
    // How long a step goes quietly before its line is written: shorter for a click's own pass,
    // whose student is waiting for it.
    private static readonly TimeSpan StepQuietFor = TimeSpan.FromSeconds(1), ActionStepQuietFor = TimeSpan.FromMilliseconds(300);
    // How often a long count says how far it got ("Read 600 of 1,400 files").
    private static readonly TimeSpan CountEvery = TimeSpan.FromSeconds(1);

    // What the run moved so far, and when it started moving (0: nothing yet).
    private int runDownloaded, runUploaded, runKept;
    private long runStarted, lastProgressLine;
    // A run of passes goes on: the loop's last pass left files for the next one or carried some
    // in flight. Its views say Armory is moving files (never "saved" between two slices: the tray
    // and the window kept flipping), and its counts stay.
    private bool continuing;

    private bool RunMoved => runDownloaded + runUploaded + runKept > 0;

    private void ResetRun()
    {
        runDownloaded = runUploaded = runKept = 0;
        runStarted = lastProgressLine = 0;
    }

    // A line of what Armory is doing, in the window at once.
    private void Line(string line)
    {
        activity.Log(line);
        RaiseActivity(now: true);
    }

    // The answer to a window action of many files (a folder's too), as a running line as well.
    private ActionResult Said(ActionResult result, bool many)
    {
        if (many && result.Message.Length > 0) Line(result.Message);
        return result;
    }

    // How far a run that goes on has got, at most every ProgressEvery.
    private void ProgressLines()
    {
        if (!RunMoved) return;
        var since = lastProgressLine != 0 ? lastProgressLine : runStarted;
        if (since != 0 && deps.Clock.GetElapsedTime(since) < ProgressEvery) return;
        var said = false;
        foreach (var (direction, verb) in new[] { (Directions.Download, "Downloaded"), (Directions.Upload, "Uploaded") })
        {
            var (done, total) = activity.Count(direction);
            if (done == 0 || done >= total) continue;
            activity.Log($"{verb} {done:N0} of {Count(total, "file", "files")}");
            said = true;
        }
        if (said) lastProgressLine = deps.Clock.GetTimestamp();
    }

    // The run is over: one line for all of it ("Finished: 1,429 files downloaded in 7 min."), or,
    // paused, how far it got ("Paused: 412 files downloaded so far.").
    private void EndRun(bool paused)
    {
        if (RunMoved)
            activity.Log(paused ? $"Paused: {RunWords()} so far." : $"Finished: {RunWords()}{RunTook()}.");
        ResetRun();
        if (paused) continuing = false;
    }

    // "1,429 files downloaded", "3 files uploaded and 1 kept copy saved".
    private string RunWords()
    {
        List<string> did = [];
        if (runDownloaded > 0) did.Add($"{Count(runDownloaded, "file", "files")} downloaded");
        if (runUploaded > 0) did.Add($"{Count(runUploaded, "file", "files")} uploaded");
        if (runKept > 0) did.Add($"{Count(runKept, "kept copy", "kept copies")} saved");
        return did.Count == 1 ? did[0] : string.Join(", ", did[..^1]) + " and " + did[^1];
    }

    // " in 7 min" for a run of a minute or more.
    private string RunTook()
    {
        if (runStarted == 0) return "";
        var took = deps.Clock.GetElapsedTime(runStarted);
        return took < TimeSpan.FromMinutes(1) ? "" : $" in {Math.Max(1, (int)Math.Round(took.TotalMinutes, MidpointRounding.AwayFromZero)):N0} min";
    }

    // A step that may take a while: its line is written once it has gone on for StepQuietFor (on
    // the engine thread, which the step leaves free while it waits for the disk or the server).
    private IDisposable Step(string line)
    {
        var step = new QuietStep();
        _ = StepLineAsync(line, step, passScope is not null ? ActionStepQuietFor : StepQuietFor);
        return step;
    }

    private async Task StepLineAsync(string line, QuietStep step, TimeSpan after)
    {
        await Task.Delay(after, CancellationToken.None);
        if (!step.Over) Line(line);
    }

    private sealed class QuietStep : IDisposable
    {
        internal bool Over;
        public void Dispose() => Over = true;
    }

    // A long count ("Read 600 of 1,400 files", "Copying 300 of 1,000 files into Intake"): said
    // once it has taken CountEvery, then at most every CountEvery, and at its end if anything was
    // said before (a quick one says nothing).
    private sealed class Tally(SyncEngine engine, int total, Func<int, string> words)
    {
        private int done;
        private bool spoke;
        private long said = engine.deps.Clock.GetTimestamp();

        internal void One()
        {
            done++;
            if (total <= 1) return;
            if (done >= total)
            {
                if (spoke && done == total) engine.Line(words(done));
                return;
            }
            if (engine.deps.Clock.GetElapsedTime(said) < CountEvery) return;
            said = engine.deps.Clock.GetTimestamp();
            spoke = true;
            engine.Line(words(done));
        }
    }
}
