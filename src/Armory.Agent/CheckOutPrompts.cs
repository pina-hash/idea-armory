namespace Armory.Agent;

// Decision D13, the quiet check-out prompt outside the window. While the window shows, its
// prompt card asks (it never steals focus from SolidWorks). While it is hidden, each opened file
// asks once (OpenAsks gathers them into a Windows notification, C5); the tray balloon with these
// words stands in only when Windows' notifications fail. A file asks again only after it was
// closed. No WinForms here, so the rule is tested on every host.
internal sealed class CheckOutPrompts
{
    // NotifyIcon refuses a longer balloon title.
    internal const int TitleLimit = 63;
    private readonly HashSet<string> offered = new(StringComparer.OrdinalIgnoreCase);

    // True when a balloon should be shown for this opened file now.
    internal bool ShouldOffer(string path, bool windowShowing)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var first = offered.Add(path);
        return first && !windowShowing;
    }

    // Files that are no longer open may ask again the next time they are opened.
    internal void KeepOnly(IEnumerable<string> stillOpen) => offered.IntersectWith(stillOpen);

    // The balloon's words (docs/agent/DESIGN-REVIEW.md: plain words, the same as the card).
    // checkedOutBy is "Maria Lopez on LAB-PC-07" when someone else has the file.
    internal static (string Title, string Text) Words(string name, string? checkedOutBy)
    {
        var title = checkedOutBy is null ? $"Check out {name} to edit it?" : $"{name} is checked out by {checkedOutBy}";
        if (title.Length > TitleLimit) title = title[..(TitleLimit - 3)] + "...";
        var text = checkedOutBy is null
            ? "Click here to open Armory and check it out. Then you can save your changes."
            : "You can look at it, but you can't save changes until it's checked in. Click here to open Armory.";
        return (title, text);
    }
}
