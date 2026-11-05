using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Armory.Telemetry;
using Microsoft.Win32;

namespace Armory.Agent;

// One active Armory install per Windows account (0.5.5, docs/agent/INSTALL-OWNERSHIP.md). The
// Windows inventory is embedded so an extracted release and every installer use exactly the same
// code. Since 0.5.8 it never stops a launch: see Run.
internal enum RetireResult { Done, Skipped, Deferred, RetiredWhileWaiting, TimedOut, Unavailable }

internal sealed record RetireOutcome(RetireResult Result, string Detail)
{
    internal static readonly RetireOutcome Done = new(RetireResult.Done, "inventory complete");
    internal static readonly RetireOutcome Skipped = new(RetireResult.Skipped, "not an installed copy");
    internal bool Ok => Result is RetireResult.Done or RetireResult.Skipped;
    internal string Words => Result switch
    {
        RetireResult.Done => "done",
        RetireResult.Skipped => "skipped (" + Detail + ")",
        RetireResult.Deferred => "deferred (" + Detail + ")",
        RetireResult.RetiredWhileWaiting => "this install was retired by another one while it waited (" + Detail + ")",
        RetireResult.TimedOut => "deferred (" + Detail + ")",
        _ => "could not run (" + Detail + ")",
    };
}

// The script's run: Finished false when it was still running when the wait ran out.
internal sealed record ScriptRun(bool Finished, int ExitCode, string Output, string Errors);

internal static class InstallRetirement
{
    internal const string FinishUpdateArgument = "armory-finish-update";

