using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit.Abstractions;

namespace Armory.Telemetry.Tests;

// tools/read-incident/read_incident.py reads what the app writes: an incident file, the same
// JSON uncompressed, and the website's export zip (docs/agent/TELEMETRY.md, "Reading one").
// Needs python3 on the PATH (Linux CI has it); skipped where there is none.
public sealed class ReadIncidentToolTests(ITestOutputHelper output)
{
    private static string? Python()
    {
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            foreach (var name in new[] { "python3", "python3.exe" })
                if (folder.Length > 0 && File.Exists(Path.Combine(folder, name))) return Path.Combine(folder, name);
        return null;
    }

    private static string Tool()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
            if (File.Exists(Path.Combine(folder.FullName, "Armory.sln"))) return Path.Combine(folder.FullName, "tools", "read-incident", "read_incident.py");
        throw new InvalidOperationException("The repository root was not found.");
    }

    private static (int Code, string Out) Run(string python, params string[] arguments)
    {
        var start = new ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = Encoding.UTF8 };
        start.ArgumentList.Add("-I");
        start.ArgumentList.Add(Tool());
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var text = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, text);
    }

    [Fact]
    public void The_reader_prints_a_timeline_from_a_file_its_json_and_the_websites_zip()
    {
        if (Python() is not { } python) return;
        using var temp = new TempFolder();
        var clock = new ManualClock();
        var recorder = new FlightRecorder(clock: clock);
        var reporter = new IncidentReporter(recorder, new IncidentStore(temp.Path), new IncidentSources
        {
            Header = () => new("0.3.0", "Windows 11 Education", "LAB-PC-07", "alex.kim@students.test"),
            QuickSnapshot = () => new JsonObject { ["filesByStatus"] = new JsonObject { ["synced"] = 412 } },
            LogTail = _ => ["2026-10-07T18:00:00.000Z started 0.3.0", "2026-10-07T18:01:00.000Z pass: moving 12 of 412 files (loop)"],
        }, clock) { Schedule = work => work().GetAwaiter().GetResult() };
        recorder.PassStart("loop");
        recorder.Rpc("armory_project_files", 340, 200, null);
        clock.Advance(TimeSpan.FromSeconds(2));
        recorder.Transfer("download", 3_500_000, 41_000, true, 200, null);
        recorder.Rpc("armory_commit_version_with_release", 90, 500, "XX000");
        recorder.FileFailed("Robot 2027/Drivetrain/Plate.SLDPRT", new IOException("The process cannot access the file"));
        clock.Advance(TimeSpan.FromSeconds(70));
        recorder.PassEnd("loop", false, 72_000, 12, 0, 0, 1);
        var file = Assert.Single(reporter.Store.All());

        var (code, text) = Run(python, file);
        output.WriteLine(text);
        Assert.Equal(0, code);
        Assert.Contains("Incident   slowPass", text);
        Assert.Contains("Summary    A loop pass took 72.0 s", text);
        Assert.Contains("Trigger    pass end (loop) ok after 72.0 s", text);
        Assert.Contains("download 3.3 MB in 41.0 s 200 ok", text);
        Assert.Contains("-70.000s  FILE FAILED Robot 2027/Drivetrain/Plate.SLDPRT", text);
        Assert.Contains("Slowest", text);
        Assert.Contains("2 server calls, 1 failed (XX000)", text);
        Assert.Contains("\"synced\": 412", text);
        Assert.Contains("pass: moving 12 of 412 files", text);

        // The same JSON uncompressed, and two of them in the website's zip (the report plus the row's fields).
        var incident = IncidentDocument.Read(File.ReadAllBytes(file));
        var plain = Path.Combine(temp.Path, "plain.json");
        File.WriteAllText(plain, incident.ToJsonString());
        Assert.Contains("Incident   slowPass", Run(python, plain).Out);
        var zip = Path.Combine(temp.Path, "armory-incidents.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            foreach (var (name, kind) in new[] { ("a.json", "slowPass"), ("b.json", "crash") })
            {
                var copy = (JsonObject)incident.DeepClone();
                copy["kind"] = kind;
                var row = new JsonObject { ["id"] = Guid.NewGuid().ToString(), ["created_at"] = "2026-10-07T18:01:15Z", ["status"] = "new", ["report"] = copy };
                using var stream = archive.CreateEntry(name).Open();
                stream.Write(JsonSerializer.SerializeToUtf8Bytes(row));
            }
        var (zipCode, zipText) = Run(python, zip, "--kind", "crash");
        Assert.Equal(0, zipCode);
        Assert.Contains("armory-incidents.zip:b.json", zipText);
        Assert.Contains("status=new", zipText);
        Assert.DoesNotContain("armory-incidents.zip:a.json", zipText);
        Assert.Equal(1, Run(python, temp.Path, "--kind", "readOnlyBroken").Code);
    }
}
