using Armory.Core;

namespace Armory.Core.Tests;

// Several students on one computer (docs/agent/PROFILES.md): the PIN's format and its waits,
// when the picker shows, the hand-over table, the folder of a student's own, names and
// initials, and who may remove a student, turn shared mode off or change whether PINs are asked.
// One test per rule in SharedComputer and PinHash.
public sealed class SharedComputerTests
{
    private const string Shared = @"C:\IDEA\Armory";
    private const string Alex = "alex.kim@boscotech.edu", Jordan = "jordan.reyes@boscotech.edu", Sam = "sam.patel@boscotech.edu";

    [Fact]
    public void Pins_are_exactly_four_ascii_digits()
    {
        Assert.True(SharedComputer.IsPin("2580"));
        Assert.True(SharedComputer.IsPin("0000"));
        foreach (var bad in new[] { null, "", "123", "12345", "12a4", " 123", "123 ", "１２３４", "12.4", "-123" })
            Assert.False(SharedComputer.IsPin(bad), $"'{bad}' is not a PIN");
    }

    [Fact]
    public void Easy_pins_are_refused_with_a_plain_reason()
    {
        foreach (var easy in new[] { "0000", "1111", "9999", "0123", "1234", "6789", "4321", "9876", "3210" })
            Assert.Equal(SharedComputer.TooEasyPin, SharedComputer.TooEasy(easy));
        foreach (var fine in new[] { "2580", "1357", "1243", "0912", "8890", "1122" })
            Assert.Null(SharedComputer.TooEasy(fine));
        Assert.Equal("Type 4 digits.", SharedComputer.TooEasy("12"));
    }

    [Fact]
    public void Pin_waits_start_after_five_wrong_tries_double_and_stop_at_fifteen_minutes()
    {
        for (var tries = 0; tries < 5; tries++) Assert.Null(SharedComputer.PinWait(tries));
        Assert.Equal(TimeSpan.FromSeconds(30), SharedComputer.PinWait(5));
        Assert.Equal(TimeSpan.FromSeconds(60), SharedComputer.PinWait(6));
        Assert.Equal(TimeSpan.FromMinutes(2), SharedComputer.PinWait(7));
        Assert.Equal(TimeSpan.FromMinutes(4), SharedComputer.PinWait(8));
        Assert.Equal(TimeSpan.FromMinutes(8), SharedComputer.PinWait(9));
        Assert.Equal(TimeSpan.FromMinutes(15), SharedComputer.PinWait(10));
        Assert.Equal(TimeSpan.FromMinutes(15), SharedComputer.PinWait(400));
        Assert.Equal(TimeSpan.FromMinutes(15), SharedComputer.PinWait(int.MaxValue));
        Assert.Equal([5, 4, 1, 0, 0], new[] { 0, 1, 4, 5, 9 }.Select(SharedComputer.PinTriesLeft));
    }

    [Fact]
    public void A_pin_record_matches_only_its_pin_and_salts_differ()
    {
        var one = PinHash.Create("2580", iterations: 1000);
        var two = PinHash.Create("2580", iterations: 1000);
        Assert.Equal(1000, one.Iterations);
        Assert.Equal(PinHash.SaltBytes, one.Salt.Length);
        Assert.Equal(PinHash.HashBytes, one.Hash.Length);
        Assert.NotEqual(one.Salt, two.Salt);
        Assert.NotEqual(one.Hash, two.Hash);
        var now = DateTimeOffset.Parse("2026-10-09T15:00:00Z");
        Assert.Equal(PinCheck.Right, PinHash.Check(one, "2580", now, out _));
        Assert.Equal(PinCheck.Wrong, PinHash.Check(one, "2581", now, out _));
        Assert.Equal(PinCheck.Wrong, PinHash.Check(one, "258", now, out _));
        // The iterations come from the record: the same PIN under another count is another hash.
        Assert.Equal(PinCheck.Wrong, PinHash.Check(one with { Iterations = 999 }, "2580", now, out _));
        var tampered = (byte[])one.Hash.Clone();
        tampered[0] ^= 1;
        Assert.Equal(PinCheck.Wrong, PinHash.Check(one with { Hash = tampered }, "2580", now, out _));
        Assert.Throws<ArgumentException>(() => PinHash.Create("12a4"));
        Assert.Equal(600_000, PinHash.Iterations);
    }

