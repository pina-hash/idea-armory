using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace Armory.Agent;

// One button of a notification: its words and the link it opens (ProtocolLink), or no link for
// "Not now", which is Windows' own dismiss.
internal sealed record ToastButton(string Label, string? Link);

// One of Armory's Windows notifications. Tag and Group name it: a new one with the same pair
// replaces it, and Armory withdraws it by them. Launch is the link a click on its body opens.
internal sealed record ToastContent(string Tag, string Group, string Title, string? Text, string Launch, IReadOnlyList<ToastButton> Buttons);

// The notification's XML (Windows' ToastGeneric template), built with System.Xml.Linq so every
// name and sentence is escaped. Every link opens by protocol (idea-armory:), and every Armory
// notification is silent: a question, never an alarm that pulls a student out of SolidWorks.
internal static class ToastXml
{
    // The check-out questions about opened files, the questions before a save down, and the
    // answers to what Armory did.
    internal const string OpenGroup = "open", SaveDownGroup = "savedown", AnswerGroup = "answer";
    // A question nobody answered leaves the notification center after an hour.
    internal static readonly TimeSpan Expires = TimeSpan.FromHours(1);

    internal static string Build(ToastContent toast)
    {
        var binding = new XElement("binding", new XAttribute("template", "ToastGeneric"), new XElement("text", toast.Title));
        if (!string.IsNullOrWhiteSpace(toast.Text)) binding.Add(new XElement("text", toast.Text));
        var root = new XElement("toast", new XAttribute("launch", toast.Launch), new XAttribute("activationType", "protocol"), new XElement("visual", binding));
        if (toast.Buttons.Count > 0)
            root.Add(new XElement("actions", toast.Buttons.Select(b => b.Link is null
                ? new XElement("action", new XAttribute("content", b.Label), new XAttribute("activationType", "system"), new XAttribute("arguments", "dismiss"))
                : new XElement("action", new XAttribute("content", b.Label), new XAttribute("activationType", "protocol"), new XAttribute("arguments", b.Link)))));
        root.Add(new XElement("audio", new XAttribute("silent", "true")));
        return root.ToString(SaveOptions.DisableFormatting);
    }

    // The tag of a question about these files: the first 16 hex digits of SHA-256 over their
    // vault paths, in any case and order, so a second question about the same files replaces the
    // first, and none ever names a file to Windows' notification store.
    internal static string TagFor(IEnumerable<string> paths)
    {
        var joined = string.Join("\n", paths.Select(p => p.Replace('\\', '/').ToLowerInvariant()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(joined)))[..16];
    }
}

// The notifications' words, the same voice as the window's check-out card (app.js promptHtml)
// and the tray balloon (CheckOutPrompts.Words).
internal static class ToastWords
{
    internal const string CheckOutAndReopen = "Check out and reopen", NotNow = "Not now", OpenArmory = "Open Armory";
    internal const string KeepOnThisComputer = "Keep on this computer only";

    // The question before a save down's first button: the team's rule.
    internal static string SaveIn(int year) => $"Save in {year}";

    // One file SolidWorks opened that this computer has not checked out.
    internal static (string Title, string Text) Ask(OpenPromptInfo file) => file.CanCheckOut
        ? ($"Check out {file.Name} to edit it?", "SolidWorks opened it read-only. Check it out, then close it in SolidWorks and open it again to save changes.")
        : ($"{file.Name} is checked out by {file.CheckedOutBy ?? "someone else"}", "You can look at it, but you can't save changes until it's checked in.");

    // Several at once (an assembly and its parts, or a pick of files opened together).
    internal static (string Title, string Text) Group(int count) =>
        ($"SolidWorks opened {count:N0} files you haven't checked out", "Open Armory to check out the ones you'll change.");

    // An action's one sentence: its first sentence as the title, the rest below it.
    internal static (string Title, string? Text) Answer(string message)
    {
        var text = message.Trim();
        for (var at = text.IndexOf(". ", StringComparison.Ordinal); at > 0; at = text.IndexOf(". ", at + 2, StringComparison.Ordinal))
        {
            var word = text[..at];
            word = word[(word.LastIndexOf(' ') + 1)..];
            // "Mr. Pina" is no end of a sentence.
            if (word is "Mr" or "Mrs" or "Ms" or "Dr" or "Mx") continue;
            return (text[..(at + 1)], text[(at + 2)..].Trim());
        }
        return (text, null);
    }
}
