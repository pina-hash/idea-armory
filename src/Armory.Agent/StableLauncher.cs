namespace Armory.Agent;

internal static class StableLauncher
{
    internal const string FileName = "IDEA Armory.exe";
    // Velopack 1.2.161 extracts the packTitle execution stub to the root on install and update.
    // It invokes Update.exe start and forwards all arguments to the current version.
    internal static string For(string exe, Func<string, bool> exists)
    {
        if (!Updates.IsVelopackCopy(exe, exists)) return exe;
        var normalized = exe.Replace('/', '\\');
        var current = normalized[..normalized.LastIndexOf('\\')];
        var stub = current[..current.LastIndexOf('\\')] + @"\" + FileName;
        return exists(stub) ? stub : exe;
    }
}
