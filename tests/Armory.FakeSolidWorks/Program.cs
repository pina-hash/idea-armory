using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Armory.FakeSolidWorks;

// A fake SolidWorks for the link's Windows platform tests (never shipped). It registers its
// application object in the Running Object Table as "SolidWorks_PID_<its pid>", answers IDispatch
// by hand, says StartupProcessCompleted only after --start-after milliseconds (2000), fires the
// application and document events with the real DISPIDs, and takes commands on stdin (a path
// with spaces in double quotes):
//   open <path> [readonly|writable] [reference]   activate <path>   modify <path>
//   save <path>   saveas <path> <new path>   close <path>   history <path> <entry;entry>
//   inplace <path> off   reject-calls-for <ms>   refs   prefs   set-pref <id> <value>   exit
// It writes one line per thing that happened on stdout ("ready <pid>", "started", "call <member>",
// "advise <iid> <cookie>", "unadvise ...", "event <name> <answer>", "refs app=<n> docs=<n>
// sinks=<n> rejected=<n>", "exited"). In "refs", app counts the strong references other processes
// hold on the application object (the Running Object Table's among them), docs the references
// anyone holds on its documents.
internal static unsafe class Program
{
    private static readonly ConcurrentQueue<string> commands = new();
    private static readonly AutoResetEvent arrived = new(false);
    private static FakeApp app = null!;
    private static readonly Filter filter = new();
    private static bool started, exiting;

    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
        {
            Console.Error.WriteLine("The fake SolidWorks runs on 64-bit Windows only.");
            return 2;
        }
        var revision = Arg(args, "--revision") ?? "33.5.0";
        var startAfter = int.TryParse(Arg(args, "--start-after"), out var ms) ? ms : 2000;
        var exit = 0;
        var thread = new Thread(() => exit = Run(revision, startAfter));
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        new Thread(ReadCommands) { IsBackground = true }.Start();
        thread.Join();
        return exit;
    }

    private static string? Arg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static void ReadCommands()
    {
        string? line;
        while ((line = Console.In.ReadLine()) is not null)
        {
            commands.Enqueue(line);
            arrived.Set();
        }
        commands.Enqueue("exit");
        arrived.Set();
    }

    private static void Say(string line) => Console.WriteLine(line);

    private static int Run(string revision, int startAfter)
    {
        app = new FakeApp { Revision = revision, StartedAt = Environment.TickCount64 + startAfter, Say = Say };
        FakeComWrappers.Write = Say;
        var filterPointer = FakeComWrappers.Instance.GetOrCreateComInterfaceForObject(filter, CreateComInterfaceFlags.None);
        var filterInterface = IntPtr.Zero;
        Marshal.QueryInterface(filterPointer, Iids.IMessageFilter, out filterInterface);
        CoRegisterMessageFilter(filterInterface, out var previousFilter);
        var registration = Register();
        if (registration == 0) return 3;
        // The strong connections the registration itself holds: what is left once every client let go.
        var registered = Volatile.Read(ref app.Connections);
        Say("ready " + Environment.ProcessId);
        var handles = new[] { arrived.SafeWaitHandle.DangerousGetHandle() };
        while (!exiting)
        {
            if (!started && app.Started)
            {
                started = true;
                Say("started");
            }
            while (commands.TryDequeue(out var command))
            {
                try { Handle(command); }
                catch (Exception error) { Say("error " + command + ": " + error.GetType().Name + " " + error.Message); }
                if (exiting) break;
            }
            if (exiting) break;
            MsgWaitForMultipleObjectsEx(1, handles, 50, 0x04FF, 0x0004);
            Pump();
        }
        // SolidWorks closing: DestroyNotify told the link; give it a moment to let go of its sinks
        // and then of the application (that release comes after its last Unadvise).
        var until = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < until && (Sinks() > 0 || Volatile.Read(ref app.Connections) > registered))
        {
            MsgWaitForMultipleObjectsEx(0, [], 20, 0x04FF, 0x0004);
            Pump();
        }
        Refs();
        Revoke(registration);
        CoRegisterMessageFilter(previousFilter, out _);
        Say("exited");
        return 0;
    }

    private static void Pump()
    {
        while (PeekMessageW(out var message, IntPtr.Zero, 0, 0, 1))
        {
            TranslateMessage(ref message);
            DispatchMessageW(ref message);
        }
    }

    private static void Handle(string command)
    {
        var (verb, path, rest) = Parse(command);
        if (verb.Length == 0) return;
        switch (verb)
        {
            case "open":
            {
                // A reference (a part an assembly loaded) has no window of its own.
                var reference = rest.Contains("reference", StringComparison.Ordinal);
                var doc = new FakeDoc(app, path, !rest.Contains("writable", StringComparison.Ordinal)) { Visible = !reference };
                app.Docs.Add(doc);
                if (!reference)
                {
                    app.Active = doc;
                    FireApp("ActiveModelDocChangeNotify", Events.ActiveModelDocChangeNotify);
                }
                FireApp("FileOpenPostNotify", Events.FileOpenPostNotify, path);
                break;
            }
            case "activate":
                app.Active = app.Doc(path);
                FireApp("ActiveModelDocChangeNotify", Events.ActiveModelDocChangeNotify);
                break;
            case "modify" when app.Doc(path) is { } doc:
                doc.Changed = true;
                FireDoc(doc, "ModifyNotify", Events.Of(doc.DocType).Modify);
                break;
            case "save" when app.Doc(path) is { } doc:
                FireDoc(doc, "FileSaveNotify", Events.Of(doc.DocType).Save, doc.Path);
                File.AppendAllText(doc.Path, "saved by the fake SolidWorks\n");
                doc.Changed = false;
                FireDoc(doc, "FileSavePostNotify", Events.Of(doc.DocType).SavePost, 1, doc.Path);
                break;
            case "saveas" when app.Doc(path) is { } doc:
            {
                var target = Unquote(rest);
                FireDoc(doc, "FileSaveAsNotify2", Events.Of(doc.DocType).SaveAs, target);
                File.WriteAllText(target, "saved as by the fake SolidWorks\n");
                doc.Path = target;
                doc.Changed = false;
                FireDoc(doc, "FileSavePostNotify", Events.Of(doc.DocType).SavePost, 2, target);
                break;
            }
            case "close" when app.Doc(path) is { } doc:
                FireDoc(doc, "DestroyNotify2", Events.Of(doc.DocType).Destroy2, 0);
                FireApp("FileCloseNotify", Events.FileCloseNotify, doc.Path, 0);
                app.Docs.Remove(doc);
                if (ReferenceEquals(app.Active, doc)) app.Active = app.Docs.LastOrDefault();
                break;
            case "history":
                app.Histories[path] = rest.Split(';', StringSplitOptions.RemoveEmptyEntries);
                Say("history " + path);
                break;
            case "inplace" when app.Doc(path) is { } doc:
                doc.InPlaceWorks = rest != "off";
                Say("inplace " + doc.InPlaceWorks);
                break;
            case "reject-calls-for":
                Interlocked.Exchange(ref filter.RejectUntil, Environment.TickCount64 + int.Parse(path, System.Globalization.CultureInfo.InvariantCulture));
                Say("rejecting");
                break;
            case "refs":
                Refs();
                break;
            case "prefs":
                Say("prefs " + string.Join(",", app.Toggles.OrderBy(t => t.Key).Select(t => $"t{t.Key}={t.Value}")) + " " +
                    string.Join(",", app.Integers.OrderBy(t => t.Key).Select(t => $"i{t.Key}={t.Value}")));
                break;
            case "set-pref":
                // set-pref <toggle> <bool> or set-pref <integer> <int>: the student's own setting.
                if (rest is "true" or "false") app.Toggles[int.Parse(path, System.Globalization.CultureInfo.InvariantCulture)] = rest == "true";
                else app.Integers[int.Parse(path, System.Globalization.CultureInfo.InvariantCulture)] = int.Parse(rest, System.Globalization.CultureInfo.InvariantCulture);
                Say("set " + path);
                break;
            case "exit":
                FireApp("DestroyNotify", Events.DestroyNotify);
                exiting = true;
                break;
            default:
                Say("unknown " + command);
                break;
        }
    }

    private static string Unquote(string text) => text.Trim().Trim('"');

    // "<verb> <path> <rest>": the path in double quotes when it has spaces.
    private static (string Verb, string Path, string Remainder) Parse(string command)
    {
        var text = command.Trim();
        var space = text.IndexOf(' ');
        if (space < 0) return (text, "", "");
        var after = text[(space + 1)..].TrimStart();
        var end = after.StartsWith('"') ? after.IndexOf('"', 1) : after.IndexOf(' ');
        if (end < 0) return (text[..space], Unquote(after), "");
        var path = after.StartsWith('"') ? after[1..end] : after[..end];
        return (text[..space], path, after[(end + 1)..].Trim());
    }

    private static void FireApp(string name, int dispid, params object?[] args)
    {
        if (app.PointFor(Events.App) is not { } point) return;
        foreach (var sink in point.Sinks.Values.ToList()) Say($"event {name} {FakeComWrappers.Fire(sink, dispid, args)}");
    }

    private static void FireDoc(FakeDoc doc, string name, int dispid, params object?[] args)
    {
        if (doc.PointFor(Events.Of(doc.DocType).Iid) is not { } point) return;
        foreach (var sink in point.Sinks.Values.ToList()) Say($"event {name} {FakeComWrappers.Fire(sink, dispid, args)}");
    }

    private static int Sinks() => app.Points.Values.Sum(p => p.Sinks.Count) + app.Docs.Sum(d => d.Points.Values.Sum(p => p.Sinks.Count));

    // References others hold now (the Running Object Table's among the application's: a test
    // compares with the count before the link came), and the advised sinks.
    private static void Refs()
        => Say($"refs app={Volatile.Read(ref app.Connections)} docs={app.Docs.Sum(FakeComWrappers.References)} sinks={Sinks()} rejected={Interlocked.CompareExchange(ref filter.Rejected, 0, 0)}");

    // ---- The Running Object Table ----------------------------------------------------------------

    private static int Register()
    {
        if (GetRunningObjectTable(0, out var table) < 0) return 0;
        if (CreateItemMoniker("!", "SolidWorks_PID_" + Environment.ProcessId, out var moniker) < 0) return 0;
        var unknown = FakeComWrappers.Instance.GetOrCreateComInterfaceForObject(app, CreateComInterfaceFlags.None);
        var vtable = *(IntPtr**)table;
        var register = (delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr, IntPtr, int*, int>)vtable[3];
        int cookie;
        var hr = register(table, 1 /* ROTFLAGS_REGISTRATIONKEEPSALIVE */, unknown, moniker, &cookie);
        Marshal.Release(unknown);
        Marshal.Release(moniker);
        Marshal.Release(table);
        if (hr < 0) { Say($"error register {hr:X8}"); return 0; }
        return cookie;
    }

    private static void Revoke(int cookie)
    {
        if (GetRunningObjectTable(0, out var table) < 0) return;
        var vtable = *(IntPtr**)table;
        var revoke = (delegate* unmanaged[Stdcall]<IntPtr, int, int>)vtable[4];
        revoke(table, cookie);
        Marshal.Release(table);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [DllImport("ole32.dll")] private static extern int GetRunningObjectTable(int reserved, out IntPtr table);
    [DllImport("ole32.dll", CharSet = CharSet.Unicode)] private static extern int CreateItemMoniker(string delimiter, string item, out IntPtr moniker);
    [DllImport("ole32.dll")] private static extern int CoRegisterMessageFilter(IntPtr filter, out IntPtr previous);
    [DllImport("user32.dll")] private static extern uint MsgWaitForMultipleObjectsEx(uint count, IntPtr[] handles, uint milliseconds, uint mask, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PeekMessageW(out Msg message, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TranslateMessage(ref Msg message);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref Msg message);
}
