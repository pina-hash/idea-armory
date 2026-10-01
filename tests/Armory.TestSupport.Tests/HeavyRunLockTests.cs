using System.Text.RegularExpressions;

namespace Armory.TestSupport.Tests;

public sealed class HeavyRunLockTests
{
    // Not the real key: the end-to-end suite and the server simulation may hold that one while
    // this runs in the same dotnet test.
    private const long TestKey = HeavyRunLock.Key ^ 0x7E57;
    private static readonly TimeSpan Granted = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(500);

    [PostgresFact]
    public async Task SharedHoldsCoexistAndAnExclusiveHoldWaitsForAllOfThemAndBlocksNewOnes()
    {
        var first = await HeavyRunLock.SharedAsync(TestKey).WaitAsync(Granted);
        var second = await HeavyRunLock.SharedAsync(TestKey).WaitAsync(Granted);
        Assert.True(first.Shared && second.Shared);

        var exclusive = HeavyRunLock.ExclusiveAsync(TestKey);
        await Task.Delay(Settle);
        Assert.False(exclusive.IsCompleted, "an exclusive hold was granted while two shared holds were held");
        await first.DisposeAsync();
        await Task.Delay(Settle);
        Assert.False(exclusive.IsCompleted, "an exclusive hold was granted while a shared hold was still held");
        await second.DisposeAsync();
        var held = await exclusive.WaitAsync(Granted);
        Assert.False(held.Shared);

        var third = HeavyRunLock.SharedAsync(TestKey);
        await Task.Delay(Settle);
        Assert.False(third.IsCompleted, "a shared hold was granted during an exclusive hold");
        await held.DisposeAsync();
        await (await third.WaitAsync(Granted)).DisposeAsync();
    }

    // Armory.Server.Tests cannot reference this project, so it keeps its own copy of the key.
    [Fact]
    public void TheServerSimulationUsesTheSameKey()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Armory.sln"))) root = root.Parent;
        Assert.True(root is not null, "Could not find the repository root (Armory.sln) above " + AppContext.BaseDirectory + ".");
        var server = File.ReadAllText(Path.Combine(root.FullName, "tests", "Armory.Server.Tests", "HeavyRunLock.cs"));
        var match = Regex.Match(server, @"const long Key = (0x[0-9A-Fa-f]+);");
        Assert.True(match.Success, "Armory.Server.Tests/HeavyRunLock.cs has no Key constant.");
        Assert.Equal(HeavyRunLock.Key, Convert.ToInt64(match.Groups[1].Value, 16));
        var simulation = File.ReadAllText(Path.Combine(root.FullName, "tests", "Armory.Server.Tests", "ServerSimulationTests.cs"));
        // The lock is taken before the clock starts, so the budget never counts the wait.
        Assert.Matches(@"HeavyRunLock\.Exclusive\(\);(?:\s*output\.WriteLine\([^;]*\);)?\s*var watch = Stopwatch\.StartNew\(\);", simulation);
    }
}
