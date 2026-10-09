using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    [STAThread]
    private static int Main(string[] args)
    {
        var command = AgentCommandLine.Parse(args);
        var paths = AgentPaths.Resolve();
        if (command.Check)
        {
            NativeConsole.AttachToParentIfNeeded();
            return CheckCommand.Run(paths, Console.Out);
        }
        if (command.Quit) return SingleInstance.RequestQuit(paths, TimeSpan.FromSeconds(30)) ? 0 : 1;

        using var instance = SingleInstance.Acquire(paths);
        if (!instance.IsFirst)
        {
            // A notification's link goes to the running Armory, which acts on it; if it can't be
            // reached, its window still comes up.
            if (command.Link is { } link)
            {
                if (LinkForwarder.Forward(paths, link, TimeSpan.FromSeconds(10))) return 0;
                instance.SignalShow();
                return 1;
            }
            // Already running for this Windows user: bring its window up (unless this is the
            // sign-in start entry) and leave.
            if (!command.Background) instance.SignalShow();
            return 0;
        }

        var log = new AgentLog(paths.LogFile, paths.CrashFile);
        // The flight recorder and the incident files (docs/agent/TELEMETRY.md), before anything
        // that could crash.
        var telemetry = new AgentTelemetry(paths, log);
        // A run that died without a word (a stack overflow, a native crash, the power) says so
        // in the next one, with the last thing it wrote about a pass, and becomes a crash
        // incident built from the last flight it left.
        if (log.PreviousRunEndedUnexpectedly() is { } lastWords)
        {
            log.Info("previous run ended unexpectedly; its last line was: " + lastWords);
            telemetry.PreviousRunEnded(lastWords);
        }
        else telemetry.LastFlight.Clear();
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
        // File Explorer's right-click items and a notification's second launch reach this Armory
        // over its pipe from here on, before the window or WebView2 (ShellDesk, ShellInbox). After
        // "started", so the previous run's last lines are read as it left them.
        var desk = new ShellDesk(log.Info);
        using var inbox = ShellDesk.StartInbox(paths, desk, log.Info);

        // Before any window: one identity for the taskbar, the tray and the notifications.
        if (ShellIdentity.IsInstalledCopy(paths)) ShellIdentity.SetProcessAppId();
        ApplicationConfiguration.Initialize();
        var host = new AgentHost(paths, log, AgentPaths.Site(), telemetry);
        using (var tray = new TrayApp(host, paths, log, instance, command.Background))
        {
            tray.AttachShell(desk, command.Link);
            Application.Run(tray);
        }
        telemetry.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
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
