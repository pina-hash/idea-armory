using Armory.TestSupport;
using static Armory.EndToEnd.Tests.ScenarioTests;

namespace Armory.EndToEnd.Tests;

// Robotics work is safe from whoever signs in next on a lab computer (0.5.0, ledger 0437; Mr.
// Pina's report: freshmen use the same computers as the robotics team). On one shared folder, a
// project the person in use is not in is not shown to them and every file of it is read-only for
// them from their first pass; anything they still manage to save there (the bit cleared by hand) is
// never sent, never anyone's work, and never left in place of a member's: when a member is back,
// those bytes go to Armory's recovery folder and the member's own version (their waiting save, or
// the team's) comes back. A held file someone else wrote over is put back the same way.
public sealed class ProjectSafetyTests
{
    private const string Jordan = "jordan.reyes@students.test";
    private const string Bracket = "Robot 2027/Drivetrain/Bracket.SLDPRT", Stray = "Robot 2027/Drivetrain/Jordans part.SLDPRT";

    private static Task<List<string>> Authors(Team t, Guid file)
        => t.World.QueryAsync("select author_email from armory_versions where file_id=@f order by created_at", r => r.GetString(0), ("f", file));

    private static Task<long> FilesNamed(Team t, string name)
        => t.World.CountAsync("select count(*) from armory_files where name=@n", ("n", name));

    // Every file in the computer's recovery folder, by its text.
    private static List<string> Recovered(Computer c)
    {
        var folder = c.Disk.Full(".armory/recovery");
        return Directory.Exists(folder) ? [.. Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Select(File.ReadAllText).Order(StringComparer.Ordinal)] : [];
    }

