namespace Armory.Agent;

// What each of Armory's own Windows controls outside the page does, in the sentence a student
// reads when they hover over it (0.3.3, N1: every button says what it does): the tray menu's
// items, and the button shown when WebView2 is missing. The page has its own (wwwroot/app.js,
// TIPS). Kept apart from the Windows Forms types, so the words are checked anywhere
// (HostTipsTests).
internal static class HostTips
{
    // The tray menu's items, by the words each shows. Pause and Resume, and Connect this
    // computer, Sign out and Switch student, take turns in one item each.
    internal const string OpenArmory = "Open Armory", OpenFolder = "Open Armory folder", Pause = "Pause", Resume = "Resume",
        Connect = "Connect this computer", SignOut = "Sign out", SwitchStudent = "Switch student", Quit = "Quit";
    internal static readonly IReadOnlyList<string> TrayItems = [OpenArmory, OpenFolder, Pause, Resume, Connect, SignOut, SwitchStudent, Quit];

    // The tip for a tray item showing these words; vaultFolder is the Armory folder here.
    // The "Using Armory: Jordan Reyes" line is words, not a key, and has none.
    internal static string Tray(string item, string vaultFolder) => item switch
    {
        OpenArmory => "Open the Armory window.",
        OpenFolder => "Open " + vaultFolder + " in File Explorer.",
        Pause => "Stop uploading and downloading for now. Your work stays safe on this computer.",
        Resume => "Start uploading and downloading again.",
        Connect => "Sign in to Armory on this computer with your school Google account.",
        SignOut => "Sign out of Armory on this computer. Your files stay in the Armory folder.",
        SwitchStudent => "Show the list of students, so the next one can pick their name.",
        Quit => "Close Armory. Files stop updating until it starts again.",
        _ => string.Empty,
    };

    // The one button of the window shown when WebView2 Runtime is missing.
    internal const string GetWebView2 = "Open Microsoft's download page for WebView2 Runtime in your browser. Armory's window needs it.";
}
