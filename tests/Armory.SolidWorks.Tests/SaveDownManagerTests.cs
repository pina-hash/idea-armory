using Armory.Agent.Engine;
using Armory.Core;

namespace Armory.SolidWorks.Tests;

// The save-down manager's decisions (research section 3.4) against a fake SolidWorks session:
// the student's own Save to Version kept and put back, the option set only while a vault
// document that can go back is the active one, off for a blocked or kept document, never touched
// where its numbers are unknown, and each save stamped with the year SolidWorks reads in it.
public sealed class SaveDownManagerTests
{
    private const string Root = @"C:\IDEA\Armory", Plate = @"C:\IDEA\Armory\Robot 2027\Plate.SLDPRT", Bracket = @"C:\IDEA\Armory\Robot 2027\Bracket.SLDPRT",
        Mine = @"C:\Users\maria\Documents\Mine.SLDPRT", Future = @"C:\IDEA\Armory\Robot 2028\Future.SLDPRT";
    private static readonly SaveToVersionIds Ids = new(570, 290);
    private static readonly IReadOnlyList<ProjectPin> Pins = [new(Root + @"\Robot 2027", 2025, false), new(Root + @"\Robot 2028", 2026, false)];

    // A SolidWorks session the manager talks to: preferences, documents and what each call did.
    private sealed class Session : ISaveDownCalls
    {
        public Dictionary<int, bool> Toggles { get; } = [];
        public Dictionary<int, int> Integers { get; } = [];
        public bool Ignores { get; set; }          // sets don't take (no license, as far as Armory can tell)
        public Dictionary<string, bool> Open { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string[]> Histories { get; } = new(StringComparer.OrdinalIgnoreCase);
        public CompatibilityResult? Compatibility { get; set; } = new(0, [], []);
        public List<DropItem> Drops { get; } = [];
        public List<string> Calls { get; } = [];
        public Func<string, bool>? OnSave { get; set; }

        public bool? GetToggle(int preference) => Toggles.TryGetValue(preference, out var on) ? on : false;
        public bool SetToggle(int preference, bool value)
        {
            Calls.Add($"toggle {preference}={value}");
            if (!Ignores) Toggles[preference] = value;
            return true;
        }
        public int? GetInteger(int preference) => Integers.GetValueOrDefault(preference);
        public bool SetInteger(int preference, int value)
        {
            Calls.Add($"integer {preference}={value}");
            if (!Ignores) Integers[preference] = value;
            return true;
        }
        public CompatibilityResult? CheckCompatibility(string path, int saveToVersion)
        {
            Calls.Add($"check {Name(path)} {saveToVersion}");
            return Compatibility;
        }
        public IReadOnlyList<DropItem> Inventory(string path, int docType) => Drops;
        public IReadOnlyList<string>? VersionHistory(string path) => Histories.TryGetValue(path, out var h) ? h : ["18000[2025/261]"];
        public bool? IsReadOnly(string path) => Open.TryGetValue(path, out var readOnly) ? readOnly : null;
        public bool Save(string path)
        {
            Calls.Add($"save {Name(path)} with {Toggles.GetValueOrDefault(Ids.EnableToggle)},{Integers.GetValueOrDefault(Ids.VersionValue)}");
            return OnSave?.Invoke(path) ?? true;
        }
        public (bool, int) Option => (Toggles.GetValueOrDefault(Ids.EnableToggle), Integers.GetValueOrDefault(Ids.VersionValue));
        // A Windows path's file name, on any platform.
        private static string Name(string path) => path[(path.LastIndexOf('\\') + 1)..];
    }

    private sealed class World
    {
        public Session Session { get; } = new();
        public LinkSettings Settings { get; } = LinkSettings.Load(null);
        public List<LinkRecord> Records { get; } = [];
        public List<SaveToVersionSupport> SupportChanges { get; } = [];
        public Dictionary<string, string> Hashes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Func<string, string?>? Hash { get; set; }

        public SaveDownManager Manager(string revision = "34.4.1", SaveToVersionIds? ids = null, bool noIds = false)
            => new(4120, SolidWorksRevision.Parse(revision)!.Value, Session, noIds ? null : ids ?? Ids, Settings, Records.Add, SupportChanges.Add,
                path => Hash is { } h ? h(path) : Hashes.GetValueOrDefault(path), TimeProvider.System);
    }

    private static string Sha(char c) => new(c, 64);

