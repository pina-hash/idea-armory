using Armory.Core;

namespace Armory.Agent.Engine.Tests;

// The words for a file saved in a SolidWorks newer than its project's pin (research section 6):
// they say what this computer can do, and never promise a save down it can't make.
public sealed class ReleaseWordsTests
{
    private static SolidWorksRevision Revision(string text) => SolidWorksRevision.Parse(text)!.Value;

    [Fact]
    public void The_refusal_promises_a_save_down_only_where_one_works()
    {
        const string first = "Saved in SolidWorks 2026, and Robot 2027 uses SolidWorks 2025.";
        const string waits = first + " It stays on this computer only until it is saved in SolidWorks 2025.";
        Assert.Equal(first + " Open it in SolidWorks 2026 and click Save: Armory saves it as 2025, then it uploads by itself.",
            SyncEngine.NewerThanPinWords(2026, 2025, "Robot 2027", Revision("34.4.1"), saveDownWorks: true));
        // Two releases back from 2027.
        Assert.Equal(first + " Open it in SolidWorks 2027 and click Save: Armory saves it as 2025, then it uploads by itself.",
            SyncEngine.NewerThanPinWords(2026, 2025, "Robot 2027", Revision("35.0.0"), saveDownWorks: true));
        // No SolidWorks link has said what runs here.
        Assert.Equal(waits, SyncEngine.NewerThanPinWords(2026, 2025, "Robot 2027", null, saveDownWorks: false));
        Assert.Equal(waits + " Update SolidWorks 2026 to Service Pack 3 or newer so Armory can save it in 2025.",
            SyncEngine.NewerThanPinWords(2026, 2025, "Robot 2027", Revision("34.2.0"), saveDownWorks: true));
        Assert.Equal(waits + " SolidWorks on this computer couldn't save it in 2025. Ask a CAD lead or a mentor what to do.",
            SyncEngine.NewerThanPinWords(2026, 2025, "Robot 2027", Revision("34.4.1"), saveDownWorks: false));
        Assert.Equal(waits + " SolidWorks 2028 can't save files as 2025.",
            SyncEngine.NewerThanPinWords(2026, 2025, "Robot 2027", Revision("36.0.0"), saveDownWorks: true));
        // A file from a SolidWorks newer than the one running here can't even be opened here.
        Assert.Equal("Saved in SolidWorks 2027, and Robot 2027 uses SolidWorks 2025. It stays on this computer only until it is saved in SolidWorks 2025.",
            SyncEngine.NewerThanPinWords(2027, 2025, "Robot 2027", Revision("34.4.1"), saveDownWorks: true));
        // On the pinned release itself (a 2025 computer): nothing to save down.
        Assert.Equal(waits, SyncEngine.NewerThanPinWords(2026, 2025, "Robot 2027", Revision("33.5.0"), saveDownWorks: true));
    }

    [Fact]
    public void The_notice_needs_the_student_where_they_can_fix_it_and_is_news_where_they_cannot()
    {
        var here = SyncEngine.NewerThanPinNotice(2026, 2025, Revision("34.4.1"), saveDownWorks: true);
        Assert.True(here.NeedsYou);
        Assert.Contains("You can fix them here: 1. Check one out in Armory. 2. Open it in SolidWorks 2026. 3. Click Save. Armory saves it as SolidWorks 2025 for you. 4. Check it in.", here.Detail);
        var old = SyncEngine.NewerThanPinNotice(2026, 2025, Revision("33.5.0"), saveDownWorks: true);
        Assert.False(old.NeedsYou);
        Assert.StartsWith("You can open parts and assemblies to look", old.Detail);
        Assert.EndsWith("Someone with SolidWorks 2026 can fix them: check it out, open it, click Save, and check it in.", old.Detail);
        // Unknown here (no link): it needs the team, so it is shown as needing attention.
        var unknown = SyncEngine.NewerThanPinNotice(2026, 2025, null, saveDownWorks: false);
        Assert.True(unknown.NeedsYou);
        Assert.Contains("Someone with SolidWorks 2026 can fix them", unknown.Detail);
        // A 2026 computer that can't save down is told who can, as news.
        var cannot = SyncEngine.NewerThanPinNotice(2026, 2025, Revision("34.2.0"), saveDownWorks: true);
        Assert.False(cannot.NeedsYou);
        Assert.Equal(unknown.Detail, cannot.Detail);
    }
}
