namespace Armory.TestSupport;

/// <summary>
/// A settable clock for the fakes. It starts at the real current time and moves only when a
/// test calls <see cref="Advance"/> or <see cref="Set"/>. Thread safe.
/// </summary>
public sealed class TestClock : TimeProvider
{
    private long _utcTicks;

    public TestClock() : this(TimeProvider.System.GetUtcNow()) { }

    public TestClock(DateTimeOffset start) => _utcTicks = start.UtcTicks;

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);

    public void Advance(TimeSpan by)
    {
        if (by < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(by), "The clock only moves forward.");
        Interlocked.Add(ref _utcTicks, by.Ticks);
    }

    public void Set(DateTimeOffset now) => Interlocked.Exchange(ref _utcTicks, now.UtcTicks);
}