    [Fact]
    public void The_option_is_set_only_while_a_vault_document_that_can_go_back_is_active()
    {
        var w = new World();
        w.Session.Toggles[Ids.EnableToggle] = false;
        w.Session.Integers[Ids.VersionValue] = 0;
        var m = w.Manager();
        m.Attach();
        Assert.Equal(SaveToVersionSupport.Available, m.Support);
        m.SetPins(Pins);
        Assert.Equal((false, 0), w.Session.Option);                       // nothing active yet: the student's own

        m.ActiveChanged(Plate);                                           // Robot 2027 is pinned to 2025
        Assert.Equal((true, 1), w.Session.Option);
        Assert.True(w.Settings.Student(34)!.Changed);                     // kept before the change, for a crash
        Assert.Equal((false, 0), (w.Settings.Student(34)!.Enable, w.Settings.Student(34)!.Value));

        m.ActiveChanged(Mine);                                            // the student's own file: their setting
        Assert.Equal((false, 0), w.Session.Option);
        Assert.False(w.Settings.Student(34)!.Changed);
        m.ActiveChanged(Future);                                          // pinned to 2026: nothing to save down
        Assert.Equal((false, 0), w.Session.Option);
        m.ActiveChanged(null);
        Assert.Equal((false, 0), w.Session.Option);
    }

    [Fact]
    public void Two_releases_back_is_value_two_and_the_students_own_setting_comes_back_at_the_end()
    {
        var w = new World();
        w.Session.Toggles[Ids.EnableToggle] = true;                       // the student saves their own files as 2026 SP0's previous
        w.Session.Integers[Ids.VersionValue] = 1;
        var m = w.Manager("35.0.0");                                      // 2027
        m.Attach();
        m.SetPins(Pins);
        m.ActiveChanged(Plate);
        Assert.Equal((true, 2), w.Session.Option);                        // 2027 to 2025
        m.Restore();
        Assert.Equal((true, 1), w.Session.Option);
        Assert.False(w.Settings.Student(35)!.Changed);
    }

    [Fact]
    public void A_blocked_or_kept_document_saves_with_the_option_off()
    {
        var w = new World();
        var m = w.Manager();
        m.Attach();
        m.SetPins(Pins);
        w.Session.Compatibility = new(0, [new CompatibilityItem("Hole Wizard instances on sketch geometry", "Clear it", "Hole1")], ["Appearances"]);
        w.Session.Drops.Add(new DropItem(DropItem.Appearances, 3));
        m.ActiveChanged(Plate);
        var record = m.CheckCompatibility(Plate, 1)!;
        Assert.Equal((2025, true, 1), (record.TargetYear, record.Checked, record.Blocked.Count));
        Assert.Equal([new DropItem(DropItem.Appearances, 3), new DropItem(DropItem.Warning, 1, "Appearances")], record.Drops);
        Assert.Equal((false, 0), w.Session.Option);                       // blocked: off, so Save writes 2026 here
        Assert.Equal(LinkSaveMode.PrivateDraft, m.Saving(Plate));

        w.Session.Compatibility = new(0, [], []);                         // fixed
        m.CheckCompatibility(Plate, 1);
        Assert.Equal((true, 1), w.Session.Option);
        Assert.Equal(LinkSaveMode.SaveDown, m.Saving(Plate));

        m.KeepLocal(Plate, true);                                         // "Keep this file on this computer only"
        Assert.Equal((false, 0), w.Session.Option);
        m.KeepLocal(Plate, false);
        Assert.Equal((true, 1), w.Session.Option);
    }

    // Unknown preference numbers, a SolidWorks before 2026 SP3, or one older than 2026: the
    // option is never read nor set; saves are what SolidWorks writes, kept as private drafts.
    [Theory]
    [InlineData("34.4.1", true, SaveToVersionSupport.NotConfigured, LinkSaveMode.PrivateDraft, SaveDownOutcome.CantSaveDown)]
    [InlineData("34.2.0", false, SaveToVersionSupport.OldServicePack, LinkSaveMode.PrivateDraft, SaveDownOutcome.CantSaveDown)]
    [InlineData("33.5.0", false, SaveToVersionSupport.Unsupported, LinkSaveMode.Current, SaveDownOutcome.NotNeeded)]
    public void Where_the_option_cant_be_used_it_is_never_touched(string revision, bool noIds, SaveToVersionSupport support, LinkSaveMode mode, SaveDownOutcome now)
    {
        var w = new World();
        var m = w.Manager(revision, noIds: noIds);
        m.Attach();
        m.SetPins(Pins);
        m.ActiveChanged(Plate);
        Assert.Equal(support, m.Support);
        Assert.Empty(w.Session.Calls);
        Assert.Null(m.CheckCompatibility(Plate, 1));
        Assert.Equal(mode, m.Saving(Plate));
        Assert.Equal(now, Open(w, m, Plate));
        Assert.Empty(w.Session.Calls);
    }

