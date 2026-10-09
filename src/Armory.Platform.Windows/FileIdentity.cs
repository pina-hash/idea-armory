namespace Armory.Platform.Windows;

// A file's NTFS identity, last-write time and size, read through a handle opened for attributes
// only, so it never conflicts with another program's sharing (SolidWorks holding a part, a
// backup reading it). What a later step compares with the scan's entry to tell that a file is
// still exactly the one that was hashed (0.3.3), and what a stale SolidWorks "~$" marker is
// remembered by.
public static class FileIdentity
{
    // Null when no file is there (nothing, or a folder); any other failure throws IOException.
    public static (string Id, DateTime LastWriteUtc, long Size)? Of(string file)
    {
        try { return NativeMethods.FileStamp(file); }
        catch (System.ComponentModel.Win32Exception error) { throw new IOException(error.Message, error); }
    }

    // Whether the file at path is still the one a scan hashed (the same id, size and last-write
    // time) and was hashed outside the window where a write can keep its time
    // (LocalChangeDetector.RacyWindow), so its hash can be used without reading it again. False
    // when it can't tell. The residual risk is the scan's own: a writer that puts back the size
    // and the last-write time of the same file (docs/platform/local-changes.md).
    public static bool StillAsHashed(string file, string fileId, long size, DateTime lastWriteUtc, DateTimeOffset hashedAt)
    {
        if ((hashedAt.UtcDateTime - lastWriteUtc).Duration() < LocalChangeDetector.RacyWindow) return false;
        try
        {
            return Of(file) is { } now && now.Id == fileId && now.Size == size && now.LastWriteUtc == lastWriteUtc;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
}