    [PostgresFact]
    public async Task A_save_by_someone_not_in_the_project_changes_nothing_its_members_own()
    {
        await using var t = await TeamAsync();
        var box = await t.Mentor.Api.CreateProjectAsync("Freshman Box", 2027, Guid.NewGuid());
        await t.Mentor.Api.AddMemberAsync(box, Jordan, Armory.Client.MemberRole.Student, Guid.NewGuid());
        t.A.Write(Plate, "plate v1");
        t.A.Write(Bracket, "bracket v1");
        await t.A.SyncAsync();
        var plate = await t.FileId("Plate.SLDPRT");
        var bracket = await t.FileId("Bracket.SLDPRT");
        // Alex leaves a check out with a save of his waiting in it.
        Assert.True((await t.A.CheckOutAsync(Bracket)).Ok);
        t.A.Save(Bracket, "Alex's bracket, waiting");
        await t.A.SyncAsync();
        // (Armory keeps a save made while checked out as his kept copy on the server, under him.)
        Assert.Equal([Alex + "|saved while checked out"], await t.SideAuthors(bracket));

        // A freshman who isn't in Robot 2027 signs in on the same computer: the project isn't
        // shown to him, and from his first pass every file of it is read-only for him.
        await t.A.ConnectAsync(Jordan);
        await t.A.SyncAsync();
        Assert.Contains(t.A.Engine.View.Projects, p => p.Name == "Freshman Box");
        Assert.DoesNotContain(t.A.Engine.View.Projects, p => p.Name == "Robot 2027");
        Assert.True(t.A.Disk.IsReadOnly(Plate));
        Assert.True(t.A.Disk.IsReadOnly(Bracket));
        Assert.Throws<IOException>(() => t.A.Save(Plate, "Jordan's plate"));
        Assert.Throws<IOException>(() => t.A.Save(Bracket, "Jordan's bracket"));

        // He clears the read-only bit by hand and saves over both anyway, and adds a part there.
        t.A.ForceWrite(Plate, "Jordan's plate");
        t.A.ForceWrite(Bracket, "Jordan's bracket");
        t.A.Write(Stray, "Jordan's part");
        var first = await t.A.SyncAsync();
        var second = await t.A.SyncAsync();
        // Nothing of it is sent, and both are read-only again at once.
        Assert.Equal((0, 0, 0, 0), (first.Uploaded, first.SideVersions, second.Uploaded, second.SideVersions));
        Assert.True(t.A.Disk.IsReadOnly(Plate));
        Assert.True(t.A.Disk.IsReadOnly(Bracket));
        Assert.Equal((1L, 1L, 0L), (await t.Versions(plate), await t.Versions(bracket), await t.Sides(plate)));
        Assert.Equal([Alex + "|saved while checked out"], await t.SideAuthors(bracket));
        Assert.Equal(0, await FilesNamed(t, "Jordans part.SLDPRT"));

        // Alex is back: his waiting save is his again, the team's plate is back, and Jordan's bytes
        // are in the recovery folder, nowhere else.
        await t.A.ConnectAsync(Alex);
        await t.A.SyncTimesAsync(2);
        Assert.Equal("Alex's bracket, waiting", t.A.Text(Bracket));
        Assert.False(t.A.Disk.IsReadOnly(Bracket));
        Assert.Equal("plate v1", t.A.Text(Plate));
        Assert.True(t.A.Disk.IsReadOnly(Plate));
        Assert.Null(t.A.Text(Stray));
        Assert.Equal(["Jordan's bracket", "Jordan's part", "Jordan's plate"], Recovered(t.A));
        var lines = t.A.Engine.View.Activity.Log.Select(l => l.Line).ToList();
        Assert.Contains("Jordan changed 2 files in a project Jordan isn't in. Nothing of it was sent: Armory keeps it in the .armory\\recovery folder and puts the team's version back.", lines);
        Assert.Contains("Someone changed one of your waiting files while you were away. Armory put your own version back and kept theirs in the .armory\\recovery folder.", lines);
        Assert.Equal("Checked in Bracket.SLDPRT.", (await t.A.CheckInAsync(Bracket)).Message);
        await t.A.SyncAsync();

        // On the server: Alex's work under Alex, and nothing of Jordan's in the project at all.
        Assert.Equal([Alex, Alex], await Authors(t, bracket));
        Assert.Equal(Hash("Alex's bracket, waiting"), await t.CurrentHash(bracket));
        Assert.Equal(Hash("plate v1"), await t.CurrentHash(plate));
        Assert.Equal((1L, 0L), (await t.Versions(plate), await t.Sides(plate)));
        Assert.All(await t.SideAuthors(bracket), a => Assert.StartsWith(Alex + "|", a));
        Assert.Equal(0, await FilesNamed(t, "Jordans part.SLDPRT"));
        Assert.Equal(0, await t.World.CountAsync("select count(*) from armory_versions v join armory_files f on f.id=v.file_id where f.project_id=@p and v.author_email=@e", ("p", t.Project), ("e", Jordan)));
        // Every move to the recovery folder was one of Jordan's three files (bytes no server has:
        // nobody's to send), and nothing was written over a file someone had open.
        Assert.Equal(3, t.A.Disk.UnpreservedOverwrites.Count);
        Assert.All(t.A.Disk.UnpreservedOverwrites, u => Assert.StartsWith("recovery Robot 2027/Drivetrain/", u));
        Assert.Empty(t.A.Disk.OpenWriteViolations);

        // Jordan again: nothing waits for anyone, and his own project is untouched.
        await t.A.ConnectAsync(Jordan);
        await t.A.SyncAsync();
        Assert.Empty(t.A.Engine.View.HeldForOthers!);
        Assert.True(t.A.Disk.IsReadOnly(Plate));
    }

