using System.ComponentModel;
using System.Security.Cryptography;
using Armory.Core;

namespace Armory.Platform.Windows;

public sealed record ReplaceResult(bool Succeeded, string? Problem, int SharingViolations, int Retries);

public sealed class SafeFileReplace : IDisposable
{
    private readonly WindowsPaths paths;
    private readonly string staging;
    private readonly FileStream ownership;
    private readonly OpenFileDetector detector = new();
    private readonly object gate = new();
    public int CleanedOrphans { get; }
    internal Action<string>? AfterStaging { get; set; }
    public SafeFileReplace(WindowsPaths paths)
    {
        this.paths = paths;
        staging = Path.Combine(paths.PrivateDirectory(), "downloads");
        WindowsPaths.RejectReparsePoints(staging);
        Directory.CreateDirectory(staging);
        ownership = new(Path.Combine(staging, "owner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        foreach (var orphan in Directory.EnumerateFiles(staging, "*.pending")) { File.Delete(orphan); CleanedOrphans++; }
    }
    public ReplaceResult Replace(VaultPath destination, string? expectedHash, Stream downloaded, int maxSharingRetries = 2)
    {
        lock (gate)
        {
            var violations = 0;
            var retries = 0;
            var temp = Path.Combine(staging, Guid.NewGuid().ToString("N") + ".pending");
            try
            {
                using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough))
                { downloaded.CopyTo(output); output.Flush(true); }
                AfterStaging?.Invoke(temp);
                var target = paths.Resolve(destination);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        // Revalidate immediately before the native atomic rename. MoveFileEx
                        // cannot replace a destination with our own read handle still open.
                        var status = detector.Inspect(target);
                        if (status.IsOpen)
                            return new(false, "File is open" + (status.Processes.Count > 0 ? " in " + string.Join(", ", status.Processes.Select(p => p.Name)) : "."), violations, retries);
                        if (expectedHash is null)
                        {
                            // Never replace an unexpected new file in the absent-destination case.
                            NativeMethods.Move(temp, target, replace: false);
                        }
                        else
                        {
                            string actual;
                            using (var guard = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None))
                                actual = Convert.ToHexStringLower(SHA256.HashData(guard));
                            if (!StringComparer.Ordinal.Equals(actual, expectedHash)) return new(false, "Destination changed since the plan was made.", violations, retries);
                            var latestOpen = detector.Inspect(target);
                            if (latestOpen.IsOpen) return new(false, "Destination opened during revalidation.", violations, retries);
                            NativeMethods.Move(temp, target, replace: true);
                        }
                        return new(true, null, violations, retries);
                    }
                    catch (Exception error) when (IsSharingViolation(error))
                    {
                        violations++;
                        if (attempt >= maxSharingRetries) return new(false, error.Message, violations, retries);
                        retries++;
                        Thread.Sleep(25 * (attempt + 1));
                    }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
            { return new(false, error.Message, violations, retries); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
    private static bool IsSharingViolation(Exception error)
        => error is Win32Exception native ? native.NativeErrorCode is 32 or 33
            : error is IOException && (error.HResult & 0xffff) is 32 or 33;
    public void Dispose() => ownership.Dispose();
}
