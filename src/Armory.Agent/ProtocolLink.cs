using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Armory.Agent;

// The link a Windows notification's button or body carries (docs/agent/EXPLORER.md, "Windows
// notifications"): exactly idea-armory:act?t=<token>&a=<checkout|show|savein|keeplocal>, the scheme in any case,
// the token 22 characters of [A-Za-z0-9_-] (128 random bits, ToastTokens), at most 80 characters
// in all. It carries no path and no name: the token means something only to the Armory that
// made it, once, for 30 minutes. Anything else (idea-armory: alone, a trailing slash, quotes,
// percent signs, another parameter, an unknown action) is not a link, and only opens the window,
// so a web page or an old notification can never check anything out.
internal sealed record ProtocolLink(string Token, string Action)
{
    internal const string Scheme = "idea-armory";
    internal const string Prefix = Scheme + ":";
    internal const string CheckOut = "checkout", Show = "show";
    // The two answers to the question before a save down (SaveDownAsks): save in the project's
    // year, or keep the file on this computer only.
    internal const string SaveIn = "savein", KeepLocal = "keeplocal";
    internal const int TokenLength = 22;
    internal const int MaxLength = 80;
    // What a second launch hands over for anything that is not a link: open the window, nothing more.
    internal const string OpenOnly = Prefix;
    private const string Head = "act?t=";
    private const string ActionField = "&a=";

    // An argument Windows passes for a protocol activation: the scheme first, any case.
    internal static bool IsLink(string? argument) => argument is not null && argument.Trim().StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    internal static bool TryParse(string? text, out ProtocolLink? link)
    {
        link = null;
        if (text is null || text.Length > MaxLength || !text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var rest = text.AsSpan(Prefix.Length);
        if (!rest.StartsWith(Head, StringComparison.Ordinal)) return false;
        rest = rest[Head.Length..];
        if (rest.Length <= TokenLength + ActionField.Length) return false;
        var token = rest[..TokenLength];
        foreach (var c in token) if (!IsTokenChar(c)) return false;
        rest = rest[TokenLength..];
        if (!rest.StartsWith(ActionField, StringComparison.Ordinal)) return false;
        var action = rest[ActionField.Length..].ToString();
        if (!IsAction(action)) return false;
        link = new ProtocolLink(token.ToString(), action);
        return true;
    }

    internal static bool IsAction(string action) => action is CheckOut or Show or SaveIn or KeepLocal;

    internal static bool IsTokenChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '-';

    internal string Format() => Format(Token, Action);

    internal static string Format(string token, string action) => Prefix + Head + token + ActionField + action;

    // What a second launch forwards: the link as Armory writes it, or OpenOnly for anything else,
    // so nothing the browser or a script passed reaches the running Armory as it was given.
    internal static string ForForwarding(string? argument) => TryParse(argument?.Trim(), out var link) ? link!.Format() : OpenOnly;
}

// A second IdeaArmory.exe started for a link (Windows runs "<app>\IdeaArmory.exe" "<link>" when a
// notification's button is clicked) hands it to the running Armory over the shell pipe
// (ShellInbox, verb "uri") and exits. It never loads the window, WebView2 or the log. The pipe's
// server must be IdeaArmory.exe from this same folder (a test pipe, ARMORY_SHELL_PIPE with
// ARMORY_DATA_DIR, skips that check, as ArmoryShell.exe does), and it may bring its window to the
// front: the click was the person's.
[SupportedOSPlatform("windows")]
internal static class LinkForwarder
{
    private static readonly TimeSpan Attempt = TimeSpan.FromMilliseconds(500);

    // True when the running Armory took the link (0x06) within wait.
    internal static bool Forward(AgentPaths paths, string argument, TimeSpan wait)
    {
        var pipe = ShellInbox.PipeName(paths);
        var testPipe = paths.IsOverridden && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ShellInbox.PipeVariable));
        var line = ShellLine.Format(ShellVerb.Uri, ProtocolLink.ForForwarding(argument), (ulong)Environment.TickCount64);
        var watch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.None, System.Security.Principal.TokenImpersonationLevel.Identification);
                var left = wait - watch.Elapsed;
                client.Connect((int)Math.Clamp((left < Attempt ? left : Attempt).TotalMilliseconds, 1, int.MaxValue));
                if (!GetNamedPipeServerProcessId(client.SafePipeHandle, out var server)) return false;
                if (!testPipe && !IsThisArmory(server)) return false;
                AllowSetForegroundWindow(server);
                client.Write(line);
                client.Flush();
                return client.ReadByte() == ShellLine.Ack;
            }
            catch (Exception error) when (error is TimeoutException or IOException)
            {
                // Armory is still starting (the pipe comes up before its window): try again.
                if (watch.Elapsed >= wait) return false;
                Thread.Sleep(100);
            }
            catch (Exception error) when (error is UnauthorizedAccessException or Win32Exception) { return false; }
        }
    }

    // The pipe's server is IdeaArmory.exe from this copy's own folder (long names, any case).
    private static bool IsThisArmory(uint processId)
    {
        var own = Environment.ProcessPath;
        if (own is null) return false;
        var handle = OpenProcess(QueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero) return false;
        try
        {
            var buffer = new StringBuilder(32768);
            var length = buffer.Capacity;
            if (!QueryFullProcessImageNameW(handle, 0, buffer, ref length)) return false;
            return string.Equals(LongName(buffer.ToString(0, length)), LongName(own), StringComparison.OrdinalIgnoreCase);
        }
        finally { CloseHandle(handle); }
    }

    // A path with its long names (a runner's TEMP may be an 8.3 name), or as it is.
    internal static string LongName(string path)
    {
        var buffer = new StringBuilder(32768);
        var length = GetLongPathNameW(path, buffer, buffer.Capacity);
        return length > 0 && length < buffer.Capacity ? buffer.ToString(0, (int)length) : path;
    }

    private const uint QueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathNameW(string shortPath, StringBuilder longPath, int size);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
