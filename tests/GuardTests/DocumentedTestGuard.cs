using System.Reflection;

namespace Armory.Guards;

public sealed class DocumentedTestGuard
{
    [Fact]
    public void Every_documented_guard_test_still_exists()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var assemblyName = assembly.GetName().Name!;
        var entries = File.ReadLines(Path.Combine(AppContext.BaseDirectory, "GUARDS.txt"))
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Select(line => line.Split('|'))
            .Where(parts => parts[0] == assemblyName)
            .ToArray();

        foreach (var entry in entries)
        {
            var type = assembly.GetType(entry[1]);
            Assert.True(type is not null, $"Guarded test type no longer exists: {entry[1]}");
            var method = type.GetMethod(entry[2], BindingFlags.Instance | BindingFlags.Public);
            Assert.True(method is not null, $"Guarded test no longer exists: {entry[1]}.{entry[2]}");
            Assert.Contains(method.GetCustomAttributes(), attribute => attribute is FactAttribute);
        }
    }
}
