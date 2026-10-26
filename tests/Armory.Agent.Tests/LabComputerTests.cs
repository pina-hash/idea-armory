using System.Text;
using System.Text.Json;
using Armory.Agent.Engine.View;
using Armory.Core;
using Armory.TestSupport;
using static Armory.Agent.Tests.Lab;

namespace Armory.Agent.Tests;

// A lab computer in 0.5.0 (ledger 0437; docs/agent/PROFILES.md), through the real AgentHost: one
// shared Windows sign-in, and a computer that becomes shared by itself when a second person signs
// in; PINs carried forward from 0.3.3 to 0.4.x only where a mentor turned them on, with the index
// saved as version 2 so an older Armory runs nobody from it; and a second Windows account whose
// Armory finds the folder held by the first one's.
public sealed class LabComputerTests
{
    private static void Write(LabFolder folder, string path, string text)
    {
        var full = folder.Disk.Full(path);
        if (!File.Exists(full)) folder.Disk.ForgetReadOnly(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private static async Task UntilOnServer(Lab lab, string name)
    {
        for (var i = 0; i < 600; i++)
        {
            if ((await lab.MentorApi.ProjectFilesAsync(lab.Project)).Any(f => f.Name == name && !f.Deleted && f.Current is not null)) return;
            await Task.Delay(100);
        }
        Assert.Fail(name + " never reached the server");
    }

    [PostgresFact]
    public async Task A_second_person_signing_in_makes_the_computer_shared_with_no_setup_and_the_first_sign_in_moves()
    {
        await using var lab = await StartAsync(shared: false);
        var shared = lab.Folder(Shared);
        lab.Browser.Next = Alex;
        await lab.Host.ConnectAsync();
        await Until(() => lab.View.Connection == Connections.SignedIn && lab.View.Account?.Email == Alex, "Alex signed in");
        var alexToken = lab.Host.Sessions.Current!.RefreshToken;
        Assert.Null(lab.View.Profiles);
        Write(shared, Plate, "v1");
        await UntilOnServer(lab, "Plate.SLDPRT");
        await Until(() => shared.Disk.IsReadOnly(Plate), "Plate read-only");
        Assert.True((await lab.Host.CheckOutAsync([Plate], false)).Ok);
        Write(shared, Plate, "Alex, not checked in");

        // Alex signs in again (a renewed sign-in): still one person's computer.
        lab.Browser.Next = Alex;
        await lab.Host.SwitchAccountAsync();
        await Until(() => lab.View.Connection == Connections.SignedIn && lab.Host.Sessions.Current?.RefreshToken != alexToken, "Alex's sign-in renewed");
        Assert.Null(lab.View.Profiles);
        Assert.False(Directory.Exists(lab.Paths.ProfilesFolder));
        alexToken = lab.Host.Sessions.Current!.RefreshToken;

        // Jordan signs in on Switch account: the computer is shared now, by itself, no PIN.
        lab.Browser.Next = Jordan;
        await lab.Host.SwitchAccountAsync();
        await lab.UntilRunningAsync(Jordan, Shared);
        Assert.True(lab.View.Settings.SharedComputer);
        Assert.False(lab.Profiles.PinsRequired);
        Assert.Equal([Jordan, Alex], lab.Profiles.Profiles.Select(p => p.Email));
        // Alex's sign-in moved into his profile, never copied: one file holds his token.
        var alexFiles = lab.Secrets().Where(s => s.Text.Contains(alexToken, StringComparison.Ordinal)).ToList();
        Assert.StartsWith("profiles/", Assert.Single(alexFiles).File.Replace('\\', '/'));
        Assert.DoesNotContain(lab.Secrets(), s => s.File.Replace('\\', '/').StartsWith("secrets/", StringComparison.Ordinal));
        var index = JsonDocument.Parse(File.ReadAllText(Path.Combine(lab.Paths.ProfilesFolder, "profiles.json"))).RootElement;
        Assert.Equal((2, false), (index.GetProperty("version").GetInt32(), index.GetProperty("pinsRequired").GetBoolean()));
        // Alex's check out is his, read-only for Jordan.
        await Until(() => lab.View.HeldForOthers is [{ Email: Alex, Files: 1 }], "Alex's work held for him");
        await Until(() => shared.Disk.IsReadOnly(Plate), "Alex's check out read-only for Jordan");
        Assert.Equal("Alex, not checked in", shared.Text(Plate));

        // Two people now: the window opens on their faces, and one click switches back to Alex.
        lab.Host.ShowPicker(PickerTrigger.ShownFromHidden);
        Assert.True(lab.Profiles.Showing);
        var picked = await lab.Host.PickProfileAsync(lab.IdOf(Alex));
        Assert.True(picked.Ok, picked.Message);
        await lab.UntilRunningAsync(Alex, Shared);
        Assert.Equal(alexToken, lab.Host.Sessions.Current!.RefreshToken);
        await Until(() => !shared.Disk.IsReadOnly(Plate), "Alex's check out writable");
        Assert.Equal("Checked in Plate.SLDPRT.", (await lab.Host.CheckInAsync([Plate])).Message);
        Assert.Empty(lab.Violations);
    }

    [PostgresFact]
    public async Task Pins_from_an_older_Armory_stay_on_only_where_a_mentor_turned_them_on_and_an_older_Armory_then_runs_nobody()
    {
        foreach (var (changedBy, kept) in new[] { ((string?)null, false), (Mentor, true) })
        {
            await using var lab = await StartAsync(shared: true, before: l =>
            {
                Directory.CreateDirectory(l.Paths.ProfilesFolder);
                var by = changedBy is null ? "null" : "\"" + changedBy + "\"";
                File.WriteAllText(Path.Combine(l.Paths.ProfilesFolder, "profiles.json"),
                    "{\"version\":1,\"current\":null,\"pinsRequired\":true,\"pinsChangedBy\":" + by + ",\"pinsChangedAt\":null,\"migrating\":null,\"lastPicked\":null,\"profiles\":[]}");
            });
            Assert.Equal(kept, lab.Profiles.PinsRequired);
            var index = JsonDocument.Parse(File.ReadAllText(Path.Combine(lab.Paths.ProfilesFolder, "profiles.json"))).RootElement;
            Assert.Equal((2, kept), (index.GetProperty("version").GetInt32(), index.GetProperty("pinsRequired").GetBoolean()));
            Assert.Contains("profiles: carried forward from an older Armory; PINs " + (kept ? "stay on, a mentor turned them on" : "are off"), lab.LogText);
            // 0.4.x reads any version above 1 as set up by a newer Armory and runs nobody (its
            // ProfileStore.Load); 0.5.0 itself reads 2 and refuses only what is newer than it.
            Assert.True(ProfileIndex.CurrentVersion > 1);
        }
    }

    [PostgresFact]
    public async Task A_folder_another_Windows_users_Armory_holds_says_so_and_starts_here_by_itself_once_it_is_free()
    {
        var held = true;
        await using var lab = await StartAsync(shared: false, before: l => l.RuntimeRefusal = _ => Volatile.Read(ref held) ? new IOException("in use", unchecked((int)0x80070020)) : null);
        await Until(() => lab.View.Sync.Line.StartsWith("Armory is already open for another Windows user on this computer", StringComparison.Ordinal), "the plain words");
        Assert.Equal($"Armory is already open for another Windows user on this computer, in {Shared}. Switch to that user and quit Armory there (right-click the Armory icon by the clock, then Quit). Armory then starts here by itself. Your files are safe.",
            lab.View.Sync.Line);
        Assert.DoesNotContain("Choose another folder", lab.View.Sync.Line, StringComparison.Ordinal);
        Volatile.Write(ref held, false);
        await Until(() => lab.LogText.Contains("vault runtime started at " + Shared, StringComparison.Ordinal), "the runtime started by itself");
        Assert.True(AgentHost.HeldByAnotherWindowsUser(new IOException("x", unchecked((int)0x80070021))));
        Assert.False(AgentHost.HeldByAnotherWindowsUser(new IOException("Armory is already running the folder")));
        Assert.False(AgentHost.HeldByAnotherWindowsUser(new UnauthorizedAccessException()));
    }
}
