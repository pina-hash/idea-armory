using System.Security.Cryptography;
using System.Text;
using Armory.Agent.Engine.View;

namespace Armory.Agent;

internal sealed partial class AgentHost
{
    internal Task<ActionResult> SaveBlockedActionAsync(string key) => OnEngineAsync("help with a blocked save", async engine =>
    {
        var email = Sessions.Current?.Email;
        var action = await engine.SaveBlockedActionAsync(key);
        if (action is null || PickerShowing || email is null || !string.Equals(email, Sessions.Current?.Email, StringComparison.OrdinalIgnoreCase))
            return new(false, "That save question is no longer current. Try saving in SolidWorks again.");
        if (action.Command == BridgeMessages.CheckOut) return await engine.CheckOutAndReopenAsync(action.Paths);
        try
        {
            var folder = StudentCopyFolder.Open(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), email);
            Shell.OpenFolder(folder);
            return new(true, "In SolidWorks, use Save As in the folder that opened. Your copy stays on this computer; add it to your project in Armory when you are ready.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            log.Error("could not open the student's copy folder", error);
            return new(false, "Armory couldn't open your copy folder. In SolidWorks, use Save As and choose a folder for your own work.");
        }
    });
}

// A separate destination for each school sign-in on the shared Windows account. The server
// has no student-folder mapping, so never infer ownership from a class folder's filename.
// This action only makes/opens empty directories. SolidWorks performs Save As, never Armory.
internal static class StudentCopyFolder
{
    internal static string Name(string email) => "Student-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(email.Trim().ToLowerInvariant())))[..32];

    internal static string Open(string documents, string email)
    {
        if (string.IsNullOrWhiteSpace(documents) || string.IsNullOrWhiteSpace(email)) throw new IOException("No personal folder is available.");
        var root = Path.GetFullPath(documents);
        // Refuse links on every existing ancestor, including the Documents location itself.
        for (var parent = new DirectoryInfo(root); parent is not null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("The copy folder goes through a link.");
        foreach (var name in new[] { "Armory copies", Name(email) })
        {
            root = Path.Combine(root, name);
            if (Directory.Exists(root) && (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new IOException("The copy folder goes through a link.");
            Directory.CreateDirectory(root);
        }
        return root;
    }
}