    private static SaveDownOutcome Open(World w, SaveDownManager m, string path)
    {
        w.Session.Open[path] = false;
        return m.SaveInPinnedRelease(path);
    }

    [Fact]
    public void An_option_that_does_not_read_back_means_no_save_down()
    {
        var w = new World();
        w.Session.Ignores = true;
        w.Session.Integers[Ids.VersionValue] = 0;
        var m = w.Manager();
        m.Attach();
        Assert.Equal(SaveToVersionSupport.Available, m.Support);          // setting each to its own value reads back
        m.SetPins(Pins);
        m.ActiveChanged(Plate);                                           // but a change doesn't take
        Assert.Equal(SaveToVersionSupport.NotLicensed, m.Support);
        Assert.Equal([SaveToVersionSupport.NotLicensed], w.SupportChanges);
        Assert.Equal((false, 0), w.Session.Option);
        Assert.False(w.Settings.Student(34)!.Changed);
    }

    // A crash (or a SolidWorks that closed first) left Armory's value set: the next attach puts
    // the student's own back before reading it.
    [Fact]
    public void A_value_an_earlier_run_left_set_is_put_back_at_attach()
    {
        var w = new World();
        w.Settings.SetStudent(34, new LinkSettings.StudentSetting(false, 0, Changed: true));
        w.Session.Toggles[Ids.EnableToggle] = true;                       // what Armory had set
        w.Session.Integers[Ids.VersionValue] = 1;
        var m = w.Manager();
        m.Attach();
        Assert.Equal((false, 0), w.Session.Option);
        Assert.Equal(new LinkSettings.StudentSetting(false, 0, false), w.Settings.Student(34));
        // Another SolidWorks 2026 is linked already: the value belongs to that one; nothing is put back.
        var other = new World();
        other.Settings.SetStudent(34, new LinkSettings.StudentSetting(false, 0, Changed: true));
        other.Session.Toggles[Ids.EnableToggle] = true;
        other.Session.Integers[Ids.VersionValue] = 1;
        other.Manager().Attach(restoreLeftover: false);
        Assert.Equal((true, 1), other.Session.Option);
    }

    // Research section 2: the stamp is the bytes' SHA-256 (read twice around SolidWorks' own
    // reading of their history) and the year that history names, when it is the year meant.
    [Fact]
    public void A_save_is_stamped_with_the_year_SolidWorks_reads_in_the_bytes()
    {
        var w = new World();
        var m = w.Manager();
        m.Attach();
        m.SetPins(Pins);
        m.ActiveChanged(Plate);
        w.Hashes[Plate] = Sha('a');
        Assert.Equal(LinkSaveMode.SaveDown, m.Saving(Plate));
        w.Session.Histories[Plate] = ["19000[2026/100]", "18000[2026/230]"];
        var stamp = m.Saved(Plate, LinkSaveMode.SaveDown)!;
        Assert.Equal((Sha('a'), 2025, "34.4.1", 2026, 2025), (stamp.Sha256, stamp.Year, stamp.WriterRevision, stamp.WriterYear, stamp.SaveToVersionYear));
        Assert.Equal(SaveToVersionSupport.Available, m.Support);

        // A file the student keeps here: 2026, as meant.
        m.KeepLocal(Plate, true);
        Assert.Equal(LinkSaveMode.PrivateDraft, m.Saving(Plate));
        w.Session.Histories[Plate] = ["18000[2025/261]", "19000[2026/230]"];
        stamp = m.Saved(Plate, LinkSaveMode.PrivateDraft)!;
        Assert.Equal((2026, (int?)null), (stamp.Year, stamp.SaveToVersionYear));

        // Bytes that changed while they were read: no stamp.
        var reads = 0;
        w.Hash = _ => Sha(reads++ == 0 ? 'b' : 'c');
        m.Saving(Plate);
        Assert.Null(m.Saved(Plate, LinkSaveMode.PrivateDraft));
    }

