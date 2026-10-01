using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using Armory.Core;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Armory.Agent.Tests;

public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute() { if (!OperatingSystem.IsWindows()) Skip = "Real NTFS, Win32 and child-process test; Windows only."; }
}

internal static class Repo
{
    // The repository root: the nearest folder above the test output that holds Armory.sln.
    internal static string? FindRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
            if (File.Exists(Path.Combine(folder.FullName, "Armory.sln"))) return folder.FullName;
        return null;
    }
}

// A uniquely named folder under the temp directory, removed with everything in it.
internal sealed class TempFolder : IDisposable
{
    private static readonly string Parent = Path.Combine(Path.GetTempPath(), "Armory-Agent-Tests");
    internal string Root { get; } = Path.Combine(Parent, Guid.NewGuid().ToString("N"));
    internal TempFolder() => Directory.CreateDirectory(Root);
    internal string File(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
    internal static VaultPath PathValue(string relative)
    {
        Assert.True(VaultPath.TryCreate(relative, out var path, out var problem), problem);
        return path;
    }
    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public void Dispose()
    {
        var actual = Path.GetFullPath(Root);
        if (!actual.StartsWith(Path.GetFullPath(Parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unsafe test cleanup path.");
        // A WebView2 helper process can hold its profile for a moment after the app exits.
        for (var attempt = 0; attempt < 10 && Directory.Exists(actual); attempt++)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(actual, "*", SearchOption.AllDirectories))
                    System.IO.File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(actual, recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Thread.Sleep(500); }
        }
    }
}

// Runs the real IdeaArmory.exe with a private data folder (ARMORY_DATA_DIR) and an
// unreachable site, so a test never shares a sign-in, settings, the Run entry or the
// single-instance guard with a real Armory on the same computer.
internal static class AgentExe
{
    internal static string Folder()
    {
        if (System.IO.File.Exists(Path.Combine(AppContext.BaseDirectory, "IdeaArmory.exe"))) return AppContext.BaseDirectory;
        var configuration = typeof(AgentExe).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Debug";
        var root = Repo.FindRoot() ?? throw new InvalidOperationException("Repository root not found.");
        var built = Path.Combine(root, "src", "Armory.Agent", "bin", configuration, "net10.0-windows");
        Assert.True(System.IO.File.Exists(Path.Combine(built, "IdeaArmory.exe")), "IdeaArmory.exe was not built at " + built);
        return built;
    }

    internal static Process Start(string folder, string dataFolder, params string[] arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(folder, "IdeaArmory.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = folder,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["ARMORY_DATA_DIR"] = dataFolder;
        start.Environment["ARMORY_SITE_URL"] = "http://127.0.0.1:9";
        return Process.Start(start) ?? throw new InvalidOperationException("IdeaArmory.exe did not start.");
    }

    internal static string ReadShared(string file)
    {
        if (!System.IO.File.Exists(file)) return string.Empty;
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    internal static void Stop(Process process)
    {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        process.WaitForExit(30000);
    }
}
