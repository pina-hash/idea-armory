namespace Armory.FakeSolidWorks;

// The parts of SolidWorks' API the link calls, with the real DISPIDs (research
// addin-registration.md section 5) where they were measured and the event interfaces it fires.
internal static class Events
{
    public static readonly Guid App = new("83A33D22-37C5-11CE-BFD4-00400513BB57");
    public static readonly Guid Part = new("83A33D32-37C5-11CE-BFD4-00400513BB57");
    public static readonly Guid Assembly = new("83A33D35-37C5-11CE-BFD4-00400513BB57");
    public static readonly Guid Drawing = new("83A33D34-37C5-11CE-BFD4-00400513BB57");
    public const int DestroyNotify = 3, ActiveModelDocChangeNotify = 5, FileNewNotify2 = 12, FileOpenPostNotify = 22, FileCloseNotify = 32;

    // (FileSaveNotify, FileSaveAsNotify2, FileSavePostNotify, ModifyNotify, DestroyNotify2) per type.
    public static (Guid Iid, int Save, int SaveAs, int SavePost, int Modify, int Destroy2) Of(int docType) => docType switch
    {
        2 => (Assembly, 6, 31, 39, 21, 62),
        3 => (Drawing, 6, 23, 26, 18, 42),
        _ => (Part, 6, 26, 31, 19, 50),
    };
}

internal sealed class FakeApp : Served
{
    private static readonly Dictionary<string, int> AppMembers = new(StringComparer.Ordinal)
    {
        ["ActiveDoc"] = 1, ["Frame"] = 5, ["ExitApp"] = 6, ["CloseDoc"] = 7, ["RevisionNumber"] = 12, ["VersionHistory"] = 81, ["GetOpenDocumentByName"] = 122,
        ["GetProcessID"] = 166, ["GetDocuments"] = 273, ["StartupProcessCompleted"] = 311,
        ["GetUserPreferenceToggle"] = 1001, ["SetUserPreferenceToggle"] = 1002, ["GetUserPreferenceIntegerValue"] = 1003, ["SetUserPreferenceIntegerValue"] = 1004,
        ["CloseAndReopen"] = 1005, ["GetAddInObject"] = 1006,
    };

    internal string Revision { get; init; } = "33.5.0";
    internal long StartedAt { get; init; }
    internal List<FakeDoc> Docs { get; } = [];
    internal FakeDoc? Active { get; set; }
    internal FakeFrame Frame { get; } = new();
    internal Dictionary<int, bool> Toggles { get; } = [];
    internal Dictionary<int, int> Integers { get; } = [];
    internal Dictionary<string, string[]> Histories { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal Action<string> Say { get; init; } = Console.WriteLine;

    internal override IReadOnlyDictionary<string, int> Members => AppMembers;
    internal override IReadOnlyList<Guid> EventInterfaces => [Events.App];
    internal bool Started => Environment.TickCount64 >= StartedAt;

    internal FakeDoc? Doc(string path) => Docs.FirstOrDefault(d => string.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase));

    internal override object? Invoke(int dispid, object?[] args)
    {
        var name = AppMembers.FirstOrDefault(m => m.Value == dispid).Key ?? dispid.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Say("call " + name);
        switch (dispid)
        {
            case 1: return Active;
            case 5: return Frame;
            case 7: return true; // CloseDoc: never called by the link; the test reads "call CloseDoc"
            case 12: return Revision;
            case 81: return History(args[0] as string ?? "");
            case 122: return Doc(args[0] as string ?? "");
            case 166: return Environment.ProcessId;
            case 273: return Docs.Cast<Served>().ToArray();
            case 311: return Started;
            case 1001: return Toggles.GetValueOrDefault(Int(args[0]));
            case 1002: Toggles[Int(args[0])] = args[1] is true; return true;
            case 1003: return Integers.GetValueOrDefault(Int(args[0]));
            case 1004: Integers[Int(args[0])] = Int(args[1]); return true;
            case 1005:
                // CloseAndReopen(doc, options, out newDoc): refused while changed, unless told to discard.
                if (args[0] is not FakeDoc doc) return 1;
                var options = Int(args[1]);
                Say($"closeAndReopen {doc.Path} options={options}");
                if (doc.Changed && (options & 2) == 0) return 12; // swCloseReopenModifiedError
                doc.ReadOnly = false;
                args[2] = doc;
                return 0;
            case 1006: return null;
            default: throw new MissingMemberException(name);
        }
    }

