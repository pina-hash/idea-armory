using System.Diagnostics;
using System.Security.Cryptography;
using Armory.Core;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Armory.Platform.Windows.Tests;

public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute() { if (!OperatingSystem.IsWindows()) Skip = "Real NTFS and Win32 integration test; Windows only."; }
}
public sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute() { if (!OperatingSystem.IsWindows()) Skip = "Real NTFS and Win32 integration test; Windows only."; }
}
internal sealed class TestVault : IDisposable
{
    private static readonly string Parent = Path.Combine(Path.GetTempPath(), "Armory-C2-Tests");
    internal string Root { get; } = Path.Combine(Parent, Guid.NewGuid().ToString("N"));
    internal WindowsPaths Paths { get; }
    internal TestVault(int maximumLength = 240)
    {
        Directory.CreateDirectory(Root);
        Paths = new(Root, maximumLength);
    }
    internal string File(string relative) => Path.Combine(Root, relative);
    internal static VaultPath PathValue(string relative = "part.txt", int maximumLength = 240)
    {
        Assert.True(VaultPath.TryCreate(relative, out var path, out var error, maxWindowsPathLength: maximumLength), error);
        return path;
    }
    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public void Dispose()
    {
        // Delete only the uniquely allocated test directory, never a caller-computed root.
        var actual = Path.GetFullPath(Root);
        if (!actual.StartsWith(Path.GetFullPath(Parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unsafe test cleanup path.");
        if (!Directory.Exists(actual)) return;
        foreach (var file in Directory.EnumerateFiles(actual, "*", SearchOption.AllDirectories))
            System.IO.File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(actual, recursive: true);
    }
}
internal sealed class ChildProcess : IDisposable
{
    internal Process Process { get; }
    internal ChildProcess(params string[] arguments)
    {
        var assembly = typeof(Armory.Probe.Program).Assembly.Location;
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        start.ArgumentList.Add(assembly);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        Process = Process.Start(start) ?? throw new InvalidOperationException("Child failed to start.");
    }
    internal async Task<string> ReadLine()
    {
        var line = await Process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
        if (line is null) throw new InvalidOperationException(await Process.StandardError.ReadToEndAsync());
        return line;
    }
    internal void Kill()
    {
        if (!Process.HasExited) Process.Kill(entireProcessTree: true);
        Assert.True(Process.WaitForExit(30000));
    }
    public void Dispose() { Kill(); Process.Dispose(); }
}
