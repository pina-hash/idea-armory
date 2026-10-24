using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using System.Text.Json;
using Armory.Agent.Engine.View;

namespace Armory.Agent.Tests;

// 0.4.3 (ledger 0434): the version and its date where a person finds it (InstallRecord, Settings'
// About, the tray), taskbar pins that survive an update (TaskbarPins, Velopack's --aumid), and
// computers told apart without their names (MachineId, the device tag).
public sealed class VersionAndPinsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 18, 0, 0, TimeSpan.Zero);

    // ---- The version and its date --------------------------------------------------------------

    [Fact]
    public void A_first_install_says_nothing_and_an_update_says_so_once()
    {
        // A first install: nothing to say, "installed" in Settings.
        var (fresh, freshNotice) = InstallRecord.Advance(null, "0.4.3", Now, usedBefore: false);
        Assert.Null(freshNotice);
        Assert.False(fresh.Updated);
        Assert.StartsWith("Version 0.4.3, installed ", fresh.Words);

        // 0.4.2 kept no record: a computer that ran Armory before is an update, from a version unknown.
        var (fromOld, oldNotice) = InstallRecord.Advance(null, "0.4.3", Now, usedBefore: true);
        Assert.Equal("Armory updated to 0.4.3.", oldNotice);
        Assert.True(fromOld.Updated);
        Assert.Null(fromOld.Previous);

        // The same version again: the same record, nothing said.
        var (same, sameNotice) = InstallRecord.Advance(fromOld, "0.4.3", Now.AddDays(1), usedBefore: true);
        Assert.Null(sameNotice);
        Assert.Same(fromOld, same);

        // A newer one: said with both versions, dated from now, and the pin sentence stays said.
        var (next, nextNotice) = InstallRecord.Advance(fromOld with { PinNoticeShown = true }, "0.4.4", Now.AddDays(2), usedBefore: true);
        Assert.Equal("Armory updated from 0.4.3 to 0.4.4.", nextNotice);
        Assert.Equal("0.4.3", next.Previous);
        Assert.Equal(Now.AddDays(2), next.Since);
        Assert.True(next.PinNoticeShown);
        Assert.StartsWith("Version 0.4.4, updated ", next.Words);
        Assert.StartsWith("Armory 0.4.4, updated ", next.ShortWords);
        foreach (var words in new[] { next.Words, next.ShortWords, nextNotice!, oldNotice!, TaskbarPins.RepinWords })
            Assert.DoesNotContain("\u2014", words);
    }

    [Fact]
    public void The_record_is_kept_on_disk_and_a_second_start_says_nothing()
    {
        using var folder = new TempFolder();
        var paths = new AgentPaths(folder.Root, true);
        File.WriteAllText(paths.SettingsFile, "{}");
        var (first, notice) = InstallRecord.Start(paths, "0.4.3", Now);
        Assert.Equal("Armory updated to 0.4.3.", notice);
        Assert.True(File.Exists(Path.Combine(folder.Root, InstallRecord.FileName)));
        var (second, again) = InstallRecord.Start(paths, "0.4.3", Now.AddHours(5));
        Assert.Null(again);
        Assert.Equal(first, second);
        // Marked once: read back as said.
        InstallRecord.MarkPinNotice(paths, second);
        Assert.True(InstallRecord.Read(Path.Combine(folder.Root, InstallRecord.FileName))!.PinNoticeShown);
        // A damaged record reads as none rather than stopping a start.
        File.WriteAllText(Path.Combine(folder.Root, InstallRecord.FileName), "{ not json");
        Assert.Null(InstallRecord.Read(Path.Combine(folder.Root, InstallRecord.FileName)));
    }

    // 0.4.2 kept no record and may never have saved settings: its log's "started 0.4.2" says it
    // ran (CI's first run of the update test read "installed" for exactly that computer).
    [Fact]
    public void The_log_names_the_version_this_one_replaced()
    {
        var log = "2026-10-22T08:00:00.000Z started 0.4.1\n2026-10-23T08:00:00.000Z started 0.4.2\r\n2026-10-23T08:00:01.000Z something started 9.9 later\n2026-10-24T04:42:30.000Z started 0.4.3\n";
        Assert.Equal("0.4.2", InstallRecord.EarlierStart(log, "0.4.3"));
        Assert.Null(InstallRecord.EarlierStart("2026-10-24T04:42:30.000Z started 0.4.3\n", "0.4.3"));
        Assert.Null(InstallRecord.EarlierStart(null, "0.4.3"));
        using var folder = new TempFolder();
        var paths = new AgentPaths(folder.Root, true);
        Directory.CreateDirectory(paths.LogFolder);
        File.WriteAllText(paths.LogFile, log);
        var (record, notice) = InstallRecord.Start(paths, "0.4.3", Now);
        Assert.Equal("Armory updated from 0.4.2 to 0.4.3.", notice);
        Assert.Equal("0.4.2", record.Previous);
        Assert.True(record.Updated);
        // A first install: only this version's own start in the log, no settings: nothing said.
        using var fresh = new TempFolder();
        var freshPaths = new AgentPaths(fresh.Root, true);
        Directory.CreateDirectory(freshPaths.LogFolder);
        File.WriteAllText(freshPaths.LogFile, "2026-10-24T04:42:30.000Z started 0.4.3\n");
        Assert.Null(InstallRecord.Start(freshPaths, "0.4.3", Now).Notice);
    }

    [Fact]
    public void Settings_carries_the_about_line_to_the_page()
    {
        var about = new AboutView("0.4.3", "Version 0.4.3, updated Oct 23, 2026", Now.ToString("O"), "IDEA-06", AgentHost.DeviceTag(new Guid("3f9c0a7e-2b14-4d86-9a1e-000000000001")));
        var json = JsonDocument.Parse(JsonSerializer.Serialize(new SettingsView(@"C:\IDEA\Armory", true, "system", About: about), BridgeMessages.Json)).RootElement.GetProperty("about");
        Assert.Equal("0.4.3", json.GetProperty("version").GetString());
        Assert.Equal("Version 0.4.3, updated Oct 23, 2026", json.GetProperty("words").GetString());
        Assert.Equal("IDEA-06", json.GetProperty("computerName").GetString());
        Assert.Equal("3f9c", json.GetProperty("deviceTag").GetString());
        // Before the host has said: null, and the page draws no About section.
        Assert.Equal(JsonValueKind.Null, JsonDocument.Parse(JsonSerializer.Serialize(new SettingsView(@"C:\IDEA\Armory", true, "system"), BridgeMessages.Json)).RootElement.GetProperty("about").ValueKind);
        var page = File.ReadAllText(Path.Combine(Repo.FindRoot()!, "src", "Armory.Agent", "wwwroot", "app.js"));
        Assert.Contains("settingsAboutHtml(s.about)", page);
        Assert.Contains("About this Armory", page);
    }

    [Fact]
    public void The_tray_says_the_version_and_the_sync_line_gives_up_room_first()
    {
        const int limit = 63;
        Assert.Equal("Synced\nArmory 0.4.3, updated Oct 23", HostTips.TrayTooltip("Synced", "Armory 0.4.3, updated Oct 23", limit));
        Assert.Equal("Synced", HostTips.TrayTooltip("Synced", null, limit));
        var long_ = HostTips.TrayTooltip(new string('x', 80), "Armory 0.4.3, updated Oct 23", limit);
        Assert.True(long_.Length <= limit, long_.Length.ToString());
        Assert.EndsWith("...\nArmory 0.4.3, updated Oct 23", long_);
    }

    // ---- Taskbar pins ------------------------------------------------------------------------

    private const string Exe = @"C:\Users\IDEA\AppData\Local\IDEAArmory\current\IdeaArmory.exe";
    private const string Legacy = @"C:\Users\IDEA\AppData\Local\Programs\IDEA Armory\IdeaArmory.exe";

    [Fact]
    public void Every_pin_of_Armory_is_pointed_here_and_no_other_pin_is_touched()
    {
        bool Exists(string path) => path.Equals(Exe, StringComparison.OrdinalIgnoreCase) || path.EndsWith("notepad.exe", StringComparison.OrdinalIgnoreCase) || path.EndsWith(@"\src\IdeaArmory.exe", StringComparison.OrdinalIgnoreCase);
        var pins = new[]
        {
            new TaskbarPins.Pin("velopack-id.lnk", Exe, TaskbarPins.VelopackAppId),       // what 0.4.2's updates wrote
            new TaskbarPins.Pin("old-copy.lnk", Legacy, ShellIdentity.AppId),             // the copy the move deleted
            new TaskbarPins.Pin("no-id.lnk", Exe, null),                                  // pinned before any id
            new TaskbarPins.Pin("gone.lnk", @"D:\Somewhere\IdeaArmory.exe", null),        // a copy that is gone
            new TaskbarPins.Pin("good.lnk", Exe.ToUpperInvariant(), ShellIdentity.AppId), // already right
            new TaskbarPins.Pin("notepad.lnk", @"C:\Windows\notepad.exe", null),          // someone else's
            new TaskbarPins.Pin("dev.lnk", @"C:\src\IdeaArmory.exe", null),               // a developer's build that is there
            new TaskbarPins.Pin("unreadable.lnk", null, null),
        };
        var fixes = TaskbarPins.Plan(pins, Exe, Exists);
        Assert.Equal(["velopack-id.lnk", "old-copy.lnk", "no-id.lnk", "gone.lnk"], fixes.Select(f => f.File).ToArray());
        Assert.Equal([false, true, false, true], fixes.Select(f => f.Broken).ToArray());
        // Absent: the right one, someone else's, a developer's build, and one that cannot be read.
        foreach (var left in new[] { "good.lnk", "notepad.lnk", "dev.lnk", "unreadable.lnk" })
            Assert.DoesNotContain(fixes, f => f.File == left);
        // Done once, nothing is left to do.
        var after = pins.Select(p => fixes.Any(f => f.File == p.File) ? p with { Target = Exe, AppId = ShellIdentity.AppId } : p);
        Assert.Empty(TaskbarPins.Plan(after, Exe, Exists));
        Assert.EndsWith(Path.Combine("Microsoft", "Internet Explorer", "Quick Launch", "User Pinned", "TaskBar"), TaskbarPins.Folder("R"));
    }

    // The release that updates itself gives Velopack Armory's own identity, so an update's
    // rewrite of the Start menu shortcut and of each pin keeps the identity the window has.
    [Fact]
    public void The_updating_release_carries_Armorys_own_app_id()
    {
        var script = File.ReadAllText(Path.Combine(Repo.FindRoot()!, "tools", "package-velopack.ps1"));
        Assert.Contains("'--aumid', '" + ShellIdentity.AppId + "'", script);
        Assert.Equal("velopack." + Updates.PackId, TaskbarPins.VelopackAppId);
    }

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public void A_real_pin_of_a_deleted_copy_is_pointed_here_and_another_programs_pin_is_not()
    {
        using var folder = new TempFolder();
        var pinsFolder = folder.File("TaskBar");
        Directory.CreateDirectory(pinsFolder);
        var exe = folder.File(@"IDEAArmory\current\IdeaArmory.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllBytes(exe, [0x4D, 0x5A]);
        var other = folder.File("Other.exe");
        File.WriteAllBytes(other, [0x4D, 0x5A]);
        var gone = folder.File(@"Programs\IDEA Armory\IdeaArmory.exe");
        string Shortcut(string name, string target)
        {
            var path = Path.Combine(pinsFolder, name);
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            dynamic link = shell.CreateShortcut(path);
            link.TargetPath = target;
            link.Save();
            return path;
        }
        // The deleted copy: made while it was there, then removed, as the move to 0.4.x did.
        Directory.CreateDirectory(Path.GetDirectoryName(gone)!);
        File.WriteAllBytes(gone, [0x4D, 0x5A]);
        var broken = Shortcut("IDEA Armory.lnk", gone);
        Directory.Delete(Path.GetDirectoryName(gone)!, recursive: true);
        var theirs = Shortcut("Other.lnk", other);
        var theirBytes = File.ReadAllBytes(theirs);
        var said = new List<string>();
        Exception? failed = null;
        var thread = new Thread(() =>
        {
            try
            {
                var fixes = TaskbarPins.Repair(pinsFolder, exe, said.Add);
                Assert.Equal([broken], fixes.Select(f => f.File).ToArray());
                Assert.True(fixes[0].Broken);
                Assert.Equal(Path.GetFullPath(exe), Path.GetFullPath(ShortcutAppId.Target(broken)!), ignoreCase: true);
                Assert.Equal(ShellIdentity.AppId, ShortcutAppId.ReadAppId(broken));
                // Another program's pin: not a byte changed, no identity given.
                Assert.Equal(theirBytes, File.ReadAllBytes(theirs));
                Assert.Null(ShortcutAppId.ReadAppId(theirs));
                // A second start has nothing to do.
                Assert.Empty(TaskbarPins.Repair(pinsFolder, exe, said.Add));
            }
            catch (Exception error) { failed = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failed is not null) throw failed;
        Assert.Contains(said, line => line.Contains("it pointed at a copy that is gone", StringComparison.Ordinal));
    }

    // ---- Computers that share a name ----------------------------------------------------------

    [Fact]
    public void Two_computers_from_one_image_get_two_ids_and_one_computer_keeps_its_own()
    {
        const string cloned = "6E3F1A52-0B7C-4C11-9C55-7D1E3A2B9F01";
        var a = MachineId.FromParts(cloned, "001a2b3c4d5e");
        var b = MachineId.FromParts(cloned, "001a2b3c4d5f");
        Assert.NotEqual(a, b);
        Assert.Equal(a, MachineId.FromParts(cloned.ToLowerInvariant(), " 001A2B3C4D5E "));
        // With no hardware address, the id 0.3.3 to 0.4.2 gave.
        Assert.Equal(MachineId.FromGuid(cloned), MachineId.FromParts(cloned, null));
        Assert.Matches("^[0-9a-f]{16}$", a!);
    }

    [Fact]
    public void The_hardware_address_is_the_computers_own_wired_one_first()
    {
        MachineId.NetworkAdapter A(NetworkInterfaceType kind, string description, string address) => new(kind, description, address);
        var adapters = new[]
        {
            A(NetworkInterfaceType.Wireless80211, "Intel Wi-Fi 6 AX201", "00AA00000001"),
            A(NetworkInterfaceType.Ethernet, "Hyper-V Virtual Ethernet Adapter", "000000000002"),
            A(NetworkInterfaceType.Ethernet, "Realtek PCIe GbE Family Controller", "B42E99000010"),
            A(NetworkInterfaceType.Ethernet, "Intel Ethernet Connection I219-LM", "B42E99000003"),
            A(NetworkInterfaceType.Loopback, "Software Loopback Interface 1", ""),
            A(NetworkInterfaceType.Ethernet, "Bluetooth Device (Personal Area Network)", "000000000001"),
            A(NetworkInterfaceType.Ethernet, "Broken", "000000000000"),
        };
        Assert.Equal("b42e99000003", MachineId.Hardware(adapters));
        // Wi-Fi only: the wireless one. Nothing of its own: none.
        Assert.Equal("00aa00000001", MachineId.Hardware(adapters.Where(x => x.Kind == NetworkInterfaceType.Wireless80211)));
        Assert.Null(MachineId.Hardware(adapters.Where(x => x.Description.Contains("Virtual") || x.Kind == NetworkInterfaceType.Loopback)));
    }
}
