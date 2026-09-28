using System.Text;
using Armory.Core;
using Armory.Platform.Windows;

namespace Armory.Probe;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine("Windows-only probe."); return 2; }
        try
        {
            if (args.Length == 0) { Console.WriteLine("Usage: Armory.Probe probes <repository> | compound <file> | move-diagnostic <scratch> | journal-writer <file> | hold <file> | stage <root> <expected-hash> | readonly-crash <root>"); return 0; }
            switch (args[0])
            {
                case "move-diagnostic":
                    Directory.CreateDirectory(args[1]);
                    foreach (var share in new[] { FileShare.Delete, FileShare.Read | FileShare.Delete, FileShare.ReadWrite | FileShare.Delete })
                    {
                        var oldFile = Path.Combine(args[1], "old.txt");
                        var newFile = Path.Combine(args[1], "new.txt");
                        File.WriteAllText(oldFile, "old");
                        File.WriteAllText(newFile, "new");
                        using var guard = new FileStream(oldFile, FileMode.Open, FileAccess.Read, share);
                        var success = NativeMethods.MoveFileExW(newFile, oldFile, 9);
                        Console.WriteLine($"share={share} success={success} error={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
                    }
                    NativeMethods.Move(Path.Combine(args[1], "new.txt"), Path.Combine(args[1], "old.txt"), true);
                    Console.WriteLine("closed succeeded");
                    break;
                case "journal-writer":
                    using (var store = new DurableJournalStore(args[1]))
                    {
                        Console.WriteLine("READY");
                        Console.Out.Flush();
                        for (var i = 0; ; i++)
                        {
                            var record = new byte[256 * 1024];
                            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(record, i);
                            record.AsSpan(4).Fill((byte)i);
                            store.Append(record);
                            Console.WriteLine(i);
                        }
                    }
                case "hold":
                    using (var file = new FileStream(args[1], FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    { Console.WriteLine("READY"); Console.Out.Flush(); Console.ReadLine(); }
                    break;
                case "stage":
                    var paths = new WindowsPaths(args[1]);
                    using (var replace = new SafeFileReplace(paths))
                    {
                        replace.AfterStaging = _ => { Console.WriteLine("STAGED"); Console.Out.Flush(); Console.ReadLine(); };
                        VaultPath.TryCreate("part.txt", out var path, out _);
                        _ = replace.Replace(path, args[2], new MemoryStream(Encoding.UTF8.GetBytes("replacement")));
                    }
                    break;
                case "readonly-crash":
                    using (var policy = new ReadOnlyPolicy(new WindowsPaths(args[1])))
                    {
                        VaultPath.TryCreate("part.txt", out var path, out _);
                        policy.Apply(path, LockOwnership.OtherPerson);
                        policy.AfterIntentPersisted = () => { Console.WriteLine("PERSISTED"); Console.Out.Flush(); Console.ReadLine(); };
                        policy.Apply(path, LockOwnership.ThisDevice);
                    }
                    break;
                case "probes": return PhaseZero.Run(args[1]);
                case "compound": Console.WriteLine(CompoundInspection.Inspect(args[1])); break;
                default: throw new ArgumentException("Unknown probe command.");
            }
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
