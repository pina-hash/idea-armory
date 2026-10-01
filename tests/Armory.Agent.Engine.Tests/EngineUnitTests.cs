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
    }

    private sealed class MemoryState : IEngineStateStore
    {
        private byte[]? bytes;
        public byte[]? Load() => bytes;
        public void Save(byte[] state) => bytes = state;
    }
}