    // B4: a save down that still wrote 2026 (no license for it) is stamped with no year, so the
    // engine keeps it here in both gate modes, and there is no more save down on this computer.
    [Fact]
    public void A_save_down_that_wrote_2026_means_no_save_down_here()
    {
        var w = new World();
        var m = w.Manager();
        m.Attach();
        m.SetPins(Pins);
        m.ActiveChanged(Plate);
        w.Hashes[Plate] = Sha('d');
        Assert.Equal(LinkSaveMode.SaveDown, m.Saving(Plate));
        w.Session.Histories[Plate] = ["18000[2025/261]", "19000[2026/230]"];
        var stamp = m.Saved(Plate, LinkSaveMode.SaveDown)!;
        Assert.Null(stamp.Year);
        Assert.Equal(2025, stamp.SaveToVersionYear);
        Assert.True(SavedReleaseRule.UnverifiedSaveDown(stamp));
        Assert.Equal(SaveToVersionSupport.NotLicensed, m.Support);
        Assert.Equal([SaveToVersionSupport.NotLicensed], w.SupportChanges);
        Assert.Equal((false, 0), w.Session.Option);                       // the student's own back
        Assert.Equal(LinkSaveMode.PrivateDraft, m.Saving(Bracket));
    }

    // SolidWorks' own Previous Release Check stopped a save down: the next Save keeps it here.
    [Fact]
    public void A_canceled_save_down_turns_the_option_off_for_that_document()
    {
        var w = new World();
        var m = w.Manager();
        m.Attach();
        m.SetPins(Pins);
        m.ActiveChanged(Plate);
        Assert.Equal(LinkSaveMode.SaveDown, m.Saving(Plate));
        m.SaveCanceled(Plate, LinkSaveMode.SaveDown);
        Assert.True(m.IsBlocked(Plate));
        Assert.Equal((false, 0), w.Session.Option);
        Assert.Equal(LinkSaveMode.PrivateDraft, m.Saving(Plate));
    }

    // "Save it in 2025 now": its own year set, a plain Save, then the active document's setting.
    [Fact]
    public void Save_in_the_pinned_year_now_saves_with_the_option_and_puts_it_back()
    {
        var w = new World();
        var m = w.Manager();
        m.Attach();
        m.SetPins(Pins);
        m.ActiveChanged(Mine);                                            // the student works in their own file
        Assert.Equal(SaveDownOutcome.NotOpen, m.SaveInPinnedRelease(Plate));
        w.Session.Open[Plate] = true;
        Assert.Equal(SaveDownOutcome.ReadOnly, m.SaveInPinnedRelease(Plate));
        w.Session.Open[Plate] = false;
        Assert.Equal(SaveDownOutcome.Saved, m.SaveInPinnedRelease(Plate));
        Assert.Contains("save Plate.SLDPRT with True,1", w.Session.Calls);
        Assert.Equal((false, 0), w.Session.Option);                       // back to the student's own for Mine
        w.Session.Open[Future] = false;
        Assert.Equal(SaveDownOutcome.NotNeeded, m.SaveInPinnedRelease(Future));
        w.Session.OnSave = _ => false;
        Assert.Equal(SaveDownOutcome.Failed, m.SaveInPinnedRelease(Plate));
        // A file the student kept here goes back to the team's rule once saved in its year.
        w.Session.OnSave = null;
        m.KeepLocal(Plate, true);
        Assert.Equal(SaveToVersionChoice.Off, m.ChoiceFor(Plate));
        Assert.Equal(SaveDownOutcome.Saved, m.SaveInPinnedRelease(Plate));
        Assert.Equal(SaveToVersionChoice.SaveDown, m.ChoiceFor(Plate));
    }

    [Fact]
    public void An_opened_file_is_stamped_with_what_its_history_says()
    {
        var w = new World();
        var m = w.Manager("33.5.0");
        w.Hashes[Plate] = Sha('e');
        w.Session.Histories[Plate] = ["18000[2025/100]", "19000[2026/225]"];
        var stamp = m.StampOnOpen(Plate)!;
        Assert.Equal((Sha('e'), 2026, (int?)null), (stamp.Sha256, stamp.Year, stamp.SaveToVersionYear));
        w.Session.Histories[Plate] = ["abc"];
        Assert.Null(m.StampOnOpen(Plate));
    }

    // The project a document is in: the longest pinned folder that holds it.
    [Fact]
    public void A_document_belongs_to_the_deepest_project_folder()
    {
        var w = new World();
        var m = w.Manager();
        m.SetPins([new(Root + @"\Robot", 2024, false), new(Root + @"\Robot 2027", 2025, true)]);
        Assert.Equal(2025, m.PinFor(Plate)!.PinnedRelease);
        Assert.Equal(2024, m.PinFor(Root + @"\Robot\Arm.SLDASM")!.PinnedRelease);
        Assert.Null(m.PinFor(Mine));
        Assert.Equal(SaveDownPlan.Antepenultimate, m.PlanFor(Root + @"\Robot\Arm.SLDASM"));
    }
}
