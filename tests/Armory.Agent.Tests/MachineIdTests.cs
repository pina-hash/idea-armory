using Armory.Client;
using Armory.Telemetry;

namespace Armory.Agent.Tests;

// Two lab computers imaged alike both report the name IDEA-06; an incident carries a machine id
// that tells them apart (docs/agent/TELEMETRY.md, "The incident file"): a short hash of
// Windows' own install id, never the id itself, and null where there is none.
public sealed class MachineIdTests
{
    [Fact]
    public void A_machine_id_is_a_short_hash_of_the_install_id_never_the_id_itself()
    {
        const string Guid1 = "8C26F8AC-1B2D-4E3F-9A01-23456789ABCD", Guid2 = "a0307df4-5e6f-4a1b-8c2d-3e4f5a6b7c8d";
        var one = MachineId.FromGuid(Guid1)!;
        Assert.Matches("^[0-9a-f]{16}$", one);
        // The same computer is the same id however the value is written; another is another.
        Assert.Equal(one, MachineId.FromGuid(" " + Guid1.ToLowerInvariant() + " "));
        Assert.NotEqual(one, MachineId.FromGuid(Guid2));
        Assert.DoesNotContain(one, Guid1.ToLowerInvariant().Replace("-", "", StringComparison.Ordinal));
        Assert.Null(MachineId.FromGuid(null));
        Assert.Null(MachineId.FromGuid("  "));
        // Only Windows has the install id.
        if (!OperatingSystem.IsWindows()) Assert.Null(MachineId.Current);
        else if (MachineId.Current is { } here) Assert.Matches("^[0-9a-f]{16}$", here);
    }

    [Fact]
    public async Task The_incident_document_carries_the_machine_id()
    {
        using var temp = new TempFolder();
        var paths = new AgentPaths(temp.Root, true);
        Directory.CreateDirectory(paths.LogFolder);
        var telemetry = new AgentTelemetry(paths, new AgentLog(paths.LogFile, paths.CrashFile));
        await using (telemetry)
        {
            var http = new HttpClient(new HttpClientHandler());
            var sessions = new SessionManager(http, new InMemorySecretStore());
            telemetry.Attach(() => sessions.Current, _ => Task.FromResult<System.Text.Json.Nodes.JsonNode?>(null), () => null,
                new ArmoryApi(new PostgrestClient(http, sessions, telemetry.Recorder)), () => false);
            telemetry.CrashNow("unhandled exception", new InvalidOperationException("boom"));
            var file = Assert.Single(Directory.EnumerateFiles(paths.IncidentsFolder, "*.json.gz"));
            var incident = IncidentDocument.Read(File.ReadAllBytes(file));
            Assert.True(incident.ContainsKey("machineId"));
            Assert.Equal(MachineId.Current, incident["machineId"]?.GetValue<string>());
        }
        // The document carries what its header says.
        var built = IncidentDocument.Build(GlitchRules.UserReport("bug", "it froze"), new IncidentHeader("0.3.3", "Windows", "IDEA-06", null, "0123456789abcdef"),
            DateTimeOffset.UnixEpoch, null, [], 0, 16, null, []);
        Assert.Equal(("IDEA-06", "0123456789abcdef"), (built["deviceName"]!.GetValue<string>(), built["machineId"]!.GetValue<string>()));
    }
}
