using System.Text;
using System.Text.Json;
using Armory.Agent.Engine.View;
using Armory.Client;
using Armory.Core;
using Armory.TestSupport;
using static Armory.Agent.Tests.Lab;

namespace Armory.Agent.Tests;

// Several students on one computer, through the real AgentHost (docs/agent/PROFILES.md, 0.3.3
// part F): shared mode on and off (the sign-in moves, never copied), the PIN and its waits, the
// picker, switching with and without work waiting (the last student's engine stopped for good
// before the next one's starts), a folder of one's own and the way back, adding and removing
// students, and who may turn PINs off. The host runs over portable folders, plain secrets and the
// fake site and Supabase (PostgreSQL); the window's part is in tools/agent-ui.
public sealed class ProfilesHostTests
{
    private static void Write(LabFolder folder, string path, string text)
    {
        var full = folder.Disk.Full(path);
        if (!File.Exists(full)) folder.Disk.ForgetReadOnly(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    // Ctrl+S in SolidWorks: refused over a read-only file, as SolidWorks refuses it.
    private static void Save(LabFolder folder, string path, string text)
    {
        if (folder.Disk.IsReadOnly(path)) throw new IOException(path + " is read-only.");
        Write(folder, path, text);
    }

    private static async Task<RemoteFile?> OnServer(Lab lab, string name)
        => (await lab.MentorApi.ProjectFilesAsync(lab.Project)).SingleOrDefault(f => f.Name == name && !f.Deleted);

    private static async Task UntilOnServer(Lab lab, string name, string text)
    {
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        for (var i = 0; i < 600; i++)
        {
            if (await OnServer(lab, name) is { Current.Hash: var h } && h == hash) return;
            await Task.Delay(100);
        }
        Assert.Fail(name + " never reached the server with its bytes");
    }

    // ---- Shared mode on and off --------------------------------------------------------------

    [PostgresFact]
    public async Task Turning_shared_mode_on_moves_the_sign_in_and_never_leaves_two_copies()
    {
        await using var lab = await StartAsync(shared: false);
        lab.Browser.Next = Alex;
        await lab.Host.ConnectAsync();
        await Until(() => lab.View.Connection == Connections.SignedIn, "Alex signed in");
        var refresh = lab.Host.Sessions.Current!.RefreshToken;
        Assert.Equal(["secrets/armory-session.secret"], lab.Secrets().Select(s => s.File.Replace('\\', '/')));
        Assert.False(Directory.Exists(lab.Paths.ProfilesFolder));

        // Turning it on asks Alex for a PIN first; an easy one is refused and nothing changes.
        Assert.Equal((false, "Choose a 4-digit PIN for yourself first."), Pair(await lab.Host.SetSharedComputerAsync(true, null)));
        Assert.Equal((false, SharedComputer.TooEasyPin), Pair(await lab.Host.SetSharedComputerAsync(true, "1234")));
        Assert.False(Directory.Exists(lab.Paths.ProfilesFolder));
        Assert.True((await lab.Host.SetSharedComputerAsync(true, "2580")).Ok);
        await lab.UntilRunningAsync(Alex, Shared);

        // The sign-in moved: exactly one file holds the refresh token, in Alex's profile.
        var holding = lab.Secrets().Where(s => s.Text.Contains(lab.Host.Sessions.Current!.RefreshToken, StringComparison.Ordinal)).ToList();
        var moved = Assert.Single(holding);
        Assert.StartsWith("profiles/", moved.File.Replace('\\', '/'));
        Assert.Equal(refresh, lab.Host.Sessions.Current!.RefreshToken);
        Assert.DoesNotContain(lab.Secrets(), s => s.File.Replace('\\', '/').StartsWith("secrets/", StringComparison.Ordinal));
        Assert.Contains("\"sharedComputer\": true", File.ReadAllText(lab.Paths.SettingsFile));
        Assert.True(lab.View.Settings.SharedComputer);
        var index = File.ReadAllText(Path.Combine(lab.Paths.ProfilesFolder, "profiles.json"));
        Assert.DoesNotContain(refresh, index);
        Assert.Contains(Alex, index);

        // The next start finds Alex in use, with his PIN.
        await lab.RestartAsync();
        await lab.UntilRunningAsync(Alex, Shared);
        Assert.Contains("profiles: shared computer on, 1 student, current " + Alex, lab.LogText);
        Assert.Equal((false, ""), Pair(await lab.PickAsync(Alex, "1111")));
        Assert.Equal("That PIN isn't right. 4 more tries, then a short wait.", lab.Step.Message);
        Assert.True((await lab.Host.EnterPinAsync(lab.IdOf(Alex), "2580")).Ok);
        await lab.UntilRunningAsync(Alex, Shared);

        // Off again: Alex stays, signed in as before, his sign-in back in secrets\ and only there.
        var now = lab.Host.Sessions.Current!.RefreshToken;
        Assert.True((await lab.Host.SetSharedComputerAsync(false, null)).Ok);
        await Until(() => lab.View.Connection == Connections.SignedIn && lab.View.Profiles is null, "one student again");
        Assert.Equal(["secrets/armory-session.secret"], lab.Secrets().Select(s => s.File.Replace('\\', '/')));
        Assert.Contains(now, Assert.Single(lab.Secrets()).Text);
        Assert.False(Directory.Exists(lab.Paths.ProfilesFolder));
        Assert.DoesNotContain("sharedComputer", File.ReadAllText(lab.Paths.SettingsFile));
        Assert.Equal(Alex, lab.Host.Sessions.Current!.Email);
        Assert.Empty(lab.Violations);
    }

    private static (bool, string) Pair(ActionResult result) => (result.Ok, result.Message);

    // ---- The PIN -----------------------------------------------------------------------------

    [PostgresFact]
    public async Task Right_pin_switches_wrong_pin_counts_fifth_wrong_pin_waits_and_the_wait_survives_a_restart()
    {
        await using var lab = await StartAsync(shared: true);
        // Nobody yet: the picker, with only Add a student.
        Assert.True(lab.Profiles.Showing);
        Assert.Empty(lab.Profiles.Profiles);
        Assert.Equal(PickerSteps.Choose, lab.Step.Kind);
        await lab.AddAsync(Alex, "2580");
        await lab.UntilRunningAsync(Alex, Shared);
        await lab.AddAsync(Jordan, "1357");
        await lab.UntilRunningAsync(Jordan, Shared);
        var alex = lab.IdOf(Alex);

        // Alex picks himself: four wrong PINs count down, the fifth earns a 30 second wait.
        Assert.True((await lab.Host.PickProfileAsync(alex)).Ok);
        Assert.Equal((PickerSteps.Pin, alex, 5), (lab.Step.Kind, lab.Step.ProfileId, lab.Step.TriesLeft));
        foreach (var left in new[] { 4, 3, 2 })
        {
            Assert.Equal((false, ""), Pair(await lab.Host.EnterPinAsync(alex, "0000")));
            Assert.Equal($"That PIN isn't right. {left} more tries, then a short wait.", lab.Step.Message);
        }
        await lab.Host.EnterPinAsync(alex, "0000");
        Assert.Equal("That PIN isn't right. 1 more try, then a short wait.", lab.Step.Message);
        await lab.Host.EnterPinAsync(alex, "0000");
        Assert.Equal("Too many wrong tries. Try again in 30 seconds, or sign in with Google instead.", lab.Step.Message);
        Assert.Equal(30, lab.Step.WaitSeconds);
        // During the wait even the right PIN is refused, and Jordan is still the one in use.
        Assert.False((await lab.Host.EnterPinAsync(alex, "2580")).Ok);
        Assert.Equal(Jordan, lab.InUse);
        Assert.Equal(Jordan, lab.Host.Sessions.Current!.Email);

        // The wait survives a restart: it is in Alex's own protected record, not in a file to edit.
        await lab.RestartAsync();
        Assert.True((await lab.Host.PickProfileAsync(alex)).Ok);
        Assert.Equal(30, lab.Step.WaitSeconds);
        Assert.False((await lab.Host.EnterPinAsync(alex, "2580")).Ok);
        Assert.DoesNotContain("2580", File.ReadAllText(Path.Combine(lab.Paths.ProfilesFolder, "profiles.json")));
        Assert.DoesNotContain("2580", lab.LogText);
        lab.Clock.Advance(TimeSpan.FromSeconds(31));
        Assert.True((await lab.Host.EnterPinAsync(alex, "2580")).Ok);
        await lab.UntilRunningAsync(Alex, Shared);
        // A right PIN clears the count: the next wrong one has all its tries again.
        Assert.False((await lab.PickAsync(Alex, "9999")).Ok);
        Assert.Equal("That PIN isn't right. 4 more tries, then a short wait.", lab.Step.Message);
        Assert.Empty(lab.Violations);
    }

    [PostgresFact]
    public async Task Forgot_pin_with_another_account_is_refused_and_with_the_same_account_sets_a_new_pin()
    {
        await using var lab = await StartAsync(shared: true);
        await lab.AddAsync(Alex, "2580");
        await lab.AddAsync(Jordan, "1357");
        await lab.UntilRunningAsync(Jordan, Shared);
        var alex = lab.IdOf(Alex);
        Assert.True((await lab.Host.PickProfileAsync(alex)).Ok);
        // The browser is still signed in as Jordan: that sign-in is not Alex's, and changes nothing.
        lab.Browser.Next = Jordan;
        Assert.True((await lab.Host.ForgotPinAsync(alex)).Ok);
        await Until(() => lab.Step.ConnectPhase == "failed", "the other account to be refused");
        Assert.Equal($"That was {Jordan}. To change Alex's PIN, sign in as {Alex}.", lab.Step.Message);
        Assert.Equal(Jordan, lab.InUse);
        // Alex signs in: he chooses a new PIN, and the old one no longer gets in.
        lab.Browser.Next = Alex;
        Assert.True((await lab.Host.ForgotPinAsync(alex)).Ok);
        await Until(() => lab.Step.Kind == PickerSteps.NewPin && lab.Step.ProfileId == alex, "Alex's new PIN");
        Assert.True((await lab.Host.SetPinAsync(alex, "4682")).Ok);
        await lab.UntilRunningAsync(Alex, Shared);
        Assert.False((await lab.PickAsync(Alex, "2580")).Ok);
        Assert.True((await lab.Host.EnterPinAsync(alex, "4682")).Ok);
        await lab.UntilRunningAsync(Alex, Shared);
    }

    // ---- Switching ---------------------------------------------------------------------------

    [PostgresFact]
    public async Task Switching_with_nothing_waiting_hands_the_shared_folder_over_and_downloads_nothing_again()
    {
        await using var lab = await StartAsync(shared: true);
        var shared = lab.Folder(Shared);
        await lab.AddAsync(Alex, "2580");
        await lab.UntilRunningAsync(Alex, Shared);
        Write(shared, Plate, "plate v1 by Alex");
        await UntilOnServer(lab, "Plate.SLDPRT", "plate v1 by Alex");
        await Until(() => SyncEngineOwner(lab) == Alex && shared.Disk.IsReadOnly(Plate), "the folder bound to Alex and Plate read-only");
        await UntilAsync(async () => await lab.DeviceState(Alex) is "idle" or "syncing", "Alex's heartbeat");
        var gets = lab.Network.StorageGets;

        // Jordan is added (the picker shows; Alex's engine keeps working, and the page gets none of his files).
        lab.Host.ShowPicker(PickerTrigger.SwitchStudent);
        var picking = lab.View;
        Assert.True(picking.Profiles!.Showing);
        Assert.Empty(picking.Projects);
        Assert.Empty(picking.MyFiles);
        Assert.Null(picking.Account);
        Assert.Null(picking.Prompt);
        Assert.Empty(picking.Activity.Log);
        Assert.Equal(Alex, lab.Host.Sessions.Current!.Email);
        await lab.AddAsync(Jordan, "1357");
        await lab.UntilRunningAsync(Jordan, Shared);

        // Alex's engine stopped for good before Jordan's started; the folder is Jordan's now, and
        // its files were the team's versions: nothing was downloaded again.
        await Until(() => SyncEngineOwner(lab) == Jordan, "the folder bound to Jordan");
        Assert.Equal("plate v1 by Alex", shared.Text(Plate));
        Assert.True(shared.Disk.IsReadOnly(Plate));
        Assert.Equal(gets, lab.Network.StorageGets);
        Assert.Contains("This Armory folder is yours now. It was Alex's.", lab.View.Activity.Log.Select(l => l.Line));
        Assert.Empty(lab.Violations);
        // Alex's computer said goodbye with his own device; Jordan beats with his.
        Assert.Equal("offline-soon", await lab.DeviceState(Alex));
        await UntilAsync(async () => await lab.DeviceState(Jordan) is "idle" or "syncing", "Jordan's heartbeat");
        Assert.Equal(1, await lab.Devices(Alex));
        Assert.Equal(1, await lab.Devices(Jordan));

        // Two picks at once run one after the other: one runtime at a time, and Jordan's work is his.
        Assert.True((await lab.PickAsync(Alex, "2580")).Ok);
        await lab.UntilRunningAsync(Alex, Shared);
        lab.Host.ShowPicker(PickerTrigger.SwitchStudent);
        var jordan = lab.IdOf(Jordan);
        Assert.True((await lab.Host.PickProfileAsync(jordan)).Ok);
        var both = await Task.WhenAll(lab.Host.EnterPinAsync(jordan, "1357"), lab.Host.EnterPinAsync(jordan, "1357"));
        Assert.Contains(both, r => r.Ok);
        await lab.UntilRunningAsync(Jordan, Shared);
        Assert.Empty(lab.Violations);
        Assert.Contains("profiles: " + Jordan + " in use, in " + Shared, lab.LogText);
    }

    private static string? SyncEngineOwner(Lab lab) => Armory.Agent.Engine.SyncEngine.OwnerOf(lab.Folder(Shared).State);

    private static async Task UntilAsync(Func<Task<bool>> condition, string what)
    {
        for (var i = 0; i < 600; i++)
        {
            if (await condition()) return;
            await Task.Delay(100);
        }
        Assert.Fail("waited 60 s for " + what);
    }

    [PostgresFact]
    public async Task Switching_while_a_pass_runs_stops_it_cleanly_and_nothing_of_the_old_student_is_written_after()
    {
        await using var lab = await StartAsync(shared: true);
        var shared = lab.Folder(Shared);
        // The team has 60 files; Alex's engine downloads them, then Jordan's takes the folder over
        // (nothing of Alex's waited), and Alex comes back.
        var uploader = await StartUploaderAsync(lab, 60);
        await lab.AddAsync(Alex, "2580");
        await lab.UntilRunningAsync(Alex, Shared);
        await Until(() => shared.Files("Robot 2027/Bulk") == 60, "every file downloaded once");
        await lab.AddAsync(Jordan, "1357");
        await lab.UntilRunningAsync(Jordan, Shared);
        Assert.True((await lab.PickAsync(Alex, "2580")).Ok);
        await lab.UntilRunningAsync(Alex, Shared);
        // The team changes every file again: Alex's engine downloads them, slowly, several at once.
        lab.Network.StorageDelay = r => r.Method == HttpMethod.Get ? TimeSpan.FromMilliseconds(250) : TimeSpan.Zero;
        await uploader.ChangeAllAsync("v2");
        await Until(() => Enumerable.Range(0, 60).Count(i => shared.Text($"Robot 2027/Bulk/Part-{i:D2}.SLDPRT") == "bulk v2 " + i) >= 6, "Alex's downloads under way");
        var picked = await lab.PickAsync(Jordan, "1357");
        Assert.True(picked.Ok, picked.Message);
        await lab.UntilRunningAsync(Jordan, Shared);
        // Alex's engine stopped mid-download and wrote nothing after its runtime closed; Jordan's
        // engine finishes the downloads for the folder.
        lab.Network.StorageDelay = null;
        await Until(() => Enumerable.Range(0, 60).All(i => shared.Text($"Robot 2027/Bulk/Part-{i:D2}.SLDPRT") == "bulk v2 " + i), "every v2 here");
        Assert.Empty(lab.Violations);
        Assert.Empty(shared.Disk.OpenWriteViolations);
        Assert.Empty(shared.Disk.UnpreservedOverwrites);
        Assert.True(lab.RuntimesMade >= 4);
    }

    // A teammate on the website changes the team's files (a person with a device, like World's).
    private sealed class Uploader(Lab lab, ArmoryApi api, BlobClient blobs, Guid device, List<Guid> files)
    {
        public async Task ChangeAllAsync(string version)
        {
            for (var i = 0; i < files.Count; i++)
            {
                var bytes = Encoding.UTF8.GetBytes($"bulk {version} {i}");
                var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
                Assert.True(await api.AcquireLockAsync(files[i], device, Guid.NewGuid()));
                await blobs.UploadAsync(lab.Project, hash, bytes.Length, () => new MemoryStream(bytes));
                var parent = (await api.ProjectFilesAsync(lab.Project)).Single(f => f.Id == files[i]).Current?.Id;
                await api.CommitVersionWithReleaseAsync(files[i], parent, Armory.Storage.ContentObjectKey.FromHash(hash), hash, bytes.Length, device, Guid.NewGuid(), null);
                Assert.True(await api.ReleaseLockAsync(files[i], device, Guid.NewGuid()));
            }
        }
    }

    private static async Task<Uploader> StartUploaderAsync(Lab lab, int count)
    {
        var http = new HttpClient(new FakeNetworkHandler(lab.S3));
        var sessions = new SessionManager(http, new InMemorySecretStore());
        var issued = lab.Supabase.IssueSession(Mentor);
        sessions.SignIn(new ArmorySession(lab.Supabase.SupabaseUrl, lab.Supabase.AnonKey, issued.AccessToken, issued.RefreshToken, issued.ExpiresAt, Mentor, Guid.Empty, "website"));
        var api = new ArmoryApi(new PostgrestClient(http, sessions));
        var blobs = new BlobClient(http, http, lab.Site.BaseUri, sessions);
        var device = await api.RegisterDeviceAsync("mentor laptop", Guid.NewGuid());
        List<Guid> files = [];
        for (var i = 0; i < count; i++)
        {
            var created = await api.CreateFileAsync(lab.Project, "Bulk", $"Part-{i:D2}.SLDPRT", device, Guid.NewGuid());
            files.Add(created);
        }
        var uploader = new Uploader(lab, api, blobs, device, files);
        await uploader.ChangeAllAsync("v1");
        return uploader;
    }

    [PostgresFact]
    public async Task Work_waiting_offers_wait_or_own_folder_and_wait_changes_nothing()
    {
        await using var lab = await StartAsync(shared: true);
        var shared = lab.Folder(Shared);
        await lab.AddAsync(Alex, "2580");
        await lab.UntilRunningAsync(Alex, Shared);
        Write(shared, Plate, "v1");
        await UntilOnServer(lab, "Plate.SLDPRT", "v1");
        await Until(() => shared.Disk.IsReadOnly(Plate), "Plate read-only");
        Assert.True((await lab.Host.CheckOutAsync([Plate], false)).Ok);
        Save(shared, Plate, "Alex, not checked in");
        await lab.AddAsync(Jordan, "1357");

        // Jordan picked (and typed his PIN): Alex's work waits in the folder, so it stays his.
        await Until(() => lab.Step.Kind == PickerSteps.FolderBusy, "the folder busy step");
        Assert.Equal((Alex, "Alex Kim", "1 file checked out", @"C:\IDEA\Armory-jordan"), (lab.InUse, lab.Step.OwnerName, lab.Step.OwnerWaiting, lab.Step.OwnFolder));
        Assert.Equal(Alex, lab.Host.Sessions.Current!.Email);
        // Wait: nothing changes; Alex's engine keeps working, his check out and his bytes intact.
        Assert.True((await lab.Host.ChooseFolderAsync(lab.IdOf(Jordan), "wait")).Ok);
        Assert.Equal(PickerSteps.Choose, lab.Step.Kind);
        Assert.Equal(Alex, lab.InUse);
        Assert.Equal("Alex, not checked in", shared.Text(Plate));
        Assert.False(shared.Disk.IsReadOnly(Plate));
        Assert.Equal(Alex, (await OnServer(lab, "Plate.SLDPRT"))!.Lock!.HolderEmail);
        Assert.Empty(lab.Violations);
    }

    [PostgresFact]
    public async Task Own_folder_keeps_the_last_students_work_theirs_and_they_finish_it_later()
    {
        await using var lab = await StartAsync(shared: true);
        var shared = lab.Folder(Shared);
        var own = lab.Folder(@"C:\IDEA\Armory-jordan");
        await lab.AddAsync(Alex, "2580");
        Write(shared, Plate, "v1");
        await UntilOnServer(lab, "Plate.SLDPRT", "v1");
        await Until(() => shared.Disk.IsReadOnly(Plate), "Plate read-only");
        Assert.True((await lab.Host.CheckOutAsync([Plate], false)).Ok);
        Save(shared, Plate, "Alex, not checked in");
        await lab.AddAsync(Jordan, "1357");
        await Until(() => lab.Step.Kind == PickerSteps.FolderBusy, "the folder busy step");

        // Jordan uses a folder of his own: Alex's check out is read-only while he is away.
        Assert.True((await lab.Host.ChooseFolderAsync(lab.IdOf(Jordan), "own")).Ok);
        await lab.UntilRunningAsync(Jordan, @"C:\IDEA\Armory-jordan");
        await Until(() => own.Has(Plate), "the team's files in Jordan's folder");
        Assert.Equal("v1", own.Text(Plate));
        Assert.True(shared.Disk.IsReadOnly(Plate));
        Assert.Throws<IOException>(() => Save(shared, Plate, "Jordan's save in Alex's file"));
        Assert.Equal("Alex, not checked in", shared.Text(Plate));
        Assert.Equal("1 file checked out", lab.Tile(Alex).Waiting);
        Assert.Equal($"You're in your own folder, C:\\IDEA\\Armory-jordan, while Alex's work waits in {Shared}.", lab.Profiles.Note);

        // Alex comes back: the shared folder is his, his check out writable again, and he checks in.
        Assert.True((await lab.PickAsync(Alex, "2580")).Ok);
        await lab.UntilRunningAsync(Alex, Shared);
        await Until(() => !shared.Disk.IsReadOnly(Plate), "Alex's check out writable again");
        Save(shared, Plate, "Alex v2");
        Assert.Equal("Checked in Plate.SLDPRT.", (await lab.Host.CheckInAsync([Plate])).Message);
        Assert.Equal(Alex, (await lab.MentorApi.FileHistoryAsync((await OnServer(lab, "Plate.SLDPRT"))!.Id)).First().Author);

        // Jordan's next pick moves him back to the shared folder by itself; his own folder is left as it is.
        var ownFiles = Directory.EnumerateFiles(own.Disk.Root, "*", SearchOption.AllDirectories).Where(f => !f.Contains(Path.DirectorySeparatorChar + ".armory" + Path.DirectorySeparatorChar)).Count();
        Assert.True((await lab.PickAsync(Jordan, "1357")).Ok);
        await lab.UntilRunningAsync(Jordan, Shared);
        Assert.Equal($"You're back in {Shared}. Armory doesn't use C:\\IDEA\\Armory-jordan any more; you can delete it in File Explorer.", lab.Profiles.Note);
        Assert.Equal(Shared, lab.Tile(Jordan).Folder);
        Assert.Equal("Alex v2", shared.Text(Plate));
        Assert.Equal(ownFiles, Directory.EnumerateFiles(own.Disk.Root, "*", SearchOption.AllDirectories).Where(f => !f.Contains(Path.DirectorySeparatorChar + ".armory" + Path.DirectorySeparatorChar)).Count());
        Assert.Empty(lab.Violations);
        Assert.Empty(shared.Disk.OpenWriteViolations);
        Assert.Empty(own.Disk.UnpreservedOverwrites);
    }

    // ---- Adding and removing -----------------------------------------------------------------

    [PostgresFact]
    public async Task Adding_a_student_signs_into_a_new_profile_and_cancel_or_the_same_address_leaves_no_second_one()
    {
        await using var lab = await StartAsync(shared: true);
        // Add, then Cancel while the browser waits: nothing is kept.
        lab.Browser.Next = null;
        Assert.True((await lab.Host.AddProfileAsync()).Ok);
        Assert.Equal((PickerSteps.Adding, "waitingForBrowser"), (lab.Step.Kind, lab.Step.ConnectPhase));
        lab.Host.CancelPicker();
        Assert.Equal(PickerSteps.Choose, lab.Step.Kind);
        await Until(() => !Directory.Exists(lab.Paths.ProfilesFolder) || Directory.GetDirectories(lab.Paths.ProfilesFolder).Length == 0, "no profile folder left");
        Assert.Empty(lab.Profiles.Profiles);

        // "Open the browser again" stops the sign-in under way and starts a new one; signed in
        // in the second, then Cancel at the PIN: not kept either, and its sign-in is forgotten.
        lab.Browser.Next = null;
        var opened = lab.Browser.Opened;
        Assert.True((await lab.Host.AddProfileAsync()).Ok);
        await Until(() => Volatile.Read(ref lab.Browser.Opened) == opened + 1, "the browser opened");
        lab.Browser.Next = Alex;
        Assert.True((await lab.Host.AddProfileAsync()).Ok);
        await Until(() => lab.Step.Kind == PickerSteps.NewPin, "the PIN step");
        Assert.Equal(opened + 2, Volatile.Read(ref lab.Browser.Opened));
        await Until(() => Directory.GetDirectories(lab.Paths.ProfilesFolder).Length == 1, "only the second sign-in's profile left");
        lab.Host.CancelPicker();
        await Until(() => Directory.GetDirectories(lab.Paths.ProfilesFolder).Length == 0, "the signed-in profile forgotten");
        Assert.Empty(lab.Profiles.Profiles);
        Assert.Empty(lab.Secrets());

        // Added for real; adding the same address again renews that profile (and asks for a PIN).
        var alex = await lab.AddAsync(Alex, "2580");
        await lab.UntilRunningAsync(Alex, Shared);
        var before = lab.Host.Sessions.Current!.RefreshToken;
        lab.Host.ShowPicker(PickerTrigger.SwitchStudent);
        lab.Browser.Next = Alex;
        Assert.True((await lab.Host.AddProfileAsync()).Ok);
        await Until(() => lab.Step.Kind == PickerSteps.NewPin, "Alex's PIN again");
        Assert.Equal(alex, lab.Step.ProfileId);
        Assert.Single(lab.Profiles.Profiles);
        Assert.True((await lab.Host.SetPinAsync(alex, "4682")).Ok);
        await lab.UntilRunningAsync(Alex, Shared);
        Assert.NotEqual(before, lab.Host.Sessions.Current!.RefreshToken);
        Assert.Single(Directory.GetDirectories(lab.Paths.ProfilesFolder));
        Assert.Single(lab.Secrets(), s => s.Text.Contains(lab.Host.Sessions.Current!.RefreshToken, StringComparison.Ordinal));
    }

    [PostgresFact]
    public async Task Removing_a_profile_forgets_its_sign_in_and_pin_and_deletes_no_file()
    {
        await using var lab = await StartAsync(shared: true);
        var shared = lab.Folder(Shared);
        await lab.AddAsync(Alex, "2580");
        await lab.UntilRunningAsync(Alex, Shared);
        Write(shared, Plate, "v1");
        await UntilOnServer(lab, "Plate.SLDPRT", "v1");
        await lab.AddAsync(Jordan, "1357");
        await lab.UntilRunningAsync(Jordan, Shared);
        var files = lab.AllFiles();
        var alex = lab.IdOf(Alex);

        // A student may not remove another (PINs on, no mentor in use).
        Assert.False(lab.Tile(Alex).CanRemove);
        Assert.Equal((false, "Only a mentor can remove another student from this computer."), Pair(await lab.Host.RemoveProfileAsync(alex)));

        // Jordan removes himself: his runtime stops, the picker shows, nothing is deleted.
        Assert.True(lab.Tile(Jordan).CanRemove);
        Assert.Equal((true, "Jordan Reyes was removed from this computer. No files were deleted."), Pair(await lab.Host.RemoveProfileAsync(lab.IdOf(Jordan))));
        Assert.True(lab.Profiles.Showing);
        Assert.Null(lab.InUse);
        Assert.Equal([Alex], lab.Profiles.Profiles.Select(p => p.Email));
        Assert.DoesNotContain(lab.Secrets(), s => s.Text.Contains(Jordan, StringComparison.Ordinal));
        Assert.Single(Directory.GetDirectories(lab.Paths.ProfilesFolder));
        Assert.Equal(files, lab.AllFiles());
        Assert.Equal(Jordan, SyncEngineOwner(lab));
        Assert.Contains("profiles: removed " + Jordan + "; no file was deleted", lab.LogText);
        Assert.Empty(lab.Violations);
    }

    [PostgresFact]
    public async Task Pins_can_be_turned_off_only_by_a_mentor_in_use_and_who_and_when_are_kept()
    {
        await using var lab = await StartAsync(shared: true);
        await lab.AddAsync(Alex, "2580");
        await lab.UntilRunningAsync(Alex, Shared);
        Assert.False(lab.Profiles.CanChangePins);
        Assert.Equal((false, "Only a mentor can change this."), Pair(await lab.Host.SetPinsRequiredAsync(false)));
        Assert.True(lab.Profiles.PinsRequired);

        // Mr. Pina adds himself and, as a mentor in use, turns PINs off on this computer.
        await lab.AddAsync(Mentor, "8642");
        await lab.UntilRunningAsync(Mentor, Shared);
        await Until(() => lab.Host.View.Profiles!.CanChangePins, "the mentor's projects read");
        Assert.True((await lab.Host.SetPinsRequiredAsync(false)).Ok);
        Assert.False(lab.Profiles.PinsRequired);
        Assert.Equal("Turned off by Pina on Oct 9.", lab.Profiles.PinsNote);
        Assert.True(lab.Tile(Alex).CanRemove);
        var index = JsonDocument.Parse(File.ReadAllText(Path.Combine(lab.Paths.ProfilesFolder, "profiles.json"))).RootElement;
        Assert.Equal((Mentor, false), (index.GetProperty("pinsChangedBy").GetString(), index.GetProperty("pinsRequired").GetBoolean()));

        // With PINs off a pick switches at once; Alex can't turn them back on.
        Assert.True((await lab.PickAsync(Alex)).Ok);
        await lab.UntilRunningAsync(Alex, Shared);
        Assert.False((await lab.Host.SetPinsRequiredAsync(true)).Ok);
    }

    [PostgresFact]
    public async Task A_note_is_sent_only_while_its_writer_is_in_use()
    {
        await using var lab = await StartAsync(shared: true);
        await ArmoryV3StandIn.ApplyReportsAsync(lab.Database);
        await lab.AddAsync(Alex, "2580");
        await lab.UntilRunningAsync(Alex, Shared);
        // Alex writes a note while the site can't be reached: it is saved here, his.
        lab.Network.Refuse = r => r.RequestUri!.AbsolutePath.EndsWith("/armory_submit_app_feedback", StringComparison.Ordinal);
        Assert.Equal((true, "Saved. It will be sent when this computer is back online."), await lab.Telemetry.SendFeedbackAsync("idea", "Alex's idea"));
        await lab.AddAsync(Jordan, "1357");
        await lab.UntilRunningAsync(Jordan, Shared);
        lab.Network.Refuse = null;

        // Jordan's own note goes, under his account; Alex's waits (never sent as Jordan).
        Assert.Equal((true, "Sent. Thank you for the feedback."), await lab.Telemetry.SendFeedbackAsync("idea", "Jordan's idea"));
        await Task.Delay(1500);
        Assert.Equal([(Jordan, "Jordan's idea")], await NotesAsync(lab));

        // Alex is back: his note goes under his own account (with his next one).
        Assert.True((await lab.PickAsync(Alex, "2580")).Ok);
        await lab.UntilRunningAsync(Alex, Shared);
        Assert.Equal((true, "Sent. Thank you for the feedback."), await lab.Telemetry.SendFeedbackAsync("bug", "Alex's second note"));
        await UntilAsync(async () => (await NotesAsync(lab)).Count == 3, "Alex's first note sent");
        Assert.Equal([(Alex, "Alex's idea"), (Alex, "Alex's second note"), (Jordan, "Jordan's idea")], (await NotesAsync(lab)).Order().ToList());
    }

    private static async Task<List<(string, string)>> NotesAsync(Lab lab)
    {
        await using var c = await lab.Database.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand("select email, body from public.armory_app_feedback order by created_at", c);
        await using var reader = await command.ExecuteReaderAsync();
        List<(string, string)> rows = [];
        while (await reader.ReadAsync()) rows.Add((reader.GetString(0), reader.GetString(1)));
        return rows;
    }
}
