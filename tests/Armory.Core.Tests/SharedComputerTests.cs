using Armory.Core;

namespace Armory.Core.Tests;

// Several people on one computer (docs/agent/PROFILES.md): the PIN's format and its waits, when
// the picker shows, when a computer becomes shared by itself, the folders of one's own that 0.5.0
// folds back, whether PINs stay on after the update, names and initials, and who may remove a
// person, turn shared mode off or change whether PINs are asked. One test per rule in
// SharedComputer and PinHash.
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
        Assert.True(SharedComputer.ShowPicker(PickerTrigger.ShownFromHidden, today, today, 2));
        Assert.True(SharedComputer.ShowPicker(PickerTrigger.WindowsLocked, today, today, 2));
        Assert.True(SharedComputer.ShowPicker(PickerTrigger.SwitchStudent, today, today, 3));
        Assert.True(SharedComputer.ShowPicker(PickerTrigger.SignInEnded, today, today, 2));
        Assert.True(SharedComputer.ShowPicker(PickerTrigger.Activated, yesterday, today, 2));
        Assert.True(SharedComputer.ShowPicker(PickerTrigger.Activated, null, today, 2));
        Assert.False(SharedComputer.ShowPicker(PickerTrigger.Activated, today, today, 2));
        Assert.False(SharedComputer.ShowPicker(PickerTrigger.Minimized, yesterday, today, 2));
        Assert.False(SharedComputer.ShowPicker(PickerTrigger.Start, null, today, 2));
    }

    [Fact]
    public void One_person_on_a_computer_never_gets_the_picker_by_itself_only_on_switch_or_to_sign_in_again()
    {
        var today = new DateOnly(2026, 10, 26);
        foreach (var people in new[] { 0, 1 })
        {
            foreach (var trigger in new[] { PickerTrigger.ShownFromHidden, PickerTrigger.WindowsLocked, PickerTrigger.Activated, PickerTrigger.Start, PickerTrigger.Minimized })
                Assert.False(SharedComputer.ShowPicker(trigger, null, today, people), $"{trigger} with {people}");
            Assert.True(SharedComputer.ShowPicker(PickerTrigger.SignInEnded, today, today, people));
            // Switch is where someone new adds themselves: it always shows.
            Assert.True(SharedComputer.ShowPicker(PickerTrigger.SwitchStudent, today, today, people));
        }
    }

    // Replaces Folder_plan_follows_the_hand_over_table (0.5.0, ledger 0437): there is no hand-over
    // any more. A computer becomes shared by itself when a second person signs in, and the folders
    // of one's own that 0.3.3 to 0.4.x made are recognized so they can be folded back.
    [Fact]
    public void A_computer_becomes_shared_when_someone_else_signs_in_and_own_folders_are_recognized()
    {
        // Nobody here yet, or the same person again in any case: still one person's computer.
        Assert.False(SharedComputer.BecomesShared(null, null, Alex));
        Assert.False(SharedComputer.BecomesShared(Alex, null, " ALEX.kim@boscotech.edu"));
        Assert.False(SharedComputer.BecomesShared(null, Alex, Alex));
        // Someone else signs in, while Alex is signed in or after Alex signed out of a folder he used.
        Assert.True(SharedComputer.BecomesShared(Alex, null, Jordan));
        Assert.True(SharedComputer.BecomesShared(null, Alex, Jordan));
        // The person signed in now decides when there is one, not an older folder binding.
        Assert.False(SharedComputer.BecomesShared(Jordan, Alex, Jordan));

        Assert.True(SharedComputer.IsOwnFolder(Shared, @"C:\IDEA\Armory-alex"));
        Assert.True(SharedComputer.IsOwnFolder(Shared + "\\", @"c:\idea\armory-JORDAN\"));
        Assert.True(SharedComputer.IsOwnFolder(Shared, @"C:\IDEA\Armory-alex-2"));
        Assert.True(SharedComputer.IsOwnFolder(Shared, "C:/IDEA/Armory-jsmith2"));
        Assert.True(SharedComputer.IsOwnFolder(Shared, @"C:\IDEA\Armory-student"));
        foreach (var not in new[] { Shared, @"C:\IDEA\Armory-", @"C:\IDEA\Armory2", @"C:\IDEA\Armory-alex\Robot", @"C:\IDEA\Armory-alex-x",
                     @"C:\IDEA\Armory-al ex", @"C:\IDEA\Armory-alex-2-3", @"C:\IDEA\Other-alex", @"D:\IDEA\Armory-alex", "C:\\IDEA\\Armory-jos\u00e9" })
            Assert.False(SharedComputer.IsOwnFolder(Shared, not), not);
    }

    [Fact]
    public void Pins_stay_on_after_the_update_only_where_a_mentor_turned_them_on()
    {
        // 0.3.3 to 0.4.x: on by default, nobody recorded. Students chose their own PINs; off now.
        Assert.False(SharedComputer.PinsKeptAfterUpdate(pinsRequired: true, pinsChangedBy: null));
        Assert.False(SharedComputer.PinsKeptAfterUpdate(pinsRequired: true, pinsChangedBy: "  "));
        // A mentor turned them off: off.
        Assert.False(SharedComputer.PinsKeptAfterUpdate(pinsRequired: false, pinsChangedBy: "apina@boscotech.edu"));
        // A mentor turned them (back) on deliberately: on.
        Assert.True(SharedComputer.PinsKeptAfterUpdate(pinsRequired: true, pinsChangedBy: "apina@boscotech.edu"));
        Assert.False(SharedComputer.PinsKeptAfterUpdate(pinsRequired: false, pinsChangedBy: null));
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
