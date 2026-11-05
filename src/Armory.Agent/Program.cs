using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Armory.Telemetry;
using Microsoft.Web.WebView2.Core;

namespace Armory.Agent;

// IdeaArmory.exe [--background | --quit | --check]
//   --background  start in the tray without opening the window (the sign-in start entry)
//   --quit        ask the running agent to exit cleanly, wait for it, then exit
//   --check       no UI: print one JSON line and exit 0 when the app files and the WebView2
//                 runtime are present, 1 otherwise
// IdeaArmory.exe "idea-armory:act?t=<token>&a=<checkout|show>"
//   a notification's button or body (ProtocolLink): a second launch hands it to the running
//   agent and exits; a first one starts with the window open. Link keeps the argument as given.
public sealed record AgentCommandLine(bool Background, bool Quit, bool Check, string? Link = null)
{
    public static AgentCommandLine Parse(IEnumerable<string> args)
    {
        var list = args.Select(a => a.Trim()).ToArray();
        var set = new HashSet<string>(list.Select(a => a.ToLowerInvariant()), StringComparer.Ordinal);
        var link = list.FirstOrDefault(ProtocolLink.IsLink);
        // A link is a click: the window opens, even with --background beside it.
        return new(set.Contains("--background") && link is null, set.Contains("--quit"), set.Contains("--check"), link);
    }
}

internal static class Program
{
    // The first copy's install cleanup starts this long after its window, so the window comes first.
    private static readonly TimeSpan RetireAfterWindow = TimeSpan.FromSeconds(3);

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--" + InstallRetirement.FinishUpdateArgument)
            return Updates.FinishUpdate();
        var command = AgentCommandLine.Parse(args);
        // --check and --quit answer as they always have: no window, no message box, ever.
        if (command.Check || command.Quit) return CheckOrQuit(args, command);
        // 0.5.8: Armory either opens or says why (docs/agent/INSTALL-OWNERSHIP.md, "0.5.8"). Every
        // early step runs inside the guard; nobody is looking at a sign-in start or an installer
        // step, so those only write crash.log and an incident.
        var quiet = command.Background || Updates.IsInstallerStep(args);
        return StartupGuard.Run(() => Launch(args, command), quiet, StartupNotice.Show);
    }

    private static int CheckOrQuit(string[] args, AgentCommandLine command)
    {
        // Velopack's steps first, never applying a downloaded update: these answer as the
        // version that is here.
        Updates.RunInstallerSteps(args, applyDownloaded: false);
        var paths = AgentPaths.Resolve();
        if (command.Check)
        {
            NativeConsole.AttachToParentIfNeeded();
            return CheckCommand.Run(paths, Console.Out);
        }
        return SingleInstance.RequestQuit(paths, TimeSpan.FromSeconds(30)) ? 0 : 1;
    }

    private static int Launch(string[] args, AgentCommandLine command)
    {
        // Velopack's install, update and uninstall steps run (and exit) here, before anything
        // else; a downloaded update is applied here and the new version starts instead (0.4.0).
        Updates.RunInstallerSteps(args, applyDownloaded: true);
        var paths = AgentPaths.Resolve();
        return LaunchFlow.Run(new LaunchSteps
        {
            Acquire = wait => SingleInstance.Acquire(paths, wait),
            AnotherInstallOwnsTheGuard = () => InstallRetirement.Enabled(paths, Environment.ProcessPath, File.Exists) &&
                RunningArmories.AnotherInstall(Environment.ProcessPath, RunningArmories.InThisSession(), LinkForwarder.LongName),
            RetireNow = () => InstallRetirement.Run(paths),
            RetireFailed = outcome =>
            {
                LaunchLog.Write(paths, "install cleanup before handing off: " + outcome.Words);
                StartupIncidents.Save(paths, null, GlitchRules.RetireFailed(outcome.Words));
            },
            SecondLaunch = instance => SecondLaunch.Run(command, instance,
                link => LinkForwarder.Forward(paths, link, TimeSpan.FromSeconds(10)),
                () => RunningArmories.InThisSession().Select(r => r.Id).ToList(),
                (words, detail) =>
                {
                    LaunchLog.Write(paths, "could not reach the running Armory: " + detail);
                    StartupIncidents.Save(paths, null, GlitchRules.StartupBlocked(detail));
                    StartupNotice.Show(words);
                }),
            RunFirst = (instance, retireInBackground) => RunFirst(paths, command, (SingleInstance)instance, retireInBackground),
        });
    }

    private static int RunFirst(AgentPaths paths, AgentCommandLine command, SingleInstance instance, bool retireInBackground)
    {
        var log = new AgentLog(paths.LogFile, paths.CrashFile);
        // The flight recorder and the incident files (docs/agent/TELEMETRY.md), before anything
        // that could crash.
        var telemetry = new AgentTelemetry(paths, log);
        // A run that died without a word (a stack overflow, a native crash, the power) says so
        // in the next one, with the last thing it wrote about a pass, and becomes a crash
        // incident built from the last flight it left.
        var previous = log.PreviousRun();
        if (previous is { Quitting: false })
        {
            // Windows' own records say whether it restarted, lost power or saw Armory crash
            // (0.4.0): only a crash, or an end nobody recorded, is a crash incident.
            var why = RunEndCause.Explain(previous.LastAt, previous.Asleep, WindowsEventHistory.Since(previous.LastAt, log.Info), WindowsEventHistory.CurrentUser(),
                previous.UpdatedAt);
            if (why.Crash)
            {
                log.Info("previous run ended unexpectedly; its last line was: " + previous.LastLine + " (" + why.Words + ")");
                telemetry.PreviousRunEnded(previous.LastLine, why.Words, previous.VeryLastLine);
            }
            else
            {
                log.Info((previous.UpdatedAt is null ? "previous run ended with Windows, not a crash: " : "previous run ended with an update, not a crash: ") +
                    why.Words + " Its last line was: " + (previous.VeryLastLine ?? previous.LastLine));
                telemetry.LastFlight.Clear();
            }
        }
        else
        {
            // Ended while quitting (Windows ending the session, or the installer): not a crash.
            if (previous is { Quitting: true }) log.Info("previous run was stopped while it was quitting; its last line was: " + previous.LastLine);
            telemetry.LastFlight.Clear();
        }
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            log.Crash("unhandled exception", e.ExceptionObject);
            telemetry.CrashNow("unhandled exception", e.ExceptionObject);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            log.Crash("unobserved task", e.Exception);
            telemetry.Recorder.Exception("unobserved task", e.Exception);
            e.SetObserved();
        };
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            log.Crash("window thread", e.Exception);
            // The window goes on, so the incident is written off this thread.
            telemetry.Recorder.Exception("window thread", e.Exception, fatal: true);
        };
        log.Info("started " + AgentPaths.Version);
        // Install cleanup audits a second launch, an update worker or an earlier run left behind.
        if (ShellIdentity.IsInstalledCopy(paths)) InstallRetirement.Replay(paths, log, telemetry.Recorder);
        using var retirementAudits = InstallRetirement.Watch(paths, log, telemetry.Recorder);
        // File Explorer's right-click items and a notification's second launch reach this Armory
        // over its pipe from here on, before the window or WebView2 (ShellDesk, ShellInbox). After
        // "started", so the previous run's last lines are read as it left them.
        var desk = new ShellDesk(log.Info);
        using var inbox = ShellDesk.StartInbox(paths, desk, log.Info);

        // Before any window: one identity for the taskbar, the tray and the notifications.
        if (ShellIdentity.IsInstalledCopy(paths))
        {
            ShellIdentity.SetProcessAppId();
        }
        ApplicationConfiguration.Initialize();
        var host = new AgentHost(paths, log, AgentPaths.Site(), telemetry);
        using (var tray = new TrayApp(host, paths, log, instance, command.Background))
        {
            tray.AttachShell(desk, command.Link);
            // 0.5.8: the install cleanup runs now that the window is up, on its own thread. It
            // never ends this run: what it could not do is logged and a retireFailed incident.
            if (retireInBackground) InstallRetirement.StartInBackground(paths, log, telemetry.Save, RetireAfterWindow);
            Application.Run(tray);
        }
        try { telemetry.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5)); }
        catch (Exception error) when (error is not OutOfMemoryException) { log.Error("stopping telemetry", error); }
        log.Info("stopped");
        return 0;
    }
}