    [PostgresFact]
    public async Task A_held_file_another_member_writes_over_comes_back_as_its_owners_and_theirs_is_never_sent_as_him()
    {
        await using var t = await TeamAsync();
        t.A.Write(Bracket, "bracket v1");
        await t.A.SyncAsync();
        var bracket = await t.FileId("Bracket.SLDPRT");
        Assert.True((await t.A.CheckOutAsync(Bracket)).Ok);
        t.A.Save(Bracket, "Alex's bracket, waiting");
        await t.A.SyncAsync();

        // Maria is in the project too, but the bracket is Alex's waiting work: she clears the bit
        // and saves over it.
        await t.A.ConnectAsync(Maria);
        await t.A.SyncAsync();
        Assert.True(t.A.Disk.IsReadOnly(Bracket));
        t.A.ForceWrite(Bracket, "Maria's bracket");
        var hers = await t.A.SyncAsync();
        Assert.Equal((0, 0), (hers.Uploaded, hers.SideVersions));
        Assert.True(t.A.Disk.IsReadOnly(Bracket));

        await t.A.ConnectAsync(Alex);
        await t.A.SyncTimesAsync(2);
        Assert.Equal("Alex's bracket, waiting", t.A.Text(Bracket));
        Assert.Equal(["Maria's bracket"], Recovered(t.A));
        Assert.Equal("Checked in Bracket.SLDPRT.", (await t.A.CheckInAsync(Bracket)).Message);
        await t.A.SyncAsync();
        Assert.Equal([Alex, Alex], await Authors(t, bracket));
        Assert.Equal(Hash("Alex's bracket, waiting"), await t.CurrentHash(bracket));
        Assert.All(await t.SideAuthors(bracket), a => Assert.StartsWith(Alex + "|", a));
        Assert.Empty(t.A.Disk.OpenWriteViolations);
    }

    // The freshman still has the file open in SolidWorks when a member is back: it stays where it
    // is, untouched and read-only, and is nobody's (never captured, sent or planned over) until it
    // is closed; then it goes aside and the team's version comes back.
    [PostgresFact]
    public async Task A_change_by_someone_not_in_the_project_that_is_still_open_waits_untouched_and_is_never_sent()
    {
        await using var t = await TeamAsync();
        var box = await t.Mentor.Api.CreateProjectAsync("Freshman Box", 2027, Guid.NewGuid());
        await t.Mentor.Api.AddMemberAsync(box, Jordan, Armory.Client.MemberRole.Student, Guid.NewGuid());
        t.A.Write(Plate, "plate v1");
        await t.A.SyncAsync();
        var plate = await t.FileId("Plate.SLDPRT");

        await t.A.ConnectAsync(Jordan);
        await t.A.SyncAsync();
        t.A.ForceWrite(Plate, "Jordan's plate");
        t.A.Open(Plate);
        await t.A.SyncAsync();

        await t.A.ConnectAsync(Alex);
        for (var i = 0; i < 3; i++)
        {
            var pass = await t.A.SyncAsync();
            Assert.Equal((0, 0), (pass.Uploaded, pass.SideVersions));
        }
        Assert.Equal("Jordan's plate", t.A.Text(Plate));
        Assert.True(t.A.Disk.IsReadOnly(Plate));
        Assert.Empty(Recovered(t.A));
        Assert.Equal((1L, 0L), (await t.Versions(plate), await t.Sides(plate)));
        Assert.DoesNotContain(new Armory.Core.OfflineJournal(t.A.Journal).Read().Entries, e => e.Path == Plate && e.Hash == Hash("Jordan's plate"));
        // His check out of it waits too: the file is nobody's while it is open.
        Assert.False((await t.A.CheckOutAsync(Plate)).Ok);

        t.A.Close(Plate);
        await t.A.SyncTimesAsync(2);
        Assert.Equal("plate v1", t.A.Text(Plate));
        Assert.Equal(["Jordan's plate"], Recovered(t.A));
        Assert.Equal((1L, 0L), (await t.Versions(plate), await t.Sides(plate)));
        Assert.True((await t.A.CheckOutAsync(Plate)).Ok);
        Assert.Empty(t.A.Disk.OpenWriteViolations);
    }
}
