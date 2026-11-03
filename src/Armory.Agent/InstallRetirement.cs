using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Armory.Telemetry;

namespace Armory.Agent;

// Runs before the single-instance guard: an older install may own that guard. The Windows
// inventory is embedded so an extracted release and every installer use exactly the same code.
internal static class InstallRetirement
{
    internal const string FinishUpdateArgument = "--armory-finish-update";

    internal static void FinishAfterUpdaterExits()
    {
        try
        {
            var info = new ProcessBasicInformation();
            Marshal.ThrowExceptionForHR(NtQueryInformationProcess(Process.GetCurrentProcess().Handle, 0, ref info, Marshal.SizeOf<ProcessBasicInformation>(), out _));
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add(FinishUpdateArgument);
            start.ArgumentList.Add(info.ParentId.ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var worker = Process.Start(start);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or COMException or InvalidOperationException)
        { Save(AgentPaths.Resolve(), "deferred update repair: " + error.Message); }
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
           (ShellIdentity.IsInstalledCopy(paths, exe, ShellIdentity.InstallFolder()) || Updates.IsVelopackCopy(exe, exists));

    internal static bool Run(AgentPaths paths)
    {
        if (!OperatingSystem.IsWindows() || !Enabled(paths, Environment.ProcessPath, File.Exists)) return true;
        var script = Path.Combine(Path.GetTempPath(), "Armory-retire-" + Guid.NewGuid().ToString("N") + ".ps1");
        try
        {
            using (var input = typeof(InstallRetirement).Assembly.GetManifestResourceStream("Armory.Retire.ps1")!)
            using (var output = File.Create(script)) input.CopyTo(output);
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script,
                         "-CurrentExe", Environment.ProcessPath!, "-DataFolder", paths.DataFolder }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            // Never kill an uninstaller halfway through its work. It has its own bounded waits;
            // if it outlives this start, the next start retries the inventory under the same lock.
            if (!process.WaitForExit(TimeSpan.FromSeconds(90))) { Save(paths, "deferred: retirement is still running; this launch waits for a later retry"); return false; }
            foreach (var line in stdout.GetAwaiter().GetResult().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                Save(paths, line);
            if (process.ExitCode != 0) { Save(paths, "deferred: " + stderr.GetAwaiter().GetResult()); return false; }
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        { Save(paths, "deferred: " + error.Message); return false; }
        finally
        {
            try { File.Delete(script); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
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

    // A second launch or a post-install worker can retire a copy while this runtime is already
    // open. Move those events into this recorder too, without requiring another restart.
    internal static IDisposable? Watch(AgentPaths paths, AgentLog log, FlightRecorder recorder)
        => paths.IsOverridden ? null : new AuditWatcher(paths, log, recorder);

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
