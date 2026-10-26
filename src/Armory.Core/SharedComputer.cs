namespace Armory.Core;

// Several people taking turns on one computer with one shared Windows login (0.3.3, part F; 0.5.0,
// ledger 0437; docs/agent/PROFILES.md). Each person keeps a profile of their own (their own
// sign-in), the window opens on the faces of the people who use the computer, and everyone works
// in the computer's one Armory folder: whoever is picked works there, and what someone else left
// waiting stays theirs (the engine holds it for them). These are the rules the host follows; none
// of them touches a disk or a network.

// Why the window is asking whether to show the picker (PROFILES.md, "When the picker shows").
public enum PickerTrigger
{
    // Armory starts in the background (--background): it runs as the student in use, and the
    // picker shows when the window first opens.
    Start,
    // The window was hidden (closed with X, or never shown) and is shown now: the taskbar, the
    // Start menu, the desktop, a second launch, the tray, a balloon.
    ShownFromHidden,
    // Brought to the front or restored from minimized.
    Activated,
    // Windows was locked: the picker is up when someone unlocks it.
    WindowsLocked,
    // Switch student (Home, the tray).
    SwitchStudent,
    Minimized,
    // The student in use's sign-in ended (a refused refresh).
    SignInEnded,
}

public static class SharedComputer
{
    public const int PinDigits = 4, FreePinTries = 5, PictureHues = 8;
    public static readonly TimeSpan FirstPinWait = TimeSpan.FromSeconds(30), LongestPinWait = TimeSpan.FromMinutes(15);
    // The longest folder path Armory takes (AgentSettings.TryNormalizeVaultRoot), so CAD file paths still fit.
    public const int MaximumFolderPath = 120;
    public const string TooEasyPin = "Pick a PIN that's harder to guess than 1234.";

    // Exactly four ASCII digits: no spaces, no other digits (a full-width 1 is not 1).
    public static bool IsPin(string? text)
    {
        if (text is null || text.Length != PinDigits) return false;
        foreach (var c in text) if (c is < '0' or > '9') return false;
        return true;
    }

    // A PIN anyone would try first: four of one digit, or a straight run up or down (1234, 9876).
    public static string? TooEasy(string pin)
    {
        if (!IsPin(pin)) return "Type 4 digits.";
        bool same = true, up = true, down = true;
        for (var i = 1; i < pin.Length; i++)
        {
            same &= pin[i] == pin[0];
            up &= pin[i] == pin[i - 1] + 1;
            down &= pin[i] == pin[i - 1] - 1;
        }
        return same || up || down ? TooEasyPin : null;
    }

    // After FreePinTries wrong PINs in a row: 30 seconds, then twice as long after each further
    // wrong PIN, never more than 15 minutes, and never a lockout for good (a classmate typing
    // wrong PINs must not shut a student out; the browser sign-in always gets them in).
    public static TimeSpan? PinWait(int wrongTries)
    {
        if (wrongTries < FreePinTries) return null;
        var doublings = Math.Min(wrongTries - FreePinTries, 10);
        var wait = TimeSpan.FromSeconds(FirstPinWait.TotalSeconds * (1 << doublings));
        return wait < LongestPinWait ? wait : LongestPinWait;
    }

    // Tries left before the first wait (0 once waits have begun).
    public static int PinTriesLeft(int wrongTries) => Math.Max(0, FreePinTries - wrongTries);

    // Whether the window opens on the picker. lastPicked: the local day someone last picked a
    // person here (null: never). people: how many people use this computer. With fewer than two
    // there is nobody to switch to, so the window never opens on the picker by itself (0.5.0: "one
    // click or none"); Switch (which is where someone new adds themselves) and a sign-in that ended
    // still show it. A person continuing after their own X goes through the picker too: nothing
    // tells them apart from the next person.
    public static bool ShowPicker(PickerTrigger trigger, DateOnly? lastPicked, DateOnly today, int people) => trigger switch
    {
        PickerTrigger.SignInEnded or PickerTrigger.SwitchStudent => true,
        _ when people < 2 => false,
        PickerTrigger.ShownFromHidden or PickerTrigger.WindowsLocked => true,
        // The first open of the day, a window left open overnight included.
        PickerTrigger.Activated => lastPicked is not { } day || day < today,
        _ => false,
    };

    // A computer becomes shared by itself (0.5.0, no setup step): a sign-in arrives for an address
    // that is neither the one signed in here now nor, when nobody is, the one the Armory folder was
    // last used by. The same person signing in again (a renewed sign-in, a new device id) never
    // makes a computer shared.
    public static bool BecomesShared(string? signedIn, string? folderOwner, string arriving)
    {
        var here = signedIn ?? folderOwner;
        return here is not null && !SameEmail(here, arriving);
    }