    internal static void FinishAfterUpdaterExits()
    {
        var paths = AgentPaths.Resolve();
        if (paths.IsOverridden) return;
        try
        {
            var info = new ProcessBasicInformation();
            Marshal.ThrowExceptionForHR(NtQueryInformationProcess(Process.GetCurrentProcess().Handle, 0, ref info, Marshal.SizeOf<ProcessBasicInformation>(), out _));
            using var updater = Process.GetProcessById((int)info.ParentId);
            var script = Path.Combine(Path.GetTempPath(), "Armory-after-update-" + Guid.NewGuid().ToString("N") + ".ps1");
            using (var input = typeof(InstallRetirement).Assembly.GetManifestResourceStream("Armory.AfterUpdate.ps1")!)
            using (var output = File.Create(script)) input.CopyTo(output);
            // A same-root worker is killed by Velopack's post-hook force_stop_package.
            // ShellExecute detaches the external waiter's standard handles from the hook too.
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"))
            { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetTempPath() };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-ExecutionPolicy", "Bypass", "-File", script,
                "-UpdaterPid", updater.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "-UpdaterStarted", updater.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "-Exe", Environment.ProcessPath!, "-DataFolder", paths.DataFolder, "-Argument", FinishUpdateArgument }) start.ArgumentList.Add(arg);
            using var worker = Process.Start(start);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or COMException or InvalidOperationException or IOException or UnauthorizedAccessException)
        { Save(paths, "deferred update repair: " + error.Message); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        internal IntPtr Reserved, Peb, Reserved2, Reserved3, Id, ParentId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int kind, ref ProcessBasicInformation info, int size, out int returned);

    internal static bool Enabled(AgentPaths paths, string? exe, Func<string, bool> exists)
        => !paths.IsOverridden && exe is not null &&
           (ShellIdentity.IsInstalledCopy(paths, exe, ShellIdentity.InstallFolder()) || Updates.IsVelopackCopy(exe, exists) || ShellIdentity.IsPackagedCopy(paths, exe, exists));

    // How long a launch waits for the inventory script. Never kill an uninstaller halfway
    // through its work: past this the script keeps running and holds its lock, and this launch
    // stops waiting for it.
    internal static readonly TimeSpan ScriptWait = TimeSpan.FromSeconds(90);

    // 0.5.8: never a reason for a launch to end. Before 0.5.8 this ran before the window, and any
    // way it could not finish (a lock held by another cleanup, WMI or PowerShell blocked by school
    // policy, a .ps1 in %TEMP% blocked, a slow uninstaller) ended the launch with no window and no
    // word ("Armory doesn't open", 2026-11-05). Now the first copy runs it after the window is up
    // (StartInBackground), and a second launch runs it only when another install owns the
    // single-instance guard, then hands off to that copy if it could not finish
    // (docs/agent/INSTALL-OWNERSHIP.md, "0.5.8"). callerRunning: this process already owns the
    // guard and is the survivor, so an older copy is never asked to quit with its own --quit
    // (which would reach this process's quit signal) and is closed by its full path instead.
    internal static RetireOutcome Run(AgentPaths paths, bool callerRunning = false)
    {
        if (!OperatingSystem.IsWindows() || !Enabled(paths, Environment.ProcessPath, File.Exists)) return RetireOutcome.Skipped;
        return Run(paths, () => RunScript(paths, callerRunning, ScriptWait), () => Repair(paths));
    }

    // The steps without Windows: the script's run (its exit code, output, or that it is still
    // running), then the registration repairs. Every way it can go is an outcome, never a throw.
    internal static RetireOutcome Run(AgentPaths paths, Func<ScriptRun> script, Action repairs)
    {
        try
        {
            var auditBefore = AuditLength(paths);
            var run = script();
            if (!run.Finished)
            {
                Save(paths, "deferred: retirement is still running; this launch stopped waiting for it");
                return new(RetireResult.TimedOut, "the cleanup was still running after " + (int)ScriptWait.TotalSeconds + " s");
            }
            foreach (var line in run.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                Save(paths, line);
            if (run.ExitCode != 0)
            {
                var errors = run.Errors.ReplaceLineEndings(" ").Trim();
                // The script says why in the audit file (its last line), PowerShell on stderr.
                var why = LastSaved(paths, auditBefore) ?? LastLine(run.Output) ?? (errors.Length == 0 ? "no reason given" : errors);
                Save(paths, "deferred: exit " + run.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) + (errors.Length == 0 ? "" : ": " + errors));
                return new(run.ExitCode == 2 ? RetireResult.RetiredWhileWaiting : RetireResult.Deferred,
                    "exit " + run.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) + ": " + why);
            }
            // Old uninstallers remove shared shell keys and the common Start menu link.
            repairs();
            return RetireOutcome.Done;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // A missing or blocked powershell.exe is a Win32Exception here; a resource or temp
            // folder problem an IOException. Each is a reason, never the end of a launch.
            Save(paths, "deferred: " + error.GetType().Name + ": " + error.Message);
            return new(RetireResult.Unavailable, error.GetType().Name + ": " + error.Message);
        }
    }

    private static string? LastLine(string output)
        => output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();

    private static long AuditLength(AgentPaths paths)
    {
        try { return new FileInfo(AuditFile(paths)).Exists ? new FileInfo(AuditFile(paths)).Length : 0; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return 0; }
    }

    // The audit's last line without its time, when this run wrote it (the file grew past
    // before), or null. A script that was never allowed to run wrote nothing, and an older line
    // is not its reason. Never throws.
    internal static string? LastSaved(AgentPaths paths, long before)
    {
        try
        {
            var file = AuditFile(paths);
            if (!File.Exists(file) || new FileInfo(file).Length <= before) return null;
            var last = File.ReadLines(file).LastOrDefault(line => line.Length > 0);
            if (last is null) return null;
            var text = (JsonSerializer.Deserialize<string>(last) ?? "").Trim();
            var space = text.IndexOf(' ', StringComparison.Ordinal);
            var words = space > 0 && DateTimeOffset.TryParse(text[..space], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _)
                ? text[(space + 1)..] : text;
            return words.Length == 0 ? null : Redactor.Scrub(words);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    // The embedded script under Windows PowerShell, waited for at most wait.
    private static ScriptRun RunScript(AgentPaths paths, bool callerRunning, TimeSpan wait)
    {
        var script = Path.Combine(Path.GetTempPath(), "Armory-retire-" + Guid.NewGuid().ToString("N") + ".ps1");
        try
        {
            using (var input = typeof(InstallRetirement).Assembly.GetManifestResourceStream("Armory.Retire.ps1")
                ?? throw new InvalidOperationException("The retirement script is missing from the app."))
            using (var output = File.Create(script)) input.CopyTo(output);
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                // 0.5.8: the script can outlive Armory now (it runs beside the window, and a setup
                // may close Armory meanwhile). Started in Armory's own folder it would hold that
                // folder open, and a setup could not replace it ("A file ... is in use").
                WorkingDirectory = Path.GetTempPath(),
            };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script,
                         "-CurrentExe", Environment.ProcessPath!, "-DataFolder", paths.DataFolder, "-CurrentProcessId", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) }) start.ArgumentList.Add(arg);
            if (callerRunning) start.ArgumentList.Add("-CallerRunning");
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Windows PowerShell did not start.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(wait)) return new(false, 0, "", "");
            return new(true, process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
        }
        finally
        {
            try { File.Delete(script); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    // Old uninstallers remove shared shell keys and the common Start menu link. Repaired after
    // every finished inventory, for the copy that survives.
    private static void Repair(AgentPaths paths)
    {
        var exe = Environment.ProcessPath!;
        var settings = new SettingsStore(paths.SettingsFile).Load();
        StartupRegistration.Apply(settings.StartAtSignIn, StableLauncher.For(exe, File.Exists));
        ShellIdentity.Repair(AgentPaths.AppFolder, line => Save(paths, line));
        RepairExplorerTargets(paths);
        AppsRegistration.Repair(exe, line => Save(paths, line));
    }

    // 0.5.8: the first copy's cleanup, after its window is up, on its own thread (STA: the
    // shortcut and shell repairs use COM), a few seconds after start so the window comes first.
    // Whatever happens is logged and, when it did not finish, a retireFailed incident; the
    // window and the engine go on either way.
    // stopping: Armory began to quit; a cleanup that has not started yet does not start.
    internal static Thread? StartInBackground(AgentPaths paths, AgentLog log, Func<Glitch, string?> report, TimeSpan delay, CancellationToken stopping)
    {
        if (!OperatingSystem.IsWindows() || !Enabled(paths, Environment.ProcessPath, File.Exists)) return null;
        var thread = new Thread(() =>
        {
            if (stopping.WaitHandle.WaitOne(delay)) return;
            RunAndReport(() => Run(paths, callerRunning: true), log.Info, report);
        })
        { IsBackground = true, Name = "Armory install retirement" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return thread;
    }

    // What the background thread does: never throws, logs every outcome, files one incident when
    // the cleanup did not finish.
    internal static RetireOutcome RunAndReport(Func<RetireOutcome> retire, Action<string> log, Func<Glitch, string?> report)
    {
        RetireOutcome outcome;
        try { outcome = retire(); }
        catch (Exception error) when (error is not OutOfMemoryException) { outcome = new(RetireResult.Unavailable, error.GetType().Name + ": " + error.Message); }
        try
        {
            log("retire: " + outcome.Words);
            if (!outcome.Ok) report(GlitchRules.RetireFailed(outcome.Words));
        }
        catch (Exception error) when (error is not OutOfMemoryException) { try { log("retire: could not report: " + error.Message); } catch (Exception) { } }
        return outcome;
    }

    // Fast install/update callbacks have no live flight recorder. Queue their scrubbed audit
    // under logs, then replay into the normal process's existing recorder before its first UI.
    internal static void Save(AgentPaths paths, string line)
    {
        try
        {
            Directory.CreateDirectory(paths.LogFolder);
            File.AppendAllText(AuditFile(paths), JsonSerializer.Serialize(DateTimeOffset.UtcNow.ToString("O") + " " + Redactor.Scrub(line)) + "\n");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    internal static void Replay(AgentPaths paths, AgentLog log, FlightRecorder recorder)
    {
        try
        {
            var batch = AuditFile(paths) + ".replay";
            if (!File.Exists(batch) && File.Exists(AuditFile(paths))) File.Move(AuditFile(paths), batch);
            if (!File.Exists(batch)) return;
            foreach (var line in File.ReadLines(batch))
            {
                try
                {
                    var detail = Redactor.Scrub(JsonSerializer.Deserialize<string>(line) ?? "");
                    log.Info("retire: " + detail);
                    recorder.Note("install retirement", detail);
                }
                catch (JsonException) { log.Info("retire: ignored an incomplete audit line from an interrupted cleanup"); }
            }
            File.Delete(batch);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { log.Error("could not replay install retirement", error); }
    }

    private static string AuditFile(AgentPaths paths) => Path.Combine(paths.LogFolder, "install-retirement.jsonl");

    private static void RepairExplorerTargets(AgentPaths paths)
    {
        // Restore the survivor's forwarder path without changing the current account's vault
        // filters or force-check-in permissions. The host owns the rest of this layout.
        using var classes = Registry.CurrentUser.OpenSubKey(ShellVerbs.ClassesKey, writable: true);
        if (classes is null) return;
        var todo = new Queue<string>(ShellVerbs.Roots);
        while (todo.TryDequeue(out var path))
        {
            using var key = classes.OpenSubKey(path, writable: true);
            if (key is null) continue;
            foreach (var child in key.GetSubKeyNames()) todo.Enqueue(path + "\\" + child);
            foreach (var name in key.GetValueNames())
            {
                if (name != "Icon" && !(name.Length == 0 && path.EndsWith(@"\command", StringComparison.OrdinalIgnoreCase))) continue;
                if (key.GetValue(name) is not string text || !text.StartsWith('"')) continue;
                var end = text.IndexOf('"', 1);
                if (end <= 1) continue;
                var file = Path.GetFileName(text[1..end]);
                if (!file.Equals("ArmoryShell.exe", StringComparison.OrdinalIgnoreCase) && !file.Equals("IdeaArmory.exe", StringComparison.OrdinalIgnoreCase)) continue;
                var wanted = "\"" + Path.Combine(AgentPaths.AppFolder, file) + text[end..];
                if (wanted == text) continue;
                key.SetValue(name, wanted, key.GetValueKind(name));
                Save(paths, "repointed Explorer registration " + path);
            }
        }
    }

    // A second launch or a post-install worker can retire a copy while this runtime is already
    // open. Move those events into this recorder too, without requiring another restart.
    internal static IDisposable? Watch(AgentPaths paths, AgentLog log, FlightRecorder recorder)
        => Enabled(paths, Environment.ProcessPath, File.Exists) ? new AuditWatcher(paths, log, recorder) : null;

    private sealed class AuditWatcher : IDisposable
    {
        private readonly System.Threading.Timer timer;
        private int reading;

        internal AuditWatcher(AgentPaths paths, AgentLog log, FlightRecorder recorder)
        {
            timer = new System.Threading.Timer(_ =>
            {
                if (Interlocked.Exchange(ref reading, 1) != 0) return;
                try { Replay(paths, log, recorder); }
                finally { Volatile.Write(ref reading, 0); }
            }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        }

        public void Dispose() => timer.Dispose();
    }
}
