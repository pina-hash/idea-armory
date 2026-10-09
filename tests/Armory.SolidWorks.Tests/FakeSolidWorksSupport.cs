using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Armory.Agent.Engine;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Armory.SolidWorks.Tests;

public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute() { if (!OperatingSystem.IsWindows()) Skip = "COM between processes and a fake SolidWorks; Windows only."; }
}

// tests/Armory.FakeSolidWorks as a child process: commands on its stdin, every line it writes
// kept, and waits on them. ARMORY_FAKE_SOLIDWORKS may name a published copy (an .exe) to run
// instead of "dotnet Armory.FakeSolidWorks.dll".
internal sealed class FakeSolidWorks : IDisposable
{
    // The ones running now, so a test that times out can say what each one wrote.
    private static readonly ConcurrentDictionary<FakeSolidWorks, bool> running = new();
    private readonly Process process;
    private readonly List<string> lines = [];
    private readonly object gate = new();

    private FakeSolidWorks(Process process)
    {
        this.process = process;
        process.OutputDataReceived += (_, e) => { if (e.Data is { } line) lock (gate) lines.Add(line); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is { } line) lock (gate) lines.Add("stderr " + line); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        running[this] = true;
    }

    internal static string WhatEachWrote()
        => string.Concat(running.Keys.Select(f => $"\nThe fake SolidWorks {f.Pid} wrote:\n" + string.Join("\n", f.Lines.TakeLast(60))));

    internal int Pid => process.Id;
    internal IReadOnlyList<string> Lines { get { lock (gate) return [.. lines]; } }
    internal bool Exited => process.HasExited;

    internal static async Task<FakeSolidWorks> StartAsync(string revision = "33.5.0", int startAfterMs = 1500)
    {
        var published = Environment.GetEnvironmentVariable("ARMORY_FAKE_SOLIDWORKS");
        var start = published is { Length: > 0 }
            ? new ProcessStartInfo(published)
            : new ProcessStartInfo("dotnet") { ArgumentList = { Path.Combine(AppContext.BaseDirectory, "Armory.FakeSolidWorks.dll") } };
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        foreach (var argument in new[] { "--revision", revision, "--start-after", startAfterMs.ToString(System.Globalization.CultureInfo.InvariantCulture) })
            start.ArgumentList.Add(argument);
        var fake = new FakeSolidWorks(Process.Start(start) ?? throw new InvalidOperationException("The fake SolidWorks did not start."));
        await fake.WaitForAsync(l => l.StartsWith("ready ", StringComparison.Ordinal), TimeSpan.FromSeconds(60));
        return fake;
    }

    internal void Send(string command)
    {
        process.StandardInput.WriteLine(command);
        process.StandardInput.Flush();
    }

    // The first line (from index from on) that matches, waiting up to timeout.
    internal async Task<string> WaitForAsync(Func<string, bool> match, TimeSpan timeout, int from = 0)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            var current = Lines;
            for (var i = from; i < current.Count; i++) if (match(current[i])) return current[i];
            if (deadline.Elapsed > timeout) throw new TimeoutException("The fake SolidWorks never wrote the line waited for. It wrote:\n" + string.Join("\n", current));
            if (process.HasExited && Lines.Count == current.Count) throw new InvalidOperationException("The fake SolidWorks exited. It wrote:\n" + string.Join("\n", current));
            await Task.Delay(20);
        }
    }

    internal int IndexOf(Func<string, bool> match) => Lines.ToList().FindIndex(l => match(l));

    // Its own count of references and sinks ("refs app=<n> docs=<n> sinks=<n> rejected=<n>"):
    // app is how many strong references other processes hold on the application object.
    internal async Task<(int App, int Docs, int Sinks, int Rejected)> RefsAsync()
    {
        var from = Lines.Count;
        Send("refs");
        var line = await WaitForAsync(l => l.StartsWith("refs ", StringComparison.Ordinal), TimeSpan.FromSeconds(30), from);
        var values = line[5..].Split(' ').Select(p => int.Parse(p[(p.IndexOf('=') + 1)..], System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        return (values[0], values[1], values[2], values[3]);
    }

    internal void Kill()
    {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        process.WaitForExit(30_000);
    }

    public void Dispose()
    {
        running.TryRemove(this, out _);
        try { Kill(); } catch (InvalidOperationException) { }
        process.Dispose();
    }
}

// The SolidWorks link under test, with every record it sends kept.
internal sealed class LinkUnderTest : IAsyncDisposable
{
    private readonly List<LinkRecord> records = [];
    private readonly object gate = new();
    private readonly Task reading;
    private readonly List<string> log = [];

    internal LinkUnderTest(string vaultRoot, Func<IReadOnlyList<int>> candidates, string? settingsFile = null)
    {
        Link = new SolidWorksLink(new SolidWorksLinkOptions
        {
            VaultRoot = vaultRoot,
            Candidates = candidates,
            DiscoveryInterval = TimeSpan.FromMilliseconds(250),
            ModifySettle = TimeSpan.FromMilliseconds(300),
            SettingsFile = settingsFile,
            Log = line => { lock (gate) log.Add(line); },
        });
        reading = Task.Run(async () =>
        {
            await foreach (var record in Link.Records.ReadAllAsync()) lock (gate) records.Add(record);
        });
        Link.Start();
    }

    internal SolidWorksLink Link { get; }
    internal IReadOnlyList<LinkRecord> Records { get { lock (gate) return [.. records]; } }
    internal IReadOnlyList<string> Log { get { lock (gate) return [.. log]; } }

    internal async Task<T> WaitForAsync<T>(Func<T, bool>? match = null, TimeSpan? timeout = null) where T : LinkRecord
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            if (Records.OfType<T>().FirstOrDefault(r => match?.Invoke(r) ?? true) is { } found) return found;
            if (deadline.Elapsed > (timeout ?? TimeSpan.FromSeconds(30)))
                throw new TimeoutException($"No {typeof(T).Name} came. Records: {string.Join("; ", Records)}\nLog:\n{string.Join("\n", Log)}{FakeSolidWorks.WhatEachWrote()}");
            await Task.Delay(20);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Link.DisposeAsync();
        await reading.WaitAsync(TimeSpan.FromSeconds(30));
    }
}

// A throwaway vault folder for one test.
internal sealed class TempVault : IDisposable
{
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "Armory-SW-" + Guid.NewGuid().ToString("N")[..8]);
    internal TempVault() => Directory.CreateDirectory(Path.Combine(Root, "Robot 2027"));
    internal string File(string relative, string? content = null)
    {
        var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        if (content is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(path, content);
        }
        return path;
    }
    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
