using Armory.Core;

namespace Armory.Platform.Windows;

public sealed class WindowsPaths
{
    public string Root { get; }
    public int MaximumLength { get; }
    public WindowsPaths(string root, int maximumLength = 240)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        MaximumLength = maximumLength;
        RejectReparsePoints(Root);
    }
    public bool TryResolve(string relative, out string? absolute, out string? problem)
    {
        absolute = null;
        if (!VaultPath.TryCreate(relative, out var path, out problem, Root, MaximumLength)) return false;
        return TryResolve(path, out absolute, out problem);
    }
    public bool TryResolve(VaultPath path, out string? absolute, out string? problem)
    {
        absolute = null;
        if (!path.TryToWindowsPath(out var raw, out problem, Root, MaximumLength)) return false;
        try { RejectReparsePoints(raw!); }
        catch (IOException error) { problem = error.Message; return false; }
        absolute = Extended(raw!);
        return true;
    }
    public string Resolve(VaultPath path)
        => TryResolve(path, out var absolute, out var problem) ? absolute! : throw new IOException(problem);
    public string PrivateDirectory()
    {
        var path = Path.Combine(Root, ".armory");
        RejectReparsePoints(path);
        Directory.CreateDirectory(Extended(path));
        File.SetAttributes(Extended(path), File.GetAttributes(Extended(path)) | FileAttributes.Hidden);
        return Extended(path);
    }
    public static string Extended(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal)) return full;
        return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    }
    internal static void RejectReparsePoints(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(Extended(current)) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"Reparse points are not supported in the vault path: {current}");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}

public static class VaultIgnore
{
    public static bool IsIgnored(string relative)
        => relative.Replace('\\', '/').Split('/').Any(segment =>
            segment.StartsWith("~$", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals(".armory", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase));
}