    internal string[] History(string path) => Histories.TryGetValue(path, out var h) ? h : ["18000[2025/261]"];

    private static int Int(object? value) => value switch { int i => i, long l => (int)l, short s => s, bool b => b ? 1 : 0, _ => 0 };
}

internal sealed class FakeDoc(FakeApp app, string path, bool readOnly) : Served
{
    private static readonly Dictionary<string, int> DocMembers = new(StringComparer.Ordinal)
    {
        ["GetTitle"] = 65607, ["GetPathName"] = 65608, ["GetType"] = 65609, ["IsOpenedReadOnly"] = 65914, ["VersionHistory"] = 65922, ["SetReadOnlyState"] = 65938,
        ["GetSaveFlag"] = 65986, ["ReloadOrReplace"] = 66289, ["Extension"] = 66306,
        ["Visible"] = 2001, ["Save3"] = 2002, ["SetSaveFlag"] = 2003, ["GetLightSourceCount"] = 2004, ["GetConfigurationNames"] = 2005, ["GetConfigurationByName"] = 2006,
    };

    internal string Path { get; set; } = path;
    internal bool ReadOnly { get; set; } = readOnly;
    internal bool Changed { get; set; }
    internal bool Visible { get; set; } = true;
    // SetReadOnlyState(false) works (it does once the file's read-only bit is off).
    internal bool InPlaceWorks { get; set; } = true;
    internal FakeExtension Extension { get; } = new();
    internal int DocType => System.IO.Path.GetExtension(Path).ToLowerInvariant() switch { ".sldasm" => 2, ".slddrw" => 3, _ => 1 };

    internal override IReadOnlyDictionary<string, int> Members => DocMembers;
    internal override IReadOnlyList<Guid> EventInterfaces => [Events.Of(DocType).Iid];

    internal override object? Invoke(int dispid, object?[] args)
    {
        var name = DocMembers.FirstOrDefault(m => m.Value == dispid).Key ?? dispid.ToString(System.Globalization.CultureInfo.InvariantCulture);
        app.Say("call " + name + " " + System.IO.Path.GetFileName(Path));
        switch (dispid)
        {
            case 65607: return System.IO.Path.GetFileName(Path);
            case 65608: return Path;
            case 65609: return DocType;
            case 65914: return ReadOnly;
            case 65922: return app.History(Path);
            case 65938:
                if (args[0] is false && InPlaceWorks) ReadOnly = false;
                return !ReadOnly;
            case 65986: return Changed;
            case 66289:
                // ReloadOrReplace(ReadOnly, ReplaceFileName, DiscardChanges): refused while changed.
                app.Say($"reload {Path} discard={args[2]}");
                if (Changed && args[2] is not true) return 3; // swModifiedNotReloadedError
                ReadOnly = args[0] is true;
                return 0;
            case 66306: return Extension;
            case 2001: return Visible;
            case 2002:
                app.Say("save3 " + Path);
                return true;
            case 2003: return null;
            case 2004: return 0;
            case 2005: return new[] { "Default" };
            case 2006: return null;
            default: throw new MissingMemberException(name);
        }
    }
}

internal sealed class FakeExtension : Served
{
    private static readonly Dictionary<string, int> ExtensionMembers = new(StringComparer.Ordinal)
    {
        ["IsFutureVersion"] = 3001, ["CheckVersionCompatibility"] = 3002, ["GetRenderMaterialsCount2"] = 3003, ["GetDecalsCount"] = 3004, ["CustomPropertyManager"] = 3005,
    };
    internal override IReadOnlyDictionary<string, int> Members => ExtensionMembers;

    internal override object? Invoke(int dispid, object?[] args) => dispid switch
    {
        3001 => false,
        3002 => 0,
        3003 => 0,
        3004 => 0,
        3005 => null,
        _ => throw new MissingMemberException(dispid.ToString(System.Globalization.CultureInfo.InvariantCulture)),
    };
}

internal sealed class FakeFrame : Served
{
    private static readonly Dictionary<string, int> FrameMembers = new(StringComparer.Ordinal) { ["SetStatusBarText"] = 10 };
    internal string? Status { get; private set; }
    internal override IReadOnlyDictionary<string, int> Members => FrameMembers;
    internal override object? Invoke(int dispid, object?[] args)
    {
        if (dispid != 10) throw new MissingMemberException(dispid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Status = args[0] as string;
        return null;
    }
}
