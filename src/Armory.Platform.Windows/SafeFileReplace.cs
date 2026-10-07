using System.ComponentModel;
using System.Security.Cryptography;
using Armory.Core;

namespace Armory.Platform.Windows;

public sealed record ReplaceResult(bool Succeeded, string? Problem, int SharingViolations, int Retries)
{
    // Only on a file system without FileRenameInfoEx, where the destination's read-only bit is
    // cleared for the rename itself: the rename failed and the bit could not be put back yet.
    public bool ReadOnlyNotRestored { get; init; }
}

public sealed class SafeFileReplace : IDisposable
{
    private readonly WindowsPaths paths;
    private readonly string staging;
    private readonly FileStream ownership;
    private readonly OpenFileDetector detector = new();
    private readonly object gate = new();
    public int CleanedOrphans { get; }
    internal Action<string>? AfterStaging { get; set; }
    // Tests look at the destination after every check, immediately before the rename.
    internal Action<string>? BeforeRename { get; set; }
    // Tests force the path for a file system without FileRenameInfoEx (FAT32, exFAT, a share),
    // and count how often it ran.
    internal bool ForceRenameFallback { get; set; }
    internal int FallbackRenames { get; private set; }
    public SafeFileReplace(WindowsPaths paths)
    {
        this.paths = paths;
        staging = Path.Combine(paths.PrivateDirectory(), "downloads");
        WindowsPaths.RejectReparsePoints(staging);
        Directory.CreateDirectory(staging);
        ownership = new(Path.Combine(staging, "owner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        foreach (var orphan in Directory.EnumerateFiles(staging, "*.pending")) { DeleteStaged(orphan); CleanedOrphans++; }
    }
    // readOnly: the new bytes must never appear writable (the vault's read-only rule says this
    // file is read-only on this computer), so FILE_ATTRIBUTE_READONLY is set on the staged file
    // before the rename and travels with it. A destination that is read-only stays read-only at
    // every moment: its bit is never cleared while the bytes are staged, hashed and checked (a
    // file nobody checked out must not be writable while SolidWorks could open it), the staged
    // file takes the bit too, and the rename replaces it with FileRenameInfoEx and
    // FILE_RENAME_FLAG_IGNORE_READONLY_ATTRIBUTE. Only where the file system cannot do that is
    // the bit cleared, after the last check and immediately before MoveFileEx.
    public ReplaceResult Replace(VaultPath destination, string? expectedHash, Stream downloaded, int maxSharingRetries = 2, bool readOnly = false)
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
                if (readOnly) File.SetAttributes(temp, File.GetAttributes(temp) | FileAttributes.ReadOnly);
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
                            var targetReadOnly = (File.GetAttributes(target) & FileAttributes.ReadOnly) != 0;
                            if (targetReadOnly && !readOnly) File.SetAttributes(temp, File.GetAttributes(temp) | FileAttributes.ReadOnly);
                            BeforeRename?.Invoke(target);
                            if (!ReplaceExisting(temp, target, targetReadOnly))
                                return new(false, "The file could not be replaced, and Armory could not make it read-only again yet.", violations, retries) { ReadOnlyNotRestored = true };
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
            finally { if (File.Exists(temp)) DeleteStaged(temp); }
        }
    }
    // A failed rename throws its own error with the destination's bit in place. False only on
    // the fallback path, when the rename failed and the bit could not be put back yet.
    private bool ReplaceExisting(string temp, string target, bool targetReadOnly)
    {
        if (!targetReadOnly) { NativeMethods.Move(temp, target, replace: true); return true; }
        if (!ForceRenameFallback && NativeMethods.TryReplaceIgnoringReadOnly(temp, target)) return true;
        FallbackRenames++;
        var attributes = File.GetAttributes(target);
        File.SetAttributes(target, attributes & ~FileAttributes.ReadOnly);
        try { NativeMethods.Move(temp, target, replace: true); return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
        {
            if (TryRestoreReadOnly(target)) throw;
            return false;
        }
    }

    private static bool TryRestoreReadOnly(string file)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (File.Exists(file)) File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);
                return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                if (attempt >= 4) return false;
                Thread.Sleep(50 * (attempt + 1));
            }
        }
    }

    // A staged file may carry the read-only bit, which File.Delete refuses.
    private static void DeleteStaged(string file)
    {
        File.SetAttributes(file, FileAttributes.Normal);
        File.Delete(file);
    }
    private static bool IsSharingViolation(Exception error)
        => error is Win32Exception native ? native.NativeErrorCode is 32 or 33
            : error is IOException && (error.HResult & 0xffff) is 32 or 33;
    public void Dispose() => ownership.Dispose();
}
