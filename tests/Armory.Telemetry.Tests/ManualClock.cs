namespace Armory.Telemetry.Tests;

// A clock that moves only when a test moves it: wall time and timestamps together.
internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private long ticks;
    public ManualClock() : this(new DateTimeOffset(2026, 10, 7, 18, 0, 0, TimeSpan.Zero)) { }
    public override DateTimeOffset GetUtcNow() => start.AddTicks(Interlocked.Read(ref ticks));
    public override long GetTimestamp() => Interlocked.Read(ref ticks);
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public void Advance(TimeSpan by) => Interlocked.Add(ref ticks, by.Ticks);
}

// A folder of its own under the system's temporary folder, removed at the end.
internal sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "armory-telemetry-" + Guid.NewGuid().ToString("N"));
    public TempFolder() => Directory.CreateDirectory(Path);
    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
    }
}