internal sealed record CheckResult(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("webView2Runtime")] string? WebView2Runtime,
    [property: JsonPropertyName("wwwroot")] bool Wwwroot,
    [property: JsonPropertyName("vaultRoot")] string VaultRoot);

internal static class CheckCommand
{
    internal static int Run(AgentPaths paths, TextWriter output)
    {
        var runtime = WebView2Runtime();
        var web = File.Exists(Path.Combine(AgentPaths.WebRoot, "index.html")) && File.Exists(Path.Combine(AgentPaths.WebRoot, "bridge.js"));
        var settings = new SettingsStore(paths.SettingsFile).Load();
        output.WriteLine(JsonSerializer.Serialize(new CheckResult(AgentPaths.Version, runtime, web, settings.VaultRoot)));
        output.Flush();
        return runtime is not null && web ? 0 : 1;
    }

    // Null when the runtime, WebView2Loader.dll or the WebView2 assemblies are missing.
    internal static string? WebView2Runtime()
    {
        try { return RuntimeVersion(); }
        catch (Exception error) when (error is WebView2RuntimeNotFoundException or DllNotFoundException or FileNotFoundException
            or BadImageFormatException or COMException or TypeLoadException) { return null; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string RuntimeVersion() => CoreWebView2Environment.GetAvailableBrowserVersionString();
}

// A WinExe has no console. "--check" writes to an inherited (redirected) stdout as is, and
// attaches to the parent's console when there is nothing to inherit.
internal static class NativeConsole
{
    internal static void AttachToParentIfNeeded()
    {
        var handle = GetStdHandle(-11);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1)) AttachConsole(-1);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);
}
