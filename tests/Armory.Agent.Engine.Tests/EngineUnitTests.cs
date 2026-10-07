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
        state.Files["Robot/Plate.SLDPRT"] = new FileState { Path = "Robot/Plate.SLDPRT", BaseId = "v1", BaseHash = "h", Inflight = new Inflight("commit", Guid.NewGuid(), "e1", Hash: "h") };
        state.Completed.Add("e0");
        store.Save(state.Serialize());
        var loaded = EngineState.Load(store);
        Assert.True(loaded.Files.ContainsKey("robot/plate.sldprt"));
        Assert.Equal("commit", loaded.Files["ROBOT/PLATE.SLDPRT"].Inflight!.Kind);
        Assert.Contains("e0", loaded.Completed);
        Assert.Equal(state.DeviceId, loaded.DeviceId);
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
        var view = new AgentView(Connections.SignedIn, new ConnectView("idle", null), new AccountView("a@b.c", "Lab PC"),
            new SyncView(SyncStates.Synced, "Everything is saved to Armory.", null, 0), @"C:\IDEA\Armory", [], [],
            [new ProjectView("p", "Robot", [new FolderView("", "Robot", [new FileRowView("f", "Plate.SLDPRT", "Robot/Plate.SLDPRT", FileStatuses.Synced, null, true, null, null)])])],
            new SettingsView(@"C:\IDEA\Armory", true, "system"), "idea");
        using var json = JsonDocument.Parse(BridgeMessages.ViewMessage(view));
        Assert.Equal("view", json.RootElement.GetProperty("type").GetString());
        var v = json.RootElement.GetProperty("view");
        foreach (var name in new[] { "connection", "connect", "account", "sync", "vaultRoot", "myFiles", "needsMe", "projects", "settings", "effectiveTheme" })
            Assert.True(v.TryGetProperty(name, out _), name);
        var row = v.GetProperty("projects")[0].GetProperty("folders")[0].GetProperty("files")[0];
        Assert.True(row.GetProperty("releaseNotChecked").GetBoolean());
        Assert.Equal("synced", row.GetProperty("status").GetString());
        Assert.True(v.GetProperty("settings").GetProperty("startAtSignIn").GetBoolean());

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

        // The check-out question carries its own key (one per open), which Not now sends
        // back in dismissNotice; the page reads these names.
        var prompt = new PromptView("prompt:Robot/Plate.SLDPRT:2026-10-01T22:28:00.000Z", "f", "Robot/Plate.SLDPRT", "Plate.SLDPRT",
            new CheckoutView(CheckoutStates.Available, "Available", null, null, null, null), true);
        using var q = JsonDocument.Parse(JsonSerializer.Serialize(prompt, BridgeMessages.Json));
        Assert.Equal(["key", "fileId", "path", "name", "checkout", "canCheckOut"], q.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("available", q.RootElement.GetProperty("checkout").GetProperty("state").GetString());
    }

    private sealed class MemoryState : IEngineStateStore
    {
        private byte[]? bytes;
        public byte[]? Load() => bytes;
        public void Save(byte[] state) => bytes = state;
    }
}