    // Whether a computer set up by Armory 0.3.3 to 0.4.x keeps asking for PINs after the update.
    // There PINs were on by default the moment shared mode was turned on, and each student chose
    // their own; 0.5.0 asks for none by default, and a mentor may turn them on for a computer. A
    // mentor's deliberate choice is the one thing the old index can show: only a mentor in use
    // could change the setting, and the index recorded who (PinsChangedBy). So PINs stay on only
    // where a mentor's last change turned them on; a student-chosen PIN under the old default never
    // starts blocking switches. The PIN records themselves are kept either way.
    public static bool PinsKeptAfterUpdate(bool pinsRequired, string? pinsChangedBy) => pinsRequired && !string.IsNullOrWhiteSpace(pinsChangedBy);

    // A folder of one's own from Armory 0.3.3 to 0.4.x (C:\IDEA\Armory-alex beside C:\IDEA\Armory,
    // then -2, -3): the shared folder's own name, a dash, letters and digits, and at most one more
    // dash and a number. 0.5.0 folds every such folder back into the shared one (FolderFold).
    public static bool IsOwnFolder(string sharedRoot, string candidate)
    {
        var shared = sharedRoot.Replace('/', '\\').TrimEnd('\\');
        var folder = candidate.Replace('/', '\\').TrimEnd('\\');
        if (folder.Length <= shared.Length + 1 || !folder.StartsWith(shared + "-", StringComparison.OrdinalIgnoreCase)) return false;
        var rest = folder[(shared.Length + 1)..];
        if (rest.Contains('\\')) return false;
        var parts = rest.Split('-');
        if (parts.Length > 2 || parts[0].Length == 0 || !parts[0].All(char.IsAsciiLetterOrDigit)) return false;
        return parts.Length == 1 || (parts[1].Length > 0 && parts[1].All(char.IsAsciiDigit));
    }


    // The name a tile shows (the name the website shows when Armory knows it, else the address's
    // words: "alex.kim" is "Alex Kim"), its initials (first and last word, a title like "Mr."
    // skipped) and one of PictureHues circle colors, the same every time for an address.
    public static (string Name, string Initials, int Hue) NameFor(string email, string? shownName)
    {
        var name = string.IsNullOrWhiteSpace(shownName) ? NameFromEmail(email) : shownName.Trim();
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (words.Count > 1 && words[0].TrimEnd('.') is "Mr" or "Mrs" or "Ms" or "Dr" or "Mx") words.RemoveAt(0);
        var initials = words.Count == 0 ? "?" : (words[0][..1] + (words.Count > 1 ? words[^1][..1] : "")).ToUpperInvariant();
        // FNV-1a over the address: stable across runs and computers (string.GetHashCode is not).
        var hash = 2166136261u;
        foreach (var c in email.Trim().ToLowerInvariant()) hash = (hash ^ c) * 16777619u;
        return (name, initials, (int)(hash % PictureHues));
    }

    // "Alex Kim" for alex.kim@boscotech.edu (the engine's DisplayName does the same).
    public static string NameFromEmail(string email)
    {
        var at = email.IndexOf('@', StringComparison.Ordinal);
        var local = at > 0 ? email[..at] : email;
        var words = local.Split(['.', '_', '-'], StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]) + w[1..]);
        var name = string.Join(' ', words);
        return name.Length == 0 ? email : name;
    }

    // Who may remove a student from this computer: themselves; a mentor in use; anyone while PINs
    // are off (with PINs off anyone can enter anyone anyway).
    public static bool CanRemove(bool self, bool mentorInUse, bool pinsRequired) => self || mentorInUse || !pinsRequired;

    // Turning "shared by several students" off keeps only the student in use: allowed with at
    // most one student, a mentor in use, or PINs off.
    public static bool CanTurnOff(int profiles, bool mentorInUse, bool pinsRequired) => profiles <= 1 || mentorInUse || !pinsRequired;

    // Only a mentor's own profile in use changes whether PINs are asked on this computer.
    public static bool CanChangePins(bool mentorInUse) => mentorInUse;

    // Folders compare as Windows does: case-insensitive, either slash, no trailing slash.
    public static bool SameFolder(string a, string b)
        => string.Equals(a.Replace('/', '\\').TrimEnd('\\'), b.Replace('/', '\\').TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    public static bool SameEmail(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}
