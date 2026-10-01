using System.Diagnostics;
using System.Security.Principal;
using Armory.Client;
using Armory.Platform.Windows;
using Microsoft.Win32;

namespace Armory.Agent;

// HKCU\Software\Microsoft\Windows\CurrentVersion\Run "IDEA Armory" = "<exe>" --background.
internal static class StartupRegistration
{
    internal const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string ValueName = "IDEA Armory";

    internal static string Command(string exe) => "\"" + exe + "\" --background";

    internal static void Apply(bool enabled, string exe)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled)
        {
            var wanted = Command(exe);
            if (!string.Equals(key.GetValue(ValueName) as string, wanted, StringComparison.OrdinalIgnoreCase))
                key.SetValue(ValueName, wanted, RegistryValueKind.String);
        }
        else if (key.GetValue(ValueName) is not null) key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}

// Windows' "Choose your app mode": AppsUseLightTheme 1 is light, 0 is dark.
internal static class WindowsTheme
{
    internal static bool AppsUseLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return true; }
    }
}

// One agent per Windows user. A second launch asks the first to open its window and exits;
// --quit asks the first to exit cleanly. The events exist before the mutex is taken, so a
// signal from a second launch is never lost.
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex? mutex;
    private readonly EventWaitHandle show;
    private readonly EventWaitHandle quit;
    private bool owned;

    // A copy started elevated (for example by a setup run as administrator) owns names that
    // a normal launch may not open. That launch is not first, and has nothing to signal.
    private SingleInstance()
    {
        show = new EventWaitHandle(false, EventResetMode.AutoReset);
        quit = new EventWaitHandle(false, EventResetMode.AutoReset);
        owned = false;
    }

    private SingleInstance(string name)
    {
        show = new EventWaitHandle(false, EventResetMode.AutoReset, name + "-Show");
        quit = new EventWaitHandle(false, EventResetMode.AutoReset, name + "-Quit");
        mutex = new Mutex(true, name, out var created);
        if (!created)
        {
            try { owned = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { owned = true; }
        }
        else owned = true;
    }

    internal bool IsFirst => owned;
    internal WaitHandle ShowSignal => show;
    internal WaitHandle QuitSignal => quit;

    internal static string Name(AgentPaths paths)
    {
        var user = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        return @"Local\IDEA-Armory-Agent-" + user + paths.InstanceSuffix;
    }

    internal static SingleInstance Acquire(AgentPaths paths)
    {
        try { return new(Name(paths)); }
        catch (Exception error) when (error is UnauthorizedAccessException or WaitHandleCannotBeOpenedException) { return new(); }
    }

    internal void SignalShow() => show.Set();

    // Asks a running agent to quit and waits for it to let go of the mutex. True when no
    // agent is running any more.
    internal static bool RequestQuit(AgentPaths paths, TimeSpan wait)
    {
        var name = Name(paths);
        Mutex? running;
        try { if (!Mutex.TryOpenExisting(name, out running)) return true; }
        catch (UnauthorizedAccessException) { return false; }
        using (running)
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(name + "-Quit", out var quit))
                    using (quit) quit.Set();
            }
            catch (UnauthorizedAccessException) { return false; }
            try
            {
                if (!running.WaitOne(wait)) return false;
            }
            catch (AbandonedMutexException) { }
            running.ReleaseMutex();
            return true;
        }
    }

    public void Dispose()
    {
        if (owned && mutex is not null)
        {
            try { mutex.ReleaseMutex(); }
            catch (ApplicationException) { }
            owned = false;
        }
        mutex?.Dispose();
        show.Dispose();
        quit.Dispose();
    }
}

// Opens URLs and Explorer. Only http/https URLs and validated vault paths ever reach a process.
internal static class Shell
{
    internal static bool IsWebUrl(Uri uri) => uri.IsAbsoluteUri && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    internal static void OpenUrl(Uri uri)
    {
        if (!IsWebUrl(uri)) return;
        using var _ = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    internal static void OpenFolder(string folder)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = false };
        start.ArgumentList.Add(Plain(folder));
        using var _ = Process.Start(start);
    }

    // explorer.exe /select,"<full path>". The path comes only from WindowsPaths.TryResolve of
    // a valid VaultPath, which cannot contain a quote.
    internal static void SelectInExplorer(string file)
    {
        var plain = Plain(file);
        if (plain.Contains('"', StringComparison.Ordinal)) throw new ArgumentException("Unexpected quote in a vault path.", nameof(file));
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
        {
            UseShellExecute = false,
            Arguments = "/select,\"" + plain + "\"",
        };
        using var _ = Process.Start(start);
    }

    // Explorer does not understand the \\?\ prefix WindowsPaths uses for disk access.
    internal static string Plain(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return @"\\" + path[8..];
        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }
}

internal sealed class DefaultBrowserLauncher : IBrowserLauncher
{
    public void Open(Uri uri)
    {
        if (!Shell.IsWebUrl(uri)) throw new ArgumentException("Only web addresses open in the browser.", nameof(uri));
        Shell.OpenUrl(uri);
    }
}

// Resolves a path sent by the page (vault-relative, or absolute under the vault root) to a
// file inside the vault, using Armory.Core.VaultPath and WindowsPaths.TryResolve only.
internal static class VaultLocator
{
    internal static bool TryResolve(string vaultRoot, string? requested, out string? file)
    {
        file = null;
        if (string.IsNullOrWhiteSpace(requested)) return false;
        var text = requested.Trim().Replace('/', '\\');
        var root = Path.TrimEndingDirectorySeparator(vaultRoot);
        if (text.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) text = text[(root.Length + 1)..];
        else if (Path.IsPathRooted(text)) return false;
        if (!Armory.Core.VaultPath.TryCreate(text, out var path, out _, root)) return false;
        if (Armory.Platform.Windows.VaultIgnore.IsIgnored(path.Value)) return false;
        try
        {
            var paths = new WindowsPaths(root);
            if (!paths.TryResolve(path, out var resolved, out _)) return false;
            file = resolved;
            return true;
        }
        catch (IOException) { return false; }
    }
}
