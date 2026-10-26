using System.Security.Cryptography;
using Armory.Agent.Engine;
using Armory.TestSupport;
using static Armory.EndToEnd.Tests.ScenarioTests;

namespace Armory.EndToEnd.Tests;

// A lab computer updating from 0.4.x with a folder of one's own beside the shared one (0.5.0,
// ledger 0437; FolderFoldRunner): Maria worked in C:\IDEA\Armory-maria while Alex's work waited in
// C:\IDEA\Armory. The fold brings her folder into the shared one with nothing downloaded again,
// nothing lost, and her waiting work held for her; the next person in the shared folder sees it
// read-only and never sends it; Maria, back, finishes it under her own sign-in, and her check out
// is still hers although it was taken in the other folder.
public sealed class FoldTests
{
    private const string Gear = "Robot 2027/Drivetrain/Gear.SLDPRT", Bracket = "Robot 2027/Drivetrain/Bracket.SLDPRT";

    private static HashSet<string> Hashes(params string[] roots)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in roots.Where(Directory.Exists))
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                found.Add(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file))));
        return found;
    }

    [PostgresFact]
    public async Task A_folder_of_ones_own_folds_into_the_shared_one_and_its_work_stays_its_owners()
    {
        await using var t = await TeamAsync();
        // The shared folder: Alex's computer (t.A). Maria's own folder on it: t.B, a folder of its own.
        t.A.Write(Plate, "plate v1");
        t.A.Write(Gear, "gear v1");
        await t.A.SyncAsync();
        await t.B.SyncAsync();
        Assert.Equal("gear v1", t.B.Text(Gear));
        // In her own folder Maria checks Plate out and changes it, and makes a bracket offline.
        Assert.True((await t.B.CheckOutAsync(Plate)).Ok);
        t.B.Save(Plate, "Maria's plate, not checked in");
        t.B.Offline = true;
        t.B.Write(Bracket, "Maria's bracket, made offline");
        await t.B.SyncAsync();
        t.B.Offline = false;
        await t.B.Engine.StopForGoodAsync();
        await t.A.Engine.StopForGoodAsync();
        var before = Hashes(t.A.Disk.Root, t.B.Disk.Root);

        // The update's first start folds Maria's folder in.
        List<string> log = [];
        var outcome = FolderFoldRunner.Run(t.A.Disk.Root, t.B.Disk.Root, new FoldDisk(), root => root == t.A.Disk.Root ? t.A.State : t.B.State, log.Add);
        Assert.Equal((Maria, 2, 0, true), (outcome.Owner, outcome.HeldFiles, outcome.Left, outcome.Removed));
        Assert.Equal(["Robot 2027"], outcome.Projects);
        Assert.False(Directory.Exists(t.B.Disk.Root));
        Assert.Equal("Maria's plate, not checked in", t.A.Text(Plate));
        Assert.Equal("Maria's bracket, made offline", t.A.Text(Bracket));
        Assert.Equal("gear v1", t.A.Text(Gear));
        // Nothing lost: every byte string either folder had is still on this disk (the team's copy
        // of Plate it replaced is set aside in .armory\folded).
        Assert.Subset(Hashes(t.A.Disk.Root), before);
        Assert.Equal(2, SyncEngine.HeldOf(t.A.State)[Maria]);
        // A second run (the next start) finds nothing to do.
        Assert.Equal((0, 0), (FolderFoldRunner.Run(t.A.Disk.Root, t.B.Disk.Root, new FoldDisk(), root => root == t.A.Disk.Root ? t.A.State : t.B.State).HeldFiles, 0));

        // Alex is in use on the shared folder: nothing comes down again, nothing of Maria's goes up,
        // and her files are read-only for him.
        t.A.Restart();
        var alex = await t.A.SyncAsync();
        Assert.Equal((0, 0), (alex.Downloaded, alex.Uploaded));
        Assert.True(t.A.Disk.IsReadOnly(Plate));
        Assert.True(t.A.Disk.IsReadOnly(Bracket));
        Assert.Equal(0, await t.World.CountAsync("select count(*) from armory_files where name='Bracket.SLDPRT'"));
        var plate = await t.FileId("Plate.SLDPRT");
        Assert.Equal(Hash("plate v1"), await t.CurrentHash(plate));
        Assert.Equal(2, Assert.Single(t.A.Engine.View.HeldForOthers!).Files);

        // Maria picks her name in the shared folder: her check out (taken in her own folder, under
        // that folder's device) is hers here, and her work goes up under her name.
        await t.A.ConnectAsync(Maria);
        await t.A.SyncAsync();
        await t.A.SyncAsync();
        Assert.False(t.A.Disk.IsReadOnly(Plate));
        Assert.Equal(Maria, (await t.World.QueryAsync("select v.author_email from armory_files f join armory_versions v on v.id = f.current_version_id where f.name='Bracket.SLDPRT'", r => r.GetString(0))).Single());
        Assert.Equal("Checked in Plate.SLDPRT.", (await t.A.CheckInAsync(Plate)).Message);
        Assert.Equal(Hash("Maria's plate, not checked in"), await t.CurrentHash(plate));
        Assert.Equal(new[] { Alex, Maria }, await t.World.QueryAsync("select author_email from armory_versions where file_id=@f order by created_at", r => r.GetString(0), ("f", plate)));
        Assert.Empty(t.A.Engine.View.HeldForOthers!);
        Assert.Empty(t.A.Disk.OpenWriteViolations);
        Assert.Empty(t.A.Disk.UnpreservedOverwrites);
    }
}
