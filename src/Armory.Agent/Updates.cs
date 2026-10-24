using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Velopack;
using Velopack.Sources;

namespace Armory.Agent;

// Armory updates itself (0.4.0, Velopack; docs/agent/INSTALL.md "Updates" and the README's
// "Publishing a release"). Every copy installed by IDEA-Armory-Setup (Velopack's setup, per user,
// %LOCALAPPDATA%\IDEAArmory\current\IdeaArmory.exe) looks for a newer release on GitHub when it
// starts and every few hours after, downloads it in the background, and the next start runs it:
// no reinstall, no administrator, no paid code signing (an update is never a downloaded .exe a
// person opens, so SmartScreen never asks). A copy from the 0.4.0 flash drive or the older
// IDEA-Armory-Setup (Inno Setup, %LOCALAPPDATA%\Programs\IDEA Armory) moves itself onto the
// updating install the first time it finds a newer release: it downloads IDEA-Armory-Setup and
// runs it silently, and that setup retires the old copy (LegacyInstall). Copies older than 0.4.0
// have none of this: each needs 0.4.0 installed once, by hand.
//
// The feed is GitHub's "latest release" download address, never the GitHub API, so every lab
// computer behind the school's one address can look as often as it likes (the API allows 60
// unsigned calls an hour per address).
internal static class Updates
{
    internal const string PackId = "IDEAArmory";
    internal const string Repository = "https://github.com/pina-hash/idea-armory";
    internal static readonly Uri Feed = new(Repository + "/releases/latest/download/");
    // Velopack names its setup <packId>-win-Setup.exe.
    internal const string SetupName = PackId + "-win-Setup.exe";
    internal static readonly TimeSpan FirstLook = TimeSpan.FromMinutes(1), LookEvery = TimeSpan.FromHours(4);

    // Before anything else in Main: Velopack's install, update and uninstall steps run here and
    // exit; a downloaded update is applied here, at start, and the new version starts instead.
    internal static void RunInstallerSteps(string[] args, bool applyDownloaded)
    {
        if (!OperatingSystem.IsWindows()) return;
        VelopackApp.Build()
            .SetArgs(args)
            .SetAppUserModelId(ShellIdentity.AppId)
            .SetAutoApplyOnStartup(applyDownloaded)
            .OnAfterInstallFastCallback(_ => AfterInstall())
            .OnAfterUpdateFastCallback(_ => AfterInstall())
            .OnBeforeUninstallFastCallback(_ => BeforeUninstall())
            .Run();
    }