    [Fact]
    public void Pin_check_counts_wrong_tries_waits_and_resets_on_the_right_pin()
    {
        var now = DateTimeOffset.Parse("2026-10-09T15:00:00Z");
        var record = PinHash.Create("2580", iterations: 1000);
        for (var i = 1; i <= 4; i++)
        {
            Assert.Equal(PinCheck.Wrong, PinHash.Check(record, "1111", now, out record));
            Assert.Equal((i, (DateTimeOffset?)null), (record.WrongTries, record.WaitUntil));
        }
        // A right PIN before the fifth wrong one clears the count.
        Assert.Equal(PinCheck.Right, PinHash.Check(record, "2580", now, out record));
        Assert.Equal(0, record.WrongTries);
        for (var i = 1; i <= 5; i++) PinHash.Check(record, "1111", now, out record);
        Assert.Equal(now.AddSeconds(30), record.WaitUntil);
        // During the wait even the right PIN is refused, and nothing changes.
        Assert.Equal(PinCheck.Waiting, PinHash.Check(record, "2580", now.AddSeconds(29), out var during));
        Assert.Same(record, during);
        // After it, a sixth wrong PIN waits twice as long.
        Assert.Equal(PinCheck.Wrong, PinHash.Check(record, "1111", now.AddSeconds(30), out record));
        Assert.Equal(now.AddSeconds(90), record.WaitUntil);
        // Never a lockout for good: the right PIN after the wait gets in and clears everything.
        Assert.Equal(PinCheck.Right, PinHash.Check(record, "2580", now.AddSeconds(90), out record));
        Assert.Equal((0, (DateTimeOffset?)null), (record.WrongTries, record.WaitUntil));
    }

    [Fact]
    public void The_picker_shows_on_open_from_hidden_new_day_windows_lock_and_switch_but_not_on_minimize()
    {
        var today = new DateOnly(2026, 10, 9);
        var yesterday = today.AddDays(-1);
        Assert.True(SharedComputer.ShowPicker(PickerTrigger.ShownFromHidden, today, today));
        Assert.True(SharedComputer.ShowPicker(PickerTrigger.WindowsLocked, today, today));
        Assert.True(SharedComputer.ShowPicker(PickerTrigger.SwitchStudent, today, today));
        Assert.True(SharedComputer.ShowPicker(PickerTrigger.SignInEnded, today, today));
        Assert.True(SharedComputer.ShowPicker(PickerTrigger.Activated, yesterday, today));
        Assert.True(SharedComputer.ShowPicker(PickerTrigger.Activated, null, today));
        Assert.False(SharedComputer.ShowPicker(PickerTrigger.Activated, today, today));
        Assert.False(SharedComputer.ShowPicker(PickerTrigger.Minimized, yesterday, today));
        Assert.False(SharedComputer.ShowPicker(PickerTrigger.Start, null, today));
    }

