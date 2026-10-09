using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Armory.Agent.Tests;

// T6 (docs/agent/EXPLORER.md): after ShellVerbs.Apply, Windows' own right-click menu (the one
// Explorer shows, built by shell32 in this process) has "IDEA Armory" with its items inside the
// vault only, and Check out runs ArmoryShell.exe, which reaches the test's pipe. This proves
// AppliesTo end to end without a screen. It writes the real HKCU\Software\Classes keys for a
// moment and puts back whatever was there.
[SupportedOSPlatform("windows")]
public sealed class ExplorerMenuTests
{
    private static readonly string[] Items = ["Check out", "Check out and open", "Check in", "Undo check out", "Show in Armory"];

    [NativeShellFact]
    public void The_menu_shows_inside_the_vault_only_and_check_out_reaches_armory()
    {
        using var folder = new TempFolder();
        var vault = folder.File("Vault Root");
        Directory.CreateDirectory(Path.Combine(vault, "Robot 2027"));
        Directory.CreateDirectory(Path.Combine(vault, "Class 2026"));
        var robotFile = Path.Combine(vault, "Robot 2027", "Plate notes.txt");
        var classFile = Path.Combine(vault, "Class 2026", "Lesson.txt");
        var outside = folder.File("Outside.txt");
        File.WriteAllText(robotFile, "synthetic");
        File.WriteAllText(classFile, "synthetic");
        File.WriteAllText(outside, "synthetic");

        using var classes = Registry.CurrentUser.CreateSubKey(ShellVerbs.ClassesKey, writable: true);
        var before = ShellVerbs.Read(classes);
        var dataBefore = Environment.GetEnvironmentVariable(AgentPaths.DataFolderVariable);
        var pipeBefore = Environment.GetEnvironmentVariable(ShellInbox.PipeVariable);
        var pipe = "armory-menu-test-" + Guid.NewGuid().ToString("N");
        var batches = new System.Collections.Concurrent.ConcurrentQueue<ShellBatch>();
        try
        {
            ShellVerbs.Apply(vault, NativeShell.Folder!, ["Robot 2027"]);
            Environment.SetEnvironmentVariable(AgentPaths.DataFolderVariable, folder.File("data"));
            Environment.SetEnvironmentVariable(ShellInbox.PipeVariable, pipe);
            using var inbox = new ShellInbox(pipe, batches.Enqueue);
            inbox.Start();

            OnSta(() =>
            {
                Assert.Equal([.. Items, "Force check in"], Menu.Armory(robotFile));
                Assert.Equal(Items, Menu.Armory(classFile));
                Assert.Equal([.. Items, "Force check in"], Menu.Armory(Path.Combine(vault, "Robot 2027")));
                Assert.Null(Menu.Armory(outside));
                Assert.Null(Menu.Armory(vault));
                Assert.True(Menu.Invoke(robotFile, "Check out"));
            });
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (batches.IsEmpty && watch.Elapsed < TimeSpan.FromSeconds(30)) Thread.Sleep(50);
            var batch = Assert.Single(batches);
            Assert.Equal(ShellVerb.CheckOut, batch.Verb);
            Assert.Equal([robotFile], batch.Paths);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AgentPaths.DataFolderVariable, dataBefore);
            Environment.SetEnvironmentVariable(ShellInbox.PipeVariable, pipeBefore);
            // Whatever a real Armory had written goes back.
            ShellVerbs.Write(classes, before.SelectMany(k => k.Value.Where(v => v.Value is string or int).Select(v => new ShellRegistryValue(k.Key, v.Key, v.Value))).ToArray());
            if (before.Count == 0) ShellVerbs.Remove(classes);
            ShellNotify();
        }
    }

    private static void ShellNotify() => Armory.Platform.Windows.ShellNotify.AssociationsChanged();

    private static void OnSta(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "The menu thread did not finish.");
        if (failure is not null) throw new Xunit.Sdk.XunitException("On the menu thread: " + failure);
    }

    // shell32's context menu for one item, as Explorer builds it.
    private static class Menu
    {
        private const uint First = 1;
        private const uint Last = 0x7FFF;

        // The items under "IDEA Armory", or null when the menu has no such item.
        internal static IReadOnlyList<string>? Armory(string path) => With(path, (menu, context, _) =>
        {
            var armory = Find(menu, "IDEA Armory");
            if (armory is null) return null;
            return Labels(Open(context, armory.Value.SubMenu, armory.Value.Position)).Select(l => l.Label).Where(l => l.Length > 0).ToArray();
        });

        // Runs one item under "IDEA Armory", as a click does.
        internal static bool Invoke(string path, string label) => With(path, (menu, context, _) =>
        {
            var armory = Find(menu, "IDEA Armory") ?? throw new InvalidOperationException("No IDEA Armory item.");
            var item = Labels(Open(context, armory.SubMenu, armory.Position)).First(l => l.Label == label);
            var info = new InvokeInfo
            {
                Size = Marshal.SizeOf<InvokeInfo>(),
                Mask = 0x4000 | 0x400 | 0x100, // CMIC_MASK_UNICODE | CMIC_MASK_FLAG_NO_UI | CMIC_MASK_NOASYNC
                Verb = (IntPtr)(item.Id - First),
                VerbW = (IntPtr)(item.Id - First),
                Show = 1,
            };
            return context.InvokeCommand(ref info) == 0;
        });

        private static T With<T>(string path, Func<IntPtr, IContextMenu3, int, T> use)
        {
            SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItem).GUID, out var item);
            // BHID_SFUIObject for IContextMenu; the menu object also answers IContextMenu3.
            item.BindToHandler(IntPtr.Zero, new Guid("3981e225-f559-11d3-8e3a-00c04f6837d5"), new Guid("000214e4-0000-0000-c000-000000000046"), out var handler);
            var context = (IContextMenu3)Marshal.GetObjectForIUnknown(handler);
            Marshal.Release(handler);
            var menu = CreatePopupMenu();
            try
            {
                Assert.True(context.QueryContextMenu(menu, 0, First, Last, 0) >= 0);
                return use(menu, context, 0);
            }
            finally
            {
                DestroyMenu(menu);
                Marshal.ReleaseComObject(context);
                Marshal.ReleaseComObject(item);
            }
        }

        // A cascade fills itself when it is about to open.
        private static IntPtr Open(IContextMenu3 context, IntPtr subMenu, int position)
        {
            context.HandleMenuMsg2(0x0117, subMenu, (IntPtr)position, out _); // WM_INITMENUPOPUP
            return subMenu;
        }

        private static (IntPtr SubMenu, int Position)? Find(IntPtr menu, string label)
        {
            foreach (var (text, id, sub, position) in Labels(menu))
                if (text == label) return (sub, position);
            return null;
        }

        private static List<(string Label, uint Id, IntPtr SubMenu, int Position)> Labels(IntPtr menu)
        {
            var labels = new List<(string, uint, IntPtr, int)>();
            var count = GetMenuItemCount(menu);
            for (var i = 0; i < count; i++)
            {
                var info = new MenuItemInfo { Size = (uint)Marshal.SizeOf<MenuItemInfo>(), Mask = 0x40 | 0x4 | 0x2 }; // MIIM_STRING | MIIM_SUBMENU | MIIM_ID
                if (!GetMenuItemInfoW(menu, (uint)i, true, ref info)) continue;
                var text = "";
                if (info.Length > 0)
                {
                    var buffer = Marshal.AllocHGlobal((int)(info.Length + 1) * 2);
                    try
                    {
                        info.TypeData = buffer;
                        info.Length++;
                        if (GetMenuItemInfoW(menu, (uint)i, true, ref info)) text = Marshal.PtrToStringUni(buffer) ?? "";
                    }
                    finally { Marshal.FreeHGlobal(buffer); }
                }
                labels.Add((text.Replace("&", "", StringComparison.Ordinal), info.Id, info.SubMenu, i));
            }
            return labels;
        }
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr bindContext, [MarshalAs(UnmanagedType.LPStruct)] Guid handler, [MarshalAs(UnmanagedType.LPStruct)] Guid iid, out IntPtr result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint form, out IntPtr name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hint, out int order);
    }

    // IContextMenu, IContextMenu2 and IContextMenu3 in one, in vtable order.
    [ComImport, Guid("bcfce0a0-ec17-11d0-8d10-00a0c90f2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu3
    {
        [PreserveSig] int QueryContextMenu(IntPtr menu, uint index, uint first, uint last, uint flags);
        [PreserveSig] int InvokeCommand(ref InvokeInfo info);
        [PreserveSig] int GetCommandString(UIntPtr command, uint type, IntPtr reserved, IntPtr name, uint length);
        [PreserveSig] int HandleMenuMsg(uint message, IntPtr wParam, IntPtr lParam);
        [PreserveSig] int HandleMenuMsg2(uint message, IntPtr wParam, IntPtr lParam, out IntPtr result);
    }

    // CMINVOKECOMMANDINFOEX
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct InvokeInfo
    {
        public int Size;
        public uint Mask;
        public IntPtr Window;
        public IntPtr Verb;
        public IntPtr Parameters;
        public IntPtr Directory;
        public int Show;
        public uint HotKey;
        public IntPtr Icon;
        public IntPtr Title;
        public IntPtr VerbW;
        public IntPtr ParametersW;
        public IntPtr DirectoryW;
        public IntPtr TitleW;
        public int X;
        public int Y;
    }

    // MENUITEMINFOW
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MenuItemInfo
    {
        public uint Size;
        public uint Mask;
        public uint Type;
        public uint State;
        public uint Id;
        public IntPtr SubMenu;
        public IntPtr Checked;
        public IntPtr Unchecked;
        public IntPtr ItemData;
        public IntPtr TypeData;
        public uint Length;
        public IntPtr Bitmap;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr bindContext, [MarshalAs(UnmanagedType.LPStruct)] Guid iid,
        [MarshalAs(UnmanagedType.Interface, IidParameterIndex = 2)] out IShellItem item);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern int GetMenuItemCount(IntPtr menu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMenuItemInfoW(IntPtr menu, uint item, [MarshalAs(UnmanagedType.Bool)] bool byPosition, ref MenuItemInfo info);
}