    // Installed (or updated) by Velopack: the older install of this account goes (it would start
    // a second Armory at sign-in), and the sign-in start entry points here. The registrations are
    // the app's own to repair at its first start (ShellIdentity.Repair). 30 seconds at most.
    [SupportedOSPlatform("windows")]
    private static void AfterInstall()
    {
        var log = SetupLog();
        try
        {
            // 0.4.3: a taskbar pin of the older copy points here before that copy is deleted
            // (TaskbarPins), so moving over never leaves a blank pin.
            if (Environment.ProcessPath is { } pinned && IsVelopackCopy(pinned, File.Exists))
                TaskbarPins.Repair(TaskbarPins.Folder(), pinned, log);
            LegacyInstall.Retire(LegacyInstall.Folder(), log);
            if (Environment.ProcessPath is { } exe && IsVelopackCopy(exe, File.Exists))
            {
                var settings = new SettingsStore(AgentPaths.Resolve().SettingsFile).Load();
                if (settings.StartAtSignIn) StartupRegistration.Apply(true, exe);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
        { log("install step: " + error.Message); }
    }

    // Uninstalled: what Armory registered for this account goes. The vault and its files never do.
    [SupportedOSPlatform("windows")]
    private static void BeforeUninstall()
    {
        var log = SetupLog();
        try
        {
            StartupRegistration.Apply(false, Environment.ProcessPath ?? "");
            ShellVerbs.Remove();
            using var classes = Registry.CurrentUser.CreateSubKey(ShellVerbs.ClassesKey, writable: true);
            classes.DeleteSubKeyTree(ShellIdentity.AppIdKey, throwOnMissingSubKey: false);
            classes.DeleteSubKeyTree(ShellIdentity.SchemeKey, throwOnMissingSubKey: false);
            log("uninstalled: the sign-in start entry, the right-click items and the notification registration were removed; the vault was left as it is");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
        { log("uninstall step: " + error.Message); }
    }

    private static Action<string> SetupLog()
    {
        var paths = AgentPaths.Resolve();
        var log = new AgentLog(paths.LogFile, paths.CrashFile);
        return line => log.Info("updates: " + line);
    }

    // A copy Velopack installed: <root>\current\IdeaArmory.exe with <root>\Update.exe beside
    // current. Windows paths, read the same on every host.
    internal static bool IsVelopackCopy(string? processPath, Func<string, bool> fileExists)
    {
        if (string.IsNullOrEmpty(processPath)) return false;
        var path = processPath.Replace('/', '\\');
        var cut = path.LastIndexOf('\\');
        if (cut <= 0 || !path[(cut + 1)..].Equals("IdeaArmory.exe", StringComparison.OrdinalIgnoreCase)) return false;
        var folder = path[..cut];
        var up = folder.LastIndexOf('\\');
        if (up <= 0 || !folder[(up + 1)..].Equals("current", StringComparison.OrdinalIgnoreCase)) return false;
        return fileExists(folder[..up] + @"\Update.exe");
    }

    // "0.4.1" (or "0.4.1-beta"): its released numbers, or null. Releases are only ever numbers.
    internal static Version? Release(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        var plain = version.Split('-', '+')[0];
        return Version.TryParse(plain, out var v) ? new Version(v.Major, v.Minor, Math.Max(v.Build, 0)) : null;
    }

    // What the updater says, in the log and the tray, for the version it found.
    internal static string ReadyWords(string version) => $"Armory {version} is ready. It starts the next time Armory opens.";
    internal static string MovingWords(string version) => $"Armory {version} is installing. Armory closes for a moment, then opens again by itself or at your next sign-in.";
}

// The looks for a newer release, while Armory runs. Never throws; every failure waits for the next look.
internal sealed class Updater : IDisposable
{
    private readonly AgentLog log;
    private readonly AgentPaths paths;
    private readonly Action<string>? ready;
    private System.Threading.Timer? timer;
    private int looking;
    private UpdateManager? manager;

    // ready: the tray's words once an update waits for the next start.
    internal Updater(AgentLog log, AgentPaths paths, Action<string>? ready = null)
    {
        this.log = log;
        this.paths = paths;
        this.ready = ready;
    }

    // The downloaded release that the next start runs, if any.
    internal string? Waiting { get; private set; }

    internal void Start()
    {
        if (!OperatingSystem.IsWindows() || paths.IsOverridden) return;
        timer = new System.Threading.Timer(_ => _ = LookAsync(), null, Updates.FirstLook, Updates.LookEvery);
    }

    internal async Task LookAsync()
    {
        if (Interlocked.Exchange(ref looking, 1) == 1) return;
        try
        {
            var exe = Environment.ProcessPath;
            if (Updates.IsVelopackCopy(exe, File.Exists)) await UpdateInPlaceAsync();
            else if (ShellIdentity.IsInstalledCopy(paths)) await MoveOntoUpdatingInstallAsync();
            // A developer's build or a test instance never updates.
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            log.Info("updates: the look for a new version failed (" + error.GetType().Name + ": " + error.Message + "); Armory looks again later");
        }
        finally { Volatile.Write(ref looking, 0); }
    }

    private async Task UpdateInPlaceAsync()
    {
        manager ??= new UpdateManager(new SimpleWebSource(Updates.Feed));
        if (!manager.IsInstalled) return;
        if (manager.UpdatePendingRestart is { } pending)
        {
            Ready(pending.Version.ToString());
            return;
        }
        var found = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
        if (found is null)
        {
            log.Info("updates: Armory " + AgentPaths.Version + " is the newest");
            return;
        }
        var version = found.TargetFullRelease.Version.ToString();
        log.Info("updates: downloading Armory " + version);
        await manager.DownloadUpdatesAsync(found).ConfigureAwait(false);
        Ready(version);
    }

    private void Ready(string version)
    {
        if (Waiting == version) return;
        Waiting = version;
        log.Info("updates: " + Updates.ReadyWords(version));
        ready?.Invoke(Updates.ReadyWords(version));
    }

    // Quitting with an update downloaded: it is applied as soon as this process ends, so the next
    // start is quick. (Windows ending the session skips this; the next start applies it then.)
    internal void ApplyOnQuit()
    {
        if (manager is not { IsInstalled: true } m || m.UpdatePendingRestart is not { } pending) return;
        try
        {
            m.WaitExitThenApplyUpdates(pending, silent: true, restart: false);
            log.Info("updates: Armory " + pending.Version + " is applied as Armory closes");
        }
        catch (Exception error) when (error is not OutOfMemoryException) { log.Info("updates: could not apply it at quit (" + error.Message + "); the next start does"); }
    }

    // A copy installed the old way (flash drive or the Inno Setup installer): when the latest
    // release is newer, its setup is downloaded and run silently; it closes this copy, installs
    // the updating one, retires this one and starts the new one.
    private async Task MoveOntoUpdatingInstallAsync()
    {
        using var http = AgentHost.Http(TimeSpan.FromMinutes(10));
        var feed = await http.GetStringAsync(new Uri(Updates.Feed, "releases.win.json")).ConfigureAwait(false);
        var latest = VelopackAssetFeed.FromJson(feed).Assets
            .Where(a => a.PackageId.Equals(Updates.PackId, StringComparison.OrdinalIgnoreCase))
            .Select(a => Updates.Release(a.Version?.ToString())).Where(v => v is not null).Max();
        if (latest is null || Updates.Release(AgentPaths.Version) is not { } mine || latest <= mine) return;
        var setup = Path.Combine(Path.GetTempPath(), "IDEA Armory update", Updates.SetupName);
        Directory.CreateDirectory(Path.GetDirectoryName(setup)!);
        log.Info("updates: downloading " + Updates.SetupName + " for Armory " + latest + ", to move onto the install that updates itself");
        using (var source = await http.GetStreamAsync(new Uri(Updates.Feed, Updates.SetupName)).ConfigureAwait(false))
        await using (var target = File.Create(setup))
            await source.CopyToAsync(target).ConfigureAwait(false);
        log.Info("updates: " + Updates.MovingWords(latest.ToString()));
        ready?.Invoke(Updates.MovingWords(latest.ToString()));
        Process.Start(new ProcessStartInfo(setup, "--silent") { UseShellExecute = false });
    }

    public void Dispose() => timer?.Dispose();
}

// The install before 0.4.0's updating one (docs/agent/INSTALL.md): %LOCALAPPDATA%\Programs\IDEA
// Armory, its Apps entries and its Start menu shortcut. Retired once Velopack's setup has
// installed this account's updating copy: the old copy is asked to quit, then its files and
// entries go. The vault, settings and sign-in (%LOCALAPPDATA%\IDEA Armory) are never touched.
internal static class LegacyInstall
{
    // The Apps entries of the flash drive and of the Inno Setup installer.
    internal static readonly string[] UninstallKeys =
    [
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\IDEA Armory",
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{28A1D010-82E3-4294-9676-83AC0AA1F5D3}_is1",
    ];
    private static readonly TimeSpan QuitWait = TimeSpan.FromSeconds(12);

    internal static string Folder() => ShellIdentity.InstallFolder();

    [SupportedOSPlatform("windows")]
    internal static void Retire(string folder, Action<string> log)
    {
        var exe = Path.Combine(folder, "IdeaArmory.exe");
        if (!File.Exists(exe)) return;
        // The old copy, if it runs, quits cleanly first (the same request "--quit" makes).
        try
        {
            using var quit = Process.Start(new ProcessStartInfo(exe, "--quit") { UseShellExecute = false, CreateNoWindow = true });
            if (quit is not null && !quit.WaitForExit(QuitWait)) log("the older Armory did not quit within " + QuitWait.TotalSeconds + " s");
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { log("could not ask the older Armory to quit: " + error.Message); }
        foreach (var key in UninstallKeys)
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false); }
            catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException) { log("could not remove " + key + ": " + error.Message); }
        }
        var shortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs, Environment.SpecialFolderOption.DoNotVerify), ShellIdentity.ShortcutName);
        try
        {
            if (File.Exists(shortcut) && ShortcutAppId.Target(shortcut) is { } target && string.Equals(Path.GetFullPath(target), Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase))
                File.Delete(shortcut);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or InvalidCastException) { log("could not remove the older Start menu shortcut: " + error.Message); }
        try
        {
            Directory.Delete(folder, recursive: true);
            log("retired the older install at " + folder);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { log("could not remove every file of the older install at " + folder + ": " + error.Message); }
    }
}
