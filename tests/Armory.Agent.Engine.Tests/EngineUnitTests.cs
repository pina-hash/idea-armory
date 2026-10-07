using System.Text;
using System.Text.Json;
using Armory.Agent.Engine;
using Armory.Agent.Engine.View;

namespace Armory.Agent.Engine.Tests;

public sealed class EngineUnitTests
{
    [Theory]
    [InlineData("Robot 2027/Drivetrain/~$Plate.SLDPRT", "Robot 2027/Drivetrain/Plate.SLDPRT")]
    [InlineData("Robot 2027/~$Gearbox.sldasm", "Robot 2027/Gearbox.sldasm")]
    [InlineData("Robot 2027\\Drawings\\~$Frame.SLDDRW", "Robot 2027/Drawings/Frame.SLDDRW")]
    public void A_SolidWorks_lock_file_names_its_open_document(string marker, string document)
    {
        Assert.True(LockMarkers.TryGetDocument(marker, out var path));
        Assert.Equal(document, path);
    }

    [Theory]
    [InlineData("Robot 2027/~$notes.docx")] // Office lock files are ignored, not SolidWorks documents
    [InlineData("Robot 2027/~$")]
    [InlineData("Robot 2027/Plate.SLDPRT")]
    [InlineData("Robot 2027/~$CON.SLDPRT")]
    public void Anything_else_is_not_a_SolidWorks_open_marker(string marker) => Assert.False(LockMarkers.TryGetDocument(marker, out _));

    [Fact]
    public void Operation_ids_are_stable_distinct_and_version_5_shaped()
    {
        var a = OperationIds.Derive("device:save:1", "commit");
        Assert.Equal(a, OperationIds.Derive("device:save:1", "commit"));
        Assert.NotEqual(a, OperationIds.Derive("device:save:1", "side"));
        Assert.NotEqual(a, OperationIds.Derive("device:save:2", "commit"));
        var text = a.ToString();
        Assert.Equal('5', text[14]);
        Assert.Contains(text[19], "89ab");
    }

    [Fact]
    public void Display_names_come_from_school_emails()
    {
        Assert.Equal("Maria Lopez", SyncEngine.DisplayName("maria.lopez@students.test"));
        Assert.Equal("Alex Kim", SyncEngine.DisplayName("alex_kim@school.org"));
        Assert.Equal("Jreyes", SyncEngine.DisplayName("jreyes@x.org"));
    }