    [Fact]
    public void Folder_plan_follows_the_hand_over_table()
    {
        var own = @"C:\IDEA\Armory-jordan";
        FolderPlan Plan(string? owner, string? running, string? runningFolder, string folder = Shared, int waiting = 0, bool open = false)
            => SharedComputer.PlanFolder(new FolderFacts(Jordan, Shared, owner, running, runningFolder, folder, waiting, open));

        // The shared folder is nobody's yet, or already Jordan's.
        Assert.Equal(new FolderPlan(FolderStep.UseShared, Shared), Plan(null, Alex, Shared));
        Assert.Equal(new FolderPlan(FolderStep.UseShared, Shared), Plan("JORDAN.reyes@boscotech.edu", Alex, @"C:\IDEA\Armory-alex"));
        // It is Alex's and Alex runs on it: ask Alex's engine what waits.
        Assert.Equal(new FolderPlan(FolderStep.AskRunningOwner, Shared), Plan(Alex, Alex, Shared + "\\"));
        // It is Alex's and Alex is not running on it (Sam runs elsewhere, nobody runs, or Alex
        // runs in a folder of his own): look with a stopped engine first.
        Assert.Equal(new FolderPlan(FolderStep.ProbeShared, Shared), Plan(Alex, Sam, @"C:\IDEA\Armory-sam"));
        Assert.Equal(new FolderPlan(FolderStep.ProbeShared, Shared), Plan(Alex, null, null));
        Assert.Equal(new FolderPlan(FolderStep.ProbeShared, Shared), Plan(Alex, Alex, @"C:\IDEA\Armory-alex"));
        // An address with no profile here (a 0.3.2 folder) is probed the same way.
        Assert.Equal(FolderStep.ProbeShared, Plan("apina@boscotech.edu", Sam, Shared).Step);

        // Jordan's own folder with work waiting there, or open in SolidWorks: Jordan stays.
        Assert.Equal(new FolderPlan(FolderStep.UseOwn, own), Plan(Alex, Alex, Shared, own, waiting: 2));
        Assert.Equal(new FolderPlan(FolderStep.UseOwn, own), Plan(null, null, null, own, open: true));
        // Nothing waits in it: back to the shared folder when it can be had, else stay, unasked.
        var back = Plan(null, Alex, @"C:\IDEA\Armory-alex", own);
        Assert.Equal(new FolderPlan(FolderStep.UseShared, Shared, own), back);
        Assert.True(back.MovesBack);
        Assert.Equal(new FolderPlan(FolderStep.AskRunningOwner, Shared, own), Plan(Alex, Alex, Shared, own));
        Assert.Equal(new FolderPlan(FolderStep.ProbeShared, Shared, own), Plan(Alex, null, null, own));
        Assert.False(Plan(null, null, null).MovesBack);
        Assert.False(Plan(Alex, Alex, Shared, own, waiting: 1).MovesBack);
    }

