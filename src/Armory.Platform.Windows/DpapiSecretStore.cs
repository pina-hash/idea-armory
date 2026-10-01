using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Armory.Client;

namespace Armory.Platform.Windows;

// Secrets at rest for one Windows user (docs/agent/CLIENT.md, contract 3g). Each name is
// one file under Folder, holding a small header and a DPAPI CurrentUser blob made with an
// application entropy that also binds the blob to its name, so a file copied to another
// name or read by another Windows user does not decrypt. Writes go to a write-through temp
// file that is flushed and then renamed over the old file, so a crash leaves the old
// secret or the new one, never a torn file.
//
// A corrupted or unreadable blob (wrong user, wrong name, truncated, bad header) reads as
// null: the caller treats that exactly like "no secret", which for the session means this
// computer shows "Connect this computer" again. The bad file stays where it is until the
// next Write replaces it; Problem reports the name (never the contents). Real I/O failures
// (the file is locked, the disk is gone) still throw, because they say nothing about the
// secret itself.
public sealed partial class DpapiSecretStore : ISecretStore
{
    private const int UiForbidden = 0x1;
    private const string Extension = ".secret";
    private static readonly byte[] Header = "ARMORY-DPAPI-1\n"u8.ToArray();
    private readonly object gate = new();

    public DpapiSecretStore(string? folder = null, Action<string>? problem = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI is Windows only.");
        Folder = Path.GetFullPath(folder ?? DefaultFolder);
        Problem = problem;
    }

    public static string DefaultFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify), "IDEA Armory", "secrets");

    public string Folder { get; }
    private Action<string>? Problem { get; }

    public byte[]? Read(string name)
    {
        var file = FileFor(name);
        lock (gate)
        {
            byte[] stored;
            try { stored = File.ReadAllBytes(file); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            if (stored.Length <= Header.Length || !stored.AsSpan(0, Header.Length).SequenceEqual(Header))
            {
                Problem?.Invoke($"Secret '{name}' has an unknown format and was treated as missing.");
                return null;
            }
            var plain = Unprotect(stored.AsSpan(Header.Length).ToArray(), Entropy(name));
            if (plain is null) Problem?.Invoke($"Secret '{name}' could not be decrypted for this Windows user and was treated as missing.");
            return plain;
        }
    }

    public void Write(string name, byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var file = FileFor(name);
        var blob = Protect(value, Entropy(name));
        lock (gate)
        {
            Directory.CreateDirectory(Folder);
            var temp = Path.Combine(Folder, $"{name}.{Guid.NewGuid():N}.pending");
            try
            {
                using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    output.Write(Header);
                    output.Write(blob);
                    output.Flush(true);
                }
                NativeMethods.Move(temp, file, replace: true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }

    public void Delete(string name)
    {
        var file = FileFor(name);
        lock (gate)
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    // Names become file names, so only a small safe alphabet is accepted.
    private string FileFor(string name)
    {
        if (name is null || !SafeName().IsMatch(name)) throw new ArgumentException("A secret name is 1-64 letters, digits, '-', '_' or '.', and starts with a letter or digit.", nameof(name));
        return Path.Combine(Folder, name + Extension);
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex SafeName();

    private static byte[] Entropy(string name) => Encoding.UTF8.GetBytes("IDEA Armory secret store v1/" + name);

    private static byte[] Protect(byte[] plain, byte[] entropy)
    {
        var input = GCHandle.Alloc(plain, GCHandleType.Pinned);
        var salt = GCHandle.Alloc(entropy, GCHandleType.Pinned);
        try
        {
            var data = new DataBlob { Size = plain.Length, Data = input.AddrOfPinnedObject() };
            var extra = new DataBlob { Size = entropy.Length, Data = salt.AddrOfPinnedObject() };
            if (!CryptProtectData(ref data, "IDEA Armory", ref extra, IntPtr.Zero, IntPtr.Zero, UiForbidden, out var output))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not protect the Armory secret.");
            return TakeAndFree(output, clear: false);
        }
        finally { input.Free(); salt.Free(); }
    }

    private static byte[]? Unprotect(byte[] blob, byte[] entropy)
    {
        var input = GCHandle.Alloc(blob, GCHandleType.Pinned);
        var salt = GCHandle.Alloc(entropy, GCHandleType.Pinned);
        try
        {
            var data = new DataBlob { Size = blob.Length, Data = input.AddrOfPinnedObject() };
            var extra = new DataBlob { Size = entropy.Length, Data = salt.AddrOfPinnedObject() };
            return CryptUnprotectData(ref data, IntPtr.Zero, ref extra, IntPtr.Zero, IntPtr.Zero, UiForbidden, out var output)
                ? TakeAndFree(output, clear: true) : null;
        }
        finally { input.Free(); salt.Free(); }
    }

    private static byte[] TakeAndFree(DataBlob blob, bool clear)
    {
        try
        {
            var bytes = new byte[blob.Size];
            if (blob.Size > 0) Marshal.Copy(blob.Data, bytes, 0, blob.Size);
            // Plaintext returned by Windows is wiped before its buffer goes back to the heap.
            if (clear && blob.Size > 0) Marshal.Copy(new byte[blob.Size], 0, blob.Data, blob.Size);
            return bytes;
        }
        finally { if (blob.Data != IntPtr.Zero) LocalFree(blob.Data); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        internal int Size;
        internal IntPtr Data;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
