using Armory.Agent.Engine;

namespace Armory.SolidWorks.Tests;

// The link's decisions about SolidWorks' events (LinkPolicy), its message filter's retries, and
// how it recognizes a session in the Running Object Table: everything that needs no SolidWorks.
public sealed class LinkPolicyTests
{
    private const string Root = @"C:\IDEA\Armory";

    [Theory]
    [InlineData(@"C:\IDEA\Armory\Robot 2027\Plate.SLDPRT", true)]
    [InlineData(@"c:\idea\armory\robot 2027\arm.sldasm", true)]
    [InlineData(@"C:/IDEA/Armory/Robot 2027/Plate.SLDDRW", true)]
    [InlineData(@"C:\IDEA\Armory\Robot 2027\Notes.txt", false)]          // not a SolidWorks document
    [InlineData(@"C:\IDEA\ArmoryOld\Plate.SLDPRT", false)]               // a folder that only starts like the vault
    [InlineData(@"C:\Users\maria\Documents\Plate.SLDPRT", false)]
    [InlineData(@"C:\IDEA\Armory", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_SolidWorks_documents_under_the_vault_are_vault_documents(string? path, bool expected)
        => Assert.Equal(expected, LinkPolicy.IsVaultDocument(path, Root));

    [Fact]
    public void A_new_document_is_watched_until_it_is_saved_somewhere_else()
    {
        Assert.True(LinkPolicy.Watches("", Root));
        Assert.True(LinkPolicy.Watches(null, Root));
        Assert.True(LinkPolicy.Watches(@"C:\IDEA\Armory\Robot\Plate.SLDPRT", Root));
        Assert.False(LinkPolicy.Watches(@"D:\Elsewhere\Plate.SLDPRT", Root));
    }

    [Theory]
    [InlineData("Plate.SLDPRT", 1)]
    [InlineData("Arm.sldasm", 2)]
    [InlineData("Plate.SLDDRW", 3)]
    [InlineData("Notes.txt", 0)]
    [InlineData(null, 0)]
    public void The_document_type_follows_the_extension(string? path, int type) => Assert.Equal(type, LinkPolicy.DocTypeOf(path));

    // The student opened it: SolidWorks' active document when the open finished, or a document
    // with its own window when the link attached later. A part an assembly loaded is neither.
    [Fact]
    public void Only_the_active_or_visible_document_is_the_students_own_open()
    {
        const string arm = @"C:\IDEA\Armory\Robot\Arm.SLDASM", plate = @"C:\IDEA\Armory\Robot\Plate.SLDPRT";
        Assert.True(LinkPolicy.StudentOpened(arm, arm));
        Assert.True(LinkPolicy.StudentOpened(@"c:\idea\armory\robot\arm.sldasm", arm));
        Assert.False(LinkPolicy.StudentOpened(arm, plate));
        Assert.False(LinkPolicy.StudentOpened(null, plate));
        Assert.True(LinkPolicy.StudentOpened(arm, plate, visibleAtAttach: true));
    }

    [Theory]
    [InlineData(1, true)]   // Save
    [InlineData(2, true)]   // Save As: the document is that file now
    [InlineData(3, false)]  // Save As Copy: another file; the document's own bytes are unchanged
    [InlineData(4, false)]
    public void Save_and_save_as_write_the_documents_own_path(int saveType, bool own) => Assert.Equal(own, LinkPolicy.SavedOwnPath(saveType));

    [Fact]
    public void A_hidden_document_is_still_open()
    {
        Assert.True(LinkPolicy.Closed(SwConstants.DestroyTypeDestroy));
        Assert.False(LinkPolicy.Closed(SwConstants.DestroyTypeHidden));
    }

    // What a save writes, from Save to Version as it is set for that save.
    [Theory]
    [InlineData(true, 2025, 2026, 2025, LinkSaveMode.SaveDown)]
    [InlineData(true, 2024, 2026, 2025, LinkSaveMode.SaveDown)]      // two back is older than the pin: still fine
    [InlineData(false, 2026, 2026, 2025, LinkSaveMode.PrivateDraft)] // off: 2026, kept here
    [InlineData(true, 2026, 2027, 2025, LinkSaveMode.PrivateDraft)]  // one back from 2027 is 2026, still newer than the pin
    [InlineData(false, 2026, 2026, 2026, LinkSaveMode.Current)]      // nothing to save down to
    [InlineData(true, 2025, 2026, null, LinkSaveMode.Current)]       // not a document of a project
    [InlineData(false, 2025, 2025, 2025, LinkSaveMode.Current)]
    public void A_save_writes_the_pinned_year_only_with_the_option_on(bool on, int target, int running, int? pinned, LinkSaveMode expected)
        => Assert.Equal(expected, LinkPolicy.SaveMode(on, target, running, pinned));

    [Theory]
    [InlineData(2, 0, 100)]       // SERVERCALL_RETRYLATER, just now: again in 100 ms
    [InlineData(2, 29_999, 100)]
    [InlineData(2, 30_000, -1)]   // 30 seconds of "busy": give up (the work is retried later)
    [InlineData(1, 0, -1)]        // SERVERCALL_REJECTED: never retried
    public void A_busy_SolidWorks_is_retried_for_thirty_seconds(int rejectType, int elapsed, int expected)
        => Assert.Equal(expected, MessageFilter.RetryAfter(rejectType, elapsed, MessageFilter.RetryFor));

    [Fact]
    public void A_starting_or_closing_SolidWorks_is_retried_briefly_and_then_as_before()
    {
        var filter = new MessageFilter();
        Assert.Equal(MessageFilter.RetryFor, filter.Limit);
        using (filter.Hurry(TimeSpan.FromSeconds(2)))
        {
            Assert.Equal(TimeSpan.FromSeconds(2), filter.Limit);
            Assert.Equal(-1, filter.RetryRejectedCall(IntPtr.Zero, 2_000, 2));
            Assert.Equal(100, filter.RetryRejectedCall(IntPtr.Zero, 1_999, 2));
            using (filter.Hurry(TimeSpan.FromSeconds(5))) Assert.Equal(TimeSpan.FromSeconds(2), filter.Limit); // never longer than asked before
        }
        Assert.Equal(MessageFilter.RetryFor, filter.Limit);
        Assert.Equal(0, filter.HandleInComingCall(0, IntPtr.Zero, 0, IntPtr.Zero));
        Assert.Equal(2, filter.MessagePending(IntPtr.Zero, 0, 0));
    }

    [Theory]
    [InlineData("SolidWorks_PID_4120", 4120, true)]
    [InlineData("!SolidWorks_PID_4120", 4120, true)]
    [InlineData("!solidworks_pid_4120", 4120, true)]
    [InlineData("!SolidWorks_PID_41200", 4120, false)]
    [InlineData("!SolidWorks_PID_412", 4120, false)]
    [InlineData("!Excel.Application", 4120, false)]
    public void The_running_object_table_names_one_session_by_its_process(string displayName, int pid, bool names)
        => Assert.Equal(names, RunningObjects.Names(displayName, pid));
}