    [Fact]
    public void Own_folder_names_take_the_first_name_and_never_collide()
    {
        Assert.Equal(@"C:\IDEA\Armory-alex", SharedComputer.OwnFolder(Shared, Alex, []));
        Assert.Equal(@"C:\IDEA\Armory-alex", SharedComputer.OwnFolder(Shared + "\\", "Alex_Kim@boscotech.edu", []));
        Assert.Equal(@"C:\IDEA\Armory-jsmith2", SharedComputer.OwnFolder(Shared, "jsmith2@boscotech.edu", []));
        Assert.Equal(@"C:\IDEA\Armory-jose", SharedComputer.OwnFolder(Shared, "josé.núñez@boscotech.edu", []));
        Assert.Equal(@"C:\IDEA\Armory-student", SharedComputer.OwnFolder(Shared, "...@boscotech.edu", []));
        Assert.Equal(@"C:\IDEA\Armory-student", SharedComputer.OwnFolder(Shared, "李@boscotech.edu", []));
        Assert.Equal(@"C:\IDEA\Armory-alex-2", SharedComputer.OwnFolder(Shared, Alex, [@"c:\idea\armory-ALEX"]));
        Assert.Equal(@"C:\IDEA\Armory-alex-3", SharedComputer.OwnFolder(Shared, "alex.reyes@boscotech.edu", [@"C:\IDEA\Armory-alex", @"C:\IDEA\Armory-alex-2\"]));
        // A deep shared folder (111 characters): the name gives way, never the limit.
        var deep = @"C:\" + string.Join('\\', Enumerable.Repeat("Folder", 15)) + @"\Arm";
        var fitted = SharedComputer.OwnFolder(deep, "maximiliano.rodriguez@boscotech.edu", []);
        Assert.True(fitted.Length <= SharedComputer.MaximumFolderPath, fitted);
        Assert.Equal(deep + "-maximili", fitted);
        var last = fitted[(fitted.LastIndexOf('\\') + 1)..];
        Assert.True(VaultPath.TryValidateName(last, out var why), why);
        Assert.Equal(deep + "-maximi-2", SharedComputer.OwnFolder(deep, "maximiliano.rodriguez@boscotech.edu", [fitted]));
    }

    [Fact]
    public void Names_and_initials_come_from_the_shown_name_else_the_address()
    {
        Assert.Equal(("Alex Kim", "AK"), Pick(SharedComputer.NameFor(Alex, null)));
        Assert.Equal(("Alex Kim", "AK"), Pick(SharedComputer.NameFor(Alex, "  ")));
        Assert.Equal(("Alexander Kim-Lee", "AK"), Pick(SharedComputer.NameFor(Alex, "Alexander Kim-Lee")));
        Assert.Equal(("Mr. Pina", "P"), Pick(SharedComputer.NameFor("apina@boscotech.edu", "Mr. Pina")));
        Assert.Equal(("Apina", "A"), Pick(SharedComputer.NameFor("apina@boscotech.edu", null)));
        Assert.Equal(("Maria Lopez Garcia", "MG"), Pick(SharedComputer.NameFor("maria.lopez.garcia@boscotech.edu", null)));
        // The circle's color is the same for an address every time, in any case, and one of eight.
        var hue = SharedComputer.NameFor(Alex, null).Hue;
        Assert.Equal(hue, SharedComputer.NameFor("ALEX.KIM@boscotech.edu", "Alex").Hue);
        Assert.InRange(hue, 0, SharedComputer.PictureHues - 1);
        var hues = new[] { Alex, Jordan, Sam, "maria.lopez@boscotech.edu", "apina@boscotech.edu", "chris.ng@boscotech.edu" }
            .Select(e => SharedComputer.NameFor(e, null).Hue).Distinct().Count();
        Assert.True(hues > 1, "every address got the same color");

        static (string, string) Pick((string Name, string Initials, int Hue) n) => (n.Name, n.Initials);
    }

    [Fact]
    public void Who_may_remove_turn_off_and_change_pins()
    {
        // Remove: yourself, anyone as a mentor, anyone while PINs are off.
        Assert.True(SharedComputer.CanRemove(self: true, mentorInUse: false, pinsRequired: true));
        Assert.False(SharedComputer.CanRemove(self: false, mentorInUse: false, pinsRequired: true));
        Assert.True(SharedComputer.CanRemove(self: false, mentorInUse: true, pinsRequired: true));
        Assert.True(SharedComputer.CanRemove(self: false, mentorInUse: false, pinsRequired: false));
        // Turn shared mode off: one student or none, a mentor, or PINs off.
        Assert.True(SharedComputer.CanTurnOff(1, mentorInUse: false, pinsRequired: true));
        Assert.True(SharedComputer.CanTurnOff(0, mentorInUse: false, pinsRequired: true));
        Assert.False(SharedComputer.CanTurnOff(2, mentorInUse: false, pinsRequired: true));
        Assert.True(SharedComputer.CanTurnOff(2, mentorInUse: true, pinsRequired: true));
        Assert.True(SharedComputer.CanTurnOff(5, mentorInUse: false, pinsRequired: false));
        // PINs: only a mentor in use.
        Assert.True(SharedComputer.CanChangePins(mentorInUse: true));
        Assert.False(SharedComputer.CanChangePins(mentorInUse: false));
        // Folders and addresses compare as Windows compares them.
        Assert.True(SharedComputer.SameFolder(@"c:\idea\armory\", "C:/IDEA/Armory"));
        Assert.False(SharedComputer.SameFolder(@"C:\IDEA\Armory", @"C:\IDEA\Armory-alex"));
        Assert.True(SharedComputer.SameEmail(" Alex.Kim@boscotech.edu", Alex));
    }
}
