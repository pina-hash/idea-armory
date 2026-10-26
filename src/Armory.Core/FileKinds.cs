namespace Armory.Core;

// What kind of file a name is, for the window's row icon and its one-word chip (0.5.0, ledger
// 0437: "file icons must support all the types of files we use"). One table, here; the window's
// app.js carries the same table as FILE_KINDS and a test holds the two equal, and every kind has
// a drawing in wwwroot/index.html (i-kind-<kind>, made by tools/agent-icon/make_file_icons.py).
// A picture Windows can make of the file (ShellThumbnails) still wins over the icon.
//
// The extensions are the ones the IDEA lab uses (the queue's list) and every one the tree itself
// names (test files, docs, the server schema). Anything else is the generic file.
public static class FileKinds
{
    public const string Generic = "file";

    // kind -> (the word a student knows, its extensions), in the order the window's legend lists them.
    public static IReadOnlyList<(string Kind, string Word, string[] Extensions)> Kinds { get; } =
    [
        ("part", "Part", [".sldprt"]),
        ("asm", "Assembly", [".sldasm"]),
        ("drw", "Drawing", [".slddrw"]),
        ("template", "Drawing template", [".slddrt", ".drwdot", ".prtdot", ".asmdot"]),
        ("feature", "Library feature", [".sldlfp"]),
        ("block", "Block", [".sldblk"]),
        ("edrawing", "eDrawings", [".eprt", ".easm", ".edrw"]),
        ("cad", "CAD exchange", [".step", ".stp", ".iges", ".igs", ".x_t", ".x_b", ".sat", ".parasolid"]),
        ("mesh", "Mesh", [".stl", ".3mf", ".obj", ".ply", ".amf"]),
        ("print", "Print file", [".gcode", ".bgcode", ".ufp"]),
        ("vector", "Drawing for cutting", [".dxf", ".dwg", ".svg", ".ai", ".eps"]),
        ("laser", "Laser job", [".lbrn", ".lbrn2", ".xcs"]),
        ("pdf", "PDF", [".pdf"]),
        ("doc", "Document", [".docx", ".doc", ".rtf", ".odt"]),
        ("sheet", "Spreadsheet", [".xlsx", ".xls", ".csv", ".ods"]),
        ("slides", "Slides", [".pptx", ".ppt", ".odp"]),
        ("text", "Text", [".txt", ".md", ".log", ".json", ".ini", ".html", ".htm"]),
        ("code", "Code", [".py", ".cpp", ".h", ".java", ".ino", ".cs", ".js"]),
        ("image", "Picture", [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".heic"]),
        ("video", "Video", [".mp4", ".mov", ".avi", ".mkv", ".wmv"]),
        ("archive", "Archive", [".zip", ".7z", ".rar"]),
        (Generic, "File", []),
    ];

    private static readonly Dictionary<string, string> ByExtension = Build();

    private static Dictionary<string, string> Build()
    {
        var table = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (kind, _, extensions) in Kinds)
            foreach (var extension in extensions)
                if (!table.TryAdd(extension, kind)) throw new InvalidOperationException($"{extension} is listed twice.");
        return table;
    }

    // Every extension the table knows, lowercased with its dot, mapped to its kind.
    public static IReadOnlyDictionary<string, string> Table => ByExtension;

    // The kind of a file name or path ("Plate.SLDPRT" is "part"); the generic file for anything
    // else, a name with no extension, a dot file, or a trailing dot.
    public static string KindOf(string? name)
    {
        if (string.IsNullOrEmpty(name)) return Generic;
        var slash = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));
        var leaf = name[(slash + 1)..];
        var dot = leaf.LastIndexOf('.');
        if (dot <= 0 || dot == leaf.Length - 1) return Generic;
        return ByExtension.TryGetValue(leaf[dot..], out var kind) ? kind : Generic;
    }

    // The word for a kind ("Part", "Laser job"); "File" for one the table doesn't know.
    public static string WordOf(string kind)
    {
        foreach (var (k, word, _) in Kinds) if (k == kind) return word;
        return "File";
    }
}