    [Fact]
    public void Engine_state_round_trips_with_case_insensitive_paths()
    {
        var store = new MemoryState();
        var state = new EngineState { Email = "a@b.c", DeviceId = Guid.NewGuid() };
        var project = Guid.NewGuid();
        state.Projects[project] = new ProjectState { Id = project, Name = "Robot", Folder = "Robot", Role = "cad_lead", Archived = true };
        state.Files["Robot/Plate.SLDPRT"] = new FileState
        {
            Path = "Robot/Plate.SLDPRT", BaseId = "v1", BaseHash = "h", Inflight = new Inflight("commit", Guid.NewGuid(), "e1", Hash: "h"),
            CheckOut = "d:checkout:7", Request = Armory.Core.CheckoutRequest.Undo, AutoCheckIn = true, TransientLock = true,
            AppliedOwnership = Armory.Core.LockOwnership.ThisDevice,
        };
        state.Completed.Add("e0");
        state.Dismissed["keptCopy"] = new(StringComparer.Ordinal) { "kept:1" };
        store.Save(state.Serialize());
        var loaded = EngineState.Load(store);
        Assert.Equal(EngineState.CurrentSchema, loaded.Schema);
        Assert.True(loaded.Files.ContainsKey("robot/plate.sldprt"));
        var file = loaded.Files["ROBOT/PLATE.SLDPRT"];
        Assert.Equal("commit", file.Inflight!.Kind);
        Assert.Equal("d:checkout:7", file.CheckOut);
        Assert.Equal(Armory.Core.CheckoutRequest.Undo, file.Request);
        Assert.True(file.AutoCheckIn);
        Assert.True(file.TransientLock);
        Assert.Equal(Armory.Core.LockOwnership.ThisDevice, file.AppliedOwnership); // a schema 2 state keeps what it applied
        Assert.Contains("e0", loaded.Completed);
        Assert.Contains("kept:1", loaded.Dismissed["keptCopy"]);
        Assert.Equal(state.DeviceId, loaded.DeviceId);
        Assert.Equal(("Robot", "cad_lead", true, true), (loaded.Projects[project].Folder, loaded.Projects[project].Role, loaded.Projects[project].Archived, loaded.Projects[project].CanTakeBack));

        // The upgrade (decision D15): state.json exactly as 0.1.0 wrote it loads as schema 2.
        var migrated = EngineState.Load(new MemoryState(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "state-0.1.0.json"))));
        Assert.Equal(2, migrated.Schema);
        Assert.Equal(2, migrated.Files.Count);
        Assert.All(migrated.Files.Values, f => Assert.Null(f.AppliedOwnership));
    }

    // Decision D15 on a state.json the 0.1.0 engine (b18791d) wrote: Alex added Plate and
    // Bracket, opened Bracket in SolidWorks (its ~$ marker took the lock, as 0.1.0 did) and saved
    // it offline. Everything it knew is kept; only the read-only bookkeeping starts over.
    [Fact]
    public void A_0_1_0_state_loads_as_schema_2_and_keeps_the_vault()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "state-0.1.0.json"));
        using (var raw = JsonDocument.Parse(bytes))
        {
            Assert.Equal(1, raw.RootElement.GetProperty("schema").GetInt32());
            Assert.Equal(1, raw.RootElement.GetProperty("files").GetProperty("Robot 2027/Drivetrain/Bracket.SLDPRT").GetProperty("appliedOwnership").GetInt32());
        }
        var state = EngineState.Load(new MemoryState(bytes));
        Assert.Equal(EngineState.CurrentSchema, state.Schema);
        Assert.Equal("alex.kim@students.test", state.Email);
        Assert.Equal(Guid.Parse("b5428ca9-a0f6-4efb-9de6-64f0b6a535f9"), state.DeviceId);
        Assert.Equal(4, state.Sequence);
        var project = Assert.Single(state.Projects.Values);
        Assert.Equal(("Robot 2027", "Robot 2027", "student", 12L), (project.Name, project.Folder, project.Role, project.Cursor));
        // Every file is still known, with its server id, base and unsent saves.
        var bracket = state.Files["robot 2027/drivetrain/bracket.sldprt"];
        Assert.Equal(Guid.Parse("860d3bdd-a047-4b31-abb0-523c1ac9c297"), bracket.FileId);
        Assert.Equal("df3b1fdc-ff97-4035-8b2e-04788a00d027", bracket.BaseId);
        Assert.Equal(["b5428ca9-a0f6-4efb-9de6-64f0b6a535f9:save:4"], bracket.Entries);
        var plate = state.Files["Robot 2027/Drivetrain/Plate.SLDPRT"];
        Assert.Equal("69f99a99aab601b8cb2e0d72212a1a6740c12deeb4a14f2aaf834366f3886c6f", plate.BaseHash);
        Assert.Equal(["b5428ca9-a0f6-4efb-9de6-64f0b6a535f9:save:1", "b5428ca9-a0f6-4efb-9de6-64f0b6a535f9:save:2"],
            state.Completed.Where(c => c.Contains(":save:", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
        // The ownership 0.1.0 applied (Free was writable then) is forgotten, so the first pass
        // applies the v2 rule to every file. The lock is not touched here: on the server it is
        // still this computer's, now a check out. The marker's lock intent is done with.
        Assert.All(state.Files.Values, f => Assert.Null(f.AppliedOwnership));
        Assert.Null(bracket.MarkerEntry);
        Assert.Contains("b5428ca9-a0f6-4efb-9de6-64f0b6a535f9:open:3", state.Completed);
        Assert.Equal(Armory.Core.CheckoutRequest.None, bracket.Request);
        Assert.False(bracket.AutoCheckIn);
        // Saved again, it is a schema 2 document with nothing of the 0.1.0 marker left.
        var again = Encoding.UTF8.GetString(state.Serialize());
        Assert.Contains("\"schema\":2", again, StringComparison.Ordinal);
        Assert.DoesNotContain("markerEntry", again, StringComparison.Ordinal);
        Assert.DoesNotContain("appliedOwnership", again, StringComparison.Ordinal);
        Assert.Contains("\"folder\":\"Robot 2027\"", again, StringComparison.Ordinal);
    }

    [Fact]
    public void File_state_store_replaces_atomically_and_leaves_no_pending_file()
    {
        var folder = Path.Combine(Path.GetTempPath(), "armory-state-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileStateStore(Path.Combine(folder, "state.json"));
            Assert.Null(store.Load());
            store.Save([1, 2, 3]);
            store.Save([4, 5]);
            Assert.Equal([4, 5], store.Load());
            Assert.Single(Directory.GetFiles(folder));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void The_view_serializes_with_the_bridge_field_names()
    {
        var mine = new CheckoutView(CheckoutStates.Mine, "Checked out by you", "Alex Kim", "alex.kim@b.c", "Lab PC", "2026-10-06T10:00:00.0000000+00:00");
        var other = new CheckoutView(CheckoutStates.Other, "Checked out by Maria Lopez on LAB-PC-07", "Maria Lopez", "maria.lopez@b.c", "LAB-PC-07", "2026-10-06T10:00:00.0000000+00:00");
        var available = new CheckoutView(CheckoutStates.Available, "Available", null, null, null, null);
        var view = new AgentView(Connections.SignedIn, new ConnectView("idle", null), new AccountView("a@b.c", "Lab PC"),
            new SyncView(SyncStates.Synced, "Everything is saved to Armory.", null, 0),
            new ActivityView(null, null, null, null, new WaitingView(3, "3 files are waiting to upload. They upload when this computer is back online."), []),
            @"C:\IDEA\Armory",
            [new NoticeGroupView(NoticeKinds.KeptCopy, NoticeKinds.KeptCopy, NoticeTones.Look, "Your change to Plate.SLDPRT was kept as your own copy", "Saved without a check out.", 1, null,
                [new NoticeItemView("f", "Robot/Plate.SLDPRT", "Plate.SLDPRT", "Saved without a check out.")])],
            new PromptView("prompt:Robot/Plate.SLDPRT:2026-10-06T10:00:00.0000000+00:00", "f", "Robot/Plate.SLDPRT", "Plate.SLDPRT", other, false),
            [new MyFileView("g", "Robot/Gear.SLDPRT", "Gear.SLDPRT", "Robot", FileStatuses.Changed, null, mine)],
            [new ProjectView("p", "Robot", false, "student", false,
                [new FolderView("", "Robot", 2, [
                    new FileRowView("f", "Plate.SLDPRT", "Robot/Plate.SLDPRT", FileStatuses.Synced, other, false, true, null, null),
                    new FileRowView(null, "Notes.txt", "Robot/Notes.txt", FileStatuses.NotInArmory, available, false, false, null, null)])])],
            new SettingsView(@"C:\IDEA\Armory", true, "system"), "idea");
        using var json = JsonDocument.Parse(BridgeMessages.ViewMessage(view));
        Assert.Equal("view", json.RootElement.GetProperty("type").GetString());
        var v = json.RootElement.GetProperty("view");
        Assert.Equal(["connection", "connect", "account", "sync", "activity", "vaultRoot", "notices", "prompt", "myFiles", "projects", "settings", "effectiveTheme"],
            v.EnumerateObject().Select(p => p.Name).ToArray());
        var project = v.GetProperty("projects")[0];
        foreach (var name in new[] { "id", "name", "archived", "role", "canTakeBack", "folders" })
            Assert.True(project.TryGetProperty(name, out _), name);
        Assert.False(project.TryGetProperty("season", out _)); // season is shown nowhere
        var folder = project.GetProperty("folders")[0];
        Assert.Equal(2, folder.GetProperty("fileCount").GetInt32());
        var row = folder.GetProperty("files")[0];
        Assert.True(row.GetProperty("releaseNotChecked").GetBoolean());
        Assert.Equal("synced", row.GetProperty("status").GetString());
        Assert.False(row.GetProperty("changed").GetBoolean());
        Assert.Equal("Checked out by Maria Lopez on LAB-PC-07", row.GetProperty("checkout").GetProperty("label").GetString());
        Assert.Equal("other", row.GetProperty("checkout").GetProperty("state").GetString());
        var local = folder.GetProperty("files")[1];
        Assert.Equal(JsonValueKind.Null, local.GetProperty("fileId").ValueKind);
        Assert.Equal("Available", local.GetProperty("checkout").GetProperty("label").GetString());
        var notice = v.GetProperty("notices")[0];
        foreach (var name in new[] { "key", "kind", "tone", "title", "detail", "count", "action", "items" })
            Assert.True(notice.TryGetProperty(name, out _), name);
        Assert.False(notice.TryGetProperty("type", out _)); // data objects use kind, never type
        var prompt = v.GetProperty("prompt");
        foreach (var name in new[] { "key", "fileId", "path", "name", "checkout", "canCheckOut" })
            Assert.True(prompt.TryGetProperty(name, out _), name);
        Assert.Equal("Checked out by you", v.GetProperty("myFiles")[0].GetProperty("checkout").GetProperty("label").GetString());
        Assert.Equal(3, v.GetProperty("activity").GetProperty("waiting").GetProperty("count").GetInt32());
        Assert.True(v.GetProperty("settings").GetProperty("startAtSignIn").GetBoolean());
        var detail = new FileDetailView("f", "Plate.SLDPRT", "Robot/Plate.SLDPRT", "Robot", "", FileStatuses.Synced, available, true, false,
            [new HistoryEntryView("h", HistoryKinds.KeptCopy, "Alex Kim", "2026-10-06T10:00:00.0000000+00:00", 12, "Saved while checked out", false, false)]);
        using var d = JsonDocument.Parse(BridgeMessages.DetailMessage(detail));
        var dv = d.RootElement.GetProperty("detail");
        Assert.True(dv.GetProperty("releaseNotChecked").GetBoolean()); // the tag lives on the detail
        Assert.Equal("keptCopy", dv.GetProperty("history")[0].GetProperty("kind").GetString());
        Assert.False(dv.GetProperty("canTakeBack").GetBoolean());

        // The two v2 host messages (docs/agent/BRIDGE.md): what is moving right now, and the
        // one answer to an action, with the names the page reads.
        var activity = new ActivityView("Uploading 1 of 3 files, 48 MB left, about 20 sec",
            new DirectionView(1, 3, 30, 78, 2, 20, "Uploading 1 of 3 files, 48 MB left, about 20 sec"), null,
            new DirectionView(45, 120, 0, 0, 0, null, "Moving 120 files to Gearbox"),
            new WaitingView(2, "2 checked-out files have changes. Check them in to share them."),
            [new ActiveTransferView("Robot/Gearbox.SLDASM", "Gearbox.SLDASM", Directions.Upload, 12, 14)]);
        using var a = JsonDocument.Parse(BridgeMessages.ActivityMessage(activity));
        Assert.Equal("activity", a.RootElement.GetProperty("type").GetString());
        var act = a.RootElement.GetProperty("activity");
        foreach (var name in new[] { "line", "upload", "download", "move", "waiting", "active" })
            Assert.True(act.TryGetProperty(name, out _), name);
        Assert.Equal(JsonValueKind.Null, act.GetProperty("download").ValueKind);
        foreach (var name in new[] { "filesDone", "filesTotal", "bytesDone", "bytesTotal", "bytesPerSecond", "secondsLeft", "line" })
            Assert.True(act.GetProperty("upload").TryGetProperty(name, out _), name);
        Assert.Equal(JsonValueKind.Null, act.GetProperty("move").GetProperty("secondsLeft").ValueKind);
        Assert.Equal(2, act.GetProperty("waiting").GetProperty("count").GetInt32());
        var moving = act.GetProperty("active")[0];
        Assert.Equal("upload", moving.GetProperty("direction").GetString());
        foreach (var name in new[] { "path", "name", "bytesDone", "bytesTotal" })
            Assert.True(moving.TryGetProperty(name, out _), name);
        using var r = JsonDocument.Parse(BridgeMessages.ActionResultMessage("r3", true, "Checked in Plate.SLDPRT."));
        Assert.Equal(["type", "requestId", "ok", "message"], r.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("actionResult", r.RootElement.GetProperty("type").GetString());
        Assert.Equal("r3", r.RootElement.GetProperty("requestId").GetString());
        Assert.True(r.RootElement.GetProperty("ok").GetBoolean());
    }

    private sealed class MemoryState(byte[]? initial = null) : IEngineStateStore
    {
        private byte[]? bytes = initial;
        public byte[]? Load() => bytes;
        public void Save(byte[] state) => bytes = state;
    }
}
